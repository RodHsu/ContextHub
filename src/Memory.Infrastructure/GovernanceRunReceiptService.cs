using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

public sealed class GovernanceRunReceiptService(
    MemoryDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    TimeProvider timeProvider) : IGovernanceRunReceiptService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task RecordReviewAsync(
        KnowledgeReviewResult result,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var previous = await LatestAsync(result.GovernanceRunId, actor, cancellationToken);
        var snapshot = result.DurableMemoryCoverage?.SnapshotToken ?? string.Empty;
        var candidateCount = SumCandidates(result.GovernanceCoverage);
        var eventKey = Hash($"review\n{snapshot}\n{result.IsReReview}\n{result.Convergence.Status}\n{result.Convergence.GovernanceActionableCount}");
        var receipt = NewReceipt(actor, result.GovernanceRunId, eventKey, "Review", startedAt);
        receipt.InitialSnapshotToken = previous?.InitialSnapshotToken ?? snapshot;
        receipt.FinalSnapshotToken = snapshot;
        receipt.CoverageComplete = result.Convergence.CoverageComplete;
        receipt.InitialGovernanceActionable = previous?.InitialGovernanceActionable ?? result.Convergence.GovernanceActionableCount;
        receipt.FinalGovernanceActionable = result.Convergence.GovernanceActionableCount;
        receipt.CandidateCount = candidateCount;
        receipt.ExecutionActionableCount = result.Convergence.GovernanceActionableCount;
        receipt.GovernedExceptionCount = result.Convergence.GovernedExceptionCount;
        receipt.Applied = previous?.Applied ?? 0;
        receipt.Failed = previous?.Failed ?? 0;
        receipt.Deferred = result.Convergence.DeferredCount;
        receipt.RequiresUserDecision = result.Convergence.RequiresUserDecisionCount;
        receipt.HostBlocked = result.Convergence.HostBlockedCount;
        receipt.Quarantined = previous?.Quarantined ?? result.QuarantinedCount;
        receipt.DeleteEligible = result.DeleteEligibleCount;
        receipt.DeleteMatured = result.DeleteMaturedCount;
        receipt.AutoDeleted = previous?.AutoDeleted ?? result.AutoDeletedCount;
        receipt.DeleteCancelled = result.DeleteCancelledCount;
        receipt.Tombstoned = previous?.Tombstoned ?? result.TombstonedCount;
        receipt.SemanticAutoResolved = previous?.SemanticAutoResolved ?? result.SemanticAutoResolvedCount;
        receipt.BusinessWorkItemActionable = result.Convergence.BusinessWorkItemActionableCount;
        receipt.FinalConvergenceStatus = result.Convergence.Status;
        receipt.StoppedReason = "ReviewCompleted";
        receipt.AuditIdsJson = previous?.AuditIdsJson ?? "[]";
        receipt.ProjectIdsJson = JsonSerializer.Serialize(result.Projects.Select(x => x.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase), JsonOptions);
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task RecordExecutionAsync(
        GovernanceBatchExecuteRequest request,
        GovernanceBatchExecuteResult result,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var previous = await LatestAsync(request.GovernanceRunId, actor, cancellationToken);
        var eventKind = result.IsReplay ? "replay" : "execute";
        var eventKey = Hash($"{eventKind}\n{request.GovernanceRunId}\n{request.SnapshotToken}\n{request.Cursor}\n{CanonicalExecutionKey(request)}");
        var receipt = NewReceipt(actor, request.GovernanceRunId, eventKey, request.ExecutionMode.ToString(), startedAt);
        var add = result.IsReplay ? 0 : 1;
        receipt.InitialSnapshotToken = previous?.InitialSnapshotToken ?? result.SnapshotToken;
        receipt.FinalSnapshotToken = result.SnapshotToken;
        receipt.CoverageComplete = previous?.CoverageComplete ?? false;
        receipt.InitialGovernanceActionable = previous?.InitialGovernanceActionable ?? 0;
        receipt.FinalGovernanceActionable = previous?.FinalGovernanceActionable ?? 0;
        receipt.CandidateCount = previous?.CandidateCount ?? 0;
        receipt.ExecutionActionableCount = previous?.ExecutionActionableCount ?? 0;
        receipt.GovernedExceptionCount = previous?.GovernedExceptionCount ?? 0;
        receipt.Applied = (previous?.Applied ?? 0) + result.AppliedCount * add;
        receipt.Failed = (previous?.Failed ?? 0) + result.FailedCount * add;
        receipt.Deferred = Math.Max(previous?.Deferred ?? 0, result.DeferredCount);
        receipt.RequiresUserDecision = Math.Max(previous?.RequiresUserDecision ?? 0, result.RequiresUserDecisionCount);
        receipt.HostBlocked = (previous?.HostBlocked ?? 0) + (result.ErrorCode == GovernanceBatchErrorCode.HostBlockedMaturedDelete ? add : 0);
        receipt.Quarantined = (previous?.Quarantined ?? 0) + result.QuarantinedCount * add;
        receipt.DeleteEligible = result.DeleteEligibleCount;
        receipt.DeleteMatured = result.DeleteMaturedCount;
        receipt.AutoDeleted = (previous?.AutoDeleted ?? 0) + result.AutoDeletedCount * add;
        receipt.DeleteCancelled = result.DeleteCancelledCount;
        receipt.Tombstoned = (previous?.Tombstoned ?? 0) + result.TombstonedCount * add;
        receipt.SemanticAutoResolved = (previous?.SemanticAutoResolved ?? 0) + result.SemanticAutoResolvedCount * add;
        receipt.BusinessWorkItemActionable = previous?.BusinessWorkItemActionable ?? 0;
        receipt.FinalConvergenceStatus = result.ErrorCode == GovernanceBatchErrorCode.None ? "ExecutionCompleted" : result.ErrorCode.ToString();
        receipt.StoppedReason = result.StoppedReason;
        receipt.AuditIdsJson = JsonSerializer.Serialize(MergeAuditIds(previous, result.AuditIds), JsonOptions);
        receipt.ProjectIdsJson = JsonSerializer.Serialize(request.ProjectIds ?? DeserializeStrings(previous?.ProjectIdsJson), JsonOptions);
        receipt.IsReplay = result.IsReplay;
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task RecordInternalRetentionAsync(
        InternalMaturedDeleteBatchResult result,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryWrite);
        var eventKey = Hash($"internal-retention\n{result.GovernanceRunId}");
        var receipt = NewReceipt(actor, result.GovernanceRunId, eventKey, "InternalRetentionWorker", startedAt);
        receipt.CoverageComplete = true;
        receipt.DeleteMatured = result.ScannedCount;
        receipt.AutoDeleted = result.DeletedCount;
        receipt.DeleteCancelled = result.CancelledCount;
        receipt.Failed = result.FailedCount;
        receipt.Tombstoned = result.TombstoneIds.Count;
        receipt.FinalConvergenceStatus = result.FailedCount > 0 ? "ConvergedWithExceptions" : "InternalRetentionCompleted";
        receipt.StoppedReason = result.StoppedReason;
        receipt.AuditIdsJson = JsonSerializer.Serialize(result.AuditIds.Distinct(), JsonOptions);
        receipt.ProjectIdsJson = JsonSerializer.Serialize(result.ProjectIds, JsonOptions);
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task<GovernanceRunReceiptResult?> GetAsync(string governanceRunId, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        if (string.IsNullOrWhiteSpace(governanceRunId)) throw new ArgumentException("GovernanceRunId is required.", nameof(governanceRunId));
        var entity = await LatestAsync(governanceRunId.Trim(), actor, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<GovernanceRunReceiptResult>> ListAsync(
        GovernanceRunReceiptListRequest request,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var limit = Math.Clamp(request.Limit, 1, 100);
        var offset = Math.Max(0, request.Offset);
        var recent = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
            .OrderByDescending(x => x.EventSequence)
            .Take(1_000)
            .ToListAsync(cancellationToken);
        var latest = recent.GroupBy(x => x.GovernanceRunId, StringComparer.Ordinal)
            .Select(x => x.OrderByDescending(v => v.EventSequence).First());
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            var projectId = ProjectContext.Normalize(request.ProjectId);
            ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
            latest = latest.Where(x => DeserializeStrings(x.ProjectIdsJson).Contains(projectId, StringComparer.OrdinalIgnoreCase));
        }
        return latest.OrderByDescending(x => x.EventSequence).Skip(offset).Take(limit).Select(Map).ToArray();
    }

    private GovernanceRunReceipt NewReceipt(
        ContextHubRequestActor actor,
        string governanceRunId,
        string eventKey,
        string executionMode,
        DateTimeOffset startedAt)
    {
        var now = timeProvider.GetUtcNow();
        return new GovernanceRunReceipt
        {
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            GovernanceRunId = governanceRunId.Trim(),
            EventKey = eventKey,
            Actor = string.IsNullOrWhiteSpace(actor.Username) ? "unknown" : actor.Username,
            ExecutionMode = executionMode,
            StartedAt = startedAt,
            CompletedAt = now,
            ToolContractVersion = GovernanceToolContract.ToolContractVersion,
            SchemaHash = GovernanceToolContract.SchemaHash,
            PublishedCatalogVersion = GovernanceToolContract.PublishedCatalogVersion,
            CreatedAt = now
        };
    }

    private async Task InsertImmutableAsync(GovernanceRunReceipt receipt, CancellationToken cancellationToken)
    {
        await dbContext.GovernanceRunReceipts.AddAsync(receipt, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            dbContext.Entry(receipt).State = EntityState.Detached;
            var exists = await dbContext.GovernanceRunReceipts.AsNoTracking().AnyAsync(x =>
                x.TenantId == receipt.TenantId && x.OwnerUserId == receipt.OwnerUserId &&
                x.GovernanceRunId == receipt.GovernanceRunId && x.EventKey == receipt.EventKey,
                cancellationToken);
            if (!exists) throw;
        }
    }

    private Task<GovernanceRunReceipt?> LatestAsync(
        string governanceRunId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
        => dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId && x.GovernanceRunId == governanceRunId)
            .OrderByDescending(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);

    private ContextHubRequestActor RequireActor(string scope)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, scope);
        return actor;
    }

    private static int SumCandidates(FullGovernanceCoverageResult? coverage)
        => coverage is null ? 0 :
            coverage.ProjectCoverage.CandidateCount + coverage.HierarchyCoverage.CandidateCount +
            coverage.MemoryCoverage.CandidateCount + coverage.PreferenceCoverage.CandidateCount +
            coverage.ArtifactCoverage.CandidateCount + coverage.DiscussionCoverage.CandidateCount +
            coverage.WorkItemCoverage.CandidateCount + coverage.InsightCoverage.CandidateCount +
            coverage.SuggestedActionCoverage.CandidateCount + coverage.ProposalCoverage.CandidateCount +
            coverage.LogCoverage.CandidateCount;

    private static string CanonicalExecutionKey(GovernanceBatchExecuteRequest request)
        => JsonSerializer.Serialize(request with { ToolContractVersion = null, SchemaHash = null }, JsonOptions);

    private static IReadOnlyList<Guid> MergeAuditIds(GovernanceRunReceipt? previous, IReadOnlyList<Guid> current)
        => DeserializeGuids(previous?.AuditIdsJson).Concat(current).Distinct().Order().ToArray();

    private static IReadOnlyList<Guid> DeserializeGuids(string? json)
    {
        try { return JsonSerializer.Deserialize<Guid[]>(json ?? "[]", JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static IReadOnlyList<string> DeserializeStrings(string? json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json ?? "[]", JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static GovernanceRunReceiptResult Map(GovernanceRunReceipt x)
        => new(x.Id, x.GovernanceRunId, x.Actor, x.ExecutionMode, x.StartedAt, x.CompletedAt,
            x.ToolContractVersion, x.SchemaHash, x.PublishedCatalogVersion, x.InitialSnapshotToken,
            x.FinalSnapshotToken, x.CoverageComplete, x.InitialGovernanceActionable,
            x.FinalGovernanceActionable, x.CandidateCount, x.ExecutionActionableCount,
            x.GovernedExceptionCount, x.Applied, x.Failed, x.Deferred, x.RequiresUserDecision,
            x.HostBlocked, x.Quarantined, x.DeleteEligible, x.DeleteMatured, x.AutoDeleted,
            x.DeleteCancelled, x.Tombstoned, x.SemanticAutoResolved, x.BusinessWorkItemActionable,
            x.FinalConvergenceStatus, x.StoppedReason, DeserializeGuids(x.AuditIdsJson),
            DeserializeStrings(x.ProjectIdsJson), x.IsReplay);

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

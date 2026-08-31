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
        var runId = RequireGovernanceRunId(result.GovernanceRunId);
        var previous = await LatestAsync(runId, actor, cancellationToken);
        var snapshot = result.DurableMemoryCoverage?.SnapshotToken ?? string.Empty;
        var eventKey = Hash($"review\n{snapshot}\n{result.IsReReview}\n{result.Convergence.Status}\n{result.Convergence.GovernanceActionableCount}");
        var receipt = NewReceipt(actor, runId, eventKey, "Review", "ReviewCompleted", "Completed", startedAt,
            result.ReceiptContractIdentity);
        CopyCumulative(previous, receipt);
        receipt.InitialSnapshotToken = previous?.InitialSnapshotToken ?? snapshot;
        receipt.FinalSnapshotToken = snapshot;
        receipt.CoverageComplete = result.Convergence.CoverageComplete;
        receipt.InitialGovernanceActionable = previous?.InitialGovernanceActionable ?? result.Convergence.GovernanceActionableCount;
        receipt.FinalGovernanceActionable = result.Convergence.GovernanceActionableCount;
        receipt.CandidateCount = SumCandidates(result.GovernanceCoverage);
        receipt.ExecutionActionableCount = result.Convergence.GovernanceActionableCount;
        receipt.GovernedExceptionCount = result.Convergence.GovernedExceptionCount;
        receipt.Deferred = result.Convergence.DeferredCount;
        receipt.RequiresUserDecision = result.Convergence.RequiresUserDecisionCount;
        receipt.HostBlocked = result.Convergence.HostBlockedCount;
        var currentExceptionStates = result.GovernedExceptionStates
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
        var exceptionDelta = ComputeExceptionDelta(
            DeserializeExceptionStates(previous?.GovernedExceptionStatesJson),
            currentExceptionStates);
        receipt.ExceptionNew = exceptionDelta.New;
        receipt.ExceptionResolved = exceptionDelta.Resolved;
        receipt.ExceptionUnchanged = exceptionDelta.Unchanged;
        receipt.ExceptionEscalated = exceptionDelta.Escalated;
        receipt.GovernedExceptionStatesJson = JsonSerializer.Serialize(currentExceptionStates, JsonOptions);
        receipt.DeleteEligible = result.DeleteEligibleCount;
        receipt.DeleteMatured = result.DeleteMaturedCount;
        receipt.DeleteCancelled = result.DeleteCancelledCount;
        receipt.BusinessWorkItemActionable = result.Convergence.BusinessWorkItemActionableCount;
        receipt.FinalConvergenceStatus = result.Convergence.Status;
        receipt.StoppedReason = "ReviewCompleted";
        receipt.ProjectIdsJson = JsonSerializer.Serialize(
            result.Projects.Select(x => x.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase), JsonOptions);
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task RecordExecutionStartedAsync(
        GovernanceBatchExecuteRequest request,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(request.GovernanceRunId);
        var requestIdentity = RequestIdentity(request);
        var requestHash = RequestHash(request);
        var previous = await LatestAsync(runId, actor, cancellationToken);
        var receipt = NewReceipt(
            actor, runId, Hash($"batch-received\n{runId}\n{requestIdentity}"),
            request.ExecutionMode.ToString(), "BatchReceived", "Running", startedAt,
            request.ReceiptContractIdentity);
        CopyCumulative(previous, receipt);
        receipt.LatestBatchReceived = true;
        receipt.RequestIdentityHash = requestIdentity;
        receipt.RequestHash = requestHash;
        receipt.FinalSnapshotToken = request.SnapshotToken ?? previous?.FinalSnapshotToken ?? string.Empty;
        receipt.ProjectIdsJson = JsonSerializer.Serialize(
            request.ProjectIds ?? DeserializeStrings(previous?.ProjectIdsJson), JsonOptions);
        receipt.StoppedReason = "BatchReceived";
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task RecordExecutionAsync(
        GovernanceBatchExecuteRequest request,
        GovernanceBatchExecuteResult result,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(request.GovernanceRunId);
        var requestIdentity = RequestIdentity(request);
        var requestHash = RequestHash(request);
        var previous = await LatestAsync(runId, actor, cancellationToken);
        var status = ResolveTerminalStatus(result);
        var eventKind = result.IsReplay ? "BatchReplay" : "BatchCompleted";
        var eventKey = Hash($"{eventKind}\n{runId}\n{requestIdentity}\n{status}\n{result.StoppedReason}");
        var receipt = NewReceipt(actor, runId, eventKey, request.ExecutionMode.ToString(), eventKind, status, startedAt,
            request.ReceiptContractIdentity);
        CopyCumulative(previous, receipt);
        var add = result.IsReplay ? 0 : 1;
        receipt.LatestBatchReceived = true;
        receipt.RequestIdentityHash = requestIdentity;
        receipt.RequestHash = requestHash;
        receipt.FailurePhase = ResolveFailurePhase(result);
        receipt.InitialSnapshotToken = previous?.InitialSnapshotToken ?? result.SnapshotToken;
        receipt.FinalSnapshotToken = result.SnapshotToken;
        receipt.Applied += result.AppliedCount * add;
        receipt.Failed += result.FailedCount * add;
        receipt.Deferred = Math.Max(receipt.Deferred, result.DeferredCount);
        receipt.RequiresUserDecision = Math.Max(receipt.RequiresUserDecision, result.RequiresUserDecisionCount);
        receipt.HostBlocked += result.ErrorCode == GovernanceBatchErrorCode.HostBlockedMaturedDelete ? add : 0;
        receipt.Quarantined += result.QuarantinedCount * add;
        receipt.DeleteEligible = result.DeleteEligibleCount;
        receipt.DeleteMatured = result.DeleteMaturedCount;
        receipt.AutoDeleted += result.AutoDeletedCount * add;
        receipt.DeleteCancelled = result.DeleteCancelledCount;
        receipt.Tombstoned += result.TombstonedCount * add;
        receipt.SemanticAutoResolved += result.SemanticAutoResolvedCount * add;
        receipt.FinalConvergenceStatus = result.ErrorCode == GovernanceBatchErrorCode.None
            ? "ExecutionCompleted"
            : result.ErrorCode.ToString();
        receipt.StoppedReason = result.StoppedReason;
        receipt.AuditIdsJson = JsonSerializer.Serialize(MergeAuditIds(previous, result.AuditIds), JsonOptions);
        receipt.ProjectIdsJson = JsonSerializer.Serialize(
            request.ProjectIds ?? DeserializeStrings(previous?.ProjectIdsJson), JsonOptions);
        receipt.IsReplay = result.IsReplay;
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task RecordExecutionStoppedAsync(
        GovernanceBatchExecuteRequest request,
        DateTimeOffset startedAt,
        string status,
        string stoppedReason,
        string failurePhase,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(request.GovernanceRunId);
        var requestIdentity = RequestIdentity(request);
        var requestHash = RequestHash(request);
        var previous = await LatestAsync(runId, actor, cancellationToken);
        var normalizedStatus = string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase) ? "Failed" : "Stopped";
        var receipt = NewReceipt(
            actor, runId, Hash($"batch-stopped\n{runId}\n{requestIdentity}\n{normalizedStatus}\n{stoppedReason}"),
            request.ExecutionMode.ToString(), "BatchStopped", normalizedStatus, startedAt,
            request.ReceiptContractIdentity);
        CopyCumulative(previous, receipt);
        receipt.LatestBatchReceived = true;
        receipt.RequestIdentityHash = requestIdentity;
        receipt.RequestHash = requestHash;
        receipt.FailurePhase = failurePhase;
        receipt.FinalSnapshotToken = request.SnapshotToken ?? previous?.FinalSnapshotToken ?? string.Empty;
        receipt.ProjectIdsJson = JsonSerializer.Serialize(
            request.ProjectIds ?? DeserializeStrings(previous?.ProjectIdsJson), JsonOptions);
        receipt.StoppedReason = stoppedReason;
        receipt.FinalConvergenceStatus = normalizedStatus;
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task<GovernanceBatchExecuteResult?> GetTerminalPreExecutionReplayAsync(
        GovernanceBatchExecuteRequest request,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(request.GovernanceRunId);
        var requestHash = RequestHash(request);
        var receipt = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.GovernanceRunId == runId &&
                        x.RequestHash == requestHash &&
                        x.LatestBatchReceived &&
                        x.FailurePhase.StartsWith("PreExecution") &&
                        (x.Status == "Failed" || x.Status == "Stopped"))
            .OrderByDescending(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (receipt is null || !Enum.TryParse<GovernanceBatchErrorCode>(receipt.StoppedReason, out var errorCode))
        {
            return null;
        }

        return GovernanceBatchExecuteResult.Failure(
            request,
            new GovernanceBatchException(errorCode, receipt.StoppedReason)) with
        {
            IsReplay = true
        };
    }

    public async Task RecordInternalRetentionAsync(
        InternalMaturedDeleteBatchResult result,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryWrite);
        var runId = RequireGovernanceRunId(result.GovernanceRunId);
        var status = result.FailedCount > 0 ? "Failed" : "Completed";
        var receipt = NewReceipt(
            actor, runId, Hash($"internal-retention\n{runId}"),
            "InternalRetentionWorker", "InternalRetentionCompleted", status, startedAt);
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

    public async Task<GovernanceRunReceiptResult?> GetAsync(
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(governanceRunId);
        var entity = await LatestAsync(runId, actor, cancellationToken);
        if (entity is null)
        {
            return null;
        }

        EnsureReceiptProjectsAllowed(actor, DeserializeStrings(entity.ProjectIdsJson));
        return await MapAsync(entity, actor, cancellationToken);
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
            .Select(x => x.OrderByDescending(v => v.EventSequence).First())
            .Where(x => CanReadReceiptProjects(actor, DeserializeStrings(x.ProjectIdsJson)));
        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            var projectId = ProjectContext.Normalize(request.ProjectId);
            ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
            latest = latest.Where(x => DeserializeStrings(x.ProjectIdsJson)
                .Contains(projectId, StringComparer.OrdinalIgnoreCase));
        }

        var page = latest.OrderByDescending(x => x.EventSequence).Skip(offset).Take(limit).ToArray();
        var results = new List<GovernanceRunReceiptResult>(page.Length);
        foreach (var entity in page)
        {
            results.Add(await MapAsync(entity, actor, cancellationToken));
        }
        return results;
    }

    private GovernanceRunReceipt NewReceipt(
        ContextHubRequestActor actor,
        string governanceRunId,
        string eventKey,
        string executionMode,
        string eventType,
        string status,
        DateTimeOffset startedAt,
        GovernanceReceiptContractIdentity? contractIdentity = null)
    {
        var now = timeProvider.GetUtcNow();
        return new GovernanceRunReceipt
        {
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            GovernanceRunId = governanceRunId,
            EventKey = eventKey,
            Actor = string.IsNullOrWhiteSpace(actor.Username) ? "unknown" : actor.Username,
            ExecutionMode = executionMode,
            EventType = eventType,
            Status = status,
            StartedAt = startedAt,
            CompletedAt = now,
            ToolContractVersion = contractIdentity?.ToolContractVersion ?? GovernanceToolContract.ToolContractVersion,
            SchemaHash = contractIdentity?.SchemaHash ?? GovernanceToolContract.SchemaHash,
            PublishedCatalogVersion = contractIdentity?.PublishedCatalogVersion ?? GovernanceToolContract.PublishedCatalogVersion,
            CreatedAt = now
        };
    }

    private async Task<GovernanceRunReceiptResult> MapAsync(
        GovernanceRunReceipt receipt,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        var executions = await dbContext.GovernanceBatchExecutions.AsNoTracking()
            .Include(x => x.Run)
            .Where(x => x.Run != null &&
                        x.Run.TenantId == actor.TenantId &&
                        x.Run.OwnerUserId == actor.UserId &&
                        x.Run.GovernanceRunId == receipt.GovernanceRunId)
            .OrderByDescending(x => x.CreatedAt)
            .ThenByDescending(x => x.Id)
            .Take(100)
            .ToListAsync(cancellationToken);
        var batchReceipt = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.GovernanceRunId == receipt.GovernanceRunId &&
                        x.LatestBatchReceived)
            .OrderByDescending(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);
        var receivedReceipt = batchReceipt is null ||
                              string.Equals(batchReceipt.EventType, "BatchReceived", StringComparison.Ordinal)
            ? batchReceipt
            : await dbContext.GovernanceRunReceipts.AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId &&
                            x.OwnerUserId == actor.UserId &&
                            x.GovernanceRunId == receipt.GovernanceRunId &&
                            x.EventType == "BatchReceived" &&
                            x.RequestIdentityHash == batchReceipt.RequestIdentityHash)
                .OrderByDescending(x => x.EventSequence)
                .FirstOrDefaultAsync(cancellationToken);
        var execution = string.IsNullOrWhiteSpace(batchReceipt?.RequestIdentityHash)
            ? executions.FirstOrDefault()
            : executions.FirstOrDefault(x => string.Equals(
                ExecutionIdentity(x), batchReceipt.RequestIdentityHash, StringComparison.Ordinal)) ??
              (receivedReceipt is null
                  ? null
                  : executions.FirstOrDefault(x => x.CreatedAt >= receivedReceipt.CreatedAt));
        var latestBatch = BuildLatestBatch(batchReceipt, execution);
        var readStatus = latestBatch?.Status ??
            (string.IsNullOrWhiteSpace(receipt.Status) ? InferLegacyStatus(receipt) : receipt.Status);
        var readStoppedReason = latestBatch is { Status: not "Running" } &&
                                !string.IsNullOrWhiteSpace(latestBatch.StoppedReason)
            ? latestBatch.StoppedReason
            : receipt.StoppedReason;

        return new GovernanceRunReceiptResult(
            receipt.Id, receipt.GovernanceRunId, receipt.Actor, receipt.ExecutionMode,
            receipt.StartedAt, receipt.CompletedAt, receipt.ToolContractVersion, receipt.SchemaHash,
            receipt.PublishedCatalogVersion, receipt.InitialSnapshotToken, receipt.FinalSnapshotToken,
            receipt.CoverageComplete, receipt.InitialGovernanceActionable, receipt.FinalGovernanceActionable,
            receipt.CandidateCount, receipt.ExecutionActionableCount, receipt.GovernedExceptionCount,
            receipt.Applied, receipt.Failed, receipt.Deferred, receipt.RequiresUserDecision,
            receipt.HostBlocked, receipt.Quarantined, receipt.DeleteEligible, receipt.DeleteMatured,
            receipt.AutoDeleted, receipt.DeleteCancelled, receipt.Tombstoned, receipt.SemanticAutoResolved,
            receipt.BusinessWorkItemActionable, receipt.FinalConvergenceStatus, readStoppedReason,
            DeserializeGuids(receipt.AuditIdsJson), DeserializeStrings(receipt.ProjectIdsJson), receipt.IsReplay,
            RunExists: true,
            Status: readStatus,
            LatestBatchReceived: batchReceipt is not null,
            RequestIdentityHash: batchReceipt?.RequestIdentityHash ?? string.Empty,
            LatestBatch: latestBatch)
        {
            ExceptionDelta = new GovernanceExceptionDeltaResult(
                receipt.ExceptionNew,
                receipt.ExceptionResolved,
                receipt.ExceptionUnchanged,
                receipt.ExceptionEscalated),
            GovernedExceptionStates = DeserializeExceptionStates(receipt.GovernedExceptionStatesJson)
        };
    }

    private static GovernanceBatchOutcomeResult? BuildLatestBatch(
        GovernanceRunReceipt? batchReceipt,
        GovernanceBatchExecution? execution)
    {
        if (batchReceipt is null && execution is null)
        {
            return null;
        }

        GovernanceBatchExecuteResult? result = null;
        if (execution is not null)
        {
            try { result = JsonSerializer.Deserialize<GovernanceBatchExecuteResult>(execution.ResultJson, JsonOptions); }
            catch (JsonException) { }
        }

        var executed = execution is not null && string.Equals(execution.Status, "Completed", StringComparison.Ordinal);
        var status = executed
            ? "Completed"
            : !string.IsNullOrWhiteSpace(batchReceipt?.Status) &&
              !string.Equals(batchReceipt.Status, "Running", StringComparison.OrdinalIgnoreCase)
                ? batchReceipt.Status
                : execution is null ? "Running" : "Stopped";
        var receivedAt = batchReceipt?.CreatedAt ?? execution?.CreatedAt ?? DateTimeOffset.MinValue;
        var snapshotToken = result?.SnapshotToken ?? execution?.Run?.SnapshotToken ?? batchReceipt?.FinalSnapshotToken ?? string.Empty;
        var snapshotIdentity = ParseSnapshotIdentity(snapshotToken);
        return new GovernanceBatchOutcomeResult(
            Received: batchReceipt is not null || execution is not null,
            Executed: executed,
            RequestIdentityHash: batchReceipt?.RequestIdentityHash ?? string.Empty,
            RequestHash: execution?.RequestHash ?? batchReceipt?.RequestHash ?? string.Empty,
            Status: status,
            FailurePhase: batchReceipt?.FailurePhase ?? string.Empty,
            ReceivedAt: receivedAt,
            StartedAt: execution?.CreatedAt ?? batchReceipt?.StartedAt,
            CompletedAt: execution?.CompletedAt,
            SnapshotToken: snapshotToken,
            SnapshotGeneration: snapshotIdentity.Generation,
            IsReReview: snapshotIdentity.IsReReview,
            CursorBefore: execution?.CursorBefore ?? string.Empty,
            NextCursor: result is null ? execution?.CursorAfter : result.NextCursor,
            HasMore: result?.HasMore ?? true,
            RequiresReReview: result?.RequiresReReview ?? !executed,
            StoppedReason: result?.StoppedReason ?? batchReceipt?.StoppedReason ?? string.Empty,
            Scanned: result?.ScannedCount ?? 0,
            Attempted: result?.AttemptedCount ?? 0,
            Applied: result?.AppliedCount ?? 0,
            NoOp: result?.NoOpCount ?? 0,
            Failed: result?.FailedCount ?? 0,
            Deferred: result?.DeferredCount ?? 0,
            RequiresUserDecision: result?.RequiresUserDecisionCount ?? 0,
            Quarantined: result?.QuarantinedCount ?? 0,
            DeleteEligible: result?.DeleteEligibleCount ?? 0,
            DeleteMatured: result?.DeleteMaturedCount ?? 0,
            AutoDeleted: result?.AutoDeletedCount ?? 0,
            DeleteCancelled: result?.DeleteCancelledCount ?? 0,
            Tombstoned: result?.TombstonedCount ?? 0,
            SemanticAutoResolved: result?.SemanticAutoResolvedCount ?? 0,
            RemainingHumanDecision: result?.RemainingHumanDecisionCount ?? 0,
            ProtectedRetention: result?.ProtectedRetentionCount ?? 0,
            AuditIds: result?.AuditIds ?? [],
            IsReplay: batchReceipt?.IsReplay ?? result?.IsReplay ?? false);
    }

    private static (int Generation, bool IsReReview) ParseSnapshotIdentity(string snapshotToken)
    {
        var marker = snapshotToken.Split(':', StringSplitOptions.RemoveEmptyEntries).LastOrDefault();
        if (marker is { Length: > 1 } &&
            (marker[0] == 'i' || marker[0] == 'r') &&
            int.TryParse(marker.AsSpan(1), out var generation))
        {
            return (generation, marker[0] == 'r');
        }

        return (0, false);
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
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.GovernanceRunId == governanceRunId)
            .OrderByDescending(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);

    private ContextHubRequestActor RequireActor(string scope)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, scope);
        return actor;
    }

    private static void CopyCumulative(GovernanceRunReceipt? previous, GovernanceRunReceipt receipt)
    {
        if (previous is null) return;
        receipt.InitialSnapshotToken = previous.InitialSnapshotToken;
        receipt.FinalSnapshotToken = previous.FinalSnapshotToken;
        receipt.CoverageComplete = previous.CoverageComplete;
        receipt.InitialGovernanceActionable = previous.InitialGovernanceActionable;
        receipt.FinalGovernanceActionable = previous.FinalGovernanceActionable;
        receipt.CandidateCount = previous.CandidateCount;
        receipt.ExecutionActionableCount = previous.ExecutionActionableCount;
        receipt.GovernedExceptionCount = previous.GovernedExceptionCount;
        receipt.Applied = previous.Applied;
        receipt.Failed = previous.Failed;
        receipt.Deferred = previous.Deferred;
        receipt.RequiresUserDecision = previous.RequiresUserDecision;
        receipt.HostBlocked = previous.HostBlocked;
        receipt.ExceptionNew = previous.ExceptionNew;
        receipt.ExceptionResolved = previous.ExceptionResolved;
        receipt.ExceptionUnchanged = previous.ExceptionUnchanged;
        receipt.ExceptionEscalated = previous.ExceptionEscalated;
        receipt.GovernedExceptionStatesJson = previous.GovernedExceptionStatesJson;
        receipt.Quarantined = previous.Quarantined;
        receipt.DeleteEligible = previous.DeleteEligible;
        receipt.DeleteMatured = previous.DeleteMatured;
        receipt.AutoDeleted = previous.AutoDeleted;
        receipt.DeleteCancelled = previous.DeleteCancelled;
        receipt.Tombstoned = previous.Tombstoned;
        receipt.SemanticAutoResolved = previous.SemanticAutoResolved;
        receipt.BusinessWorkItemActionable = previous.BusinessWorkItemActionable;
        receipt.FinalConvergenceStatus = previous.FinalConvergenceStatus;
        receipt.StoppedReason = previous.StoppedReason;
        receipt.RequestHash = previous.RequestHash;
        receipt.FailurePhase = previous.FailurePhase;
        receipt.AuditIdsJson = previous.AuditIdsJson;
        receipt.ProjectIdsJson = previous.ProjectIdsJson;
    }

    private static bool CanReadReceiptProjects(ContextHubRequestActor actor, IReadOnlyList<string> projectIds)
    {
        if (actor.AllowedProjectIds.Count == 0) return true;
        if (projectIds.Count == 0) return false;
        return projectIds.All(projectId =>
            ProjectContext.IsShared(projectId) || ProjectContext.IsUser(projectId) ||
            actor.AllowedProjectIds.Contains(projectId, StringComparer.OrdinalIgnoreCase));
    }

    private static void EnsureReceiptProjectsAllowed(ContextHubRequestActor actor, IReadOnlyList<string> projectIds)
    {
        if (!CanReadReceiptProjects(actor, projectIds))
        {
            throw new UnauthorizedAccessException("The governance run receipt is outside the current project authorization boundary.");
        }
        ActorAuthorization.EnsureProjectsAllowed(actor, projectIds, write: false);
    }

    private static GovernanceExceptionDeltaResult ComputeExceptionDelta(
        IReadOnlyList<GovernanceExceptionStateResult> previous,
        IReadOnlyList<GovernanceExceptionStateResult> current)
    {
        var previousByKey = previous.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var currentByKey = current.ToDictionary(x => x.Key, StringComparer.Ordinal);
        var added = currentByKey.Keys.Count(key => !previousByKey.ContainsKey(key));
        var resolved = previousByKey.Keys.Count(key => !currentByKey.ContainsKey(key));
        var escalated = currentByKey.Count(pair =>
            previousByKey.TryGetValue(pair.Key, out var old) && pair.Value.Severity > old.Severity);
        var unchanged = currentByKey.Count(pair =>
            previousByKey.TryGetValue(pair.Key, out var old) && pair.Value.Severity == old.Severity);
        return new GovernanceExceptionDeltaResult(added, resolved, unchanged, escalated);
    }

    private static IReadOnlyList<GovernanceExceptionStateResult> DeserializeExceptionStates(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<GovernanceExceptionStateResult[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static int SumCandidates(FullGovernanceCoverageResult? coverage)
        => coverage is null ? 0 :
            coverage.ProjectCoverage.CandidateCount + coverage.HierarchyCoverage.CandidateCount +
            coverage.MemoryCoverage.CandidateCount + coverage.PreferenceCoverage.CandidateCount +
            coverage.ArtifactCoverage.CandidateCount + coverage.DiscussionCoverage.CandidateCount +
            coverage.WorkItemCoverage.CandidateCount + coverage.InsightCoverage.CandidateCount +
            coverage.SuggestedActionCoverage.CandidateCount + coverage.ProposalCoverage.CandidateCount +
            coverage.LogCoverage.CandidateCount;

    private static string ResolveTerminalStatus(GovernanceBatchExecuteResult result)
        => result.ErrorCode == GovernanceBatchErrorCode.None
            ? "Completed"
            : result.ErrorCode == GovernanceBatchErrorCode.HostBlockedMaturedDelete
                ? "Stopped"
                : "Failed";

    private static string ResolveFailurePhase(GovernanceBatchExecuteResult result)
        => result.ErrorCode is GovernanceBatchErrorCode.CursorScopeMismatch or
            GovernanceBatchErrorCode.CursorSnapshotMismatch
            ? "PreExecutionScopeValidation"
            : result.ErrorCode != GovernanceBatchErrorCode.None
                ? "PreExecutionValidation"
                : result.FailedCount > 0 ? "ItemExecution" : string.Empty;

    private static string InferLegacyStatus(GovernanceRunReceipt receipt)
        => receipt.FinalConvergenceStatus.Contains("Failed", StringComparison.OrdinalIgnoreCase)
            ? "Failed"
            : receipt.StoppedReason.Contains("Stopped", StringComparison.OrdinalIgnoreCase)
                ? "Stopped"
                : "Completed";

    private static string RequestIdentity(GovernanceBatchExecuteRequest request)
        => Hash(JsonSerializer.Serialize(
            request with
            {
                GovernanceRunId = request.GovernanceRunId.Trim(),
                ProjectIds = request.ProjectIds?.Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                SnapshotToken = request.SnapshotToken?.Trim(),
                Cursor = request.Cursor?.Trim(),
                AllowedActionTypes = request.AllowedActionTypes?.Distinct().Order().ToArray(),
                ToolContractVersion = null,
                SchemaHash = null
            }, JsonOptions));

    private static string RequestHash(GovernanceBatchExecuteRequest request)
        => Hash(JsonSerializer.Serialize(
            request with
            {
                GovernanceRunId = request.GovernanceRunId.Trim(),
                ProjectIds = request.ProjectIds?.Select(x => ProjectContext.Normalize(x)).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
                SnapshotToken = request.SnapshotToken?.Trim(),
                Cursor = request.Cursor?.Trim(),
                AllowedActionTypes = request.AllowedActionTypes?.Distinct().Order().ToArray(),
                ToolContractVersion = request.ToolContractVersion?.Trim(),
                SchemaHash = request.SchemaHash?.Trim().ToLowerInvariant()
            }, JsonOptions));

    private static string ExecutionIdentity(GovernanceBatchExecution execution)
    {
        try
        {
            var request = JsonSerializer.Deserialize<GovernanceBatchExecuteRequest>(execution.RequestJson, JsonOptions);
            return request is null ? string.Empty : RequestIdentity(request);
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    private static IReadOnlyList<Guid> MergeAuditIds(
        GovernanceRunReceipt? previous,
        IReadOnlyList<Guid> current)
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

    private static string RequireGovernanceRunId(string governanceRunId)
    {
        if (string.IsNullOrWhiteSpace(governanceRunId))
        {
            throw new ArgumentException("GovernanceRunId is required.", nameof(governanceRunId));
        }
        return governanceRunId.Trim();
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

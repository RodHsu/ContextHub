using System.Buffers.Binary;
using System.Data;
using System.Globalization;
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
    internal const string ScheduledAcceptanceEvidenceVersion = "1";
    internal const string ScheduledRuntimeEvidenceVersion = "1";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly GovernanceReceiptContractIdentity CurrentScheduledContractIdentity = new(
        ScheduledGovernanceContract.ToolContractVersion,
        ScheduledGovernanceContract.SchemaHash,
        ScheduledGovernanceContract.PublishedCatalogVersion);

    public async Task RecordReviewStartedAsync(
        string governanceRunId,
        DateTimeOffset startedAt,
        GovernanceReceiptContractIdentity contractIdentity,
        CancellationToken cancellationToken,
        bool isReReview = false)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(governanceRunId);
        var previous = await LatestAsync(runId, actor, cancellationToken);
        var executionMode = ResolveReceiptExecutionMode(contractIdentity);
        var receipt = NewReceipt(
            actor,
            runId,
            Hash($"review-received\n{runId}"),
            executionMode,
            "ReviewReceived",
            "Running",
            startedAt,
            contractIdentity);
        CopyCumulative(previous, receipt);
        receipt.ProjectIdsJson = previous?.ProjectIdsJson ?? "[]";
        receipt.StoppedReason = "ReviewReceived";
        receipt.RequestIdentityHash = ReviewRequestIdentityHash(runId, isReReview);
        SetCanonicalReviewEventKey(receipt);
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task RecordReviewAsync(
        KnowledgeReviewResult result,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(result.GovernanceRunId);
        var previous = await LatestAsync(runId, actor, cancellationToken);
        var durableEvidence = result.DurableMemoryCoverage;
        var snapshot = durableEvidence?.SnapshotToken ?? string.Empty;
        var eventKey = Hash(string.Join('\n',
            "review",
            snapshot,
            result.IsReReview,
            result.Convergence.Status,
            result.Convergence.GovernanceActionableCount,
            durableEvidence?.AuthorizedGovernanceDurableMemoryCount,
            durableEvidence?.GovernanceCoveredDurableMemoryCount,
            durableEvidence?.ScannedCount,
            durableEvidence?.TotalCount,
            durableEvidence?.GovernanceProjectIds.Count(ProjectContext.IsShared),
            durableEvidence?.GovernanceProjectIds.Count(ProjectContext.IsUser),
            durableEvidence?.CountInvariantSatisfied));
        var executionMode = ResolveReceiptExecutionMode(result.ReceiptContractIdentity);
        var receipt = NewReceipt(actor, runId, eventKey, executionMode, "ReviewCompleted", "Completed", startedAt,
            result.ReceiptContractIdentity);
        CopyCumulative(previous, receipt);
        receipt.InitialSnapshotToken = snapshot;
        receipt.FinalSnapshotToken = snapshot;
        receipt.CoverageComplete = result.Convergence.CoverageComplete;
        receipt.InitialGovernanceActionable = result.Convergence.GovernanceActionableCount;
        receipt.FinalGovernanceActionable = result.Convergence.GovernanceActionableCount;
        receipt.CandidateCount = SumCandidates(result.GovernanceCoverage);
        receipt.ExecutionActionableCount = result.Convergence.GovernanceActionableCount;
        receipt.GovernedExceptionCount = result.Convergence.GovernedExceptionCount;
        receipt.Deferred = result.Convergence.DeferredCount;
        receipt.RequiresUserDecision = result.Convergence.RequiresUserDecisionCount;
        receipt.HostBlocked = result.Convergence.HostBlockedCount;
        var projectIds = result.Projects
            .Select(x => x.ProjectId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var currentExceptionStates = result.GovernedExceptionStates
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .ToArray();
        receipt.GovernedExceptionStatesJson = JsonSerializer.Serialize(currentExceptionStates, JsonOptions);
        receipt.DeleteEligible = result.DeleteEligibleCount;
        receipt.DeleteMatured = result.DeleteMaturedCount;
        receipt.DeleteCancelled = result.DeleteCancelledCount;
        receipt.BusinessWorkItemActionable = result.Convergence.BusinessWorkItemActionableCount;
        receipt.FinalConvergenceStatus = result.Convergence.Status;
        receipt.StoppedReason = "ReviewCompleted";
        receipt.ProjectIdsJson = JsonSerializer.Serialize(projectIds, JsonOptions);
        receipt.RequestIdentityHash = ReviewRequestIdentityHash(runId, result.IsReReview);
        SetCanonicalReviewEventKey(receipt);
        if (string.Equals(executionMode, "Scheduled", StringComparison.Ordinal) &&
            result.DurableMemoryCoverage is { } durableCoverage)
        {
            var sharedScopeOccurrences = durableCoverage.GovernanceProjectIds.Count(ProjectContext.IsShared);
            var userScopeOccurrences = durableCoverage.GovernanceProjectIds.Count(ProjectContext.IsUser);
            var countInvariant = new ScheduledGovernanceCountInvariant(
                durableCoverage.AuthorizedGovernanceDurableMemoryCount,
                durableCoverage.GovernanceCoveredDurableMemoryCount,
                durableCoverage.ScannedCount,
                durableCoverage.TotalCount,
                sharedScopeOccurrences,
                userScopeOccurrences,
                userScopeOccurrences == 0,
                durableCoverage.CountInvariantSatisfied &&
                sharedScopeOccurrences == 1 &&
                userScopeOccurrences == 0);
            ApplyScheduledCountInvariant(receipt, countInvariant);
        }
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    public async Task RecordScheduledDecisionAsync(
        ScheduledGovernanceReviewResult result,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.ScheduledGovernance);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("Scheduled governance decision projection requires a tenant owner or administrator.");
        }
        var runId = RequireGovernanceRunId(result.GovernanceRunId);
        var contractIdentity = new GovernanceReceiptContractIdentity(
            result.ToolContractVersion,
            result.SchemaHash,
            result.PublishedCatalogVersion);
        if (!ReceiptIdentityMatches(contractIdentity, CurrentScheduledContractIdentity))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "Scheduled governance decision projection requires the current scheduled contract identity.");
        }
        ValidateScheduledCountInvariant(result);
        var lineage = await GetScheduledLineageAsync(
            runId,
            CurrentScheduledContractIdentity,
            cancellationToken);
        if (lineage.RunExists && !lineage.IsValid)
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                $"Scheduled governance run lineage is invalid: {lineage.Status}.");
        }
        var previous = await LatestAsync(runId, actor, cancellationToken);
        var exceptionDelta = result.ExceptionDelta ?? new GovernanceExceptionDeltaResult(0, 0, 0, 0);
        var eventKey = Hash(string.Join('\n',
            "scheduled-decision",
            runId,
            result.SnapshotToken,
            result.IsReReview,
            result.Decision,
            result.CoverageComplete,
            result.AutomationActionableCount,
            result.RequiresUserDecisionCount,
            result.GovernedExceptionCount,
            exceptionDelta.New,
            exceptionDelta.Resolved,
            exceptionDelta.Unchanged,
            exceptionDelta.Escalated,
            result.CountInvariant.AuthorizedDurableMemoryCount,
            result.CountInvariant.CoveredDurableMemoryCount,
            result.CountInvariant.ScannedDurableMemoryCount,
            result.CountInvariant.TotalDurableMemoryCount,
            result.CountInvariant.SharedScopeOccurrences,
            result.CountInvariant.UserScopeOccurrences,
            result.CountInvariant.UserScopeHandledSeparately,
            result.CountInvariant.Satisfied));
        var receipt = NewReceipt(
            actor,
            runId,
            eventKey,
            "Scheduled",
            "ScheduledDecisionProjected",
            "Completed",
            startedAt,
            contractIdentity);
        CopyCumulative(previous, receipt);
        receipt.InitialSnapshotToken = result.SnapshotToken;
        receipt.FinalSnapshotToken = result.SnapshotToken;
        receipt.CoverageComplete = result.CoverageComplete;
        ApplyScheduledCountInvariant(receipt, result.CountInvariant);
        receipt.InitialGovernanceActionable = result.AutomationActionableCount;
        receipt.FinalGovernanceActionable = result.AutomationActionableCount;
        receipt.CandidateCount = result.CandidateCount;
        receipt.ExecutionActionableCount = result.AutomationActionableCount;
        receipt.GovernedExceptionCount = result.GovernedExceptionCount;
        receipt.Deferred = result.GovernedDeferredExceptionCount;
        receipt.RequiresUserDecision = result.RequiresUserDecisionCount;
        receipt.HostBlocked = result.GovernedHostBlockedExceptionCount;
        receipt.ExceptionNew = exceptionDelta.New;
        receipt.ExceptionResolved = exceptionDelta.Resolved;
        receipt.ExceptionUnchanged = exceptionDelta.Unchanged;
        receipt.ExceptionEscalated = exceptionDelta.Escalated;
        receipt.BusinessWorkItemActionable = result.BusinessWorkItemActionableCount;
        receipt.FinalConvergenceStatus = result.Decision.ToString();
        receipt.StoppedReason = "ScheduledDecisionProjected";
        receipt.ProjectIdsJson = JsonSerializer.Serialize(result.ResolvedProjectIds, JsonOptions);
        receipt.RequestIdentityHash = ReviewRequestIdentityHash(runId, result.IsReReview);
        SetCanonicalReviewEventKey(receipt);
        await InsertImmutableAsync(receipt, cancellationToken);
    }

    private static void ValidateScheduledCountInvariant(ScheduledGovernanceReviewResult result)
    {
        var invariant = result.CountInvariant;
        var recomputed = invariant.AuthorizedDurableMemoryCount >= 0 &&
                         invariant.AuthorizedDurableMemoryCount == invariant.CoveredDurableMemoryCount &&
                         invariant.CoveredDurableMemoryCount == invariant.ScannedDurableMemoryCount &&
                         invariant.ScannedDurableMemoryCount == invariant.TotalDurableMemoryCount &&
                         invariant.SharedScopeOccurrences == 1 &&
                         invariant.UserScopeOccurrences == 0 &&
                         invariant.UserScopeHandledSeparately;
        if (invariant.Satisfied != recomputed || (result.CoverageComplete && !recomputed))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "Scheduled governance count-invariant evidence is inconsistent.");
        }
    }

    private static void ApplyScheduledCountInvariant(
        GovernanceRunReceipt receipt,
        ScheduledGovernanceCountInvariant invariant)
    {
        receipt.AcceptanceEvidenceVersion = ScheduledAcceptanceEvidenceVersion;
        receipt.AuthorizedDurableMemoryCount = invariant.AuthorizedDurableMemoryCount;
        receipt.CoveredDurableMemoryCount = invariant.CoveredDurableMemoryCount;
        receipt.ScannedDurableMemoryCount = invariant.ScannedDurableMemoryCount;
        receipt.TotalDurableMemoryCount = invariant.TotalDurableMemoryCount;
        receipt.SharedScopeOccurrences = invariant.SharedScopeOccurrences;
        receipt.UserScopeOccurrences = invariant.UserScopeOccurrences;
        receipt.UserScopeHandledSeparately = invariant.UserScopeHandledSeparately;
        receipt.CountInvariantSatisfied = invariant.Satisfied;
    }

    public async Task RecordReviewStoppedAsync(
        string governanceRunId,
        DateTimeOffset startedAt,
        string status,
        string stoppedReason,
        string failurePhase,
        GovernanceReceiptContractIdentity contractIdentity,
        CancellationToken cancellationToken,
        bool isReReview = false)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(governanceRunId);
        var normalizedStatus = string.Equals(status, "Failed", StringComparison.OrdinalIgnoreCase)
            ? "Failed"
            : "Stopped";
        var normalizedReason = string.IsNullOrWhiteSpace(stoppedReason) ? "ReviewStopped" : stoppedReason.Trim();
        var normalizedPhase = string.IsNullOrWhiteSpace(failurePhase) ? "Review" : failurePhase.Trim();
        var previous = await LatestAsync(runId, actor, cancellationToken);
        var executionMode = ResolveReceiptExecutionMode(contractIdentity);
        var receipt = NewReceipt(
            actor,
            runId,
            Hash($"review-stopped\n{runId}\n{normalizedStatus}\n{normalizedReason}\n{normalizedPhase}"),
            executionMode,
            "ReviewStopped",
            normalizedStatus,
            startedAt,
            contractIdentity);
        CopyCumulative(previous, receipt);
        receipt.ProjectIdsJson = previous?.ProjectIdsJson ?? "[]";
        receipt.FailurePhase = normalizedPhase;
        receipt.StoppedReason = normalizedReason;
        receipt.FinalConvergenceStatus = normalizedStatus;
        receipt.RequestIdentityHash = ReviewRequestIdentityHash(runId, isReReview);
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
        receipt.InitialSnapshotToken = result.SnapshotToken;
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
        receipt.FinalConvergenceStatus = PreserveScheduledDecision(previous, request)
            ? previous!.FinalConvergenceStatus
            : result.ErrorCode == GovernanceBatchErrorCode.None
                ? "ExecutionCompleted"
                : result.ErrorCode.ToString();
        receipt.StoppedReason = result.StoppedReason;
        receipt.AuditIdsJson = JsonSerializer.Serialize(MergeAuditIds(previous, result.AuditIds), JsonOptions);
        receipt.ProjectIdsJson = JsonSerializer.Serialize(
            request.ProjectIds ?? DeserializeStrings(previous?.ProjectIdsJson), JsonOptions);
        receipt.IsReplay = result.IsReplay;
        CumulativeExecutionDelta? cumulativeDelta = result.IsReplay
            ? null
            : new CumulativeExecutionDelta(
                result.AppliedCount,
                result.FailedCount,
                result.QuarantinedCount,
                result.AutoDeletedCount,
                result.TombstonedCount,
                result.SemanticAutoResolvedCount);
        await InsertImmutableAsync(receipt, cancellationToken, cumulativeDelta);
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

    public async Task<GovernanceRunLineageResult> GetScheduledLineageAsync(
        string governanceRunId,
        GovernanceReceiptContractIdentity expectedContractIdentity,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(governanceRunId);
        var events = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.GovernanceRunId == runId)
            .OrderBy(x => x.EventSequence)
            .ThenBy(x => x.CreatedAt)
            .ThenBy(x => x.Id)
            .Take(1_001)
            .ToArrayAsync(cancellationToken);

        if (events.Length == 0)
        {
            return new GovernanceRunLineageResult(
                RunExists: false,
                IsScheduledMode: false,
                ContractMatches: false,
                Status: "NotReceived",
                Reason: "No receipt event exists for this governance run and actor.");
        }

        if (events.Length > 1_000)
        {
            return new GovernanceRunLineageResult(
                RunExists: true,
                IsScheduledMode: false,
                ContractMatches: false,
                Status: "ContractMismatch",
                Reason: "The governance run lineage exceeds the bounded validation window.");
        }

        // The first immutable event binds the run. Every subsequent event must
        // remain in the same scheduled mode and use the exact current catalog
        // identity; a generic review or an older catalog therefore invalidates
        // the scheduled read/execute view instead of being folded into it.
        var first = events[0];
        var firstIsScheduled = string.Equals(first.ExecutionMode, "Scheduled", StringComparison.Ordinal);
        if (!firstIsScheduled || events.Any(x => !string.Equals(x.ExecutionMode, "Scheduled", StringComparison.Ordinal)))
        {
            return new GovernanceRunLineageResult(
                RunExists: true,
                IsScheduledMode: false,
                ContractMatches: false,
                Status: "ModeMismatch",
                Reason: "The governance run lineage contains a non-scheduled receipt event.");
        }

        var contractMatches = events.All(x => ReceiptIdentityMatches(x, expectedContractIdentity));
        if (!contractMatches)
        {
            return new GovernanceRunLineageResult(
                RunExists: true,
                IsScheduledMode: true,
                ContractMatches: false,
                Status: "ContractMismatch",
                Reason: "The governance run lineage is not bound to the current scheduled contract identity.");
        }

        return new GovernanceRunLineageResult(
            RunExists: true,
            IsScheduledMode: true,
            ContractMatches: true,
            Status: "Valid",
            Reason: "The governance run lineage is bound to the current scheduled contract identity.");
    }

    public async Task<IAsyncDisposable> AcquireRunLockAsync(
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var runId = RequireGovernanceRunId(governanceRunId);
        return await AcquireRunLockCoreAsync(
            actor.TenantId!.Value,
            actor.UserId!.Value,
            runId,
            cancellationToken);
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
        var receipt = new GovernanceRunReceipt
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
        if (string.Equals(executionMode, "Scheduled", StringComparison.Ordinal) &&
            contractIdentity is not null &&
            contractIdentity.ToolContractVersion == CurrentScheduledContractIdentity.ToolContractVersion &&
            contractIdentity.SchemaHash == CurrentScheduledContractIdentity.SchemaHash &&
            contractIdentity.PublishedCatalogVersion == CurrentScheduledContractIdentity.PublishedCatalogVersion)
        {
            ApplyScheduledRuntimeIdentity(receipt, ScheduledGovernanceContract.RuntimeIdentity);
        }

        return receipt;
    }

    private static void ApplyScheduledRuntimeIdentity(
        GovernanceRunReceipt receipt,
        ScheduledGovernanceRuntimeIdentity runtimeIdentity)
    {
        receipt.RuntimeEvidenceVersion = ScheduledRuntimeEvidenceVersion;
        receipt.RuntimeServiceName = runtimeIdentity.ServiceName;
        receipt.RuntimeBuildVersion = runtimeIdentity.BuildVersion;
        receipt.RuntimeBuildTimestampUtc = runtimeIdentity.BuildTimestampUtc.ToUniversalTime();
        receipt.RuntimeDerivedIdentity = runtimeIdentity.DerivedIdentity;
        receipt.RuntimeIdentityHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeRuntimeIdentityHash(runtimeIdentity);
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
        var scheduledProjection = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.GovernanceRunId == receipt.GovernanceRunId &&
                        x.ExecutionMode == "Scheduled" &&
                        x.EventType == "ScheduledDecisionProjected")
            .OrderByDescending(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);
        var latestReviewSequence = scheduledProjection is null
            ? null
            : await dbContext.GovernanceRunReceipts.AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId &&
                            x.OwnerUserId == actor.UserId &&
                            x.GovernanceRunId == receipt.GovernanceRunId &&
                            x.EventType == "ReviewCompleted")
                .Select(x => (long?)x.EventSequence)
                .MaxAsync(cancellationToken);
        var successfulScheduledEvent = string.Equals(receipt.EventType, "ScheduledDecisionProjected", StringComparison.Ordinal) ||
                                       (string.Equals(receipt.ExecutionMode, "Scheduled", StringComparison.Ordinal) &&
                                        (string.Equals(receipt.EventType, "BatchReceived", StringComparison.Ordinal) ||
                                         ((string.Equals(receipt.EventType, "BatchCompleted", StringComparison.Ordinal) ||
                                           string.Equals(receipt.EventType, "BatchReplay", StringComparison.Ordinal)) &&
                                          string.Equals(receipt.Status, "Completed", StringComparison.OrdinalIgnoreCase))));
        var useScheduledProjection = scheduledProjection is not null &&
                                      successfulScheduledEvent &&
                                      (!latestReviewSequence.HasValue ||
                                       scheduledProjection.EventSequence > latestReviewSequence.Value);
        var canonical = useScheduledProjection ? scheduledProjection! : receipt;

        return new GovernanceRunReceiptResult(
            receipt.Id, receipt.GovernanceRunId, receipt.Actor, receipt.ExecutionMode,
            receipt.StartedAt, receipt.CompletedAt, receipt.ToolContractVersion, receipt.SchemaHash,
            receipt.PublishedCatalogVersion, canonical.InitialSnapshotToken, canonical.FinalSnapshotToken,
            canonical.CoverageComplete, canonical.InitialGovernanceActionable, canonical.FinalGovernanceActionable,
            canonical.CandidateCount, canonical.ExecutionActionableCount, canonical.GovernedExceptionCount,
            receipt.Applied, receipt.Failed, canonical.Deferred, canonical.RequiresUserDecision,
            canonical.HostBlocked, receipt.Quarantined, receipt.DeleteEligible, receipt.DeleteMatured,
            receipt.AutoDeleted, receipt.DeleteCancelled, receipt.Tombstoned, receipt.SemanticAutoResolved,
            canonical.BusinessWorkItemActionable, canonical.FinalConvergenceStatus, readStoppedReason,
            DeserializeGuids(receipt.AuditIdsJson), DeserializeStrings(receipt.ProjectIdsJson), receipt.IsReplay,
            RunExists: true,
            Status: readStatus,
            LatestBatchReceived: batchReceipt is not null,
            RequestIdentityHash: batchReceipt?.RequestIdentityHash ?? string.Empty,
            LatestBatch: latestBatch)
        {
            ExceptionDelta = new GovernanceExceptionDeltaResult(
                canonical.ExceptionNew,
                canonical.ExceptionResolved,
                canonical.ExceptionUnchanged,
                canonical.ExceptionEscalated),
            GovernedExceptionStates = DeserializeExceptionStates(canonical.GovernedExceptionStatesJson)
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

    private async Task InsertImmutableAsync(
        GovernanceRunReceipt receipt,
        CancellationToken cancellationToken,
        CumulativeExecutionDelta? cumulativeDelta = null)
    {
        await using var runLock = await AcquireRunLockCoreAsync(
            receipt.TenantId,
            receipt.OwnerUserId,
            receipt.GovernanceRunId,
            cancellationToken);
        await MergeLockedCumulativeEvidenceAsync(receipt, cumulativeDelta, cancellationToken);
        await ApplyLockedReviewExceptionDeltaAsync(receipt, cancellationToken);
        await ApplyInitialReviewBaselineAsync(receipt, cancellationToken);
        await EnsureScheduledDecisionBindsLatestReviewAsync(receipt, cancellationToken);
        SetCanonicalReviewEventKey(receipt);
        await EnsureScheduledRunBindingAsync(receipt, cancellationToken);
        await dbContext.GovernanceRunReceipts.AddAsync(receipt, cancellationToken);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (IsUniqueViolation(exception))
        {
            dbContext.Entry(receipt).State = EntityState.Detached;
            var existing = await dbContext.GovernanceRunReceipts.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == receipt.TenantId && x.OwnerUserId == receipt.OwnerUserId &&
                x.GovernanceRunId == receipt.GovernanceRunId && x.EventKey == receipt.EventKey,
                cancellationToken);
            if (existing is null)
            {
                throw;
            }

            if (!CanonicalReceiptPayloadMatches(existing, receipt))
            {
                throw new GovernanceBatchException(
                    GovernanceBatchErrorCode.ReplayPayloadMismatch,
                    "The immutable governance receipt event key collides with a different canonical payload.");
            }
        }
    }

    private static void SetCanonicalReviewEventKey(GovernanceRunReceipt receipt)
    {
        if (receipt.EventType is "ReviewReceived" or "ReviewCompleted" or "ScheduledDecisionProjected")
        {
            receipt.EventKey = Hash(CanonicalReceiptPayload(receipt));
        }
    }

    private static bool CanonicalReceiptPayloadMatches(
        GovernanceRunReceipt existing,
        GovernanceRunReceipt incoming)
        => string.Equals(
            CanonicalReceiptPayload(existing),
            CanonicalReceiptPayload(incoming),
            StringComparison.Ordinal);

    private static string CanonicalReceiptPayload(GovernanceRunReceipt receipt)
    {
        var builder = new StringBuilder("governance-receipt-payload-v2");
        AppendCanonicalField(builder, receipt.TenantId.ToString("D"));
        AppendCanonicalField(builder, receipt.OwnerUserId.ToString("D"));
        AppendCanonicalField(builder, receipt.GovernanceRunId);
        AppendCanonicalField(builder, receipt.Actor);
        AppendCanonicalField(builder, receipt.ExecutionMode);
        AppendCanonicalField(builder, receipt.EventType);
        AppendCanonicalField(builder, receipt.Status);
        AppendCanonicalField(builder, receipt.ToolContractVersion);
        AppendCanonicalField(builder, receipt.SchemaHash);
        AppendCanonicalField(builder, receipt.PublishedCatalogVersion);
        AppendCanonicalField(builder, receipt.RuntimeEvidenceVersion);
        AppendCanonicalField(builder, receipt.RuntimeServiceName);
        AppendCanonicalField(builder, receipt.RuntimeBuildVersion);
        AppendCanonicalField(builder, receipt.RuntimeBuildTimestampUtc);
        AppendCanonicalField(builder, receipt.RuntimeDerivedIdentity);
        AppendCanonicalField(builder, receipt.RuntimeIdentityHash);
        if (receipt.EventType == "ReviewReceived")
        {
            AppendCanonicalField(builder, receipt.RequestIdentityHash);
            return builder.ToString();
        }

        if (receipt.EventType == "BatchReceived")
        {
            AppendCanonicalField(builder, receipt.RequestIdentityHash);
            AppendCanonicalField(builder, receipt.RequestHash);
            AppendCanonicalField(builder, receipt.FinalSnapshotToken);
            return builder.ToString();
        }

        AppendCanonicalField(builder, receipt.InitialSnapshotToken);
        AppendCanonicalField(builder, receipt.FinalSnapshotToken);
        AppendCanonicalField(builder, receipt.CoverageComplete);
        AppendCanonicalField(builder, receipt.AcceptanceEvidenceVersion);
        AppendCanonicalField(builder, receipt.AuthorizedDurableMemoryCount);
        AppendCanonicalField(builder, receipt.CoveredDurableMemoryCount);
        AppendCanonicalField(builder, receipt.ScannedDurableMemoryCount);
        AppendCanonicalField(builder, receipt.TotalDurableMemoryCount);
        AppendCanonicalField(builder, receipt.SharedScopeOccurrences);
        AppendCanonicalField(builder, receipt.UserScopeOccurrences);
        AppendCanonicalField(builder, receipt.UserScopeHandledSeparately);
        AppendCanonicalField(builder, receipt.CountInvariantSatisfied);
        AppendCanonicalField(builder, receipt.InitialGovernanceActionable);
        AppendCanonicalField(builder, receipt.FinalGovernanceActionable);
        AppendCanonicalField(builder, receipt.CandidateCount);
        AppendCanonicalField(builder, receipt.ExecutionActionableCount);
        AppendCanonicalField(builder, receipt.GovernedExceptionCount);
        AppendCanonicalField(builder, receipt.Applied);
        AppendCanonicalField(builder, receipt.Failed);
        AppendCanonicalField(builder, receipt.Deferred);
        AppendCanonicalField(builder, receipt.RequiresUserDecision);
        AppendCanonicalField(builder, receipt.HostBlocked);
        AppendCanonicalField(builder, receipt.ExceptionNew);
        AppendCanonicalField(builder, receipt.ExceptionResolved);
        AppendCanonicalField(builder, receipt.ExceptionUnchanged);
        AppendCanonicalField(builder, receipt.ExceptionEscalated);
        AppendCanonicalField(builder, CanonicalJson(receipt.GovernedExceptionStatesJson));
        AppendCanonicalField(builder, receipt.Quarantined);
        AppendCanonicalField(builder, receipt.DeleteEligible);
        AppendCanonicalField(builder, receipt.DeleteMatured);
        AppendCanonicalField(builder, receipt.AutoDeleted);
        AppendCanonicalField(builder, receipt.DeleteCancelled);
        AppendCanonicalField(builder, receipt.Tombstoned);
        AppendCanonicalField(builder, receipt.SemanticAutoResolved);
        AppendCanonicalField(builder, receipt.BusinessWorkItemActionable);
        AppendCanonicalField(builder, receipt.FinalConvergenceStatus);
        AppendCanonicalField(builder, receipt.StoppedReason);
        AppendCanonicalField(builder, receipt.LatestBatchReceived);
        AppendCanonicalField(builder, receipt.RequestIdentityHash);
        AppendCanonicalField(builder, receipt.RequestHash);
        AppendCanonicalField(builder, receipt.FailurePhase);
        AppendCanonicalField(builder, CanonicalJson(receipt.AuditIdsJson));
        AppendCanonicalField(builder, CanonicalJson(receipt.ProjectIdsJson));
        AppendCanonicalField(builder, receipt.IsReplay);
        return builder.ToString();
    }

    private static void AppendCanonicalField(StringBuilder builder, object? value)
    {
        if (value is null)
        {
            builder.Append("-1:");
            return;
        }

        var text = value switch
        {
            bool boolean => boolean ? "true" : "false",
            IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture) ?? string.Empty,
            _ => value.ToString() ?? string.Empty
        };
        builder.Append(text.Length).Append(':').Append(text);
    }

    private static string CanonicalJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return string.Empty;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return CanonicalJson(document.RootElement);
        }
        catch (JsonException)
        {
            // A malformed JSON payload remains part of the exact replay
            // comparison. It must not become equal to a valid empty payload.
            return $"invalid:{json.Trim()}";
        }
    }

    private static string CanonicalJson(JsonElement element)
        => element.ValueKind switch
        {
            JsonValueKind.Object => "{" + string.Join(
                ',',
                element.EnumerateObject()
                    .OrderBy(property => property.Name, StringComparer.Ordinal)
                    .Select(property =>
                        $"{JsonSerializer.Serialize(property.Name, JsonOptions)}:{CanonicalJson(property.Value)}")) + "}",
            JsonValueKind.Array => "[" + string.Join(',', element.EnumerateArray().Select(CanonicalJson)) + "]",
            JsonValueKind.String => JsonSerializer.Serialize(element.GetString() ?? string.Empty, JsonOptions),
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.Null => "null",
            _ => element.GetRawText()
        };

    private static string ReviewRequestIdentityHash(string governanceRunId, bool isReReview)
        => ScheduledGovernanceReliabilityEvidenceContract.ComputeReviewRequestIdentityHash(
            governanceRunId,
            isReReview);

    private static bool IsUniqueViolation(DbUpdateException exception)
        => exception.InnerException is Npgsql.PostgresException { SqlState: "23505" };

    private async Task EnsureScheduledRunBindingAsync(
        GovernanceRunReceipt receipt,
        CancellationToken cancellationToken)
    {
        var existing = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == receipt.TenantId &&
                        x.OwnerUserId == receipt.OwnerUserId &&
                        x.GovernanceRunId == receipt.GovernanceRunId)
            .OrderBy(x => x.EventSequence)
            .Select(x => new
            {
                x.ExecutionMode,
                x.ToolContractVersion,
                x.SchemaHash,
                x.PublishedCatalogVersion
            })
            .Take(1_001)
            .ToArrayAsync(cancellationToken);

        if (existing.Length > 1_000)
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "The governance run lineage exceeds the bounded validation window.");
        }

        var receiptIsScheduled = string.Equals(receipt.ExecutionMode, "Scheduled", StringComparison.Ordinal);
        var existingContainsScheduled = existing.Any(x =>
            string.Equals(x.ExecutionMode, "Scheduled", StringComparison.Ordinal));
        if (!receiptIsScheduled && !existingContainsScheduled)
        {
            return;
        }

        var matchesScheduledBinding = receiptIsScheduled &&
                                      existing.All(x =>
                                          string.Equals(x.ExecutionMode, "Scheduled", StringComparison.Ordinal) &&
                                          string.Equals(x.ToolContractVersion, receipt.ToolContractVersion, StringComparison.Ordinal) &&
                                          string.Equals(x.SchemaHash, receipt.SchemaHash, StringComparison.OrdinalIgnoreCase) &&
                                          string.Equals(x.PublishedCatalogVersion, receipt.PublishedCatalogVersion, StringComparison.Ordinal));
        if (!matchesScheduledBinding)
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "The governance run is already bound to a different execution mode or contract identity.");
        }
    }

    private async Task<IAsyncDisposable> AcquireRunLockCoreAsync(
        Guid tenantId,
        Guid ownerUserId,
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        var connection = dbContext.Database.GetDbConnection();
        var closeWhenReleased = connection.State == ConnectionState.Closed;
        if (closeWhenReleased)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
        }

        var lockKey = ComputeRunLockKey(tenantId, ownerUserId, governanceRunId);
        try
        {
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_lock(@lock_key);";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "lock_key";
            parameter.Value = lockKey;
            command.Parameters.Add(parameter);
            await command.ExecuteNonQueryAsync(cancellationToken);
            return new AdvisoryRunLock(dbContext, lockKey, closeWhenReleased);
        }
        catch
        {
            if (closeWhenReleased)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
            throw;
        }
    }

    private static long ComputeRunLockKey(Guid tenantId, Guid ownerUserId, string governanceRunId)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{tenantId:N}\n{ownerUserId:N}\n{governanceRunId}"));
        return BinaryPrimitives.ReadInt64BigEndian(digest.AsSpan(0, sizeof(long)));
    }

    private sealed class AdvisoryRunLock(
        MemoryDbContext dbContext,
        long lockKey,
        bool closeWhenReleased) : IAsyncDisposable
    {
        private bool disposed;

        public async ValueTask DisposeAsync()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            try
            {
                await using var command = dbContext.Database.GetDbConnection().CreateCommand();
                command.CommandText = "SELECT pg_advisory_unlock(@lock_key);";
                var parameter = command.CreateParameter();
                parameter.ParameterName = "lock_key";
                parameter.Value = lockKey;
                command.Parameters.Add(parameter);
                await command.ExecuteNonQueryAsync();
            }
            finally
            {
                if (closeWhenReleased)
                {
                    await dbContext.Database.CloseConnectionAsync();
                }
            }
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

    private async Task ApplyInitialReviewBaselineAsync(
        GovernanceRunReceipt receipt,
        CancellationToken cancellationToken)
    {
        var firstCompletedReview = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == receipt.TenantId &&
                        x.OwnerUserId == receipt.OwnerUserId &&
                        x.GovernanceRunId == receipt.GovernanceRunId &&
                        x.EventType == "ReviewCompleted" &&
                        x.Status == "Completed")
            .OrderBy(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);

        if (firstCompletedReview is not null)
        {
            receipt.InitialSnapshotToken = firstCompletedReview.FinalSnapshotToken;
            receipt.InitialGovernanceActionable = firstCompletedReview.FinalGovernanceActionable;
            return;
        }

        if (!string.Equals(receipt.EventType, "ReviewCompleted", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.FinalSnapshotToken))
        {
            receipt.InitialSnapshotToken = string.Empty;
            receipt.InitialGovernanceActionable = 0;
        }
    }

    private async Task MergeLockedCumulativeEvidenceAsync(
        GovernanceRunReceipt receipt,
        CumulativeExecutionDelta? cumulativeDelta,
        CancellationToken cancellationToken)
    {
        var latest = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == receipt.TenantId &&
                        x.OwnerUserId == receipt.OwnerUserId &&
                        x.GovernanceRunId == receipt.GovernanceRunId)
            .OrderByDescending(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (cumulativeDelta is not null)
        {
            var baseline = latest ?? new GovernanceRunReceipt();
            receipt.Applied = checked(baseline.Applied + cumulativeDelta.Value.Applied);
            receipt.Failed = checked(baseline.Failed + cumulativeDelta.Value.Failed);
            receipt.Quarantined = checked(baseline.Quarantined + cumulativeDelta.Value.Quarantined);
            receipt.AutoDeleted = checked(baseline.AutoDeleted + cumulativeDelta.Value.AutoDeleted);
            receipt.Tombstoned = checked(baseline.Tombstoned + cumulativeDelta.Value.Tombstoned);
            receipt.SemanticAutoResolved = checked(
                baseline.SemanticAutoResolved + cumulativeDelta.Value.SemanticAutoResolved);
        }
        else if (latest is not null)
        {
            // Non-execution events and exact replay carry no new cumulative
            // effect, so they retain the greatest already committed totals.
            receipt.Applied = Math.Max(receipt.Applied, latest.Applied);
            receipt.Failed = Math.Max(receipt.Failed, latest.Failed);
            receipt.Quarantined = Math.Max(receipt.Quarantined, latest.Quarantined);
            receipt.AutoDeleted = Math.Max(receipt.AutoDeleted, latest.AutoDeleted);
            receipt.Tombstoned = Math.Max(receipt.Tombstoned, latest.Tombstoned);
            receipt.SemanticAutoResolved = Math.Max(receipt.SemanticAutoResolved, latest.SemanticAutoResolved);
        }

        if (latest is null)
        {
            return;
        }

        // HostBlocked is the current governed-exception projection, not a
        // cumulative execution counter. A blocked execution remains immutable
        // in its event row and is projected through LatestBatch.
        receipt.AuditIdsJson = JsonSerializer.Serialize(
            DeserializeGuids(receipt.AuditIdsJson)
                .Concat(DeserializeGuids(latest.AuditIdsJson))
                .Distinct()
                .Order(),
            JsonOptions);
    }

    private readonly record struct CumulativeExecutionDelta(
        int Applied,
        int Failed,
        int Quarantined,
        int AutoDeleted,
        int Tombstoned,
        int SemanticAutoResolved);

    private async Task ApplyLockedReviewExceptionDeltaAsync(
        GovernanceRunReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(receipt.EventType, "ReviewCompleted", StringComparison.Ordinal))
        {
            return;
        }

        var currentExceptionStates = DeserializeExceptionStates(receipt.GovernedExceptionStatesJson);
        var projectIds = DeserializeStrings(receipt.ProjectIdsJson);
        var contractIdentity = new GovernanceReceiptContractIdentity(
            receipt.ToolContractVersion,
            receipt.SchemaHash,
            receipt.PublishedCatalogVersion);
        var exceptionBaseline = await FindExceptionDeltaBaselineAsync(
            receipt.TenantId,
            receipt.OwnerUserId,
            receipt.GovernanceRunId,
            projectIds,
            contractIdentity,
            cancellationToken);
        var exceptionDelta = ComputeExceptionDelta(
            DeserializeExceptionStates(exceptionBaseline?.GovernedExceptionStatesJson),
            currentExceptionStates);
        receipt.ExceptionNew = exceptionDelta.New;
        receipt.ExceptionResolved = exceptionDelta.Resolved;
        receipt.ExceptionUnchanged = exceptionDelta.Unchanged;
        receipt.ExceptionEscalated = exceptionDelta.Escalated;
    }

    private async Task EnsureScheduledDecisionBindsLatestReviewAsync(
        GovernanceRunReceipt receipt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(receipt.EventType, "ScheduledDecisionProjected", StringComparison.Ordinal))
        {
            return;
        }

        var latestCompletedReview = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == receipt.TenantId &&
                        x.OwnerUserId == receipt.OwnerUserId &&
                        x.GovernanceRunId == receipt.GovernanceRunId &&
                        x.EventType == "ReviewCompleted" &&
                        x.Status == "Completed")
            .OrderByDescending(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestCompletedReview is null ||
            string.IsNullOrWhiteSpace(latestCompletedReview.FinalSnapshotToken) ||
            !string.Equals(
                latestCompletedReview.FinalSnapshotToken,
                receipt.FinalSnapshotToken,
                StringComparison.Ordinal) ||
            !ScheduledCountInvariantMatches(latestCompletedReview, receipt))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.ReReviewRequired,
                "Scheduled governance decision projection requires the latest completed review for the exact snapshot.");
        }
    }

    private static bool ScheduledCountInvariantMatches(
        GovernanceRunReceipt review,
        GovernanceRunReceipt decision)
        => string.Equals(
               review.AcceptanceEvidenceVersion,
               ScheduledAcceptanceEvidenceVersion,
               StringComparison.Ordinal) &&
           string.Equals(
               decision.AcceptanceEvidenceVersion,
               ScheduledAcceptanceEvidenceVersion,
               StringComparison.Ordinal) &&
           review.AuthorizedDurableMemoryCount == decision.AuthorizedDurableMemoryCount &&
           review.CoveredDurableMemoryCount == decision.CoveredDurableMemoryCount &&
           review.ScannedDurableMemoryCount == decision.ScannedDurableMemoryCount &&
           review.TotalDurableMemoryCount == decision.TotalDurableMemoryCount &&
           review.SharedScopeOccurrences == decision.SharedScopeOccurrences &&
           review.UserScopeOccurrences == decision.UserScopeOccurrences &&
           review.UserScopeHandledSeparately == decision.UserScopeHandledSeparately &&
           review.CountInvariantSatisfied == decision.CountInvariantSatisfied;

    private async Task<GovernanceRunReceipt?> FindExceptionDeltaBaselineAsync(
        Guid tenantId,
        Guid ownerUserId,
        string governanceRunId,
        IReadOnlyList<string> projectIds,
        GovernanceReceiptContractIdentity contractIdentity,
        CancellationToken cancellationToken)
    {
        var sameRunReview = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == tenantId &&
                        x.OwnerUserId == ownerUserId &&
                        x.GovernanceRunId == governanceRunId &&
                        x.EventType == "ReviewCompleted")
            .OrderByDescending(x => x.EventSequence)
            .FirstOrDefaultAsync(cancellationToken);
        if (sameRunReview is not null &&
            ReceiptIdentityMatches(sameRunReview, contractIdentity) &&
            ProjectSetsEqual(DeserializeStrings(sameRunReview.ProjectIdsJson), projectIds))
        {
            return sameRunReview;
        }

        // A fresh scheduled run has only a ReviewReceived event, whose copied
        // exception state is empty. Compare with the most recent successful
        // full review for the exact same actor and project scope so unchanged
        // governed exceptions do not become a new human-decision event every
        // four hours. A missing or ambiguous baseline deliberately returns
        // null, preserving the fail-closed "all current exceptions are new"
        // behavior.
        var candidates = await dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == tenantId &&
                        x.OwnerUserId == ownerUserId &&
                        x.GovernanceRunId != governanceRunId &&
                        x.EventType == "ReviewCompleted" &&
                        x.Status == "Completed" &&
                        x.CoverageComplete &&
                        x.ToolContractVersion == contractIdentity.ToolContractVersion &&
                        x.SchemaHash == contractIdentity.SchemaHash &&
                        x.PublishedCatalogVersion == contractIdentity.PublishedCatalogVersion)
            .OrderByDescending(x => x.EventSequence)
            .Take(1_000)
            .ToArrayAsync(cancellationToken);

        return candidates.FirstOrDefault(candidate =>
            ProjectSetsEqual(DeserializeStrings(candidate.ProjectIdsJson), projectIds));
    }

    private static bool ProjectSetsEqual(
        IReadOnlyList<string> left,
        IReadOnlyList<string> right)
        => left.Count == right.Count &&
           left.ToHashSet(StringComparer.OrdinalIgnoreCase)
               .SetEquals(right);

    private static bool ReceiptIdentityMatches(
        GovernanceRunReceipt receipt,
        GovernanceReceiptContractIdentity expected)
        => string.Equals(receipt.ToolContractVersion, expected.ToolContractVersion, StringComparison.Ordinal) &&
           string.Equals(receipt.SchemaHash, expected.SchemaHash, StringComparison.Ordinal) &&
           string.Equals(receipt.PublishedCatalogVersion, expected.PublishedCatalogVersion, StringComparison.Ordinal);

    private static bool ReceiptIdentityMatches(
        GovernanceReceiptContractIdentity? actual,
        GovernanceReceiptContractIdentity expected)
        => actual is not null &&
           string.Equals(actual.ToolContractVersion, expected.ToolContractVersion, StringComparison.Ordinal) &&
           string.Equals(actual.SchemaHash, expected.SchemaHash, StringComparison.Ordinal) &&
           string.Equals(actual.PublishedCatalogVersion, expected.PublishedCatalogVersion, StringComparison.Ordinal);

    private static string ResolveReceiptExecutionMode(GovernanceReceiptContractIdentity? contractIdentity)
        => ReceiptIdentityMatches(contractIdentity, CurrentScheduledContractIdentity)
            ? "Scheduled"
            : "Review";

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

    private static bool PreserveScheduledDecision(
        GovernanceRunReceipt? previous,
        GovernanceBatchExecuteRequest request)
        => previous is not null &&
           request.ExecutionMode == GovernanceBatchExecutionMode.Scheduled &&
           string.Equals(previous.ExecutionMode, "Scheduled", StringComparison.Ordinal) &&
           ReceiptIdentityMatches(previous, CurrentScheduledContractIdentity) &&
           Enum.TryParse<ScheduledGovernanceDecision>(
               previous.FinalConvergenceStatus,
               ignoreCase: false,
               out _);

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

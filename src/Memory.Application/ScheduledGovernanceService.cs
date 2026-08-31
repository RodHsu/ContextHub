using Memory.Domain;

namespace Memory.Application;

public sealed class ScheduledGovernanceService(
    IKnowledgeReviewService knowledgeReview,
    IGovernanceBatchExecutor batchExecutor,
    IGovernanceRunReceiptService receipts,
    IRequestActorAccessor actorAccessor) : IScheduledGovernanceService
{
    private static readonly IReadOnlySet<GovernanceBatchActionType> AllowedActions =
        ScheduledGovernanceContract.FixedReversibleActions.ToHashSet();
    private static readonly GovernanceReceiptContractIdentity ReceiptContractIdentity = new(
        ScheduledGovernanceContract.ToolContractVersion,
        ScheduledGovernanceContract.SchemaHash,
        ScheduledGovernanceContract.PublishedCatalogVersion);

    public async Task<ScheduledGovernanceReviewResult> ReviewAsync(
        ScheduledGovernanceReviewRequest request,
        CancellationToken cancellationToken)
    {
        EnsureScheduledAuthority();
        if (string.IsNullOrWhiteSpace(request.GovernanceRunId))
        {
            throw new InvalidOperationException("GovernanceRunId is required.");
        }

        var review = await knowledgeReview.ReviewAsync(
            new KnowledgeReviewRequest(
                ProjectIds: null,
                LimitPerSection: 200,
                Offset: 0,
                GovernanceRunId: request.GovernanceRunId,
                IsReReview: request.IsReReview)
            {
                ReceiptContractIdentity = ReceiptContractIdentity
            },
            cancellationToken);

        var durableCoverage = review.DurableMemoryCoverage
            ?? throw new InvalidOperationException("Scheduled governance requires durable-memory coverage evidence.");
        var governanceCoverage = review.GovernanceCoverage
            ?? throw new InvalidOperationException("Scheduled governance requires full-surface coverage evidence.");
        var sharedOccurrences = durableCoverage.GovernanceProjectIds.Count(ProjectContext.IsShared);
        var userOccurrences = durableCoverage.GovernanceProjectIds.Count(ProjectContext.IsUser);
        var countInvariant = new ScheduledGovernanceCountInvariant(
            durableCoverage.AuthorizedGovernanceDurableMemoryCount,
            durableCoverage.GovernanceCoveredDurableMemoryCount,
            durableCoverage.ScannedCount,
            durableCoverage.TotalCount,
            sharedOccurrences,
            userOccurrences,
            userOccurrences == 0,
            durableCoverage.CountInvariantSatisfied && sharedOccurrences == 1 && userOccurrences == 0);
        var coverageComplete = countInvariant.Satisfied &&
                               governanceCoverage.CoverageComplete &&
                               !governanceCoverage.HasMore;

        var reversible = review.GovernancePlan.Count(IsAutomationExecutable);
        var humanDecision = review.GovernancePlan.Count(item => !IsAutomationExecutable(item));
        var decision = !coverageComplete
            ? ScheduledGovernanceDecision.CoverageIncomplete
            : reversible > 0
                ? ScheduledGovernanceDecision.ReversibleExecutionRequired
                : humanDecision > 0 || review.GovernedExceptionCount > 0
                    ? ScheduledGovernanceDecision.HumanDecisionOnly
                    : ScheduledGovernanceDecision.NoOpConverged;

        var receipt = await receipts.GetAsync(review.GovernanceRunId, cancellationToken);
        return new ScheduledGovernanceReviewResult(
            review.GovernanceRunId,
            review.IsReReview,
            decision,
            durableCoverage.SnapshotToken,
            countInvariant,
            coverageComplete,
            review.CandidateCount,
            reversible,
            humanDecision,
            review.GovernedExceptionCount,
            review.Convergence.BusinessWorkItemActionableCount,
            durableCoverage.GovernanceProjectIds,
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            humanDecision,
            review.Convergence.RequiresUserDecisionCount,
            review.Convergence.HostBlockedCount,
            review.Convergence.DeferredCount,
            receipt?.ExceptionDelta,
            ScheduledGovernanceContract.RuntimeIdentity);
    }

    public async Task<ScheduledGovernanceExecutionResult> ExecuteAsync(
        ScheduledGovernanceExecuteRequest request,
        CancellationToken cancellationToken)
    {
        EnsureScheduledAuthority();
        ValidateContract(request);

        var batchRequest = new GovernanceBatchExecuteRequest(
            request.GovernanceRunId,
            ProjectIds: null,
            SnapshotToken: request.SnapshotToken,
            Cursor: request.Cursor,
            MaxMutations: Math.Clamp(request.MaxMutations, 1, 100),
            MaxDurationSeconds: Math.Clamp(request.MaxDurationSeconds, 1, 120),
            AllowedActionTypes: ScheduledGovernanceContract.FixedReversibleActions,
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            IsReReview: request.IsReReview,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled,
            AllowMaturedDelete: false,
            SemanticAutoResolutionConfidenceThreshold: 0.90m,
            ToolContractVersion: GovernanceToolContract.ToolContractVersion,
            SchemaHash: GovernanceToolContract.SchemaHash)
        {
            ReceiptContractIdentity = ReceiptContractIdentity
        };
        var result = await batchExecutor.ExecuteAsync(batchRequest, cancellationToken);

        return ToScheduledResult(result);
    }

    public async Task<ScheduledGovernanceRunResult?> GetReceiptAsync(
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        EnsureScheduledAuthority();
        var receipt = await receipts.GetAsync(governanceRunId, cancellationToken);
        return receipt is null ? null : new ScheduledGovernanceRunResult(
            receipt.ReceiptId,
            receipt.GovernanceRunId,
            receipt.StartedAt,
            receipt.CompletedAt,
            receipt.ToolContractVersion,
            receipt.SchemaHash,
            receipt.PublishedCatalogVersion,
            receipt.InitialSnapshotToken,
            receipt.FinalSnapshotToken,
            receipt.CoverageComplete,
            receipt.InitialGovernanceActionable,
            receipt.FinalGovernanceActionable,
            receipt.CandidateCount,
            receipt.ExecutionActionableCount,
            receipt.GovernedExceptionCount,
            receipt.Applied,
            receipt.Failed,
            receipt.Deferred,
            receipt.RequiresUserDecision,
            receipt.Quarantined,
            receipt.SemanticAutoResolved,
            receipt.BusinessWorkItemActionable,
            receipt.FinalConvergenceStatus,
            receipt.StoppedReason,
            receipt.AuditIds,
            receipt.ProjectIds,
            receipt.IsReplay,
            receipt.RunExists,
            receipt.Status,
            receipt.LatestBatchReceived,
            receipt.RequestIdentityHash,
            receipt.ExceptionDelta,
            ScheduledGovernanceContract.RuntimeIdentity);
    }

    public static ScheduledGovernanceExecutionResult ToScheduledResult(GovernanceBatchExecuteResult result)
        => new(
            result.GovernanceRunId,
            result.Succeeded,
            result.ScannedCount,
            result.AttemptedCount,
            result.AppliedCount,
            result.NoOpCount,
            result.FailedCount,
            result.DeferredCount,
            result.RequiresUserDecisionCount,
            result.QuarantinedCount,
            result.SemanticAutoResolvedCount,
            result.RemainingHumanDecisionCount,
            result.NextCursor,
            result.HasMore,
            result.RequiresReReview,
            result.Items.Select(item => new ScheduledGovernanceExecutionItem(
                item.ItemKey,
                item.ItemKind,
                item.ResourceId,
                item.ProjectId,
                item.ActionType?.ToString() ?? string.Empty,
                item.Disposition.ToString(),
                item.Summary,
                item.Error,
                item.Retryable,
                item.CursorDisposition,
                item.AuditIds,
                item.ResourceIds,
                item.IsReplay,
                item.SemanticAutoResolved)).ToArray(),
            result.AuditIds,
            result.SnapshotToken,
            result.StoppedReason,
            result.IsReplay,
            result.ElapsedMilliseconds,
            ToScheduledError(result.ErrorCode),
            ScheduledGovernanceContract.RuntimeIdentity);

    private static ScheduledGovernanceExecutionError ToScheduledError(GovernanceBatchErrorCode error)
        => error switch
        {
            GovernanceBatchErrorCode.None => ScheduledGovernanceExecutionError.None,
            GovernanceBatchErrorCode.ReReviewRequired => ScheduledGovernanceExecutionError.ReReviewRequired,
            GovernanceBatchErrorCode.InvalidCursor => ScheduledGovernanceExecutionError.InvalidCursor,
            GovernanceBatchErrorCode.CursorExpired => ScheduledGovernanceExecutionError.CursorExpired,
            GovernanceBatchErrorCode.CursorActorMismatch => ScheduledGovernanceExecutionError.ActorMismatch,
            GovernanceBatchErrorCode.CursorScopeMismatch => ScheduledGovernanceExecutionError.ScopeMismatch,
            GovernanceBatchErrorCode.CursorPolicyMismatch => ScheduledGovernanceExecutionError.PolicyMismatch,
            GovernanceBatchErrorCode.CursorSnapshotMismatch => ScheduledGovernanceExecutionError.SnapshotMismatch,
            GovernanceBatchErrorCode.ReplayPayloadMismatch => ScheduledGovernanceExecutionError.ReplayMismatch,
            GovernanceBatchErrorCode.SchemaCapabilityMismatch => ScheduledGovernanceExecutionError.ContractMismatch,
            _ => ScheduledGovernanceExecutionError.RestrictedActionUnavailable
        };

    private void EnsureScheduledAuthority()
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.ScheduledGovernance);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("Scheduled governance requires a tenant owner or administrator.");
        }
    }

    private static bool IsAutomationExecutable(GovernanceReviewItem item)
        => item.IsReversible &&
           !item.RequiresExplicitApproval &&
           item.RiskLevel == GovernanceBatchRiskLevel.Low &&
           Enum.TryParse<GovernanceBatchActionType>(item.RecommendedAction, out var action) &&
           AllowedActions.Contains(action);

    private static void ValidateContract(ScheduledGovernanceExecuteRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.GovernanceRunId) || string.IsNullOrWhiteSpace(request.SnapshotToken))
        {
            throw new InvalidOperationException("GovernanceRunId and SnapshotToken are required.");
        }
        if (!string.Equals(request.ToolContractVersion, ScheduledGovernanceContract.ToolContractVersion, StringComparison.Ordinal) ||
            !string.Equals(request.SchemaHash, ScheduledGovernanceContract.SchemaHash, StringComparison.Ordinal))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "Scheduled governance contract version or schema hash does not match the published automation surface.");
        }
        if (request.MaxMutations is < 1 or > 100 || request.MaxDurationSeconds is < 1 or > 120)
        {
            throw new InvalidOperationException("Scheduled execution bounds must be MaxMutations 1..100 and MaxDurationSeconds 1..120.");
        }
    }
}

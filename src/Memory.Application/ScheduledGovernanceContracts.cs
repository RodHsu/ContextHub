namespace Memory.Application;

public enum ScheduledGovernanceDecision
{
    NoOpConverged,
    ReversibleExecutionRequired,
    HumanDecisionOnly,
    CoverageIncomplete
}

public sealed record ScheduledGovernanceReviewRequest(
    string GovernanceRunId,
    bool IsReReview = false);

public sealed record ScheduledGovernanceCountInvariant(
    int AuthorizedDurableMemoryCount,
    int CoveredDurableMemoryCount,
    int ScannedDurableMemoryCount,
    int TotalDurableMemoryCount,
    int SharedScopeOccurrences,
    int UserScopeOccurrences,
    bool UserScopeHandledSeparately,
    bool Satisfied);

public sealed record ScheduledGovernanceReviewResult(
    string GovernanceRunId,
    bool IsReReview,
    ScheduledGovernanceDecision Decision,
    string SnapshotToken,
    ScheduledGovernanceCountInvariant CountInvariant,
    bool CoverageComplete,
    int CandidateCount,
    int ReversibleExecutionCount,
    int HumanDecisionCount,
    int GovernedExceptionCount,
    int BusinessWorkItemActionableCount,
    IReadOnlyList<string> ResolvedProjectIds,
    string ToolContractVersion,
    string SchemaHash,
    string PublishedCatalogVersion,
    int CurrentReviewHumanDecisionCandidateCount = 0,
    int GovernedRequiresUserDecisionExceptionCount = 0,
    int GovernedHostBlockedExceptionCount = 0,
    int GovernedDeferredExceptionCount = 0,
    GovernanceExceptionDeltaResult? ExceptionDelta = null,
    ScheduledGovernanceRuntimeIdentity? RuntimeIdentity = null);

public sealed record ScheduledGovernanceExecuteRequest(
    string GovernanceRunId,
    string SnapshotToken,
    string? Cursor = null,
    int MaxMutations = 100,
    int MaxDurationSeconds = 120,
    bool IsReReview = false,
    string? ToolContractVersion = null,
    string? SchemaHash = null);

public enum ScheduledGovernanceExecutionError
{
    None,
    ReReviewRequired,
    InvalidCursor,
    CursorExpired,
    ActorMismatch,
    ScopeMismatch,
    PolicyMismatch,
    SnapshotMismatch,
    ReplayMismatch,
    ContractMismatch,
    RestrictedActionUnavailable
}

public sealed record ScheduledGovernanceExecutionItem(
    string ItemKey,
    string ItemKind,
    Guid ResourceId,
    string ProjectId,
    string Action,
    string Disposition,
    string Summary,
    string Error,
    bool Retryable,
    string CursorDisposition,
    IReadOnlyList<Guid> AuditIds,
    IReadOnlyList<Guid> ResourceIds,
    bool IsReplay,
    bool SemanticAutoResolved);

public sealed record ScheduledGovernanceExecutionResult(
    string GovernanceRunId,
    bool Succeeded,
    int ScannedCount,
    int AttemptedCount,
    int AppliedCount,
    int NoOpCount,
    int FailedCount,
    int DeferredCount,
    int RequiresUserDecisionCount,
    int QuarantinedCount,
    int SemanticAutoResolvedCount,
    int RemainingHumanDecisionCount,
    string? NextCursor,
    bool HasMore,
    bool RequiresReReview,
    IReadOnlyList<ScheduledGovernanceExecutionItem> Items,
    IReadOnlyList<Guid> AuditIds,
    string SnapshotToken,
    string StoppedReason,
    bool IsReplay,
    long ElapsedMilliseconds,
    ScheduledGovernanceExecutionError ErrorCode,
    ScheduledGovernanceRuntimeIdentity? RuntimeIdentity = null);

public sealed record ScheduledGovernanceRunResult(
    Guid ReceiptId,
    string GovernanceRunId,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string ToolContractVersion,
    string SchemaHash,
    string PublishedCatalogVersion,
    string InitialSnapshotToken,
    string FinalSnapshotToken,
    bool CoverageComplete,
    int InitialGovernanceActionable,
    int FinalGovernanceActionable,
    int CandidateCount,
    int ReversibleExecutionActionableCount,
    int GovernedExceptionCount,
    int Applied,
    int Failed,
    int Deferred,
    int RequiresUserDecision,
    int Quarantined,
    int SemanticAutoResolved,
    int BusinessWorkItemActionable,
    string FinalConvergenceStatus,
    string StoppedReason,
    IReadOnlyList<Guid> AuditIds,
    IReadOnlyList<string> ProjectIds,
    bool IsReplay,
    bool RunExists,
    string Status,
    bool LatestBatchReceived,
    string RequestIdentityHash,
    GovernanceExceptionDeltaResult? ExceptionDelta = null,
    ScheduledGovernanceRuntimeIdentity? RuntimeIdentity = null);

public sealed record ScheduledGovernanceContractResult(
    string ReviewToolName,
    string ExecuteToolName,
    string ReceiptToolName,
    string ToolContractVersion,
    string SchemaHash,
    string PublishedCatalogVersion,
    IReadOnlyList<string> FixedReversibleActions,
    IReadOnlyList<string> Decisions,
    string IrreversibleRetentionOwner,
    ScheduledGovernanceRuntimeIdentity? RuntimeIdentity = null);

public sealed record ScheduledGovernanceRuntimeIdentity(
    string ServiceName,
    string BuildVersion,
    DateTimeOffset BuildTimestampUtc,
    string DerivedIdentity);

public interface IScheduledGovernanceService
{
    Task<ScheduledGovernanceReviewResult> ReviewAsync(
        ScheduledGovernanceReviewRequest request,
        CancellationToken cancellationToken);

    Task<ScheduledGovernanceExecutionResult> ExecuteAsync(
        ScheduledGovernanceExecuteRequest request,
        CancellationToken cancellationToken);

    Task<ScheduledGovernanceRunResult?> GetReceiptAsync(
        string governanceRunId,
        CancellationToken cancellationToken);
}

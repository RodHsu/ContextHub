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
    ScheduledGovernanceRuntimeIdentity? RuntimeIdentity = null)
{
    public int AutomationActionableCount => ReversibleExecutionCount;

    public int RequiresUserDecisionCount => HumanDecisionCount;
}

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
    ScheduledGovernanceRuntimeIdentity? RuntimeIdentity = null)
{
    public bool Received { get; init; } = true;
    public bool Terminal { get; init; } = true;
    public ScheduledGovernanceDecision? Decision { get; init; }
    public string Outcome { get; init; } = string.Empty;
    public ScheduledGovernanceReliabilitySummary? Reliability { get; init; }
}

/// <summary>
/// Read-only reliability-window evidence returned as part of the existing
/// scheduled governance receipt. The summary is server-derived; it is not a
/// claim made by the ChatGPT caller.
/// </summary>
public sealed record ScheduledGovernanceReliabilitySummary(
    int RequiredRuns,
    int ConsecutiveQualifyingRuns,
    bool GatePassed,
    DateTimeOffset? FirstQualifyingAtUtc,
    DateTimeOffset? LatestQualifyingAtUtc,
    string? FirstQualifyingGovernanceRunId,
    string? LatestQualifyingGovernanceRunId,
    IReadOnlyList<ScheduledGovernanceReliabilityRunResult> Runs,
    IReadOnlyList<ScheduledGovernanceReliabilityRunResult> QualifyingRuns,
    IReadOnlyList<ScheduledGovernanceReliabilityRunResult> NonQualifyingRuns,
    IReadOnlyList<ScheduledGovernanceReliabilityRunResult> FailedRuns,
    IReadOnlyList<ScheduledGovernanceReliabilityResetResult> ResetEvents,
    int IgnoredManualRunCount,
    int IgnoredReplayProjectionCount,
    int IgnoredNonScheduledRunCount,
    ScheduledGovernanceReliabilityScheduleResult Schedule,
    TimeSpan? MaximumAbsoluteDrift,
    TimeSpan? LatestSignedDrift,
    ScheduledGovernanceNaturalOriginEvidenceResult NaturalOriginEvidence,
    bool RelevantDeploymentOrConfigurationChangeReset,
    string? LatestResetReason)
{
    public bool ResetOccurred => ResetEvents.Count > 0;

    /// <summary>
    /// ContextHub observes scheduler metadata and cannot change the host
    /// platform timezone configuration.
    /// </summary>
    public bool HostTimeZoneControlAvailable => false;

    public string TimeZoneEvidenceBoundary =>
        "ContextHub observes scheduler timezone metadata; it cannot change the host platform timezone configuration.";
}

public sealed record ScheduledGovernanceReliabilityRunResult(
    string GovernanceRunId,
    Guid ReceiptId,
    string ObservedMode,
    DateTimeOffset ObservedAtUtc,
    DateTimeOffset? ExpectedAtUtc,
    TimeSpan? SignedDrift,
    TimeSpan? AbsoluteDrift,
    bool? DriftWithinTolerance,
    bool CountedTowardGate,
    bool Qualifies,
    bool IsIgnored,
    bool IsFailed,
    IReadOnlyList<string> Reasons,
    string NaturalOriginStatus,
    bool PlatformSignedNaturalOriginAttested,
    string EvidenceBoundary);

public sealed record ScheduledGovernanceReliabilityResetResult(
    DateTimeOffset AtUtc,
    string Reason,
    string? GovernanceRunId,
    int PreviousConsecutiveQualifyingRuns);

public sealed record ScheduledGovernanceReliabilityScheduleResult(
    string IntendedTimeZoneId,
    string SchedulerTimeZoneId,
    TimeSpan Cadence,
    IReadOnlyList<TimeOnly> IntendedLocalRunTimes,
    IReadOnlyList<TimeOnly> SchedulerLocalRunTimes,
    string CompensationDescription);

public sealed record ScheduledGovernanceNaturalOriginEvidenceResult(
    bool PlatformSignedAttestationAvailable,
    string Status,
    string EvidenceBoundary);

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

    Task<ScheduledGovernanceRunResult> GetReceiptAsync(
        string governanceRunId,
        CancellationToken cancellationToken);
}

public interface IScheduledGovernanceReliabilityService
{
    Task<ScheduledGovernanceReliabilitySummary> ObserveAsync(
        GovernanceRunReceiptResult receipt,
        CancellationToken cancellationToken);

    Task<ScheduledGovernanceReliabilitySummary> GetAsync(
        CancellationToken cancellationToken);
}

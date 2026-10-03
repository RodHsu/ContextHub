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
    public GovernanceSurfaceCoverageResult SkillCoverage { get; init; } = new(0, 0, 0, 0, 0, 0, 0, false, true);
    public IReadOnlyDictionary<string, int> SkillSignalCounts { get; init; } = new Dictionary<string, int>();
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
    /// <summary>Current receipt proof projection for hosts that collapse nested reliability schemas.</summary>
    [System.Text.Json.Serialization.JsonPropertyOrder(-2)]
    public IReadOnlyDictionary<string, bool?> ServerInvariants =>
        CurrentRunEvidence?.ServerInvariants ?? new Dictionary<string, bool?>();
    [System.Text.Json.Serialization.JsonPropertyOrder(-1)]
    public IReadOnlyDictionary<string, ScheduledGovernanceInvariantProof> ServerInvariantProofs =>
        CurrentRunEvidence?.ServerInvariantProofs ?? new Dictionary<string, ScheduledGovernanceInvariantProof>();

    // Explicit primitive properties survive hosts that collapse complex output schemas.
    public bool? InitialReviewReceived => ScalarValue("initialReviewReceived");
    public ScheduledGovernanceInvariantProofStatus InitialReviewReceivedStatus => ScalarProof("initialReviewReceived").Status;
    public string InitialReviewReceivedReason => ScalarProof("initialReviewReceived").Reason;
    public string InitialReviewReceivedScope => ScalarProof("initialReviewReceived").Scope;

    public bool? CountInvariantSatisfied => ScalarValue("countInvariantSatisfied");
    public ScheduledGovernanceInvariantProofStatus CountInvariantSatisfiedStatus => ScalarProof("countInvariantSatisfied").Status;
    public string CountInvariantSatisfiedReason => ScalarProof("countInvariantSatisfied").Reason;
    public string CountInvariantSatisfiedScope => ScalarProof("countInvariantSatisfied").Scope;

    public bool? DecisionObeyed => ScalarValue("decisionObeyed");
    public ScheduledGovernanceInvariantProofStatus DecisionObeyedStatus => ScalarProof("decisionObeyed").Status;
    public string DecisionObeyedReason => ScalarProof("decisionObeyed").Reason;
    public string DecisionObeyedScope => ScalarProof("decisionObeyed").Scope;

    public bool? NoGeneralConnectorFallback => ScalarValue("noGeneralConnectorFallback");
    public ScheduledGovernanceInvariantProofStatus NoGeneralConnectorFallbackStatus => ScalarProof("noGeneralConnectorFallback").Status;
    public string NoGeneralConnectorFallbackReason => ScalarProof("noGeneralConnectorFallback").Reason;
    public string NoGeneralConnectorFallbackScope => ScalarProof("noGeneralConnectorFallback").Scope;

    public bool? NoUnauthorizedMutation => ScalarValue("noUnauthorizedMutation");
    public ScheduledGovernanceInvariantProofStatus NoUnauthorizedMutationStatus => ScalarProof("noUnauthorizedMutation").Status;
    public string NoUnauthorizedMutationReason => ScalarProof("noUnauthorizedMutation").Reason;
    public string NoUnauthorizedMutationScope => ScalarProof("noUnauthorizedMutation").Scope;

    public bool? NoDuplicateMutation => ScalarValue("noDuplicateMutation");
    public ScheduledGovernanceInvariantProofStatus NoDuplicateMutationStatus => ScalarProof("noDuplicateMutation").Status;
    public string NoDuplicateMutationReason => ScalarProof("noDuplicateMutation").Reason;
    public string NoDuplicateMutationScope => ScalarProof("noDuplicateMutation").Scope;

    public bool? DisplayNameUnchanged => ScalarValue("displayNameUnchangedByRun");
    public ScheduledGovernanceInvariantProofStatus DisplayNameUnchangedStatus => ScalarProof("displayNameUnchangedByRun").Status;
    public string DisplayNameUnchangedReason => ScalarProof("displayNameUnchangedByRun").Reason;
    public string DisplayNameUnchangedScope => ScalarProof("displayNameUnchangedByRun").Scope;

    public bool? BusinessWorkItemsUntouched => ScalarValue("businessWorkItemsUntouchedByRun");
    public ScheduledGovernanceInvariantProofStatus BusinessWorkItemsUntouchedStatus => ScalarProof("businessWorkItemsUntouchedByRun").Status;
    public string BusinessWorkItemsUntouchedReason => ScalarProof("businessWorkItemsUntouchedByRun").Reason;
    public string BusinessWorkItemsUntouchedScope => ScalarProof("businessWorkItemsUntouchedByRun").Scope;

    public bool? HostDispatchCompleted => ScalarValue("hostDispatchCompleted");
    public ScheduledGovernanceInvariantProofStatus HostDispatchCompletedStatus => ScalarProof("hostDispatchCompleted").Status;
    public string HostDispatchCompletedReason => ScalarProof("hostDispatchCompleted").Reason;
    public string HostDispatchCompletedScope => ScalarProof("hostDispatchCompleted").Scope;

    public bool? ImmutableSnapshotBound => ScalarValue("immutableSnapshotBound");
    public ScheduledGovernanceInvariantProofStatus ImmutableSnapshotBoundStatus => ScalarProof("immutableSnapshotBound").Status;
    public string ImmutableSnapshotBoundReason => ScalarProof("immutableSnapshotBound").Reason;
    public string ImmutableSnapshotBoundScope => ScalarProof("immutableSnapshotBound").Scope;

    public bool? FixedReversibleExecutorUsed => ScalarValue("fixedReversibleExecutorUsed");
    public ScheduledGovernanceInvariantProofStatus FixedReversibleExecutorUsedStatus => ScalarProof("fixedReversibleExecutorUsed").Status;
    public string FixedReversibleExecutorUsedReason => ScalarProof("fixedReversibleExecutorUsed").Reason;
    public string FixedReversibleExecutorUsedScope => ScalarProof("fixedReversibleExecutorUsed").Scope;

    private bool? ScalarValue(string key) =>
        ServerInvariants.TryGetValue(key, out var value) ? value : null;

    private ScheduledGovernanceInvariantProof ScalarProof(string key) =>
        ServerInvariantProofs.TryGetValue(key, out var proof) ? proof : new(
            key == "noGeneralConnectorFallback" ? ScheduledGovernanceInvariantProofStatus.NotObservable :
                ScheduledGovernanceInvariantProofStatus.Unproven,
            key == "noGeneralConnectorFallback" ? "separate-host-connector-calls-not-observable" :
                "current-receipt-proof-unavailable",
            key is "noGeneralConnectorFallback" or "hostDispatchCompleted" ? "Host" : "GovernanceRun");

    // Never borrow evidence from another run, a missing receipt, or rejected lineage.
    private ScheduledGovernanceReliabilityRunResult? CurrentRunEvidence =>
        RunExists && Received && Decision.HasValue && Enum.IsDefined(Decision.Value) &&
        ReceiptId != Guid.Empty && !string.IsNullOrWhiteSpace(GovernanceRunId)
            ? Reliability?.Runs.FirstOrDefault(run => run.ReceiptId == ReceiptId &&
                string.Equals(run.GovernanceRunId, GovernanceRunId, StringComparison.Ordinal))
            : null;
    public GovernanceSurfaceCoverageResult SkillCoverage { get; init; } = new(0, 0, 0, 0, 0, 0, 0, false, true);
    public IReadOnlyDictionary<string, int> SkillSignalCounts { get; init; } = new Dictionary<string, int>();
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
    string EvidenceBoundary)
{
    public string Surface { get; init; } = "General";
    public string DispatchProvenance { get; init; } = "Unproven";
    public bool ProvenanceTrusted { get; init; }
    public string EvidenceKind { get; init; } = "None";
    public string? EvidenceReferenceHash { get; init; }
    public string? NaturalScheduleSlotHash { get; init; }
    public string? ExclusionReason { get; init; }
    public int StreakBefore { get; init; }
    public int StreakAfter { get; init; }
    public string? ResetReason { get; init; }
    public IReadOnlyDictionary<string, bool?> ServerInvariants { get; init; } =
        new Dictionary<string, bool?>();
    public IReadOnlyDictionary<string, ScheduledGovernanceInvariantProof> ServerInvariantProofs { get; init; } =
        new Dictionary<string, ScheduledGovernanceInvariantProof>();
}

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
    string EvidenceBoundary)
{
    public string? ExternalProvenanceBlocker => Status == "Verified"
        ? null
        : "Trusted platform attestation and immutable control-plane audit are unavailable or unverified; natural reliability remains blocked.";
}

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

namespace Memory.Application;

/// <summary>
/// Internal, read-only projection of the evidence needed to count one natural
/// Scheduled Governance run. It deliberately is not part of the MCP or REST
/// contract. Nullable evidence fields mean that the source did not prove the
/// condition; the reliability gate treats that as non-qualifying.
/// </summary>
internal sealed record ScheduledGovernanceReliabilityReceiptProjection
{
    internal const string AuthorityEpochBaselineLabel = "authority-epoch";

    public Guid? TenantId { get; init; }
    public Guid? OwnerUserId { get; init; }
    public Guid ReceiptId { get; init; }
    public long? ReceiptEventSequence { get; init; }
    public string GovernanceRunId { get; init; } = string.Empty;
    public DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset CompletedAt { get; init; }
    public DateTimeOffset? ObservedAtUtc { get; init; }
    public DateTimeOffset? ExpectedAtUtc { get; init; }
    public string ExecutionMode { get; init; } = string.Empty;
    public bool IsReplay { get; init; }
    public bool RunExists { get; init; }
    public bool Terminal { get; init; }
    public string Status { get; init; } = string.Empty;
    public string ToolContractVersion { get; init; } = string.Empty;
    public string SchemaHash { get; init; } = string.Empty;
    public string PublishedCatalogVersion { get; init; } = string.Empty;
    public string RequestIdentityHash { get; init; } = string.Empty;
    public IReadOnlyList<string> ProjectIds { get; init; } = [];
    public ScheduledGovernanceRuntimeIdentity? RuntimeIdentity { get; init; }
    public string? BaselineIdentity { get; init; }
    public string? ResetReason { get; init; }
    public ScheduledGovernanceReliabilityEvidenceSnapshot? NaturalOriginEvidence { get; init; }
    public ScheduledGovernanceDecision? Decision { get; init; }
    public bool? InitialReviewReceived { get; init; }
    public bool CoverageComplete { get; init; }
    public bool? CountInvariantSatisfied { get; init; }
    public bool? DecisionObeyed { get; init; }
    public bool? NoGeneralConnectorFallback { get; init; }
    public bool? NoUnauthorizedMutation { get; init; }
    public bool? NoDuplicateMutation { get; init; }
    public bool? DisplayNameUnchanged { get; init; }
    public bool? BusinessWorkItemsUntouched { get; init; }
    public bool? HostDispatchCompleted { get; init; }
    public bool? ImmutableSnapshotBound { get; init; }
    public bool? FixedReversibleExecutorUsed { get; init; }
    public bool LatestBatchReceived { get; init; }
    public int InitialGovernanceActionable { get; init; }
    public int FinalGovernanceActionable { get; init; }
    public int ExecutionActionableCount { get; init; }
    public int GovernedExceptionCount { get; init; }
    public int Applied { get; init; }
    public int Failed { get; init; }
    public IReadOnlyList<Guid> AuditIds { get; init; } = [];
    public string FinalConvergenceStatus { get; init; } = string.Empty;
    public string StoppedReason { get; init; } = string.Empty;

    public static ScheduledGovernanceReliabilityReceiptProjection FromReceipt(
        GovernanceRunReceiptResult receipt,
        ScheduledGovernanceRuntimeIdentity? runtimeIdentity = null,
        ScheduledGovernanceReliabilityEvidence? evidence = null,
        DateTimeOffset? expectedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(receipt);

        return new ScheduledGovernanceReliabilityReceiptProjection
        {
            ReceiptId = receipt.ReceiptId,
            GovernanceRunId = receipt.GovernanceRunId,
            StartedAt = receipt.StartedAt,
            CompletedAt = receipt.CompletedAt,
            ExpectedAtUtc = expectedAtUtc,
            ObservedAtUtc = receipt.StartedAt,
            ExecutionMode = receipt.ExecutionMode,
            IsReplay = receipt.IsReplay,
            RunExists = receipt.RunExists,
            Terminal = !string.Equals(receipt.Status, "Running", StringComparison.OrdinalIgnoreCase),
            Status = receipt.Status,
            ToolContractVersion = receipt.ToolContractVersion,
            SchemaHash = receipt.SchemaHash,
            PublishedCatalogVersion = receipt.PublishedCatalogVersion,
            RequestIdentityHash = receipt.RequestIdentityHash,
            ProjectIds = receipt.ProjectIds,
            RuntimeIdentity = runtimeIdentity,
            NaturalOriginEvidence = evidence?.NaturalOriginEvidence,
            BaselineIdentity = BuildBaselineIdentity(receipt, runtimeIdentity),
            Decision = ResolveDecision(receipt),
            InitialReviewReceived = evidence?.InitialReviewReceived ??
                !string.IsNullOrWhiteSpace(receipt.InitialSnapshotToken),
            CoverageComplete = receipt.CoverageComplete,
            CountInvariantSatisfied = evidence?.CountInvariantSatisfied,
            DecisionObeyed = evidence?.DecisionObeyed,
            NoGeneralConnectorFallback = evidence?.NoGeneralConnectorFallback,
            NoUnauthorizedMutation = evidence?.NoUnauthorizedMutation,
            NoDuplicateMutation = evidence?.NoDuplicateMutation,
            DisplayNameUnchanged = evidence?.DisplayNameUnchanged,
            BusinessWorkItemsUntouched = evidence?.BusinessWorkItemsUntouched,
            HostDispatchCompleted = evidence?.HostDispatchCompleted,
            ImmutableSnapshotBound = evidence?.ImmutableSnapshotBound,
            FixedReversibleExecutorUsed = evidence?.FixedReversibleExecutorUsed,
            LatestBatchReceived = receipt.LatestBatchReceived,
            InitialGovernanceActionable = receipt.InitialGovernanceActionable,
            FinalGovernanceActionable = receipt.FinalGovernanceActionable,
            ExecutionActionableCount = receipt.ExecutionActionableCount,
            GovernedExceptionCount = receipt.GovernedExceptionCount,
            Applied = receipt.Applied,
            Failed = receipt.Failed,
            AuditIds = receipt.AuditIds,
            FinalConvergenceStatus = receipt.FinalConvergenceStatus,
            StoppedReason = receipt.StoppedReason,
            ResetReason = evidence?.ResetReason
        };
    }

    public static ScheduledGovernanceReliabilityReceiptProjection FromScheduledRun(
        ScheduledGovernanceRunResult run,
        ScheduledGovernanceReliabilityEvidence? evidence = null,
        DateTimeOffset? expectedAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(run);

        return new ScheduledGovernanceReliabilityReceiptProjection
        {
            ReceiptId = run.ReceiptId,
            GovernanceRunId = run.GovernanceRunId,
            StartedAt = run.StartedAt,
            CompletedAt = run.CompletedAt,
            ExpectedAtUtc = expectedAtUtc,
            ObservedAtUtc = run.StartedAt,
            ExecutionMode = "Scheduled",
            IsReplay = run.IsReplay,
            RunExists = run.RunExists,
            Terminal = run.Terminal,
            Status = run.Status,
            ToolContractVersion = run.ToolContractVersion,
            SchemaHash = run.SchemaHash,
            PublishedCatalogVersion = run.PublishedCatalogVersion,
            RequestIdentityHash = string.Empty,
            ProjectIds = run.ProjectIds,
            RuntimeIdentity = run.RuntimeIdentity,
            NaturalOriginEvidence = evidence?.NaturalOriginEvidence,
            BaselineIdentity = BuildBaselineIdentity(run),
            Decision = run.Decision,
            InitialReviewReceived = evidence?.InitialReviewReceived ?? run.Received &&
                !string.IsNullOrWhiteSpace(run.InitialSnapshotToken),
            CoverageComplete = run.CoverageComplete,
            CountInvariantSatisfied = evidence?.CountInvariantSatisfied,
            DecisionObeyed = evidence?.DecisionObeyed,
            NoGeneralConnectorFallback = evidence?.NoGeneralConnectorFallback,
            NoUnauthorizedMutation = evidence?.NoUnauthorizedMutation,
            NoDuplicateMutation = evidence?.NoDuplicateMutation,
            DisplayNameUnchanged = evidence?.DisplayNameUnchanged,
            BusinessWorkItemsUntouched = evidence?.BusinessWorkItemsUntouched,
            HostDispatchCompleted = evidence?.HostDispatchCompleted,
            ImmutableSnapshotBound = evidence?.ImmutableSnapshotBound,
            FixedReversibleExecutorUsed = evidence?.FixedReversibleExecutorUsed,
            LatestBatchReceived = run.LatestBatchReceived,
            InitialGovernanceActionable = run.InitialGovernanceActionable,
            FinalGovernanceActionable = run.FinalGovernanceActionable,
            ExecutionActionableCount = run.ReversibleExecutionActionableCount,
            GovernedExceptionCount = run.GovernedExceptionCount,
            Applied = run.Applied,
            Failed = run.Failed,
            AuditIds = run.AuditIds,
            FinalConvergenceStatus = run.FinalConvergenceStatus,
            StoppedReason = run.StoppedReason,
            ResetReason = evidence?.ResetReason
        };
    }

    internal static string BindAuthorityEpoch(
        string? baselineIdentity,
        string? authorityEpochDigest)
        => string.Join(
            '|',
            baselineIdentity?.Trim() ?? string.Empty,
            AuthorityEpochBaselineLabel,
            authorityEpochDigest?.Trim() ?? string.Empty);

    internal static bool HasAuthorityEpoch(
        string? baselineIdentity,
        string? authorityEpochDigest)
    {
        if (string.IsNullOrWhiteSpace(baselineIdentity) ||
            string.IsNullOrWhiteSpace(authorityEpochDigest))
        {
            return false;
        }

        var segments = baselineIdentity.Split('|');
        return segments.Length >= 3 &&
               string.Equals(segments[^2], AuthorityEpochBaselineLabel, StringComparison.Ordinal) &&
               string.Equals(segments[^1], authorityEpochDigest.Trim(), StringComparison.Ordinal);
    }

    private static string BuildBaselineIdentity(
        GovernanceRunReceiptResult receipt,
        ScheduledGovernanceRuntimeIdentity? runtimeIdentity)
        => string.Join('|',
            receipt.ToolContractVersion,
            receipt.SchemaHash,
            receipt.PublishedCatalogVersion,
            runtimeIdentity?.ServiceName ?? string.Empty,
            runtimeIdentity?.BuildVersion ?? string.Empty,
            runtimeIdentity?.DerivedIdentity ?? string.Empty);

    private static string BuildBaselineIdentity(ScheduledGovernanceRunResult run)
        => string.Join('|',
            run.ToolContractVersion,
            run.SchemaHash,
            run.PublishedCatalogVersion,
            run.RuntimeIdentity?.ServiceName ?? string.Empty,
            run.RuntimeIdentity?.BuildVersion ?? string.Empty,
            run.RuntimeIdentity?.DerivedIdentity ?? string.Empty);

    private static ScheduledGovernanceDecision? ResolveDecision(GovernanceRunReceiptResult receipt)
    {
        if (string.Equals(receipt.Status, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!receipt.CoverageComplete)
        {
            return ScheduledGovernanceDecision.CoverageIncomplete;
        }

        if (receipt.ExecutionActionableCount > 0)
        {
            return ScheduledGovernanceDecision.ReversibleExecutionRequired;
        }

        if (receipt.CandidateCount > 0 || receipt.GovernedExceptionCount > 0)
        {
            return ScheduledGovernanceDecision.HumanDecisionOnly;
        }

        return ScheduledGovernanceDecision.NoOpConverged;
    }
}

/// <summary>
/// Optional safety and lifecycle evidence attached by the caller that owns the
/// corresponding application or runtime observation. A null value is unknown,
/// not an implicit success.
/// </summary>
internal sealed record ScheduledGovernanceReliabilityEvidence
{
    public bool? InitialReviewReceived { get; init; }
    public bool? CountInvariantSatisfied { get; init; }
    public bool? DecisionObeyed { get; init; }
    public bool? NoGeneralConnectorFallback { get; init; }
    public bool? NoUnauthorizedMutation { get; init; }
    public bool? NoDuplicateMutation { get; init; }
    public bool? DisplayNameUnchanged { get; init; }
    public bool? BusinessWorkItemsUntouched { get; init; }
    public bool? HostDispatchCompleted { get; init; }
    public bool? ImmutableSnapshotBound { get; init; }
    public bool? FixedReversibleExecutorUsed { get; init; }
    public string? ResetReason { get; init; }
    public ScheduledGovernanceReliabilityEvidenceSnapshot? NaturalOriginEvidence { get; init; }
}

internal sealed record ScheduledGovernanceReliabilityWindowOptions
{
    public int RequiredRuns { get; init; } = 6;
    public TimeSpan Cadence { get; init; } = TimeSpan.FromHours(4);
    public string IntendedTimeZoneId { get; init; } = "Asia/Taipei";
    public string SchedulerTimeZoneId { get; init; } = "Asia/Tokyo";
    public IReadOnlyList<TimeOnly> IntendedLocalRunTimes { get; init; } =
        [new TimeOnly(0, 0), new TimeOnly(4, 0), new TimeOnly(8, 0), new TimeOnly(12, 0), new TimeOnly(16, 0), new TimeOnly(20, 0)];
    public IReadOnlyList<TimeOnly> SchedulerLocalRunTimes { get; init; } =
        [new TimeOnly(1, 0), new TimeOnly(5, 0), new TimeOnly(9, 0), new TimeOnly(13, 0), new TimeOnly(17, 0), new TimeOnly(21, 0)];
    public TimeSpan MaximumAllowedDrift { get; init; } = TimeSpan.FromMinutes(15);
    public DateTimeOffset? ExpectedFirstRunAtUtc { get; init; }
    public string? ExpectedBaselineIdentity { get; init; }
    public ScheduledGovernanceRuntimeIdentity? ExpectedRuntimeIdentity { get; init; } =
        ScheduledGovernanceContract.RuntimeIdentity;
    public ScheduledGovernanceNaturalOriginAuthority? ExpectedNaturalOriginAuthority { get; init; }

    public static ScheduledGovernanceReliabilityWindowOptions Default { get; } = new();

    public void Validate()
    {
        if (RequiredRuns <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(RequiredRuns), "RequiredRuns must be positive.");
        }

        if (Cadence <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Cadence), "Cadence must be positive.");
        }

        if (MaximumAllowedDrift < TimeSpan.Zero || MaximumAllowedDrift > TimeSpan.FromMinutes(15))
        {
            throw new ArgumentOutOfRangeException(
                nameof(MaximumAllowedDrift),
                "MaximumAllowedDrift must be between zero and 15 minutes.");
        }

        if (string.IsNullOrWhiteSpace(IntendedTimeZoneId))
        {
            throw new ArgumentException("IntendedTimeZoneId is required.", nameof(IntendedTimeZoneId));
        }

        if (string.IsNullOrWhiteSpace(SchedulerTimeZoneId))
        {
            throw new ArgumentException("SchedulerTimeZoneId is required.", nameof(SchedulerTimeZoneId));
        }

        if (IntendedLocalRunTimes is null || IntendedLocalRunTimes.Count == 0)
        {
            throw new ArgumentException("At least one intended local run time is required.", nameof(IntendedLocalRunTimes));
        }

        if (SchedulerLocalRunTimes is null || SchedulerLocalRunTimes.Count == 0)
        {
            throw new ArgumentException("At least one scheduler local run time is required.", nameof(SchedulerLocalRunTimes));
        }

        ExpectedNaturalOriginAuthority?.Validate();
    }
}

internal sealed record ScheduledGovernanceNaturalOriginAuthority(
    string PlatformIssuer,
    string ControlPlaneSourceSystem,
    string Environment,
    string TaskBindingHash,
    string AutomationBindingHash,
    string ScheduleDigest,
    string ConfigurationDigest,
    string AuthorityEpochDigest)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(PlatformIssuer) ||
            string.IsNullOrWhiteSpace(ControlPlaneSourceSystem) ||
            string.IsNullOrWhiteSpace(Environment) ||
            !IsDigest(TaskBindingHash) ||
            !IsDigest(AutomationBindingHash) ||
            !IsDigest(ScheduleDigest) ||
            !IsDigest(ConfigurationDigest) ||
            !IsDigest(AuthorityEpochDigest))
        {
            throw new ArgumentException("Natural-origin authority is incomplete or malformed.");
        }
    }

    private static bool IsDigest(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');
}

internal sealed record ScheduledGovernanceReliabilityScheduleIntent(
    string IntendedTimeZoneId,
    string SchedulerTimeZoneId,
    TimeSpan Cadence,
    IReadOnlyList<TimeOnly> IntendedLocalRunTimes,
    IReadOnlyList<TimeOnly> SchedulerLocalRunTimes,
    string CompensationDescription);

internal sealed record ScheduledGovernanceReliabilityRunEvidence(
    string GovernanceRunId,
    Guid ReceiptId,
    string ExecutionMode,
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
    string NaturalOriginStatus = "Unattested",
    bool PlatformSignedNaturalOriginAttested = false);

internal sealed record ScheduledGovernanceReliabilityResetEvent(
    DateTimeOffset AtUtc,
    string Reason,
    string? GovernanceRunId,
    int PreviousConsecutiveQualifyingRuns);

internal sealed record ScheduledGovernanceReliabilityWindowResult(
    int RequiredRuns,
    int ConsecutiveQualifyingRuns,
    bool GatePassed,
    DateTimeOffset? FirstQualifyingAtUtc,
    DateTimeOffset? LatestQualifyingAtUtc,
    string? FirstQualifyingGovernanceRunId,
    string? LatestQualifyingGovernanceRunId,
    IReadOnlyList<ScheduledGovernanceReliabilityRunEvidence> Runs,
    IReadOnlyList<ScheduledGovernanceReliabilityRunEvidence> QualifyingRuns,
    IReadOnlyList<ScheduledGovernanceReliabilityRunEvidence> NonQualifyingRuns,
    IReadOnlyList<ScheduledGovernanceReliabilityRunEvidence> FailedRuns,
    IReadOnlyList<ScheduledGovernanceReliabilityResetEvent> ResetEvents,
    int IgnoredManualRunCount,
    int IgnoredReplayProjectionCount,
    int IgnoredNonScheduledRunCount,
    ScheduledGovernanceReliabilityScheduleIntent Schedule,
    TimeSpan? MaximumAbsoluteDrift,
    TimeSpan? LatestSignedDrift)
{
    public int QualifyingConsecutiveRuns => ConsecutiveQualifyingRuns;
    public ScheduledGovernanceReliabilityResetEvent? LastResetEvent => ResetEvents.LastOrDefault();
    public bool ResetOccurred => ResetEvents.Count > 0;
}

internal interface IScheduledGovernanceReliabilityWindowCalculator
{
    ScheduledGovernanceReliabilityWindowResult Calculate(
        IEnumerable<ScheduledGovernanceReliabilityReceiptProjection> receipts);
}

internal sealed class ScheduledGovernanceReliabilityWindowCalculator :
    IScheduledGovernanceReliabilityWindowCalculator
{
    private readonly ScheduledGovernanceReliabilityWindowOptions _options;

    public ScheduledGovernanceReliabilityWindowCalculator(
        ScheduledGovernanceReliabilityWindowOptions? options = null)
    {
        _options = options ?? ScheduledGovernanceReliabilityWindowOptions.Default;
        _options.Validate();
    }

    public ScheduledGovernanceReliabilityWindowResult Calculate(
        IEnumerable<GovernanceRunReceiptResult> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);
        return Calculate(receipts.Select(receipt =>
            ScheduledGovernanceReliabilityReceiptProjection.FromReceipt(receipt)));
    }

    public ScheduledGovernanceReliabilityWindowResult Calculate(
        IEnumerable<ScheduledGovernanceRunResult> runs)
    {
        ArgumentNullException.ThrowIfNull(runs);
        return Calculate(runs.Select(run =>
            ScheduledGovernanceReliabilityReceiptProjection.FromScheduledRun(run)));
    }

    public ScheduledGovernanceReliabilityWindowResult Calculate(
        IEnumerable<ScheduledGovernanceReliabilityReceiptProjection> receipts)
    {
        ArgumentNullException.ThrowIfNull(receipts);

        var source = receipts.ToArray();
        var canonical = Canonicalize(source);
        var ordered = canonical
            .OrderBy(x => ObservedAt(x.Projection))
            .ThenBy(x => x.Projection.GovernanceRunId, StringComparer.Ordinal)
            .ThenBy(x => x.Projection.ReceiptId)
            .ToArray();
        var runEvidence = new List<ScheduledGovernanceReliabilityRunEvidence>(ordered.Length);
        var qualifyingRuns = new List<ScheduledGovernanceReliabilityRunEvidence>();
        var nonQualifyingRuns = new List<ScheduledGovernanceReliabilityRunEvidence>();
        var failedRuns = new List<ScheduledGovernanceReliabilityRunEvidence>();
        var resetEvents = new List<ScheduledGovernanceReliabilityResetEvent>();
        var currentStreak = new List<ScheduledGovernanceReliabilityRunEvidence>();
        string? previousBaselineIdentity = null;
        DateTimeOffset? previousExpectedAt = null;
        var maximumAbsoluteDrift = (TimeSpan?)null;
        var latestSignedDrift = (TimeSpan?)null;

        foreach (var item in ordered)
        {
            var projection = item.Projection;
            var observedAt = ObservedAt(projection);
            if (!IsScheduled(projection.ExecutionMode))
            {
                var ignoredReasons = IsManual(projection.ExecutionMode)
                    ? new[] { "manual-execution-ignored" }
                    : new[] { "non-scheduled-execution-ignored" };
                runEvidence.Add(new ScheduledGovernanceReliabilityRunEvidence(
                    projection.GovernanceRunId.Trim(),
                    projection.ReceiptId,
                    projection.ExecutionMode,
                    observedAt,
                    null,
                    null,
                    null,
                    null,
                    false,
                    false,
                    true,
                    IsFailed(projection),
                    ignoredReasons));
                continue;
            }

            var reasons = new List<string>();
            var naturalOrigin = EvaluateNaturalOrigin(projection, observedAt, reasons);
            var expectedAt = ResolveExpectedAt(projection, observedAt, naturalOrigin.ExpectedAtUtc);
            var signedDrift = expectedAt.HasValue
                ? (TimeSpan?)(observedAt - expectedAt.Value)
                : null;
            var absoluteDrift = signedDrift.HasValue
                ? (TimeSpan?)Absolute(signedDrift.Value)
                : null;
            var driftWithinTolerance = absoluteDrift.HasValue
                ? (bool?)(absoluteDrift.Value <= _options.MaximumAllowedDrift)
                : null;
            if (absoluteDrift.HasValue &&
                (!maximumAbsoluteDrift.HasValue || absoluteDrift.Value > maximumAbsoluteDrift.Value))
            {
                maximumAbsoluteDrift = absoluteDrift;
            }

            latestSignedDrift = signedDrift;
            EvaluateProjection(item, expectedAt, driftWithinTolerance, reasons);
            if (expectedAt.HasValue && _options.ExpectedFirstRunAtUtc.HasValue &&
                expectedAt.Value < _options.ExpectedFirstRunAtUtc.Value)
            {
                reasons.Add("scheduled-run-before-reliability-window");
            }

            if (expectedAt.HasValue && previousExpectedAt.HasValue)
            {
                var slotDelta = expectedAt.Value - previousExpectedAt.Value;
                if (slotDelta <= TimeSpan.Zero)
                {
                    reasons.Add("duplicate-or-out-of-order-schedule-slot");
                }
                else if (slotDelta != _options.Cadence)
                {
                    reasons.Add("scheduled-cadence-gap");
                }
            }
            var baselineIdentity = EffectiveBaselineIdentity(projection);
            var baselineChanged = !string.IsNullOrWhiteSpace(previousBaselineIdentity) &&
                                  !string.IsNullOrWhiteSpace(baselineIdentity) &&
                                  !string.Equals(previousBaselineIdentity, baselineIdentity, StringComparison.Ordinal);
            if (baselineChanged && !reasons.Contains("reliability-baseline-changed", StringComparer.Ordinal))
            {
                reasons.Add("reliability-baseline-changed");
            }

            if (!string.IsNullOrWhiteSpace(projection.ResetReason))
            {
                reasons.Add("explicit-reset-event");
            }

            if (expectedAt is null)
            {
                reasons.Add("expected-execution-time-unavailable");
            }

            var qualifies = reasons.Count == 0;
            var failed = IsFailed(projection);
            var evidence = new ScheduledGovernanceReliabilityRunEvidence(
                projection.GovernanceRunId.Trim(),
                projection.ReceiptId,
                projection.ExecutionMode,
                observedAt,
                expectedAt,
                signedDrift,
                absoluteDrift,
                driftWithinTolerance,
                true,
                qualifies,
                false,
                failed,
                reasons.Distinct(StringComparer.Ordinal).ToArray(),
                naturalOrigin.Status,
                naturalOrigin.PlatformSignedAttestationVerified);
            runEvidence.Add(evidence);
            if (qualifies)
            {
                qualifyingRuns.Add(evidence);
                currentStreak.Add(evidence);
                previousBaselineIdentity = baselineIdentity ?? previousBaselineIdentity;
                previousExpectedAt = expectedAt;
                continue;
            }

            nonQualifyingRuns.Add(evidence);
            if (failed)
            {
                failedRuns.Add(evidence);
            }

            var resetReason = ResolveResetReason(projection, baselineChanged, evidence.Reasons);
            resetEvents.Add(new ScheduledGovernanceReliabilityResetEvent(
                observedAt,
                resetReason,
                string.IsNullOrWhiteSpace(projection.GovernanceRunId) ? null : projection.GovernanceRunId.Trim(),
                currentStreak.Count));
            currentStreak.Clear();
            previousBaselineIdentity = baselineIdentity ?? previousBaselineIdentity;
            if (expectedAt.HasValue && (!previousExpectedAt.HasValue || expectedAt.Value > previousExpectedAt.Value))
            {
                previousExpectedAt = expectedAt;
            }
        }

        var schedule = new ScheduledGovernanceReliabilityScheduleIntent(
            _options.IntendedTimeZoneId,
            _options.SchedulerTimeZoneId,
            _options.Cadence,
            _options.IntendedLocalRunTimes,
            _options.SchedulerLocalRunTimes,
            BuildCompensationDescription(_options));
        var firstCurrent = currentStreak.FirstOrDefault();
        var latestCurrent = currentStreak.LastOrDefault();
        return new ScheduledGovernanceReliabilityWindowResult(
            _options.RequiredRuns,
            currentStreak.Count,
            currentStreak.Count >= _options.RequiredRuns,
            firstCurrent?.ObservedAtUtc,
            latestCurrent?.ObservedAtUtc,
            firstCurrent?.GovernanceRunId,
            latestCurrent?.GovernanceRunId,
            runEvidence,
            qualifyingRuns,
            nonQualifyingRuns,
            failedRuns,
            resetEvents,
            source.Count(x => !x.IsReplay && IsManual(x.ExecutionMode)),
            source.Count(x => x.IsReplay),
            source.Count(x => !x.IsReplay && !IsScheduled(x.ExecutionMode) && !IsManual(x.ExecutionMode)),
            schedule,
            maximumAbsoluteDrift,
            latestSignedDrift);
    }

    private void EvaluateProjection(
        CanonicalProjection item,
        DateTimeOffset? expectedAt,
        bool? driftWithinTolerance,
        ICollection<string> reasons)
    {
        var projection = item.Projection;
        if (string.IsNullOrWhiteSpace(projection.GovernanceRunId))
        {
            reasons.Add("missing-governance-run-id");
        }

        if (item.DuplicateNonReplayProjection)
        {
            reasons.Add("duplicate-non-replay-governance-run-id");
        }

        if (!projection.RunExists)
        {
            reasons.Add("receipt-not-found");
        }

        if (!projection.Terminal)
        {
            reasons.Add("receipt-not-terminal");
        }

        if (!string.Equals(projection.Status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            reasons.Add("receipt-status-not-completed");
        }

        RequireTrue(projection.InitialReviewReceived, "initial-review-not-proven", reasons);
        if (!projection.CoverageComplete)
        {
            reasons.Add("coverage-incomplete");
        }

        RequireTrue(projection.CountInvariantSatisfied, "count-invariant-not-proven", reasons);
        if (projection.Decision is null)
        {
            reasons.Add("decision-not-proven");
        }
        else if (projection.Decision == ScheduledGovernanceDecision.CoverageIncomplete)
        {
            reasons.Add("decision-coverage-incomplete");
        }
        else if (projection.Decision == ScheduledGovernanceDecision.HumanDecisionOnly)
        {
            reasons.Add("decision-human-authority-required");
        }

        RequireTrue(projection.DecisionObeyed, "decision-obedience-not-proven", reasons);
        RequireContractIdentity(projection, reasons);
        RequireRuntimeIdentity(projection, reasons);
        RequireTrue(projection.NoGeneralConnectorFallback, "general-connector-fallback-not-proven", reasons);
        RequireTrue(projection.NoUnauthorizedMutation, "unauthorized-mutation-not-proven", reasons);
        RequireTrue(projection.NoDuplicateMutation, "duplicate-mutation-safety-not-proven", reasons);
        RequireTrue(projection.DisplayNameUnchanged, "display-name-unchanged-not-proven", reasons);
        RequireTrue(projection.BusinessWorkItemsUntouched, "business-work-item-safety-not-proven", reasons);
        RequireTrue(projection.HostDispatchCompleted, "host-dispatch-not-proven", reasons);

        if (projection.Decision == ScheduledGovernanceDecision.ReversibleExecutionRequired ||
            projection.LatestBatchReceived ||
            projection.Applied > 0)
        {
            RequireTrue(projection.ImmutableSnapshotBound, "immutable-snapshot-not-proven", reasons);
            RequireTrue(projection.FixedReversibleExecutorUsed, "fixed-reversible-executor-not-proven", reasons);
        }

        if (projection.Failed > 0)
        {
            reasons.Add("failed-count-nonzero");
        }

        if (IsFailed(projection))
        {
            reasons.Add("receipt-failed-or-stopped");
        }

        if (string.IsNullOrWhiteSpace(projection.FinalConvergenceStatus))
        {
            reasons.Add("final-outcome-not-proven");
        }
        else if (ContainsFailureMarker(projection.FinalConvergenceStatus) ||
                 ContainsFailureMarker(projection.StoppedReason))
        {
            reasons.Add("final-outcome-failed-or-stopped");
        }

        if (!string.IsNullOrWhiteSpace(_options.ExpectedBaselineIdentity) &&
            !string.Equals(
                _options.ExpectedBaselineIdentity,
                EffectiveBaselineIdentity(projection),
                StringComparison.Ordinal))
        {
            reasons.Add("reliability-baseline-mismatch");
        }

        if (expectedAt is not null && driftWithinTolerance is false)
        {
            reasons.Add("observed-schedule-drift-exceeds-tolerance");
        }

    }

    private NaturalOriginEvaluation EvaluateNaturalOrigin(
        ScheduledGovernanceReliabilityReceiptProjection projection,
        DateTimeOffset observedAt,
        ICollection<string> reasons)
    {
        var evidence = projection.NaturalOriginEvidence;
        if (evidence is null)
        {
            reasons.Add(ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);
            return NaturalOriginEvaluation.Unattested;
        }

        var naturalOriginReasonCount = reasons.Count;
        var attestation = evidence.PlatformAttestation;
        var audit = evidence.ControlPlaneAudit;
        var attestationVerified = attestation is not null &&
                                   attestation.VerificationStatus ==
                                   ScheduledGovernanceEvidenceVerificationStatus.Verified &&
                                   attestation.SignatureValid &&
                                   attestation.ReplaySafe;
        var auditVerified = audit is not null &&
                             audit.VerificationStatus ==
                             ScheduledGovernanceEvidenceVerificationStatus.Verified &&
                             audit.SourceAuthenticated &&
                             audit.ImmutableEvent &&
                             audit.ReplaySafe;

        if (!attestationVerified)
        {
            reasons.Add("platform-attestation-not-verified");
        }

        if (!auditVerified)
        {
            reasons.Add("control-plane-audit-not-verified");
        }

        var expectedAt = attestation?.Binding.ExpectedAtUtc ?? audit?.Binding.ExpectedAtUtc;
        if (expectedAt is null)
        {
            reasons.Add("signed-schedule-slot-not-proven");
        }

        if (attestation is not null)
        {
            if (string.IsNullOrWhiteSpace(attestation.Issuer) ||
                string.IsNullOrWhiteSpace(attestation.Environment) ||
                string.IsNullOrWhiteSpace(attestation.KeyId))
            {
                reasons.Add("platform-attestation-key-binding-not-proven");
            }

            var issuedAt = attestation.IssuedAtUtc.ToUniversalTime();
            var expiresAt = attestation.ExpiresAtUtc.ToUniversalTime();
            var lifetime = expiresAt - issuedAt;
            if (issuedAt > expiresAt ||
                lifetime <= TimeSpan.Zero ||
                lifetime > ScheduledGovernanceReliabilityEvidenceContract.MaximumAttestationLifetime ||
                observedAt.ToUniversalTime() < issuedAt ||
                observedAt.ToUniversalTime() > expiresAt)
            {
                reasons.Add("platform-attestation-freshness-invalid");
            }
        }

        if (audit is not null &&
            (string.IsNullOrWhiteSpace(audit.SourceSystem) ||
             string.IsNullOrWhiteSpace(audit.AuditEventHash) ||
             audit.Sequence < 0))
        {
            reasons.Add("control-plane-audit-identity-not-proven");
        }

        if (attestation is null || audit is null)
        {
            reasons.Add(ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);
        }
        else
        {
            RequireExactBinding(projection, attestation, audit, reasons);
        }

        var verified = attestationVerified &&
                       auditVerified &&
                       reasons.Count == naturalOriginReasonCount;
        if (!verified)
        {
            if (!reasons.Contains(
                    ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason,
                    StringComparer.Ordinal))
            {
                reasons.Add(ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);
            }

            return new NaturalOriginEvaluation(
                expectedAt,
                "Unattested",
                attestationVerified);
        }

        return new NaturalOriginEvaluation(expectedAt, "Verified", true);
    }

    private void RequireExactBinding(
        ScheduledGovernanceReliabilityReceiptProjection projection,
        ScheduledGovernancePlatformAttestationEvidence attestationEvidence,
        ScheduledGovernanceControlPlaneAuditEvidence auditEvidence,
        ICollection<string> reasons)
    {
        var attestation = attestationEvidence.Binding;
        var audit = auditEvidence.Binding;
        if (projection.TenantId is null || projection.OwnerUserId is null ||
            attestation.TenantId != projection.TenantId.Value ||
            audit.TenantId != projection.TenantId.Value ||
            attestation.OwnerUserId != projection.OwnerUserId.Value ||
            audit.OwnerUserId != projection.OwnerUserId.Value)
        {
            reasons.Add("natural-origin-actor-binding-mismatch");
        }
        else
        {
            var actorBindingHash = ScheduledGovernanceReliabilityEvidenceContract
                .ComputeActorBindingHash(projection.TenantId.Value, projection.OwnerUserId.Value);
            if (!string.Equals(attestation.ActorBindingHash, actorBindingHash, StringComparison.Ordinal) ||
                !string.Equals(audit.ActorBindingHash, actorBindingHash, StringComparison.Ordinal))
            {
                reasons.Add("natural-origin-actor-binding-mismatch");
            }
        }

        if (!BindingIdentityMatches(attestation, audit))
        {
            reasons.Add("natural-origin-evidence-binding-mismatch");
        }

        var receiptScopeHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeProjectScopeHash(projection.ProjectIds);
        if (string.IsNullOrWhiteSpace(receiptScopeHash) ||
            !string.Equals(attestation.ProjectScopeHash, receiptScopeHash, StringComparison.Ordinal) ||
            !string.Equals(audit.ProjectScopeHash, receiptScopeHash, StringComparison.Ordinal))
        {
            reasons.Add("natural-origin-project-scope-mismatch");
        }

        if (attestation.ReceiptId != projection.ReceiptId ||
            audit.ReceiptId != projection.ReceiptId ||
            string.IsNullOrWhiteSpace(projection.RequestIdentityHash) ||
            !string.Equals(
                attestation.RequestIdentityHash,
                projection.RequestIdentityHash,
                StringComparison.Ordinal) ||
            !string.Equals(
                audit.RequestIdentityHash,
                projection.RequestIdentityHash,
                StringComparison.Ordinal))
        {
            reasons.Add("natural-origin-request-correlation-mismatch");
        }

        if (attestation.TriggerType != ScheduledGovernanceReliabilityEvidenceContract.NaturalScheduleTrigger ||
            audit.TriggerType != ScheduledGovernanceReliabilityEvidenceContract.NaturalScheduleTrigger)
        {
            reasons.Add("natural-origin-trigger-not-natural");
        }

        if (attestation.ResourceAudience != ScheduledGovernanceReliabilityEvidenceContract.ResourceAudience ||
            audit.ResourceAudience != ScheduledGovernanceReliabilityEvidenceContract.ResourceAudience)
        {
            reasons.Add("natural-origin-audience-mismatch");
        }

        if (!BindingContractIdentityMatches(projection, attestation) ||
            !BindingContractIdentityMatches(projection, audit))
        {
            reasons.Add("natural-origin-contract-runtime-mismatch");
        }

        if (attestation.ExpectedAtUtc is null || audit.ExpectedAtUtc is null ||
            attestation.ExpectedAtUtc.Value.ToUniversalTime() != audit.ExpectedAtUtc.Value.ToUniversalTime() ||
            !string.Equals(attestation.ScheduleSlotId, audit.ScheduleSlotId, StringComparison.Ordinal))
        {
            reasons.Add("natural-origin-slot-binding-mismatch");
        }

        if (string.IsNullOrWhiteSpace(attestation.DispatchIdentityHash) ||
            !string.Equals(attestation.DispatchIdentityHash, audit.DispatchIdentityHash, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attestation.ActorBindingHash) ||
            !string.Equals(attestation.ActorBindingHash, audit.ActorBindingHash, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attestation.TaskBindingHash) ||
            !string.Equals(attestation.TaskBindingHash, audit.TaskBindingHash, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attestation.AutomationBindingHash) ||
            !string.Equals(attestation.AutomationBindingHash, audit.AutomationBindingHash, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attestation.ScheduleDigest) ||
            !string.Equals(attestation.ScheduleDigest, audit.ScheduleDigest, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(attestation.ConfigurationDigest) ||
            !string.Equals(attestation.ConfigurationDigest, audit.ConfigurationDigest, StringComparison.Ordinal))
        {
            reasons.Add("natural-origin-dispatch-correlation-mismatch");
        }

        var authority = _options.ExpectedNaturalOriginAuthority;
        if (authority is null)
        {
            reasons.Add("natural-origin-authority-baseline-not-proven");
        }
        else if (!string.Equals(attestationEvidence.Issuer, authority.PlatformIssuer, StringComparison.Ordinal) ||
                 !string.Equals(auditEvidence.SourceSystem, authority.ControlPlaneSourceSystem, StringComparison.Ordinal) ||
                 !string.Equals(attestation.Environment, authority.Environment, StringComparison.Ordinal) ||
                 !string.Equals(audit.Environment, authority.Environment, StringComparison.Ordinal) ||
                 !string.Equals(attestation.TaskBindingHash, authority.TaskBindingHash, StringComparison.Ordinal) ||
                 !string.Equals(audit.TaskBindingHash, authority.TaskBindingHash, StringComparison.Ordinal) ||
                 !string.Equals(attestation.AutomationBindingHash, authority.AutomationBindingHash, StringComparison.Ordinal) ||
                 !string.Equals(audit.AutomationBindingHash, authority.AutomationBindingHash, StringComparison.Ordinal) ||
                 !string.Equals(attestation.ScheduleDigest, authority.ScheduleDigest, StringComparison.Ordinal) ||
                 !string.Equals(audit.ScheduleDigest, authority.ScheduleDigest, StringComparison.Ordinal) ||
                 !string.Equals(attestation.ConfigurationDigest, authority.ConfigurationDigest, StringComparison.Ordinal) ||
                 !string.Equals(audit.ConfigurationDigest, authority.ConfigurationDigest, StringComparison.Ordinal) ||
                 !ScheduledGovernanceReliabilityReceiptProjection.HasAuthorityEpoch(
                     projection.BaselineIdentity,
                     authority.AuthorityEpochDigest))
        {
            reasons.Add("natural-origin-authority-baseline-mismatch");
        }
    }

    private static bool BindingIdentityMatches(
        ScheduledGovernanceReliabilityEvidenceBinding left,
        ScheduledGovernanceReliabilityEvidenceBinding right)
        => left.TenantId == right.TenantId &&
           left.OwnerUserId == right.OwnerUserId &&
           string.Equals(left.GovernanceRunId, right.GovernanceRunId, StringComparison.Ordinal) &&
           left.ReceiptId == right.ReceiptId &&
           string.Equals(left.ProjectScopeHash, right.ProjectScopeHash, StringComparison.Ordinal) &&
           string.Equals(left.Environment, right.Environment, StringComparison.Ordinal) &&
           left.ExpectedAtUtc?.ToUniversalTime() == right.ExpectedAtUtc?.ToUniversalTime() &&
           string.Equals(left.ScheduleSlotId, right.ScheduleSlotId, StringComparison.Ordinal) &&
           string.Equals(left.TriggerType, right.TriggerType, StringComparison.Ordinal) &&
           string.Equals(left.ResourceAudience, right.ResourceAudience, StringComparison.Ordinal) &&
           string.Equals(left.RequestIdentityHash, right.RequestIdentityHash, StringComparison.Ordinal) &&
           string.Equals(left.DispatchIdentityHash, right.DispatchIdentityHash, StringComparison.Ordinal) &&
           string.Equals(left.ActorBindingHash, right.ActorBindingHash, StringComparison.Ordinal) &&
           string.Equals(left.TaskBindingHash, right.TaskBindingHash, StringComparison.Ordinal) &&
           string.Equals(left.AutomationBindingHash, right.AutomationBindingHash, StringComparison.Ordinal) &&
           string.Equals(left.ScheduleDigest, right.ScheduleDigest, StringComparison.Ordinal) &&
           string.Equals(left.ConfigurationDigest, right.ConfigurationDigest, StringComparison.Ordinal) &&
           string.Equals(left.ToolContractVersion, right.ToolContractVersion, StringComparison.Ordinal) &&
           string.Equals(left.SchemaHash, right.SchemaHash, StringComparison.Ordinal) &&
           string.Equals(left.PublishedCatalogVersion, right.PublishedCatalogVersion, StringComparison.Ordinal) &&
           string.Equals(left.RuntimeIdentityHash, right.RuntimeIdentityHash, StringComparison.Ordinal);

    private static bool BindingContractIdentityMatches(
        ScheduledGovernanceReliabilityReceiptProjection projection,
        ScheduledGovernanceReliabilityEvidenceBinding binding)
        => string.Equals(binding.GovernanceRunId, projection.GovernanceRunId, StringComparison.Ordinal) &&
           string.Equals(binding.ToolContractVersion, projection.ToolContractVersion, StringComparison.Ordinal) &&
           string.Equals(binding.SchemaHash, projection.SchemaHash, StringComparison.Ordinal) &&
           string.Equals(binding.PublishedCatalogVersion, projection.PublishedCatalogVersion, StringComparison.Ordinal) &&
           string.Equals(
               binding.RuntimeIdentityHash,
               ScheduledGovernanceReliabilityEvidenceContract.ComputeRuntimeIdentityHash(projection.RuntimeIdentity),
               StringComparison.Ordinal);

    private void RequireContractIdentity(
        ScheduledGovernanceReliabilityReceiptProjection projection,
        ICollection<string> reasons)
    {
        if (!string.Equals(projection.ToolContractVersion, ScheduledGovernanceContract.ToolContractVersion, StringComparison.Ordinal))
        {
            reasons.Add("contract-version-mismatch");
        }

        if (!string.Equals(projection.SchemaHash, ScheduledGovernanceContract.SchemaHash, StringComparison.Ordinal))
        {
            reasons.Add("schema-hash-mismatch");
        }

        if (!string.Equals(projection.PublishedCatalogVersion, ScheduledGovernanceContract.PublishedCatalogVersion, StringComparison.Ordinal))
        {
            reasons.Add("catalog-version-mismatch");
        }
    }

    private void RequireRuntimeIdentity(
        ScheduledGovernanceReliabilityReceiptProjection projection,
        ICollection<string> reasons)
    {
        if (projection.RuntimeIdentity is null)
        {
            reasons.Add("runtime-identity-not-proven");
            return;
        }

        var expected = _options.ExpectedRuntimeIdentity;
        if (expected is not null && !RuntimeIdentityMatches(expected, projection.RuntimeIdentity))
        {
            reasons.Add("runtime-identity-mismatch");
        }
    }

    private static string? EffectiveBaselineIdentity(
        ScheduledGovernanceReliabilityReceiptProjection projection)
        => string.IsNullOrWhiteSpace(projection.BaselineIdentity)
            ? null
            : projection.BaselineIdentity.Trim();

    private static IReadOnlyList<CanonicalProjection> Canonicalize(
        IReadOnlyList<ScheduledGovernanceReliabilityReceiptProjection> source)
    {
        var canonical = new List<CanonicalProjection>();
        foreach (var group in source
                     .Where(x => !string.IsNullOrWhiteSpace(x.GovernanceRunId))
                     .GroupBy(x => x.GovernanceRunId.Trim(), StringComparer.Ordinal))
        {
            var nonReplay = group
                .Where(x => !x.IsReplay)
                .OrderByDescending(ObservedAt)
                .ThenByDescending(x => x.CompletedAt)
                .ThenByDescending(x => x.ReceiptId)
                .ToArray();
            if (nonReplay.Length == 0)
            {
                continue;
            }

            canonical.Add(new CanonicalProjection(
                nonReplay[0],
                group.Count(x => x.IsReplay),
                nonReplay.Length > 1));
        }

        foreach (var invalid in source.Where(x => string.IsNullOrWhiteSpace(x.GovernanceRunId) && !x.IsReplay))
        {
            canonical.Add(new CanonicalProjection(invalid, 0, false));
        }

        return canonical;
    }

    private static DateTimeOffset ObservedAt(
        ScheduledGovernanceReliabilityReceiptProjection projection)
        => projection.ObservedAtUtc ??
           (projection.StartedAt != default ? projection.StartedAt : projection.CompletedAt);

    private static bool IsScheduled(string? executionMode)
        => string.Equals(executionMode?.Trim(), "Scheduled", StringComparison.OrdinalIgnoreCase);

    private static bool IsManual(string? executionMode)
        => string.Equals(executionMode?.Trim(), "Manual", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(executionMode?.Trim(), "Interactive", StringComparison.OrdinalIgnoreCase);

    private static bool IsFailed(ScheduledGovernanceReliabilityReceiptProjection projection)
        => string.Equals(projection.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(projection.Status, "Stopped", StringComparison.OrdinalIgnoreCase) ||
           ContainsFailureMarker(projection.FinalConvergenceStatus) ||
           ContainsFailureMarker(projection.StoppedReason);

    private static bool ContainsFailureMarker(string? value)
        => !string.IsNullOrWhiteSpace(value) &&
           (value.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("stopped", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("blocked", StringComparison.OrdinalIgnoreCase));

    private static void RequireTrue(bool? value, string reason, ICollection<string> reasons)
    {
        if (value != true)
        {
            reasons.Add(reason);
        }
    }

    private DateTimeOffset? ResolveExpectedAt(
        ScheduledGovernanceReliabilityReceiptProjection projection,
        DateTimeOffset observedAt,
        DateTimeOffset? attestedExpectedAt)
    {
        if (attestedExpectedAt.HasValue)
        {
            return attestedExpectedAt.Value.ToUniversalTime();
        }

        if (projection.ExpectedAtUtc.HasValue)
        {
            return projection.ExpectedAtUtc.Value.ToUniversalTime();
        }

        try
        {
            var timeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.IntendedTimeZoneId);
            var localObserved = TimeZoneInfo.ConvertTime(observedAt, timeZone);
            var localDate = DateOnly.FromDateTime(localObserved.DateTime);
            var candidates = new List<DateTimeOffset>(_options.IntendedLocalRunTimes.Count * 3);
            for (var dayOffset = -1; dayOffset <= 1; dayOffset++)
            {
                var date = localDate.AddDays(dayOffset);
                foreach (var time in _options.IntendedLocalRunTimes)
                {
                    var local = date.ToDateTime(time, DateTimeKind.Unspecified);
                    if (timeZone.IsInvalidTime(local))
                    {
                        continue;
                    }

                    candidates.Add(new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(local, timeZone), TimeSpan.Zero));
                }
            }

            return candidates
                .OrderBy(candidate => Absolute(observedAt.ToUniversalTime() - candidate))
                .ThenBy(candidate => candidate)
                .Select(candidate => (DateTimeOffset?)candidate)
                .FirstOrDefault();
        }
        catch (TimeZoneNotFoundException)
        {
            return null;
        }
        catch (InvalidTimeZoneException)
        {
            return null;
        }
    }

    private static TimeSpan Absolute(TimeSpan value)
        => value == TimeSpan.MinValue
            ? TimeSpan.MaxValue
            : value < TimeSpan.Zero ? value.Negate() : value;

    private static bool RuntimeIdentityMatches(
        ScheduledGovernanceRuntimeIdentity? expected,
        ScheduledGovernanceRuntimeIdentity? actual)
        => expected is not null && actual is not null &&
           string.Equals(expected.ServiceName, actual.ServiceName, StringComparison.Ordinal) &&
           string.Equals(expected.BuildVersion, actual.BuildVersion, StringComparison.Ordinal) &&
           string.Equals(expected.DerivedIdentity, actual.DerivedIdentity, StringComparison.Ordinal);

    private static string ResolveResetReason(
        ScheduledGovernanceReliabilityReceiptProjection projection,
        bool baselineChanged,
        IReadOnlyList<string> reasons)
    {
        if (!string.IsNullOrWhiteSpace(projection.ResetReason))
        {
            return projection.ResetReason.Trim();
        }

        if (baselineChanged || reasons.Contains("reliability-baseline-mismatch", StringComparer.Ordinal) ||
            reasons.Contains("contract-version-mismatch", StringComparer.Ordinal) ||
            reasons.Contains("schema-hash-mismatch", StringComparer.Ordinal) ||
            reasons.Contains("catalog-version-mismatch", StringComparer.Ordinal) ||
            reasons.Contains("runtime-identity-mismatch", StringComparer.Ordinal))
        {
            return "relevant-deployment-or-configuration-change";
        }

        return reasons.FirstOrDefault() ?? "non-qualifying-scheduled-run";
    }

    private static string BuildCompensationDescription(
        ScheduledGovernanceReliabilityWindowOptions options)
        => $"scheduler {options.SchedulerTimeZoneId} " +
           $"{string.Join('/', options.SchedulerLocalRunTimes.Select(x => x.ToString("HH:mm")))} " +
           $"=> intended {options.IntendedTimeZoneId} " +
           string.Join('/', options.IntendedLocalRunTimes.Select(x => x.ToString("HH:mm")));

    private sealed record NaturalOriginEvaluation(
        DateTimeOffset? ExpectedAtUtc,
        string Status,
        bool PlatformSignedAttestationVerified)
    {
        public static NaturalOriginEvaluation Unattested { get; } = new(null, "Unattested", false);
    }

    private sealed record CanonicalProjection(
        ScheduledGovernanceReliabilityReceiptProjection Projection,
        int ReplayProjectionCount,
        bool DuplicateNonReplayProjection);
}

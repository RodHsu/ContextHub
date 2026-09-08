using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class ScheduledGovernanceReliabilityServiceTests
{
    private static readonly DateTimeOffset FirstRun = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Scheduled_Mode_Without_Platform_Attestation_Must_Not_Qualify_Even_With_Other_Evidence()
    {
        var observedProjection = ScheduledGovernanceReliabilityService.BuildProjection(
            CreateReceipt("scheduled-1", "Review"));
        observedProjection.RuntimeIdentity.Should().Be(ScheduledGovernanceContract.RuntimeIdentity);
        observedProjection.BaselineIdentity.Should()
            .Contain(ScheduledGovernanceContract.RuntimeIdentity.BuildVersion);

        var projection = observedProjection with
        {
            BaselineIdentity = "scheduled-1|baseline",
            InitialReviewReceived = true,
            CountInvariantSatisfied = true,
            DecisionObeyed = true,
            NoGeneralConnectorFallback = true,
            NoUnauthorizedMutation = true,
            NoDuplicateMutation = true,
            DisplayNameUnchanged = true,
            BusinessWorkItemsUntouched = true,
            HostDispatchCompleted = true,
            ImmutableSnapshotBound = true,
            FixedReversibleExecutorUsed = true
        };

        var result = new ScheduledGovernanceReliabilityWindowCalculator(
            new ScheduledGovernanceReliabilityWindowOptions { ExpectedFirstRunAtUtc = FirstRun })
            .Calculate([projection]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        projection.ExecutionMode.Should().Be("Scheduled");
        result.GatePassed.Should().BeFalse();
        result.NonQualifyingRuns.Should().ContainSingle();
        result.NonQualifyingRuns[0].Reasons.Should().Contain("explicit-reset-event");
        result.LastResetEvent!.Reason.Should()
            .Be(ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);
    }

    [Fact]
    public void Later_receipt_phase_must_keep_the_first_captured_identity()
    {
        var capturedIdentity = new ScheduledGovernanceRuntimeIdentity(
            "Memory.ScheduledGovernanceGateway",
            "old-deployment",
            FirstRun.AddMinutes(-5),
            "old-derived-identity");
        var persisted = QualifyingProjection(0) with
        {
            RuntimeIdentity = capturedIdentity,
            BaselineIdentity = "old-contract|old-schema|old-catalog|old-service|old-deployment|old-derived-identity"
        };
        var laterPhase = persisted with
        {
            ReceiptId = Guid.NewGuid(),
            RuntimeIdentity = ScheduledGovernanceContract.RuntimeIdentity,
            BaselineIdentity = "current-contract|current-schema|current-catalog|current-service|current-deployment|current-derived-identity",
            Status = "Completed",
            FinalConvergenceStatus = "ExecutionCompleted",
            Applied = 1
        };

        var merged = ScheduledGovernanceReliabilityService.PreserveCapturedIdentity(
            persisted,
            laterPhase);

        merged.RuntimeIdentity.Should().Be(capturedIdentity);
        merged.BaselineIdentity.Should().Be(persisted.BaselineIdentity);
        merged.ReceiptId.Should().Be(laterPhase.ReceiptId);
        merged.FinalConvergenceStatus.Should().Be("ExecutionCompleted");
        merged.Applied.Should().Be(1);
    }

    [Fact]
    public void Governance_run_id_normalization_must_trim_and_bound_input()
    {
        ScheduledGovernanceReliabilityService.NormalizeRunId("  run-1  ")
            .Should().Be("run-1");

        var tooLong = new string('r', ScheduledGovernanceReliabilityService.MaxGovernanceRunIdLength + 1);
        var action = () => ScheduledGovernanceReliabilityService.NormalizeRunId(tooLong);

        action.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Manual_Invocation_Must_Be_Ignored_And_Cannot_Increase_Natural_Streak()
    {
        var projections = Enumerable.Range(0, 3)
            .Select(QualifyingProjection)
            .Append(ScheduledGovernanceReliabilityService.BuildProjection(
                CreateReceipt("manual-1", "Manual", FirstRun.AddHours(2))))
            .ToArray();

        var result = new ScheduledGovernanceReliabilityWindowCalculator(
            new ScheduledGovernanceReliabilityWindowOptions { ExpectedFirstRunAtUtc = FirstRun })
            .Calculate(projections);

        result.ConsecutiveQualifyingRuns.Should().Be(3);
        result.IgnoredManualRunCount.Should().Be(1);
        result.NonQualifyingRuns.Should().BeEmpty();
        result.ResetEvents.Should().BeEmpty();
        result.Runs.Single(x => x.GovernanceRunId == "manual-1").IsIgnored.Should().BeTrue();
        result.Runs.Single(x => x.GovernanceRunId == "manual-1").CountedTowardGate.Should().BeFalse();
    }

    private static ScheduledGovernanceReliabilityReceiptProjection QualifyingProjection(int index)
        => new()
        {
            ReceiptId = Guid.NewGuid(),
            GovernanceRunId = $"scheduled-{index}",
            StartedAt = FirstRun.AddHours(index * 4),
            CompletedAt = FirstRun.AddHours(index * 4).AddMinutes(1),
            ObservedAtUtc = FirstRun.AddHours(index * 4),
            ExecutionMode = "Scheduled",
            RunExists = true,
            Terminal = true,
            Status = "Completed",
            ToolContractVersion = ScheduledGovernanceContract.ToolContractVersion,
            SchemaHash = ScheduledGovernanceContract.SchemaHash,
            PublishedCatalogVersion = ScheduledGovernanceContract.PublishedCatalogVersion,
            RuntimeIdentity = ScheduledGovernanceContract.RuntimeIdentity,
            BaselineIdentity = "stable-baseline",
            Decision = ScheduledGovernanceDecision.NoOpConverged,
            InitialReviewReceived = true,
            CoverageComplete = true,
            CountInvariantSatisfied = true,
            DecisionObeyed = true,
            NoGeneralConnectorFallback = true,
            NoUnauthorizedMutation = true,
            NoDuplicateMutation = true,
            DisplayNameUnchanged = true,
            BusinessWorkItemsUntouched = true,
            HostDispatchCompleted = true,
            ImmutableSnapshotBound = true,
            FixedReversibleExecutorUsed = true,
            FinalConvergenceStatus = "NoOpConverged",
            StoppedReason = "ReviewCompleted"
        };

    private static GovernanceRunReceiptResult CreateReceipt(
        string runId,
        string executionMode = "Scheduled",
        DateTimeOffset? startedAt = null)
    {
        var started = startedAt ?? FirstRun;
        return new(
            Guid.NewGuid(),
            runId,
            "scheduled-governance",
            executionMode,
            started,
            started.AddMinutes(1),
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            "snapshot-initial",
            "snapshot-final",
            true,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "NoOpConverged",
            "ReviewCompleted",
            [],
            [],
            false,
            true,
            "Completed",
            false,
            string.Empty,
            null);
    }
}

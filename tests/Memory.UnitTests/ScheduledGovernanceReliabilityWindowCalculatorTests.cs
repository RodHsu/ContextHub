using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class ScheduledGovernanceReliabilityWindowCalculatorTests
{
    private static readonly DateTimeOffset FirstRun = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Default_Window_Should_Expose_The_Six_Run_Taiwan_Schedule()
    {
        var result = Calculate([]);

        result.RequiredRuns.Should().Be(6);
        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.GatePassed.Should().BeFalse();
        result.Schedule.IntendedTimeZoneId.Should().Be("Asia/Taipei");
        result.Schedule.SchedulerTimeZoneId.Should().Be("Asia/Tokyo");
        result.Schedule.Cadence.Should().Be(TimeSpan.FromHours(4));
        result.Schedule.IntendedLocalRunTimes.Select(x => x.ToString("HH:mm"))
            .Should().Equal("00:00", "04:00", "08:00", "12:00", "16:00", "20:00");
        result.Schedule.SchedulerLocalRunTimes.Select(x => x.ToString("HH:mm"))
            .Should().Equal("01:00", "05:00", "09:00", "13:00", "17:00", "21:00");
        result.Schedule.CompensationDescription.Should()
            .Be("scheduler Asia/Tokyo 01:00/05:00/09:00/13:00/17:00/21:00 => intended Asia/Taipei 00:00/04:00/08:00/12:00/16:00/20:00");
    }

    [Theory]
    [InlineData(2026, 1, 1)]
    [InlineData(2026, 9, 1)]
    public void Default_Scheduler_Compensation_Should_Map_To_The_Intended_Utc_Slots(
        int year,
        int month,
        int day)
    {
        var options = ScheduledGovernanceReliabilityWindowOptions.Default;
        var schedulerZone = TimeZoneInfo.FindSystemTimeZoneById(options.SchedulerTimeZoneId);
        var intendedZone = TimeZoneInfo.FindSystemTimeZoneById(options.IntendedTimeZoneId);
        var date = new DateOnly(year, month, day);

        var schedulerUtc = options.SchedulerLocalRunTimes
            .Select(time => TimeZoneInfo.ConvertTimeToUtc(
                date.ToDateTime(time, DateTimeKind.Unspecified),
                schedulerZone))
            .Order()
            .ToArray();
        var intendedUtc = options.IntendedLocalRunTimes
            .Select(time => TimeZoneInfo.ConvertTimeToUtc(
                date.ToDateTime(time, DateTimeKind.Unspecified),
                intendedZone))
            .Order()
            .ToArray();

        schedulerUtc.Should().Equal(intendedUtc);
    }

    [Fact]
    public void Six_Consecutive_Qualifying_Natural_Runs_Should_Pass_And_Report_First_And_Latest()
    {
        var result = Calculate(Enumerable.Range(0, 6)
            .Select(index => QualifyingRun(index)));

        result.GatePassed.Should().BeTrue();
        result.ConsecutiveQualifyingRuns.Should().Be(6);
        result.QualifyingRuns.Should().HaveCount(6);
        result.FirstQualifyingAtUtc.Should().Be(FirstRun);
        result.LatestQualifyingAtUtc.Should().Be(FirstRun.AddHours(20));
        result.FirstQualifyingGovernanceRunId.Should().Be("run-0");
        result.LatestQualifyingGovernanceRunId.Should().Be("run-5");
        result.NonQualifyingRuns.Should().BeEmpty();
        result.ResetEvents.Should().BeEmpty();
        result.Runs.Should().OnlyContain(x => x.Qualifies && x.CountedTowardGate);
        result.Runs.Select(x => x.SignedDrift).Should().OnlyContain(x => x == TimeSpan.Zero);
    }

    [Fact]
    public void Manual_Runs_Should_Be_Ignored_Without_Resetting_The_Natural_Streak()
    {
        var receipts = Enumerable.Range(0, 3)
            .Select(index => QualifyingRun(index))
            .Append(QualifyingRun(3) with
            {
                GovernanceRunId = "manual-run",
                ExecutionMode = "Manual",
                ObservedAtUtc = FirstRun.AddHours(10)
            })
            .Concat(Enumerable.Range(3, 3).Select(index => QualifyingRun(index)))
            .ToArray();

        var result = Calculate(receipts);

        result.GatePassed.Should().BeTrue();
        result.ConsecutiveQualifyingRuns.Should().Be(6);
        result.IgnoredManualRunCount.Should().Be(1);
        result.NonQualifyingRuns.Should().BeEmpty();
        result.ResetEvents.Should().BeEmpty();
        result.Runs.Single(x => x.GovernanceRunId == "manual-run").IsIgnored.Should().BeTrue();
    }

    [Fact]
    public void Replay_Projection_Should_Be_Idempotent_And_Not_Count_As_A_Run()
    {
        var original = QualifyingRun(0);
        var result = Calculate(new[]
        {
            original,
            original with { ReceiptId = Guid.NewGuid(), IsReplay = true },
            QualifyingRun(1),
            QualifyingRun(2)
        });

        result.ConsecutiveQualifyingRuns.Should().Be(3);
        result.QualifyingRuns.Should().HaveCount(3);
        result.IgnoredReplayProjectionCount.Should().Be(1);
        result.Runs.Should().HaveCount(3);
        result.ResetEvents.Should().BeEmpty();
    }

    [Fact]
    public void Failed_Natural_Run_Should_Record_Reason_And_Reset_The_Streak()
    {
        var failed = QualifyingRun(3) with
        {
            Status = "Failed",
            FinalConvergenceStatus = "Failed",
            StoppedReason = "KnowledgeReviewException"
        };
        var result = Calculate(Enumerable.Range(0, 3)
            .Select(index => QualifyingRun(index))
            .Append(failed)
            .Append(QualifyingRun(4)));

        result.GatePassed.Should().BeFalse();
        result.ConsecutiveQualifyingRuns.Should().Be(1);
        result.NonQualifyingRuns.Should().ContainSingle();
        result.FailedRuns.Should().ContainSingle();
        result.NonQualifyingRuns[0].Reasons.Should().Contain("receipt-status-not-completed");
        result.NonQualifyingRuns[0].Reasons.Should().Contain("receipt-failed-or-stopped");
        result.LastResetEvent.Should().NotBeNull();
        result.LastResetEvent!.GovernanceRunId.Should().Be("run-3");
        result.LastResetEvent.PreviousConsecutiveQualifyingRuns.Should().Be(3);
    }

    [Fact]
    public void Contract_Change_Should_Be_A_NonQualifying_Run_And_Explicit_Reset_Event()
    {
        var changed = QualifyingRun(3) with
        {
            ToolContractVersion = "1.1"
        };
        var result = Calculate(Enumerable.Range(0, 3)
            .Select(index => QualifyingRun(index))
            .Append(changed)
            .Append(QualifyingRun(4)));

        result.ConsecutiveQualifyingRuns.Should().Be(1);
        result.NonQualifyingRuns.Should().ContainSingle();
        result.NonQualifyingRuns[0].Reasons.Should().Contain("contract-version-mismatch");
        result.ResetEvents.Should().ContainSingle(x =>
            x.Reason == "relevant-deployment-or-configuration-change");
    }

    [Fact]
    public void Excessive_Expected_Versus_Observed_Drift_Should_Not_Qualify()
    {
        var options = new ScheduledGovernanceReliabilityWindowOptions
        {
            ExpectedFirstRunAtUtc = FirstRun,
            MaximumAllowedDrift = TimeSpan.FromMinutes(15)
        };
        var result = new ScheduledGovernanceReliabilityWindowCalculator(options)
            .Calculate(new[]
            {
                QualifyingRun(0),
                QualifyingRun(1) with { ObservedAtUtc = FirstRun.AddHours(5) },
                QualifyingRun(2)
            });

        result.ConsecutiveQualifyingRuns.Should().Be(1);
        result.NonQualifyingRuns.Should().ContainSingle();
        result.NonQualifyingRuns[0].Reasons.Should()
            .Contain("observed-schedule-drift-exceeds-tolerance");
        result.NonQualifyingRuns[0].AbsoluteDrift.Should().Be(TimeSpan.FromHours(1));
        result.MaximumAbsoluteDrift.Should().Be(TimeSpan.FromHours(1));
        result.LatestSignedDrift.Should().Be(TimeSpan.Zero);
    }

    [Fact]
    public void Duplicate_NonReplay_Projection_For_One_Run_Should_Fail_Closed()
    {
        var original = QualifyingRun(0);
        var result = Calculate(new[]
        {
            original,
            original with { ReceiptId = Guid.NewGuid(), CompletedAt = original.CompletedAt.AddSeconds(1) }
        });

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle();
        result.NonQualifyingRuns[0].Reasons.Should()
            .Contain("duplicate-non-replay-governance-run-id");
    }

    [Fact]
    public void Missing_Safety_Evidence_From_An_Existing_Receipt_Should_Not_Count()
    {
        var receipt = ReceiptFrom(QualifyingRun(0));
        var result = new ScheduledGovernanceReliabilityWindowCalculator()
            .Calculate(new[] { receipt });

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle();
        result.NonQualifyingRuns[0].Reasons.Should().Contain("decision-obedience-not-proven");
        result.NonQualifyingRuns[0].Reasons.Should().Contain("count-invariant-not-proven");
        result.NonQualifyingRuns[0].Reasons.Should().Contain("runtime-identity-not-proven");
        result.NonQualifyingRuns[0].Reasons.Should().Contain("display-name-unchanged-not-proven");
    }

    [Fact]
    public void Explicit_Receipt_Evidence_Should_Allow_Existing_Receipt_Adapter_To_Qualify()
    {
        var projection = QualifyingRun(0);
        var receipt = ReceiptFrom(projection);
        var evidence = new ScheduledGovernanceReliabilityEvidence
        {
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
        var adapted = ScheduledGovernanceReliabilityReceiptProjection.FromReceipt(
            receipt,
            ScheduledGovernanceContract.RuntimeIdentity,
            evidence);
        var result = new ScheduledGovernanceReliabilityWindowCalculator()
            .Calculate(new[] { adapted });

        result.ConsecutiveQualifyingRuns.Should().Be(1);
        result.GatePassed.Should().BeFalse();
        result.NonQualifyingRuns.Should().BeEmpty();
    }

    [Fact]
    public void Missing_Natural_Slot_Should_Reset_And_Allow_A_New_Streak_From_The_Next_Slot()
    {
        var result = Calculate(new[]
        {
            QualifyingRun(0),
            QualifyingRun(2),
            QualifyingRun(3)
        });

        result.ConsecutiveQualifyingRuns.Should().Be(1);
        result.NonQualifyingRuns.Should().ContainSingle(x =>
            x.GovernanceRunId == "run-2" && x.Reasons.Contains("scheduled-cadence-gap"));
        result.LastResetEvent.Should().NotBeNull();
        result.LastResetEvent!.PreviousConsecutiveQualifyingRuns.Should().Be(1);
        result.LatestQualifyingGovernanceRunId.Should().Be("run-3");
    }

    [Fact]
    public void Two_Different_Runs_In_The_Same_Natural_Slot_Should_Fail_Closed()
    {
        var duplicateSlot = QualifyingRun(1) with
        {
            GovernanceRunId = "different-run-same-slot",
            ReceiptId = Guid.NewGuid(),
            ObservedAtUtc = FirstRun.AddMinutes(2),
            StartedAt = FirstRun.AddMinutes(2),
            CompletedAt = FirstRun.AddMinutes(3)
        };
        var result = Calculate(new[] { QualifyingRun(0), duplicateSlot });

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle(x =>
            x.GovernanceRunId == "different-run-same-slot" &&
            x.Reasons.Contains("duplicate-or-out-of-order-schedule-slot"));
    }

    [Fact]
    public void Default_Schedule_Should_Compare_Observed_Start_Against_The_Nearest_Taiwan_Slot()
    {
        var observed = QualifyingRun(0) with
        {
            StartedAt = FirstRun.AddMinutes(10),
            CompletedAt = FirstRun.AddMinutes(11),
            ObservedAtUtc = null
        };
        var result = new ScheduledGovernanceReliabilityWindowCalculator().Calculate(new[] { observed });

        result.ConsecutiveQualifyingRuns.Should().Be(1);
        result.Runs.Should().ContainSingle();
        result.Runs[0].ExpectedAtUtc.Should().Be(FirstRun);
        result.Runs[0].ObservedAtUtc.Should().Be(FirstRun.AddMinutes(10));
        result.Runs[0].SignedDrift.Should().Be(TimeSpan.FromMinutes(10));
    }

    private static ScheduledGovernanceReliabilityWindowResult Calculate(
        IEnumerable<ScheduledGovernanceReliabilityReceiptProjection> receipts)
        => new ScheduledGovernanceReliabilityWindowCalculator(
            new ScheduledGovernanceReliabilityWindowOptions
            {
                ExpectedFirstRunAtUtc = FirstRun
            }).Calculate(receipts);

    private static ScheduledGovernanceReliabilityReceiptProjection QualifyingRun(int index)
        => new()
        {
            ReceiptId = Guid.NewGuid(),
            GovernanceRunId = $"run-{index}",
            StartedAt = FirstRun.AddHours(index * 4),
            CompletedAt = FirstRun.AddHours(index * 4).AddMinutes(1),
            ObservedAtUtc = FirstRun.AddHours(index * 4),
            ExecutionMode = "Scheduled",
            IsReplay = false,
            RunExists = true,
            Terminal = true,
            Status = "Completed",
            ToolContractVersion = ScheduledGovernanceContract.ToolContractVersion,
            SchemaHash = ScheduledGovernanceContract.SchemaHash,
            PublishedCatalogVersion = ScheduledGovernanceContract.PublishedCatalogVersion,
            RuntimeIdentity = ScheduledGovernanceContract.RuntimeIdentity,
            BaselineIdentity = "v1.1.95|catalog-automation-v4",
            Decision = ScheduledGovernanceDecision.HumanDecisionOnly,
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
            LatestBatchReceived = false,
            InitialGovernanceActionable = 0,
            FinalGovernanceActionable = 0,
            ExecutionActionableCount = 0,
            GovernedExceptionCount = 1,
            Applied = 0,
            Failed = 0,
            AuditIds = [Guid.NewGuid()],
            FinalConvergenceStatus = "ConvergedWithExceptions",
            StoppedReason = "ReviewCompleted"
        };

    private static GovernanceRunReceiptResult ReceiptFrom(
        ScheduledGovernanceReliabilityReceiptProjection projection)
        => new(
            projection.ReceiptId,
            projection.GovernanceRunId,
            "scheduled-governance",
            projection.ExecutionMode,
            projection.StartedAt,
            projection.CompletedAt,
            projection.ToolContractVersion,
            projection.SchemaHash,
            projection.PublishedCatalogVersion,
            "snapshot-initial",
            "snapshot-final",
            projection.CoverageComplete,
            projection.InitialGovernanceActionable,
            projection.FinalGovernanceActionable,
            1,
            projection.ExecutionActionableCount,
            projection.GovernedExceptionCount,
            projection.Applied,
            projection.Failed,
            0,
            1,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            projection.FinalConvergenceStatus,
            projection.StoppedReason,
            projection.AuditIds,
            [],
            projection.IsReplay,
            projection.RunExists,
            projection.Status,
            projection.LatestBatchReceived,
            string.Empty,
            null);
}

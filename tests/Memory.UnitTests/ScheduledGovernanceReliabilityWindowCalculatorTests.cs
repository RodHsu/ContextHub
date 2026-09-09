using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class ScheduledGovernanceReliabilityWindowCalculatorTests
{
    private static readonly DateTimeOffset FirstRun = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid OwnerUserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly string[] ProjectIds = ["project-a"];
    private static readonly string AuthorityEpochDigest = Digest("authority-epoch-a");
    private static readonly string BaseConfigurationDigest = Digest("configuration");
    private static readonly string ExpectedConfigurationDigest =
        ScheduledGovernanceReliabilityService.ComputeConfigurationDigest(
            BaseConfigurationDigest,
            AuthorityEpochDigest);
    private static readonly ScheduledGovernanceNaturalOriginAuthority NaturalOriginAuthority = new(
        "https://scheduler.example.test",
        "scheduler-control-plane",
        "production",
        Digest("task"),
        Digest("automation"),
        Digest("schedule"),
        ExpectedConfigurationDigest,
        AuthorityEpochDigest);

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
            MaximumAllowedDrift = TimeSpan.FromMinutes(15),
            ExpectedNaturalOriginAuthority = NaturalOriginAuthority
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
            FixedReversibleExecutorUsed = true,
            NaturalOriginEvidence = projection.NaturalOriginEvidence
        };
        var adapted = ScheduledGovernanceReliabilityReceiptProjection.FromReceipt(
            receipt,
            ScheduledGovernanceContract.RuntimeIdentity,
            evidence) with
        {
            TenantId = projection.TenantId,
            OwnerUserId = projection.OwnerUserId,
            RequestIdentityHash = projection.RequestIdentityHash,
            BaselineIdentity = ScheduledGovernanceReliabilityReceiptProjection.BindAuthorityEpoch(
                projection.BaselineIdentity,
                NaturalOriginAuthority.AuthorityEpochDigest)
        };
        var result = new ScheduledGovernanceReliabilityWindowCalculator(new()
        {
            ExpectedNaturalOriginAuthority = NaturalOriginAuthority
        })
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
        var duplicateSlot = RebindRun(QualifyingRun(0), "different-run-same-slot") with
        {
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
        var result = new ScheduledGovernanceReliabilityWindowCalculator(new()
        {
            ExpectedNaturalOriginAuthority = NaturalOriginAuthority
        }).Calculate(new[] { observed });

        result.ConsecutiveQualifyingRuns.Should().Be(1);
        result.Runs.Should().ContainSingle();
        result.Runs[0].ExpectedAtUtc.Should().Be(FirstRun);
        result.Runs[0].ObservedAtUtc.Should().Be(FirstRun.AddMinutes(10));
        result.Runs[0].SignedDrift.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Schedule_Aligned_Run_Without_A_And_B_Should_Remain_Unattested()
    {
        var run = QualifyingRun(0) with { NaturalOriginEvidence = null };

        var result = Calculate([run]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.Runs.Should().ContainSingle(x =>
            !x.Qualifies &&
            x.NaturalOriginStatus == "Unattested" &&
            x.Reasons.Contains(ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason));
    }

    [Fact]
    public void Verified_A_Without_Control_Plane_B_Should_Not_Qualify()
    {
        var run = QualifyingRun(0);
        run = run with
        {
            NaturalOriginEvidence = run.NaturalOriginEvidence! with { ControlPlaneAudit = null }
        };

        var result = Calculate([run]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle(x =>
            x.Reasons.Contains("control-plane-audit-not-verified"));
    }

    [Fact]
    public void A_And_B_With_Different_Actor_Binding_Should_Fail_Closed()
    {
        var run = QualifyingRun(0);
        var audit = run.NaturalOriginEvidence!.ControlPlaneAudit!;
        run = run with
        {
            NaturalOriginEvidence = run.NaturalOriginEvidence with
            {
                ControlPlaneAudit = audit with
                {
                    Binding = audit.Binding with { ActorBindingHash = Digest("different-actor") }
                }
            }
        };

        var result = Calculate([run]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle(x =>
            x.Reasons.Contains("natural-origin-actor-binding-mismatch"));
    }

    [Theory]
    [InlineData("platformIssuer")]
    [InlineData("controlPlaneSource")]
    [InlineData("environment")]
    [InlineData("task")]
    [InlineData("automation")]
    [InlineData("schedule")]
    [InlineData("configuration")]
    public void A_And_B_That_Agree_But_Do_Not_Match_Current_Authority_Should_Fail_Closed(
        string mismatchedField)
    {
        var run = QualifyingRun(0);
        var currentAuthority = mismatchedField switch
        {
            "platformIssuer" => NaturalOriginAuthority with { PlatformIssuer = "https://other.example.test" },
            "controlPlaneSource" => NaturalOriginAuthority with { ControlPlaneSourceSystem = "other-control-plane" },
            "environment" => NaturalOriginAuthority with { Environment = "staging" },
            "task" => NaturalOriginAuthority with { TaskBindingHash = Digest("different-task") },
            "automation" => NaturalOriginAuthority with { AutomationBindingHash = Digest("different-automation") },
            "schedule" => NaturalOriginAuthority with { ScheduleDigest = Digest("different-schedule") },
            "configuration" => NaturalOriginAuthority with
            {
                ConfigurationDigest = Digest("different-current-configuration")
            },
            _ => throw new ArgumentOutOfRangeException(nameof(mismatchedField))
        };
        var result = new ScheduledGovernanceReliabilityWindowCalculator(new()
        {
            ExpectedFirstRunAtUtc = FirstRun,
            ExpectedNaturalOriginAuthority = currentAuthority
        }).Calculate([run]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle(x =>
            x.Reasons.Contains("natural-origin-authority-baseline-mismatch"));
    }

    [Fact]
    public void Missing_Current_Natural_Origin_Authority_Should_Fail_Closed()
    {
        var result = new ScheduledGovernanceReliabilityWindowCalculator(new()
        {
            ExpectedFirstRunAtUtc = FirstRun
        }).Calculate([QualifyingRun(0)]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle(x =>
            x.Reasons.Contains("natural-origin-authority-baseline-not-proven"));
    }

    [Fact]
    public void Authority_Epoch_A_to_B_to_A_Must_Not_Revive_The_Previous_Streak()
    {
        var baseIdentity = "v1.1.95|catalog-automation-v4";
        var first = QualifyingRun(0) with
        {
            BaselineIdentity = ScheduledGovernanceReliabilityReceiptProjection.BindAuthorityEpoch(
                baseIdentity,
                NaturalOriginAuthority.AuthorityEpochDigest)
        };
        var epochB = Digest("authority-epoch-b");
        var second = QualifyingRun(1) with
        {
            BaselineIdentity = ScheduledGovernanceReliabilityReceiptProjection.BindAuthorityEpoch(
                baseIdentity,
                epochB)
        };
        var third = QualifyingRun(2) with
        {
            BaselineIdentity = ScheduledGovernanceReliabilityReceiptProjection.BindAuthorityEpoch(
                baseIdentity,
                NaturalOriginAuthority.AuthorityEpochDigest)
        };

        var result = Calculate([first, second, third]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().HaveCount(2);
        result.NonQualifyingRuns[0].Reasons.Should()
            .Contain("natural-origin-authority-baseline-mismatch");
        result.NonQualifyingRuns[1].Reasons.Should().Contain("reliability-baseline-changed");
    }

    [Fact]
    public void Missing_Immutable_Server_Safety_Request_Identity_Must_Not_Qualify()
    {
        var result = Calculate([QualifyingRun(0) with { RequestIdentityHash = string.Empty }]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle(x =>
            x.Reasons.Contains("natural-origin-request-correlation-mismatch"));
    }

    [Fact]
    public void Human_Decision_Only_Must_Not_Qualify_A_Natural_Run()
    {
        var run = QualifyingRun(0) with
        {
            Decision = ScheduledGovernanceDecision.HumanDecisionOnly,
            GovernedExceptionCount = 1,
            FinalConvergenceStatus = "ConvergedWithExceptions"
        };

        var result = Calculate([run]);

        result.ConsecutiveQualifyingRuns.Should().Be(0);
        result.NonQualifyingRuns.Should().ContainSingle(x =>
            x.Reasons.Contains("decision-human-authority-required"));
    }

    [Fact]
    public void Drift_Tolerance_Above_Fifteen_Minutes_Must_Be_Rejected()
    {
        var action = () => new ScheduledGovernanceReliabilityWindowCalculator(new()
        {
            MaximumAllowedDrift = TimeSpan.FromMinutes(16)
        });

        action.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void Malformed_Authority_Digest_Should_Return_A_Stable_Validation_Error()
    {
        var action = () => new ScheduledGovernanceReliabilityWindowCalculator(new()
        {
            ExpectedNaturalOriginAuthority = NaturalOriginAuthority with { TaskBindingHash = null! }
        });

        action.Should().Throw<ArgumentException>()
            .WithMessage("Natural-origin authority is incomplete or malformed.");
    }

    private static ScheduledGovernanceReliabilityWindowResult Calculate(
        IEnumerable<ScheduledGovernanceReliabilityReceiptProjection> receipts)
        => new ScheduledGovernanceReliabilityWindowCalculator(
            new ScheduledGovernanceReliabilityWindowOptions
            {
                ExpectedFirstRunAtUtc = FirstRun,
                ExpectedNaturalOriginAuthority = NaturalOriginAuthority
            }).Calculate(receipts);

    private static ScheduledGovernanceReliabilityReceiptProjection QualifyingRun(int index)
    {
        var governanceRunId = $"run-{index}";
        var receiptId = Guid.NewGuid();
        var expectedAt = FirstRun.AddHours(index * 4);
        var projectScopeHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeProjectScopeHash(ProjectIds);
        var requestIdentityHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeReviewRequestIdentityHash(governanceRunId);
        var actorBindingHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeActorBindingHash(TenantId, OwnerUserId);
        var binding = new ScheduledGovernanceReliabilityEvidenceBinding(
            TenantId,
            OwnerUserId,
            governanceRunId,
            receiptId,
            projectScopeHash,
            "production",
            expectedAt,
            $"slot-{index}",
            ScheduledGovernanceReliabilityEvidenceContract.NaturalScheduleTrigger,
            ScheduledGovernanceReliabilityEvidenceContract.ResourceAudience,
            requestIdentityHash,
            Digest($"dispatch-{index}"),
            actorBindingHash,
            Digest("task"),
            Digest("automation"),
            Digest("schedule"),
            NaturalOriginAuthority.ConfigurationDigest,
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            ScheduledGovernanceReliabilityEvidenceContract.ComputeRuntimeIdentityHash(
                ScheduledGovernanceContract.RuntimeIdentity));
        var naturalOriginEvidence = new ScheduledGovernanceReliabilityEvidenceSnapshot(
            new ScheduledGovernancePlatformAttestationEvidence(
                ScheduledGovernanceEvidenceVerificationStatus.Verified,
                SignatureValid: true,
                ReplaySafe: true,
                "https://scheduler.example.test",
                "production",
                "key-1",
                binding,
                expectedAt.AddMinutes(-1),
                expectedAt.AddMinutes(14)),
            new ScheduledGovernanceControlPlaneAuditEvidence(
                ScheduledGovernanceEvidenceVerificationStatus.Verified,
                SourceAuthenticated: true,
                ImmutableEvent: true,
                ReplaySafe: true,
                "scheduler-control-plane",
                Digest($"audit-{index}"),
                index,
                binding));

        return new()
        {
            TenantId = TenantId,
            OwnerUserId = OwnerUserId,
            ReceiptId = receiptId,
            GovernanceRunId = governanceRunId,
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
            RequestIdentityHash = requestIdentityHash,
            ProjectIds = ProjectIds,
            RuntimeIdentity = ScheduledGovernanceContract.RuntimeIdentity,
            NaturalOriginEvidence = naturalOriginEvidence,
            BaselineIdentity = ScheduledGovernanceReliabilityReceiptProjection.BindAuthorityEpoch(
                "v1.1.95|catalog-automation-v4",
                NaturalOriginAuthority.AuthorityEpochDigest),
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
            LatestBatchReceived = false,
            InitialGovernanceActionable = 0,
            FinalGovernanceActionable = 0,
            ExecutionActionableCount = 0,
            GovernedExceptionCount = 0,
            Applied = 0,
            Failed = 0,
            AuditIds = [Guid.NewGuid()],
            FinalConvergenceStatus = "NoOpConverged",
            StoppedReason = "ReviewCompleted"
        };
    }

    private static string Digest(string value)
        => ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(value);

    private static ScheduledGovernanceReliabilityReceiptProjection RebindRun(
        ScheduledGovernanceReliabilityReceiptProjection run,
        string governanceRunId)
    {
        var receiptId = Guid.NewGuid();
        var requestIdentityHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeReviewRequestIdentityHash(governanceRunId);
        var evidence = run.NaturalOriginEvidence!;
        var attestation = evidence.PlatformAttestation!;
        var audit = evidence.ControlPlaneAudit!;
        var binding = attestation.Binding with
        {
            GovernanceRunId = governanceRunId,
            ReceiptId = receiptId,
            RequestIdentityHash = requestIdentityHash,
            DispatchIdentityHash = Digest($"dispatch-{governanceRunId}")
        };
        return run with
        {
            ReceiptId = receiptId,
            GovernanceRunId = governanceRunId,
            RequestIdentityHash = requestIdentityHash,
            NaturalOriginEvidence = evidence with
            {
                PlatformAttestation = attestation with { Binding = binding },
                ControlPlaneAudit = audit with { Binding = binding }
            }
        };
    }

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
            0,
            projection.ExecutionActionableCount,
            projection.GovernedExceptionCount,
            projection.Applied,
            projection.Failed,
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
            projection.FinalConvergenceStatus,
            projection.StoppedReason,
            projection.AuditIds,
            projection.ProjectIds,
            projection.IsReplay,
            projection.RunExists,
            projection.Status,
            projection.LatestBatchReceived,
            string.Empty,
            null);
}

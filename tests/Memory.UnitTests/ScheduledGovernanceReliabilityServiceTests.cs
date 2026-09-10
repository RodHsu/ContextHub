using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class ScheduledGovernanceReliabilityServiceTests
{
    private static readonly DateTimeOffset FirstRun = new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly Guid TenantId = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid OwnerUserId = Guid.Parse("20000000-0000-0000-0000-000000000002");
    private static readonly string[] ProjectIds = ["test-project"];
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
    public void Scheduled_Mode_Without_Platform_Attestation_Must_Not_Qualify_Even_With_Other_Evidence()
    {
        var observedProjection = ScheduledGovernanceReliabilityService.BuildProjection(
            CreateReceipt("scheduled-1", "Review"));
        observedProjection.RuntimeIdentity.Should().BeNull(
            "scheduled runtime identity must come from immutable receipt evidence");
        observedProjection.BaselineIdentity.Should().NotContain(
            ScheduledGovernanceContract.RuntimeIdentity.BuildVersion);

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
    public void Runtime_identity_hash_must_canonicalize_to_postgres_microsecond_precision()
    {
        var timestampWithSeventhFractionalDigit =
            new DateTimeOffset(2026, 9, 10, 1, 2, 3, TimeSpan.Zero).AddTicks(1_234_567);
        var original = new ScheduledGovernanceRuntimeIdentity(
            "Memory.ScheduledGovernanceGateway",
            "v1.1.98",
            timestampWithSeventhFractionalDigit,
            "runtime-derived-identity");
        var persisted = original with
        {
            BuildTimestampUtc = ScheduledGovernanceReliabilityEvidenceContract
                .NormalizeRuntimeBuildTimestampUtc(timestampWithSeventhFractionalDigit)
        };

        (persisted.BuildTimestampUtc.Ticks % TimeSpan.TicksPerMicrosecond).Should().Be(0);
        ScheduledGovernanceReliabilityEvidenceContract.ComputeRuntimeIdentityHash(original)
            .Should().Be(ScheduledGovernanceReliabilityEvidenceContract.ComputeRuntimeIdentityHash(persisted));
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

    [Theory]
    [InlineData(null, null, true, true)]
    [InlineData(null, 3, false, true)]
    [InlineData(3, null, true, false)]
    [InlineData(3, 2, true, false)]
    [InlineData(3, 3, true, true)]
    [InlineData(3, 3, false, false)]
    [InlineData(3, 4, false, true)]
    public void Receipt_projection_must_not_be_overwritten_by_older_or_unordered_evidence(
        int? storedSequence,
        int? incomingSequence,
        bool sameReceipt,
        bool expected)
    {
        var storedReceiptId = Guid.NewGuid();
        var incomingReceiptId = sameReceipt ? storedReceiptId : Guid.NewGuid();

        ScheduledGovernanceReliabilityService.ShouldApplyCanonicalProjection(
                storedSequence,
                storedReceiptId,
                incomingSequence,
                incomingReceiptId)
            .Should().Be(expected);
    }

    [Fact]
    public void Current_Natural_Origin_Authority_Must_Bind_The_Server_Schedule_Intent()
    {
        ScheduledGovernanceReliabilityService.CurrentScheduleDigest.Should().MatchRegex("^[0-9a-f]{64}$");
        ScheduledGovernanceReliabilityService.IsCurrentScheduleDigest(
                ScheduledGovernanceReliabilityService.CurrentScheduleDigest)
            .Should().BeTrue();
        ScheduledGovernanceReliabilityService.IsCurrentScheduleDigest(Digest("different-schedule"))
            .Should().BeFalse();
        ScheduledGovernanceReliabilityService.IsCurrentScheduleDigest(null).Should().BeFalse();
    }

    [Fact]
    public void BuildProjection_Must_Use_Only_Immutable_Server_Safety_Request_Identity()
    {
        var receipt = CreateReceipt("scheduled-immutable-request");
        var immutableRequestHash = Digest("immutable-request");
        var serverSafetyEvidence = new ScheduledGovernanceServerSafetyEvidenceSnapshot(
            ReceiptEventSequence: 7,
            ReviewRequestIdentityHash: immutableRequestHash,
            CapturedRuntimeIdentity: ScheduledGovernanceContract.RuntimeIdentity,
            InitialReviewReceived: true,
            CountInvariantSatisfied: true,
            DecisionObeyed: true,
            NoGeneralConnectorFallback: true,
            NoUnauthorizedMutation: true,
            NoDuplicateMutation: true,
            DisplayNameUnchanged: true,
            BusinessWorkItemsUntouched: true,
            HostDispatchCompleted: true,
            ImmutableSnapshotBound: true,
            FixedReversibleExecutorUsed: true);

        var projection = ScheduledGovernanceReliabilityService.BuildProjection(
            receipt,
            actor: null,
            naturalOriginEvidence: null,
            serverSafetyEvidence: serverSafetyEvidence,
            authority: NaturalOriginAuthority);

        projection.RequestIdentityHash.Should().Be(immutableRequestHash);
        projection.InitialReviewReceived.Should().BeTrue();

        var missingRequestHash = ScheduledGovernanceReliabilityService.BuildProjection(
            receipt,
            actor: null,
            naturalOriginEvidence: null,
            serverSafetyEvidence: serverSafetyEvidence with { ReviewRequestIdentityHash = null },
            authority: NaturalOriginAuthority);

        missingRequestHash.RequestIdentityHash.Should().BeEmpty();
        missingRequestHash.InitialReviewReceived.Should().BeTrue();
    }

    [Fact]
    public void ConfigurationDigest_Must_Bind_The_Base_Configuration_To_The_Authority_Epoch()
    {
        var epochA = Digest("epoch-a");
        var epochB = Digest("epoch-b");
        var baseDigest = Digest("base-configuration");

        var digestA = ScheduledGovernanceReliabilityService.ComputeConfigurationDigest(baseDigest, epochA);
        var digestB = ScheduledGovernanceReliabilityService.ComputeConfigurationDigest(baseDigest, epochB);

        digestA.Should().MatchRegex("^[0-9a-f]{64}$");
        digestA.Should().NotBe(digestB);
        digestA.Should().Be(
            ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(
                $"{ScheduledGovernanceReliabilityService.ConfigurationDigestBindingVersion}|{baseDigest}|{epochA}"));
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
            new ScheduledGovernanceReliabilityWindowOptions
            {
                ExpectedFirstRunAtUtc = FirstRun,
                ExpectedNaturalOriginAuthority = NaturalOriginAuthority
            })
            .Calculate(projections);

        result.ConsecutiveQualifyingRuns.Should().Be(3);
        result.IgnoredManualRunCount.Should().Be(1);
        result.NonQualifyingRuns.Should().BeEmpty();
        result.ResetEvents.Should().BeEmpty();
        result.Runs.Single(x => x.GovernanceRunId == "manual-1").IsIgnored.Should().BeTrue();
        result.Runs.Single(x => x.GovernanceRunId == "manual-1").CountedTowardGate.Should().BeFalse();
    }

    private static ScheduledGovernanceReliabilityReceiptProjection QualifyingProjection(int index)
    {
        var runId = $"scheduled-{index}";
        var receiptId = Guid.NewGuid();
        var expectedAt = FirstRun.AddHours(index * 4);
        var requestIdentityHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeReviewRequestIdentityHash(runId);
        var actorBindingHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeActorBindingHash(TenantId, OwnerUserId);
        var binding = new ScheduledGovernanceReliabilityEvidenceBinding(
            TenantId,
            OwnerUserId,
            runId,
            receiptId,
            ScheduledGovernanceReliabilityEvidenceContract.ComputeProjectScopeHash(ProjectIds),
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
        var evidence = new ScheduledGovernanceReliabilityEvidenceSnapshot(
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
            GovernanceRunId = runId,
            StartedAt = expectedAt,
            CompletedAt = expectedAt.AddMinutes(1),
            ObservedAtUtc = expectedAt,
            ExecutionMode = "Scheduled",
            RunExists = true,
            Terminal = true,
            Status = "Completed",
            ToolContractVersion = ScheduledGovernanceContract.ToolContractVersion,
            SchemaHash = ScheduledGovernanceContract.SchemaHash,
            PublishedCatalogVersion = ScheduledGovernanceContract.PublishedCatalogVersion,
            RequestIdentityHash = requestIdentityHash,
            ProjectIds = ProjectIds,
            RuntimeIdentity = ScheduledGovernanceContract.RuntimeIdentity,
            NaturalOriginEvidence = evidence,
            BaselineIdentity = ScheduledGovernanceReliabilityReceiptProjection.BindAuthorityEpoch(
                "stable-baseline",
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
            FinalConvergenceStatus = "NoOpConverged",
            StoppedReason = "ReviewCompleted"
        };
    }

    private static string Digest(string value)
        => ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(value);

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

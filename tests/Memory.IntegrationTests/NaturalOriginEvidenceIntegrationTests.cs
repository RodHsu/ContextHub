using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace Memory.IntegrationTests;

public sealed class NaturalOriginEvidenceIntegrationTests(ContainerTestEnvironment environment, ITestOutputHelper output)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Batched_history_matches_exact_readers_with_mixed_natural_evidence_and_bounded_queries()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var now = DateTimeOffset.UtcNow;
        var owner = new TenantUser
        {
            TenantId = actor.TenantId!.Value,
            Username = $"history-projection-{Guid.NewGuid():N}",
            DisplayName = "Reliability history fixture",
            Role = TenantUserRole.Owner,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TenantUsers.Add(owner);
        await db.SaveChangesAsync(CancellationToken.None);
        actor = actor with { UserId = owner.Id };
        accessor.Current = actor;

        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var reliability = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>();
        var store = scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>();
        var recorded = new List<GovernanceRunReceiptResult>();
        for (var index = 0; index < 34; index++)
        {
            var runId = $"mixed-history-{index:D2}-{Guid.NewGuid():N}";
            var startedAt = now.AddSeconds(index);
            var identity = CurrentScheduledIdentity();
            var review = CreateReview(runId, ProjectContext.SharedProjectId, identity);
            await receipts.RecordReviewStartedAsync(runId, startedAt, identity, CancellationToken.None);
            await receipts.RecordReviewAsync(review, startedAt, CancellationToken.None);
            await receipts.RecordScheduledDecisionAsync(
                CreateScheduledDecision(runId, ProjectContext.SharedProjectId, review.DurableMemoryCoverage!.SnapshotToken),
                startedAt, CancellationToken.None);
            var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
            await reliability.PersistObservationAsync(receipt, CancellationToken.None);
            recorded.Add(receipt);

            if (index < 3)
            {
                var eventKey = await db.GovernanceRunReceipts.AsNoTracking().Where(row => row.Id == receipt.ReceiptId)
                    .Select(row => row.EventKey).SingleAsync(CancellationToken.None);
                await store.AppendAsync(CreateEvidence(
                    NaturalOriginEvidenceKind.PlatformAttestation, actor, receipt, eventKey, startedAt, null));
                if (index == 0 || index == 2)
                {
                    var secondKind = index == 0 ? NaturalOriginEvidenceKind.ControlPlaneAudit : NaturalOriginEvidenceKind.PlatformAttestation;
                    var secondSlot = index == 0 ? startedAt : startedAt.AddMinutes(-1);
                    await store.AppendAsync(CreateEvidence(
                        secondKind, actor, receipt, eventKey, secondSlot, index == 0 ? 0 : null));
                }
            }
        }

        var commandCount = 0;
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .LogTo(_ => commandCount++, [RelationalEventId.CommandExecuted])
            .Options;
        await using var historyDb = new MemoryDbContext(options);
        var safety = new ScheduledGovernanceServerSafetyEvidenceProvider(historyDb);
        var natural = new ScheduledGovernanceReliabilityEvidenceProvider(
            historyDb, new NaturalOriginEvidenceStore(historyDb, TimeProvider.System));
        var exactReader = new ScheduledGovernanceReliabilityService(
            historyDb, accessor, TimeProvider.System, new ExactNaturalReader(natural), new ExactSafetyReader(safety));
        var historyReader = new ScheduledGovernanceReliabilityService(
            historyDb, accessor, TimeProvider.System, natural, safety);

        var elapsed = Stopwatch.StartNew();
        var expected = await exactReader.GetAsync(CancellationToken.None);
        var exactCommands = commandCount;
        var exactMilliseconds = elapsed.Elapsed.TotalMilliseconds;
        commandCount = 0;
        elapsed.Restart();
        var actual = await historyReader.GetAsync(CancellationToken.None);
        output.WriteLine($"Scheduled history reads for 34 runs: exact {exactCommands} commands {exactMilliseconds:F1} ms, batch {commandCount} commands {elapsed.Elapsed.TotalMilliseconds:F1} ms.");

        actual.Should().BeEquivalentTo(expected, options => options.WithStrictOrdering());
        actual.Runs.Should().HaveCount(recorded.Count);
        actual.Runs.Single(run => run.GovernanceRunId == recorded[0].GovernanceRunId)
            .PlatformSignedNaturalOriginAttested.Should().BeTrue();
        actual.GatePassed.Should().BeFalse();
        commandCount.Should().Be(10, "one actor history read, six safety reads, two ledger reads and one bound receipt read");
        commandCount.Should().BeLessThan(exactCommands);
        historyDb.ChangeTracker.HasChanges().Should().BeFalse();
    }

    private sealed class ExactNaturalReader(IScheduledGovernanceReliabilityEvidenceProvider provider)
        : IScheduledGovernanceReliabilityEvidenceProvider
    {
        public Task<ScheduledGovernanceReliabilityEvidenceSnapshot?> GetAsync(
            ScheduledGovernanceReliabilityEvidenceQuery query,
            CancellationToken cancellationToken)
            => provider.GetAsync(query, cancellationToken);
    }

    private sealed class ExactSafetyReader(IScheduledGovernanceServerSafetyEvidenceProvider provider)
        : IScheduledGovernanceServerSafetyEvidenceProvider
    {
        public Task<ScheduledGovernanceServerSafetyEvidenceSnapshot?> GetAsync(
            ScheduledGovernanceServerSafetyEvidenceQuery query,
            CancellationToken cancellationToken)
            => provider.GetAsync(query, cancellationToken);
    }

    [DockerRequiredFact]
    public async Task Verified_A_And_B_Without_Current_Server_Authority_Should_Remain_Unattested()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"natural-origin-{Guid.NewGuid():N}";
        var projectId = ProjectContext.SharedProjectId;
        var startedAt = DateTimeOffset.UtcNow;
        var identity = CurrentScheduledIdentity();

        await receipts.RecordReviewStartedAsync(runId, startedAt, identity, CancellationToken.None);
        var review = CreateReview(runId, projectId, identity);
        await receipts.RecordReviewAsync(review, startedAt, CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(
                runId,
                projectId,
                review.DurableMemoryCoverage!.SnapshotToken),
            startedAt,
            CancellationToken.None);
        var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var receiptRow = await db.GovernanceRunReceipts.AsNoTracking()
            .SingleAsync(row => row.Id == receipt.ReceiptId);
        receiptRow.RuntimeBuildTimestampUtc.Should().NotBeNull();
        (receiptRow.RuntimeBuildTimestampUtc!.Value.Ticks % TimeSpan.TicksPerMicrosecond)
            .Should().Be(0);
        var serverSafety = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
            .GetAsync(
                new ScheduledGovernanceServerSafetyEvidenceQuery(
                    actor.TenantId!.Value,
                    actor.UserId!.Value,
                    receipt.ReceiptId,
                    runId),
                CancellationToken.None);
        serverSafety!.CapturedRuntimeIdentity.Should().Be(ScheduledGovernanceContract.RuntimeIdentity);
        ScheduledGovernanceReliabilityEvidenceContract
            .ComputeRuntimeIdentityHash(serverSafety.CapturedRuntimeIdentity)
            .Should().Be(receiptRow.RuntimeIdentityHash);
        var expectedAt = startedAt.ToUniversalTime();
        var store = scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>();
        var reliabilityService = scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityService>();
        var initialReliability = await reliabilityService.ObserveAsync(receipt, CancellationToken.None);
        initialReliability.Runs.Single(run => run.GovernanceRunId == runId)
            .NaturalOriginStatus.Should().Be("Unattested");
        var persistedBeforeEvidence = await db.ScheduledGovernanceReliabilityRuns
            .AsNoTracking()
            .SingleAsync(row => row.GovernanceRunId == runId);

        await store.AppendAsync(CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            receipt,
            receiptRow.EventKey,
            expectedAt,
            sourceSequence: null));
        await store.AppendAsync(CreateEvidence(
            NaturalOriginEvidenceKind.ControlPlaneAudit,
            actor,
            receipt,
            receiptRow.EventKey,
            expectedAt,
            sourceSequence: 0));

        var query = new ScheduledGovernanceReliabilityEvidenceQuery(
            actor.TenantId!.Value,
            actor.UserId!.Value,
            receipt.ReceiptId,
            runId,
            ScheduledGovernanceReliabilityEvidenceContract.ComputeReviewRequestIdentityHash(runId),
            ScheduledGovernanceReliabilityEvidenceContract.ComputeProjectScopeHash(receipt.ProjectIds));
        var provider = scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityEvidenceProvider>();
        var evidence = await provider.GetAsync(query, CancellationToken.None);

        evidence.Should().NotBeNull();
        evidence!.PlatformAttestation!.VerificationStatus.Should()
            .Be(ScheduledGovernanceEvidenceVerificationStatus.Verified);
        evidence.ControlPlaneAudit!.VerificationStatus.Should()
            .Be(ScheduledGovernanceEvidenceVerificationStatus.Verified);
        evidence.ControlPlaneAudit.Sequence.Should().Be(0);

        var reliability = await reliabilityService.GetAsync(CancellationToken.None);
        reliability.Runs.Should().ContainSingle(run =>
            run.GovernanceRunId == runId &&
            run.NaturalOriginStatus == "Unattested" &&
            run.PlatformSignedNaturalOriginAttested);
        reliability.Runs.Single().Reasons.Should().Contain(
            "natural-origin-authority-baseline-not-proven");
        reliability.Runs.Single().Reasons.Should().Contain(
            ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);
        reliability.Runs.Single().Qualifies.Should().BeFalse(
            "natural origin does not replace missing server-owned mutation-safety evidence");
        var persistedAfterEvidence = await db.ScheduledGovernanceReliabilityRuns
            .AsNoTracking()
            .SingleAsync(row => row.GovernanceRunId == runId);
        persistedAfterEvidence.UpdatedAt.Should().Be(persistedBeforeEvidence.UpdatedAt,
            "run_get evidence re-evaluation must remain read-only");
    }

    [DockerRequiredFact]
    public async Task Authority_Must_Match_Committed_Epoch_Through_History_Read()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;
        var owner = new TenantUser
        {
            TenantId = actor.TenantId!.Value,
            Username = $"epoch-owner-{Guid.NewGuid():N}",
            DisplayName = "Authority epoch fixture",
            Role = TenantUserRole.Owner,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TenantUsers.Add(owner);
        await db.SaveChangesAsync(CancellationToken.None);
        actor = actor with { UserId = owner.Id, Username = owner.Username, Role = owner.Role };
        accessor.Current = actor;

        var ledger = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var store = scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>();
        var safety = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>();
        var evidence = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityEvidenceProvider>();
        var configured = new ScheduledGovernanceNaturalOriginAuthoritySnapshot(
            PlatformIssuer: "https://scheduler.example.test",
            ControlPlaneSourceSystem: "control-plane-audit",
            Environment: $"epoch-test-{Guid.NewGuid():N}",
            TaskBindingHash: Digest("cloud-task"),
            AutomationBindingHash: Digest("automation"),
            ScheduleDigest: ScheduledGovernanceReliabilityService.CurrentScheduleDigest,
            ConfigurationDigest: Digest("epoch-fixture-config"),
            AuthorityEpochDigest: Digest("epoch-fixture-a"));
        var advanceA = new ScheduledGovernanceAuthorityEpochAdvanceRequest(
            actor.TenantId!.Value, actor.UserId!.Value, configured.Environment,
            configured.ConfigurationDigest, configured.AuthorityEpochDigest);

        ScheduledGovernanceReliabilityService CreateReader(
            ScheduledGovernanceNaturalOriginAuthoritySnapshot snapshot,
            IScheduledGovernanceReliabilityEvidenceProvider? naturalEvidence = null)
            => new(
                db, accessor, TimeProvider.System, naturalEvidence ?? evidence, safety,
                new FixedAuthorityProvider(snapshot), authorityEpochLedger: ledger);

        async Task<GovernanceRunReceiptResult> RecordDecisionAsync()
        {
            var runId = $"authority-epoch-{Guid.NewGuid():N}";
            var startedAt = DateTimeOffset.UtcNow;
            var identity = CurrentScheduledIdentity();
            var projectId = ProjectContext.SharedProjectId;
            var review = CreateReview(runId, projectId, identity);
            await receipts.RecordReviewStartedAsync(runId, startedAt, identity, CancellationToken.None);
            await receipts.RecordReviewAsync(review, startedAt, CancellationToken.None);
            await receipts.RecordScheduledDecisionAsync(CreateScheduledDecision(
                runId, projectId, review.DurableMemoryCoverage!.SnapshotToken), startedAt, CancellationToken.None);
            return (await receipts.GetAsync(runId, CancellationToken.None))!;
        }

        Task<ScheduledGovernanceReliabilityRun> ReadProjectionAsync(string runId)
            => db.ScheduledGovernanceReliabilityRuns.AsNoTracking().SingleAsync(row => row.GovernanceRunId == runId);

        static bool HasEpoch(ScheduledGovernanceReliabilityRun row, string digest)
        {
            using var projection = JsonDocument.Parse(row.ProjectionJson);
            return ScheduledGovernanceReliabilityReceiptProjection.HasAuthorityEpoch(
                projection.RootElement.GetProperty("baselineIdentity").GetString(), digest);
        }

        var reader = CreateReader(configured);
        var absent = await RecordDecisionAsync();
        await reader.PersistObservationAsync(absent, CancellationToken.None);
        HasEpoch(await ReadProjectionAsync(absent.GovernanceRunId), configured.AuthorityEpochDigest).Should().BeFalse();

        (await ledger.AdvanceAsync(advanceA)).Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);
        var wrongConfiguration = await RecordDecisionAsync();
        await CreateReader(configured with { ConfigurationDigest = Digest("wrong-config") })
            .PersistObservationAsync(wrongConfiguration, CancellationToken.None);
        HasEpoch(await ReadProjectionAsync(wrongConfiguration.GovernanceRunId), configured.AuthorityEpochDigest).Should().BeFalse();

        var receipt = await RecordDecisionAsync();
        var eventKey = await db.GovernanceRunReceipts.AsNoTracking().Where(row => row.Id == receipt.ReceiptId)
            .Select(row => row.EventKey).SingleAsync();
        var expectedAt = receipt.StartedAt;
        foreach (var kind in new[] { NaturalOriginEvidenceKind.PlatformAttestation, NaturalOriginEvidenceKind.ControlPlaneAudit })
        {
            var entry = CreateEvidence(
                kind, actor, receipt, eventKey, expectedAt,
                sourceSequence: kind == NaturalOriginEvidenceKind.ControlPlaneAudit ? 0 : null);
            entry.Environment = configured.Environment;
            entry.ScheduleDigest = configured.ScheduleDigest;
            entry.ConfigurationDigest = ScheduledGovernanceReliabilityService.ComputeConfigurationDigest(
                configured.ConfigurationDigest, configured.AuthorityEpochDigest);
            await store.AppendAsync(entry);
        }

        await reader.PersistObservationAsync(receipt, CancellationToken.None);
        var capture = await ReadProjectionAsync(receipt.GovernanceRunId);
        HasEpoch(capture, configured.AuthorityEpochDigest).Should().BeTrue();
        var current = await reader.GetAsync(CancellationToken.None);
        current.Runs.Single(run => run.GovernanceRunId == receipt.GovernanceRunId)
            .NaturalOriginStatus.Should().Be("Verified");

        using var rotationScope = factory.Services.CreateScope();
        var rotationLedger = rotationScope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var advanceB = advanceA with
        {
            AuthorityEpochDigest = Digest("epoch-fixture-b"),
            ExpectedPreviousAuthorityEpochDigest = advanceA.AuthorityEpochDigest,
            ExpectedPreviousGeneration = 1
        };
        ScheduledGovernanceAuthorityEpochAdvanceResult? rotation = null;
        var rotatingReader = CreateReader(configured, new EvidenceReadCallback(
            evidence, receipt.ReceiptId, async cancellationToken =>
            {
                rotation = await rotationLedger.AdvanceAsync(advanceB, cancellationToken);
            }));

        var rotated = await rotatingReader.GetAsync(CancellationToken.None);
        rotation.Should().NotBeNull();
        rotation!.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);
        rotation.Epoch!.Generation.Should().Be(2);
        rotated.Runs.Single(run => run.GovernanceRunId == receipt.GovernanceRunId)
            .NaturalOriginStatus.Should().Be("Unattested");
        var afterRead = await ReadProjectionAsync(receipt.GovernanceRunId);
        afterRead.ProjectionJson.Should().Be(capture.ProjectionJson);
        afterRead.UpdatedAt.Should().Be(capture.UpdatedAt);
    }

    private sealed class FixedAuthorityProvider(ScheduledGovernanceNaturalOriginAuthoritySnapshot snapshot)
        : IScheduledGovernanceNaturalOriginAuthorityProvider
    {
        public ScheduledGovernanceNaturalOriginAuthoritySnapshot? GetCurrent() => snapshot;
    }

    private sealed class EvidenceReadCallback(
        IScheduledGovernanceReliabilityEvidenceProvider provider,
        Guid receiptId,
        Func<CancellationToken, Task> afterRead) : IScheduledGovernanceReliabilityEvidenceProvider
    {
        private bool _called;

        public async Task<ScheduledGovernanceReliabilityEvidenceSnapshot?> GetAsync(
            ScheduledGovernanceReliabilityEvidenceQuery query,
            CancellationToken cancellationToken)
        {
            var result = await provider.GetAsync(query, cancellationToken);
            if (!_called && query.ReceiptId == receiptId)
            {
                _called = true;
                await afterRead(cancellationToken);
            }

            return result;
        }
    }

    [DockerRequiredFact]
    public async Task Issuer_Jti_Replay_Should_Be_Idempotent_But_Conflicting_Binding_Should_Fail()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var (receipt, receiptEventKey) = await CreatePersistedReceiptAsync(
            scope.ServiceProvider,
            actor,
            "natural-origin-replay");
        var entry = CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            receipt,
            receiptEventKey,
            DateTimeOffset.UtcNow,
            sourceSequence: null);
        var store = scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>();

        var created = await store.AppendAsync(entry);
        var replay = await store.AppendAsync(Clone(entry));
        var conflicting = Clone(entry);
        conflicting.GovernanceRunIdHash = Digest($"different-{Guid.NewGuid():N}");
        var conflict = () => store.AppendAsync(conflicting);

        created.IsReplay.Should().BeFalse();
        replay.IsReplay.Should().BeTrue();
        replay.Entry.Id.Should().Be(created.Entry.Id);
        await conflict.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*replay binding mismatch*");
    }

    [DockerRequiredFact]
    public async Task Evidence_With_A_Nonmatching_Receipt_Must_Remain_Unattested()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"natural-origin-mismatch-{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.UtcNow;
        var identity = CurrentScheduledIdentity();

        await receipts.RecordReviewStartedAsync(runId, startedAt, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateReview(runId, $"evidence-project-{Guid.NewGuid():N}", identity),
            startedAt,
            CancellationToken.None);
        var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var receiptRow = await db.GovernanceRunReceipts.AsNoTracking()
            .SingleAsync(row => row.Id == receipt.ReceiptId);
        var mismatchedReceipt = receipt with { ReceiptId = Guid.NewGuid() };
        var store = scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>();

        var orphanAppend = () => store.AppendAsync(CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            mismatchedReceipt,
            receiptRow.EventKey,
            startedAt,
            sourceSequence: null));
        await orphanAppend.Should().ThrowAsync<DbUpdateException>();
        db.ChangeTracker.Clear();

        var reliability = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityService>()
            .ObserveAsync(receipt, CancellationToken.None);

        var run = reliability.Runs.Single(item => item.GovernanceRunId == runId);
        run.NaturalOriginStatus.Should().Be("Unattested");
        run.PlatformSignedNaturalOriginAttested.Should().BeFalse();
        run.Reasons.Should().Contain(
            ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);
    }

    [DockerRequiredFact]
    public async Task Evidence_With_A_Nonmatching_Dispatch_Identity_Must_Be_Invalid()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var (receipt, receiptEventKey) = await CreatePersistedReceiptAsync(
            scope.ServiceProvider,
            actor,
            "natural-origin-dispatch-mismatch");
        var evidence = CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            receipt,
            receiptEventKey,
            DateTimeOffset.UtcNow,
            sourceSequence: null);
        evidence.DispatchIdentityHash = Digest("wrong-dispatch");
        await scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>()
            .AppendAsync(evidence);

        var query = new ScheduledGovernanceReliabilityEvidenceQuery(
            actor.TenantId!.Value,
            actor.UserId!.Value,
            receipt.ReceiptId,
            receipt.GovernanceRunId,
            ScheduledGovernanceReliabilityEvidenceContract.ComputeReviewRequestIdentityHash(
                receipt.GovernanceRunId),
            ScheduledGovernanceReliabilityEvidenceContract.ComputeProjectScopeHash(receipt.ProjectIds));
        var projected = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityEvidenceProvider>()
            .GetAsync(query, CancellationToken.None);

        projected!.PlatformAttestation!.VerificationStatus.Should()
            .Be(ScheduledGovernanceEvidenceVerificationStatus.Invalid);
        projected.PlatformAttestation.SignatureValid.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Ledger_Must_Reject_Noncanonical_Identifiers_And_All_Mutations()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var (receipt, receiptEventKey) = await CreatePersistedReceiptAsync(
            scope.ServiceProvider,
            actor,
            "natural-origin-ledger");
        var store = scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>();
        var invalid = CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            receipt,
            receiptEventKey,
            DateTimeOffset.UtcNow,
            sourceSequence: null);
        invalid.Issuer = "https://scheduler.example.test\nnoncanonical";

        var invalidAppend = () => store.AppendAsync(invalid);
        await invalidAppend.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*invalid identifier*");

        var valid = CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            receipt,
            receiptEventKey,
            DateTimeOffset.UtcNow,
            sourceSequence: null);
        await store.AppendAsync(valid);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var replacementEnvironment = "staging";

        var update = async () => await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE natural_origin_evidence_ledger SET environment = {replacementEnvironment} WHERE id = {valid.Id}");
        var updateFailure = await update.Should().ThrowAsync<PostgresException>();
        updateFailure.Which.SqlState.Should().Be(PostgresErrorCodes.RaiseException);

        var delete = async () => await db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM natural_origin_evidence_ledger WHERE id = {valid.Id}");
        var deleteFailure = await delete.Should().ThrowAsync<PostgresException>();
        deleteFailure.Which.SqlState.Should().Be(PostgresErrorCodes.RaiseException);

        var truncateEvidence = async () => await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE natural_origin_evidence_ledger");
        var truncateEvidenceFailure = await truncateEvidence.Should().ThrowAsync<PostgresException>();
        truncateEvidenceFailure.Which.SqlState.Should().Be(PostgresErrorCodes.RaiseException);

        var truncateReceipts = async () => await db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE governance_run_receipts CASCADE");
        var truncateReceiptsFailure = await truncateReceipts.Should().ThrowAsync<PostgresException>();
        truncateReceiptsFailure.Which.SqlState.Should().Be(PostgresErrorCodes.RaiseException);

        (await db.NaturalOriginEvidenceLedgerEntries.AsNoTracking()
            .CountAsync(row => row.Id == valid.Id)).Should().Be(1);
    }

    private static NaturalOriginEvidenceLedgerEntry CreateEvidence(
        NaturalOriginEvidenceKind kind,
        ContextHubRequestActor actor,
        GovernanceRunReceiptResult receipt,
        string receiptEventKey,
        DateTimeOffset expectedAt,
        long? sourceSequence)
    {
        var now = DateTimeOffset.UtcNow;
        var requestIdentityHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeReviewRequestIdentityHash(receipt.GovernanceRunId);
        var sharedDispatchHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeDispatchIdentityHash(receipt.ReceiptId, receiptEventKey, requestIdentityHash);
        return new NaturalOriginEvidenceLedgerEntry
        {
            EvidenceKind = kind,
            Issuer = kind == NaturalOriginEvidenceKind.PlatformAttestation
                ? "https://scheduler.example.test"
                : "https://control-plane.example.test",
            Environment = "production",
            KeyId = "key-1",
            Algorithm = "ES256",
            EvidenceVersion = "1",
            JtiHash = Digest($"jti:{kind}:{Guid.NewGuid():N}"),
            SourceSystem = kind == NaturalOriginEvidenceKind.PlatformAttestation
                ? "platform-attestation"
                : "control-plane-audit",
            SourceEventIdHash = Digest($"event:{kind}:{Guid.NewGuid():N}"),
            SourceSequence = sourceSequence,
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            ProjectScopeHash = ScheduledGovernanceReliabilityEvidenceContract
                .ComputeProjectScopeHash(receipt.ProjectIds),
            TriggerKind = ScheduledGovernanceReliabilityEvidenceContract.NaturalScheduleTrigger,
            Audience = ScheduledGovernanceReliabilityEvidenceContract.ResourceAudience,
            ActorBindingHash = ScheduledGovernanceReliabilityEvidenceContract
                .ComputeActorBindingHash(actor.TenantId.Value, actor.UserId.Value),
            TaskBindingHash = Digest("cloud-task"),
            AutomationBindingHash = Digest("automation"),
            GovernanceRunIdHash = Digest(receipt.GovernanceRunId),
            SlotIdHash = Digest(expectedAt.ToUniversalTime().ToString("yyyyMMddTHHmmZ")),
            ExpectedAtUtc = expectedAt,
            IssuedAtUtc = now.AddMinutes(-1),
            ObservedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10),
            ScheduleDigest = Digest("four-hour-cadence"),
            ConfigurationDigest = Digest("approved-config"),
            RequestIdentityHash = requestIdentityHash,
            DispatchIdentityHash = sharedDispatchHash,
            ReceiptId = receipt.ReceiptId,
            ReceiptEventKeyHash = Digest(receiptEventKey),
            SignatureDigest = Digest($"signature:{kind}:{Guid.NewGuid():N}"),
            TenantBindingHash = ScheduledGovernanceReliabilityEvidenceContract
                .ComputeActorBindingHash(actor.TenantId.Value, actor.UserId.Value),
            ToolContractVersion = receipt.ToolContractVersion,
            SchemaHash = receipt.SchemaHash,
            PublishedCatalogVersion = receipt.PublishedCatalogVersion,
            RuntimeIdentityHash = ScheduledGovernanceReliabilityEvidenceContract
                .ComputeRuntimeIdentityHash(ScheduledGovernanceContract.RuntimeIdentity),
            VerificationStatus = "Verified"
        };
    }

    private static NaturalOriginEvidenceLedgerEntry Clone(NaturalOriginEvidenceLedgerEntry source)
        => new()
        {
            EvidenceKind = source.EvidenceKind,
            Issuer = source.Issuer,
            Environment = source.Environment,
            KeyId = source.KeyId,
            Algorithm = source.Algorithm,
            EvidenceVersion = source.EvidenceVersion,
            JtiHash = source.JtiHash,
            SourceSystem = source.SourceSystem,
            SourceEventIdHash = source.SourceEventIdHash,
            SourceSequence = source.SourceSequence,
            TenantId = source.TenantId,
            OwnerUserId = source.OwnerUserId,
            ProjectScopeHash = source.ProjectScopeHash,
            TriggerKind = source.TriggerKind,
            Audience = source.Audience,
            ActorBindingHash = source.ActorBindingHash,
            TaskBindingHash = source.TaskBindingHash,
            AutomationBindingHash = source.AutomationBindingHash,
            GovernanceRunIdHash = source.GovernanceRunIdHash,
            SlotIdHash = source.SlotIdHash,
            ExpectedAtUtc = source.ExpectedAtUtc,
            IssuedAtUtc = source.IssuedAtUtc,
            ObservedAtUtc = source.ObservedAtUtc,
            ExpiresAtUtc = source.ExpiresAtUtc,
            ScheduleDigest = source.ScheduleDigest,
            ConfigurationDigest = source.ConfigurationDigest,
            RequestIdentityHash = source.RequestIdentityHash,
            DispatchIdentityHash = source.DispatchIdentityHash,
            ReceiptId = source.ReceiptId,
            ReceiptEventKeyHash = source.ReceiptEventKeyHash,
            SignatureDigest = source.SignatureDigest,
            TenantBindingHash = source.TenantBindingHash,
            ToolContractVersion = source.ToolContractVersion,
            SchemaHash = source.SchemaHash,
            PublishedCatalogVersion = source.PublishedCatalogVersion,
            RuntimeIdentityHash = source.RuntimeIdentityHash,
            VerificationStatus = source.VerificationStatus
        };

    private static KnowledgeReviewResult CreateReview(
        string runId,
        string projectId,
        GovernanceReceiptContractIdentity identity)
    {
        var durable = new KnowledgeGovernanceCoverageResult(
            Guid.NewGuid(), $"snapshot-{Guid.NewGuid():N}", DateTimeOffset.UtcNow,
            0, 0, 0, 0, 0, 0, true, false, null)
        {
            AuthorizedGovernanceDurableMemoryCount = 0,
            GovernanceCoveredDurableMemoryCount = 0,
            GovernanceProjectIds = [projectId]
        };
        var surface = new GovernanceSurfaceCoverageResult(0, 0, 0, 0, 0, 0, 0, false, true);
        var coverage = new FullGovernanceCoverageResult(
            surface, surface, surface, surface, surface, surface,
            surface, surface, surface, surface, surface);
        var page = new KnowledgeReviewPageResult(0, 200, 0, 0, false);
        var convergence = new KnowledgeReviewConvergenceResult("NoOpConverged", 0, false, true)
        {
            CoverageComplete = true
        };
        return new KnowledgeReviewResult(
            [new AccessibleProjectResult(projectId, true, true)],
            null!, [], [], [], [], [], [], [], runId, false,
            new KnowledgeReviewPaginationResult(page, page, page, page, page, page, page, page),
            convergence)
        {
            DurableMemoryCoverage = durable,
            GovernanceCoverage = coverage,
            ReceiptContractIdentity = identity
        };
    }

    private static ScheduledGovernanceReviewResult CreateScheduledDecision(
        string runId,
        string projectId,
        string snapshotToken)
        => new(
            runId,
            IsReReview: false,
            ScheduledGovernanceDecision.NoOpConverged,
            snapshotToken,
            new ScheduledGovernanceCountInvariant(0, 0, 0, 0, 1, 0, true, true),
            CoverageComplete: true,
            CandidateCount: 0,
            ReversibleExecutionCount: 0,
            HumanDecisionCount: 0,
            GovernedExceptionCount: 0,
            BusinessWorkItemActionableCount: 0,
            ResolvedProjectIds: [projectId],
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            RuntimeIdentity: ScheduledGovernanceContract.RuntimeIdentity);

    private static async Task<(GovernanceRunReceiptResult Receipt, string EventKey)> CreatePersistedReceiptAsync(
        IServiceProvider services,
        ContextHubRequestActor actor,
        string prefix)
    {
        var receipts = services.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"{prefix}-{Guid.NewGuid():N}";
        var projectId = $"evidence-project-{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.UtcNow;
        var identity = CurrentScheduledIdentity();
        await receipts.RecordReviewStartedAsync(runId, startedAt, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateReview(runId, projectId, identity),
            startedAt,
            CancellationToken.None);
        var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
        var eventKey = await services.GetRequiredService<MemoryDbContext>()
            .GovernanceRunReceipts.AsNoTracking()
            .Where(row => row.Id == receipt.ReceiptId &&
                          row.TenantId == actor.TenantId &&
                          row.OwnerUserId == actor.UserId)
            .Select(row => row.EventKey)
            .SingleAsync();
        return (receipt, eventKey);
    }

    private static GovernanceReceiptContractIdentity CurrentScheduledIdentity()
        => new(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);

    private static ContextHubRequestActor UseBootstrapActor(IServiceProvider services)
    {
        var user = services.GetRequiredService<MemoryDbContext>().TenantUsers
            .Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SecurityManage, SecurityScopes.ScheduledGovernance],
            [],
            true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }

    private static string Digest(string value)
        => ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(value);
}

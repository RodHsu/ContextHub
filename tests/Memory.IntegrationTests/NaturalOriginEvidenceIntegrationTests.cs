using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class NaturalOriginEvidenceIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Verified_A_And_B_Should_Bind_To_The_Immutable_Receipt_And_Project_As_Verified()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"natural-origin-{Guid.NewGuid():N}";
        var projectId = $"evidence-project-{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.UtcNow;
        var identity = CurrentScheduledIdentity();

        await receipts.RecordReviewStartedAsync(runId, startedAt, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateReview(runId, projectId, identity),
            startedAt,
            CancellationToken.None);
        var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var receiptRow = await db.GovernanceRunReceipts.AsNoTracking()
            .SingleAsync(row => row.Id == receipt.ReceiptId);
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
            run.NaturalOriginStatus == "Verified" &&
            run.PlatformSignedNaturalOriginAttested);
        reliability.Runs.Single().Reasons.Should().NotContain(
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
    public async Task Issuer_Jti_Replay_Should_Be_Idempotent_But_Conflicting_Binding_Should_Fail()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var runId = $"natural-origin-replay-{Guid.NewGuid():N}";
        var receipt = CreateReceiptResult(runId, actor);
        var entry = CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            receipt,
            Digest("receipt-event"),
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

        await store.AppendAsync(CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            mismatchedReceipt,
            receiptRow.EventKey,
            startedAt,
            sourceSequence: null));
        await store.AppendAsync(CreateEvidence(
            NaturalOriginEvidenceKind.ControlPlaneAudit,
            actor,
            mismatchedReceipt,
            receiptRow.EventKey,
            startedAt,
            sourceSequence: 0));

        var reliability = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityService>()
            .ObserveAsync(receipt, CancellationToken.None);

        var run = reliability.Runs.Single(item => item.GovernanceRunId == runId);
        run.NaturalOriginStatus.Should().Be("Unattested");
        run.PlatformSignedNaturalOriginAttested.Should().BeFalse();
        run.Reasons.Should().Contain("platform-attestation-not-verified");
        run.Reasons.Should().Contain("control-plane-audit-not-verified");
    }

    [DockerRequiredFact]
    public async Task Ledger_Must_Reject_Noncanonical_Identifiers_And_All_Mutations()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipt = CreateReceiptResult($"natural-origin-ledger-{Guid.NewGuid():N}", actor);
        var store = scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>();
        var invalid = CreateEvidence(
            NaturalOriginEvidenceKind.PlatformAttestation,
            actor,
            receipt,
            Digest("receipt-event-invalid"),
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
            Digest("receipt-event-valid"),
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
        var sharedDispatchHash = Digest($"dispatch:{receipt.GovernanceRunId}");
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
            RequestIdentityHash = ScheduledGovernanceReliabilityEvidenceContract
                .ComputeReviewRequestIdentityHash(receipt.GovernanceRunId),
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

    private static GovernanceRunReceiptResult CreateReceiptResult(
        string runId,
        ContextHubRequestActor actor)
        => new(
            Guid.NewGuid(), runId, actor.Username, "Scheduled", DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            "snapshot", "snapshot", true, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, "NoOpConverged", "ReviewCompleted", [],
            ["test-project"], false, true, "Completed", false, string.Empty, null);

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

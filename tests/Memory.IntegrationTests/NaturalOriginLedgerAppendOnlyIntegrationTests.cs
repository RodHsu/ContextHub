using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class NaturalOriginLedgerAppendOnlyIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Receipt_fk_and_truncate_guard_reject_invalid_operations_and_retain_rows()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var (receipt, receiptEventKey) = await CreatePersistedReceiptAsync(
            scope.ServiceProvider,
            actor,
            "natural-origin-append-only");
        var store = scope.ServiceProvider.GetRequiredService<INaturalOriginEvidenceStore>();
        var valid = CreateEvidence(actor, receipt, receiptEventKey, "valid");

        await store.AppendAsync(valid);

        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using (var migration = connection.CreateCommand())
        {
            migration.CommandText = "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE name = '038_governance_evidence_integrity.sql');";
            ((bool)(await migration.ExecuteScalarAsync())!).Should().BeTrue();
        }

        var orphan = CreateEvidence(actor, receipt, receiptEventKey, "orphan");
        orphan.ReceiptId = Guid.NewGuid();
        orphan.GovernanceRunIdHash = Digest("orphan-run");
        orphan.SlotIdHash = Digest("orphan-slot");
        orphan.JtiHash = Digest("orphan-jti");
        orphan.SourceEventIdHash = Digest("orphan-event");
        db.NaturalOriginEvidenceLedgerEntries.Add(orphan);

        var orphanFailure = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var orphanPostgres = GetPostgresException(orphanFailure);
        orphanPostgres.Should().NotBeNull();
        orphanPostgres!.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        db.ChangeTracker.Clear();

        var crossOwner = CreateEvidence(actor, receipt, receiptEventKey, "cross-owner");
        crossOwner.TenantId = Guid.NewGuid();
        crossOwner.OwnerUserId = Guid.NewGuid();
        db.NaturalOriginEvidenceLedgerEntries.Add(crossOwner);

        var crossOwnerFailure = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        var crossOwnerPostgres = GetPostgresException(crossOwnerFailure);
        crossOwnerPostgres.Should().NotBeNull();
        crossOwnerPostgres!.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
        db.ChangeTracker.Clear();

        var truncateFailure = await Record.ExceptionAsync(
            () => db.Database.ExecuteSqlRawAsync("TRUNCATE TABLE natural_origin_evidence_ledger"));
        var truncatePostgres = GetPostgresException(truncateFailure);
        truncatePostgres.Should().NotBeNull();
        truncatePostgres!.SqlState.Should().Be(PostgresErrorCodes.RaiseException);

        (await db.NaturalOriginEvidenceLedgerEntries.AsNoTracking()
                .CountAsync(row => row.Id == valid.Id))
            .Should().Be(1);
        (await db.NaturalOriginEvidenceLedgerEntries.AsNoTracking()
                .CountAsync(row => row.Id == orphan.Id))
            .Should().Be(0);
    }

    private static PostgresException? GetPostgresException(Exception? exception)
        => exception switch
        {
            PostgresException postgres => postgres,
            DbUpdateException update => update.GetBaseException() as PostgresException,
            _ => null
        };

    private static NaturalOriginEvidenceLedgerEntry CreateEvidence(
        ContextHubRequestActor actor,
        GovernanceRunReceiptResult receipt,
        string receiptEventKey,
        string suffix)
    {
        var now = DateTimeOffset.UtcNow;
        return new NaturalOriginEvidenceLedgerEntry
        {
            EvidenceKind = NaturalOriginEvidenceKind.PlatformAttestation,
            Issuer = "https://scheduler.example.test",
            Environment = "production",
            KeyId = "key-1",
            Algorithm = "ES256",
            EvidenceVersion = "1",
            JtiHash = Digest($"jti:{suffix}"),
            SourceSystem = "platform-attestation",
            SourceEventIdHash = Digest($"event:{suffix}"),
            SourceSequence = null,
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
            GovernanceRunIdHash = ScheduledGovernanceReliabilityEvidenceContract
                .ComputeOpaqueHash($"run:{suffix}"),
            SlotIdHash = Digest($"slot:{suffix}"),
            ExpectedAtUtc = now,
            IssuedAtUtc = now.AddMinutes(-1),
            ObservedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(10),
            ScheduleDigest = Digest("four-hour-cadence"),
            ConfigurationDigest = Digest("approved-config"),
            RequestIdentityHash = ScheduledGovernanceReliabilityEvidenceContract
                .ComputeReviewRequestIdentityHash(receipt.GovernanceRunId),
            DispatchIdentityHash = Digest($"dispatch:{suffix}"),
            ReceiptId = receipt.ReceiptId,
            ReceiptEventKeyHash = Digest(receiptEventKey),
            SignatureDigest = Digest($"signature:{suffix}"),
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

    private static KnowledgeReviewResult CreateReview(
        string runId,
        string projectId,
        GovernanceReceiptContractIdentity identity)
    {
        var durable = new KnowledgeGovernanceCoverageResult(
            Guid.NewGuid(),
            $"snapshot-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            0,
            0,
            0,
            true,
            false,
            null)
        {
            AuthorizedGovernanceDurableMemoryCount = 0,
            GovernanceCoveredDurableMemoryCount = 0,
            GovernanceProjectIds = [projectId]
        };
        var surface = new GovernanceSurfaceCoverageResult(0, 0, 0, 0, 0, 0, 0, false, true);
        var coverage = new FullGovernanceCoverageResult(
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface);
        var page = new KnowledgeReviewPageResult(0, 200, 0, 0, false);
        var convergence = new KnowledgeReviewConvergenceResult("NoOpConverged", 0, false, true)
        {
            CoverageComplete = true
        };
        return new KnowledgeReviewResult(
            [new AccessibleProjectResult(projectId, true, true)],
            null!,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            runId,
            false,
            new KnowledgeReviewPaginationResult(page, page, page, page, page, page, page, page),
            convergence)
        {
            DurableMemoryCoverage = durable,
            GovernanceCoverage = coverage,
            ReceiptContractIdentity = identity
        };
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

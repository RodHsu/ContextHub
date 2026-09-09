using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Transactions;

namespace Memory.IntegrationTests;

public sealed class ScheduledGovernanceAuthorityEpochLedgerIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Advance_and_exact_replay_are_durable_and_historical_replay_is_not_current()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(scope.ServiceProvider);
        var ledger = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var runtimeEnvironment = UniqueEnvironment();
        var requestA = CreateRequest(identity, runtimeEnvironment, Digest("configuration"), Digest("epoch-a"));

        var first = await ledger.AdvanceAsync(requestA);
        var replayA = await ledger.AdvanceAsync(requestA);
        var requestB = requestA with
        {
            AuthorityEpochDigest = Digest("epoch-b"),
            ExpectedPreviousAuthorityEpochDigest = requestA.AuthorityEpochDigest
        };
        var second = await ledger.AdvanceAsync(requestB);
        var historicalA = await ledger.AdvanceAsync(requestA);
        var current = await ledger.GetCurrentAsync(new(
            identity.TenantId,
            identity.OwnerUserId,
            runtimeEnvironment));

        first.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);
        first.Epoch.Should().NotBeNull();
        first.Epoch!.Generation.Should().Be(1);
        replayA.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayCurrent);
        replayA.Epoch!.Id.Should().Be(first.Epoch.Id);
        replayA.Accepted.Should().BeTrue();
        second.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);
        second.Epoch!.Generation.Should().Be(2);
        historicalA.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayHistorical);
        historicalA.Epoch!.Id.Should().Be(first.Epoch.Id);
        historicalA.Accepted.Should().BeFalse();
        current!.Id.Should().Be(second.Epoch.Id);
        current.AuthorityEpochDigest.Should().Be(requestB.AuthorityEpochDigest);

        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ScheduledGovernanceAuthorityEpochs.AsNoTracking()
                .CountAsync(row => row.TenantId == identity.TenantId &&
                                   row.OwnerUserId == identity.OwnerUserId &&
                                   row.Environment == runtimeEnvironment))
            .Should().Be(2);
    }

    [DockerRequiredFact]
    public async Task A_to_B_to_A_reuse_and_binding_conflicts_fail_closed_without_append()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(scope.ServiceProvider);
        var ledger = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var runtimeEnvironment = UniqueEnvironment();
        var configuration = Digest("configuration");
        var requestA = CreateRequest(identity, runtimeEnvironment, configuration, Digest("epoch-a"));
        var requestB = requestA with
        {
            AuthorityEpochDigest = Digest("epoch-b"),
            ExpectedPreviousAuthorityEpochDigest = requestA.AuthorityEpochDigest
        };

        (await ledger.AdvanceAsync(requestA)).Status
            .Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);
        (await ledger.AdvanceAsync(requestB)).Status
            .Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);

        var reusedA = requestA with
        {
            ExpectedPreviousAuthorityEpochDigest = requestB.AuthorityEpochDigest,
            ExpectedPreviousGeneration = 2
        };
        var bindingConflict = requestA with { ConfigurationDigest = Digest("different-configuration") };
        var reusedResult = await ledger.AdvanceAsync(reusedA);
        var bindingResult = await ledger.AdvanceAsync(bindingConflict);

        reusedResult.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Conflict);
        reusedResult.Accepted.Should().BeFalse();
        bindingResult.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Conflict);
        bindingResult.Accepted.Should().BeFalse();
        var current = await ledger.GetCurrentAsync(new(identity.TenantId, identity.OwnerUserId, runtimeEnvironment));
        current!.Generation.Should().Be(2);
        current.AuthorityEpochDigest.Should().Be(requestB.AuthorityEpochDigest);
    }

    [DockerRequiredFact]
    public async Task Omitted_predecessor_generation_is_canonicalized_for_exact_replay()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(scope.ServiceProvider);
        var ledger = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var runtimeEnvironment = UniqueEnvironment();
        var requestA = CreateRequest(identity, runtimeEnvironment, Digest("configuration"), Digest("epoch-a"));
        var first = await ledger.AdvanceAsync(requestA);
        var requestB = requestA with
        {
            AuthorityEpochDigest = Digest("epoch-b"),
            ExpectedPreviousAuthorityEpochDigest = requestA.AuthorityEpochDigest,
            ExpectedPreviousGeneration = null
        };

        var advanced = await ledger.AdvanceAsync(requestB);
        var replay = await ledger.AdvanceAsync(requestB);

        first.Epoch!.Generation.Should().Be(1);
        advanced.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);
        replay.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayCurrent);
        replay.Epoch!.Id.Should().Be(advanced.Epoch!.Id);
    }

    [DockerRequiredFact]
    public async Task Concurrent_advances_from_the_same_predecessor_have_one_winner_and_one_conflict()
    {
        var factory = environment.GetFactory();
        using var setupScope = factory.Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(setupScope.ServiceProvider);
        var runtimeEnvironment = UniqueEnvironment();
        var requestA = CreateRequest(identity, runtimeEnvironment, Digest("configuration"), Digest("epoch-a"));
        var setupLedger = setupScope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        (await setupLedger.AdvanceAsync(requestA)).Status
            .Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);

        var requestB = requestA with
        {
            AuthorityEpochDigest = Digest("epoch-b"),
            ExpectedPreviousAuthorityEpochDigest = requestA.AuthorityEpochDigest
        };
        var requestC = requestA with
        {
            AuthorityEpochDigest = Digest("epoch-c"),
            ExpectedPreviousAuthorityEpochDigest = requestA.AuthorityEpochDigest
        };

        var results = await Task.WhenAll(
            AdvanceInNewScopeAsync(factory, requestB),
            AdvanceInNewScopeAsync(factory, requestC));

        results.Count(result => result.Status == ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced)
            .Should().Be(1);
        results.Count(result => result.Status == ScheduledGovernanceAuthorityEpochAdvanceStatus.Conflict)
            .Should().Be(1);
        var current = await setupLedger.GetCurrentAsync(new(identity.TenantId, identity.OwnerUserId, runtimeEnvironment));
        current!.Generation.Should().Be(2);
        current.AuthorityEpochDigest.Should().BeOneOf(requestB.AuthorityEpochDigest, requestC.AuthorityEpochDigest);
    }

    [DockerRequiredFact]
    public async Task Concurrent_exact_replays_are_idempotent_and_append_only()
    {
        var factory = environment.GetFactory();
        using var setupScope = factory.Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(setupScope.ServiceProvider);
        var runtimeEnvironment = UniqueEnvironment();
        var request = CreateRequest(identity, runtimeEnvironment, Digest("configuration"), Digest("epoch-a"));

        var results = await Task.WhenAll(
            AdvanceInNewScopeAsync(factory, request),
            AdvanceInNewScopeAsync(factory, request));

        results.Count(result => result.Status == ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced)
            .Should().Be(1);
        results.Count(result => result.Status == ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayCurrent)
            .Should().Be(1);
        results.Select(result => result.Epoch!.Id).Distinct().Should().ContainSingle();
        var db = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.ScheduledGovernanceAuthorityEpochs.AsNoTracking()
                .CountAsync(row => row.TenantId == identity.TenantId &&
                                   row.OwnerUserId == identity.OwnerUserId &&
                                   row.Environment == runtimeEnvironment))
            .Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Advance_rejects_an_ambient_transaction_without_appending()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var request = CreateRequest(
            identity,
            UniqueEnvironment(),
            Digest("configuration"),
            Digest("epoch-a"));

        await using var transaction = await db.Database.BeginTransactionAsync();
        var result = await ledger.AdvanceAsync(request);

        result.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Unavailable);
        result.Accepted.Should().BeFalse();
        (await db.ScheduledGovernanceAuthorityEpochs.AsNoTracking()
                .CountAsync(row => row.TenantId == identity.TenantId &&
                                   row.OwnerUserId == identity.OwnerUserId &&
                                   row.Environment == request.Environment))
            .Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Get_current_rejects_an_ef_ambient_transaction()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var ledger = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var authorityScope = new ScheduledGovernanceAuthorityEpochScope(
            identity.TenantId,
            identity.OwnerUserId,
            UniqueEnvironment());

        await using var transaction = await db.Database.BeginTransactionAsync();
        var read = () => ledger.GetCurrentAsync(authorityScope);

        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authority epoch reads cannot join an ambient transaction.");
    }

    [DockerRequiredFact]
    public async Task Advance_and_get_current_reject_system_transaction_ambient_context()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(scope.ServiceProvider);
        var ledger = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var request = CreateRequest(
            identity,
            UniqueEnvironment(),
            Digest("configuration"),
            Digest("epoch-a"));
        var authorityScope = new ScheduledGovernanceAuthorityEpochScope(
            identity.TenantId,
            identity.OwnerUserId,
            request.Environment);

        using var transaction = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled);
        var advance = await ledger.AdvanceAsync(request);
        var read = () => ledger.GetCurrentAsync(authorityScope);

        advance.Status.Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Unavailable);
        advance.Accepted.Should().BeFalse();
        await read.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Authority epoch reads cannot join an ambient transaction.");
    }

    [DockerRequiredFact]
    public async Task Environment_binding_isolation_and_append_only_guards_are_database_enforced()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(scope.ServiceProvider);
        var ledger = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>();
        var configuration = Digest("configuration");
        var requestA = CreateRequest(identity, UniqueEnvironment(), configuration, Digest("shared-epoch"));
        var requestB = requestA with { Environment = UniqueEnvironment() };

        (await ledger.AdvanceAsync(requestA)).Status
            .Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);
        (await ledger.AdvanceAsync(requestB)).Status
            .Should().Be(ScheduledGovernanceAuthorityEpochAdvanceStatus.Advanced);

        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        var row = await db.ScheduledGovernanceAuthorityEpochs.AsNoTracking()
            .SingleAsync(epoch => epoch.TenantId == identity.TenantId &&
                                  epoch.OwnerUserId == identity.OwnerUserId &&
                                  epoch.Environment == requestA.Environment);
        const string mutatedEnvironment = "mutated";
        var updateFailure = await Record.ExceptionAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE scheduled_governance_authority_epochs SET environment = {mutatedEnvironment} WHERE id = {row.Id}"));
        var deleteFailure = await Record.ExceptionAsync(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"DELETE FROM scheduled_governance_authority_epochs WHERE id = {row.Id}"));
        var truncateFailure = await Record.ExceptionAsync(() => db.Database.ExecuteSqlRawAsync(
            "TRUNCATE TABLE scheduled_governance_authority_epochs"));

        GetPostgresException(updateFailure)!.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        GetPostgresException(deleteFailure)!.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        GetPostgresException(truncateFailure)!.SqlState.Should().Be(PostgresErrorCodes.RaiseException);
        (await db.ScheduledGovernanceAuthorityEpochs.AsNoTracking().CountAsync(epoch => epoch.Id == row.Id))
            .Should().Be(1);

        var orphanOwner = Guid.NewGuid();
        var orphan = new ScheduledGovernanceAuthorityEpoch
        {
            TenantId = identity.TenantId,
            OwnerUserId = orphanOwner,
            Environment = UniqueEnvironment(),
            ConfigurationDigest = configuration,
            AuthorityEpochDigest = Digest("orphan-epoch"),
            Generation = 1,
            RequestHash = Digest("orphan-request")
        };
        db.ScheduledGovernanceAuthorityEpochs.Add(orphan);
        var orphanFailure = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        GetPostgresException(orphanFailure)!.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [DockerRequiredFact]
    public async Task Database_rejects_zero_identity_null_predecessor_generation_and_overflow_generation()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var identity = await ReadBootstrapIdentityAsync(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runtimeEnvironment = UniqueEnvironment();
        var configurationDigest = Digest("configuration");
        var epochDigest = Digest("epoch");
        var previousDigest = Digest("previous");
        var requestHash = Digest("request");
        var zeroId = Guid.Empty;
        var nonZeroId = Guid.NewGuid();

        var zeroIdentityFailure = await Record.ExceptionAsync(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO scheduled_governance_authority_epochs
                (id, tenant_id, owner_user_id, environment, configuration_digest,
                 authority_epoch_digest, generation, request_hash, created_at_utc)
            VALUES
                ({zeroId}, {identity.TenantId}, {identity.OwnerUserId}, {runtimeEnvironment},
                 {configurationDigest}, {epochDigest}, 1, {requestHash}, NOW())
            """));
        GetPostgresException(zeroIdentityFailure)!.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);

        var nullPredecessorGenerationFailure = await Record.ExceptionAsync(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO scheduled_governance_authority_epochs
                    (id, tenant_id, owner_user_id, environment, configuration_digest,
                     authority_epoch_digest, generation, previous_authority_epoch_digest,
                     previous_generation, request_hash, created_at_utc)
                VALUES
                    ({nonZeroId}, {identity.TenantId}, {identity.OwnerUserId}, {runtimeEnvironment},
                     {configurationDigest}, {epochDigest}, 2, {previousDigest},
                     NULL, {requestHash}, NOW())
                """));
        GetPostgresException(nullPredecessorGenerationFailure)!.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);

        var overflowGenerationFailure = await Record.ExceptionAsync(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO scheduled_governance_authority_epochs
                    (id, tenant_id, owner_user_id, environment, configuration_digest,
                     authority_epoch_digest, generation, previous_authority_epoch_digest,
                     previous_generation, request_hash, created_at_utc)
                VALUES
                    ({Guid.NewGuid()}, {identity.TenantId}, {identity.OwnerUserId}, {UniqueEnvironment()},
                     {configurationDigest}, {Digest("overflow")}, 4097, {previousDigest},
                     4096, {Digest("overflow-request")}, NOW())
                """));
        GetPostgresException(overflowGenerationFailure)!.SqlState
            .Should().Be(PostgresErrorCodes.CheckViolation);
    }

    private static async Task<ScheduledGovernanceAuthorityEpochAdvanceResult> AdvanceInNewScopeAsync(
        MemoryApplicationFactory factory,
        ScheduledGovernanceAuthorityEpochAdvanceRequest request)
    {
        using var scope = factory.Services.CreateScope();
        return await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceAuthorityEpochLedger>()
            .AdvanceAsync(request);
    }

    private static async Task<BootstrapIdentity> ReadBootstrapIdentityAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.AsNoTracking()
            .SingleAsync(row => row.Username == "contract-test-admin");
        return new(user.TenantId, user.Id);
    }

    private static ScheduledGovernanceAuthorityEpochAdvanceRequest CreateRequest(
        BootstrapIdentity identity,
        string runtimeEnvironment,
        string configurationDigest,
        string authorityEpochDigest)
        => new(
            identity.TenantId,
            identity.OwnerUserId,
            runtimeEnvironment,
            configurationDigest,
            authorityEpochDigest);

    private static string UniqueEnvironment()
        => $"test-{Guid.NewGuid():N}";

    private static string Digest(string value)
        => ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(value);

    private static PostgresException? GetPostgresException(Exception? exception)
        => exception switch
        {
            PostgresException postgres => postgres,
            DbUpdateException update => update.GetBaseException() as PostgresException,
            _ => exception?.GetBaseException() as PostgresException
        };

    private sealed record BootstrapIdentity(Guid TenantId, Guid OwnerUserId);
}

using System.Data;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class MemoryAuthoritySupersessionIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    private static readonly TimeSpan ConcurrentAttemptTimeout = TimeSpan.FromSeconds(30);

    [DockerRequiredFact]
    public async Task Applied_migration_allows_same_owner_same_project_chains_for_project_shared_and_user_scopes()
    {
        var owners = await CreateOwnersAsync();
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using (var migrationCommand = connection.CreateCommand())
        {
            migrationCommand.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM schema_migrations
                    WHERE name = '033_authority_supersession.sql');
                """;
            ((bool)(await migrationCommand.ExecuteScalarAsync())!).Should().BeTrue();
        }

        await using (var indexCommand = connection.CreateCommand())
        {
            indexCommand.CommandText = """
                SELECT indexdef
                FROM pg_indexes
                WHERE schemaname = current_schema()
                  AND indexname = 'ix_memory_items_successor_evidence';
                """;
            var indexDefinition = (await indexCommand.ExecuteScalarAsync()) as string;
            indexDefinition.Should().Contain("tenant_id, owner_user_id, project_id, successor_evidence_id");
        }

        var scopedCases = new[]
        {
            (ProjectId: $"authority-project-{Guid.NewGuid():N}", Scope: MemoryScope.Project),
            (ProjectId: ProjectContext.SharedProjectId, Scope: MemoryScope.Project),
            (ProjectId: ProjectContext.UserProjectId, Scope: MemoryScope.User)
        };

        foreach (var (projectId, memoryScope) in scopedCases)
        {
            var chain = await CreateValidChainAsync(
                dbFactory,
                owners.TenantId,
                owners.OwnerUserId,
                projectId,
                memoryScope);

            await using var verifyDb = await dbFactory.CreateDbContextAsync();
            var rows = await verifyDb.MemoryItems
                .AsNoTracking()
                .Where(x => x.Id == chain.Predecessor.Id ||
                            x.Id == chain.Successor.Id ||
                            x.Id == chain.Evidence.Id)
                .ToArrayAsync();

            rows.Should().HaveCount(3);
            rows.Single(x => x.Id == chain.Successor.Id).SupersedesId.Should().Be(chain.Predecessor.Id);
            rows.Single(x => x.Id == chain.Predecessor.Id).SupersededById.Should().Be(chain.Successor.Id);
            rows.Single(x => x.Id == chain.Successor.Id).SuccessorEvidenceId.Should().Be(chain.Evidence.Id);
        }
    }

    [DockerRequiredFact]
    public async Task Same_transaction_can_commit_both_reciprocal_authority_halves()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-same-transaction-{Guid.NewGuid():N}";
        var predecessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "same-transaction-predecessor");
        var successor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "same-transaction-successor");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.AddRange(predecessor, successor);
            await setupDb.SaveChangesAsync();
        }

        await using (var updateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedPredecessor = await updateDb.MemoryItems.SingleAsync(x => x.Id == predecessor.Id);
            var trackedSuccessor = await updateDb.MemoryItems.SingleAsync(x => x.Id == successor.Id);
            trackedPredecessor.AuthorityState = MemoryAuthorityState.Superseded;
            trackedPredecessor.SupersededById = trackedSuccessor.Id;
            trackedPredecessor.ValidUntil = DateTimeOffset.UtcNow.AddMinutes(1);
            trackedSuccessor.SupersedesId = trackedPredecessor.Id;

            // The two updates are intentionally persisted by one SaveChanges
            // transaction; the deferred trigger must observe the final pair.
            await updateDb.SaveChangesAsync();
        }

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var readBackPredecessor = await verifyDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == predecessor.Id);
        var readBackSuccessor = await verifyDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == successor.Id);
        readBackPredecessor.SupersededById.Should().Be(successor.Id);
        readBackSuccessor.SupersedesId.Should().Be(predecessor.Id);
        readBackPredecessor.AuthorityState.Should().Be(MemoryAuthorityState.Superseded);
    }

    [DockerRequiredFact]
    public async Task Committed_one_sided_authority_links_are_rejected_and_rolled_back()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-one-sided-{Guid.NewGuid():N}";
        var predecessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "one-sided-predecessor");
        var successor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "one-sided-successor");
        var reversePredecessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "one-sided-reverse-predecessor");
        var reverseSuccessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "one-sided-reverse-successor");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.AddRange(predecessor, successor, reversePredecessor, reverseSuccessor);
            await setupDb.SaveChangesAsync();
        }

        await using (var successorOnlyDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedSuccessor = await successorOnlyDb.MemoryItems.SingleAsync(x => x.Id == successor.Id);
            trackedSuccessor.SupersedesId = predecessor.Id;
            await AssertPostgresFailureAsync(successorOnlyDb, "23514");
        }

        await using (var predecessorOnlyDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedPredecessor = await predecessorOnlyDb.MemoryItems.SingleAsync(x => x.Id == reversePredecessor.Id);
            trackedPredecessor.SupersededById = reverseSuccessor.Id;
            await AssertPostgresFailureAsync(predecessorOnlyDb, "23514");
        }

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        (await verifyDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == predecessor.Id)).SupersededById.Should().BeNull();
        (await verifyDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == successor.Id)).SupersedesId.Should().BeNull();
        (await verifyDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == reversePredecessor.Id)).SupersededById.Should().BeNull();
        (await verifyDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == reverseSuccessor.Id)).SupersedesId.Should().BeNull();
    }

    [DockerRequiredFact]
    public async Task Mixed_column_fork_is_rejected_without_partial_commit()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-mixed-fork-{Guid.NewGuid():N}";
        var predecessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "mixed-fork-predecessor");
        var firstSuccessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "mixed-fork-first-successor");
        var secondSuccessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "mixed-fork-second-successor");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.AddRange(predecessor, firstSuccessor, secondSuccessor);
            await setupDb.SaveChangesAsync();
        }

        await using (var updateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedPredecessor = await updateDb.MemoryItems.SingleAsync(x => x.Id == predecessor.Id);
            var trackedFirstSuccessor = await updateDb.MemoryItems.SingleAsync(x => x.Id == firstSuccessor.Id);

            // The two claims use different pointer columns, so neither legacy
            // partial unique index sees the fork. The deferred reciprocal
            // validator must reject the final transaction state.
            trackedFirstSuccessor.SupersedesId = trackedPredecessor.Id;
            trackedPredecessor.SupersededById = secondSuccessor.Id;
            await AssertPostgresFailureAsync(updateDb, "23514");
        }

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var rows = await verifyDb.MemoryItems.AsNoTracking()
            .Where(x => x.Id == predecessor.Id || x.Id == firstSuccessor.Id || x.Id == secondSuccessor.Id)
            .ToArrayAsync();
        rows.Should().OnlyContain(x => x.SupersedesId == null && x.SupersededById == null);
    }

    [DockerRequiredFact]
    public async Task Mixed_pointer_cycle_is_rejected_without_partial_commit()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-mixed-cycle-{Guid.NewGuid():N}";
        var first = CreateMemory(owners.TenantId, owners.OwnerUserId, projectId, MemoryScope.Project, "mixed-cycle-first");
        var second = CreateMemory(owners.TenantId, owners.OwnerUserId, projectId, MemoryScope.Project, "mixed-cycle-second");
        var third = CreateMemory(owners.TenantId, owners.OwnerUserId, projectId, MemoryScope.Project, "mixed-cycle-third");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.AddRange(first, second, third);
            await setupDb.SaveChangesAsync();
        }

        await using (var updateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedFirst = await updateDb.MemoryItems.SingleAsync(x => x.Id == first.Id);
            var trackedSecond = await updateDb.MemoryItems.SingleAsync(x => x.Id == second.Id);
            var trackedThird = await updateDb.MemoryItems.SingleAsync(x => x.Id == third.Id);

            // Normalized edges are first -> second -> third -> first, while
            // the claims deliberately alternate superseded_by/supersedes.
            trackedFirst.SupersededById = trackedSecond.Id;
            trackedThird.SupersedesId = trackedSecond.Id;
            trackedFirst.SupersedesId = trackedThird.Id;
            await AssertPostgresFailureAsync(updateDb, "23514");
        }

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var rows = await verifyDb.MemoryItems.AsNoTracking()
            .Where(x => x.Id == first.Id || x.Id == second.Id || x.Id == third.Id)
            .ToArrayAsync();
        rows.Should().OnlyContain(x => x.SupersedesId == null && x.SupersededById == null);
    }

    [DockerRequiredFact]
    public async Task Replacement_scope_guard_rejects_cross_tenant_owner_project_and_memory_scope_links()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-scope-{Guid.NewGuid():N}";
        var predecessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "scope-predecessor");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.Add(predecessor);
            await setupDb.SaveChangesAsync();
        }

        var crossTenant = CreateMemory(
            Guid.NewGuid(),
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "scope-cross-tenant");
        crossTenant.SupersedesId = predecessor.Id;
        await AssertPostgresFailureAsync(dbFactory, crossTenant, "23514");

        var crossOwner = CreateMemory(
            owners.TenantId,
            owners.OtherOwnerUserId,
            projectId,
            MemoryScope.Project,
            "scope-cross-owner");
        crossOwner.SupersedesId = predecessor.Id;
        await AssertPostgresFailureAsync(dbFactory, crossOwner, "23514");

        var crossProject = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            $"authority-other-project-{Guid.NewGuid():N}",
            MemoryScope.Project,
            "scope-cross-project");
        crossProject.SupersedesId = predecessor.Id;
        await AssertPostgresFailureAsync(dbFactory, crossProject, "23514");

        var crossScope = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.User,
            "scope-cross-memory-scope");
        crossScope.SupersedesId = predecessor.Id;
        await AssertPostgresFailureAsync(dbFactory, crossScope, "23514");

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        (await verifyDb.MemoryItems.AsNoTracking().CountAsync(x => x.Id == predecessor.Id)).Should().Be(1);
        (await verifyDb.MemoryItems.AsNoTracking().AnyAsync(x => x.Id == crossTenant.Id)).Should().BeFalse();
        (await verifyDb.MemoryItems.AsNoTracking().AnyAsync(x => x.Id == crossOwner.Id)).Should().BeFalse();
        (await verifyDb.MemoryItems.AsNoTracking().AnyAsync(x => x.Id == crossProject.Id)).Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Successor_evidence_scope_guard_rejects_cross_owner_evidence()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-evidence-{Guid.NewGuid():N}";
        var target = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "evidence-target");
        var evidence = CreateMemory(
            owners.TenantId,
            owners.OtherOwnerUserId,
            projectId,
            MemoryScope.Project,
            "evidence-cross-owner");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.AddRange(target, evidence);
            await setupDb.SaveChangesAsync();
        }

        await using (var updateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedTarget = await updateDb.MemoryItems.SingleAsync(x => x.Id == target.Id);
            trackedTarget.SuccessorEvidenceId = evidence.Id;
            await AssertPostgresFailureAsync(updateDb, "23514");
        }

        var validTarget = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "evidence-update-target");
        var validEvidence = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "evidence-update-source");
        await using (var validSetupDb = await dbFactory.CreateDbContextAsync())
        {
            validSetupDb.MemoryItems.AddRange(validTarget, validEvidence);
            await validSetupDb.SaveChangesAsync();
        }

        await using (var validLinkDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedTarget = await validLinkDb.MemoryItems.SingleAsync(x => x.Id == validTarget.Id);
            trackedTarget.SuccessorEvidenceId = validEvidence.Id;
            await validLinkDb.SaveChangesAsync();
        }

        await using (var evidenceUpdateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedEvidence = await evidenceUpdateDb.MemoryItems.SingleAsync(x => x.Id == validEvidence.Id);
            trackedEvidence.OwnerUserId = owners.OtherOwnerUserId;
            await AssertPostgresFailureAsync(evidenceUpdateDb, "23514");
        }

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var readBack = await verifyDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        readBack.SuccessorEvidenceId.Should().BeNull();
        (await verifyDb.MemoryItems.AsNoTracking().CountAsync(x => x.Id == evidence.Id)).Should().Be(1);
        (await verifyDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == validEvidence.Id)).OwnerUserId
            .Should().Be(owners.OwnerUserId);
    }

    [DockerRequiredFact]
    public async Task Concurrent_duplicate_successors_allow_only_one_commit()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-race-{Guid.NewGuid():N}";
        var predecessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "race-predecessor");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.Add(predecessor);
            await setupDb.SaveChangesAsync();
        }

        var readyCount = 0;
        var bothReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var attemptDeadline = new CancellationTokenSource(ConcurrentAttemptTimeout);
        var successorIds = new[] { Guid.NewGuid(), Guid.NewGuid() };
        var attempts = successorIds
            .Select(successorId => TryInsertConcurrentSuccessorAsync(
                dbFactory,
                owners.TenantId,
                owners.OwnerUserId,
                projectId,
                predecessor.Id,
                successorId,
                bothReady,
                () => Interlocked.Increment(ref readyCount),
                attemptDeadline.Token))
            .ToArray();

        var results = await Task.WhenAll(attempts).WaitAsync(attemptDeadline.Token);
        results.Count(x => x is null).Should().Be(1);
        results.Count(x => x is not null).Should().Be(1);
        var duplicateError = results.Single(x => x is not null)!;
        var duplicatePostgresError = Assert.IsType<PostgresException>(duplicateError.GetBaseException());
        duplicatePostgresError.SqlState.Should().Be("23505");
        duplicatePostgresError.ConstraintName.Should().Be("ux_memory_items_supersedes_id");

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        (await verifyDb.MemoryItems.AsNoTracking().CountAsync(x => x.SupersedesId == predecessor.Id)).Should().Be(1);
        (await verifyDb.MemoryItems.AsNoTracking().CountAsync(x => successorIds.Contains(x.Id))).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Concurrent_reciprocal_replacement_conflict_rolls_back_loser_without_one_sided_chain()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-reciprocal-race-{Guid.NewGuid():N}";
        var predecessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "reciprocal-race-predecessor");
        var firstSuccessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "reciprocal-race-successor-a");
        var secondSuccessor = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "reciprocal-race-successor-b");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.AddRange(predecessor, firstSuccessor, secondSuccessor);
            await setupDb.SaveChangesAsync();
        }

        var readyCount = 0;
        var bothReady = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var attemptDeadline = new CancellationTokenSource(ConcurrentAttemptTimeout);
        var attempts = new[] { firstSuccessor.Id, secondSuccessor.Id }
            .Select(successorId => TryApplyConcurrentReciprocalReplacementAsync(
                dbFactory,
                owners.TenantId,
                owners.OwnerUserId,
                projectId,
                predecessor.Id,
                successorId,
                bothReady,
                () => Interlocked.Increment(ref readyCount),
                attemptDeadline.Token))
            .ToArray();

        var results = await Task.WhenAll(attempts).WaitAsync(attemptDeadline.Token);
        results.Count(x => x is null).Should().Be(1);
        results.Count(x => x is not null).Should().Be(1);
        var failedAttempt = results.Single(x => x is not null)!;
        var postgresException = Assert.IsType<PostgresException>(failedAttempt.GetBaseException());
        // The guard trigger, unique index, or deadlock detector may reject the concurrent loser first.
        postgresException.SqlState.Should().BeOneOf("23514", "23505", "40P01");
        if (postgresException.SqlState == "23514")
        {
            postgresException.MessageText.Should().BeOneOf(
                "memory authority predecessor already has a different successor",
                "memory authority successor already points to a different predecessor",
                "memory authority replacement must have a reciprocal successor link",
                "memory authority replacement must have a reciprocal predecessor link",
                "memory authority replacement chain contains a fork",
                "memory authority replacement chain cannot contain a cycle");
        }
        else if (postgresException.SqlState == "23505")
        {
            postgresException.ConstraintName.Should().BeOneOf(
                "ux_memory_items_supersedes_id",
                "ux_memory_items_superseded_by_id");
        }

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var rows = await verifyDb.MemoryItems
            .AsNoTracking()
            .Where(x => x.Id == predecessor.Id ||
                        x.Id == firstSuccessor.Id ||
                        x.Id == secondSuccessor.Id)
            .ToArrayAsync();

        rows.Should().HaveCount(3);
        var byId = rows.ToDictionary(x => x.Id);
        var linkedSuccessors = rows.Where(x => x.SupersedesId == predecessor.Id).ToArray();
        linkedSuccessors.Should().ContainSingle();
        var winner = linkedSuccessors.Single();
        var loser = rows.Single(x => x.Id != predecessor.Id && x.Id != winner.Id);
        var predecessorReadBack = byId[predecessor.Id];

        predecessorReadBack.AuthorityState.Should().Be(MemoryAuthorityState.Superseded);
        predecessorReadBack.SupersededById.Should().Be(winner.Id);
        winner.AuthorityState.Should().Be(MemoryAuthorityState.Current);
        winner.SupersedesId.Should().Be(predecessor.Id);
        loser.AuthorityState.Should().Be(MemoryAuthorityState.Current);
        loser.SupersedesId.Should().BeNull();
        loser.SupersededById.Should().BeNull();
        loser.ValidUntil.Should().BeNull();

        foreach (var successor in rows.Where(x => x.SupersedesId.HasValue))
        {
            byId[successor.SupersedesId!.Value].SupersededById.Should().Be(successor.Id);
        }

        foreach (var superseded in rows.Where(x => x.SupersededById.HasValue))
        {
            byId[superseded.SupersededById!.Value].SupersedesId.Should().Be(superseded.Id);
        }
    }

    [DockerRequiredFact]
    public async Task Replacement_cycle_is_rejected_and_historical_rows_are_retained()
    {
        var owners = await CreateOwnersAsync();
        var dbFactory = environment.GetFactory().Services.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        var projectId = $"authority-cycle-{Guid.NewGuid():N}";
        var first = CreateMemory(owners.TenantId, owners.OwnerUserId, projectId, MemoryScope.Project, "cycle-first");
        var second = CreateMemory(owners.TenantId, owners.OwnerUserId, projectId, MemoryScope.Project, "cycle-second");
        var historical = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "historical-row",
            MemoryStatus.Archived,
            MemoryAuthorityState.Historical);
        var forwardFirst = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "forward-cycle-first");
        var forwardSecond = CreateMemory(
            owners.TenantId,
            owners.OwnerUserId,
            projectId,
            MemoryScope.Project,
            "forward-cycle-second");

        await using (var setupDb = await dbFactory.CreateDbContextAsync())
        {
            setupDb.MemoryItems.AddRange(first, second, historical, forwardFirst, forwardSecond);
            await setupDb.SaveChangesAsync();
        }

        await using (var firstUpdateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedFirst = await firstUpdateDb.MemoryItems.SingleAsync(x => x.Id == first.Id);
            var trackedSecond = await firstUpdateDb.MemoryItems.SingleAsync(x => x.Id == second.Id);
            trackedFirst.SupersedesId = second.Id;
            trackedSecond.SupersededById = first.Id;
            trackedFirst.AuthorityState = MemoryAuthorityState.Superseded;
            await firstUpdateDb.SaveChangesAsync();
        }

        await using (var cycleUpdateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedSecond = await cycleUpdateDb.MemoryItems.SingleAsync(x => x.Id == second.Id);
            trackedSecond.SupersedesId = first.Id;
            await AssertPostgresFailureAsync(cycleUpdateDb, "23514");
        }

        await using (var forwardFirstUpdateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedFirst = await forwardFirstUpdateDb.MemoryItems.SingleAsync(x => x.Id == forwardFirst.Id);
            var trackedSecond = await forwardFirstUpdateDb.MemoryItems.SingleAsync(x => x.Id == forwardSecond.Id);
            trackedFirst.SupersededById = forwardSecond.Id;
            trackedSecond.SupersedesId = forwardFirst.Id;
            await forwardFirstUpdateDb.SaveChangesAsync();
        }

        await using (var forwardCycleUpdateDb = await dbFactory.CreateDbContextAsync())
        {
            var trackedSecond = await forwardCycleUpdateDb.MemoryItems.SingleAsync(x => x.Id == forwardSecond.Id);
            trackedSecond.SupersededById = forwardFirst.Id;
            await AssertPostgresFailureAsync(forwardCycleUpdateDb, "23514");
        }

        await using var verifyDb = await dbFactory.CreateDbContextAsync();
        var rows = await verifyDb.MemoryItems
            .AsNoTracking()
            .Where(x => x.Id == first.Id || x.Id == second.Id || x.Id == historical.Id ||
                        x.Id == forwardFirst.Id || x.Id == forwardSecond.Id)
            .ToArrayAsync();

        rows.Should().HaveCount(5);
        rows.Single(x => x.Id == first.Id).SupersedesId.Should().Be(second.Id);
        rows.Single(x => x.Id == second.Id).SupersededById.Should().Be(first.Id);
        rows.Single(x => x.Id == forwardFirst.Id).SupersededById.Should().Be(forwardSecond.Id);
        rows.Single(x => x.Id == forwardSecond.Id).SupersedesId.Should().Be(forwardFirst.Id);
        var historicalReadBack = rows.Single(x => x.Id == historical.Id);
        historicalReadBack.Status.Should().Be(MemoryStatus.Archived);
        historicalReadBack.AuthorityState.Should().Be(MemoryAuthorityState.Historical);
    }

    private async Task<(Guid TenantId, Guid OwnerUserId, Guid OtherOwnerUserId)> CreateOwnersAsync()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var primary = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var other = new TenantUser
        {
            Id = Guid.NewGuid(),
            TenantId = primary.TenantId,
            Username = $"authority-owner-{Guid.NewGuid():N}",
            DisplayName = "Authority integration owner",
            Email = "authority-owner@example.test",
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantUsers.Add(other);
        await db.SaveChangesAsync();
        return (primary.TenantId, primary.Id, other.Id);
    }

    private static async Task<(MemoryItem Predecessor, MemoryItem Successor, MemoryItem Evidence)> CreateValidChainAsync(
        IDbContextFactory<MemoryDbContext> dbFactory,
        Guid tenantId,
        Guid ownerUserId,
        string projectId,
        MemoryScope scope)
    {
        var now = DateTimeOffset.UtcNow;
        var predecessor = CreateMemory(tenantId, ownerUserId, projectId, scope, $"valid-predecessor-{Guid.NewGuid():N}");
        predecessor.ValidFrom = now.AddMinutes(-2);
        var evidence = CreateMemory(
            tenantId,
            ownerUserId,
            projectId,
            scope,
            $"valid-evidence-{Guid.NewGuid():N}",
            MemoryStatus.Archived,
            MemoryAuthorityState.Historical);
        evidence.ValidFrom = now.AddMinutes(-2);

        await using var db = await dbFactory.CreateDbContextAsync();
        db.MemoryItems.AddRange(predecessor, evidence);
        await db.SaveChangesAsync();

        var successor = CreateMemory(tenantId, ownerUserId, projectId, scope, $"valid-successor-{Guid.NewGuid():N}");
        successor.SupersedesId = predecessor.Id;
        successor.ValidFrom = now;
        predecessor.AuthorityState = MemoryAuthorityState.Superseded;
        predecessor.SupersededById = successor.Id;
        predecessor.ValidUntil = now.AddMinutes(1);
        successor.SuccessorEvidenceId = evidence.Id;
        successor.SuccessorEvidenceRef = $"test://authority-evidence/{evidence.Id:N}";
        db.MemoryItems.Add(successor);
        await db.SaveChangesAsync();
        return (predecessor, successor, evidence);
    }

    private static async Task AssertPostgresFailureAsync(
        IDbContextFactory<MemoryDbContext> dbFactory,
        MemoryItem item,
        string expectedSqlState)
    {
        await using var db = await dbFactory.CreateDbContextAsync();
        db.MemoryItems.Add(item);
        await AssertPostgresFailureAsync(db, expectedSqlState);
    }

    private static async Task AssertPostgresFailureAsync(MemoryDbContext db, string expectedSqlState)
    {
        // Deferred constraint triggers can surface directly from PostgreSQL
        // during transaction commit, while immediate statement failures are
        // wrapped by EF Core. Assert the durable SQL state at either boundary.
        var exception = await Record.ExceptionAsync(() => db.SaveChangesAsync());
        exception.Should().NotBeNull();
        var postgresException = exception switch
        {
            PostgresException direct => direct,
            DbUpdateException update => Assert.IsType<PostgresException>(update.GetBaseException()),
            _ => throw new Xunit.Sdk.XunitException(
                $"Expected PostgreSQL failure {expectedSqlState}, got {exception.GetType().FullName}.")
        };
        postgresException.SqlState.Should().Be(expectedSqlState);
    }

    private static async Task<Exception?> TryInsertConcurrentSuccessorAsync(
        IDbContextFactory<MemoryDbContext> dbFactory,
        Guid tenantId,
        Guid ownerUserId,
        string projectId,
        Guid predecessorId,
        Guid successorId,
        TaskCompletionSource<bool> bothReady,
        Func<int> signalReady,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var successor = CreateMemory(
                tenantId,
                ownerUserId,
                projectId,
                MemoryScope.Project,
                $"race-successor-{successorId:N}");
            successor.Id = successorId;
            successor.SupersedesId = predecessorId;
            var predecessor = await db.MemoryItems.SingleAsync(x => x.Id == predecessorId, cancellationToken);
            predecessor.SupersededById = successorId;
            predecessor.AuthorityState = MemoryAuthorityState.Superseded;
            db.MemoryItems.Add(successor);
            if (signalReady() == 2)
            {
                bothReady.TrySetResult(true);
            }

            await bothReady.Task.WaitAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            bothReady.TrySetResult(true);
            return exception;
        }
    }

    private static async Task<Exception?> TryApplyConcurrentReciprocalReplacementAsync(
        IDbContextFactory<MemoryDbContext> dbFactory,
        Guid tenantId,
        Guid ownerUserId,
        string projectId,
        Guid predecessorId,
        Guid successorId,
        TaskCompletionSource<bool> bothReady,
        Func<int> signalReady,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var predecessor = await db.MemoryItems.SingleAsync(x =>
                x.Id == predecessorId &&
                x.TenantId == tenantId &&
                x.OwnerUserId == ownerUserId &&
                x.ProjectId == projectId,
                cancellationToken);
            var successor = await db.MemoryItems.SingleAsync(x =>
                x.Id == successorId &&
                x.TenantId == tenantId &&
                x.OwnerUserId == ownerUserId &&
                x.ProjectId == projectId,
                cancellationToken);
            var now = DateTimeOffset.UtcNow;

            predecessor.AuthorityState = MemoryAuthorityState.Superseded;
            predecessor.SupersededById = successor.Id;
            predecessor.ValidFrom ??= predecessor.CreatedAt;
            predecessor.ValidUntil = now.AddMinutes(5);

            successor.AuthorityState = MemoryAuthorityState.Current;
            successor.SupersedesId = predecessor.Id;
            successor.ValidFrom ??= successor.CreatedAt;

            if (signalReady() == 2)
            {
                bothReady.TrySetResult(true);
            }

            await bothReady.Task.WaitAsync(cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            return null;
        }
        catch (Exception exception)
        {
            bothReady.TrySetResult(true);
            return exception;
        }
    }

    private static MemoryItem CreateMemory(
        Guid tenantId,
        Guid ownerUserId,
        string projectId,
        MemoryScope scope,
        string externalKey,
        MemoryStatus status = MemoryStatus.Active,
        MemoryAuthorityState authorityState = MemoryAuthorityState.Current)
        => new()
        {
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            ProjectId = projectId,
            ExternalKey = externalKey,
            Scope = scope,
            MemoryType = MemoryType.Decision,
            Title = externalKey,
            Content = $"Authority integration test: {externalKey}",
            Summary = externalKey,
            Tags = ["authority-integration"],
            SourceType = "test",
            SourceRef = $"test://{externalKey}",
            Importance = .8m,
            Confidence = .9m,
            Version = 1,
            Status = status,
            AuthorityState = authorityState,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}

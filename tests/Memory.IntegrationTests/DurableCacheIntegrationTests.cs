using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;
using Xunit.Abstractions;

namespace Memory.IntegrationTests;

public sealed class DurableCacheIntegrationTests(ContainerTestEnvironment environment, ITestOutputHelper output) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Unicode_scope_revisions_and_invalidation_inside_outer_transaction_must_remain_correct()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var versions = services.GetRequiredService<ICacheVersionStore>();
        var item = NewItem(actor, $"İ-{Guid.NewGuid():N}");
        db.MemoryItems.Add(item);
        await db.SaveChangesAsync();
        var before = await versions.GetVersionStampAsync([item.ProjectId], actor, false, default);
        before.ProjectVersions[item.ProjectId].Should().BeGreaterThan(0);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            item.Title = "Changed in transaction";
            await db.SaveChangesAsync();
            // This used to deadlock on a new connection trying to update the trigger's locked row.
            await versions.IncrementProjectAsync(item.ProjectId, default).WaitAsync(TimeSpan.FromSeconds(5));
            await transaction.CommitAsync();
        }
        var after = await versions.GetVersionStampAsync([item.ProjectId], actor, false, default);
        after.ProjectVersions[item.ProjectId].Should().BeGreaterThan(before.ProjectVersions[item.ProjectId]);
        after.Value.Should().NotBe(before.Value);
    }

    [DockerRequiredFact]
    public async Task Dashboard_unfiltered_catalog_and_details_follow_project_revisions_without_global_bumps()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var prefix = $"catalog-{Guid.NewGuid():N}";
        var a = NewItem(actor, prefix + "-a");
        db.MemoryItems.Add(a);
        await db.SaveChangesAsync();
        var dashboard = services.GetRequiredService<IDashboardQueryService>();
        var request = new MemoryListRequest(ProjectQuery: prefix);
        var first = await dashboard.GetMemoriesAsync(request, default);
        first.TotalCount.Should().Be(1);
        (await dashboard.GetMemoryDetailsAsync(a.Id, default))!.Document.Title.Should().Be(a.Title);
        var b = NewItem(actor, prefix + "-b");
        db.MemoryItems.Add(b);
        await db.SaveChangesAsync();
        (await dashboard.GetMemoriesAsync(request, default)).TotalCount.Should().Be(2);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE memory_items SET title = 'Changed title' WHERE id = {a.Id}");
        (await dashboard.GetMemoryDetailsAsync(a.Id, default))!.Document.Title.Should().Be("Changed title");
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM memory_items WHERE id = {a.Id}");
        (await dashboard.GetMemoryDetailsAsync(a.Id, default)).Should().BeNull();
    }

    [DockerRequiredFact]
    public async Task Version_lookup_measurement_compares_legacy_sequential_reads_with_database_snapshot_and_mget()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseActor(services);
        var durable = services.GetRequiredService<ICacheVersionStore>();
        var legacy = new RedisCacheVersionStore(services.GetRequiredService<IConnectionMultiplexer>(), services.GetRequiredService<IOptions<MemoryOptions>>());
        var projects = Enumerable.Range(0, 10).Select(x => $"version-measure-{x}").ToArray();
        await legacy.GetVersionStampAsync(projects, actor, true, default);
        await durable.GetVersionStampAsync(projects, actor, true, default);
        var timings = new Dictionary<string, double[]>();
        foreach (var (name, store) in new[] { ("legacy-redis-sequential", (ICacheVersionStore)legacy), ("durable-db-and-mget", durable) })
        {
            var samples = new double[32];
            for (var index = 0; index < samples.Length; index++)
            {
                var start = System.Diagnostics.Stopwatch.GetTimestamp();
                var stamp = await store.GetVersionStampAsync(projects, actor, true, default);
                samples[index] = System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                stamp.ProjectVersions.Should().HaveCount(10);
            }
            timings[name] = samples;
        }
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(timings.ToDictionary(x => x.Key, x => new
        {
            Samples = x.Value.Length,
            MedianMs = x.Value.Order().ElementAt(16),
            MaxMs = x.Value.Max(),
            MeanMs = x.Value.Average()
        })));
    }

    [DockerRequiredFact]
    public async Task Tenant_service_cache_must_not_supply_other_owners_results_to_interactive_user()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var otherOwner = new TenantUser
        {
            TenantId = actor.TenantId!.Value,
            Username = $"owner-{Guid.NewGuid():N}",
            DisplayName = "Other owner",
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.TenantUsers.Add(otherOwner);
        await db.SaveChangesAsync();
        var project = $"actor-{Guid.NewGuid():N}";
        var query = $"isolated{Guid.NewGuid():N}";
        var mine = NewItem(actor, project);
        var other = NewItem(actor with { UserId = otherOwner.Id }, project);
        db.MemoryItems.AddRange(mine, other);
        db.MemoryItemChunks.AddRange(new[] { mine, other }.Select(item => new MemoryItemChunk
        {
            MemoryItemId = item.Id,
            ChunkText = query,
            ChunkKind = ChunkKind.Document,
            CreatedAt = DateTimeOffset.UtcNow
        }));
        await db.SaveChangesAsync();
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        var memory = services.GetRequiredService<IMemoryService>();
        accessor.Current = actor with { IsServiceActor = true };
        var request = new MemorySearchRequest(query, ProjectId: project);
        var broad = await memory.SearchAsync(request, default);
        broad.Select(x => x.MemoryId).Should().Contain(mine.Id).And.Contain(other.Id);
        accessor.Current = actor;
        var narrow = await memory.SearchAsync(request, default);
        narrow.Select(x => x.MemoryId).Should().Contain(mine.Id).And.NotContain(other.Id);
        var repeated = await memory.SearchAsync(request, default);
        repeated.Select(x => x.MemoryId).Should().BeEquivalentTo(narrow.Select(x => x.MemoryId));
    }

    [DockerRequiredFact]
    public async Task Committed_mutations_invalidate_only_affected_projects_and_rollback_preserves_revision()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var actor = UseActor(services);
        var revisions = services.GetRequiredService<DurableCacheRevisionStore>();
        var store = services.GetRequiredService<ICacheVersionStore>();
        var a = NewItem(actor, $"A-{Guid.NewGuid():N}");
        var b = NewItem(actor, $"B-{Guid.NewGuid():N}");
        db.MemoryItems.AddRange(a, b);
        await db.SaveChangesAsync();
        var beforeA = await store.GetVersionStampAsync([a.ProjectId], actor, false, default);
        var beforeB = await store.GetVersionStampAsync([b.ProjectId], actor, false, default);

        await using (var connection = await services.GetRequiredService<NpgsqlDataSource>().OpenConnectionAsync())
        {
            await using var transaction = await connection.BeginTransactionAsync();
            await using var command = new NpgsqlCommand("UPDATE memory_items SET content = 'pending' WHERE id = @id", connection, transaction);
            command.Parameters.AddWithValue("id", a.Id);
            await command.ExecuteNonQueryAsync();
            (await store.GetVersionStampAsync([a.ProjectId], actor, false, default)).Value.Should().Be(beforeA.Value,
                "another connection must not observe an uncommitted invalidation");
            await transaction.RollbackAsync();
        }
        (await store.GetVersionStampAsync([a.ProjectId], actor, false, default)).Value.Should().Be(beforeA.Value);

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE memory_items SET content = 'committed' WHERE id = {a.Id}");
        var afterA = await store.GetVersionStampAsync([a.ProjectId], actor, false, default);
        afterA.ProjectVersions[a.ProjectId].Should().BeGreaterThan(beforeA.ProjectVersions[a.ProjectId]);
        (await store.GetVersionStampAsync([b.ProjectId], actor, false, default)).Value.Should().Be(beforeB.Value);
        afterA.GlobalVersion.Should().Be(beforeA.GlobalVersion);

        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE memory_items SET project_id = {b.ProjectId} WHERE id = {a.Id}");
        (await store.GetVersionStampAsync([a.ProjectId], actor, false, default)).Value.Should().NotBe(afterA.Value);
        (await store.GetVersionStampAsync([b.ProjectId], actor, false, default)).Value.Should().NotBe(beforeB.Value);
        var globalBefore = await revisions.ReadScopeAsync("global", default);
        await store.IncrementAsync(default);
        (await revisions.ReadScopeAsync("global", default)).Should().BeGreaterThan(globalBefore);
    }

    [DockerRequiredFact]
    public async Task Vector_ready_and_link_deletion_have_transactional_scope_revisions_and_outbox_evidence()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var actor = UseActor(services);
        var a = NewItem(actor, $"vectors-{Guid.NewGuid():N}");
        var b = NewItem(actor, $"links-{Guid.NewGuid():N}");
        var chunk = new MemoryItemChunk { MemoryItemId = a.Id, ChunkText = "vector test", ChunkKind = ChunkKind.Document };
        db.MemoryItems.AddRange(a, b);
        db.MemoryItemChunks.Add(chunk);
        await db.SaveChangesAsync();
        var revisions = services.GetRequiredService<DurableCacheRevisionStore>();
        var aScope = DurableCacheRevisionStore.ProjectScope(a.ProjectId);
        var bScope = DurableCacheRevisionStore.ProjectScope(b.ProjectId);
        var before = await revisions.ReadScopeAsync(aScope, default);
        var beforeB = await revisions.ReadScopeAsync(bScope, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO memory_chunk_vectors (id, chunk_id, model_key, dimension, status, embedding, created_at) VALUES ({Guid.NewGuid()}, {chunk.Id}, 'test', 3, 'Active', '[1,0,0]'::vector, NOW())");
        (await revisions.ReadScopeAsync(aScope, default)).Should().BeGreaterThan(before);
        (await revisions.ReadScopeAsync(bScope, default)).Should().Be(beforeB);
        var linkId = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO memory_links (id, from_id, to_id, link_type, created_at) VALUES ({linkId}, {a.Id}, {b.Id}, 'Related', NOW())");
        var linked = await revisions.ReadScopeAsync(bScope, default);
        linked.Should().BeGreaterThan(beforeB);
        await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM memory_links WHERE id = {linkId}");
        (await revisions.ReadScopeAsync(bScope, default)).Should().BeGreaterThan(linked);
        await using var command = services.GetRequiredService<NpgsqlDataSource>().CreateCommand(
            "SELECT COUNT(*) FROM audit.authority_outbox_events WHERE category = 'KnowledgeRevision' AND aggregate_id = @scope");
        command.Parameters.AddWithValue("scope", aScope);
        ((long)(await command.ExecuteScalarAsync())!).Should().BeGreaterThan(0);
    }

    [DockerRequiredFact]
    public async Task Working_context_SummaryOnly_separates_primary_project_and_rechecks_primary_authorization()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var a = NewItem(actor, $"context-a-{Guid.NewGuid():N}");
        var b = NewItem(actor, $"context-b-{Guid.NewGuid():N}");
        a.ExternalKey = b.ExternalKey = "system:project-information";
        a.Title = "Project A";
        b.Title = "Project B";
        db.MemoryItems.AddRange(a, b);
        await db.SaveChangesAsync();
        var memory = services.GetRequiredService<IMemoryService>();
        var request = new WorkingContextRequest($"nohits-{Guid.NewGuid():N}", RecentLogLimit: 0, ProjectId: a.ProjectId, QueryMode: MemoryQueryMode.SummaryOnly);
        var first = await memory.BuildWorkingContextAsync(request, default);
        var second = await memory.BuildWorkingContextAsync(request with { ProjectId = b.ProjectId }, default);
        first.ProjectInformation!.ProjectId.Should().Be(a.ProjectId);
        second.ProjectInformation!.ProjectId.Should().Be(b.ProjectId);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE memory_items SET content = 'Updated A' WHERE id = {a.Id}");
        var updated = await memory.BuildWorkingContextAsync(request, default);
        updated.ProjectInformation!.Description.Should().Be("Updated A");
        services.GetRequiredService<IRequestActorAccessor>().Current = actor with { AllowedProjectIds = [b.ProjectId] };
        var denied = () => memory.BuildWorkingContextAsync(request, default);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [DockerRequiredFact]
    public async Task Malformed_or_null_payload_is_not_a_hit_but_empty_array_is_valid()
    {
        var services = environment.GetFactory().Services;
        var options = services.GetRequiredService<IOptions<MemoryOptions>>();
        var redis = services.GetRequiredService<IConnectionMultiplexer>();
        var telemetry = new RedisCacheTelemetry();
        var cache = new RedisObjectCache(redis, options, telemetry);
        var key = $"payload-{Guid.NewGuid():N}";
        foreach (var payload in new[] { "null", "{broken" })
        {
            await redis.GetDatabase().StringSetAsync($"memory:{options.Value.Namespace}:{key}", payload);
            (await cache.GetAsync<string[]>(key, "test", default)).Hit.Should().BeFalse();
        }
        telemetry.GetSnapshot().Hits.Should().Be(0);
        telemetry.GetSnapshot().InvalidPayloads.Should().Be(2);
        await redis.GetDatabase().StringSetAsync($"memory:{options.Value.Namespace}:{key}", "{}");
        (await cache.GetAsync<WorkingContextResult>(key, "test", default)).Hit.Should().BeFalse();
        telemetry.GetSnapshot().InvalidPayloads.Should().Be(3);
        await redis.GetDatabase().StringSetAsync($"memory:{options.Value.Namespace}:{key}", "[null]");
        (await cache.GetAsync<IReadOnlyList<MemorySearchHit>>(key, "test", default)).Hit.Should().BeFalse();
        await redis.GetDatabase().StringSetAsync($"memory:{options.Value.Namespace}:{key}", """{"facts":[null],"decisions":[],"episodes":[],"artifacts":[],"recentLogs":[],"userPreferences":[],"suggestedTests":[],"citations":[]}""");
        (await cache.GetAsync<WorkingContextResult>(key, "test", default)).Hit.Should().BeFalse();
        telemetry.GetSnapshot().InvalidPayloads.Should().Be(5);
        await redis.GetDatabase().StringSetAsync($"memory:{options.Value.Namespace}:{key}", "[]");
        var empty = await cache.GetAsync<string[]>(key, "test", default);
        empty.Hit.Should().BeTrue();
        empty.Value.Should().BeEmpty();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        var read = () => cache.GetAsync<string[]>(key, "test", cancelled.Token);
        await read.Should().ThrowAsync<OperationCanceledException>();
        telemetry.GetSnapshot().Errors.Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Old_Redis_revision_restore_cannot_revalidate_a_precommit_result()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseActor(services);
        var store = services.GetRequiredService<ICacheVersionStore>();
        var item = NewItem(actor, $"restore-{Guid.NewGuid():N}");
        var db = services.GetRequiredService<MemoryDbContext>();
        db.MemoryItems.Add(item);
        await db.SaveChangesAsync();
        var oldStamp = await store.GetVersionStampAsync([item.ProjectId], actor, false, default);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE memory_items SET content = 'new state' WHERE id = {item.Id}");
        var ns = services.GetRequiredService<IOptions<MemoryOptions>>().Value.Namespace;
        var redis = services.GetRequiredService<IConnectionMultiplexer>().GetDatabase();
        await redis.StringSetAsync($"memory:{ns}:version:project:{RedisCacheKeyBuilder.Hash(item.ProjectId)}", 1);
        await redis.StringSetAsync($"memory:{ns}:version:global", 1);
        (await store.GetVersionStampAsync([item.ProjectId], actor, false, default)).Value.Should().NotBe(oldStamp.Value);
    }

    private static ContextHubRequestActor UseActor(IServiceProvider services)
    {
        var user = services.GetRequiredService<MemoryDbContext>().TenantUsers.Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.PreferencesRead, SecurityScopes.PreferencesWrite], [], true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }

    private static MemoryItem NewItem(ContextHubRequestActor actor, string project) => new()
    {
        TenantId = actor.TenantId,
        OwnerUserId = actor.UserId,
        ProjectId = project,
        ExternalKey = Guid.NewGuid().ToString("N"),
        Title = "Cache test",
        Content = "Original",
        Summary = "Cache test",
        Scope = MemoryScope.Project,
        MemoryType = MemoryType.Fact,
        Status = MemoryStatus.Active,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };
}

using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using System.Reflection;

namespace Memory.IntegrationTests;

public sealed class DashboardGraphRefreshIntegrationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Replicas_should_deduplicate_reject_expired_publish_and_recover_after_restart()
    {
        var services = environment.GetFactory().Services;
        var source = services.GetRequiredService<NpgsqlDataSource>();
        var options = Options.Create(new MemoryOptions { Namespace = "graph-test-" + Guid.NewGuid().ToString("N") });
        var first = new NpgsqlDashboardGraphRefreshCoordinator(source, options);
        var second = new NpgsqlDashboardGraphRefreshCoordinator(source, options);
        var results = await Task.WhenAll(first.TryAcquireAsync(false, default), second.TryAcquireAsync(false, default));
        results.Count(result => result is not null).Should().Be(1);
        var oldLease = results.Single(result => result is not null)!;
        await using (var expire = source.CreateCommand("UPDATE dashboard_graph_projection SET lease_expires_at=clock_timestamp()-interval '1 second' WHERE scope=@scope"))
        {
            expire.Parameters.AddWithValue("scope", options.Value.Namespace + ":project-identity-v1:global");
            await expire.ExecuteNonQueryAsync();
        }
        var newLease = (await second.TryAcquireAsync(false, default))!;
        newLease.Generation.Should().BeGreaterThan(oldLease.Generation);
        (await first.PublishAsync(oldLease, EmptySnapshot(), default)).Should().BeFalse();
        (await second.PublishAsync(newLease, EmptySnapshot(), default)).Should().BeTrue();
        (await first.TryAcquireAsync(false, default)).Should().BeNull("unchanged revisions should not trigger any rebuild");
        var restarted = new NpgsqlDashboardGraphRefreshCoordinator(source, options);
        (await restarted.ReadSnapshotAsync(default)).Should().NotBeNull();
        var status = await restarted.GetStatusAsync(default);
        status.FullBuilds.Should().Be(1);
        status.Skipped.Should().Be(1);
        status.Deduplicated.Should().Be(1);
        await using (var overdue = source.CreateCommand("UPDATE dashboard_graph_projection SET last_full_at=clock_timestamp()-interval '25 hours' WHERE scope=@scope"))
        {
            overdue.Parameters.AddWithValue("scope", options.Value.Namespace + ":project-identity-v1:global");
            await overdue.ExecuteNonQueryAsync();
        }
        var full = (await restarted.TryAcquireAsync(false, default))!;
        full.Full.Should().BeTrue();
        await restarted.FailAsync(full, "BuildFailed", default);
    }

    [DockerRequiredFact]
    public async Task Mutation_during_build_should_reject_publication_and_remain_dirty_for_retry()
    {
        var source = environment.GetFactory().Services.GetRequiredService<NpgsqlDataSource>();
        var options = Options.Create(new MemoryOptions { Namespace = "graph-race-" + Guid.NewGuid().ToString("N") });
        var coordinator = new NpgsqlDashboardGraphRefreshCoordinator(source, options);
        var lease = (await coordinator.TryAcquireAsync(false, default))!;
        var project = "graph-race-" + Guid.NewGuid().ToString("N");
        await using (var mutation = source.CreateCommand("SELECT bump_cache_scope_revision(@scope)"))
        {
            mutation.Parameters.AddWithValue("scope", "project:" + project);
            await mutation.ExecuteNonQueryAsync();
        }
        (await coordinator.PublishAsync(lease, EmptySnapshot(), default)).Should().BeFalse();
        (await coordinator.ReadSnapshotAsync(default)).Should().BeNull();
        var retry = (await coordinator.TryAcquireAsync(false, default))!;
        retry.DirtyProjects.Should().Contain(ProjectContext.IdentityKey(project));
        (await coordinator.PublishAsync(retry, EmptySnapshot(), default)).Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task Incremental_builder_should_skip_unchanged_sources_including_sources_without_edges()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var prefix = "graph-incremental-" + Guid.NewGuid().ToString("N");
        var itemA = new MemoryItem { ProjectId = prefix + "a", Title = "uniquealpha", Summary = "uniquealpha", Status = MemoryStatus.Active, Importance = 1m, Confidence = 1m };
        var itemB = new MemoryItem { ProjectId = prefix + "b", Title = "uniquebeta", Summary = "uniquebeta", Status = MemoryStatus.Active, Importance = 1m, Confidence = 1m };
        db.MemoryItems.AddRange(itemA, itemB);
        await db.SaveChangesAsync(default);
        var search = DispatchProxy.Create<IMemoryService, SearchSpy>();
        var spy = (SearchSpy)search;
        var builder = new DashboardMemoryGraphIndexBuilder(db, search);
        var initial = await builder.BuildAsync(default);
        initial.SimilaritySourceIds.Should().Contain(itemA.Id).And.Contain(itemB.Id);
        spy.Projects.Clear();
        await builder.BuildIncrementalAsync(initial, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { itemA.ProjectId }, default);
        spy.Projects.Should().Contain(itemA.ProjectId).And.NotContain(itemB.ProjectId);
        spy.Projects.Clear();
        await builder.BuildIncrementalAsync(initial, new HashSet<string>(), default);
        spy.Projects.Should().BeEmpty();
    }

    private static DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload> EmptySnapshot() => new(
        DashboardSnapshotKeys.MemoryGraphIndex, DateTimeOffset.UtcNow, 15, DateTimeOffset.UtcNow.AddSeconds(60), "",
        new(new MemoryGraphResult([], [], new MemoryGraphStatsResult(0, 0, 0, false)), []));

    [DockerRequiredFact]
    public async Task Incremental_builder_should_reconcile_the_global_top_source_boundary()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var prefix = "graph-boundary-" + Guid.NewGuid().ToString("N");
        var baselineTime = new DateTimeOffset(2100, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var items = Enumerable.Range(0, 161).Select(index => new MemoryItem
        {
            ProjectId = prefix + index,
            Title = "node" + index,
            Summary = "unique" + index,
            Importance = 1m,
            Confidence = 1m,
            Status = MemoryStatus.Active,
            UpdatedAt = baselineTime.AddMinutes(-index),
            CreatedAt = DateTimeOffset.UtcNow
        }).ToArray();
        db.MemoryItems.AddRange(items);
        await db.SaveChangesAsync(default);
        var search = DispatchProxy.Create<IMemoryService, SearchSpy>();
        var spy = (SearchSpy)search;
        var builder = new DashboardMemoryGraphIndexBuilder(db, search);
        var previous = await builder.BuildAsync(default);
        previous.SimilaritySourceIds.Should().HaveCount(160).And.NotContain(items[160].Id);
        items[160].UpdatedAt = baselineTime.AddDays(1);
        await db.SaveChangesAsync(default);
        spy.Projects.Clear();
        var next = await builder.BuildIncrementalAsync(previous, new HashSet<string> { items[160].ProjectId }, default);
        next.SimilaritySourceIds.Should().HaveCount(160).And.Contain(items[160].Id).And.NotContain(items[159].Id);
        spy.Projects.Should().ContainSingle().Which.Should().Be(items[160].ProjectId);
        next.Graph.Edges.Where(edge => edge.EdgeType == "similar").Should().NotContain(edge => edge.FromId == items[159].Id);
        db.MemoryItems.RemoveRange(items);
        await db.SaveChangesAsync(default);
    }

    public class SearchSpy : DispatchProxy
    {
        public List<string> Projects { get; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name != nameof(IMemoryService.SearchAsync)) throw new NotSupportedException();
            Projects.Add(((MemorySearchRequest)args![0]!).ProjectId!);
            return Task.FromResult<IReadOnlyList<MemorySearchHit>>([]);
        }
    }
}

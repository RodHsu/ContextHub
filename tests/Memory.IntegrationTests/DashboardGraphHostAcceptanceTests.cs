using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class DashboardGraphHostAcceptanceTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Two_authenticated_application_hosts_and_worker_scope_should_publish_one_global_generation()
    {
        var configuration = environment.GetFactory().Services.GetRequiredService<IConfiguration>();
        var postgres = configuration.GetConnectionString("Postgres")!;
        var redis = configuration.GetConnectionString("Redis")!;
        var graphNamespace = "graph-host-" + Guid.NewGuid().ToString("N");
        var barrier = new BuildBarrier();
        await using var firstBase = new MemoryApplicationFactory(postgres, redis);
        await using var secondBase = new MemoryApplicationFactory(postgres, redis);
        await using var first = CreateHost(firstBase, graphNamespace, barrier);
        await using var second = CreateHost(secondBase, graphNamespace, barrier);
        using var firstClient = first.CreateClient();
        using var secondClient = second.CreateClient();
        using var setupScope = first.Services.CreateScope();
        var db = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owners = await db.TenantUsers.AsNoTracking().Take(1).ToArrayAsync();
        var owner = owners.Single();
        var projectA = "graph-host-a-" + Guid.NewGuid().ToString("N");
        var projectB = "graph-host-b-" + Guid.NewGuid().ToString("N");
        var itemA = new MemoryItem { TenantId = owner.TenantId, OwnerUserId = owner.Id, ProjectId = projectA, Title = "Graph host A", Importance = 1m, Confidence = 1m };
        var itemB = new MemoryItem { ProjectId = projectB, Title = "Graph global B", Importance = 1m, Confidence = 1m };
        db.MemoryItems.AddRange(itemA, itemB);
        await db.SaveChangesAsync(default);

        var firstRequest = firstClient.PostAsync("/api/memories/graph/index/refresh", null);
        try
        {
            await barrier.Started.Task.WaitAsync(TimeSpan.FromSeconds(20));
            using var competingResponse = await secondClient.PostAsync("/api/memories/graph/index/refresh", null);
            competingResponse.EnsureSuccessStatusCode();
            (await competingResponse.Content.ReadFromJsonAsync<DashboardMemoryGraphIndexRefreshResult>())!.Trigger.Should().Be("skipped");
            using var workerScope = second.Services.CreateScope();
            var workerActor = workerScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            workerActor.Current = new ContextHubRequestActor(owner.TenantId, owner.Id, "job-owner", TenantUserRole.Member,
                [SecurityScopes.MemoryRead], [projectA], true, IsServiceActor: true);
            var caller = workerActor.Current;
            var workerResult = await workerScope.ServiceProvider.GetRequiredService<IDashboardMemoryGraphIndexRefreshService>()
                .RefreshAsync("event", 15, default);
            workerResult.Trigger.Should().Be("skipped");
            workerActor.Current.Should().BeSameAs(caller);
        }
        finally
        {
            barrier.Release.TrySetResult();
        }
        using var publicationResponse = await firstRequest;
        publicationResponse.EnsureSuccessStatusCode();
        var publication = (await publicationResponse.Content.ReadFromJsonAsync<DashboardMemoryGraphIndexRefreshResult>())!;
        publication.Trigger.Should().Be("manual");
        barrier.BuildCount.Should().Be(1);
        barrier.Actor!.IsServiceActor.Should().BeTrue();
        barrier.Actor.HasUser.Should().BeFalse();
        barrier.Actor.Scopes.Should().Equal(SecurityScopes.MemoryRead);
        var firstSnapshot = await first.Services.GetRequiredService<IDashboardSnapshotStore>()
            .GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(DashboardSnapshotKeys.MemoryGraphIndex, default);
        var secondSnapshot = await second.Services.GetRequiredService<IDashboardSnapshotStore>()
            .GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(DashboardSnapshotKeys.MemoryGraphIndex, default);
        firstSnapshot!.Payload.Graph.Nodes.Select(node => node.Id).Should().Contain(itemA.Id).And.Contain(itemB.Id);
        secondSnapshot.Should().BeEquivalentTo(firstSnapshot);
        var status = await second.Services.GetRequiredService<IDashboardGraphRefreshCoordinator>().GetStatusAsync(default);
        status.Generation.Should().Be(1);
        status.FullBuilds.Should().Be(1);
        status.Deduplicated.Should().Be(2);
        status.LeaseActive.Should().BeFalse();
        status.LastError.Should().BeEmpty();

        // A later worker trigger belongs to one tenant/project, but it must still publish
        // the same global projection after a different project's revision changes.
        itemB.Title = "Graph global B updated";
        await db.SaveChangesAsync(default);
        using var followupScope = second.Services.CreateScope();
        var followupActor = followupScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        followupActor.Current = new ContextHubRequestActor(owner.TenantId, owner.Id, "job-owner", TenantUserRole.Member,
            [SecurityScopes.MemoryRead], [projectA], true, IsServiceActor: true);
        var previousActor = followupActor.Current;
        var followupResult = await followupScope.ServiceProvider.GetRequiredService<IDashboardMemoryGraphIndexRefreshService>()
            .RefreshAsync("event", 15, default);
        followupResult.Trigger.Should().Be("event");
        followupActor.Current.Should().BeSameAs(previousActor);
        barrier.Actor!.HasUser.Should().BeFalse();
        barrier.Actor.IsServiceActor.Should().BeTrue();
        var refreshed = await first.Services.GetRequiredService<IDashboardSnapshotStore>()
            .GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(DashboardSnapshotKeys.MemoryGraphIndex, default);
        refreshed!.Payload.Graph.Nodes.Single(node => node.Id == itemB.Id).Title.Should().Be("Graph global B updated");
        var followupStatus = await first.Services.GetRequiredService<IDashboardGraphRefreshCoordinator>().GetStatusAsync(default);
        followupStatus.Generation.Should().Be(2);
        followupStatus.IncrementalBuilds.Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Database_unavailability_should_fail_closed_without_reading_old_Redis_graph_then_recover()
    {
        var services = environment.GetFactory().Services;
        var configuration = services.GetRequiredService<IConfiguration>();
        var settings = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("Postgres"))
        {
            Host = "127.0.0.1",
            Port = 1,
            Timeout = 1,
            CommandTimeout = 1,
            Pooling = false
        };
        await using var unavailable = NpgsqlDataSource.Create(settings.ConnectionString);
        var options = Options.Create(new MemoryOptions { Namespace = "graph-db-fault-" + Guid.NewGuid().ToString("N") });
        var healthy = new NpgsqlDashboardGraphRefreshCoordinator(services.GetRequiredService<NpgsqlDataSource>(), options);
        var lease = (await healthy.TryAcquireAsync(false, default))!;
        var envelope = new DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>(DashboardSnapshotKeys.MemoryGraphIndex,
            DateTimeOffset.UtcNow, 15, DateTimeOffset.UtcNow.AddSeconds(60), "", new(new([], [], new(0, 0, 0, false)), []));
        (await healthy.PublishAsync(lease, envelope, default)).Should().BeTrue();
        var redisStore = services.GetRequiredService<RedisDashboardSnapshotStore>();
        await redisStore.SetAsync(envelope, default);
        var brokenCoordinator = new NpgsqlDashboardGraphRefreshCoordinator(unavailable, options);
        var protectedStore = new CoordinatedDashboardSnapshotStore(redisStore, brokenCoordinator);
        await protectedStore.Invoking(store => store.GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(DashboardSnapshotKeys.MemoryGraphIndex, default))
            .Should().ThrowAsync<NpgsqlException>();
        await brokenCoordinator.Invoking(coordinator => coordinator.TryAcquireAsync(false, default)).Should().ThrowAsync<NpgsqlException>();
        var restoredStore = new CoordinatedDashboardSnapshotStore(redisStore, healthy);
        (await restoredStore.GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(DashboardSnapshotKeys.MemoryGraphIndex, default))!
            .CapturedAtUtc.Should().Be(envelope.CapturedAtUtc);
        var retry = (await healthy.TryAcquireAsync(true, default))!;
        (await healthy.PublishAsync(retry, envelope, default)).Should().BeTrue();
        (await healthy.GetStatusAsync(default)).FullBuilds.Should().Be(2);
    }

    private static WebApplicationFactory<Program> CreateHost(MemoryApplicationFactory factory, string graphNamespace, BuildBarrier barrier)
        => factory.WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            // Trigger the collector explicitly to keep overlap reproducible; all other application
            // middleware, authentication, database, cache and projection registrations are real.
            var collector = services.Single(descriptor => descriptor.ServiceType == typeof(IHostedService)
                && descriptor.ImplementationType == typeof(DashboardSnapshotCollectorHostedService));
            services.Remove(collector);
            services.PostConfigure<MemoryOptions>(options => options.Namespace = graphNamespace);
            services.RemoveAll<IDashboardMemoryGraphIndexBuilder>();
            services.AddScoped<IDashboardMemoryGraphIndexBuilder>(provider => new BlockingBuilder(
                new DashboardMemoryGraphIndexBuilder(provider.GetRequiredService<IApplicationDbContext>(), provider.GetRequiredService<IMemoryService>()),
                provider.GetRequiredService<IRequestActorAccessor>(), barrier));
        }));

    private sealed class BuildBarrier
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int BuildCount;
        public ContextHubRequestActor? Actor;
    }
    private sealed class BlockingBuilder(DashboardMemoryGraphIndexBuilder inner, IRequestActorAccessor actor, BuildBarrier barrier) : IIncrementalDashboardMemoryGraphIndexBuilder
    {
        public async Task<DashboardMemoryGraphIndexSnapshotPayload> BuildAsync(CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref barrier.BuildCount);
            barrier.Actor = actor.Current;
            barrier.Started.TrySetResult();
            await barrier.Release.Task.WaitAsync(cancellationToken);
            return await inner.BuildAsync(cancellationToken);
        }
        public Task<DashboardMemoryGraphIndexSnapshotPayload> BuildIncrementalAsync(DashboardMemoryGraphIndexSnapshotPayload previous, IReadOnlySet<string> dirtyProjects, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref barrier.BuildCount);
            barrier.Actor = actor.Current;
            return inner.BuildIncrementalAsync(previous, dirtyProjects, cancellationToken);
        }
    }
}

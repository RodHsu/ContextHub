using System.Reflection;
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

namespace Memory.IntegrationTests;

public sealed class RedisCacheOperationIntegrationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Signals_transport_failure_should_skip_remaining_cache_attempts_but_keep_reading_current_db_revisions()
        => await VerifySignalFailureAsync(new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Synthetic cache transport fault."));

    [DockerRequiredFact]
    public async Task Signals_timeout_should_skip_remaining_cache_attempts_but_keep_reading_current_db_revisions()
        => await VerifySignalFailureAsync(new RedisTimeoutException("Synthetic cache timeout.", CommandStatus.Unknown));

    private async Task VerifySignalFailureAsync(Exception signalFailure)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var actor = UseActor(services);
        var project = "operation-revision-" + Guid.NewGuid().ToString("N");
        var item = new MemoryItem { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ProjectId = project, Title = "before" };
        db.MemoryItems.Add(item); await db.SaveChangesAsync();
        var transport = new RedisProbe { SignalFailure = signalFailure };
        var options = services.GetRequiredService<IOptions<MemoryOptions>>();
        var versions = new RedisCacheVersionStore(transport.Connection, options, services.GetRequiredService<DurableCacheRevisionStore>());
        var telemetry = new RedisCacheTelemetry();
        var objects = new RedisObjectCache(transport.Connection, options, telemetry);
        using (RedisCacheOperationScope.BeginOrJoin())
        {
            var first = await versions.GetVersionStampAsync([project], actor, false, default);
            RedisCacheOperationScope.IsBypassed.Should().BeTrue();
            transport.SignalReads.Should().Be(1);
            first.Value.Should().Contain("signals-unavailable=");
            (await objects.GetAsync<string>("a", "search-final", default)).Hit.Should().BeFalse();
            await objects.SetAsync("a", "search-final", "value", TimeSpan.FromMinutes(1), default);
            transport.ObjectCalls.Should().Be(0);
            telemetry.GetSnapshot().Bypasses.Should().Be(2);
            telemetry.GetSnapshot().Errors.Should().Be(0, "the failed MGET is not an object-cache attempt");
            item.Title = "after"; await db.SaveChangesAsync();
            var second = await versions.GetVersionStampAsync([project], actor, false, default);
            second.ProjectVersions[project].Should().BeGreaterThan(first.ProjectVersions[project]);
            second.Value.Should().NotBe(first.Value);
            var third = await versions.GetVersionStampAsync([project], actor, false, default);
            third.Value.Should().NotBe(second.Value, "unknown signals must not address an existing reusable key");
            transport.SignalReads.Should().Be(1);
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => versions.GetVersionStampAsync([project], actor, false, cancelled.Token));
            // Disposable-cache bypass cannot silence mutation invalidations or job signals.
            await versions.IncrementProjectAsync(project, default);
            await versions.PublishJobSignalAsync(Guid.NewGuid(), default);
            transport.Invalidations.Should().Be(1); transport.JobPublishes.Should().Be(1);
            // Legacy readers still require Redis authority; no durable-only shortcut is invented.
            var legacy = new RedisCacheVersionStore(transport.Connection, options);
            await legacy.GetVersionStampAsync([project], actor, false, default);
            transport.ObjectCalls.Should().BeGreaterThan(0);
        }
        RedisCacheOperationScope.IsBypassed.Should().BeFalse();
        transport.FailSignals = false;
        using (RedisCacheOperationScope.BeginOrJoin())
        {
            var recovered = await versions.GetVersionStampAsync([project], actor, false, default);
            recovered.Value.Should().Contain(";signals=");
            RedisCacheOperationScope.IsBypassed.Should().BeFalse();
            transport.SignalReads.Should().Be(2);
        }
    }

    [DockerRequiredFact]
    public async Task Signals_fault_and_caller_cancellation_race_should_not_poison_operation()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseActor(services);
        foreach (var failure in new Exception[] { new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Synthetic connection fault."), new RedisTimeoutException("Synthetic timeout.", CommandStatus.Unknown) })
        {
            using var cancellation = new CancellationTokenSource();
            var transport = new RedisProbe { SignalFailure = failure, BeforeSignals = cancellation.Cancel };
            var versions = new RedisCacheVersionStore(transport.Connection, services.GetRequiredService<IOptions<MemoryOptions>>(), services.GetRequiredService<DurableCacheRevisionStore>());
            using (RedisCacheOperationScope.BeginOrJoin())
            {
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => versions.GetVersionStampAsync(["cancel-race"], actor, false, cancellation.Token));
                transport.SignalReads.Should().Be(1);
                RedisCacheOperationScope.IsBypassed.Should().BeFalse();
            }
        }
    }

    [DockerRequiredFact]
    public async Task Actual_graph_builder_should_share_one_failure_scope_and_next_refresh_should_retry()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var actor = UseActor(services);
        var projects = Enumerable.Range(0, 4).Select(_ => "operation-graph-" + Guid.NewGuid().ToString("N")).ToArray();
        var items = projects.Select((project, i) => new MemoryItem { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ProjectId = project, Title = "Graph operation node " + i, Importance = 1m, Confidence = 1m }).ToArray();
        db.MemoryItems.AddRange(items); await db.SaveChangesAsync();
        var transport = new RedisProbe();
        var options = services.GetRequiredService<IOptions<MemoryOptions>>();
        var versions = new RedisCacheVersionStore(transport.Connection, options, services.GetRequiredService<DurableCacheRevisionStore>());
        var telemetry = new RedisCacheTelemetry();
        var objects = new RedisObjectCache(transport.Connection, options, telemetry);
        var memory = ActivatorUtilities.CreateInstance<MemoryService>(services, versions, objects);
        var builder = new DashboardMemoryGraphIndexBuilder(db, memory);
        var coordinator = new NpgsqlDashboardGraphRefreshCoordinator(services.GetRequiredService<NpgsqlDataSource>(), Options.Create(new MemoryOptions { Namespace = "operation-graph-" + Guid.NewGuid().ToString("N") }));
        var refresh = new DashboardMemoryGraphIndexRefreshService(builder, services.GetRequiredService<IDashboardSnapshotStore>(), services.GetRequiredService<IInstanceBehaviorSettingsAccessor>(), TimeProvider.System, coordinator, services.GetRequiredService<IRequestActorAccessor>());
        var result = await refresh.RefreshAsync("manual", 15, default);
        result.Trigger.Should().Be("manual"); result.NodeCount.Should().BeGreaterThanOrEqualTo(4);
        transport.SignalReads.Should().Be(1, "all graph source searches join the same refresh operation");
        transport.ObjectCalls.Should().Be(0);
        telemetry.GetSnapshot().Bypasses.Should().BeGreaterThanOrEqualTo(24);
        telemetry.GetSnapshot().Errors.Should().Be(0);
        services.GetRequiredService<IRequestActorAccessor>().Current.Should().Be(actor);
        RedisCacheOperationScope.IsBypassed.Should().BeFalse();
        var snapshot = (await coordinator.ReadSnapshotAsync(default))!;
        foreach (var item in items) snapshot.Payload.Graph.Nodes.Single(n => n.Id == item.Id).Title.Should().Be(item.Title);
        var status = await coordinator.GetStatusAsync(default);
        status.Generation.Should().Be(1); status.FullBuilds.Should().Be(1); status.LeaseActive.Should().BeFalse(); status.Failures.Should().Be(0);
        var deniedActor = actor with { AllowedProjectIds = ["unrelated"] };
        services.GetRequiredService<IRequestActorAccessor>().Current = deniedActor;
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => memory.SearchAsync(new MemorySearchRequest("Graph", ProjectId: projects[0], UseSummaryLayer: false), default));
        transport.SignalReads.Should().Be(1, "authorization must run before cache or signals");
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        transport.FailSignals = false;
        await refresh.RefreshAsync("manual", 15, default);
        transport.SignalReads.Should().BeGreaterThan(1);
        transport.ObjectCalls.Should().BeGreaterThan(0);
        RedisCacheOperationScope.IsBypassed.Should().BeFalse();
    }

    private static ContextHubRequestActor UseActor(IServiceProvider services)
    {
        var user = services.GetRequiredService<MemoryDbContext>().TenantUsers.Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role, [SecurityScopes.MemoryRead], [], true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor; return actor;
    }

    private sealed class RedisProbe
    {
        public IConnectionMultiplexer Connection { get; }
        public bool FailSignals { get; set; } = true;
        public Exception SignalFailure { get; set; } = new RedisConnectionException(ConnectionFailureType.UnableToConnect, "Synthetic cache transport fault.");
        public Action? BeforeSignals { get; set; }
        public int SignalReads { get; private set; }
        public int ObjectCalls { get; private set; }
        public int Invalidations { get; private set; }
        public int JobPublishes { get; private set; }
        public RedisProbe()
        {
            var database = DispatchProxy.Create<IDatabase, InterfaceProxy>();
            ((InterfaceProxy)database).Call = (method, args) =>
            {
                if (method.Name == "StringGetAsync" && args![0] is RedisKey[] keys)
                {
                    SignalReads++;
                    BeforeSignals?.Invoke();
                    return FailSignals ? Task.FromException<RedisValue[]>(SignalFailure) : Task.FromResult(new RedisValue[keys.Length]);
                }
                if (method.Name == "StringGetAsync") { ObjectCalls++; return Task.FromResult(RedisValue.Null); }
                if (method.Name == "StringSetAsync") { ObjectCalls++; return Task.FromResult(true); }
                if (method.Name == "StringIncrementAsync") { Invalidations++; return Task.FromResult(2L); }
                throw new NotSupportedException(method.Name);
            };
            var subscriber = DispatchProxy.Create<ISubscriber, InterfaceProxy>();
            ((InterfaceProxy)subscriber).Call = (method, _) =>
            {
                if (method.Name == "PublishAsync") { JobPublishes++; return Task.FromResult(1L); }
                throw new NotSupportedException(method.Name);
            };
            Connection = DispatchProxy.Create<IConnectionMultiplexer, InterfaceProxy>();
            ((InterfaceProxy)Connection).Call = (method, _) => method.Name switch
            {
                "GetDatabase" => database,
                "GetSubscriber" => subscriber,
                _ => throw new NotSupportedException(method.Name)
            };
        }
    }
    public class InterfaceProxy : DispatchProxy
    {
        public Func<MethodInfo, object?[]?, object?> Call { get; set; } = null!;
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) => Call(targetMethod!, args);
    }
}

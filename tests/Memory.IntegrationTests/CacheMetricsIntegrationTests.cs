using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using System.Diagnostics;
using Xunit.Abstractions;

namespace Memory.IntegrationTests;

public sealed class CacheMetricsIntegrationTests(ContainerTestEnvironment environment, ITestOutputHelper output) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Parallel_cold_and_warm_searches_should_preserve_results_and_report_singleflight_opportunity()
    {
        var provider = environment.GetFactory().Services;
        using var initialScope = provider.CreateScope();
        var user = initialScope.ServiceProvider.GetRequiredService<MemoryDbContext>().TenantUsers.AsNoTracking().Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role, [SecurityScopes.MemoryRead], [], true);
        var telemetry = provider.GetRequiredService<IRedisCacheTelemetry>();
        var origins = new OriginRecorder();
        var request = new MemorySearchRequest($"cache-cold-load-{Guid.NewGuid():N}", ProjectId: $"cache-load-{Guid.NewGuid():N}",
            QueryMode: MemoryQueryMode.CurrentOnly, UseSummaryLayer: false,
            Telemetry: new RetrievalTelemetryContext("cache-load-fixture", "internal", "local concurrency probe", false));
        async Task<double[]> RunAsync()
        {
            return await Task.WhenAll(Enumerable.Range(0, 100).Select(async _ =>
            {
                using var scope = provider.CreateScope();
                scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
                var started = Stopwatch.GetTimestamp();
                var result = await ActivatorUtilities.CreateInstance<MemoryService>(scope.ServiceProvider, origins).SearchAsync(request, CancellationToken.None);
                result.Should().BeEmpty();
                return Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            }));
        }
        var before = telemetry.GetSnapshot().Kinds.GetValueOrDefault("search-final", new(0, 0, 0, 0, 0));
        var cold = await RunAsync();
        var afterCold = telemetry.GetSnapshot().Kinds["search-final"];
        var coldOrigins = origins.Count(CacheMetricKinds.OriginSearch);
        var warm = await RunAsync();
        var afterWarm = telemetry.GetSnapshot().Kinds["search-final"];
        (afterCold.Sets - before.Sets).Should().BeInRange(1, 100);
        (afterWarm.Hits - afterCold.Hits).Should().Be(100);
        (afterWarm.Sets - afterCold.Sets).Should().Be(0);
        coldOrigins.Should().Be(afterCold.Misses - before.Misses);
        origins.Count(CacheMetricKinds.OriginSearch).Should().Be(coldOrigins, "warm hits must not enter the origin path");
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Environment = "Local testcontainer; deterministic embedding; empty result; full service calls, not HTTP or production",
            ParallelRequests = 100,
            Cold = new { Sets = afterCold.Sets - before.Sets, OriginAttempts = coldOrigins, Misses = afterCold.Misses - before.Misses, Hits = afterCold.Hits - before.Hits, P50Ms = Percentile(cold, .50), P95Ms = Percentile(cold, .95), P99Ms = Percentile(cold, .99) },
            Warm = new { Sets = afterWarm.Sets - afterCold.Sets, Hits = afterWarm.Hits - afterCold.Hits, P50Ms = Percentile(warm, .50), P95Ms = Percentile(warm, .95), P99Ms = Percentile(warm, .99) },
            Decision = "Cold duplicate sets identify a potential singleflight benefit; deployment needs representative same-key concurrency before adding distributed coordination."
        }));
    }

    [DockerRequiredFact]
    public async Task Graph_aggregation_should_remove_per_request_database_writes_under_local_load()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var factory = new CountingFactory(services.GetRequiredService<IDbContextFactory<MemoryDbContext>>());
        var clock = new TestClock(CacheMetricsBuffer.TruncateMinute(DateTimeOffset.UtcNow));
        var actor = new RequestActorAccessor { Current = ContextHubRequestActor.Unrestricted };
        var buffer = new CacheMetricsBuffer(clock, instanceId: "metric-load-fixture");
        var baseline = new DatabaseRetrievalTelemetryService(factory, clock, actor);
        var aggregated = new DatabaseRetrievalTelemetryService(factory, clock, actor, buffer);
        var projectId = $"cache-metric-load-{Guid.NewGuid():N}";
        var request = new RetrievalTelemetryWriteRequest(projectId, "dashboard", "dashboard.memory_graph_index", "local load fixture", "fixture query", "CurrentOnly", [], false, 10, true, 3, 15, true, "", "{}", "", "", []);
        await baseline.RecordAsync(request with { ProjectId = "metric-warmup" }, CancellationToken.None);
        await aggregated.RecordAsync(request, CancellationToken.None);
        // Warm-up uses a separate buffer so measured counts can be reconciled exactly.
        buffer = new CacheMetricsBuffer(clock, instanceId: "metric-load-fixture");
        aggregated = new DatabaseRetrievalTelemetryService(factory, clock, actor, buffer);
        var rawTimings = new double[64];
        var callsBefore = factory.Calls;
        for (var i = 0; i < rawTimings.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await baseline.RecordAsync(request, CancellationToken.None);
            rawTimings[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var rawCalls = factory.Calls - callsBefore;
        var aggregateTimings = new double[100_000];
        callsBefore = factory.Calls;
        for (var i = 0; i < aggregateTimings.Length; i++)
        {
            var started = Stopwatch.GetTimestamp();
            await aggregated.RecordAsync(request, CancellationToken.None);
            aggregateTimings[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        }
        var aggregateCalls = factory.Calls - callsBefore;
        rawCalls.Should().Be(64);
        aggregateCalls.Should().Be(0);
        buffer.Snapshot().Should().HaveCount(3);
        buffer.Snapshot().Single(x => x.Kind == "final-result").Hits.Should().Be(100_000);
        await using var db = await factory.CreateDbContextAsync(CancellationToken.None);
        (await db.RetrievalEvents.CountAsync(x => x.ProjectId == projectId)).Should().Be(64);
        var store = new DatabaseCacheMetricsStore(services.GetRequiredService<NpgsqlDataSource>());
        var flushStart = Stopwatch.GetTimestamp();
        await store.WriteAsync(buffer, buffer.Snapshot(), clock.Now, false, CancellationToken.None);
        var flushMs = Stopwatch.GetElapsedTime(flushStart).TotalMilliseconds;
        output.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
        {
            Environment = "Local PostgreSQL testcontainer; not production or full request latency",
            Baseline = new { Samples = 64, DatabaseContexts = rawCalls, RawRows = 64, TotalMs = rawTimings.Sum(), P50Ms = Percentile(rawTimings, .50), P95Ms = Percentile(rawTimings, .95), P99Ms = Percentile(rawTimings, .99) },
            Aggregate = new { Samples = 100_000, DatabaseContextsOnRequestPath = aggregateCalls, RawRows = 0, TotalMs = aggregateTimings.Sum(), P50Ms = Percentile(aggregateTimings, .50), P95Ms = Percentile(aggregateTimings, .95), P99Ms = Percentile(aggregateTimings, .99), DurableBuckets = 3, FlushTransactions = 1, FlushMs = flushMs },
            Comparison = "Raw sample and aggregate load are different sample sizes. Values describe telemetry-only local cost, not production p95."
        }));
    }

    [DockerRequiredFact]
    public async Task Durable_windows_should_weight_replicas_preserve_restarts_and_ignore_duplicate_or_late_snapshots()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var source = scope.ServiceProvider.GetRequiredService<NpgsqlDataSource>();
        var store = new DatabaseCacheMetricsStore(source);
        var clock = new TestClock(CacheMetricsBuffer.TruncateMinute(DateTimeOffset.UtcNow).AddMinutes(-2));
        var first = new CacheMetricsBuffer(clock, instanceId: "metric-test-replica-a");
        var second = new CacheMetricsBuffer(clock, instanceId: "metric-test-replica-b");
        first.Record("other", CacheMetricOutcome.Hit, "other");
        var late = first.Snapshot();
        for (var i = 0; i < 99; i++) first.Record("other", CacheMetricOutcome.Miss, "other");
        for (var i = 0; i < 9; i++) second.Record("other", CacheMetricOutcome.Hit, "other");
        second.Record("other", CacheMetricOutcome.Miss, "other");
        await store.WriteAsync(first, first.Snapshot(), clock.Now, true, CancellationToken.None);
        await store.WriteAsync(first, first.Snapshot(), clock.Now, true, CancellationToken.None);
        await store.WriteAsync(first, late, clock.Now, false, CancellationToken.None);
        await store.WriteAsync(second, second.Snapshot(), clock.Now, false, CancellationToken.None);
        var restart = new CacheMetricsBuffer(clock, instanceId: first.InstanceId);
        restart.Record("other", CacheMetricOutcome.Hit, "other");
        restart.Record("other", CacheMetricOutcome.Hit, "other");
        await store.WriteAsync(restart, restart.Snapshot(), clock.Now, true, CancellationToken.None);
        clock.Now = clock.Now.AddMinutes(2);
        var actor = new RequestActorAccessor
        {
            Current = new(Guid.NewGuid(), Guid.NewGuid(), "cache-metrics-admin", TenantUserRole.Admin, [], [], true)
        };
        var query = new DatabaseCacheMetricsQueryService(source, actor, clock);
        foreach (var period in new[] { "24H", "3D", "7D", "14D", "30D" })
        {
            var result = await query.GetAsync(period, CancellationToken.None);
            result.Supported.Should().BeTrue();
            result.CoverageStatus.Should().Be("Partial");
            var aggregate = result.Series.Single(x => x.Kind == "other" && x.TrafficClass == "other");
            aggregate.Hits.Should().Be(12);
            aggregate.Misses.Should().Be(100);
            aggregate.HitRate.Should().BeApproximately(100d * 12 / 112, 0.00001);
            result.Instances.Count(x => x.InstanceId == first.InstanceId).Should().Be(2);
            result.Instances.Single(x => x.BootId == first.BootId).UncleanShutdown.Should().BeFalse();
            result.Instances.Single(x => x.BootId == second.BootId).UncleanShutdown.Should().BeTrue();
            result.Instances.Single(x => x.BootId == second.BootId).ObservedMinutes.Should().BeLessThan(2);
        }
        actor.Current = actor.Current with { Role = TenantUserRole.Member };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => query.GetAsync("24H", CancellationToken.None));
        actor.Current = actor.Current with { IsAuthenticated = false };
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => query.GetAsync("24H", CancellationToken.None));
    }

    private sealed class TestClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static double Percentile(double[] values, double percentile)
        => values.Order().ElementAt(Math.Clamp((int)Math.Ceiling(values.Length * percentile) - 1, 0, values.Length - 1));

    private sealed class OriginRecorder : ICacheMetricsRecorder
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<string, long> _counts = new();
        public long Count(string kind) => _counts.GetValueOrDefault(kind);
        public void Record(string kind, CacheMetricOutcome outcome, string? trafficClass = null, double durationMs = 0)
        {
            if (outcome == CacheMetricOutcome.Observation && CacheMetricKinds.IsOrigin(kind))
                _counts.AddOrUpdate(kind, 1, (_, count) => count + 1);
        }
    }

    private sealed class CountingFactory(IDbContextFactory<MemoryDbContext> inner) : IDbContextFactory<MemoryDbContext>
    {
        public int Calls { get; private set; }
        public MemoryDbContext CreateDbContext() { Calls++; return inner.CreateDbContext(); }
        public Task<MemoryDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            Calls++;
            return inner.CreateDbContextAsync(cancellationToken);
        }
    }
}

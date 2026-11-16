using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Memory.UnitTests;

public sealed class CacheMetricsTests
{
    [Fact]
    public void Origin_observations_should_remain_distinct_from_cache_lookup_outcomes_and_latency()
    {
        var buffer = new CacheMetricsBuffer(new MutableClock());
        using var traffic = CacheMetricsTrafficScope.Begin("interactive");
        foreach (var kind in new[] { CacheMetricKinds.OriginSearch, CacheMetricKinds.OriginContext, CacheMetricKinds.OriginSemantic, CacheMetricKinds.OriginEmbedding })
            buffer.Record(kind, CacheMetricOutcome.Observation);
        buffer.Record("search-final", CacheMetricOutcome.Error);
        buffer.Record("search-final", CacheMetricOutcome.Bypass);
        var origins = buffer.Snapshot().Where(x => CacheMetricKinds.IsOrigin(x.Kind)).ToArray();
        origins.Should().HaveCount(4);
        origins.Should().OnlyContain(x => x.Observations == 1 && x.Hits == 0 && x.Misses == 0 && x.DurationMs == 0 && x.TrafficClass == "interactive");
        CacheMetricKinds.IsOrigin("query-compute").Should().BeFalse();
    }

    [Fact]
    public void Rates_should_use_lookup_counts_and_leave_zero_samples_unknown()
    {
        new CacheMetricsSeriesResult("final-result", "interactive", 1, 99, 500, 5, 2, 0, 0).HitRate.Should().Be(1);
        new CacheMetricsSeriesResult("final-result", "interactive", 0, 0, 500, 5, 2, 0, 0).HitRate.Should().BeNull();
        new CacheMetricsSeriesResult("server-request", "application", 0, 0, 0, 0, 0, 0, 120, 3).Samples.Should().Be(3);
    }

    [Fact]
    public void Flush_acknowledgement_should_not_lose_concurrent_events_or_reset_open_minute()
    {
        var clock = new MutableClock();
        var buffer = new CacheMetricsBuffer(clock);
        buffer.Record("search-final", CacheMetricOutcome.Hit);
        var snapshot = buffer.Snapshot();
        buffer.Record("search-final", CacheMetricOutcome.Miss);
        buffer.Acknowledge(snapshot);
        buffer.Snapshot().Single().Should().Match<CacheMetricBucket>(x => x.Hits == 1 && x.Misses == 1);
        buffer.Acknowledge(buffer.Snapshot());
        buffer.PendingBuckets.Should().Be(0);
        buffer.Record("search-final", CacheMetricOutcome.Hit);
        buffer.Snapshot().Single().Hits.Should().Be(2);
        buffer.Acknowledge(buffer.Snapshot());
        clock.Now = clock.Now.AddMinutes(1);
        buffer.Snapshot().Should().BeEmpty();
        buffer.Record("search-final", CacheMetricOutcome.Hit);
        buffer.Snapshot().Single().Hits.Should().Be(1);
    }

    [Fact]
    public async Task A_committed_but_unacknowledged_flush_should_retry_without_double_counting()
    {
        var clock = new MutableClock();
        var buffer = new CacheMetricsBuffer(clock);
        var store = new CommitThenFailStore();
        var service = new CacheMetricsFlushService(buffer, store, clock, NullLogger<CacheMetricsFlushService>.Instance);
        buffer.Record("search-final", CacheMetricOutcome.Hit);
        await Assert.ThrowsAsync<IOException>(() => service.FlushAsync(false, CancellationToken.None));
        buffer.PendingBuckets.Should().Be(1);
        await service.FlushAsync(false, CancellationToken.None);
        buffer.PendingBuckets.Should().Be(0);
        store.Rows.Single().Value.Hits.Should().Be(1);
        buffer.Record("search-final", CacheMetricOutcome.Miss);
        await service.FlushAsync(false, CancellationToken.None);
        store.Rows.Single().Value.Misses.Should().Be(1);
    }

    [Fact]
    public void Outage_buffer_should_be_bounded_and_report_drops_without_erasing_retry_totals()
    {
        var clock = new MutableClock();
        var buffer = new CacheMetricsBuffer(clock, 2);
        buffer.Record("search-final", CacheMetricOutcome.Hit);
        clock.Now = clock.Now.AddMinutes(1);
        buffer.Record("search-final", CacheMetricOutcome.Hit);
        clock.Now = clock.Now.AddMinutes(1);
        for (var i = 0; i < 100; i++) buffer.Record("search-final", CacheMetricOutcome.Hit);
        buffer.Snapshot().Count.Should().Be(2);
        buffer.DroppedSamples.Should().Be(100);
        buffer.Acknowledge(buffer.Snapshot());
        buffer.Record("search-final", CacheMetricOutcome.Miss);
        buffer.Snapshot().Single().Misses.Should().Be(1);
    }

    [Fact]
    public void Parallel_load_should_retain_exact_totals_and_bounded_label_cardinality()
    {
        var buffer = new CacheMetricsBuffer(new MutableClock());
        Parallel.For(0, 100_000, i => buffer.Record("search-final", i % 4 == 0 ? CacheMetricOutcome.Miss : CacheMetricOutcome.Hit, "interactive"));
        var row = buffer.Snapshot().Single();
        row.Hits.Should().Be(75_000);
        row.Misses.Should().Be(25_000);
        for (var i = 0; i < 1000; i++) buffer.Record($"query-secret-{i}", CacheMetricOutcome.Hit, $"user-{i}");
        buffer.Snapshot().Should().HaveCount(2).And.Contain(x => x.Kind == "other" && x.TrafficClass == "other" && x.Hits == 1000);
    }

    [Fact]
    public void Restart_should_separate_boots_and_clock_rollback_should_not_recreate_old_totals()
    {
        var clock = new MutableClock();
        var first = new CacheMetricsBuffer(clock, instanceId: "replica-a");
        var restarted = new CacheMetricsBuffer(clock, instanceId: "replica-a");
        first.BootId.Should().NotBe(restarted.BootId);
        first.Record("search-final", CacheMetricOutcome.Hit);
        first.Acknowledge(first.Snapshot());
        clock.Now = clock.Now.AddMinutes(1);
        first.Snapshot().Should().BeEmpty();
        clock.Now = clock.Now.AddMinutes(-2);
        first.Record("search-final", CacheMetricOutcome.Hit);
        first.Record("search-final", CacheMetricOutcome.Hit);
        first.Snapshot().Single().Hits.Should().Be(2);
    }

    [Fact]
    public async Task Graph_telemetry_should_aggregate_without_opening_a_database_context()
    {
        var clock = new MutableClock();
        var buffer = new CacheMetricsBuffer(clock);
        var service = new DatabaseRetrievalTelemetryService(new RejectingFactory(), clock, new RequestActorAccessor(), buffer);
        var request = new RetrievalTelemetryWriteRequest("ContextHub", "dashboard", "dashboard.memory_graph_index", "refresh", "private query", "CurrentOnly", [], false, 10, true, 3, 15, true, "", "{}", "", "", []);
        await service.RecordAsync(request, CancellationToken.None);
        buffer.Snapshot().Should().Contain(x => x.Kind == "final-result" && x.TrafficClass == "graph-background" && x.Hits == 1);
        buffer.Snapshot().Should().Contain(x => x.Kind == "query-compute" && x.DurationMs == 15 && x.Observations == 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(request with { Channel = "mcp", EntryPoint = "memory_search" }, CancellationToken.None));
    }

    [Fact]
    public async Task Traffic_scope_should_flow_across_await_and_restore_parent()
    {
        using (CacheMetricsTrafficScope.Begin("graph-background"))
        {
            await Task.Yield();
            CacheMetricsTrafficScope.Current.Should().Be("graph-background");
            using (CacheMetricsTrafficScope.Begin("interactive")) CacheMetricsTrafficScope.Current.Should().Be("interactive");
            CacheMetricsTrafficScope.Current.Should().Be("graph-background");
        }
        CacheMetricsTrafficScope.Current.Should().Be("application");
    }

    private sealed class MutableClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 10, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class RejectingFactory : IDbContextFactory<MemoryDbContext>
    {
        public MemoryDbContext CreateDbContext() => throw new InvalidOperationException("No database allowed for graph telemetry.");
    }

    private sealed class CommitThenFailStore : ICacheMetricsStore
    {
        private bool _fail = true;
        public Dictionary<(Guid, DateTimeOffset, string, string), CacheMetricBucket> Rows { get; } = [];
        public Task WriteAsync(CacheMetricsBuffer buffer, IReadOnlyList<CacheMetricBucket> snapshots, DateTimeOffset now, bool stopped, CancellationToken cancellationToken)
        {
            foreach (var row in snapshots)
            {
                var key = (row.BootId, row.BucketStartUtc, row.Kind, row.TrafficClass);
                if (!Rows.TryGetValue(key, out var prior) || prior.Revision < row.Revision) Rows[key] = row;
            }
            if (_fail) { _fail = false; throw new IOException("Connection lost after commit."); }
            return Task.CompletedTask;
        }
    }
}

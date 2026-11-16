using System.Text.Json;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Memory.UnitTests;

public sealed class RequestArrivalObservationTests
{
    [Fact]
    public void Capture_is_disabled_unless_enabled_with_a_valid_fixed_end()
    {
        var time = new ObservationClock();
        foreach (var settings in new[]
        {
            new RequestArrivalObservationOptions(),
            new RequestArrivalObservationOptions { Enabled = true },
            new RequestArrivalObservationOptions { Enabled = true, UntilUtc = "not-a-date" },
            new RequestArrivalObservationOptions { Enabled = true, UntilUtc = time.GetUtcNow().ToString("O") },
            new RequestArrivalObservationOptions { Enabled = true, UntilUtc = time.GetUtcNow().AddHours(25).ToString("O") }
        })
            Assert.Null(new RequestArrivalObservations(time, Options.Create(settings)).Begin("http", "memory_search"));
    }

    [Fact]
    public void Overlap_has_exact_start_times_and_monotonic_durations_and_distinct_layers()
    {
        var time = new ObservationClock();
        var buffer = Buffer(time);
        var http = buffer.Begin("http", "http-mcp")!;
        time.Advance(TimeSpan.FromMilliseconds(20));
        var tool = buffer.Begin("mcp-tool", "memory_search", http.Sequence)!;
        var second = buffer.Begin("http", "rest-working-context")!;
        Assert.Equal(2, buffer.Snapshot().Coverage.HttpActive);
        Assert.Equal(1, buffer.Snapshot().Coverage.ToolActive);
        time.Advance(TimeSpan.FromMilliseconds(30));
        tool.Complete("success");
        second.Complete("cancelled");
        http.Complete("success");
        http.Complete("error");
        var snapshot = buffer.Snapshot();
        Assert.Equal(2, snapshot.Coverage.HttpCompleted);
        Assert.Equal(1, snapshot.Coverage.ToolCompleted);
        Assert.Equal(0, snapshot.Coverage.HttpActive);
        Assert.Equal(0, snapshot.Coverage.ToolActive);
        var toolRow = Assert.Single(snapshot.Samples, row => row.Layer == "mcp-tool");
        Assert.Equal(http.Sequence, toolRow.ParentSequence);
        Assert.Equal(time.InitialUtc.AddMilliseconds(20), toolRow.StartedAtUtc);
        Assert.Equal(30d, toolRow.DurationMs);
        Assert.Equal(50d, Assert.Single(snapshot.Samples, row => row.Sequence == http.Sequence).DurationMs);
    }

    [Fact]
    public void Concurrent_arrivals_produce_one_ordered_active_count_per_layer()
    {
        var buffer = Buffer(new ObservationClock());
        var leases = new System.Collections.Concurrent.ConcurrentBag<RequestArrivalLease>();
        Parallel.For(0, 64, _ => leases.Add(buffer.Begin("http", "memory_search")!));
        Assert.Equal(Enumerable.Range(1, 64), buffer.Snapshot().Samples.Select(row => row.ActiveAtStart).Order());
        Parallel.ForEach(leases, lease => lease.Complete("success"));
        Assert.Equal(64, buffer.Snapshot().Coverage.HttpCompleted);
        Assert.Equal(0, buffer.Snapshot().Coverage.HttpActive);
    }

    [Fact]
    public void Old_flush_ack_cannot_remove_a_completion_that_arrived_during_the_write()
    {
        var buffer = Buffer(new ObservationClock());
        var lease = buffer.Begin("http", "memory_search")!;
        var start = buffer.Snapshot();
        lease.Complete("success");
        buffer.Acknowledge(start.Samples);
        Assert.Equal(2, Assert.Single(buffer.Snapshot().Samples).Revision);
        buffer.Acknowledge(buffer.Snapshot().Samples);
        Assert.Empty(buffer.Snapshot().Samples);
    }

    [Fact]
    public void Capacity_and_total_limit_preserve_loss_counters_without_blocking_requests()
    {
        var buffer = Buffer(new ObservationClock(), capacity: 1, limit: 1);
        var admitted = buffer.Begin("http", "memory_search")!;
        var dropped = buffer.Begin("http", "memory_search")!;
        admitted.Complete("success");
        dropped.Complete("success");
        buffer.Acknowledge(buffer.Snapshot().Samples);
        buffer.Begin("http", "memory_search")!.Complete("success");
        var coverage = buffer.Snapshot().Coverage;
        Assert.Equal(1, coverage.Admitted);
        Assert.Equal(2, coverage.DroppedStarts);
        Assert.Equal(3, coverage.HttpStarted);
        Assert.Equal(3, coverage.HttpCompleted);
        Assert.Equal(0, coverage.HttpActive);
    }

    [Fact]
    public void A_persisted_start_with_dropped_completion_remains_explicitly_partial()
    {
        var buffer = Buffer(new ObservationClock(), capacity: 1);
        var first = buffer.Begin("http", "memory_search")!;
        buffer.Acknowledge(buffer.Snapshot().Samples);
        var second = buffer.Begin("http", "memory_search")!;
        first.Complete("success");
        Assert.Equal(1, buffer.Snapshot().Coverage.DroppedFinishes);
        second.Complete("error");
        Assert.Equal(0, buffer.Snapshot().Coverage.HttpActive);
        Assert.Equal(1, buffer.Snapshot().Coverage.Pending);
    }

    [Fact]
    public void Closed_window_never_reopens_after_a_wall_clock_correction_and_inflight_can_finish()
    {
        var time = new ObservationClock();
        var buffer = Buffer(time);
        var first = buffer.Begin("http", "memory_search")!;
        time.Advance(TimeSpan.FromMinutes(1));
        Assert.Null(buffer.Begin("http", "memory_search"));
        time.Utc = time.InitialUtc.AddHours(-1);
        Assert.Null(buffer.Begin("http", "memory_search"));
        first.Complete("success");
        Assert.True(buffer.Snapshot().Coverage.WindowClosed);
        Assert.Equal(60_000d, Assert.Single(buffer.Snapshot().Samples).DurationMs);
    }

    [Fact]
    public void A_backward_wall_clock_cannot_extend_the_fixed_monotonic_capture_window()
    {
        var time = new ObservationClock();
        var buffer = Buffer(time);
        time.Advance(TimeSpan.FromMinutes(1));
        time.Utc = time.InitialUtc.AddHours(-1);
        Assert.Null(buffer.Begin("http", "memory_search"));
        Assert.True(buffer.Snapshot().Coverage.WindowClosed);
    }

    [Fact]
    public void Arbitrary_operation_values_are_never_persisted()
    {
        var buffer = Buffer(new ObservationClock());
        buffer.Begin("SECRET_SENTINEL", "/private/SECRET_SENTINEL?token=SECRET_SENTINEL")!.Complete("SECRET_SENTINEL");
        var row = Assert.Single(buffer.Snapshot().Samples);
        Assert.Equal("other", row.Operation);
        Assert.Equal("http", row.Layer);
        Assert.Equal("error", row.Outcome);
        Assert.DoesNotContain("SECRET_SENTINEL", JsonSerializer.Serialize(buffer.Snapshot()));
    }

    private static RequestArrivalObservations Buffer(ObservationClock time, int capacity = 2048, int limit = 100_000)
        => new(time, Options.Create(new RequestArrivalObservationOptions
        {
            Enabled = true,
            UntilUtc = time.GetUtcNow().AddMinutes(1).ToString("O"),
            Capacity = capacity,
            MaximumSamples = limit
        }));

    private sealed class ObservationClock : TimeProvider
    {
        private long _ticks;
        public DateTimeOffset InitialUtc { get; } = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Utc { get; set; } = new(2026, 10, 7, 0, 0, 0, TimeSpan.Zero);
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => _ticks;
        public void Advance(TimeSpan value) { _ticks += value.Ticks; Utc += value; }
    }
}

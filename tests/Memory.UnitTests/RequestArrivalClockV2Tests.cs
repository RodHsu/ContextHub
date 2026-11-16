using Memory.Infrastructure;
using Microsoft.Extensions.Options;
using System.Text.Json;

namespace Memory.UnitTests;

public sealed class RequestArrivalClockV2Tests
{
    [Theory]
    [InlineData(false, 1)]
    [InlineData(false, 2)]
    [InlineData(true, 1)]
    [InlineData(true, 3)]
    public void Disabled_v1_and_unknown_schema_never_read_native_clock(bool enabled, int schema)
    {
        var native = new Clock { Throw = true };
        var buffer = Buffer(new WallClock(), native, enabled: enabled, schema: schema);
        buffer.Begin("http", "http-mcp")?.Complete("success");
        buffer.CaptureClockAnchor();
        buffer.Snapshot();
        Assert.Equal(0, native.Reads);
        Assert.Equal(0, native.DomainReads);
    }

    [Fact]
    public void Raw_brackets_and_original_fields_have_distinct_unmodified_semantics()
    {
        var wall = new WallClock();
        var native = new Clock();
        var buffer = Buffer(wall, native);
        var lease = buffer.Begin("http", "http-mcp", classification: new("http-mcp", "POST", "le1k"))!;
        var start = Assert.Single(buffer.Snapshot().Samples);
        wall.Advance(TimeSpan.FromMilliseconds(1030));
        native.Advance(1_000_000_000);
        lease.Complete("success");
        var row = Assert.Single(buffer.Snapshot().Samples);
        Assert.Equal(1030, row.DurationMs);
        Assert.Equal(wall.Initial, start.StartedAtUtc);
        Assert.Equal(wall.Utc, row.FinishedAtUtc);
        Assert.True(row.RawStartedBeforeNs <= row.RawStartedAfterNs);
        Assert.True(row.RawStartedAfterNs <= row.RawFinishedBeforeNs);
        Assert.True(row.RawFinishedBeforeNs <= row.RawFinishedAfterNs);
        Assert.Equal("http-mcp", row.RequestFamily);
        Assert.Equal("POST", row.RequestMethod);
        Assert.Equal("le1k", row.PayloadSizeBucket);
    }

    [Fact]
    public void V1_never_persists_shape_or_raw_fields()
    {
        var buffer = Buffer(new WallClock(), new Clock(), schema: 1);
        buffer.Begin("http", "http-mcp", classification: new("http-mcp", "POST", "le1k"))!.Complete("success");
        var row = Assert.Single(buffer.Snapshot().Samples);
        Assert.Null(row.RequestFamily);
        Assert.Null(row.RequestMethod);
        Assert.Null(row.PayloadSizeBucket);
        Assert.Null(row.RawStartedBeforeNs);
        Assert.Null(buffer.Snapshot().Coverage.Clock);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unavailable_or_throwing_native_clock_closes_admission_without_throwing(bool throws)
    {
        var native = new Clock { Available = false, Throw = throws };
        var buffer = Buffer(new WallClock(), native);
        Assert.Null(buffer.Begin("http", "http-mcp"));
        var coverage = buffer.Snapshot().Coverage;
        Assert.True(coverage.WindowClosed);
        Assert.Equal(1, coverage.Clock!.Failures);
        Assert.Equal(throws ? "CLOCK_READ_FAILED" : "CLOCK_DOMAIN_UNAVAILABLE", coverage.Clock.InvalidReason);
        Assert.Equal(0, coverage.HttpStarted);
    }

    [Fact]
    public void Native_failure_after_start_keeps_actual_completion_and_explicitly_missing_raw_evidence()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        var lease = buffer.Begin("http", "http-mcp")!;
        native.Throw = true;
        lease.Complete("success");
        lease.Complete("error");
        Assert.Null(buffer.Begin("http", "http-mcp"));
        var snapshot = buffer.Snapshot();
        Assert.Equal(1, snapshot.Coverage.HttpCompleted);
        Assert.Equal(0, snapshot.Coverage.HttpActive);
        Assert.Equal("CLOCK_READ_FAILED", snapshot.Coverage.Clock!.InvalidReason);
        Assert.Null(Assert.Single(snapshot.Samples).RawFinishedAfterNs);
        Assert.Equal("success", Assert.Single(snapshot.Samples).Outcome);
    }

    [Fact]
    public void Changed_domain_is_rejected_and_cannot_replace_boot_identity()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        var original = native.Domain;
        native.Domain = original with { TimeNamespace = "time:[888]" };
        buffer.CaptureClockAnchor();
        Assert.Null(buffer.Begin("http", "http-mcp"));
        Assert.Equal(original, buffer.Snapshot().Coverage.Clock!.Domain);
        Assert.Equal("CLOCK_DOMAIN_CHANGED", buffer.Snapshot().Coverage.Clock!.InvalidReason);
    }

    [Fact]
    public void Domain_metadata_is_validated_before_it_can_be_persisted()
    {
        var native = new Clock();
        native.Domain = native.Domain with { Clocksource = "PRIVATE_SECRET_SENTINEL\n" };
        var buffer = Buffer(new WallClock(), native);
        Assert.Null(buffer.Begin("http", "http-mcp"));
        var snapshot = buffer.Snapshot();
        Assert.Null(snapshot.Coverage.Clock!.Domain);
        Assert.DoesNotContain("PRIVATE_SECRET_SENTINEL", JsonSerializer.Serialize(new { snapshot.Coverage, snapshot.Samples }));
    }

    [Fact]
    public void Nonzero_namespace_offsets_fail_closed()
    {
        var native = new Clock();
        native.Domain = native.Domain with { MonotonicOffsetNs = 1 };
        Assert.Equal("CLOCK_DOMAIN_UNAVAILABLE", Buffer(new WallClock(), native).Snapshot().Coverage.Clock!.InvalidReason);
    }

    [Fact]
    public void Raw_regression_closes_capture_permanently()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        native.Raw = 0;
        Assert.Null(buffer.Begin("http", "http-mcp"));
        native.Raw = 10_000_000_000;
        Assert.Null(buffer.Begin("http", "http-mcp"));
        Assert.Equal("RAW_CLOCK_REGRESSED", buffer.Snapshot().Coverage.Clock!.InvalidReason);
    }

    [Fact]
    public void Excessive_native_bracket_closes_capture_without_admitting_a_sample()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        native.Bracket = 50_000_001;
        Assert.Null(buffer.Begin("http", "http-mcp"));
        Assert.Equal("CLOCK_READ_BRACKET_EXCEEDED", buffer.Snapshot().Coverage.Clock!.InvalidReason);
    }

    [Fact]
    public void Whole_sample_bracket_includes_the_original_timeprovider_reads_and_is_bounded()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        native.AfterReadAdvance = 50_000_000;
        Assert.Null(buffer.Begin("http", "http-mcp"));
        Assert.Equal("CLOCK_READ_BRACKET_EXCEEDED", buffer.Snapshot().Coverage.Clock!.InvalidReason);
        Assert.Empty(buffer.Snapshot().Samples);
    }

    [Fact]
    public void An_excessive_completion_bracket_keeps_the_observation_and_rejects_the_clock_evidence()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        var lease = buffer.Begin("http", "http-mcp")!;
        native.AfterReadAdvance = 50_000_000;
        lease.Complete("success");
        var snapshot = buffer.Snapshot();
        var sample = Assert.Single(snapshot.Samples);
        Assert.Equal("success", sample.Outcome);
        Assert.Equal("CLOCK_READ_BRACKET_EXCEEDED", snapshot.Coverage.Clock!.InvalidReason);
        Assert.True(sample.RawFinishedAfterNs - sample.RawFinishedBeforeNs > 50_000_000);
        Assert.Equal(0, snapshot.Coverage.HttpActive);
    }

    [Fact]
    public void Backward_utc_does_not_extend_raw_maximum_capture_span()
    {
        var wall = new WallClock();
        var native = new Clock();
        var buffer = Buffer(wall, native);
        native.Advance(60_000_000_000);
        wall.Utc -= TimeSpan.FromHours(1);
        Assert.Null(buffer.Begin("http", "http-mcp"));
        Assert.True(buffer.Snapshot().Coverage.WindowClosed);
        Assert.Equal(60_000_000_000, buffer.Snapshot().Coverage.Clock!.RawMaximumSpanNs);
    }

    [Fact]
    public void Stale_anchor_and_backward_utc_cannot_admit_an_arrival_after_the_fresh_raw_deadline()
    {
        var wall = new WallClock();
        var native = new Clock();
        var buffer = Buffer(wall, native);
        wall.Utc -= TimeSpan.FromHours(1);
        native.Advance(60_000_000_001);
        Assert.Null(buffer.Begin("http", "http-mcp"));
        Assert.Empty(buffer.Snapshot().Samples);
        Assert.Equal(0, buffer.Snapshot().Coverage.HttpStarted);
        var calls = native.Reads;
        Assert.Null(buffer.Begin("http", "http-mcp"));
        Assert.Equal(calls, native.Reads);
    }

    [Fact]
    public void A_deadline_crossed_inside_the_begin_read_bracket_cannot_admit_a_request()
    {
        var wall = new WallClock();
        var native = new Clock();
        var buffer = Buffer(wall, native);
        wall.Utc -= TimeSpan.FromHours(1);
        native.Advance(59_999_999_997);
        Assert.Null(buffer.Begin("http", "http-mcp"));
        Assert.True(buffer.Snapshot().Coverage.WindowClosed);
        Assert.Equal(0, buffer.Snapshot().Coverage.HttpStarted);
    }

    [Fact]
    public void Utc_steps_are_observed_not_rewritten_or_used_to_fake_clock_quality()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        native.Advance(1_000_000_000);
        native.Realtime -= 900_000_000;
        var observedRealtime = native.Realtime;
        buffer.CaptureClockAnchor();
        var clock = buffer.Snapshot().Coverage.Clock!;
        Assert.Equal(1, clock.Discontinuities);
        Assert.Null(clock.InvalidReason);
        Assert.NotNull(buffer.Begin("http", "http-mcp"));
        Assert.Equal(observedRealtime, clock.Anchors.Last().Reading.RealtimeNs);
    }

    [Fact]
    public void Suspend_evidence_stops_admission_and_preserves_the_observed_anchor()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        native.Advance(1_000_000_000);
        native.SuspendOffset = 100_000_000;
        buffer.CaptureClockAnchor();
        Assert.Null(buffer.Begin("http", "http-mcp"));
        var clock = buffer.Snapshot().Coverage.Clock!;
        Assert.Equal(1, clock.Suspends);
        Assert.Equal("CLOCK_SUSPEND_OBSERVED", clock.InvalidReason);
        Assert.Equal(2, clock.Anchors.Count);
    }

    [Fact]
    public void Anchor_capacity_and_total_limit_record_loss_and_old_ack_cannot_remove_new_anchor()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native, anchors: 1, maximumAnchors: 2);
        var first = buffer.Snapshot().Coverage.Clock!;
        native.Advance(1_000_000_000);
        buffer.CaptureClockAnchor();
        Assert.Equal(1, buffer.Snapshot().Coverage.Clock!.AnchorsDropped);
        buffer.AcknowledgeClockAnchors(first.Anchors);
        native.Advance(1_000_000_000);
        buffer.CaptureClockAnchor();
        buffer.AcknowledgeClockAnchors(first.Anchors);
        var next = buffer.Snapshot().Coverage.Clock!;
        Assert.Single(next.Anchors);
        buffer.AcknowledgeClockAnchors(next.Anchors);
        native.Advance(1_000_000_000);
        buffer.CaptureClockAnchor();
        Assert.Equal(2, buffer.Snapshot().Coverage.Clock!.AnchorsDropped);
        Assert.Equal(2, buffer.Snapshot().Coverage.Clock!.AnchorsAdmitted);
    }

    [Theory]
    [InlineData(2.0)]
    [InlineData(0.5)]
    public void Producer_anchor_density_is_bounded_by_raw_seconds_even_when_monotonic_rate_differs(double monotonicRate)
    {
        var wall = new WallClock();
        var native = new Clock();
        var buffer = Buffer(wall, native);
        for (var index = 0; index < 4; index++)
        {
            wall.Advance(TimeSpan.FromMilliseconds(500 * monotonicRate));
            native.Advance(500_000_000);
            buffer.CaptureClockAnchor();
        }
        var coverage = buffer.Snapshot().Coverage;
        Assert.Equal(3, coverage.Clock!.Anchors.Count);
        Assert.Equal(5, coverage.Clock.Checks);
        Assert.Equal(5, coverage.Revision);
        Assert.Equal(0, coverage.Clock.AnchorsDropped);
        Assert.Equal(3, coverage.Clock.Anchors.Select(a => a.Reading.RawBeforeNs / 1_000_000_000).Distinct().Count());
    }

    [Fact]
    public void Same_raw_bucket_still_checks_domain_and_observes_utc_changes()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        native.Realtime -= 900_000_000;
        buffer.CaptureClockAnchor();
        var first = buffer.Snapshot().Coverage.Clock!;
        Assert.Single(first.Anchors);
        Assert.Equal(1, first.Discontinuities);
        Assert.Equal(2, first.Checks);
        native.Domain = native.Domain with { Clocksource = "other_clock" };
        buffer.CaptureClockAnchor();
        var second = buffer.Snapshot().Coverage.Clock!;
        Assert.Equal("CLOCK_DOMAIN_CHANGED", second.InvalidReason);
        Assert.Null(buffer.Begin("http", "http-mcp"));
    }

    [Fact]
    public void Final_anchor_is_preserved_once_even_in_the_startup_raw_bucket()
    {
        var native = new Clock();
        var buffer = Buffer(new WallClock(), native);
        buffer.CaptureClockAnchor(final: true);
        var snapshot = buffer.Snapshot().Coverage.Clock!;
        Assert.Equal(2, snapshot.Anchors.Count);
        var reads = native.Reads;
        buffer.CaptureClockAnchor(final: true);
        Assert.Equal(2, buffer.Snapshot().Coverage.Clock!.Anchors.Count);
        Assert.Equal(reads, native.Reads);
    }

    private static RequestArrivalObservations Buffer(WallClock wall, Clock clock, bool enabled = true,
        int schema = 2, int anchors = 128, int maximumAnchors = 86_402)
        => new(wall, Options.Create(new RequestArrivalObservationOptions
        {
            Enabled = enabled,
            CaptureSchemaVersion = schema,
            UntilUtc = wall.GetUtcNow().AddMinutes(1).ToString("O"),
            ClockAnchorCapacity = anchors,
            MaximumClockAnchors = maximumAnchors
        }), clock);

    private sealed class WallClock : TimeProvider
    {
        public DateTimeOffset Initial { get; } = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        public DateTimeOffset Utc { get; set; } = new(2026, 10, 9, 0, 0, 0, TimeSpan.Zero);
        private long _ticks;
        public override DateTimeOffset GetUtcNow() => Utc;
        public override long GetTimestamp() => _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public void Advance(TimeSpan duration) { Utc += duration; _ticks += duration.Ticks; }
    }

    private sealed class Clock : IRequestArrivalClock
    {
        public RequestArrivalClockDomain Domain { get; set; } = new(new("37fb6646-1692-4503-8836-dfc5385d1a7b"), "time:[123]", 0, 0, "hyperv_clocksource_tsc_page");
        public bool Available { get; set; } = true;
        public bool Throw { get; set; }
        public int Reads { get; private set; }
        public int DomainReads { get; private set; }
        public long Raw { get; set; } = 1_000_000_000;
        public long Realtime { get; set; } = 1_791_504_000_000_000_000;
        public long Bracket { get; set; } = 1;
        public long AfterReadAdvance { get; set; }
        public long SuspendOffset { get; set; }
        public bool TryReadDomain(out RequestArrivalClockDomain? domain)
        {
            DomainReads++;
            if (Throw) throw new InvalidOperationException("SECRET_SENTINEL");
            domain = Available ? Domain : null;
            return Available;
        }
        public bool TryRead(out RequestArrivalClockReading? reading)
        {
            Reads++;
            if (Throw) throw new InvalidOperationException("SECRET_SENTINEL");
            reading = Available ? new(Raw, Raw + Bracket, Raw, Raw + SuspendOffset, Realtime) : null;
            Raw += Bracket + AfterReadAdvance;
            Realtime += Bracket + AfterReadAdvance;
            return Available;
        }
        public void Advance(long duration) { Raw += duration; Realtime += duration; }
    }
}

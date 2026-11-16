using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class RequestArrivalClockV2StoreTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task V2_persists_direct_clock_evidence_and_old_start_replay_cannot_hide_completion()
    {
        _ = environment.GetFactory();
        await using var source = NpgsqlDataSource.Create(environment.PostgresConnectionString!);
        var native = new Clock();
        var buffer = Buffer(native);
        var lease = buffer.Begin("http", "http-mcp", classification: new("http-mcp", "POST", "le1k"))!;
        var start = buffer.Snapshot();
        var store = new RequestArrivalObservationStore(source);
        await store.WriteAsync(start.Coverage, start.Samples, DateTimeOffset.UtcNow, false, default);
        lease.Complete("success");
        native.Advance(1_000_000_000);
        buffer.CaptureClockAnchor();
        var finish = buffer.Snapshot();
        await store.WriteAsync(finish.Coverage, finish.Samples, DateTimeOffset.UtcNow, false, default);
        await store.WriteAsync(start.Coverage, start.Samples, DateTimeOffset.UtcNow, false, default);
        await using var command = source.CreateCommand("""
            SELECT b.capture_schema_version,b.v2_clock_kind,b.time_namespace,b.clock_anchor_admitted,
                   s.revision,s.outcome,s.raw_started_before_ns,s.raw_started_after_ns,
                   s.raw_finished_before_ns,s.raw_finished_after_ns,s.request_family,s.request_method,s.payload_size_bucket,
                   (SELECT count(*) FROM monitoring.request_arrival_clock_anchors a WHERE a.boot_id=b.boot_id),
                   b.http_completed
            FROM monitoring.request_arrival_boots b JOIN monitoring.request_arrival_samples s USING(boot_id)
            WHERE b.boot_id=@boot;
            """);
        command.Parameters.AddWithValue("boot", buffer.BootId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("CLOCK_MONOTONIC_RAW", reader.GetString(1));
        Assert.Equal("time:[123]", reader.GetString(2));
        Assert.Equal(2L, reader.GetInt64(3));
        Assert.Equal(2, reader.GetInt32(4));
        Assert.Equal("success", reader.GetString(5));
        Assert.True(reader.GetInt64(6) <= reader.GetInt64(7));
        Assert.True(reader.GetInt64(7) <= reader.GetInt64(8));
        Assert.True(reader.GetInt64(8) <= reader.GetInt64(9));
        Assert.Equal("http-mcp", reader.GetString(10));
        Assert.Equal("POST", reader.GetString(11));
        Assert.Equal("le1k", reader.GetString(12));
        Assert.Equal(2L, reader.GetInt64(13));
        Assert.Equal(1L, reader.GetInt64(14));
    }

    [DockerRequiredFact]
    public async Task Compatible_v1_insert_and_reader_work_after_058_without_rewriting_v2_rows()
    {
        _ = environment.GetFactory();
        await using var source = NpgsqlDataSource.Create(environment.PostgresConnectionString!);
        var buffer = Buffer();
        buffer.Begin("http", "http-mcp")!.Complete("success");
        var snapshot = buffer.Snapshot();
        var store = new RequestArrivalObservationStore(source);
        await store.WriteAsync(snapshot.Coverage, snapshot.Samples, DateTimeOffset.UtcNow, false, default);
        var oldBoot = Guid.NewGuid();
        await using var legacy = source.CreateCommand("""
            INSERT INTO monitoring.request_arrival_boots
                (boot_id,started_at_utc,until_utc,timestamp_frequency,revision,last_flushed_at_utc,
                 http_started,http_completed,http_active,tool_started,tool_completed,tool_active,
                 admitted,dropped_starts,dropped_finishes,window_closed)
            VALUES (@old,now(),now()+interval '1 hour',1000000000,0,now(),0,0,0,0,0,0,0,0,0,false);
            SELECT capture_schema_version,raw_started_before_ns,clock_failures
            FROM monitoring.request_arrival_boots WHERE boot_id=@old;
            SELECT s.operation,s.started_at_utc,s.started_timestamp,s.duration_ms,b.http_completed
            FROM monitoring.request_arrival_samples s JOIN monitoring.request_arrival_boots b USING(boot_id)
            WHERE b.boot_id=@new;
            """);
        legacy.Parameters.AddWithValue("old", oldBoot);
        legacy.Parameters.AddWithValue("new", buffer.BootId);
        await using var reader = await legacy.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt32(0));
        Assert.True(reader.IsDBNull(1));
        Assert.Equal(0L, reader.GetInt64(2));
        Assert.True(await reader.NextResultAsync());
        Assert.True(await reader.ReadAsync());
        Assert.Equal("http-mcp", reader.GetString(0));
        Assert.Equal(1L, reader.GetInt64(4));
    }

    [DockerRequiredFact]
    public async Task Anchor_write_failure_is_atomic_keeps_staging_and_recovers_on_retry()
    {
        _ = environment.GetFactory();
        await using var source = NpgsqlDataSource.Create(environment.PostgresConnectionString!);
        var native = new Clock();
        var buffer = Buffer(native);
        buffer.Begin("http", "http-mcp")!.Complete("success");
        var flush = new RequestArrivalObservationFlushService(buffer, new(source), TimeProvider.System,
            NullLogger<RequestArrivalObservationFlushService>.Instance);
        var suffix = buffer.BootId.ToString("N");
        await using var fault = source.CreateCommand($"""
            CREATE FUNCTION monitoring.arrival_clock_fault_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.boot_id = '{buffer.BootId:D}' THEN RAISE EXCEPTION 'OWNED_ARRIVAL_CLOCK_WRITE_FAULT'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER arrival_clock_fault_{suffix} BEFORE INSERT ON monitoring.request_arrival_clock_anchors
            FOR EACH ROW EXECUTE FUNCTION monitoring.arrival_clock_fault_{suffix}();
            """);
        await fault.ExecuteNonQueryAsync();
        native.Advance(1_000_000_000);
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => flush.FlushAsync(false, default));
            Assert.Single(buffer.Snapshot().Samples);
            Assert.Equal(2, buffer.Snapshot().Coverage.Clock!.Anchors.Count);
            await using var count = source.CreateCommand("SELECT count(*) FROM monitoring.request_arrival_boots WHERE boot_id=@boot");
            count.Parameters.AddWithValue("boot", buffer.BootId);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await using var remove = source.CreateCommand($"DROP TRIGGER arrival_clock_fault_{suffix} ON monitoring.request_arrival_clock_anchors; DROP FUNCTION monitoring.arrival_clock_fault_{suffix}();");
            await remove.ExecuteNonQueryAsync();
        }
        native.Advance(1_000_000_000);
        await flush.FlushAsync(false, default);
        Assert.Empty(buffer.Snapshot().Samples);
        Assert.Empty(buffer.Snapshot().Coverage.Clock!.Anchors);
        await using var saved = source.CreateCommand("SELECT count(*) FROM monitoring.request_arrival_clock_anchors WHERE boot_id=@boot");
        saved.Parameters.AddWithValue("boot", buffer.BootId);
        Assert.Equal(3L, await saved.ExecuteScalarAsync());
    }

    [DockerRequiredFact]
    public async Task Closed_failure_evidence_is_readable_with_no_fabricated_sample()
    {
        _ = environment.GetFactory();
        await using var source = NpgsqlDataSource.Create(environment.PostgresConnectionString!);
        var buffer = Buffer(new Clock { Available = false });
        Assert.Null(buffer.Begin("http", "http-mcp"));
        var snapshot = buffer.Snapshot();
        await new RequestArrivalObservationStore(source).WriteAsync(snapshot.Coverage, snapshot.Samples,
            DateTimeOffset.UtcNow, false, default);
        await using var command = source.CreateCommand("""
            SELECT capture_schema_version,window_closed,clock_failures,clock_invalid_reason,kernel_boot_id,
                   (SELECT count(*) FROM monitoring.request_arrival_samples s WHERE s.boot_id=b.boot_id)
            FROM monitoring.request_arrival_boots b WHERE boot_id=@boot;
            """);
        command.Parameters.AddWithValue("boot", buffer.BootId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.True(reader.GetBoolean(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal("CLOCK_DOMAIN_UNAVAILABLE", reader.GetString(3));
        Assert.True(reader.IsDBNull(4));
        Assert.Equal(0L, reader.GetInt64(5));
    }

    [DockerRequiredFact]
    public Task Closed_idle_capture_retries_failed_background_write_before_exiting()
        => AssertIdleBackgroundRetry(clockAvailable: true);

    [DockerRequiredFact]
    public Task Invalid_clock_capture_retries_failed_background_write_before_exiting()
        => AssertIdleBackgroundRetry(clockAvailable: false);

    private async Task AssertIdleBackgroundRetry(bool clockAvailable)
    {
        _ = environment.GetFactory();
        await using var source = NpgsqlDataSource.Create(environment.PostgresConnectionString!);
        var native = new Clock { Available = clockAvailable };
        var buffer = Buffer(native);
        native.Advance(360_000_000_000);
        var logger = new FlushFailureLogger();
        using var flush = new RequestArrivalObservationFlushService(buffer, new(source), TimeProvider.System, logger);
        var suffix = buffer.BootId.ToString("N");
        await using var fault = source.CreateCommand($"""
            CREATE FUNCTION monitoring.arrival_idle_fault_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.boot_id = '{buffer.BootId:D}' THEN RAISE EXCEPTION 'OWNED_IDLE_WRITE_FAULT'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER arrival_idle_fault_{suffix} BEFORE INSERT ON monitoring.request_arrival_boots
            FOR EACH ROW EXECUTE FUNCTION monitoring.arrival_idle_fault_{suffix}();
            """);
        await fault.ExecuteNonQueryAsync();
        try
        {
            await flush.StartAsync(default);
            await logger.FirstFailure.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(buffer.Snapshot().Coverage.WindowClosed);
            Assert.Empty(buffer.Snapshot().Samples);
        }
        finally
        {
            await using var remove = source.CreateCommand($"DROP TRIGGER arrival_idle_fault_{suffix} ON monitoring.request_arrival_boots; DROP FUNCTION monitoring.arrival_idle_fault_{suffix}();");
            await remove.ExecuteNonQueryAsync();
        }
        await flush.ExecuteTask!.WaitAsync(TimeSpan.FromSeconds(10));
        await using var saved = source.CreateCommand("""
            SELECT window_closed,revision,clock_anchor_admitted,
                   (SELECT count(*) FROM monitoring.request_arrival_clock_anchors a WHERE a.boot_id=b.boot_id)
            FROM monitoring.request_arrival_boots b WHERE boot_id=@boot;
            """);
        saved.Parameters.AddWithValue("boot", buffer.BootId);
        await using var reader = await saved.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.True(reader.GetBoolean(0));
        Assert.Equal(buffer.Snapshot().Coverage.Revision, reader.GetInt64(1));
        Assert.Equal(clockAvailable ? 2L : 0L, reader.GetInt64(2));
        Assert.Equal(clockAvailable ? 2L : 0L, reader.GetInt64(3));
        Assert.Empty(buffer.Snapshot().Coverage.Clock!.Anchors);
    }

    private sealed class FlushFailureLogger : ILogger<RequestArrivalObservationFlushService>
    {
        public TaskCompletionSource FirstFailure { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning) FirstFailure.TrySetResult();
        }
    }

    private static RequestArrivalObservations Buffer(Clock? clock = null)
        => new(TimeProvider.System, Options.Create(new RequestArrivalObservationOptions
        {
            Enabled = true,
            CaptureSchemaVersion = 2,
            UntilUtc = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")
        }), clock ?? new Clock());

    private sealed class Clock : IRequestArrivalClock
    {
        private long _raw = 1_000_000_000;
        public bool Available { get; set; } = true;
        public bool TryReadDomain(out RequestArrivalClockDomain? domain)
        {
            domain = Available ? new(new("8d8115a1-4b8a-4cb5-80f6-2f3255760738"), "time:[123]", 0, 0, "hyperv_clocksource_tsc_page") : null;
            return Available;
        }
        public bool TryRead(out RequestArrivalClockReading? reading)
        {
            reading = Available ? new(_raw, _raw + 1, _raw, _raw, 1_791_504_000_000_000_000 + _raw) : null;
            _raw += 2;
            return Available;
        }
        public void Advance(long nanoseconds) => _raw += nanoseconds;
    }
}

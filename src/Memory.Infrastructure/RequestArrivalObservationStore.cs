using System.Globalization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using NpgsqlTypes;

namespace Memory.Infrastructure;

public sealed class RequestArrivalObservationStore(NpgsqlDataSource source)
{
    public async Task WriteAsync(RequestArrivalCoverage coverage, IReadOnlyList<RequestArrivalSample> samples,
        DateTimeOffset now, bool stopped, CancellationToken cancellationToken)
    {
        await using var connection = await source.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction) { Timeout = 5 };
        var boot = new NpgsqlBatchCommand("""
            INSERT INTO monitoring.request_arrival_boots
                (boot_id, started_at_utc, until_utc, timestamp_frequency, revision, last_flushed_at_utc, stopped_at_utc,
                 http_started, http_completed, http_active, tool_started, tool_completed, tool_active,
                 admitted, dropped_starts, dropped_finishes, window_closed,
                 capture_schema_version,v2_clock_kind,kernel_boot_id,time_namespace,monotonic_offset_ns,boottime_offset_ns,
                 clocksource,raw_started_before_ns,raw_started_after_ns,raw_max_span_ns,
                 clock_checks,clock_failures,clock_discontinuities,clock_suspends,clock_invalid_reason,clock_anchor_admitted,clock_anchor_dropped)
            VALUES (@boot,@started,@until,@frequency,@revision,@now,CASE WHEN @stopped THEN @now ELSE NULL END,
                @hs,@hc,@ha,@ts,@tc,@ta,@admitted,@ds,@df,@closed,
                @schema,@kind,@kernel,@namespace,@monoOffset,@bootOffset,@clocksource,@rawStartBefore,@rawStartAfter,@rawSpan,
                @clockChecks,@clockFailures,@discontinuities,@suspends,@invalidReason,@anchorAdmitted,@anchorDropped)
            ON CONFLICT (boot_id) DO UPDATE SET
                revision=EXCLUDED.revision,
                last_flushed_at_utc=GREATEST(request_arrival_boots.last_flushed_at_utc,EXCLUDED.last_flushed_at_utc),
                stopped_at_utc=COALESCE(EXCLUDED.stopped_at_utc,request_arrival_boots.stopped_at_utc),
                http_started=EXCLUDED.http_started,http_completed=EXCLUDED.http_completed,http_active=EXCLUDED.http_active,
                tool_started=EXCLUDED.tool_started,tool_completed=EXCLUDED.tool_completed,tool_active=EXCLUDED.tool_active,
                admitted=EXCLUDED.admitted,dropped_starts=EXCLUDED.dropped_starts,dropped_finishes=EXCLUDED.dropped_finishes,
                window_closed=EXCLUDED.window_closed,
                clock_checks=EXCLUDED.clock_checks,clock_failures=EXCLUDED.clock_failures,clock_discontinuities=EXCLUDED.clock_discontinuities,
                clock_suspends=EXCLUDED.clock_suspends,clock_invalid_reason=EXCLUDED.clock_invalid_reason,
                clock_anchor_admitted=EXCLUDED.clock_anchor_admitted,clock_anchor_dropped=EXCLUDED.clock_anchor_dropped
            WHERE request_arrival_boots.revision <= EXCLUDED.revision;
            """);
        boot.Parameters.AddWithValue("boot", coverage.BootId);
        boot.Parameters.AddWithValue("started", coverage.StartedAtUtc);
        boot.Parameters.AddWithValue("until", coverage.UntilUtc);
        boot.Parameters.AddWithValue("frequency", coverage.TimestampFrequency);
        boot.Parameters.AddWithValue("revision", coverage.Revision);
        boot.Parameters.AddWithValue("now", now);
        boot.Parameters.AddWithValue("stopped", stopped);
        boot.Parameters.AddWithValue("hs", coverage.HttpStarted);
        boot.Parameters.AddWithValue("hc", coverage.HttpCompleted);
        boot.Parameters.AddWithValue("ha", coverage.HttpActive);
        boot.Parameters.AddWithValue("ts", coverage.ToolStarted);
        boot.Parameters.AddWithValue("tc", coverage.ToolCompleted);
        boot.Parameters.AddWithValue("ta", coverage.ToolActive);
        boot.Parameters.AddWithValue("admitted", coverage.Admitted);
        boot.Parameters.AddWithValue("ds", coverage.DroppedStarts);
        boot.Parameters.AddWithValue("df", coverage.DroppedFinishes);
        boot.Parameters.AddWithValue("closed", coverage.WindowClosed);
        boot.Parameters.AddWithValue("schema", coverage.CaptureSchemaVersion);
        AddNullable(boot, "kind", NpgsqlDbType.Text, coverage.Clock is null ? null : "CLOCK_MONOTONIC_RAW");
        AddNullable(boot, "kernel", NpgsqlDbType.Uuid, coverage.Clock?.Domain?.KernelBootId);
        AddNullable(boot, "namespace", NpgsqlDbType.Text, coverage.Clock?.Domain?.TimeNamespace);
        AddNullable(boot, "monoOffset", NpgsqlDbType.Bigint, coverage.Clock?.Domain?.MonotonicOffsetNs);
        AddNullable(boot, "bootOffset", NpgsqlDbType.Bigint, coverage.Clock?.Domain?.BoottimeOffsetNs);
        AddNullable(boot, "clocksource", NpgsqlDbType.Text, coverage.Clock?.Domain?.Clocksource);
        AddNullable(boot, "rawStartBefore", NpgsqlDbType.Bigint, coverage.Clock?.RawStartedBeforeNs);
        AddNullable(boot, "rawStartAfter", NpgsqlDbType.Bigint, coverage.Clock?.RawStartedAfterNs);
        AddNullable(boot, "rawSpan", NpgsqlDbType.Bigint, coverage.Clock?.RawMaximumSpanNs);
        boot.Parameters.AddWithValue("clockChecks", coverage.Clock?.Checks ?? 0);
        boot.Parameters.AddWithValue("clockFailures", coverage.Clock?.Failures ?? 0);
        boot.Parameters.AddWithValue("discontinuities", coverage.Clock?.Discontinuities ?? 0);
        boot.Parameters.AddWithValue("suspends", coverage.Clock?.Suspends ?? 0);
        AddNullable(boot, "invalidReason", NpgsqlDbType.Text, coverage.Clock?.InvalidReason);
        boot.Parameters.AddWithValue("anchorAdmitted", coverage.Clock?.AnchorsAdmitted ?? 0);
        boot.Parameters.AddWithValue("anchorDropped", coverage.Clock?.AnchorsDropped ?? 0);
        batch.BatchCommands.Add(boot);
        foreach (var anchor in coverage.Clock?.Anchors ?? [])
        {
            if (anchor.BootId != coverage.BootId) throw new ArgumentException("Clock anchor boot mismatch.", nameof(coverage));
            var command = new NpgsqlBatchCommand("""
                INSERT INTO monitoring.request_arrival_clock_anchors
                    (boot_id,sequence,raw_before_ns,raw_after_ns,monotonic_ns,boottime_ns,realtime_ns,
                     kernel_boot_id,time_namespace,monotonic_offset_ns,boottime_offset_ns,clocksource)
                VALUES (@boot,@sequence,@before,@after,@mono,@boottime,@realtime,@kernel,@namespace,@monoOffset,@bootOffset,@clocksource)
                ON CONFLICT (boot_id,sequence) DO NOTHING;
                """);
            command.Parameters.AddWithValue("boot", anchor.BootId);
            command.Parameters.AddWithValue("sequence", anchor.Sequence);
            command.Parameters.AddWithValue("before", anchor.Reading.RawBeforeNs);
            command.Parameters.AddWithValue("after", anchor.Reading.RawAfterNs);
            command.Parameters.AddWithValue("mono", anchor.Reading.MonotonicNs);
            command.Parameters.AddWithValue("boottime", anchor.Reading.BoottimeNs);
            command.Parameters.AddWithValue("realtime", anchor.Reading.RealtimeNs);
            command.Parameters.AddWithValue("kernel", anchor.Domain.KernelBootId);
            command.Parameters.AddWithValue("namespace", anchor.Domain.TimeNamespace);
            command.Parameters.AddWithValue("monoOffset", anchor.Domain.MonotonicOffsetNs);
            command.Parameters.AddWithValue("bootOffset", anchor.Domain.BoottimeOffsetNs);
            command.Parameters.AddWithValue("clocksource", anchor.Domain.Clocksource);
            batch.BatchCommands.Add(command);
        }
        foreach (var row in samples)
        {
            if (row.BootId != coverage.BootId) throw new ArgumentException("Observation boot mismatch.", nameof(samples));
            var command = new NpgsqlBatchCommand("""
                INSERT INTO monitoring.request_arrival_samples
                    (boot_id,sequence,parent_sequence,layer,operation,started_at_utc,started_timestamp,active_at_start,
                     finished_at_utc,duration_ms,outcome,revision,
                     raw_started_before_ns,raw_started_after_ns,raw_finished_before_ns,raw_finished_after_ns,
                     request_family,request_method,payload_size_bucket)
                VALUES (@boot,@sequence,@parent,@layer,@operation,@start,@tick,@active,@finish,@duration,@outcome,@revision,
                        @rawStartBefore,@rawStartAfter,@rawFinishBefore,@rawFinishAfter,@family,@method,@size)
                ON CONFLICT (boot_id,sequence) DO UPDATE SET
                    finished_at_utc=EXCLUDED.finished_at_utc,duration_ms=EXCLUDED.duration_ms,
                    outcome=EXCLUDED.outcome,revision=EXCLUDED.revision,
                    raw_finished_before_ns=EXCLUDED.raw_finished_before_ns,raw_finished_after_ns=EXCLUDED.raw_finished_after_ns
                WHERE request_arrival_samples.revision < EXCLUDED.revision;
                """);
            command.Parameters.AddWithValue("boot", row.BootId);
            command.Parameters.AddWithValue("sequence", row.Sequence);
            command.Parameters.AddWithValue("parent", row.ParentSequence);
            command.Parameters.AddWithValue("layer", row.Layer);
            command.Parameters.AddWithValue("operation", row.Operation);
            command.Parameters.AddWithValue("start", row.StartedAtUtc);
            command.Parameters.AddWithValue("tick", row.StartedTimestamp);
            command.Parameters.AddWithValue("active", row.ActiveAtStart);
            command.Parameters.AddWithValue("finish", NpgsqlDbType.TimestampTz, (object?)row.FinishedAtUtc ?? DBNull.Value);
            command.Parameters.AddWithValue("duration", NpgsqlDbType.Double, (object?)row.DurationMs ?? DBNull.Value);
            command.Parameters.AddWithValue("outcome", row.Outcome);
            command.Parameters.AddWithValue("revision", row.Revision);
            AddNullable(command, "rawStartBefore", NpgsqlDbType.Bigint, row.RawStartedBeforeNs);
            AddNullable(command, "rawStartAfter", NpgsqlDbType.Bigint, row.RawStartedAfterNs);
            AddNullable(command, "rawFinishBefore", NpgsqlDbType.Bigint, row.RawFinishedBeforeNs);
            AddNullable(command, "rawFinishAfter", NpgsqlDbType.Bigint, row.RawFinishedAfterNs);
            AddNullable(command, "family", NpgsqlDbType.Text, row.RequestFamily);
            AddNullable(command, "method", NpgsqlDbType.Text, row.RequestMethod);
            AddNullable(command, "size", NpgsqlDbType.Text, row.PayloadSizeBucket);
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static void AddNullable(NpgsqlBatchCommand command, string name, NpgsqlDbType type, object? value)
        => command.Parameters.AddWithValue(name, type, value ?? DBNull.Value);
}

public sealed class RequestArrivalObservationFlushService(
    RequestArrivalObservations buffer, RequestArrivalObservationStore store, TimeProvider time,
    ILogger<RequestArrivalObservationFlushService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _flush = new(1);
    private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;
    private long _persistedRevision = -1;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!buffer.Enabled) return;
        try
        {
            logger.LogInformation(new EventId(5701, "RequestArrivalCaptureStarted"),
                "Request arrival capture started: SchemaVersion={SchemaVersion}, ObservationBootId={ObservationBootId}, StartedAtUtc={StartedAtUtc}, UntilUtc={UntilUtc}, TimestampFrequency={TimestampFrequency}, ProcessId={ProcessId}",
                buffer.CaptureSchemaVersion, buffer.BootId.ToString("D"), buffer.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                buffer.UntilUtc.ToString("O", CultureInfo.InvariantCulture), time.TimestampFrequency, Environment.ProcessId);
        }
        catch (Exception)
        {
            // Optional source-binding evidence must not prevent capture when a logging provider fails.
        }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(buffer.CaptureSchemaVersion == 2 ? 0.5 : 2), time);
        do
        {
            try { await FlushAsync(false, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception)
            {
                if (time.GetUtcNow() - _lastWarning >= TimeSpan.FromMinutes(5))
                {
                    _lastWarning = time.GetUtcNow();
                    // Never log connection strings, SQL, samples or exception payloads.
                    logger.LogWarning("Request arrival observation flush failed; bounded staging retained for retry.");
                }
            }
            var coverage = buffer.Snapshot().Coverage;
            if (coverage.WindowClosed && coverage.HttpActive == 0 && coverage.ToolActive == 0 && coverage.Pending == 0 &&
                (coverage.Clock?.Anchors.Count ?? 0) == 0 && coverage.Revision <= Interlocked.Read(ref _persistedRevision)) break;
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task FlushAsync(bool stopped, CancellationToken cancellationToken)
    {
        if (!buffer.Enabled) return;
        await _flush.WaitAsync(cancellationToken);
        try
        {
            buffer.CaptureClockAnchor(stopped);
            var snapshot = buffer.Snapshot();
            await store.WriteAsync(snapshot.Coverage, snapshot.Samples, time.GetUtcNow(), stopped, cancellationToken);
            buffer.Acknowledge(snapshot.Samples);
            if (snapshot.Coverage.Clock is not null) buffer.AcknowledgeClockAnchors(snapshot.Coverage.Clock.Anchors);
            Interlocked.Exchange(ref _persistedRevision, snapshot.Coverage.Revision);
        }
        finally { _flush.Release(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await FlushAsync(true, cancellationToken); }
        catch (Exception) { logger.LogWarning("Final request arrival flush failed; capture coverage remains incomplete."); }
    }
}

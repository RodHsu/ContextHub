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
                 admitted, dropped_starts, dropped_finishes, window_closed)
            VALUES (@boot,@started,@until,@frequency,@revision,@now,CASE WHEN @stopped THEN @now ELSE NULL END,
                @hs,@hc,@ha,@ts,@tc,@ta,@admitted,@ds,@df,@closed)
            ON CONFLICT (boot_id) DO UPDATE SET
                revision=EXCLUDED.revision,
                last_flushed_at_utc=GREATEST(request_arrival_boots.last_flushed_at_utc,EXCLUDED.last_flushed_at_utc),
                stopped_at_utc=COALESCE(EXCLUDED.stopped_at_utc,request_arrival_boots.stopped_at_utc),
                http_started=EXCLUDED.http_started,http_completed=EXCLUDED.http_completed,http_active=EXCLUDED.http_active,
                tool_started=EXCLUDED.tool_started,tool_completed=EXCLUDED.tool_completed,tool_active=EXCLUDED.tool_active,
                admitted=EXCLUDED.admitted,dropped_starts=EXCLUDED.dropped_starts,dropped_finishes=EXCLUDED.dropped_finishes,
                window_closed=EXCLUDED.window_closed
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
        batch.BatchCommands.Add(boot);
        foreach (var row in samples)
        {
            if (row.BootId != coverage.BootId) throw new ArgumentException("Observation boot mismatch.", nameof(samples));
            var command = new NpgsqlBatchCommand("""
                INSERT INTO monitoring.request_arrival_samples
                    (boot_id,sequence,parent_sequence,layer,operation,started_at_utc,started_timestamp,active_at_start,
                     finished_at_utc,duration_ms,outcome,revision)
                VALUES (@boot,@sequence,@parent,@layer,@operation,@start,@tick,@active,@finish,@duration,@outcome,@revision)
                ON CONFLICT (boot_id,sequence) DO UPDATE SET
                    finished_at_utc=EXCLUDED.finished_at_utc,duration_ms=EXCLUDED.duration_ms,
                    outcome=EXCLUDED.outcome,revision=EXCLUDED.revision
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
            batch.BatchCommands.Add(command);
        }
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class RequestArrivalObservationFlushService(
    RequestArrivalObservations buffer, RequestArrivalObservationStore store, TimeProvider time,
    ILogger<RequestArrivalObservationFlushService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _flush = new(1);
    private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!buffer.Enabled) return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2), time);
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
            if (coverage.WindowClosed && coverage.HttpActive == 0 && coverage.ToolActive == 0 && coverage.Pending == 0) break;
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task FlushAsync(bool stopped, CancellationToken cancellationToken)
    {
        if (!buffer.Enabled) return;
        await _flush.WaitAsync(cancellationToken);
        try
        {
            var snapshot = buffer.Snapshot();
            await store.WriteAsync(snapshot.Coverage, snapshot.Samples, time.GetUtcNow(), stopped, cancellationToken);
            buffer.Acknowledge(snapshot.Samples);
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

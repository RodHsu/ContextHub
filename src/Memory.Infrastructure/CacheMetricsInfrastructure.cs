using Memory.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memory.Infrastructure;

public interface ICacheMetricsStore
{
    Task WriteAsync(CacheMetricsBuffer buffer, IReadOnlyList<CacheMetricBucket> snapshots, DateTimeOffset now, bool stopped, CancellationToken cancellationToken);
}

public sealed class DatabaseCacheMetricsStore(NpgsqlDataSource dataSource) : ICacheMetricsStore
{
    public async Task WriteAsync(CacheMetricsBuffer buffer, IReadOnlyList<CacheMetricBucket> snapshots, DateTimeOffset now, bool stopped, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection, transaction) { Timeout = 20 };
        foreach (var row in snapshots)
        {
            var command = new NpgsqlBatchCommand("""
                INSERT INTO monitoring.cache_metric_minutes
                    (instance_id, boot_id, bucket_start_utc, kind, traffic_class, revision,
                     hits, misses, sets, bypasses, errors, invalid_payloads, duration_ms, observations)
                VALUES (@instance, @boot, @minute, @kind, @traffic, @revision,
                        @hits, @misses, @sets, @bypasses, @errors, @invalid, @duration, @observations)
                ON CONFLICT (instance_id, boot_id, bucket_start_utc, kind, traffic_class) DO UPDATE SET
                    revision = EXCLUDED.revision, hits = EXCLUDED.hits, misses = EXCLUDED.misses,
                    sets = EXCLUDED.sets, bypasses = EXCLUDED.bypasses, errors = EXCLUDED.errors,
                    invalid_payloads = EXCLUDED.invalid_payloads, duration_ms = EXCLUDED.duration_ms,
                    observations = EXCLUDED.observations
                WHERE monitoring.cache_metric_minutes.revision < EXCLUDED.revision;
                """);
            command.Parameters.AddWithValue("instance", row.InstanceId);
            command.Parameters.AddWithValue("boot", row.BootId);
            command.Parameters.AddWithValue("minute", row.BucketStartUtc);
            command.Parameters.AddWithValue("kind", row.Kind);
            command.Parameters.AddWithValue("traffic", row.TrafficClass);
            command.Parameters.AddWithValue("revision", row.Revision);
            command.Parameters.AddWithValue("hits", row.Hits);
            command.Parameters.AddWithValue("misses", row.Misses);
            command.Parameters.AddWithValue("sets", row.Sets);
            command.Parameters.AddWithValue("bypasses", row.Bypasses);
            command.Parameters.AddWithValue("errors", row.Errors);
            command.Parameters.AddWithValue("invalid", row.InvalidPayloads);
            command.Parameters.AddWithValue("duration", row.DurationMs);
            command.Parameters.AddWithValue("observations", row.Observations);
            batch.BatchCommands.Add(command);
        }
        var heartbeat = new NpgsqlBatchCommand("""
            INSERT INTO monitoring.cache_metric_boots
                (instance_id, boot_id, started_at_utc, last_flushed_at_utc, stopped_at_utc, pending_buckets, dropped_samples)
            VALUES (@instance, @boot, @started, @now, CASE WHEN @stopped THEN @now ELSE NULL END, 0, @dropped)
            ON CONFLICT (instance_id, boot_id) DO UPDATE SET
                last_flushed_at_utc = GREATEST(cache_metric_boots.last_flushed_at_utc, EXCLUDED.last_flushed_at_utc),
                stopped_at_utc = COALESCE(EXCLUDED.stopped_at_utc, cache_metric_boots.stopped_at_utc),
                dropped_samples = GREATEST(cache_metric_boots.dropped_samples, EXCLUDED.dropped_samples);
            """);
        heartbeat.Parameters.AddWithValue("instance", buffer.InstanceId);
        heartbeat.Parameters.AddWithValue("boot", buffer.BootId);
        heartbeat.Parameters.AddWithValue("started", buffer.StartedAtUtc);
        heartbeat.Parameters.AddWithValue("now", now);
        heartbeat.Parameters.AddWithValue("stopped", stopped);
        heartbeat.Parameters.AddWithValue("dropped", buffer.DroppedSamples);
        batch.BatchCommands.Add(heartbeat);
        var coverage = new NpgsqlBatchCommand("""
            INSERT INTO monitoring.cache_metric_coverage (instance_id, boot_id, bucket_start_utc)
            VALUES (@instance, @boot, @minute) ON CONFLICT DO NOTHING;
            """);
        coverage.Parameters.AddWithValue("instance", buffer.InstanceId);
        coverage.Parameters.AddWithValue("boot", buffer.BootId);
        coverage.Parameters.AddWithValue("minute", CacheMetricsBuffer.TruncateMinute(now));
        batch.BatchCommands.Add(coverage);
        await batch.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }
}

public sealed class CacheMetricsFlushService(
    CacheMetricsBuffer buffer, ICacheMetricsStore store, TimeProvider timeProvider,
    ILogger<CacheMetricsFlushService> logger) : BackgroundService
{
    private readonly SemaphoreSlim _flush = new(1);
    private DateTimeOffset _lastWarning = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(15), timeProvider);
        do
        {
            try { await FlushAsync(false, stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (Exception ex)
            {
                if (timeProvider.GetUtcNow() - _lastWarning >= TimeSpan.FromMinutes(5))
                {
                    _lastWarning = timeProvider.GetUtcNow();
                    logger.LogWarning(ex, "Cache metric flush failed; bounded samples retained for retry. Dropped events: {Dropped}", buffer.DroppedSamples);
                }
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    public async Task FlushAsync(bool stopped, CancellationToken cancellationToken)
    {
        await _flush.WaitAsync(cancellationToken);
        try
        {
            var snapshots = buffer.Snapshot();
            await store.WriteAsync(buffer, snapshots, timeProvider.GetUtcNow(), stopped, cancellationToken);
            buffer.Acknowledge(snapshots);
        }
        finally { _flush.Release(); }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);
        try { await FlushAsync(true, cancellationToken); }
        catch (Exception ex) { logger.LogWarning(ex, "Final cache metric flush failed; this boot will be reported as unclean."); }
    }
}

public sealed class DatabaseCacheMetricsQueryService(
    NpgsqlDataSource dataSource, IRequestActorAccessor actorAccessor, TimeProvider timeProvider) : ICacheMetricsQueryService
{
    public async Task<CacheMetricsWindowResult> GetAsync(string period, CancellationToken cancellationToken)
    {
        ActorAuthorization.EnsureAuthenticatedUser(actorAccessor.Current);
        if (!actorAccessor.Current.IsAdmin) throw new UnauthorizedAccessException("Administrator access is required for instance cache metrics.");
        var days = period switch { "24H" => 1, "3D" => 3, "7D" => 7, "14D" => 14, "30D" => 30, _ => throw new ArgumentException("Unsupported cache metric period.", nameof(period)) };
        var now = timeProvider.GetUtcNow();
        var end = CacheMetricsBuffer.TruncateMinute(now);
        var start = end.AddDays(-days);
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 20;
            command.CommandText = """
                SELECT kind, traffic_class, SUM(hits)::bigint, SUM(misses)::bigint, SUM(sets)::bigint,
                       SUM(bypasses)::bigint, SUM(errors)::bigint, SUM(invalid_payloads)::bigint, SUM(duration_ms), SUM(observations)::bigint
                FROM monitoring.cache_metric_minutes
                WHERE bucket_start_utc >= @start AND bucket_start_utc < @end
                GROUP BY kind, traffic_class ORDER BY kind, traffic_class;
                SELECT instance_id, boot_id, started_at_utc, last_flushed_at_utc, pending_buckets, dropped_samples,
                       stopped_at_utc IS NULL AND last_flushed_at_utc < @stale AS unclean,
                       (SELECT COUNT(*) FROM monitoring.cache_metric_coverage c
                        WHERE c.instance_id = b.instance_id AND c.boot_id = b.boot_id
                          AND c.bucket_start_utc >= @start AND c.bucket_start_utc < @end) AS observed_minutes,
                       GREATEST(0, EXTRACT(EPOCH FROM (LEAST(@end, COALESCE(date_trunc('minute', stopped_at_utc), @end))
                         - GREATEST(@start, date_trunc('minute', started_at_utc)))) / 60)::bigint AS expected_minutes,
                       stopped_at_utc
                FROM monitoring.cache_metric_boots b
                WHERE last_flushed_at_utc >= @start ORDER BY instance_id, started_at_utc;
                SELECT MIN(started_at_utc) FROM monitoring.cache_metric_boots;
                """;
            command.Parameters.AddWithValue("start", start);
            command.Parameters.AddWithValue("end", end);
            command.Parameters.AddWithValue("stale", now.AddSeconds(-45));
            var series = new List<CacheMetricsSeriesResult>();
            var instances = new List<CacheMetricsInstanceResult>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                series.Add(new(reader.GetString(0), reader.GetString(1), reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt64(5), reader.GetInt64(6), reader.GetInt64(7), reader.GetDouble(8), reader.GetInt64(9)));
            await reader.NextResultAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                instances.Add(new(reader.GetString(0), reader.GetGuid(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3), reader.GetInt32(4), reader.GetInt64(5), reader.GetBoolean(6), reader.GetInt64(7), reader.GetInt64(8), reader.IsDBNull(9) ? null : reader.GetFieldValue<DateTimeOffset>(9)));
            await reader.NextResultAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            var coverageStart = reader.IsDBNull(0) ? (DateTimeOffset?)null : reader.GetFieldValue<DateTimeOffset>(0);
            var dropped = instances.Sum(x => x.DroppedSamples);
            var coverage = coverageStart is null || coverageStart > start || dropped > 0
                || instances.Count == 0 || instances.Max(x => x.LastFlushedAtUtc) < now.AddSeconds(-45)
                || instances.Any(x => x.UncleanShutdown || x.ObservedMinutes < x.ExpectedMinutes) ? "Partial" : "Observed";
            return new(true, period, start, end, now, coverage, dropped, series, instances, coverageStart);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            return new(false, period, start, end, now, "Unavailable", 0, [], []);
        }
    }
}

public static class CacheMetricsServiceCollectionExtensions
{
    public static IServiceCollection AddCacheMetrics(this IServiceCollection services)
    {
        services.AddSingleton(sp => new CacheMetricsBuffer(sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<ICacheMetricsRecorder>(sp => sp.GetRequiredService<CacheMetricsBuffer>());
        services.AddSingleton<ICacheMetricsStore, DatabaseCacheMetricsStore>();
        services.AddHostedService<CacheMetricsFlushService>();
        services.AddScoped<ICacheMetricsQueryService, DatabaseCacheMetricsQueryService>();
        return services;
    }
}

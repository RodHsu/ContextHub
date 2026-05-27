using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Memory.Infrastructure;

public sealed class RedisMaintenanceModeStore(
    IConnectionMultiplexer redis,
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    TimeProvider timeProvider) : IMaintenanceModeStore
{
    private const string StateKey = "context-hub:maintenance:state";
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<MaintenanceModeStateResult> GetAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _database.StringGetAsync(StateKey);
        return payload.IsNullOrEmpty
            ? Inactive
            : JsonSerializer.Deserialize<MaintenanceModeStateResult>(payload.ToString(), SerializerOptions) ?? Inactive;
    }

    public async Task<MaintenanceModeStateResult> EnableAsync(MaintenanceModeRequest request, string triggeredBy, CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        if (current.Active)
        {
            return current;
        }

        var now = timeProvider.GetUtcNow();
        var estimatedEndsAt = request.EstimatedEndsAtUtc
            ?? now.AddMinutes(Math.Clamp(request.EstimatedDurationMinutes ?? 90, 1, 24 * 60));
        var run = new MaintenanceRun
        {
            MaintenanceType = MaintenanceRunType.MaintenanceMode,
            Status = MaintenanceRunStatus.Running,
            StartedAt = now,
            TriggeredBy = NormalizeTriggeredBy(request.TriggeredBy, triggeredBy),
            PolicyJson = JsonSerializer.Serialize(new
            {
                reason = request.Reason ?? "Maintenance",
                estimatedEndsAtUtc = estimatedEndsAt
            }, SerializerOptions)
        };

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            dbContext.MaintenanceRuns.Add(run);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var state = new MaintenanceModeStateResult(
            true,
            request.Reason?.Trim() is { Length: > 0 } reason ? reason : "Maintenance",
            request.Message?.Trim() is { Length: > 0 } message ? message : "ContextHub is temporarily unavailable due to maintenance.",
            now,
            estimatedEndsAt,
            run.Id,
            run.TriggeredBy);
        await _database.StringSetAsync(StateKey, JsonSerializer.Serialize(state, SerializerOptions));
        return state;
    }

    public async Task<MaintenanceModeStateResult> DisableAsync(string triggeredBy, CancellationToken cancellationToken)
    {
        var current = await GetAsync(cancellationToken);
        await _database.KeyDeleteAsync(StateKey);

        if (current.RunId.HasValue)
        {
            var now = timeProvider.GetUtcNow();
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var run = await dbContext.MaintenanceRuns.FirstOrDefaultAsync(x => x.Id == current.RunId.Value, cancellationToken);
            if (run is not null)
            {
                run.Status = MaintenanceRunStatus.Completed;
                run.CompletedAt = now;
                run.ResultJson = JsonSerializer.Serialize(new
                {
                    disabledBy = NormalizeTriggeredBy(null, triggeredBy),
                    activeFromUtc = current.StartedAtUtc,
                    disabledAtUtc = now
                }, SerializerOptions);
                await dbContext.SaveChangesAsync(cancellationToken);
            }
        }

        return Inactive;
    }

    private static MaintenanceModeStateResult Inactive { get; } = new(false, string.Empty, string.Empty, null, null, null, string.Empty);

    private static string NormalizeTriggeredBy(string? requested, string fallback)
        => requested?.Trim() is { Length: > 0 } value
            ? value
            : string.IsNullOrWhiteSpace(fallback)
                ? "system"
                : fallback.Trim();
}

public sealed class MaintenanceRunQueryService(IDbContextFactory<MemoryDbContext> dbContextFactory) : IMaintenanceRunQueryService
{
    public async Task<IReadOnlyList<MaintenanceRunResult>> ListRunsAsync(int limit, CancellationToken cancellationToken)
    {
        var normalizedLimit = Math.Clamp(limit, 1, 500);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var runs = await dbContext.MaintenanceRuns
            .AsNoTracking()
            .OrderByDescending(x => x.StartedAt)
            .ThenByDescending(x => x.Id)
            .Take(normalizedLimit)
            .ToListAsync(cancellationToken);

        return runs.Select(ToResult).ToList();
    }

    private static MaintenanceRunResult ToResult(MaintenanceRun run)
        => new(
            run.Id,
            run.MaintenanceType,
            run.Status,
            run.StartedAt,
            run.CompletedAt,
            run.TriggeredBy,
            run.PolicyJson,
            run.ResultJson,
            run.Error);
}

public sealed class RetrievalTelemetryRetentionService(
    NpgsqlDataSource dataSource,
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    IOptions<TelemetryRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetrievalTelemetryRetentionService> logger) : IRetrievalTelemetryRetentionService
{
    private const long AdvisoryLockKey = 941222;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly TelemetryRetentionOptions _options = options.Value;

    public async Task<RetrievalTelemetryRetentionRunResult> RunAsync(string triggeredBy, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var startedAt = now;
        var hitsCutoff = now.AddDays(-Math.Max(1, _options.HitsRetentionDays));
        var eventsCutoff = now.AddDays(-Math.Max(1, _options.EventsRetentionDays));
        var run = new MaintenanceRun
        {
            MaintenanceType = MaintenanceRunType.RetrievalTelemetryRetention,
            Status = MaintenanceRunStatus.Running,
            StartedAt = startedAt,
            TriggeredBy = NormalizeTriggeredBy(triggeredBy),
            PolicyJson = JsonSerializer.Serialize(new
            {
                hitsRetentionDays = _options.HitsRetentionDays,
                eventsRetentionDays = _options.EventsRetentionDays,
                hitsCutoffUtc = hitsCutoff,
                eventsCutoffUtc = eventsCutoff,
                batchSize = _options.BatchSize,
                delayBetweenBatchesMs = _options.DelayBetweenBatchesMs,
                runVacuumFullAutomatically = _options.RunVacuumFullAutomatically
            }, SerializerOptions)
        };

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            dbContext.MaintenanceRuns.Add(run);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var lockCommand = connection.CreateCommand();
        lockCommand.CommandTimeout = CommandTimeoutSeconds;
        lockCommand.CommandText = "SELECT pg_try_advisory_lock(@lock_key);";
        lockCommand.Parameters.Add(new NpgsqlParameter<long>("lock_key", AdvisoryLockKey));
        var locked = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken) ?? false);
        if (!locked)
        {
            var completedAt = timeProvider.GetUtcNow();
            var skippedJson = JsonSerializer.Serialize(new { skipped = true, reason = "Another retrieval telemetry retention run is active." }, SerializerOptions);
            await CompleteRunAsync(run.Id, MaintenanceRunStatus.Completed, completedAt, skippedJson, string.Empty, cancellationToken);
            return new RetrievalTelemetryRetentionRunResult(run.Id, hitsCutoff, eventsCutoff, 0, 0, startedAt, completedAt, skippedJson);
        }

        try
        {
            var sizeBefore = await ReadTableSizesAsync(connection, cancellationToken);
            var deletedHits = await DeleteHitsAsync(connection, hitsCutoff, cancellationToken);
            var deletedEvents = await DeleteEventsAsync(connection, eventsCutoff, cancellationToken);
            await AnalyzeAsync(connection, cancellationToken);
            var sizeAfter = await ReadTableSizesAsync(connection, cancellationToken);
            var completedAt = timeProvider.GetUtcNow();
            var resultJson = JsonSerializer.Serialize(new
            {
                deletedHits,
                deletedEvents,
                startedAtUtc = startedAt,
                completedAtUtc = completedAt,
                durationMs = (completedAt - startedAt).TotalMilliseconds,
                tableSizeBeforeBytes = sizeBefore,
                tableSizeAfterBytes = sizeAfter,
                vacuumFullRequested = false,
                vacuumFullCompleted = false
            }, SerializerOptions);
            await CompleteRunAsync(run.Id, MaintenanceRunStatus.Completed, completedAt, resultJson, string.Empty, cancellationToken);
            return new RetrievalTelemetryRetentionRunResult(run.Id, hitsCutoff, eventsCutoff, deletedHits, deletedEvents, startedAt, completedAt, resultJson);
        }
        catch (Exception ex)
        {
            var completedAt = timeProvider.GetUtcNow();
            logger.LogError(ex, "Retrieval telemetry retention run {MaintenanceRunId} failed.", run.Id);
            await CompleteRunAsync(run.Id, MaintenanceRunStatus.Failed, completedAt, "{}", ex.Message, cancellationToken);
            throw;
        }
        finally
        {
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandTimeout = CommandTimeoutSeconds;
            unlockCommand.CommandText = "SELECT pg_advisory_unlock(@lock_key);";
            unlockCommand.Parameters.Add(new NpgsqlParameter<long>("lock_key", AdvisoryLockKey));
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<long> DeleteHitsAsync(NpgsqlConnection connection, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                WITH target AS (
                    SELECT h.id
                    FROM retrieval_hits h
                    JOIN retrieval_events e ON e.id = h.retrieval_event_id
                    WHERE e.created_at < @cutoff
                    LIMIT @batch_size
                ),
                deleted AS (
                    DELETE FROM retrieval_hits h
                    USING target
                    WHERE h.id = target.id
                    RETURNING 1
                )
                SELECT COUNT(*)::bigint FROM deleted;
                """;
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));
            command.Parameters.Add(new NpgsqlParameter<int>("batch_size", BatchSize));
            var deleted = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            total += deleted;
            if (deleted == 0)
            {
                return total;
            }

            await DelayBetweenBatchesAsync(cancellationToken);
        }
    }

    private async Task<long> DeleteEventsAsync(NpgsqlConnection connection, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        long total = 0;
        while (true)
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = CommandTimeoutSeconds;
            command.CommandText = """
                WITH target AS (
                    SELECT id
                    FROM retrieval_events
                    WHERE created_at < @cutoff
                    ORDER BY created_at ASC, id ASC
                    LIMIT @batch_size
                ),
                deleted AS (
                    DELETE FROM retrieval_events e
                    USING target
                    WHERE e.id = target.id
                    RETURNING 1
                )
                SELECT COUNT(*)::bigint FROM deleted;
                """;
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));
            command.Parameters.Add(new NpgsqlParameter<int>("batch_size", BatchSize));
            var deleted = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            total += deleted;
            if (deleted == 0)
            {
                return total;
            }

            await DelayBetweenBatchesAsync(cancellationToken);
        }
    }

    private async Task AnalyzeAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = "ANALYZE retrieval_hits; ANALYZE retrieval_events;";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadTableSizesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = CommandTimeoutSeconds;
        command.CommandText = """
            SELECT relname, pg_total_relation_size(relid)::bigint
            FROM pg_catalog.pg_statio_user_tables
            WHERE relname IN ('retrieval_events', 'retrieval_hits')
            ORDER BY relname;
            """;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sizes[reader.GetString(0)] = reader.GetInt64(1);
        }

        return sizes;
    }

    private async Task CompleteRunAsync(Guid runId, MaintenanceRunStatus status, DateTimeOffset completedAt, string resultJson, string error, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.MaintenanceRuns.FirstAsync(x => x.Id == runId, cancellationToken);
        run.Status = status;
        run.CompletedAt = completedAt;
        run.ResultJson = resultJson;
        run.Error = error;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private Task DelayBetweenBatchesAsync(CancellationToken cancellationToken)
        => _options.DelayBetweenBatchesMs <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(_options.DelayBetweenBatchesMs), cancellationToken);

    private int BatchSize => Math.Clamp(_options.BatchSize, 1, 100_000);
    private int CommandTimeoutSeconds => Math.Clamp(_options.CommandTimeoutSeconds, 1, 3600);

    private static string NormalizeTriggeredBy(string triggeredBy)
        => string.IsNullOrWhiteSpace(triggeredBy) ? "system" : triggeredBy.Trim();
}

public sealed class VacuumFullReclaimService(
    NpgsqlDataSource dataSource,
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<VacuumFullReclaimService> logger) : IVacuumFullReclaimService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task<VacuumFullReclaimRunResult> RunAsync(string triggeredBy, CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var run = new MaintenanceRun
        {
            MaintenanceType = MaintenanceRunType.VacuumFullReclaim,
            Status = MaintenanceRunStatus.Running,
            StartedAt = startedAt,
            TriggeredBy = string.IsNullOrWhiteSpace(triggeredBy) ? "system" : triggeredBy.Trim(),
            PolicyJson = JsonSerializer.Serialize(new
            {
                tables = new[] { "retrieval_hits", "retrieval_events" },
                command = "VACUUM FULL ANALYZE",
                automatic = false
            }, SerializerOptions)
        };

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            dbContext.MaintenanceRuns.Add(run);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            var sizeBefore = await ReadTableSizesAsync(connection, cancellationToken);
            await VacuumFullAnalyzeAsync(connection, "retrieval_hits", cancellationToken);
            await VacuumFullAnalyzeAsync(connection, "retrieval_events", cancellationToken);

            var sizeAfter = await ReadTableSizesAsync(connection, cancellationToken);
            var completedAt = timeProvider.GetUtcNow();
            var resultJson = JsonSerializer.Serialize(new
            {
                startedAtUtc = startedAt,
                completedAtUtc = completedAt,
                durationMs = (completedAt - startedAt).TotalMilliseconds,
                tableSizeBeforeBytes = sizeBefore,
                tableSizeAfterBytes = sizeAfter,
                vacuumFullRequested = true,
                vacuumFullCompleted = true
            }, SerializerOptions);
            await CompleteRunAsync(run.Id, MaintenanceRunStatus.Completed, completedAt, resultJson, string.Empty, cancellationToken);
            return new VacuumFullReclaimRunResult(run.Id, startedAt, completedAt, resultJson);
        }
        catch (Exception ex)
        {
            var completedAt = timeProvider.GetUtcNow();
            logger.LogError(ex, "VACUUM FULL reclaim run {MaintenanceRunId} failed.", run.Id);
            await CompleteRunAsync(run.Id, MaintenanceRunStatus.Failed, completedAt, "{}", ex.Message, cancellationToken);
            throw;
        }
    }

    private static async Task<IReadOnlyDictionary<string, long>> ReadTableSizesAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 120;
        command.CommandText = """
            SELECT relname, pg_total_relation_size(relid)::bigint
            FROM pg_catalog.pg_statio_user_tables
            WHERE relname IN ('retrieval_events', 'retrieval_hits')
            ORDER BY relname;
            """;
        var sizes = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            sizes[reader.GetString(0)] = reader.GetInt64(1);
        }

        return sizes;
    }

    private static async Task VacuumFullAnalyzeAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 7200;
        command.CommandText = $"VACUUM FULL ANALYZE {table};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task CompleteRunAsync(Guid runId, MaintenanceRunStatus status, DateTimeOffset completedAt, string resultJson, string error, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.MaintenanceRuns.FirstAsync(x => x.Id == runId, cancellationToken);
        run.Status = status;
        run.CompletedAt = completedAt;
        run.ResultJson = resultJson;
        run.Error = error;
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}

public sealed class TelemetryRetentionHostedService(
    IServiceProvider serviceProvider,
    IOptions<TelemetryRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<TelemetryRetentionHostedService> logger) : BackgroundService
{
    private readonly TelemetryRetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Telemetry retention hosted service is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun(timeProvider.GetUtcNow());
            await Task.Delay(delay, stoppingToken);

            try
            {
                using var scope = serviceProvider.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IRetrievalTelemetryRetentionService>();
                await service.RunAsync("scheduled", stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled retrieval telemetry retention failed.");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    internal TimeSpan GetDelayUntilNextRun(DateTimeOffset nowUtc)
    {
        var timeZone = ResolveTimeZone(_options.TimeZone);
        var localNow = TimeZoneInfo.ConvertTime(nowUtc, timeZone);
        var runAt = TimeOnly.TryParse(_options.RunAtLocalTime, out var parsed)
            ? parsed
            : new TimeOnly(3, 30);
        var nextLocal = new DateTimeOffset(localNow.Date + runAt.ToTimeSpan(), localNow.Offset);
        if (nextLocal <= localNow)
        {
            nextLocal = nextLocal.AddDays(1);
        }

        var nextUtc = TimeZoneInfo.ConvertTime(nextLocal, TimeZoneInfo.Utc);
        var delay = nextUtc - nowUtc;
        return delay <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : delay;
    }

    private static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId.Trim());
        }
        catch (TimeZoneNotFoundException) when (string.Equals(timeZoneId, "Asia/Taipei", StringComparison.OrdinalIgnoreCase))
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Taipei Standard Time");
        }
    }
}

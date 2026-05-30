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

public sealed record RetrievalTelemetryRetentionPolicy(
    int HitsRetentionDays,
    int EventsRetentionDays,
    int BatchSize,
    int EventBatchSize,
    int TimeWindowDays,
    int DelayBetweenBatchesMs,
    int CommandTimeoutSeconds,
    TimeSpan MaxDuration,
    bool RunVacuumAnalyzeAfterRetention,
    bool RunVacuumFullAutomatically)
{
    public static RetrievalTelemetryRetentionPolicy Create(
        TelemetryRetentionOptions options,
        RetrievalTelemetryRetentionRunRequest request)
        => new(
            Math.Max(1, options.HitsRetentionDays),
            Math.Max(1, options.EventsRetentionDays),
            Math.Clamp(request.BatchSize ?? options.BatchSize, 1, 100_000),
            Math.Clamp(request.EventBatchSize ?? options.EventBatchSize, 1, 100_000),
            Math.Clamp(request.TimeWindowDays ?? options.TimeWindowDays, 1, 3),
            Math.Clamp(request.DelayBetweenBatchesMs ?? options.DelayBetweenBatchesMs, 0, 60_000),
            Math.Clamp(request.CommandTimeoutSeconds ?? options.CommandTimeoutSeconds, 1, 3600),
            TimeSpan.FromMinutes(Math.Clamp(request.MaxDurationMinutes ?? options.MaxDurationMinutes, 1, 30)),
            request.RunVacuumAnalyzeAfterRetention ?? options.RunVacuumAnalyzeAfterRetention,
            options.RunVacuumFullAutomatically);
}

public sealed class RetrievalTelemetryRetentionService(
    NpgsqlDataSource dataSource,
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    IOptions<TelemetryRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<RetrievalTelemetryRetentionService> logger) : IRetrievalTelemetryRetentionService
{
    private const long AdvisoryLockKey = 941222;
    private const int VacuumFullCommandTimeoutSeconds = 7200;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly TelemetryRetentionOptions _options = options.Value;

    public async Task<RetrievalTelemetryRetentionRunResult> RunAsync(string triggeredBy, CancellationToken cancellationToken)
        => await RunAsync(new RetrievalTelemetryRetentionRunRequest(TriggeredBy: triggeredBy), triggeredBy, cancellationToken);

    public async Task<RetrievalTelemetryRetentionRunResult> RunAsync(RetrievalTelemetryRetentionRunRequest request, string fallbackTriggeredBy, CancellationToken cancellationToken)
    {
        var policy = RetrievalTelemetryRetentionPolicy.Create(_options, request);
        var now = timeProvider.GetUtcNow();
        var startedAt = now;
        var hitsCutoff = now.AddDays(-policy.HitsRetentionDays);
        var eventsCutoff = now.AddDays(-policy.EventsRetentionDays);
        var run = new MaintenanceRun
        {
            MaintenanceType = MaintenanceRunType.RetrievalTelemetryRetention,
            Status = MaintenanceRunStatus.Running,
            StartedAt = startedAt,
            TriggeredBy = NormalizeTriggeredBy(request.TriggeredBy, fallbackTriggeredBy),
            PolicyJson = JsonSerializer.Serialize(new
            {
                hitsRetentionDays = policy.HitsRetentionDays,
                eventsRetentionDays = policy.EventsRetentionDays,
                hitsCutoffUtc = hitsCutoff,
                eventsCutoffUtc = eventsCutoff,
                batchSize = policy.BatchSize,
                eventBatchSize = policy.EventBatchSize,
                timeWindowDays = policy.TimeWindowDays,
                delayBetweenBatchesMs = policy.DelayBetweenBatchesMs,
                commandTimeoutSeconds = policy.CommandTimeoutSeconds,
                maxDurationMinutes = policy.MaxDuration.TotalMinutes,
                runVacuumAnalyzeAfterRetention = policy.RunVacuumAnalyzeAfterRetention,
                runVacuumFullAutomatically = policy.RunVacuumFullAutomatically
            }, SerializerOptions)
        };

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            dbContext.MaintenanceRuns.Add(run);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var lockCommand = connection.CreateCommand();
        lockCommand.CommandTimeout = policy.CommandTimeoutSeconds;
        lockCommand.CommandText = "SELECT pg_try_advisory_lock(@lock_key);";
        lockCommand.Parameters.Add(new NpgsqlParameter<long>("lock_key", AdvisoryLockKey));
        var locked = (bool)(await lockCommand.ExecuteScalarAsync(cancellationToken) ?? false);
        if (!locked)
        {
            var completedAt = timeProvider.GetUtcNow();
            var skippedJson = BuildResultJson(
                startedAt,
                completedAt,
                0,
                0,
                null,
                null,
                policy,
                skipped: true,
                completed: true,
                stoppedReason: "anotherRunActive");
            await UpdateRunAsync(run.Id, MaintenanceRunStatus.Completed, completedAt, skippedJson, string.Empty, cancellationToken);
            return new RetrievalTelemetryRetentionRunResult(run.Id, hitsCutoff, eventsCutoff, 0, 0, startedAt, completedAt, skippedJson);
        }

        var deletedHits = 0L;
        var deletedEvents = 0L;
        IReadOnlyDictionary<string, long>? sizeBefore = null;
        IReadOnlyDictionary<string, long>? sizeAfter = null;
        RetentionWindow? hitsWindow = null;
        RetentionWindow? eventsWindow = null;
        RetentionProgress? progress = null;
        var vacuumAnalyzeCompleted = false;
        string? vacuumAnalyzeError = null;
        var vacuumFullCompleted = false;
        string? vacuumFullError = null;

        try
        {
            sizeBefore = await ReadTableSizesAsync(connection, policy, cancellationToken);
            progress = new RetentionProgress(run.Id, startedAt, policy, sizeBefore);

            while (!ShouldStopForMaxDuration(startedAt, policy, out _))
            {
                hitsWindow = await ResolveRetentionWindowAsync(connection, hitsCutoff, policy, requiresHits: true, cancellationToken);
                if (hitsWindow is null)
                {
                    break;
                }

                progress.CurrentHitsWindow = hitsWindow;
                await DeleteHitsAsync(connection, progress, hitsCutoff, hitsWindow, cancellationToken);
                progress.ProcessedHitsWindows++;
            }

            deletedHits = progress.DeletedHits;

            while (!ShouldStopForMaxDuration(startedAt, policy, out _))
            {
                eventsWindow = await ResolveRetentionWindowAsync(connection, eventsCutoff, policy, requiresHits: false, cancellationToken);
                if (eventsWindow is null)
                {
                    break;
                }

                progress.CurrentEventsWindow = eventsWindow;
                await DeleteEventsAsync(connection, progress, eventsCutoff, eventsWindow, cancellationToken);
                progress.ProcessedEventsWindows++;
            }

            deletedEvents = progress.DeletedEvents;
            if (ShouldStopForMaxDuration(startedAt, policy, out var stoppedReason))
            {
                var stoppedAt = timeProvider.GetUtcNow();
                sizeAfter = await ReadTableSizesAsync(connection, policy, cancellationToken);
                var stoppedJson = BuildResultJson(
                    startedAt,
                    stoppedAt,
                    deletedHits,
                    deletedEvents,
                    sizeBefore,
                    sizeAfter,
                    policy,
                    hitsWindow,
                    eventsWindow,
                    progress.ProcessedHitsWindows,
                    progress.ProcessedEventsWindows,
                    vacuumAnalyzeCompleted: vacuumAnalyzeCompleted,
                    vacuumAnalyzeError: vacuumAnalyzeError,
                    vacuumFullCompleted: vacuumFullCompleted,
                    vacuumFullError: vacuumFullError,
                    completed: true,
                    stoppedReason: stoppedReason);
                await UpdateRunAsync(run.Id, MaintenanceRunStatus.Completed, stoppedAt, stoppedJson, string.Empty, cancellationToken);
                return new RetrievalTelemetryRetentionRunResult(run.Id, hitsCutoff, eventsCutoff, deletedHits, deletedEvents, startedAt, stoppedAt, stoppedJson);
            }

            if (policy.RunVacuumFullAutomatically)
            {
                try
                {
                    await VacuumFullAnalyzeAsync(connection, "retrieval_hits", cancellationToken);
                    await VacuumFullAnalyzeAsync(connection, "retrieval_events", cancellationToken);
                    vacuumAnalyzeCompleted = true;
                    vacuumFullCompleted = true;
                }
                catch (Exception ex)
                {
                    vacuumFullError = ex.Message;
                    throw;
                }
            }
            else if (policy.RunVacuumAnalyzeAfterRetention)
            {
                try
                {
                    await VacuumAnalyzeAsync(connection, policy, cancellationToken);
                    vacuumAnalyzeCompleted = true;
                }
                catch (Exception ex)
                {
                    vacuumAnalyzeError = ex.Message;
                    throw;
                }
            }

            sizeAfter = await ReadTableSizesAsync(connection, policy, cancellationToken);
            var completedAt = timeProvider.GetUtcNow();
            var resultJson = BuildResultJson(
                startedAt,
                completedAt,
                deletedHits,
                deletedEvents,
                sizeBefore,
                sizeAfter,
                policy,
                hitsWindow,
                eventsWindow,
                progress.ProcessedHitsWindows,
                progress.ProcessedEventsWindows,
                vacuumAnalyzeCompleted: vacuumAnalyzeCompleted,
                vacuumAnalyzeError: vacuumAnalyzeError,
                vacuumFullCompleted: vacuumFullCompleted,
                vacuumFullError: vacuumFullError,
                completed: true);
            await UpdateRunAsync(run.Id, MaintenanceRunStatus.Completed, completedAt, resultJson, string.Empty, cancellationToken);
            return new RetrievalTelemetryRetentionRunResult(run.Id, hitsCutoff, eventsCutoff, deletedHits, deletedEvents, startedAt, completedAt, resultJson);
        }
        catch (Exception ex)
        {
            var completedAt = timeProvider.GetUtcNow();
            logger.LogError(ex, "Retrieval telemetry retention run {MaintenanceRunId} failed.", run.Id);
            var failedJson = BuildResultJson(
                startedAt,
                completedAt,
                deletedHits,
                deletedEvents,
                sizeBefore,
                sizeAfter,
                policy,
                hitsWindow,
                eventsWindow,
                progress?.ProcessedHitsWindows ?? 0,
                progress?.ProcessedEventsWindows ?? 0,
                vacuumAnalyzeCompleted: vacuumAnalyzeCompleted,
                vacuumAnalyzeError: vacuumAnalyzeError,
                vacuumFullCompleted: vacuumFullCompleted,
                vacuumFullError: vacuumFullError,
                completed: true,
                error: ex.Message);
            await UpdateRunAsync(run.Id, MaintenanceRunStatus.Failed, completedAt, failedJson, ex.Message, CancellationToken.None);
            throw;
        }
        finally
        {
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandTimeout = policy.CommandTimeoutSeconds;
            unlockCommand.CommandText = "SELECT pg_advisory_unlock(@lock_key);";
            unlockCommand.Parameters.Add(new NpgsqlParameter<long>("lock_key", AdvisoryLockKey));
            await unlockCommand.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<long> DeleteHitsAsync(
        NpgsqlConnection connection,
        RetentionProgress progress,
        DateTimeOffset cutoff,
        RetentionWindow? window,
        CancellationToken cancellationToken)
    {
        if (window is null)
        {
            return 0;
        }

        long total = 0;
        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;
        while (true)
        {
            if (ShouldStopForMaxDuration(progress.StartedAt, progress.Policy, out _))
            {
                return total;
            }

            var eventWindow = await ReadEventWindowAsync(connection, cutoff, window, afterCreatedAt, afterId, progress.Policy, cancellationToken);
            if (eventWindow.EventIds.Count == 0)
            {
                return total;
            }

            var deleted = await DeleteHitBatchAsync(connection, eventWindow.EventIds, progress.Policy, cancellationToken);
            total += deleted;
            progress.DeletedHits += deleted;
            await UpdateProgressAsync(progress, cancellationToken);
            if (deleted == 0)
            {
                afterCreatedAt = eventWindow.LastCreatedAt;
                afterId = eventWindow.LastId;
                continue;
            }

            if (deleted < progress.Policy.BatchSize)
            {
                afterCreatedAt = eventWindow.LastCreatedAt;
                afterId = eventWindow.LastId;
            }

            await DelayBetweenBatchesAsync(progress.Policy, cancellationToken);
        }
    }

    private async Task<long> DeleteEventsAsync(
        NpgsqlConnection connection,
        RetentionProgress progress,
        DateTimeOffset cutoff,
        RetentionWindow? window,
        CancellationToken cancellationToken)
    {
        if (window is null)
        {
            return 0;
        }

        long total = 0;
        while (true)
        {
            if (ShouldStopForMaxDuration(progress.StartedAt, progress.Policy, out _))
            {
                return total;
            }

            await using var command = connection.CreateCommand();
            command.CommandTimeout = progress.Policy.CommandTimeoutSeconds;
            command.CommandText = """
                WITH target AS (
                    SELECT id
                    FROM retrieval_events
                    WHERE created_at < @cutoff
                      AND created_at >= @window_start
                      AND created_at < @window_end
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
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("window_start", window.Start));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("window_end", window.End));
            command.Parameters.Add(new NpgsqlParameter<int>("batch_size", progress.Policy.EventBatchSize));
            var deleted = (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
            total += deleted;
            progress.DeletedEvents += deleted;
            await UpdateProgressAsync(progress, cancellationToken);
            if (deleted == 0)
            {
                return total;
            }

            await DelayBetweenBatchesAsync(progress.Policy, cancellationToken);
        }
    }

    private static async Task<RetentionWindow?> ResolveRetentionWindowAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoff,
        RetrievalTelemetryRetentionPolicy policy,
        bool requiresHits,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = requiresHits
            ? """
                SELECT MIN(e.created_at)
                FROM retrieval_events e
                WHERE e.created_at < @cutoff
                  AND EXISTS (
                      SELECT 1
                      FROM retrieval_hits h
                      WHERE h.retrieval_event_id = e.id
                  );
                """
            : """
                SELECT MIN(e.created_at)
                FROM retrieval_events e
                WHERE e.created_at < @cutoff;
                """;
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        var start = value switch
        {
            DateTimeOffset dateTimeOffset => dateTimeOffset,
            DateTime dateTime => new DateTimeOffset(DateTime.SpecifyKind(dateTime, DateTimeKind.Utc)),
            _ => throw new InvalidOperationException($"Unexpected retention window timestamp type '{value.GetType()}'.")
        };
        start = new DateTimeOffset(start.UtcDateTime.Date, TimeSpan.Zero);
        var end = start.AddDays(policy.TimeWindowDays);
        if (end > cutoff)
        {
            end = cutoff;
        }

        return new RetentionWindow(start, end);
    }

    private async Task<EventWindow> ReadEventWindowAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoff,
        RetentionWindow window,
        DateTimeOffset? afterCreatedAt,
        Guid? afterId,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = afterCreatedAt.HasValue && afterId.HasValue
            ? """
                SELECT id, created_at
                FROM retrieval_events
                WHERE created_at < @cutoff
                  AND created_at >= @window_start
                  AND created_at < @window_end
                  AND (
                      created_at > @after_created_at
                      OR (created_at = @after_created_at AND id > @after_id)
                  )
                ORDER BY created_at ASC, id ASC
                LIMIT @event_batch_size;
                """
            : """
                SELECT id, created_at
                FROM retrieval_events
                WHERE created_at < @cutoff
                  AND created_at >= @window_start
                  AND created_at < @window_end
                ORDER BY created_at ASC, id ASC
                LIMIT @event_batch_size;
                """;
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("window_start", window.Start));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("window_end", window.End));
        command.Parameters.Add(new NpgsqlParameter<int>("event_batch_size", policy.EventBatchSize));
        if (afterCreatedAt.HasValue && afterId.HasValue)
        {
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("after_created_at", afterCreatedAt.Value));
            command.Parameters.Add(new NpgsqlParameter<Guid>("after_id", afterId.Value));
        }

        var eventIds = new List<Guid>(policy.EventBatchSize);
        DateTimeOffset? lastCreatedAt = null;
        Guid? lastId = null;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            lastId = reader.GetGuid(0);
            lastCreatedAt = reader.GetFieldValue<DateTimeOffset>(1);
            eventIds.Add(lastId.Value);
        }

        return new EventWindow(eventIds, lastCreatedAt, lastId);
    }

    private static async Task<long> DeleteHitBatchAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Guid> eventIds,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH target AS (
                SELECT h.id
                FROM retrieval_hits h
                WHERE h.retrieval_event_id = ANY(@event_ids)
                ORDER BY h.retrieval_event_id ASC, h.rank ASC, h.id ASC
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
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("event_ids", eventIds.ToArray()));
        command.Parameters.Add(new NpgsqlParameter<int>("batch_size", policy.BatchSize));
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task VacuumAnalyzeAsync(NpgsqlConnection connection, RetrievalTelemetryRetentionPolicy policy, CancellationToken cancellationToken)
    {
        foreach (var table in new[] { "retrieval_hits", "retrieval_events" })
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = policy.CommandTimeoutSeconds;
            command.CommandText = $"VACUUM (ANALYZE) {table};";
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async Task VacuumFullAnalyzeAsync(NpgsqlConnection connection, string table, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = VacuumFullCommandTimeoutSeconds;
        command.CommandText = $"VACUUM FULL ANALYZE {table};";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, long>> ReadTableSizesAsync(
        NpgsqlConnection connection,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
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

    private async Task UpdateProgressAsync(RetentionProgress progress, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var resultJson = BuildResultJson(
            progress.StartedAt,
            now,
            progress.DeletedHits,
            progress.DeletedEvents,
            progress.SizeBefore,
            null,
            progress.Policy,
            progress.CurrentHitsWindow,
            progress.CurrentEventsWindow,
            progress.ProcessedHitsWindows,
            progress.ProcessedEventsWindows);
        await UpdateRunAsync(progress.RunId, MaintenanceRunStatus.Running, null, resultJson, string.Empty, cancellationToken);
    }

    private async Task UpdateRunAsync(Guid runId, MaintenanceRunStatus status, DateTimeOffset? completedAt, string resultJson, string error, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.MaintenanceRuns.FirstAsync(x => x.Id == runId, cancellationToken);
        run.Status = status;
        run.CompletedAt = completedAt;
        run.ResultJson = resultJson;
        run.Error = error;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private bool ShouldStopForMaxDuration(DateTimeOffset startedAt, RetrievalTelemetryRetentionPolicy policy, out string? stoppedReason)
    {
        if (timeProvider.GetUtcNow() - startedAt >= policy.MaxDuration)
        {
            stoppedReason = "maxDuration";
            return true;
        }

        stoppedReason = null;
        return false;
    }

    private static string BuildResultJson(
        DateTimeOffset startedAt,
        DateTimeOffset observedAt,
        long deletedHits,
        long deletedEvents,
        IReadOnlyDictionary<string, long>? sizeBefore,
        IReadOnlyDictionary<string, long>? sizeAfter,
        RetrievalTelemetryRetentionPolicy policy,
        RetentionWindow? hitsWindow = null,
        RetentionWindow? eventsWindow = null,
        int processedHitsWindows = 0,
        int processedEventsWindows = 0,
        bool skipped = false,
        bool vacuumAnalyzeCompleted = false,
        string? vacuumAnalyzeError = null,
        bool vacuumFullCompleted = false,
        string? vacuumFullError = null,
        bool completed = false,
        string? stoppedReason = null,
        string? error = null)
        => JsonSerializer.Serialize(new
        {
            deletedHits,
            deletedEvents,
            startedAtUtc = startedAt,
            observedAtUtc = observedAt,
            completedAtUtc = completed ? observedAt : (DateTimeOffset?)null,
            durationMs = (observedAt - startedAt).TotalMilliseconds,
            tableSizeBeforeBytes = sizeBefore,
            tableSizeAfterBytes = sizeAfter,
            timeWindowDays = policy.TimeWindowDays,
            hitsWindowStartUtc = hitsWindow?.Start,
            hitsWindowEndUtc = hitsWindow?.End,
            eventsWindowStartUtc = eventsWindow?.Start,
            eventsWindowEndUtc = eventsWindow?.End,
            processedHitsWindows,
            processedEventsWindows,
            skipped,
            stoppedReason,
            vacuumAnalyzeRequested = policy.RunVacuumAnalyzeAfterRetention || policy.RunVacuumFullAutomatically,
            vacuumAnalyzeCompleted,
            vacuumAnalyzeError,
            vacuumFullRequested = policy.RunVacuumFullAutomatically,
            vacuumFullCompleted,
            vacuumFullError,
            error
        }, SerializerOptions);

    private static Task DelayBetweenBatchesAsync(RetrievalTelemetryRetentionPolicy policy, CancellationToken cancellationToken)
        => policy.DelayBetweenBatchesMs <= 0
            ? Task.CompletedTask
            : Task.Delay(TimeSpan.FromMilliseconds(policy.DelayBetweenBatchesMs), cancellationToken);

    private static string NormalizeTriggeredBy(string? requested, string fallback)
        => requested?.Trim() is { Length: > 0 } value
            ? value
            : string.IsNullOrWhiteSpace(fallback)
                ? "system"
                : fallback.Trim();

    private sealed record RetentionProgress(
        Guid RunId,
        DateTimeOffset StartedAt,
        RetrievalTelemetryRetentionPolicy Policy,
        IReadOnlyDictionary<string, long> SizeBefore)
    {
        public long DeletedHits { get; set; }
        public long DeletedEvents { get; set; }
        public int ProcessedHitsWindows { get; set; }
        public int ProcessedEventsWindows { get; set; }
        public RetentionWindow? CurrentHitsWindow { get; set; }
        public RetentionWindow? CurrentEventsWindow { get; set; }
    }

    private sealed record RetentionWindow(DateTimeOffset Start, DateTimeOffset End);

    private sealed record EventWindow(
        IReadOnlyList<Guid> EventIds,
        DateTimeOffset? LastCreatedAt,
        Guid? LastId);
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

public sealed class DomainOwnerRepairService(
    NpgsqlDataSource dataSource,
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    ICacheVersionStore cacheStore,
    TimeProvider timeProvider,
    ILogger<DomainOwnerRepairService> logger) : IDomainOwnerRepairService
{
    private static readonly Guid DefaultAdminTenantId = Guid.Parse("72000000-0000-0000-0000-000000000001");
    private static readonly Guid DefaultAdminUserId = Guid.Parse("73000000-0000-0000-0000-000000000001");
    private static readonly Guid DashboardServiceUserId = Guid.Parse("209b1f29-a13c-494d-abec-723609e45a64");
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] SmallDomainOwnerTables =
    [
        "memory_items",
        "memory_jobs",
        "conversation_sessions",
        "conversation_checkpoints",
        "conversation_insights"
    ];
    private static readonly string[] AllDomainOwnerTables = [.. SmallDomainOwnerTables, "retrieval_events"];

    public async Task<DomainOwnerRepairResult> RunAsync(
        DomainOwnerRepairRequest request,
        string fallbackTriggeredBy,
        CancellationToken cancellationToken)
    {
        var adminTenantId = request.AdminTenantId ?? DefaultAdminTenantId;
        var adminUserId = request.AdminUserId ?? DefaultAdminUserId;
        var retrievalEventBatchSize = Math.Clamp(request.RetrievalEventBatchSize ?? 10_000, 1, 100_000);
        var maxRetrievalEventBatches = request.MaxRetrievalEventBatches is > 0
            ? Math.Clamp(request.MaxRetrievalEventBatches.Value, 1, 10_000)
            : (int?)null;
        var commandTimeoutSeconds = Math.Clamp(request.CommandTimeoutSeconds ?? 300, 1, 3600);
        var tables = ResolveTables(request);

        await ValidateAdminOwnerAsync(adminTenantId, adminUserId, cancellationToken);

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var distributionBefore = await ReadDistributionAsync(connection, tables, commandTimeoutSeconds, cancellationToken);
        var conflicts = tables.Contains("memory_items", StringComparer.Ordinal)
            ? await ReadMemoryConflictsAsync(connection, adminTenantId, adminUserId, commandTimeoutSeconds, cancellationToken)
            : [];
        var affectedProjectIds = await ReadAffectedProjectIdsAsync(connection, tables, adminTenantId, adminUserId, commandTimeoutSeconds, cancellationToken);

        if (!request.Apply || conflicts.Count > 0)
        {
            var previewJson = BuildResultJson(
                request.Apply,
                applied: false,
                adminTenantId,
                adminUserId,
                distributionBefore,
                [],
                conflicts,
                [],
                affectedProjectIds);
            return new DomainOwnerRepairResult(
                null,
                false,
                adminTenantId,
                adminUserId,
                distributionBefore,
                [],
                conflicts,
                [],
                affectedProjectIds,
                previewJson);
        }

        var startedAt = timeProvider.GetUtcNow();
        var run = new MaintenanceRun
        {
            MaintenanceType = MaintenanceRunType.DomainOwnerRepair,
            Status = MaintenanceRunStatus.Running,
            StartedAt = startedAt,
            TriggeredBy = NormalizeTriggeredBy(request.TriggeredBy, fallbackTriggeredBy),
            PolicyJson = JsonSerializer.Serialize(new
            {
                adminTenantId,
                adminUserId,
                preservedOwnerUserIds = new[] { DashboardServiceUserId },
                tables,
                retrievalEventBatchSize,
                maxRetrievalEventBatches,
                commandTimeoutSeconds
            }, SerializerOptions)
        };

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            dbContext.MaintenanceRuns.Add(run);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        try
        {
            var tableResults = new List<DomainOwnerRepairTableResult>();
            foreach (var table in tables.Where(table => !string.Equals(table, "retrieval_events", StringComparison.Ordinal)))
            {
                var updatedRows = await UpdateOwnerTableAsync(connection, table, adminTenantId, adminUserId, commandTimeoutSeconds, cancellationToken);
                tableResults.Add(new DomainOwnerRepairTableResult(table, updatedRows));
            }

            if (tables.Contains("retrieval_events", StringComparer.Ordinal))
            {
                var retrievalEventsUpdated = await UpdateRetrievalEventsAsync(
                    connection,
                    adminTenantId,
                    adminUserId,
                    retrievalEventBatchSize,
                    maxRetrievalEventBatches,
                    commandTimeoutSeconds,
                    cancellationToken);
                tableResults.Add(new DomainOwnerRepairTableResult("retrieval_events", retrievalEventsUpdated));
            }

            await BumpCacheVersionsAsync(adminTenantId, adminUserId, affectedProjectIds, cancellationToken);

            var distributionAfter = await ReadDistributionAsync(connection, tables, commandTimeoutSeconds, cancellationToken);
            var completedAt = timeProvider.GetUtcNow();
            var resultJson = BuildResultJson(
                requestedApply: true,
                applied: true,
                adminTenantId,
                adminUserId,
                distributionBefore,
                distributionAfter,
                conflicts,
                tableResults,
                affectedProjectIds,
                startedAt,
                completedAt);

            await CompleteRunAsync(run.Id, MaintenanceRunStatus.Completed, completedAt, resultJson, string.Empty, cancellationToken);
            return new DomainOwnerRepairResult(
                run.Id,
                true,
                adminTenantId,
                adminUserId,
                distributionBefore,
                distributionAfter,
                conflicts,
                tableResults,
                affectedProjectIds,
                resultJson);
        }
        catch (Exception ex)
        {
            var completedAt = timeProvider.GetUtcNow();
            logger.LogError(ex, "Domain owner repair run {MaintenanceRunId} failed.", run.Id);
            await CompleteRunAsync(run.Id, MaintenanceRunStatus.Failed, completedAt, "{}", ex.Message, cancellationToken);
            throw;
        }
    }

    private async Task ValidateAdminOwnerAsync(Guid adminTenantId, Guid adminUserId, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var exists = await dbContext.TenantUsers
            .AnyAsync(
                x => x.Id == adminUserId &&
                     x.TenantId == adminTenantId &&
                     x.Status == TenantUserStatus.Active &&
                     x.Role == TenantUserRole.Owner,
                cancellationToken);

        if (!exists)
        {
            throw new InvalidOperationException($"Admin owner {adminTenantId}/{adminUserId} was not found or is not active owner.");
        }
    }

    private static IReadOnlyList<string> ResolveTables(DomainOwnerRepairRequest request)
    {
        var tables = new List<string>();
        if (request.IncludeSmallTables)
        {
            tables.AddRange(SmallDomainOwnerTables);
        }

        if (request.IncludeRetrievalEvents)
        {
            tables.Add("retrieval_events");
        }

        if (tables.Count == 0)
        {
            throw new InvalidOperationException("Domain owner repair requires at least one selected table group.");
        }

        return tables;
    }

    private static async Task<IReadOnlyList<DomainOwnerDistributionResult>> ReadDistributionAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> tables,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var results = new List<DomainOwnerDistributionResult>();
        foreach (var table in tables.Where(table => !string.Equals(table, "retrieval_events", StringComparison.Ordinal)))
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = $"""
                SELECT tenant_id, owner_user_id, COUNT(*)::bigint
                FROM {table}
                GROUP BY tenant_id, owner_user_id
                ORDER BY tenant_id NULLS FIRST, owner_user_id NULLS FIRST;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new DomainOwnerDistributionResult(
                    table,
                    reader.IsDBNull(0) ? null : reader.GetGuid(0),
                    reader.IsDBNull(1) ? null : reader.GetGuid(1),
                    reader.GetInt64(2)));
            }
        }

        return results;
    }

    private static async Task<IReadOnlyList<DomainOwnerConflictResult>> ReadMemoryConflictsAsync(
        NpgsqlConnection connection,
        Guid adminTenantId,
        Guid adminUserId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = """
            SELECT project_id,
                   external_key,
                   COUNT(*)::bigint,
                   array_agg(id::text ORDER BY updated_at DESC, id) AS memory_ids
            FROM memory_items
            GROUP BY project_id, external_key
            HAVING COUNT(*) > 1
               AND bool_or(
                    owner_user_id IS DISTINCT FROM @dashboard_service_user_id AND
                    (tenant_id IS DISTINCT FROM @admin_tenant_id OR owner_user_id IS DISTINCT FROM @admin_user_id))
            ORDER BY project_id, external_key
            LIMIT 500;
            """;
        command.Parameters.AddWithValue("admin_tenant_id", adminTenantId);
        command.Parameters.AddWithValue("admin_user_id", adminUserId);
        command.Parameters.AddWithValue("dashboard_service_user_id", DashboardServiceUserId);

        var results = new List<DomainOwnerConflictResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var ids = reader.GetFieldValue<string[]>(3).Select(Guid.Parse).ToArray();
            results.Add(new DomainOwnerConflictResult(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetInt64(2),
                ids));
        }

        return results;
    }

    private static async Task<IReadOnlyList<string>> ReadAffectedProjectIdsAsync(
        NpgsqlConnection connection,
        IReadOnlyList<string> tables,
        Guid adminTenantId,
        Guid adminUserId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var projectTables = tables.Where(table => !string.Equals(table, "retrieval_events", StringComparison.Ordinal)).ToArray();
        if (projectTables.Length == 0)
        {
            return [];
        }

        var selectors = string.Join(
            $"{Environment.NewLine}UNION{Environment.NewLine}",
            projectTables.Select(table =>
                $"SELECT project_id FROM {table} WHERE owner_user_id IS DISTINCT FROM @dashboard_service_user_id AND (tenant_id IS DISTINCT FROM @admin_tenant_id OR owner_user_id IS DISTINCT FROM @admin_user_id)"));
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = $"""
            SELECT DISTINCT project_id
            FROM (
                {selectors}
            ) affected
            WHERE project_id IS NOT NULL AND btrim(project_id) <> ''
            ORDER BY project_id;
            """;
        command.Parameters.AddWithValue("admin_tenant_id", adminTenantId);
        command.Parameters.AddWithValue("admin_user_id", adminUserId);
        command.Parameters.AddWithValue("dashboard_service_user_id", DashboardServiceUserId);

        var projects = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            projects.Add(reader.GetString(0));
        }

        return projects;
    }

    private static async Task<long> UpdateOwnerTableAsync(
        NpgsqlConnection connection,
        string table,
        Guid adminTenantId,
        Guid adminUserId,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = commandTimeoutSeconds;
        command.CommandText = $"""
            UPDATE {table}
            SET tenant_id = @admin_tenant_id,
                owner_user_id = @admin_user_id
            WHERE owner_user_id IS DISTINCT FROM @dashboard_service_user_id
              AND (tenant_id IS DISTINCT FROM @admin_tenant_id
                   OR owner_user_id IS DISTINCT FROM @admin_user_id);
            """;
        command.Parameters.AddWithValue("admin_tenant_id", adminTenantId);
        command.Parameters.AddWithValue("admin_user_id", adminUserId);
        command.Parameters.AddWithValue("dashboard_service_user_id", DashboardServiceUserId);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> UpdateRetrievalEventsAsync(
        NpgsqlConnection connection,
        Guid adminTenantId,
        Guid adminUserId,
        int batchSize,
        int? maxBatches,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken)
    {
        var total = 0L;
        var batches = 0;
        while (true)
        {
            if (maxBatches.HasValue && batches >= maxBatches.Value)
            {
                return total;
            }

            await using var command = connection.CreateCommand();
            command.CommandTimeout = commandTimeoutSeconds;
            command.CommandText = """
                WITH batch AS (
                    SELECT id
                    FROM retrieval_events
                    WHERE owner_user_id IS DISTINCT FROM @dashboard_service_user_id
                      AND (tenant_id IS DISTINCT FROM @admin_tenant_id
                           OR owner_user_id IS DISTINCT FROM @admin_user_id)
                    LIMIT @batch_size
                )
                UPDATE retrieval_events target
                SET tenant_id = @admin_tenant_id,
                    owner_user_id = @admin_user_id
                FROM batch
                WHERE target.id = batch.id;
                """;
            command.Parameters.AddWithValue("admin_tenant_id", adminTenantId);
            command.Parameters.AddWithValue("admin_user_id", adminUserId);
            command.Parameters.AddWithValue("dashboard_service_user_id", DashboardServiceUserId);
            command.Parameters.AddWithValue("batch_size", batchSize);

            var updated = await command.ExecuteNonQueryAsync(cancellationToken);
            if (updated <= 0)
            {
                return total;
            }

            total += updated;
            batches++;
        }
    }

    private async Task BumpCacheVersionsAsync(
        Guid adminTenantId,
        Guid adminUserId,
        IReadOnlyList<string> affectedProjectIds,
        CancellationToken cancellationToken)
    {
        await cacheStore.IncrementAsync(cancellationToken);
        await cacheStore.IncrementUserAsync(
            new ContextHubRequestActor(adminTenantId, adminUserId, "admin", TenantUserRole.Owner, [], [], true),
            cancellationToken);
        foreach (var projectId in affectedProjectIds)
        {
            await cacheStore.IncrementProjectAsync(projectId, cancellationToken);
        }
    }

    private static string BuildResultJson(
        bool requestedApply,
        bool applied,
        Guid adminTenantId,
        Guid adminUserId,
        IReadOnlyList<DomainOwnerDistributionResult> distributionBefore,
        IReadOnlyList<DomainOwnerDistributionResult> distributionAfter,
        IReadOnlyList<DomainOwnerConflictResult> conflicts,
        IReadOnlyList<DomainOwnerRepairTableResult> tableResults,
        IReadOnlyList<string> affectedProjectIds,
        DateTimeOffset? startedAtUtc = null,
        DateTimeOffset? completedAtUtc = null)
        => JsonSerializer.Serialize(new
        {
            requestedApply,
            applied,
            adminTenantId,
            adminUserId,
            distributionBefore,
            distributionAfter,
            conflicts,
            tableResults,
            affectedProjectIds,
            startedAtUtc,
            completedAtUtc,
            durationMs = startedAtUtc.HasValue && completedAtUtc.HasValue
                ? (double?)(completedAtUtc.Value - startedAtUtc.Value).TotalMilliseconds
                : null,
            conflictGuardBlocked = requestedApply && conflicts.Count > 0
        }, SerializerOptions);

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

    private static string NormalizeTriggeredBy(string? requested, string fallback)
        => requested?.Trim() is { Length: > 0 } value
            ? value
            : string.IsNullOrWhiteSpace(fallback)
                ? "system"
                : fallback.Trim();
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

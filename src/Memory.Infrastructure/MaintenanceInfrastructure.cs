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
    TimeProvider timeProvider) : IMaintenanceModeStore, IMaintenanceCoordinator
{
    private const string StateKey = "context-hub:maintenance:state";
    private const string LeaseIndexKey = "context-hub:maintenance:leases";
    private const string LeaseKeyPrefix = "context-hub:maintenance:lease:";
    private const int DefaultDurationMinutes = 90;
    private const int DefaultMaxDrainWaitMinutes = 15;
    private const int DefaultLeaseTtlSeconds = 300;
    private const int CompletedStateTtlSeconds = 3600;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private readonly IDatabase _database = redis.GetDatabase();

    public async Task<MaintenanceModeStateResult> GetAsync(CancellationToken cancellationToken)
        => ToModeState(await GetStatusAsync(cancellationToken));

    public async Task<MaintenanceModeStateResult> EnableAsync(MaintenanceModeRequest request, string triggeredBy, CancellationToken cancellationToken)
    {
        var current = await GetStatusAsync(cancellationToken);
        if (current.Phase is MaintenancePhase.Scheduled or MaintenancePhase.Draining or MaintenancePhase.Running)
        {
            return ToModeState(current);
        }

        var now = timeProvider.GetUtcNow();
        var estimatedEndsAt = request.EstimatedEndsAtUtc
            ?? now.AddMinutes(Math.Clamp(request.EstimatedDurationMinutes ?? DefaultDurationMinutes, 1, 24 * 60));
        var normalizedTriggeredBy = NormalizeTriggeredBy(request.TriggeredBy, triggeredBy);
        var reason = NormalizeOptional(request.Reason, "Maintenance");
        var message = NormalizeOptional(request.Message, "ContextHub is temporarily unavailable due to maintenance.");
        var run = new MaintenanceRun
        {
            MaintenanceType = MaintenanceRunType.MaintenanceMode,
            Status = MaintenanceRunStatus.Running,
            StartedAt = now,
            TriggeredBy = normalizedTriggeredBy,
            PolicyJson = JsonSerializer.Serialize(new
            {
                reason,
                message,
                scheduledStartAtUtc = now,
                estimatedEndsAtUtc = estimatedEndsAt,
                maxDrainWaitMinutes = DefaultMaxDrainWaitMinutes
            }, SerializerOptions)
        };

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            dbContext.MaintenanceRuns.Add(run);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var status = new MaintenanceStatusResult(
            MaintenancePhase.Running,
            true,
            reason,
            message,
            now,
            now,
            estimatedEndsAt,
            run.Id,
            normalizedTriggeredBy,
            DefaultMaxDrainWaitMinutes,
            0,
            []);
        await SetStateAsync(status);
        return ToModeState(status);
    }

    public async Task<MaintenanceModeStateResult> DisableAsync(string triggeredBy, CancellationToken cancellationToken)
        => ToModeState(await CompleteAsync(null, triggeredBy, cancellationToken));

    public async Task<MaintenanceStatusResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stored = await ReadStoredStatusAsync(cancellationToken);
        var leases = await ReadActiveLeasesAsync(cancellationToken);
        return stored with
        {
            ActiveLeaseCount = leases.Count(x => x.BlocksMaintenance),
            ActiveLeases = leases
        };
    }

    public async Task<MaintenanceStatusResult> ScheduleAsync(MaintenanceWindowRequest request, string triggeredBy, CancellationToken cancellationToken)
    {
        var current = await GetStatusAsync(cancellationToken);
        if (current.Phase is MaintenancePhase.Scheduled or MaintenancePhase.Draining or MaintenancePhase.Running)
        {
            return current;
        }

        var now = timeProvider.GetUtcNow();
        var scheduledStart = request.ScheduledStartAtUtc ?? now;
        var estimatedEndsAt = request.EstimatedEndsAtUtc
            ?? scheduledStart.AddMinutes(Math.Clamp(request.EstimatedDurationMinutes ?? DefaultDurationMinutes, 1, 24 * 60));
        var maxDrainWait = Math.Clamp(request.MaxDrainWaitMinutes ?? DefaultMaxDrainWaitMinutes, 1, 24 * 60);
        var normalizedTriggeredBy = NormalizeTriggeredBy(request.TriggeredBy, triggeredBy);
        var reason = NormalizeOptional(request.Reason, "Maintenance");
        var message = NormalizeOptional(request.Message, "ContextHub maintenance is scheduled. New write operations may be paused during the drain window.");
        var run = new MaintenanceRun
        {
            MaintenanceType = MaintenanceRunType.MaintenanceMode,
            Status = MaintenanceRunStatus.Scheduled,
            StartedAt = now,
            TriggeredBy = normalizedTriggeredBy,
            PolicyJson = JsonSerializer.Serialize(new
            {
                reason,
                message,
                scheduledStartAtUtc = scheduledStart,
                estimatedEndsAtUtc = estimatedEndsAt,
                maxDrainWaitMinutes = maxDrainWait
            }, SerializerOptions)
        };

        await using (var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken))
        {
            dbContext.MaintenanceRuns.Add(run);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        var status = new MaintenanceStatusResult(
            MaintenancePhase.Scheduled,
            false,
            reason,
            message,
            scheduledStart,
            null,
            estimatedEndsAt,
            run.Id,
            normalizedTriggeredBy,
            maxDrainWait,
            0,
            []);
        await SetStateAsync(status);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<MaintenanceStatusResult> StartDrainAsync(Guid? runId, string triggeredBy, CancellationToken cancellationToken)
    {
        var current = await GetStatusAsync(cancellationToken);
        if (current.Phase == MaintenancePhase.Running)
        {
            return current;
        }

        if (current.Phase == MaintenancePhase.Inactive)
        {
            current = await ScheduleAsync(new MaintenanceWindowRequest(TriggeredBy: triggeredBy), triggeredBy, cancellationToken);
        }

        var now = timeProvider.GetUtcNow();
        var status = current with
        {
            Phase = MaintenancePhase.Draining,
            Active = false,
            StartedAtUtc = current.StartedAtUtc ?? now,
            Message = NormalizeOptional(current.Message, "ContextHub maintenance is draining active agent work. New write operations are paused.")
        };
        await UpdateRunStatusAsync(status.RunId, MaintenanceRunStatus.Draining, null, null, string.Empty, cancellationToken);
        await SetStateAsync(status);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<MaintenanceStatusResult> StartRunningAsync(Guid? runId, string triggeredBy, CancellationToken cancellationToken)
    {
        var current = await GetStatusAsync(cancellationToken);
        if (current.Phase == MaintenancePhase.Running)
        {
            return current;
        }

        if (current.Phase != MaintenancePhase.Draining)
        {
            current = await StartDrainAsync(runId, triggeredBy, cancellationToken);
        }

        var drainStartedAt = current.StartedAtUtc ?? timeProvider.GetUtcNow();
        var drainDeadline = drainStartedAt.AddMinutes(Math.Clamp(current.MaxDrainWaitMinutes, 1, 24 * 60));
        if (current.ActiveLeaseCount > 0 && timeProvider.GetUtcNow() < drainDeadline)
        {
            return current;
        }

        var now = timeProvider.GetUtcNow();
        var running = current with
        {
            Phase = MaintenancePhase.Running,
            Active = true,
            StartedAtUtc = now,
            Message = NormalizeOptional(current.Message, "ContextHub is temporarily unavailable due to maintenance.")
        };
        await UpdateRunStatusAsync(running.RunId, MaintenanceRunStatus.Running, null, null, string.Empty, cancellationToken);
        await SetStateAsync(running);
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<MaintenanceStatusResult> CompleteAsync(Guid? runId, string triggeredBy, CancellationToken cancellationToken)
    {
        var current = await GetStatusAsync(cancellationToken);
        if (current.Phase == MaintenancePhase.Inactive)
        {
            return current;
        }

        var now = timeProvider.GetUtcNow();
        var resultJson = JsonSerializer.Serialize(new
        {
            completedBy = NormalizeTriggeredBy(null, triggeredBy),
            activeFromUtc = current.StartedAtUtc,
            completedAtUtc = now
        }, SerializerOptions);
        await UpdateRunStatusAsync(current.RunId, MaintenanceRunStatus.Completed, now, resultJson, string.Empty, cancellationToken);
        var completed = current with { Phase = MaintenancePhase.Completed, Active = false };
        await SetStateAsync(completed, TimeSpan.FromSeconds(CompletedStateTtlSeconds));
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<MaintenanceStatusResult> CancelAsync(Guid? runId, string triggeredBy, CancellationToken cancellationToken)
    {
        var current = await GetStatusAsync(cancellationToken);
        if (current.Phase == MaintenancePhase.Inactive)
        {
            return current;
        }

        var now = timeProvider.GetUtcNow();
        var resultJson = JsonSerializer.Serialize(new
        {
            cancelledBy = NormalizeTriggeredBy(null, triggeredBy),
            cancelledAtUtc = now
        }, SerializerOptions);
        await UpdateRunStatusAsync(current.RunId, MaintenanceRunStatus.Cancelled, now, resultJson, string.Empty, cancellationToken);
        var cancelled = current with { Phase = MaintenancePhase.Cancelled, Active = false };
        await SetStateAsync(cancelled, TimeSpan.FromSeconds(CompletedStateTtlSeconds));
        return await GetStatusAsync(cancellationToken);
    }

    public async Task<MaintenanceLeaseHeartbeatResult> HeartbeatLeaseAsync(MaintenanceLeaseHeartbeatRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var status = await GetStatusAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        var leaseId = request.LeaseId ?? Guid.NewGuid();
        var ttl = TimeSpan.FromSeconds(Math.Clamp(request.TtlSeconds ?? DefaultLeaseTtlSeconds, 30, 30 * 60));
        var expiresAt = now.Add(ttl);
        var lease = new MaintenanceLeaseResult(
            leaseId,
            NormalizeOptional(request.AgentId, "agent"),
            ProjectContext.Normalize(request.ProjectId ?? ProjectContext.DefaultProjectId),
            NormalizeOptional(request.ConversationId, string.Empty),
            NormalizeOptional(request.TaskId, string.Empty),
            NormalizeOptional(request.ActivityKind, "context"),
            request.BlocksMaintenance && status.Phase is not (MaintenancePhase.Draining or MaintenancePhase.Running),
            now,
            expiresAt);

        await _database.StringSetAsync(BuildLeaseKey(leaseId), JsonSerializer.Serialize(lease, SerializerOptions), ttl);
        await _database.SortedSetAddAsync(LeaseIndexKey, leaseId.ToString("D"), expiresAt.ToUnixTimeMilliseconds());
        return new MaintenanceLeaseHeartbeatResult(lease, await GetStatusAsync(cancellationToken));
    }

    public async Task<MaintenanceStatusResult> CompleteLeaseAsync(MaintenanceLeaseCompleteRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _database.KeyDeleteAsync(BuildLeaseKey(request.LeaseId));
        await _database.SortedSetRemoveAsync(LeaseIndexKey, request.LeaseId.ToString("D"));
        return await GetStatusAsync(cancellationToken);
    }

    public async Task EnsureWriteAllowedAsync(string operation, CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        if (status.Phase is MaintenancePhase.Draining or MaintenancePhase.Running)
        {
            throw new MaintenanceUnavailableException(
                $"ContextHub is {status.Phase.ToString().ToLowerInvariant()}; write operation '{operation}' is paused.",
                status);
        }
    }

    public async Task<bool> CanStartBackgroundJobAsync(CancellationToken cancellationToken)
    {
        var status = await GetStatusAsync(cancellationToken);
        return status.Phase is not (MaintenancePhase.Draining or MaintenancePhase.Running);
    }

    private static MaintenanceModeStateResult Inactive { get; } = new(false, string.Empty, string.Empty, null, null, null, string.Empty);

    private static MaintenanceStatusResult InactiveStatus { get; } = new(
        MaintenancePhase.Inactive,
        false,
        string.Empty,
        string.Empty,
        null,
        null,
        null,
        null,
        string.Empty,
        DefaultMaxDrainWaitMinutes,
        0,
        []);

    private async Task<MaintenanceStatusResult> ReadStoredStatusAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = await _database.StringGetAsync(StateKey);
        if (payload.IsNullOrEmpty)
        {
            return InactiveStatus;
        }

        var text = payload.ToString();
        try
        {
            using var document = JsonDocument.Parse(text);
            if (document.RootElement.TryGetProperty("phase", out _))
            {
                return JsonSerializer.Deserialize<MaintenanceStatusResult>(text, SerializerOptions) ?? InactiveStatus;
            }
        }
        catch (JsonException)
        {
            return InactiveStatus;
        }

        var legacy = JsonSerializer.Deserialize<MaintenanceModeStateResult>(text, SerializerOptions);
        return legacy is null || !legacy.Active
            ? InactiveStatus
            : new MaintenanceStatusResult(
                MaintenancePhase.Running,
                true,
                legacy.Reason,
                legacy.Message,
                legacy.StartedAtUtc,
                legacy.StartedAtUtc,
                legacy.EstimatedEndsAtUtc,
                legacy.RunId,
                legacy.TriggeredBy,
                DefaultMaxDrainWaitMinutes,
                0,
                []);
    }

    private async Task SetStateAsync(MaintenanceStatusResult status, TimeSpan? ttl = null)
    {
        var stored = status with
        {
            ActiveLeaseCount = 0,
            ActiveLeases = []
        };
        await _database.StringSetAsync(StateKey, JsonSerializer.Serialize(stored, SerializerOptions), ttl);
    }

    private async Task<IReadOnlyList<MaintenanceLeaseResult>> ReadActiveLeasesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var now = timeProvider.GetUtcNow();
        await _database.SortedSetRemoveRangeByScoreAsync(LeaseIndexKey, double.NegativeInfinity, now.ToUnixTimeMilliseconds());
        var values = await _database.SortedSetRangeByRankAsync(LeaseIndexKey, 0, 499, Order.Ascending);
        if (values.Length == 0)
        {
            return [];
        }

        var leases = new List<MaintenanceLeaseResult>(values.Length);
        foreach (var value in values)
        {
            if (!Guid.TryParse(value.ToString(), out var leaseId))
            {
                continue;
            }

            var payload = await _database.StringGetAsync(BuildLeaseKey(leaseId));
            if (payload.IsNullOrEmpty)
            {
                await _database.SortedSetRemoveAsync(LeaseIndexKey, value);
                continue;
            }

            var lease = JsonSerializer.Deserialize<MaintenanceLeaseResult>(payload.ToString(), SerializerOptions);
            if (lease is null || lease.ExpiresAtUtc <= now)
            {
                await _database.KeyDeleteAsync(BuildLeaseKey(leaseId));
                await _database.SortedSetRemoveAsync(LeaseIndexKey, value);
                continue;
            }

            leases.Add(lease);
        }

        return leases
            .OrderBy(x => x.ExpiresAtUtc)
            .ThenBy(x => x.LeaseId)
            .ToArray();
    }

    private async Task UpdateRunStatusAsync(Guid? runId, MaintenanceRunStatus status, DateTimeOffset? completedAt, string? resultJson, string error, CancellationToken cancellationToken)
    {
        if (!runId.HasValue)
        {
            return;
        }

        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.MaintenanceRuns.FirstOrDefaultAsync(x => x.Id == runId.Value, cancellationToken);
        if (run is null)
        {
            return;
        }

        run.Status = status;
        run.CompletedAt = completedAt;
        if (resultJson is not null)
        {
            run.ResultJson = resultJson;
        }

        run.Error = error;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static MaintenanceModeStateResult ToModeState(MaintenanceStatusResult status)
        => status.Phase == MaintenancePhase.Running
            ? new MaintenanceModeStateResult(
                true,
                status.Reason,
                status.Message,
                status.StartedAtUtc,
                status.EstimatedEndsAtUtc,
                status.RunId,
                status.TriggeredBy)
            : Inactive;

    private static string BuildLeaseKey(Guid leaseId)
        => $"{LeaseKeyPrefix}{leaseId:D}";

    private static string NormalizeOptional(string? value, string fallback)
        => value?.Trim() is { Length: > 0 } normalized ? normalized : fallback;

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

public sealed class InProcessMaintenanceRunRecoveryHostedService(
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    TimeProvider timeProvider,
    ILogger<InProcessMaintenanceRunRecoveryHostedService> logger) : IHostedService
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var staleRuns = await dbContext.MaintenanceRuns
            .Where(x =>
                x.Status == MaintenanceRunStatus.Running &&
                x.TriggeredBy != "scheduled" &&
                (x.MaintenanceType == MaintenanceRunType.RetrievalTelemetryRetention ||
                 x.MaintenanceType == MaintenanceRunType.VacuumFullReclaim) &&
                x.StartedAt < now)
            .ToListAsync(cancellationToken);

        foreach (var run in staleRuns)
        {
            run.Status = MaintenanceRunStatus.Failed;
            run.CompletedAt = now;
            run.Error = "Maintenance run was interrupted by service restart.";
            run.ResultJson = JsonSerializer.Serialize(new
            {
                error = run.Error,
                interruptedByServiceRestart = true,
                startedAtUtc = run.StartedAt,
                completedAtUtc = now,
                durationMs = (now - run.StartedAt).TotalMilliseconds
            }, SerializerOptions);
        }

        if (staleRuns.Count > 0)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogWarning("Marked {Count} in-process maintenance runs as failed after service restart.", staleRuns.Count);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed record RetrievalTelemetryRetentionPolicy(
    int HitsRetentionDays,
    int EventsRetentionDays,
    int SummaryRetentionDays,
    int SecurityAuditRetentionDays,
    int RuntimeLogRetentionDays,
    int MaintenanceRunRetentionDays,
    int HitSummaryTopPerBucket,
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
            Math.Max(1, options.SummaryRetentionDays),
            Math.Max(1, options.SecurityAuditRetentionDays),
            Math.Max(1, options.RuntimeLogRetentionDays),
            Math.Max(1, options.MaintenanceRunRetentionDays),
            Math.Clamp(options.HitSummaryTopPerBucket, 1, 1_000),
            Math.Clamp(request.BatchSize ?? options.BatchSize, 1, 100_000),
            Math.Clamp(request.EventBatchSize ?? options.EventBatchSize, 1, 100_000),
            Math.Clamp(request.TimeWindowDays ?? options.TimeWindowDays, 1, 3),
            Math.Clamp(request.DelayBetweenBatchesMs ?? options.DelayBetweenBatchesMs, 0, 60_000),
            Math.Clamp(request.CommandTimeoutSeconds ?? options.CommandTimeoutSeconds, 1, 3600),
            TimeSpan.FromMinutes(Math.Clamp(request.MaxDurationMinutes ?? options.MaxDurationMinutes, 1, 30)),
            request.RunVacuumAnalyzeAfterRetention ?? options.RunVacuumAnalyzeAfterRetention,
            request.RunVacuumFullAutomatically ?? options.RunVacuumFullAutomatically);
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
    private static readonly string[] RetentionVacuumTables =
    [
        "retrieval_hits",
        "retrieval_events",
        "retrieval_telemetry_daily_summaries",
        "retrieval_telemetry_daily_hit_summaries",
        "security_audit_events",
        "runtime_log_entries",
        "maintenance_runs"
    ];
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
                summaryRetentionDays = policy.SummaryRetentionDays,
                securityAuditRetentionDays = policy.SecurityAuditRetentionDays,
                runtimeLogRetentionDays = policy.RuntimeLogRetentionDays,
                maintenanceRunRetentionDays = policy.MaintenanceRunRetentionDays,
                hitSummaryTopPerBucket = policy.HitSummaryTopPerBucket,
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

            await UpsertDailySummariesAsync(connection, progress, cancellationToken);

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

            if (!ShouldStopForMaxDuration(startedAt, policy, out _))
            {
                await DeleteOtherRetentionTablesAsync(connection, progress, run.Id, cancellationToken);
                await DeleteExpiredSummariesAsync(connection, progress, cancellationToken);
            }

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
                    stoppedReason: stoppedReason,
                    upsertedEventSummaryRows: progress.UpsertedEventSummaryRows,
                    upsertedHitSummaryRows: progress.UpsertedHitSummaryRows,
                    processedSummaryDays: progress.ProcessedSummaryDays,
                    deletedEventSummaryRows: progress.DeletedEventSummaryRows,
                    deletedHitSummaryRows: progress.DeletedHitSummaryRows,
                    deletedSecurityAuditEvents: progress.DeletedSecurityAuditEvents,
                    deletedRuntimeLogEntries: progress.DeletedRuntimeLogEntries,
                    deletedMaintenanceRuns: progress.DeletedMaintenanceRuns);
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
                completed: true,
                upsertedEventSummaryRows: progress.UpsertedEventSummaryRows,
                upsertedHitSummaryRows: progress.UpsertedHitSummaryRows,
                processedSummaryDays: progress.ProcessedSummaryDays,
                deletedEventSummaryRows: progress.DeletedEventSummaryRows,
                deletedHitSummaryRows: progress.DeletedHitSummaryRows,
                deletedSecurityAuditEvents: progress.DeletedSecurityAuditEvents,
                deletedRuntimeLogEntries: progress.DeletedRuntimeLogEntries,
                deletedMaintenanceRuns: progress.DeletedMaintenanceRuns);
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
                error: ex.Message,
                upsertedEventSummaryRows: progress?.UpsertedEventSummaryRows ?? 0,
                upsertedHitSummaryRows: progress?.UpsertedHitSummaryRows ?? 0,
                processedSummaryDays: progress?.ProcessedSummaryDays ?? 0,
                deletedEventSummaryRows: progress?.DeletedEventSummaryRows ?? 0,
                deletedHitSummaryRows: progress?.DeletedHitSummaryRows ?? 0,
                deletedSecurityAuditEvents: progress?.DeletedSecurityAuditEvents ?? 0,
                deletedRuntimeLogEntries: progress?.DeletedRuntimeLogEntries ?? 0,
                deletedMaintenanceRuns: progress?.DeletedMaintenanceRuns ?? 0);
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

    private async Task UpsertDailySummariesAsync(
        NpgsqlConnection connection,
        RetentionProgress progress,
        CancellationToken cancellationToken)
    {
        var summaryCutoffDate = DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime.Date);
        var earliestBackfillDate = summaryCutoffDate.AddDays(-progress.Policy.EventsRetentionDays);
        var currentDate = await ResolveNextUnsummarizedDateAsync(
            connection,
            earliestBackfillDate,
            summaryCutoffDate,
            progress.Policy,
            cancellationToken);

        while (currentDate.HasValue && currentDate.Value < summaryCutoffDate)
        {
            if (ShouldStopForMaxDuration(progress.StartedAt, progress.Policy, out _))
            {
                return;
            }

            var day = currentDate.Value;
            progress.UpsertedEventSummaryRows += await UpsertDailyEventSummaryAsync(connection, day, progress.Policy, cancellationToken);
            progress.UpsertedHitSummaryRows += await UpsertDailyHitSummaryAsync(connection, day, progress.Policy, cancellationToken);
            progress.ProcessedSummaryDays++;
            await UpdateProgressAsync(progress, cancellationToken);
            currentDate = await ResolveNextUnsummarizedDateAsync(
                connection,
                day.AddDays(1),
                summaryCutoffDate,
                progress.Policy,
                cancellationToken);
        }
    }

    private static async Task<DateOnly?> ResolveNextUnsummarizedDateAsync(
        NpgsqlConnection connection,
        DateOnly fromDate,
        DateOnly summaryCutoffDate,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH candidate_days AS (
                SELECT DISTINCT (created_at AT TIME ZONE 'UTC')::date AS summary_date
                FROM retrieval_events
                WHERE created_at >= @from_date
                  AND created_at < @summary_cutoff
            )
            SELECT candidate_days.summary_date
            FROM candidate_days
            WHERE NOT EXISTS (
                SELECT 1
                FROM retrieval_telemetry_daily_summaries existing
                WHERE existing.summary_date = candidate_days.summary_date
            )
            ORDER BY candidate_days.summary_date ASC
            LIMIT 1;
            """;
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "from_date",
            new DateTimeOffset(fromDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>(
            "summary_cutoff",
            new DateTimeOffset(summaryCutoffDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero)));

        var value = await command.ExecuteScalarAsync(cancellationToken);
        if (value is null || value is DBNull)
        {
            return null;
        }

        return value switch
        {
            DateOnly dateOnly => dateOnly,
            DateTime dateTime => DateOnly.FromDateTime(dateTime),
            _ => throw new InvalidOperationException($"Unexpected summary date type '{value.GetType()}'.")
        };
    }

    private static async Task<long> UpsertDailyEventSummaryAsync(
        NpgsqlConnection connection,
        DateOnly summaryDate,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            INSERT INTO retrieval_telemetry_daily_summaries (
                summary_date,
                tenant_id,
                owner_user_id,
                project_id,
                channel,
                entry_point,
                purpose,
                query_mode,
                request_count,
                success_count,
                error_count,
                zero_result_count,
                cache_hit_count,
                result_count_sum,
                duration_ms_sum,
                duration_ms_max,
                duration_ms_p95,
                first_seen_at,
                last_seen_at,
                updated_at)
            SELECT
                @summary_date,
                COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid),
                COALESCE(owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid),
                COALESCE(project_id, ''),
                COALESCE(channel, ''),
                COALESCE(entry_point, ''),
                COALESCE(purpose, ''),
                COALESCE(query_mode, ''),
                COUNT(*)::bigint,
                COUNT(*) FILTER (WHERE success)::bigint,
                COUNT(*) FILTER (WHERE NOT success)::bigint,
                COUNT(*) FILTER (WHERE result_count = 0)::bigint,
                COUNT(*) FILTER (WHERE cache_hit)::bigint,
                COALESCE(SUM(result_count), 0)::bigint,
                COALESCE(SUM(duration_ms), 0)::double precision,
                COALESCE(MAX(duration_ms), 0)::double precision,
                COALESCE(percentile_cont(0.95) WITHIN GROUP (ORDER BY duration_ms), 0)::double precision,
                MIN(created_at),
                MAX(created_at),
                NOW()
            FROM retrieval_events
            WHERE created_at >= @day_start
              AND created_at < @day_end
            GROUP BY tenant_id, owner_user_id, project_id, channel, entry_point, purpose, query_mode
            ON CONFLICT (summary_date, tenant_id, owner_user_id, project_id, channel, entry_point, purpose, query_mode)
            DO UPDATE SET
                request_count = EXCLUDED.request_count,
                success_count = EXCLUDED.success_count,
                error_count = EXCLUDED.error_count,
                zero_result_count = EXCLUDED.zero_result_count,
                cache_hit_count = EXCLUDED.cache_hit_count,
                result_count_sum = EXCLUDED.result_count_sum,
                duration_ms_sum = EXCLUDED.duration_ms_sum,
                duration_ms_max = EXCLUDED.duration_ms_max,
                duration_ms_p95 = EXCLUDED.duration_ms_p95,
                first_seen_at = EXCLUDED.first_seen_at,
                last_seen_at = EXCLUDED.last_seen_at,
                updated_at = EXCLUDED.updated_at;
            """;
        AddSummaryDayParameters(command, summaryDate);
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> UpsertDailyHitSummaryAsync(
        NpgsqlConnection connection,
        DateOnly summaryDate,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await DeleteSummaryRowsForDayAsync(
            connection,
            "retrieval_telemetry_daily_hit_summaries",
            summaryDate,
            policy,
            cancellationToken);

        long total = 0;
        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;
        while (true)
        {
            var eventWindow = await ReadSummaryEventWindowAsync(
                connection,
                summaryDate,
                afterCreatedAt,
                afterId,
                policy,
                cancellationToken);
            if (eventWindow.EventIds.Count == 0)
            {
                break;
            }

            total += await UpsertDailyHitSummaryBatchAsync(connection, summaryDate, eventWindow.EventIds, policy, cancellationToken);
            afterCreatedAt = eventWindow.LastCreatedAt;
            afterId = eventWindow.LastId;
        }

        await PruneDailyHitSummaryAsync(connection, summaryDate, policy, cancellationToken);
        return total;
    }

    private static async Task<EventWindow> ReadSummaryEventWindowAsync(
        NpgsqlConnection connection,
        DateOnly summaryDate,
        DateTimeOffset? afterCreatedAt,
        Guid? afterId,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        var dayStart = new DateTimeOffset(summaryDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = afterCreatedAt.HasValue && afterId.HasValue
            ? """
                SELECT id, created_at
                FROM retrieval_events
                WHERE created_at >= @day_start
                  AND created_at < @day_end
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
                WHERE created_at >= @day_start
                  AND created_at < @day_end
                ORDER BY created_at ASC, id ASC
                LIMIT @event_batch_size;
                """;
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("day_start", dayStart));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("day_end", dayStart.AddDays(1)));
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

    private static async Task<long> UpsertDailyHitSummaryBatchAsync(
        NpgsqlConnection connection,
        DateOnly summaryDate,
        IReadOnlyList<Guid> eventIds,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH ranked AS (
                SELECT
                    @summary_date::date AS summary_date,
                    COALESCE(e.tenant_id, '00000000-0000-0000-0000-000000000000'::uuid) AS tenant_id,
                    COALESCE(e.owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid) AS owner_user_id,
                    COALESCE(e.project_id, '') AS project_id,
                    COALESCE(e.entry_point, '') AS entry_point,
                    COALESCE(h.memory_id, '00000000-0000-0000-0000-000000000000'::uuid) AS memory_id,
                    COALESCE(MIN(NULLIF(h.title, '')), '(untitled)') AS title,
                    COALESCE(MIN(NULLIF(h.memory_type, '')), '') AS memory_type,
                    COALESCE(MIN(NULLIF(h.source_type, '')), '') AS source_type,
                    COALESCE(h.source_ref, '') AS source_ref,
                    COUNT(*)::bigint AS hit_count,
                    MIN(h.rank) AS best_rank,
                    MAX(h.score) AS best_score,
                    AVG(h.score) AS average_score,
                    MIN(e.created_at) AS first_seen_at,
                    MAX(e.created_at) AS last_seen_at
                FROM retrieval_events e
                INNER JOIN retrieval_hits h ON h.retrieval_event_id = e.id
                WHERE e.id = ANY(@event_ids)
                GROUP BY e.tenant_id, e.owner_user_id, e.project_id, e.entry_point,
                         h.memory_id, h.source_ref
            )
            INSERT INTO retrieval_telemetry_daily_hit_summaries (
                summary_date,
                tenant_id,
                owner_user_id,
                project_id,
                entry_point,
                memory_id,
                title,
                memory_type,
                source_type,
                source_ref,
                hit_count,
                best_rank,
                best_score,
                average_score,
                first_seen_at,
                last_seen_at,
                updated_at)
            SELECT
                summary_date,
                tenant_id,
                owner_user_id,
                project_id,
                entry_point,
                memory_id,
                title,
                memory_type,
                source_type,
                source_ref,
                hit_count,
                best_rank,
                best_score,
                average_score,
                first_seen_at,
                last_seen_at,
                NOW()
            FROM ranked
            ON CONFLICT (summary_date, tenant_id, owner_user_id, project_id, entry_point, memory_id, source_ref)
            DO UPDATE SET
                title = EXCLUDED.title,
                memory_type = EXCLUDED.memory_type,
                source_type = EXCLUDED.source_type,
                hit_count = retrieval_telemetry_daily_hit_summaries.hit_count + EXCLUDED.hit_count,
                best_rank = LEAST(retrieval_telemetry_daily_hit_summaries.best_rank, EXCLUDED.best_rank),
                best_score = GREATEST(retrieval_telemetry_daily_hit_summaries.best_score, EXCLUDED.best_score),
                average_score =
                    ((COALESCE(retrieval_telemetry_daily_hit_summaries.average_score, 0) * retrieval_telemetry_daily_hit_summaries.hit_count)
                     + (COALESCE(EXCLUDED.average_score, 0) * EXCLUDED.hit_count))
                    / NULLIF(retrieval_telemetry_daily_hit_summaries.hit_count + EXCLUDED.hit_count, 0),
                first_seen_at = LEAST(retrieval_telemetry_daily_hit_summaries.first_seen_at, EXCLUDED.first_seen_at),
                last_seen_at = GREATEST(retrieval_telemetry_daily_hit_summaries.last_seen_at, EXCLUDED.last_seen_at),
                updated_at = EXCLUDED.updated_at;
            """;
        command.Parameters.Add(new NpgsqlParameter<DateOnly>("summary_date", summaryDate));
        command.Parameters.Add(new NpgsqlParameter<Guid[]>("event_ids", eventIds.ToArray()));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task PruneDailyHitSummaryAsync(
        NpgsqlConnection connection,
        DateOnly summaryDate,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH ranked AS (
                SELECT summary_date,
                       tenant_id,
                       owner_user_id,
                       project_id,
                       entry_point,
                       memory_id,
                       source_ref,
                       ROW_NUMBER() OVER (
                           PARTITION BY summary_date, tenant_id, owner_user_id, project_id, entry_point
                           ORDER BY hit_count DESC, best_rank ASC, title ASC
                       ) AS row_number
                FROM retrieval_telemetry_daily_hit_summaries
                WHERE summary_date = @summary_date
            )
            DELETE FROM retrieval_telemetry_daily_hit_summaries target
            USING ranked
            WHERE target.summary_date = ranked.summary_date
              AND target.tenant_id = ranked.tenant_id
              AND target.owner_user_id = ranked.owner_user_id
              AND target.project_id = ranked.project_id
              AND target.entry_point = ranked.entry_point
              AND target.memory_id = ranked.memory_id
              AND target.source_ref = ranked.source_ref
              AND ranked.row_number > @top_per_bucket;
            """;
        command.Parameters.Add(new NpgsqlParameter<DateOnly>("summary_date", summaryDate));
        command.Parameters.Add(new NpgsqlParameter<int>("top_per_bucket", policy.HitSummaryTopPerBucket));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task DeleteSummaryRowsForDayAsync(
        NpgsqlConnection connection,
        string table,
        DateOnly summaryDate,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = $"DELETE FROM {table} WHERE summary_date = @summary_date;";
        command.Parameters.Add(new NpgsqlParameter<DateOnly>("summary_date", summaryDate));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddSummaryDayParameters(NpgsqlCommand command, DateOnly summaryDate)
    {
        var dayStart = new DateTimeOffset(summaryDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        command.Parameters.Add(new NpgsqlParameter<DateOnly>("summary_date", summaryDate));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("day_start", dayStart));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("day_end", dayStart.AddDays(1)));
    }

    private async Task DeleteOtherRetentionTablesAsync(
        NpgsqlConnection connection,
        RetentionProgress progress,
        Guid currentRunId,
        CancellationToken cancellationToken)
    {
        progress.DeletedSecurityAuditEvents += await DeleteOlderThanAsync(
            connection,
            "security_audit_events",
            "created_at",
            timeProvider.GetUtcNow().AddDays(-progress.Policy.SecurityAuditRetentionDays),
            progress.Policy,
            cancellationToken);
        progress.DeletedRuntimeLogEntries += await DeleteOlderThanAsync(
            connection,
            "runtime_log_entries",
            "created_at",
            timeProvider.GetUtcNow().AddDays(-progress.Policy.RuntimeLogRetentionDays),
            progress.Policy,
            cancellationToken);
        progress.DeletedMaintenanceRuns += await DeleteOldMaintenanceRunsAsync(
            connection,
            currentRunId,
            timeProvider.GetUtcNow().AddDays(-progress.Policy.MaintenanceRunRetentionDays),
            progress.Policy,
            cancellationToken);
        await UpdateProgressAsync(progress, cancellationToken);
    }

    private static async Task<long> DeleteOlderThanAsync(
        NpgsqlConnection connection,
        string table,
        string column,
        DateTimeOffset cutoff,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = $"DELETE FROM {table} WHERE {column} < @cutoff;";
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<long> DeleteOldMaintenanceRunsAsync(
        NpgsqlConnection connection,
        Guid currentRunId,
        DateTimeOffset cutoff,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            DELETE FROM maintenance_runs
            WHERE id <> @current_run_id
              AND status <> 'Running'
              AND started_at < @cutoff;
            """;
        command.Parameters.Add(new NpgsqlParameter<Guid>("current_run_id", currentRunId));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task DeleteExpiredSummariesAsync(
        NpgsqlConnection connection,
        RetentionProgress progress,
        CancellationToken cancellationToken)
    {
        var cutoff = DateOnly.FromDateTime(timeProvider.GetUtcNow().AddDays(-progress.Policy.SummaryRetentionDays).UtcDateTime.Date);
        progress.DeletedEventSummaryRows += await DeleteSummaryRowsAsync(
            connection,
            "retrieval_telemetry_daily_summaries",
            cutoff,
            progress.Policy,
            cancellationToken);
        progress.DeletedHitSummaryRows += await DeleteSummaryRowsAsync(
            connection,
            "retrieval_telemetry_daily_hit_summaries",
            cutoff,
            progress.Policy,
            cancellationToken);
        await UpdateProgressAsync(progress, cancellationToken);
    }

    private static async Task<long> DeleteSummaryRowsAsync(
        NpgsqlConnection connection,
        string table,
        DateOnly cutoff,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = $"DELETE FROM {table} WHERE summary_date < @cutoff;";
        command.Parameters.Add(new NpgsqlParameter<DateOnly>("cutoff", cutoff));
        return await command.ExecuteNonQueryAsync(cancellationToken);
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
        foreach (var table in RetentionVacuumTables)
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
            WHERE relname IN (
                'retrieval_events',
                'retrieval_hits',
                'retrieval_telemetry_daily_summaries',
                'retrieval_telemetry_daily_hit_summaries',
                'security_audit_events',
                'runtime_log_entries',
                'maintenance_runs')
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
            progress.ProcessedEventsWindows,
            upsertedEventSummaryRows: progress.UpsertedEventSummaryRows,
            upsertedHitSummaryRows: progress.UpsertedHitSummaryRows,
            processedSummaryDays: progress.ProcessedSummaryDays,
            deletedEventSummaryRows: progress.DeletedEventSummaryRows,
            deletedHitSummaryRows: progress.DeletedHitSummaryRows,
            deletedSecurityAuditEvents: progress.DeletedSecurityAuditEvents,
            deletedRuntimeLogEntries: progress.DeletedRuntimeLogEntries,
            deletedMaintenanceRuns: progress.DeletedMaintenanceRuns);
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
        string? error = null,
        long upsertedEventSummaryRows = 0,
        long upsertedHitSummaryRows = 0,
        int processedSummaryDays = 0,
        long deletedEventSummaryRows = 0,
        long deletedHitSummaryRows = 0,
        long deletedSecurityAuditEvents = 0,
        long deletedRuntimeLogEntries = 0,
        long deletedMaintenanceRuns = 0)
        => JsonSerializer.Serialize(new
        {
            deletedHits,
            deletedEvents,
            upsertedEventSummaryRows,
            upsertedHitSummaryRows,
            processedSummaryDays,
            deletedEventSummaryRows,
            deletedHitSummaryRows,
            otherTableRetention = new
            {
                deletedSecurityAuditEvents,
                deletedRuntimeLogEntries,
                deletedMaintenanceRuns
            },
            startedAtUtc = startedAt,
            observedAtUtc = observedAt,
            completedAtUtc = completed ? observedAt : (DateTimeOffset?)null,
            durationMs = (observedAt - startedAt).TotalMilliseconds,
            tableSizeBeforeBytes = sizeBefore,
            tableSizeAfterBytes = sizeAfter,
            policy = new
            {
                hitsRetentionDays = policy.HitsRetentionDays,
                eventsRetentionDays = policy.EventsRetentionDays,
                summaryRetentionDays = policy.SummaryRetentionDays,
                securityAuditRetentionDays = policy.SecurityAuditRetentionDays,
                runtimeLogRetentionDays = policy.RuntimeLogRetentionDays,
                maintenanceRunRetentionDays = policy.MaintenanceRunRetentionDays,
                hitSummaryTopPerBucket = policy.HitSummaryTopPerBucket
            },
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
        public long UpsertedEventSummaryRows { get; set; }
        public long UpsertedHitSummaryRows { get; set; }
        public int ProcessedSummaryDays { get; set; }
        public long DeletedEventSummaryRows { get; set; }
        public long DeletedHitSummaryRows { get; set; }
        public long DeletedSecurityAuditEvents { get; set; }
        public long DeletedRuntimeLogEntries { get; set; }
        public long DeletedMaintenanceRuns { get; set; }
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

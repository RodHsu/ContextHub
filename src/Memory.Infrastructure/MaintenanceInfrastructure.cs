using System.Globalization;
using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;
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
        var payload = JsonSerializer.Serialize(stored, SerializerOptions);
        if (ttl.HasValue)
        {
            await _database.StringSetAsync(StateKey, payload, new Expiration(ttl.Value));
            return;
        }

        await _database.StringSetAsync(StateKey, payload);
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
                 x.MaintenanceType == MaintenanceRunType.VacuumFullReclaim ||
                 x.MaintenanceType == MaintenanceRunType.MemoryDataRetention) &&
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
    int MaxSummaryDaysPerRun,
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
            Math.Clamp(options.MaxSummaryDaysPerRun, 0, 31),
            Math.Clamp(request.BatchSize ?? options.BatchSize, 1, 100_000),
            Math.Clamp(request.EventBatchSize ?? options.EventBatchSize, 1, 100_000),
            Math.Clamp(request.TimeWindowDays ?? options.TimeWindowDays, 1, 3),
            Math.Clamp(request.DelayBetweenBatchesMs ?? options.DelayBetweenBatchesMs, 0, 60_000),
            Math.Clamp(request.CommandTimeoutSeconds ?? options.CommandTimeoutSeconds, 1, 3600),
            TimeSpan.FromMinutes(Math.Clamp(request.MaxDurationMinutes ?? options.MaxDurationMinutes, 1, 120)),
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
        "embedding_usage_hourly",
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
                maxSummaryDaysPerRun = policy.MaxSummaryDaysPerRun,
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
        string? summaryBackfillError = null;
        string? summaryBackfillErrorKind = null;
        DateOnly? summaryBackfillFailedDay = null;
        var summaryBackfillFailureCount = 0;
        string? summaryBackfillLastExceptionType = null;
        string? hitRetentionSkippedReason = null;
        string? hitRetentionError = null;

        try
        {
            sizeBefore = await ReadTableSizesAsync(connection, policy, cancellationToken);
            progress = new RetentionProgress(run.Id, startedAt, policy, sizeBefore);

            progress.DroppedHitPartitions += await DropExpiredMonthlyPartitionsAsync(
                connection,
                "retrieval_hits",
                hitsCutoff,
                run.TriggeredBy,
                policy,
                cancellationToken);
            progress.DroppedEventPartitions += await DropExpiredMonthlyPartitionsAsync(
                connection,
                "retrieval_events",
                eventsCutoff,
                run.TriggeredBy,
                policy,
                cancellationToken);
            if (progress.DroppedHitPartitions > 0 || progress.DroppedEventPartitions > 0)
            {
                await UpdateProgressAsync(progress, cancellationToken);
            }

            if (await CanRunDirectHitRetentionAsync(connection, policy, cancellationToken))
            {
                try
                {
                    while (!ShouldStopForMaxDuration(startedAt, policy, out _))
                    {
                        hitsWindow = await ResolveRetentionWindowAsync(connection, hitsCutoff, policy, requiresHits: true, cancellationToken);
                        if (hitsWindow is null)
                        {
                            break;
                        }

                        progress.CurrentHitsWindow = hitsWindow;
                        var deletedInWindow = await DeleteHitsAsync(connection, progress, hitsCutoff, hitsWindow, cancellationToken);
                        progress.ProcessedHitsWindows++;
                        if (deletedInWindow == 0)
                        {
                            break;
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    hitRetentionError = ex.Message;
                    progress.HitRetentionError = hitRetentionError;
                    logger.LogWarning(ex, "Retrieval hit retention failed; event retention will continue for run {MaintenanceRunId}.", run.Id);
                    await UpdateProgressAsync(progress, cancellationToken);
                }
            }
            else
            {
                hitRetentionSkippedReason = "retrieval_hits_created_at_retention_index_missing";
                progress.HitRetentionSkippedReason = hitRetentionSkippedReason;
                logger.LogWarning("Skipping direct retrieval hit retention because the created_at retention index is missing; event retention will continue for run {MaintenanceRunId}.", run.Id);
                await UpdateProgressAsync(progress, cancellationToken);
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
            deletedHits = progress.DeletedHits;

            if (!ShouldStopForMaxDuration(startedAt, policy, out _))
            {
                await DeleteOtherRetentionTablesAsync(connection, progress, run.Id, cancellationToken);
                await DeleteExpiredSummariesAsync(connection, progress, cancellationToken);
                try
                {
                    await UpsertDailySummariesAsync(connection, progress, cancellationToken);
                    summaryBackfillError = progress.SummaryBackfillError;
                    summaryBackfillErrorKind = progress.SummaryBackfillErrorKind;
                    summaryBackfillFailedDay = progress.SummaryBackfillFailedDay;
                    summaryBackfillFailureCount = progress.SummaryBackfillFailureCount;
                    summaryBackfillLastExceptionType = progress.SummaryBackfillLastExceptionType;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    summaryBackfillError = ex.Message;
                    summaryBackfillErrorKind = ClassifyRetentionError(ex);
                    summaryBackfillFailureCount++;
                    summaryBackfillLastExceptionType = ex.GetType().Name;
                    progress.SummaryBackfillError = summaryBackfillError;
                    progress.SummaryBackfillErrorKind = summaryBackfillErrorKind;
                    progress.SummaryBackfillFailureCount = summaryBackfillFailureCount;
                    progress.SummaryBackfillLastExceptionType = summaryBackfillLastExceptionType;
                    logger.LogWarning(ex, "Retrieval telemetry summary backfill failed; raw retention completed before summary failure for run {MaintenanceRunId}.", run.Id);
                    await UpdateProgressAsync(progress, cancellationToken);
                }
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
                    deletedMaintenanceRuns: progress.DeletedMaintenanceRuns,
                    deletedEmbeddingUsageBuckets: progress.DeletedEmbeddingUsageBuckets,
                    droppedHitPartitions: progress.DroppedHitPartitions,
                    droppedEventPartitions: progress.DroppedEventPartitions,
                    deletedHitsViaEventCascade: progress.DeletedHitsViaEventCascade,
                    summaryBackfillError: summaryBackfillError,
                    summaryBackfillErrorKind: summaryBackfillErrorKind,
                    summaryBackfillFailedDay: summaryBackfillFailedDay,
                    summaryBackfillFailureCount: summaryBackfillFailureCount,
                    summaryBackfillLastExceptionType: summaryBackfillLastExceptionType,
                    summaryEventBackfillError: progress.SummaryEventBackfillError,
                    summaryHitBackfillError: progress.SummaryHitBackfillError,
                    hitRetentionSkippedReason: hitRetentionSkippedReason,
                    hitRetentionError: hitRetentionError);
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
                deletedMaintenanceRuns: progress.DeletedMaintenanceRuns,
                deletedEmbeddingUsageBuckets: progress.DeletedEmbeddingUsageBuckets,
                droppedHitPartitions: progress.DroppedHitPartitions,
                droppedEventPartitions: progress.DroppedEventPartitions,
                deletedHitsViaEventCascade: progress.DeletedHitsViaEventCascade,
                summaryBackfillError: summaryBackfillError,
                summaryBackfillErrorKind: summaryBackfillErrorKind,
                summaryBackfillFailedDay: summaryBackfillFailedDay,
                summaryBackfillFailureCount: summaryBackfillFailureCount,
                summaryBackfillLastExceptionType: summaryBackfillLastExceptionType,
                summaryEventBackfillError: progress.SummaryEventBackfillError,
                summaryHitBackfillError: progress.SummaryHitBackfillError,
                hitRetentionSkippedReason: hitRetentionSkippedReason,
                hitRetentionError: hitRetentionError);
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
                deletedMaintenanceRuns: progress?.DeletedMaintenanceRuns ?? 0,
                deletedEmbeddingUsageBuckets: progress?.DeletedEmbeddingUsageBuckets ?? 0,
                droppedHitPartitions: progress?.DroppedHitPartitions ?? 0,
                droppedEventPartitions: progress?.DroppedEventPartitions ?? 0,
                deletedHitsViaEventCascade: progress?.DeletedHitsViaEventCascade ?? 0,
                summaryBackfillError: summaryBackfillError,
                summaryBackfillErrorKind: summaryBackfillErrorKind,
                summaryBackfillFailedDay: summaryBackfillFailedDay,
                summaryBackfillFailureCount: summaryBackfillFailureCount,
                summaryBackfillLastExceptionType: summaryBackfillLastExceptionType,
                summaryEventBackfillError: progress?.SummaryEventBackfillError,
                summaryHitBackfillError: progress?.SummaryHitBackfillError,
                hitRetentionSkippedReason: hitRetentionSkippedReason,
                hitRetentionError: hitRetentionError);
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

    private static async Task<int> DropExpiredMonthlyPartitionsAsync(
        NpgsqlConnection connection,
        string parentTable,
        DateTimeOffset cutoff,
        string triggeredBy,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        var childTables = await ReadMonthlyPartitionChildrenAsync(connection, parentTable, policy, cancellationToken);
        var dropped = 0;
        foreach (var childTable in childTables)
        {
            if (!TryResolveMonthlyPartitionWindow(parentTable, childTable, out var start, out var end) || end > DateOnly.FromDateTime(cutoff.UtcDateTime.Date))
            {
                continue;
            }

            await using var dropCommand = connection.CreateCommand();
            dropCommand.CommandTimeout = policy.CommandTimeoutSeconds;
            dropCommand.CommandText = $"DROP TABLE IF EXISTS {QuoteIdentifier(childTable)};";
            await dropCommand.ExecuteNonQueryAsync(cancellationToken);

            await using var auditCommand = connection.CreateCommand();
            auditCommand.CommandTimeout = policy.CommandTimeoutSeconds;
            auditCommand.CommandText = """
                INSERT INTO retrieval_telemetry_partition_runs (
                    id,
                    parent_table,
                    partition_name,
                    partition_start,
                    partition_end,
                    action,
                    triggered_by)
                VALUES (
                    @id,
                    @parent_table,
                    @partition_name,
                    @partition_start,
                    @partition_end,
                    'drop_expired_partition',
                    @triggered_by);
                """;
            auditCommand.Parameters.Add(new NpgsqlParameter<Guid>("id", Guid.NewGuid()));
            auditCommand.Parameters.Add(new NpgsqlParameter<string>("parent_table", parentTable));
            auditCommand.Parameters.Add(new NpgsqlParameter<string>("partition_name", childTable));
            auditCommand.Parameters.Add(new NpgsqlParameter<DateOnly>("partition_start", start));
            auditCommand.Parameters.Add(new NpgsqlParameter<DateOnly>("partition_end", end));
            auditCommand.Parameters.Add(new NpgsqlParameter<string>("triggered_by", triggeredBy));
            await auditCommand.ExecuteNonQueryAsync(cancellationToken);

            dropped++;
        }

        return dropped;
    }

    private static async Task<IReadOnlyList<string>> ReadMonthlyPartitionChildrenAsync(
        NpgsqlConnection connection,
        string parentTable,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            SELECT child.relname
            FROM pg_inherits i
            INNER JOIN pg_class parent ON parent.oid = i.inhparent
            INNER JOIN pg_class child ON child.oid = i.inhrelid
            INNER JOIN pg_namespace ns ON ns.oid = child.relnamespace
            WHERE parent.relname = @parent_table
              AND pg_table_is_visible(parent.oid)
              AND ns.nspname = current_schema()
            ORDER BY child.relname ASC;
            """;
        command.Parameters.Add(new NpgsqlParameter<string>("parent_table", parentTable));
        var children = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            children.Add(reader.GetString(0));
        }

        return children;
    }

    private static bool TryResolveMonthlyPartitionWindow(string parentTable, string childTable, out DateOnly start, out DateOnly end)
    {
        start = default;
        end = default;
        var prefix = parentTable + "_";
        if (!childTable.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = childTable[prefix.Length..];
        var parts = suffix.Split('_', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 2 ||
            parts[0].Length != 4 ||
            parts[1].Length != 2 ||
            !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out var year) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var month) ||
            month is < 1 or > 12)
        {
            return false;
        }

        start = new DateOnly(year, month, 1);
        end = start.AddMonths(1);
        return true;
    }

    private static string QuoteIdentifier(string identifier)
        => "\"" + identifier.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

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
        while (true)
        {
            if (ShouldStopForMaxDuration(progress.StartedAt, progress.Policy, out _))
            {
                return total;
            }

            var deleted = await DeleteHitBatchAsync(connection, cutoff, window, progress.Policy, cancellationToken);
            total += deleted;
            progress.DeletedHits += deleted;
            await UpdateProgressAsync(progress, cancellationToken);
            if (deleted == 0)
            {
                return total;
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
                affected_hits AS (
                    SELECT COUNT(*)::bigint AS count
                    FROM retrieval_hits h
                    INNER JOIN target ON target.id = h.retrieval_event_id
                ),
                deleted AS (
                    DELETE FROM retrieval_events e
                    USING target
                    WHERE e.id = target.id
                    RETURNING 1
                )
                SELECT
                    COUNT(*)::bigint AS deleted_events,
                    COALESCE((SELECT count FROM affected_hits), 0)::bigint AS cascade_deleted_hits
                FROM deleted;
                """;
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoff));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("window_start", window.Start));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("window_end", window.End));
            command.Parameters.Add(new NpgsqlParameter<int>("batch_size", progress.Policy.EventBatchSize));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var deleted = 0L;
            var cascadeDeletedHits = 0L;
            if (await reader.ReadAsync(cancellationToken))
            {
                deleted = reader.GetInt64(0);
                cascadeDeletedHits = reader.GetInt64(1);
            }

            total += deleted;
            progress.DeletedEvents += deleted;
            progress.DeletedHits += cascadeDeletedHits;
            progress.DeletedHitsViaEventCascade += cascadeDeletedHits;
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
        if (progress.Policy.MaxSummaryDaysPerRun <= 0)
        {
            return;
        }

        var earliestBackfillDate = summaryCutoffDate.AddDays(-progress.Policy.EventsRetentionDays);
        var currentDate = await ResolveNextUnsummarizedDateAsync(
            connection,
            earliestBackfillDate,
            summaryCutoffDate,
            progress.Policy,
            cancellationToken);

        while (currentDate.HasValue &&
               currentDate.Value < summaryCutoffDate &&
               progress.ProcessedSummaryDays < progress.Policy.MaxSummaryDaysPerRun)
        {
            if (ShouldStopForMaxDuration(progress.StartedAt, progress.Policy, out _))
            {
                return;
            }

            var day = currentDate.Value;
            var eventSummaryCompleted = false;
            try
            {
                progress.UpsertedEventSummaryRows += await UpsertDailyEventSummaryAsync(connection, day, progress.Policy, cancellationToken);
                eventSummaryCompleted = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                RecordSummaryBackfillFailure(progress, day, ex);
                progress.SummaryEventBackfillError = ex.Message;
                logger.LogWarning(ex, "Retrieval telemetry event summary backfill failed for {SummaryDate}; raw retention will continue.", day);
            }

            if (eventSummaryCompleted)
            {
                try
                {
                    await UpsertDailyHitSummaryAsync(connection, progress, day, cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    RecordSummaryBackfillFailure(progress, day, ex);
                    progress.SummaryHitBackfillError = ex.Message;
                    logger.LogWarning(ex, "Retrieval telemetry hit summary backfill failed for {SummaryDate}; event summary retention will continue.", day);
                }
            }

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
        for (var date = fromDate; date < summaryCutoffDate; date = date.AddDays(1))
        {
            await using var command = connection.CreateCommand();
            command.CommandTimeout = policy.CommandTimeoutSeconds;
            command.CommandText = """
                SELECT EXISTS (
                    SELECT 1
                    FROM retrieval_events
                    WHERE created_at >= @day_start
                      AND created_at < @day_end
                    LIMIT 1
                )
                AND NOT EXISTS (
                    SELECT 1
                    FROM retrieval_telemetry_daily_summaries existing
                    WHERE existing.summary_date = @summary_date
                    LIMIT 1
                );
                """;
            var dayStart = new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("day_start", dayStart));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("day_end", dayStart.AddDays(1)));
            command.Parameters.Add(new NpgsqlParameter<DateOnly>("summary_date", date));
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                return date;
            }
        }

        return null;
    }

    private static void RecordSummaryBackfillFailure(RetentionProgress progress, DateOnly summaryDate, Exception exception)
    {
        progress.SummaryBackfillError = exception.Message;
        progress.SummaryBackfillErrorKind = ClassifyRetentionError(exception);
        progress.SummaryBackfillFailedDay = summaryDate;
        progress.SummaryBackfillFailureCount++;
        progress.SummaryBackfillLastExceptionType = exception.GetType().Name;
    }

    private static string ClassifyRetentionError(Exception exception)
    {
        if (ContainsException<TimeoutException>(exception) ||
            exception.ToString().Contains("Timeout during reading attempt", StringComparison.OrdinalIgnoreCase))
        {
            return "databaseReadTimeout";
        }

        if (ContainsException<NpgsqlException>(exception))
        {
            return "database";
        }

        return exception.GetType().Name;
    }

    private static bool ContainsException<TException>(Exception exception)
        where TException : Exception
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is TException)
            {
                return true;
            }
        }

        return false;
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

    private async Task UpsertDailyHitSummaryAsync(
        NpgsqlConnection connection,
        RetentionProgress progress,
        DateOnly summaryDate,
        CancellationToken cancellationToken)
    {
        var policy = progress.Policy;
        if (ShouldStopForMaxDuration(progress.StartedAt, policy, out _))
        {
            return;
        }

        await DeleteSummaryRowsForDayAsync(
            connection,
            "retrieval_telemetry_daily_hit_summaries",
            summaryDate,
            policy,
            cancellationToken);

        DateTimeOffset? afterCreatedAt = null;
        Guid? afterId = null;
        try
        {
            while (true)
            {
                if (ShouldStopForMaxDuration(progress.StartedAt, policy, out _))
                {
                    progress.SummaryHitBackfillError = "maxDuration";
                    await DeleteSummaryRowsForDayAsync(
                        connection,
                        "retrieval_telemetry_daily_hit_summaries",
                        summaryDate,
                        policy,
                        cancellationToken);
                    await UpdateProgressAsync(progress, cancellationToken);
                    return;
                }

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

                progress.UpsertedHitSummaryRows += await UpsertDailyHitSummaryBatchAsync(connection, summaryDate, eventWindow.EventIds, policy, cancellationToken);
                afterCreatedAt = eventWindow.LastCreatedAt;
                afterId = eventWindow.LastId;
                await UpdateProgressAsync(progress, cancellationToken);
                await DelayBetweenBatchesAsync(policy, cancellationToken);
            }

            if (ShouldStopForMaxDuration(progress.StartedAt, policy, out _))
            {
                progress.SummaryHitBackfillError = "maxDuration";
                await DeleteSummaryRowsForDayAsync(
                    connection,
                    "retrieval_telemetry_daily_hit_summaries",
                    summaryDate,
                    policy,
                    cancellationToken);
                await UpdateProgressAsync(progress, cancellationToken);
                return;
            }

            await PruneDailyHitSummaryAsync(connection, summaryDate, policy, cancellationToken);
        }
        catch
        {
            await DeleteSummaryRowsForDayAsync(
                connection,
                "retrieval_telemetry_daily_hit_summaries",
                summaryDate,
                policy,
                cancellationToken);
            throw;
        }
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
        progress.DeletedEmbeddingUsageBuckets += await DeleteOlderThanAsync(
            connection,
            "embedding_usage_hourly",
            "bucket_start_utc",
            timeProvider.GetUtcNow().AddDays(-progress.Policy.SummaryRetentionDays),
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

    private static async Task<bool> CanRunDirectHitRetentionAsync(
        NpgsqlConnection connection,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            SELECT
                EXISTS (
                    SELECT 1
                    FROM pg_indexes
                    WHERE schemaname = current_schema()
                      AND tablename = 'retrieval_hits'
                      AND indexname = 'ix_retrieval_hits_created_at_event_id'
                )
                OR COALESCE((
                    SELECT reltuples
                    FROM pg_class
                    WHERE oid = 'retrieval_hits'::regclass
                ), 0) < 100000;
            """;
        return (bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false);
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
                SELECT MIN(h.created_at)
                FROM retrieval_hits h
                WHERE h.created_at IS NOT NULL
                  AND h.created_at < @cutoff;
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
        DateTimeOffset cutoff,
        RetentionWindow window,
        RetrievalTelemetryRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH target AS (
                SELECT h.id
                FROM retrieval_hits h
                WHERE h.created_at IS NOT NULL
                  AND h.created_at < @cutoff
                  AND h.created_at >= @window_start
                  AND h.created_at < @window_end
                ORDER BY h.created_at ASC, h.retrieval_event_id ASC, h.rank ASC, h.id ASC
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
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("window_start", window.Start));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("window_end", window.End));
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
                'embedding_usage_hourly',
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
            deletedMaintenanceRuns: progress.DeletedMaintenanceRuns,
            deletedEmbeddingUsageBuckets: progress.DeletedEmbeddingUsageBuckets,
            droppedHitPartitions: progress.DroppedHitPartitions,
            droppedEventPartitions: progress.DroppedEventPartitions,
            deletedHitsViaEventCascade: progress.DeletedHitsViaEventCascade,
            summaryBackfillError: progress.SummaryBackfillError,
            summaryBackfillErrorKind: progress.SummaryBackfillErrorKind,
            summaryBackfillFailedDay: progress.SummaryBackfillFailedDay,
            summaryBackfillFailureCount: progress.SummaryBackfillFailureCount,
            summaryBackfillLastExceptionType: progress.SummaryBackfillLastExceptionType,
            summaryEventBackfillError: progress.SummaryEventBackfillError,
            summaryHitBackfillError: progress.SummaryHitBackfillError,
            hitRetentionSkippedReason: progress.HitRetentionSkippedReason,
            hitRetentionError: progress.HitRetentionError);
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
        long deletedMaintenanceRuns = 0,
        long deletedEmbeddingUsageBuckets = 0,
        int droppedHitPartitions = 0,
        int droppedEventPartitions = 0,
        long deletedHitsViaEventCascade = 0,
        string? summaryBackfillError = null,
        string? summaryBackfillErrorKind = null,
        DateOnly? summaryBackfillFailedDay = null,
        int summaryBackfillFailureCount = 0,
        string? summaryBackfillLastExceptionType = null,
        string? summaryEventBackfillError = null,
        string? summaryHitBackfillError = null,
        string? hitRetentionSkippedReason = null,
        string? hitRetentionError = null)
        => JsonSerializer.Serialize(new
        {
            deletedHits,
            deletedHitsViaEventCascade,
            deletedEvents,
            droppedHitPartitions,
            droppedEventPartitions,
            hitRetentionSkippedReason,
            hitRetentionError,
            upsertedEventSummaryRows,
            upsertedHitSummaryRows,
            processedSummaryDays,
            summaryBackfillError,
            summaryBackfillErrorKind,
            summaryBackfillFailedDay,
            summaryBackfillFailureCount,
            summaryBackfillLastExceptionType,
            summaryEventBackfillError,
            summaryHitBackfillError,
            deletedEventSummaryRows,
            deletedHitSummaryRows,
            otherTableRetention = new
            {
                deletedSecurityAuditEvents,
                deletedRuntimeLogEntries,
                deletedMaintenanceRuns,
                deletedEmbeddingUsageBuckets
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
                hitSummaryTopPerBucket = policy.HitSummaryTopPerBucket,
                maxSummaryDaysPerRun = policy.MaxSummaryDaysPerRun
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
        public long DeletedHitsViaEventCascade { get; set; }
        public long DeletedEvents { get; set; }
        public long UpsertedEventSummaryRows { get; set; }
        public long UpsertedHitSummaryRows { get; set; }
        public int ProcessedSummaryDays { get; set; }
        public long DeletedEventSummaryRows { get; set; }
        public long DeletedHitSummaryRows { get; set; }
        public long DeletedSecurityAuditEvents { get; set; }
        public long DeletedRuntimeLogEntries { get; set; }
        public long DeletedMaintenanceRuns { get; set; }
        public long DeletedEmbeddingUsageBuckets { get; set; }
        public int DroppedHitPartitions { get; set; }
        public int DroppedEventPartitions { get; set; }
        public string? SummaryBackfillError { get; set; }
        public string? SummaryBackfillErrorKind { get; set; }
        public DateOnly? SummaryBackfillFailedDay { get; set; }
        public int SummaryBackfillFailureCount { get; set; }
        public string? SummaryBackfillLastExceptionType { get; set; }
        public string? SummaryEventBackfillError { get; set; }
        public string? SummaryHitBackfillError { get; set; }
        public string? HitRetentionSkippedReason { get; set; }
        public string? HitRetentionError { get; set; }
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

public sealed record MemoryDataRetentionPolicy(
    int ArchivedItemsRetentionDays,
    int HitWindowDays,
    long MaxRecentHitCount,
    int MaxLinkDegree,
    decimal MaxImportance,
    decimal MaxConfidence,
    int PreviewLimit,
    int PreviewOffset,
    int BatchSize,
    int DelayBetweenBatchesMs,
    int RevisionRetentionDays,
    int MinRevisionsToKeep,
    int MaxChunksPerMemoryItem,
    int CommandTimeoutSeconds,
    TimeSpan MaxDuration,
    bool AutoApplyEnabled)
{
    public static MemoryDataRetentionPolicy Create(
        MemoryDataRetentionOptions options,
        MemoryDataRetentionRunRequest request)
        => new(
            Math.Max(1, request.ArchivedItemsRetentionDays ?? options.ArchivedItemsRetentionDays),
            Math.Max(1, request.HitWindowDays ?? options.HitWindowDays),
            Math.Max(0, request.MaxRecentHitCount ?? options.MaxRecentHitCount),
            Math.Max(0, request.MaxLinkDegree ?? options.MaxLinkDegree),
            Math.Clamp(request.MaxImportance ?? options.MaxImportance, 0m, 1m),
            Math.Clamp(request.MaxConfidence ?? options.MaxConfidence, 0m, 1m),
            Math.Clamp(request.PreviewLimit ?? options.PreviewLimit, 1, 500),
            Math.Max(0, request.PreviewOffset),
            Math.Clamp(request.BatchSize ?? options.BatchSize, 1, 100_000),
            Math.Clamp(request.DelayBetweenBatchesMs ?? options.DelayBetweenBatchesMs, 0, 60_000),
            Math.Clamp(request.RevisionRetentionDays ?? options.RevisionRetentionDays, 1, 3650),
            Math.Clamp(request.MinRevisionsToKeep ?? options.MinRevisionsToKeep, 1, 1_000),
            Math.Clamp(request.MaxChunksPerMemoryItem ?? options.MaxChunksPerMemoryItem, 1, 100_000),
            Math.Clamp(request.CommandTimeoutSeconds ?? options.CommandTimeoutSeconds, 1, 3600),
            TimeSpan.FromMinutes(Math.Clamp(request.MaxDurationMinutes ?? options.MaxDurationMinutes, 1, 30)),
            options.AutoApplyEnabled);

    public MemoryDataRetentionPolicyThresholds ToThresholds()
        => new(
            ArchivedItemsRetentionDays,
            HitWindowDays,
            MaxRecentHitCount,
            MaxLinkDegree,
            MaxImportance,
            MaxConfidence,
            PreviewLimit,
            RevisionRetentionDays,
            MinRevisionsToKeep,
            MaxChunksPerMemoryItem);
}

public sealed class MemoryDataRetentionService(
    NpgsqlDataSource dataSource,
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    IOptions<MemoryDataRetentionOptions> options,
    ICacheVersionStore cacheStore,
    TimeProvider timeProvider,
    ILogger<MemoryDataRetentionService> logger) : IMemoryDataRetentionService
{
    private const long AdvisoryLockKey = 941223;
    private const string MemoryStatusArchived = "Archived";
    private static readonly string[] RetentionTables =
    [
        "memory_links",
        "memory_item_revisions",
        "memory_chunk_vectors",
        "memory_item_chunks",
        "memory_items"
    ];
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);
    private const string ClassificationCte = """
        WITH recent_hits AS (
            SELECT memory_id, COALESCE(SUM(hit_count), 0)::bigint AS recent_hit_count
            FROM retrieval_telemetry_daily_hit_summaries
            WHERE summary_date >= @hit_window_start
            GROUP BY memory_id
        ),
        link_degrees AS (
            SELECT memory_id, COUNT(*)::int AS link_degree
            FROM (
                SELECT from_id AS memory_id FROM memory_links
                UNION ALL
                SELECT to_id AS memory_id FROM memory_links
            ) links
            GROUP BY memory_id
        ),
        classified AS (
            SELECT
                mi.id,
                mi.project_id,
                mi.title,
                mi.memory_type,
                mi.status,
                mi.importance,
                mi.confidence,
                mi.updated_at,
                COALESCE(rh.recent_hit_count, 0)::bigint AS recent_hit_count,
                COALESCE(ld.link_degree, 0)::int AS link_degree,
                (mi.metadata_json::text ILIKE '%"missing":true%' AND mi.metadata_json::text ILIKE '%"sourceManaged":true%') AS source_managed_missing,
                (
                    EXISTS (
                        SELECT 1
                        FROM unnest(mi.tags) tag
                        WHERE lower(tag) IN ('superseded', 'replaced')
                    )
                    OR mi.metadata_json::text ILIKE '%"supersededByMemoryId"%'
                    OR mi.metadata_json::text ILIKE '%"replacedByMemoryId"%'
                ) AS superseded_or_replaced,
                mi.memory_type IN ('Decision', 'Preference') AS protected_type,
                EXISTS (
                    SELECT 1
                    FROM unnest(mi.tags) tag
                    WHERE lower(tag) IN ('keep', 'pinned')
                ) AS protected_tag,
                (
                    mi.status = 'Active'
                    AND mi.memory_type IN ('Fact', 'Episode', 'Artifact', 'Summary')
                    AND mi.updated_at < @stale_cutoff
                    AND mi.importance <= 0.65
                    AND mi.confidence <= 0.80
                ) AS stale_active,
                (
                    mi.status = 'Active'
                    AND mi.memory_type = 'Episode'
                    AND mi.updated_at < @episode_cutoff
                    AND (mi.importance <= 0.55 OR mi.confidence <= 0.70)
                ) AS low_signal_episode,
                (
                    mi.status = 'Archived'
                    AND mi.updated_at < @cutoff
                    AND mi.importance <= @max_importance
                    AND mi.confidence <= @max_confidence
                    AND COALESCE(rh.recent_hit_count, 0) <= @max_recent_hit_count
                    AND COALESCE(ld.link_degree, 0) <= @max_link_degree
                    AND mi.memory_type NOT IN ('Decision', 'Preference')
                    AND NOT mi.is_read_only
                    AND NOT EXISTS (
                        SELECT 1
                        FROM unnest(mi.tags) tag
                        WHERE lower(tag) IN ('keep', 'pinned')
                    )
                ) AS is_auto_delete,
                mi.is_read_only
            FROM memory_items mi
            LEFT JOIN recent_hits rh ON rh.memory_id = mi.id
            LEFT JOIN link_degrees ld ON ld.memory_id = mi.id
            WHERE
                (cardinality(@project_ids) = 0 OR mi.project_id = ANY(@project_ids))
                AND (@tenant_id IS NULL OR mi.tenant_id = @tenant_id)
                AND
                (
                    (mi.status = 'Archived' AND mi.updated_at < @cutoff)
                    OR (
                    mi.status = 'Active'
                    AND (
                        (
                            mi.memory_type IN ('Fact', 'Episode', 'Artifact', 'Summary')
                            AND mi.updated_at < @stale_cutoff
                            AND mi.importance <= 0.65
                            AND mi.confidence <= 0.80
                        )
                        OR (
                            mi.memory_type = 'Episode'
                            AND mi.updated_at < @episode_cutoff
                            AND (mi.importance <= 0.55 OR mi.confidence <= 0.70)
                        )
                        OR (mi.metadata_json::text ILIKE '%"missing":true%' AND mi.metadata_json::text ILIKE '%"sourceManaged":true%')
                        OR EXISTS (
                            SELECT 1
                            FROM unnest(mi.tags) tag
                            WHERE lower(tag) IN ('superseded', 'replaced')
                        )
                        OR mi.metadata_json::text ILIKE '%"supersededByMemoryId"%'
                        OR mi.metadata_json::text ILIKE '%"replacedByMemoryId"%'
                    )
                    )
                )
        )
        """;
    private readonly MemoryDataRetentionOptions _options = options.Value;

    public async Task<MemoryDataRetentionRunResult> RunAsync(string triggeredBy, CancellationToken cancellationToken)
        => await RunAsync(new MemoryDataRetentionRunRequest(TriggeredBy: triggeredBy), triggeredBy, cancellationToken);

    public async Task<MemoryDataRetentionRunResult> RunAsync(MemoryDataRetentionRunRequest request, string fallbackTriggeredBy, CancellationToken cancellationToken)
    {
        var policy = MemoryDataRetentionPolicy.Create(_options, request);
        var mode = ResolveMode(request, policy);
        if (request.ProjectIds is { Count: > 0 } && mode != MemoryDataRetentionRunMode.Classify)
        {
            throw new InvalidOperationException("ProjectIds is only supported for Classify retention runs.");
        }

        var now = timeProvider.GetUtcNow();
        var startedAt = now;
        var cutoff = now.AddDays(-policy.ArchivedItemsRetentionDays);
        var hitWindowStart = DateOnly.FromDateTime(now.AddDays(-policy.HitWindowDays).UtcDateTime);
        var run = new MaintenanceRun
        {
            MaintenanceType = MaintenanceRunType.MemoryDataRetention,
            Status = MaintenanceRunStatus.Running,
            StartedAt = startedAt,
            TriggeredBy = NormalizeTriggeredBy(request.TriggeredBy, fallbackTriggeredBy),
            PolicyJson = JsonSerializer.Serialize(new
            {
                mode,
                archivedItemsRetentionDays = policy.ArchivedItemsRetentionDays,
                hitWindowDays = policy.HitWindowDays,
                maxRecentHitCount = policy.MaxRecentHitCount,
                maxLinkDegree = policy.MaxLinkDegree,
                maxImportance = policy.MaxImportance,
                maxConfidence = policy.MaxConfidence,
                previewLimit = policy.PreviewLimit,
                batchSize = policy.BatchSize,
                delayBetweenBatchesMs = policy.DelayBetweenBatchesMs,
                revisionRetentionDays = policy.RevisionRetentionDays,
                minRevisionsToKeep = policy.MinRevisionsToKeep,
                maxChunksPerMemoryItem = policy.MaxChunksPerMemoryItem,
                commandTimeoutSeconds = policy.CommandTimeoutSeconds,
                maxDurationMinutes = policy.MaxDuration.TotalMinutes,
                cutoffUtc = cutoff,
                previewOnly = request.PreviewOnly,
                autoApplyEnabled = policy.AutoApplyEnabled
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
            var emptyClassification = MemoryDataRetentionClassification.Empty(policy);
            var skippedJson = BuildResultJson(startedAt, completedAt, cutoff, 0, 0, 0, 0, 0, [], mode, emptyClassification, request.PreviewOnly, policy, true, "anotherRunActive");
            await UpdateRunAsync(run.Id, MaintenanceRunStatus.Completed, completedAt, skippedJson, string.Empty, cancellationToken);
            return BuildRunResult(run.Id, cutoff, 0, 0, 0, 0, 0, [], request.PreviewOnly, mode, emptyClassification, startedAt, completedAt, skippedJson);
        }

        long deletedItems = 0;
        long deletedLinks = 0;
        long deletedRevisions = 0;
        long deletedChunks = 0;
        long deletedVectors = 0;
        long prunedRevisions = 0;
        long prunedChunks = 0;
        long prunedVectors = 0;
        string? stoppedReason = null;
        MemoryDataRetentionClassification classification = MemoryDataRetentionClassification.Empty(policy);
        IReadOnlyList<string> affectedProjectIds = [];
        var affectedProjectSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var projectIds = (request.ProjectIds ?? []).Select(projectId => ProjectContext.Normalize(projectId)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            classification = await ClassifyAsync(connection, cutoff, hitWindowStart, policy, request.IncludeCandidateDetails, projectIds, request.TenantId, cancellationToken);
            affectedProjectIds = await ReadAffectedProjectIdsAsync(connection, cutoff, hitWindowStart, policy, projectIds, request.TenantId, cancellationToken);
            affectedProjectSet.UnionWith(affectedProjectIds);

            if (mode == MemoryDataRetentionRunMode.PreviewDelete)
            {
                var preview = await PreviewAsync(connection, cutoff, hitWindowStart, policy, cancellationToken);
                deletedItems = preview.DeletedItems;
                deletedLinks = preview.DeletedLinks;
                deletedRevisions = preview.DeletedRevisions;
                deletedChunks = preview.DeletedChunks;
                deletedVectors = preview.DeletedVectors;
            }
            else if (mode == MemoryDataRetentionRunMode.ApplyAutoDelete)
            {
                while (true)
                {
                    if (ShouldStopForMaxDuration(startedAt, timeProvider, policy, out stoppedReason))
                    {
                        break;
                    }

                    var batch = await DeleteBatchAsync(connection, cutoff, hitWindowStart, policy, cancellationToken);
                    deletedItems += batch.DeletedItems;
                    deletedLinks += batch.DeletedLinks;
                    deletedRevisions += batch.DeletedRevisions;
                    deletedChunks += batch.DeletedChunks;
                    deletedVectors += batch.DeletedVectors;

                    if (batch.DeletedItems == 0)
                    {
                        break;
                    }

                    await UpdateRunAsync(
                        run.Id,
                        MaintenanceRunStatus.Running,
                        null,
                        BuildResultJson(
                            startedAt,
                            null,
                            cutoff,
                            deletedItems,
                            deletedLinks,
                            deletedRevisions,
                            deletedChunks,
                            deletedVectors,
                            affectedProjectIds,
                            mode,
                            classification,
                            request.PreviewOnly,
                            policy,
                            false,
                            stoppedReason,
                            completed: false),
                        string.Empty,
                        cancellationToken);

                    if (policy.DelayBetweenBatchesMs > 0)
                    {
                        await Task.Delay(TimeSpan.FromMilliseconds(policy.DelayBetweenBatchesMs), cancellationToken);
                    }
                }
            }

            if (mode is MemoryDataRetentionRunMode.ApplyAutoDelete or MemoryDataRetentionRunMode.ApplyMaintenanceCleanup)
            {
                var cleanup = await PruneMaintenanceDataAsync(connection, startedAt, policy, cancellationToken);
                prunedRevisions = cleanup.DeletedRevisions;
                prunedChunks = cleanup.DeletedChunks;
                prunedVectors = cleanup.DeletedVectors;
                deletedRevisions += prunedRevisions;
                deletedChunks += prunedChunks;
                deletedVectors += prunedVectors;
                affectedProjectSet.UnionWith(cleanup.AffectedProjectIds);
                affectedProjectIds = affectedProjectSet.Order(StringComparer.OrdinalIgnoreCase).ToArray();

                if (deletedItems > 0 || prunedChunks > 0)
                {
                    await BumpCacheVersionsAsync(affectedProjectIds, cancellationToken);
                }
            }

            var completedAt = timeProvider.GetUtcNow();
            var completedJson = BuildResultJson(
                startedAt,
                completedAt,
                cutoff,
                deletedItems,
                deletedLinks,
                deletedRevisions,
                deletedChunks,
                deletedVectors,
                affectedProjectIds,
                mode,
                classification,
                request.PreviewOnly,
                policy,
                false,
                stoppedReason,
                prunedRevisions,
                prunedChunks,
                prunedVectors,
                completed: true);
            await UpdateRunAsync(run.Id, MaintenanceRunStatus.Completed, completedAt, completedJson, string.Empty, cancellationToken);
            return BuildRunResult(run.Id, cutoff, deletedItems, deletedLinks, deletedRevisions, deletedChunks, deletedVectors, affectedProjectIds, request.PreviewOnly, mode, classification, startedAt, completedAt, completedJson);
        }
        catch (Exception ex)
        {
            var completedAt = timeProvider.GetUtcNow();
            logger.LogError(ex, "Memory data retention run {MaintenanceRunId} failed.", run.Id);
            var failedJson = BuildResultJson(
                startedAt,
                completedAt,
                cutoff,
                deletedItems,
                deletedLinks,
                deletedRevisions,
                deletedChunks,
                deletedVectors,
                affectedProjectIds,
                mode,
                classification,
                request.PreviewOnly,
                policy,
                false,
                stoppedReason,
                prunedRevisions,
                prunedChunks,
                prunedVectors,
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

    private async Task<MemoryDataRetentionBatchResult> DeleteBatchAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoffUtc,
        DateOnly hitWindowStart,
        MemoryDataRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH recent_hits AS (
                SELECT memory_id, COALESCE(SUM(hit_count), 0)::bigint AS recent_hit_count
                FROM retrieval_telemetry_daily_hit_summaries
                WHERE summary_date >= @hit_window_start
                GROUP BY memory_id
            ),
            link_degrees AS (
                SELECT memory_id, COUNT(*)::int AS link_degree
                FROM (
                    SELECT from_id AS memory_id FROM memory_links
                    UNION ALL
                    SELECT to_id AS memory_id FROM memory_links
                ) links
                GROUP BY memory_id
            ),
            target AS (
                SELECT mi.id
                FROM memory_items mi
                LEFT JOIN recent_hits rh ON rh.memory_id = mi.id
                LEFT JOIN link_degrees ld ON ld.memory_id = mi.id
                WHERE mi.status = @status
                  AND mi.updated_at < @cutoff
                  AND mi.importance <= @max_importance
                  AND mi.confidence <= @max_confidence
                  AND COALESCE(rh.recent_hit_count, 0) <= @max_recent_hit_count
                  AND COALESCE(ld.link_degree, 0) <= @max_link_degree
                  AND mi.memory_type NOT IN ('Decision', 'Preference')
                  AND NOT mi.is_read_only
                  AND NOT EXISTS (
                      SELECT 1
                      FROM unnest(mi.tags) tag
                      WHERE lower(tag) IN ('keep', 'pinned')
                  )
                ORDER BY mi.updated_at ASC, mi.id ASC
                LIMIT @batch_size
            ),
            deleted_links AS (
                DELETE FROM memory_links ml
                USING target
                WHERE ml.from_id = target.id
                   OR ml.to_id = target.id
                RETURNING 1
            ),
            deleted_revisions AS (
                DELETE FROM memory_item_revisions mir
                USING target
                WHERE mir.memory_item_id = target.id
                RETURNING 1
            ),
            target_chunks AS (
                SELECT mic.id
                FROM memory_item_chunks mic
                JOIN target ON target.id = mic.memory_item_id
            ),
            deleted_vectors AS (
                DELETE FROM memory_chunk_vectors mcv
                USING target_chunks
                WHERE mcv.chunk_id = target_chunks.id
                RETURNING 1
            ),
            deleted_chunks AS (
                DELETE FROM memory_item_chunks mic
                USING target
                WHERE mic.memory_item_id = target.id
                RETURNING 1
            ),
            deleted_items AS (
                DELETE FROM memory_items mi
                USING target
                WHERE mi.id = target.id
                RETURNING 1
            )
            SELECT
                (SELECT COUNT(*)::bigint FROM deleted_items) AS deleted_items,
                (SELECT COUNT(*)::bigint FROM deleted_links) AS deleted_links,
                (SELECT COUNT(*)::bigint FROM deleted_revisions) AS deleted_revisions,
                (SELECT COUNT(*)::bigint FROM deleted_chunks) AS deleted_chunks,
                (SELECT COUNT(*)::bigint FROM deleted_vectors) AS deleted_vectors;
            """;
        command.Parameters.Add(new NpgsqlParameter<string>("status", MemoryStatusArchived));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoffUtc));
        AddPolicyParameters(command, policy, hitWindowStart);
        command.Parameters.Add(new NpgsqlParameter<int>("batch_size", policy.BatchSize));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new MemoryDataRetentionBatchResult(0, 0, 0, 0, 0);
        }

        return new MemoryDataRetentionBatchResult(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private async Task<MemoryDataRetentionBatchResult> PreviewAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoffUtc,
        DateOnly hitWindowStart,
        MemoryDataRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH recent_hits AS (
                SELECT memory_id, COALESCE(SUM(hit_count), 0)::bigint AS recent_hit_count
                FROM retrieval_telemetry_daily_hit_summaries
                WHERE summary_date >= @hit_window_start
                GROUP BY memory_id
            ),
            link_degrees AS (
                SELECT memory_id, COUNT(*)::int AS link_degree
                FROM (
                    SELECT from_id AS memory_id FROM memory_links
                    UNION ALL
                    SELECT to_id AS memory_id FROM memory_links
                ) links
                GROUP BY memory_id
            ),
            target AS (
                SELECT mi.id
                FROM memory_items mi
                LEFT JOIN recent_hits rh ON rh.memory_id = mi.id
                LEFT JOIN link_degrees ld ON ld.memory_id = mi.id
                WHERE mi.status = @status
                  AND mi.updated_at < @cutoff
                  AND mi.importance <= @max_importance
                  AND mi.confidence <= @max_confidence
                  AND COALESCE(rh.recent_hit_count, 0) <= @max_recent_hit_count
                  AND COALESCE(ld.link_degree, 0) <= @max_link_degree
                  AND mi.memory_type NOT IN ('Decision', 'Preference')
                  AND NOT mi.is_read_only
                  AND NOT EXISTS (
                      SELECT 1
                      FROM unnest(mi.tags) tag
                      WHERE lower(tag) IN ('keep', 'pinned')
                  )
            ),
            target_chunks AS (
                SELECT mic.id
                FROM memory_item_chunks mic
                JOIN target ON target.id = mic.memory_item_id
            )
            SELECT
                (SELECT COUNT(*)::bigint FROM target) AS deleted_items,
                (SELECT COUNT(*)::bigint FROM memory_links ml JOIN target ON ml.from_id = target.id OR ml.to_id = target.id) AS deleted_links,
                (SELECT COUNT(*)::bigint FROM memory_item_revisions mir JOIN target ON mir.memory_item_id = target.id) AS deleted_revisions,
                (SELECT COUNT(*)::bigint FROM target_chunks) AS deleted_chunks,
                (SELECT COUNT(*)::bigint FROM memory_chunk_vectors mcv JOIN target_chunks ON target_chunks.id = mcv.chunk_id) AS deleted_vectors;
            """;
        command.Parameters.Add(new NpgsqlParameter<string>("status", MemoryStatusArchived));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoffUtc));
        AddPolicyParameters(command, policy, hitWindowStart);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new MemoryDataRetentionBatchResult(0, 0, 0, 0, 0);
        }

        return new MemoryDataRetentionBatchResult(
            reader.GetInt64(0),
            reader.GetInt64(1),
            reader.GetInt64(2),
            reader.GetInt64(3),
            reader.GetInt64(4));
    }

    private async Task<MemoryDataRetentionCleanupResult> PruneMaintenanceDataAsync(
        NpgsqlConnection connection,
        DateTimeOffset startedAt,
        MemoryDataRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        var deletedRevisions = 0L;
        var deletedChunks = 0L;
        var deletedVectors = 0L;
        var affectedProjects = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var revisionCutoff = timeProvider.GetUtcNow().AddDays(-policy.RevisionRetentionDays);

        while (true)
        {
            if (ShouldStopForMaxDuration(startedAt, timeProvider, policy, out _))
            {
                break;
            }

            var batch = await PruneRevisionBatchAsync(connection, revisionCutoff, policy, cancellationToken);
            deletedRevisions += batch;
            if (batch == 0)
            {
                break;
            }

            if (policy.DelayBetweenBatchesMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(policy.DelayBetweenBatchesMs), cancellationToken);
            }
        }

        while (true)
        {
            if (ShouldStopForMaxDuration(startedAt, timeProvider, policy, out _))
            {
                break;
            }

            var batch = await PruneChunkOverflowBatchAsync(connection, policy, cancellationToken);
            deletedChunks += batch.DeletedChunks;
            deletedVectors += batch.DeletedVectors;
            affectedProjects.UnionWith(batch.AffectedProjectIds);
            if (batch.DeletedChunks == 0)
            {
                break;
            }

            if (policy.DelayBetweenBatchesMs > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(policy.DelayBetweenBatchesMs), cancellationToken);
            }
        }

        return new MemoryDataRetentionCleanupResult(
            deletedRevisions,
            deletedChunks,
            deletedVectors,
            affectedProjects.Order(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static async Task<long> PruneRevisionBatchAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoffUtc,
        MemoryDataRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH ranked AS (
                SELECT
                    id,
                    ROW_NUMBER() OVER (
                        PARTITION BY memory_item_id
                        ORDER BY created_at DESC, version DESC, id DESC
                    ) AS revision_rank
                FROM memory_item_revisions
                WHERE created_at < @revision_cutoff
            ),
            target AS (
                SELECT id
                FROM ranked
                WHERE revision_rank > @min_revisions_to_keep
                ORDER BY revision_rank DESC, id
                LIMIT @batch_size
            ),
            deleted AS (
                DELETE FROM memory_item_revisions mir
                USING target
                WHERE mir.id = target.id
                RETURNING 1
            )
            SELECT COUNT(*)::bigint FROM deleted;
            """;
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("revision_cutoff", cutoffUtc));
        command.Parameters.Add(new NpgsqlParameter<int>("min_revisions_to_keep", policy.MinRevisionsToKeep));
        command.Parameters.Add(new NpgsqlParameter<int>("batch_size", policy.BatchSize));
        return (long)(await command.ExecuteScalarAsync(cancellationToken) ?? 0L);
    }

    private static async Task<MemoryDataRetentionCleanupResult> PruneChunkOverflowBatchAsync(
        NpgsqlConnection connection,
        MemoryDataRetentionPolicy policy,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH ranked AS (
                SELECT
                    mic.id,
                    mic.memory_item_id,
                    ROW_NUMBER() OVER (
                        PARTITION BY mic.memory_item_id
                        ORDER BY mic.chunk_index ASC, mic.id ASC
                    ) AS chunk_rank
                FROM memory_item_chunks mic
            ),
            target_chunks AS (
                SELECT id, memory_item_id
                FROM ranked
                WHERE chunk_rank > @max_chunks_per_memory_item
                ORDER BY memory_item_id, chunk_rank ASC, id
                LIMIT @batch_size
            ),
            affected_projects AS (
                SELECT DISTINCT mi.project_id
                FROM memory_items mi
                JOIN target_chunks target ON target.memory_item_id = mi.id
            ),
            deleted_vectors AS (
                DELETE FROM memory_chunk_vectors mcv
                USING target_chunks target
                WHERE mcv.chunk_id = target.id
                RETURNING 1
            ),
            deleted_chunks AS (
                DELETE FROM memory_item_chunks mic
                USING target_chunks target
                WHERE mic.id = target.id
                RETURNING 1
            )
            SELECT
                (SELECT COUNT(*)::bigint FROM deleted_chunks) AS deleted_chunks,
                (SELECT COUNT(*)::bigint FROM deleted_vectors) AS deleted_vectors,
                COALESCE((SELECT array_agg(project_id ORDER BY project_id) FROM affected_projects), ARRAY[]::text[]) AS affected_project_ids;
            """;
        command.Parameters.Add(new NpgsqlParameter<int>("max_chunks_per_memory_item", policy.MaxChunksPerMemoryItem));
        command.Parameters.Add(new NpgsqlParameter<int>("batch_size", policy.BatchSize));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new MemoryDataRetentionCleanupResult(0, 0, 0, []);
        }

        return new MemoryDataRetentionCleanupResult(
            DeletedRevisions: 0,
            DeletedChunks: reader.GetInt64(0),
            DeletedVectors: reader.GetInt64(1),
            AffectedProjectIds: reader.GetFieldValue<string[]>(2).Select(projectId => ProjectContext.Normalize(projectId)).ToArray());
    }

    private async Task<IReadOnlyList<string>> ReadAffectedProjectIdsAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoffUtc,
        DateOnly hitWindowStart,
        MemoryDataRetentionPolicy policy,
        IReadOnlyList<string> projectIds,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = """
            WITH recent_hits AS (
                SELECT memory_id, COALESCE(SUM(hit_count), 0)::bigint AS recent_hit_count
                FROM retrieval_telemetry_daily_hit_summaries
                WHERE summary_date >= @hit_window_start
                GROUP BY memory_id
            ),
            link_degrees AS (
                SELECT memory_id, COUNT(*)::int AS link_degree
                FROM (
                    SELECT from_id AS memory_id FROM memory_links
                    UNION ALL
                    SELECT to_id AS memory_id FROM memory_links
                ) links
                GROUP BY memory_id
            )
            SELECT DISTINCT mi.project_id
            FROM memory_items mi
            LEFT JOIN recent_hits rh ON rh.memory_id = mi.id
            LEFT JOIN link_degrees ld ON ld.memory_id = mi.id
            WHERE mi.status = @status
              AND (cardinality(@project_ids) = 0 OR mi.project_id = ANY(@project_ids))
              AND (@tenant_id IS NULL OR mi.tenant_id = @tenant_id)
              AND mi.updated_at < @cutoff
              AND mi.importance <= @max_importance
              AND mi.confidence <= @max_confidence
              AND COALESCE(rh.recent_hit_count, 0) <= @max_recent_hit_count
              AND COALESCE(ld.link_degree, 0) <= @max_link_degree
              AND mi.memory_type NOT IN ('Decision', 'Preference')
              AND NOT mi.is_read_only
              AND NOT EXISTS (
                  SELECT 1
                  FROM unnest(mi.tags) tag
                  WHERE lower(tag) IN ('keep', 'pinned')
              )
            ORDER BY project_id ASC;
            """;
        command.Parameters.Add(new NpgsqlParameter<string>("status", MemoryStatusArchived));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoffUtc));
        AddPolicyParameters(command, policy, hitWindowStart);
        command.Parameters.Add(new NpgsqlParameter<string[]>("project_ids", projectIds.ToArray()));
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId ?? (object)DBNull.Value });

        var affectedProjectIds = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            affectedProjectIds.Add(ProjectContext.Normalize(reader.GetString(0)));
        }

        return affectedProjectIds;
    }

    private async Task<MemoryDataRetentionClassification> ClassifyAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoffUtc,
        DateOnly hitWindowStart,
        MemoryDataRetentionPolicy policy,
        bool includeCandidateDetails,
        IReadOnlyList<string> projectIds,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        long autoDeleteCount;
        long reviewCount;

        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandTimeout = policy.CommandTimeoutSeconds;
            countCommand.CommandText = ClassificationCte + """

                SELECT
                    COUNT(*) FILTER (WHERE is_auto_delete)::bigint AS auto_delete_count,
                    COUNT(*) FILTER (WHERE NOT is_auto_delete)::bigint AS review_count
                FROM classified;
                """;
            AddClassificationParameters(countCommand, cutoffUtc, hitWindowStart, policy, projectIds, tenantId);

            await using var reader = await countCommand.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return MemoryDataRetentionClassification.Empty(policy);
            }

            autoDeleteCount = reader.GetInt64(0);
            reviewCount = reader.GetInt64(1);
        }

        var autoDeleteCandidates = includeCandidateDetails
            ? await ReadCandidatesAsync(connection, cutoffUtc, hitWindowStart, policy, true, projectIds, tenantId, cancellationToken)
            : [];
        var reviewCandidates = includeCandidateDetails
            ? await ReadCandidatesAsync(connection, cutoffUtc, hitWindowStart, policy, false, projectIds, tenantId, cancellationToken)
            : [];

        var reasonCodes = autoDeleteCandidates
            .Concat(reviewCandidates)
            .SelectMany(x => x.ReasonCodes)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var blockedReasons = reviewCandidates
            .SelectMany(x => x.BlockedReasons)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MemoryDataRetentionClassification(
            policy.ToThresholds(),
            autoDeleteCount,
            reviewCount,
            autoDeleteCandidates,
            reviewCandidates,
            reasonCodes,
            blockedReasons);
    }

    private async Task<IReadOnlyList<MemoryDataRetentionCandidateResult>> ReadCandidatesAsync(
        NpgsqlConnection connection,
        DateTimeOffset cutoffUtc,
        DateOnly hitWindowStart,
        MemoryDataRetentionPolicy policy,
        bool autoDelete,
        IReadOnlyList<string> projectIds,
        Guid? tenantId,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = policy.CommandTimeoutSeconds;
        command.CommandText = ClassificationCte + """

            SELECT
                id,
                project_id,
                title,
                memory_type,
                status,
                importance,
                confidence,
                updated_at,
                recent_hit_count,
                link_degree,
                is_auto_delete,
                source_managed_missing,
                superseded_or_replaced,
                protected_type,
                is_read_only,
                protected_tag,
                stale_active,
                low_signal_episode
            FROM classified
            WHERE is_auto_delete = @is_auto_delete
            ORDER BY updated_at ASC, id ASC
            LIMIT @preview_limit
            OFFSET @preview_offset;
            """;
        AddClassificationParameters(command, cutoffUtc, hitWindowStart, policy, projectIds, tenantId);
        command.Parameters.Add(new NpgsqlParameter<bool>("is_auto_delete", autoDelete));
        command.Parameters.Add(new NpgsqlParameter<int>("preview_limit", policy.PreviewLimit));
        command.Parameters.Add(new NpgsqlParameter<int>("preview_offset", policy.PreviewOffset));

        var candidates = new List<MemoryDataRetentionCandidateResult>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var row = new MemoryDataRetentionCandidateRow(
                reader.GetGuid(0),
                ProjectContext.Normalize(reader.GetString(1)),
                reader.GetString(2),
                ParseEnum<MemoryType>(reader.GetString(3), MemoryType.Episode),
                ParseEnum<MemoryStatus>(reader.GetString(4), MemoryStatus.Active),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetFieldValue<DateTimeOffset>(7),
                reader.GetInt64(8),
                reader.GetInt32(9),
                reader.GetBoolean(10),
                reader.GetBoolean(11),
                reader.GetBoolean(12),
                reader.GetBoolean(13),
                reader.GetBoolean(14),
                reader.GetBoolean(15),
                reader.GetBoolean(16),
                reader.GetBoolean(17));
            candidates.Add(MapCandidate(row, policy));
        }

        return candidates;
    }

    private async Task BumpCacheVersionsAsync(IReadOnlyList<string> affectedProjectIds, CancellationToken cancellationToken)
    {
        await cacheStore.IncrementAsync(cancellationToken);
        foreach (var projectId in affectedProjectIds)
        {
            await cacheStore.IncrementProjectAsync(projectId, cancellationToken);
        }
    }

    private static string BuildResultJson(
        DateTimeOffset startedAtUtc,
        DateTimeOffset? completedAtUtc,
        DateTimeOffset cutoffUtc,
        long deletedItems,
        long deletedLinks,
        long deletedRevisions,
        long deletedChunks,
        long deletedVectors,
        IReadOnlyList<string> affectedProjectIds,
        MemoryDataRetentionRunMode mode,
        MemoryDataRetentionClassification classification,
        bool previewOnly,
        MemoryDataRetentionPolicy policy,
        bool skipped,
        string? stoppedReason,
        long prunedRevisions = 0,
        long prunedChunks = 0,
        long prunedVectors = 0,
        bool completed = true,
        string error = "")
        => JsonSerializer.Serialize(new
        {
            mode,
            archivedItemsRetentionDays = policy.ArchivedItemsRetentionDays,
            hitWindowDays = policy.HitWindowDays,
            maxRecentHitCount = policy.MaxRecentHitCount,
            maxLinkDegree = policy.MaxLinkDegree,
            maxImportance = policy.MaxImportance,
            maxConfidence = policy.MaxConfidence,
            previewLimit = policy.PreviewLimit,
            previewOffset = policy.PreviewOffset,
            revisionRetentionDays = policy.RevisionRetentionDays,
            minRevisionsToKeep = policy.MinRevisionsToKeep,
            maxChunksPerMemoryItem = policy.MaxChunksPerMemoryItem,
            cutoffUtc,
            skipped,
            completed,
            batchSize = policy.BatchSize,
            delayBetweenBatchesMs = policy.DelayBetweenBatchesMs,
            commandTimeoutSeconds = policy.CommandTimeoutSeconds,
            maxDurationMinutes = policy.MaxDuration.TotalMinutes,
            deletedItems,
            deletedLinks,
            deletedRevisions,
            deletedChunks,
            deletedVectors,
            prunedRevisions,
            prunedChunks,
            prunedVectors,
            affectedProjectIds,
            previewOnly,
            autoDeleteCandidateCount = classification.AutoDeleteCandidateCount,
            reviewCandidateCount = classification.ReviewCandidateCount,
            autoDeleteCandidates = classification.AutoDeleteCandidates,
            reviewCandidates = classification.ReviewCandidates,
            reasonCodes = classification.ReasonCodes,
            blockedReasons = classification.BlockedReasons,
            policyThresholds = classification.PolicyThresholds,
            startedAtUtc,
            completedAtUtc,
            durationMs = completedAtUtc.HasValue
                ? (double?)(completedAtUtc.Value - startedAtUtc).TotalMilliseconds
                : null,
            stoppedReason,
            error = string.IsNullOrWhiteSpace(error) ? null : error,
            retentionTables = RetentionTables
        }, SerializerOptions);

    private static MemoryDataRetentionRunMode ResolveMode(MemoryDataRetentionRunRequest request, MemoryDataRetentionPolicy policy)
    {
        if (request.PreviewOnly)
        {
            return MemoryDataRetentionRunMode.PreviewDelete;
        }

        if (request.Mode == MemoryDataRetentionRunMode.ApplyMaintenanceCleanup)
        {
            return MemoryDataRetentionRunMode.ApplyMaintenanceCleanup;
        }

        if (request.Mode == MemoryDataRetentionRunMode.ApplyAutoDelete)
        {
            return MemoryDataRetentionRunMode.ApplyAutoDelete;
        }

        if (request.Mode == MemoryDataRetentionRunMode.PreviewDelete)
        {
            return MemoryDataRetentionRunMode.PreviewDelete;
        }

        return policy.AutoApplyEnabled
            ? MemoryDataRetentionRunMode.ApplyAutoDelete
            : MemoryDataRetentionRunMode.Classify;
    }

    private static void AddPolicyParameters(NpgsqlCommand command, MemoryDataRetentionPolicy policy, DateOnly hitWindowStart)
    {
        command.Parameters.Add(new NpgsqlParameter<DateOnly>("hit_window_start", hitWindowStart));
        command.Parameters.Add(new NpgsqlParameter<long>("max_recent_hit_count", policy.MaxRecentHitCount));
        command.Parameters.Add(new NpgsqlParameter<int>("max_link_degree", policy.MaxLinkDegree));
        command.Parameters.Add(new NpgsqlParameter<decimal>("max_importance", policy.MaxImportance));
        command.Parameters.Add(new NpgsqlParameter<decimal>("max_confidence", policy.MaxConfidence));
    }

    private static void AddClassificationParameters(
        NpgsqlCommand command,
        DateTimeOffset cutoffUtc,
        DateOnly hitWindowStart,
        MemoryDataRetentionPolicy policy,
        IReadOnlyList<string> projectIds,
        Guid? tenantId)
    {
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("cutoff", cutoffUtc));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("stale_cutoff", cutoffUtc.AddDays(policy.ArchivedItemsRetentionDays - 60)));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("episode_cutoff", cutoffUtc.AddDays(policy.ArchivedItemsRetentionDays - 30)));
        AddPolicyParameters(command, policy, hitWindowStart);
        command.Parameters.Add(new NpgsqlParameter<string[]>("project_ids", projectIds.ToArray()));
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Uuid) { Value = tenantId ?? (object)DBNull.Value });
    }

    private static MemoryDataRetentionCandidateResult MapCandidate(MemoryDataRetentionCandidateRow row, MemoryDataRetentionPolicy policy)
    {
        var reasonCodes = new List<string>();
        var blockedReasons = new List<string>();

        if (row.Status == MemoryStatus.Archived)
        {
            reasonCodes.Add("archivedRetentionExpired");
        }

        if (row.Importance <= policy.MaxImportance)
        {
            reasonCodes.Add("lowImportance");
        }
        else
        {
            blockedReasons.Add("highImportance");
        }

        if (row.Confidence <= policy.MaxConfidence)
        {
            reasonCodes.Add("lowConfidence");
        }
        else
        {
            blockedReasons.Add("highConfidence");
        }

        if (row.RecentHitCount <= policy.MaxRecentHitCount)
        {
            reasonCodes.Add("lowRecentHits");
        }
        else
        {
            blockedReasons.Add("recentHits");
        }

        if (row.LinkDegree <= policy.MaxLinkDegree)
        {
            reasonCodes.Add("lowLinkDegree");
        }
        else
        {
            blockedReasons.Add("linkedMemory");
        }

        if (row.SourceManagedMissing)
        {
            reasonCodes.Add("sourceManagedMissing");
        }

        if (row.SupersededOrReplaced)
        {
            reasonCodes.Add("supersededOrReplaced");
        }

        if (row.StaleActive)
        {
            reasonCodes.Add("staleActive");
            blockedReasons.Add("activeNeedsReview");
        }

        if (row.LowSignalEpisode)
        {
            reasonCodes.Add("lowSignalEpisode");
            blockedReasons.Add("activeNeedsReview");
        }

        if (row.ProtectedType)
        {
            blockedReasons.Add("protectedType");
        }

        if (row.IsReadOnly)
        {
            blockedReasons.Add("readOnly");
        }

        if (row.ProtectedTag)
        {
            blockedReasons.Add("protectedTag");
        }

        var action = ResolveRecommendedAction(row);
        return new MemoryDataRetentionCandidateResult(
            row.MemoryId,
            row.ProjectId,
            row.Title,
            row.MemoryType,
            row.Status,
            row.Importance,
            row.Confidence,
            row.UpdatedAtUtc,
            row.RecentHitCount,
            row.LinkDegree,
            action,
            reasonCodes.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blockedReasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    private static MemoryRetentionRecommendedAction ResolveRecommendedAction(MemoryDataRetentionCandidateRow row)
    {
        if (row.IsAutoDelete)
        {
            return MemoryRetentionRecommendedAction.Delete;
        }

        if (row.ProtectedType || row.IsReadOnly || row.ProtectedTag)
        {
            return MemoryRetentionRecommendedAction.Keep;
        }

        if (row.Status == MemoryStatus.Active && row.SupersededOrReplaced)
        {
            return MemoryRetentionRecommendedAction.Merge;
        }

        if (row.Status == MemoryStatus.Active && (row.StaleActive || row.LowSignalEpisode || row.SourceManagedMissing))
        {
            return MemoryRetentionRecommendedAction.Archive;
        }

        return MemoryRetentionRecommendedAction.NeedsReview;
    }

    private static TEnum ParseEnum<TEnum>(string value, TEnum fallback)
        where TEnum : struct, Enum
        => Enum.TryParse<TEnum>(value, true, out var parsed) ? parsed : fallback;

    private static MemoryDataRetentionRunResult BuildRunResult(
        Guid runId,
        DateTimeOffset cutoffUtc,
        long deletedItems,
        long deletedLinks,
        long deletedRevisions,
        long deletedChunks,
        long deletedVectors,
        IReadOnlyList<string> affectedProjectIds,
        bool previewOnly,
        MemoryDataRetentionRunMode mode,
        MemoryDataRetentionClassification classification,
        DateTimeOffset startedAtUtc,
        DateTimeOffset completedAtUtc,
        string resultJson)
        => new(
            runId,
            cutoffUtc,
            deletedItems,
            deletedLinks,
            deletedRevisions,
            deletedChunks,
            deletedVectors,
            affectedProjectIds,
            previewOnly,
            mode,
            classification.PolicyThresholds,
            classification.AutoDeleteCandidateCount,
            classification.ReviewCandidateCount,
            classification.AutoDeleteCandidates,
            classification.ReviewCandidates,
            classification.ReasonCodes,
            classification.BlockedReasons,
            startedAtUtc,
            completedAtUtc,
            resultJson);

    private async Task UpdateRunAsync(
        Guid runId,
        MaintenanceRunStatus status,
        DateTimeOffset? completedAt,
        string resultJson,
        string error,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var run = await dbContext.MaintenanceRuns.FirstAsync(x => x.Id == runId, cancellationToken);
        run.Status = status;
        run.CompletedAt = completedAt;
        run.ResultJson = resultJson;
        run.Error = error;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static bool ShouldStopForMaxDuration(
        DateTimeOffset startedAt,
        TimeProvider timeProvider,
        MemoryDataRetentionPolicy policy,
        out string? stoppedReason)
    {
        if ((timeProvider.GetUtcNow() - startedAt) >= policy.MaxDuration)
        {
            stoppedReason = "maxDurationReached";
            return true;
        }

        stoppedReason = null;
        return false;
    }

    private static string NormalizeTriggeredBy(string? requested, string fallback)
        => requested?.Trim() is { Length: > 0 } value
            ? value
            : string.IsNullOrWhiteSpace(fallback)
                ? "system"
                : fallback.Trim();

    private sealed record MemoryDataRetentionClassification(
        MemoryDataRetentionPolicyThresholds PolicyThresholds,
        long AutoDeleteCandidateCount,
        long ReviewCandidateCount,
        IReadOnlyList<MemoryDataRetentionCandidateResult> AutoDeleteCandidates,
        IReadOnlyList<MemoryDataRetentionCandidateResult> ReviewCandidates,
        IReadOnlyList<string> ReasonCodes,
        IReadOnlyList<string> BlockedReasons)
    {
        public static MemoryDataRetentionClassification Empty(MemoryDataRetentionPolicy policy)
            => new(policy.ToThresholds(), 0, 0, [], [], [], []);
    }

    private sealed record MemoryDataRetentionCandidateRow(
        Guid MemoryId,
        string ProjectId,
        string Title,
        MemoryType MemoryType,
        MemoryStatus Status,
        decimal Importance,
        decimal Confidence,
        DateTimeOffset UpdatedAtUtc,
        long RecentHitCount,
        int LinkDegree,
        bool IsAutoDelete,
        bool SourceManagedMissing,
        bool SupersededOrReplaced,
        bool ProtectedType,
        bool IsReadOnly,
        bool ProtectedTag,
        bool StaleActive,
        bool LowSignalEpisode);

    private sealed record MemoryDataRetentionBatchResult(
        long DeletedItems,
        long DeletedLinks,
        long DeletedRevisions,
        long DeletedChunks,
        long DeletedVectors);

    private sealed record MemoryDataRetentionCleanupResult(
        long DeletedRevisions,
        long DeletedChunks,
        long DeletedVectors,
        IReadOnlyList<string> AffectedProjectIds);
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

public sealed class MemoryDataRetentionHostedService(
    IServiceProvider serviceProvider,
    IOptions<MemoryDataRetentionOptions> options,
    TimeProvider timeProvider,
    ILogger<MemoryDataRetentionHostedService> logger) : BackgroundService
{
    private readonly MemoryDataRetentionOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("Memory data retention hosted service is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            var delay = GetDelayUntilNextRun(timeProvider.GetUtcNow());
            await Task.Delay(delay, stoppingToken);

            try
            {
                using var scope = serviceProvider.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IMemoryDataRetentionService>();
                await service.RunAsync(
                    new MemoryDataRetentionRunRequest(
                        TriggeredBy: "scheduled",
                        Mode: MemoryDataRetentionRunMode.Classify,
                        IncludeCandidateDetails: true),
                    "scheduled",
                    stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Scheduled memory data retention failed.");
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
            : new TimeOnly(4, 0);
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

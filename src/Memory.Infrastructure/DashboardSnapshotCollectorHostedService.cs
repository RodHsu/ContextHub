using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using StackExchange.Redis;
using System.Collections.Concurrent;
using System.Data;
using System.Globalization;
using System.Text.Json;

namespace Memory.Infrastructure;

public sealed class DashboardSnapshotCollectorHostedService(
    IDashboardSnapshotStore snapshotStore,
    ICacheVersionStore cacheVersionStore,
    IRuntimeConfigurationAccessor runtimeConfigurationAccessor,
    IServiceScopeFactory scopeFactory,
    IRequestTrafficSnapshotAccessor requestTrafficSnapshotAccessor,
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    IConnectionMultiplexer redis,
    IRedisCacheTelemetry redisCacheTelemetry,
    HealthCheckService healthCheckService,
    DockerRuntimeMetricsService dockerMetricsService,
    IEmbeddingUsageTelemetry embeddingUsageTelemetry,
    TimeProvider timeProvider,
    ILogger<DashboardSnapshotCollectorHostedService> logger) : BackgroundService
{
    private const int MaxResourceSamples = 15;
    private const int MaxContextSavingsTrendPoints = 24;
    private const int DiscussionActivityTrendHours = 24;
    private const int MaxContextSavingsTelemetryEvents = 50_000;
    private const int ContextSavingsMinimumIntervalSeconds = 60;
    private const int ContextSavingsQueryTimeoutSeconds = 20;
    private const int StorageLargeTablePreviewQueryTimeoutSeconds = 20;
    private static readonly TimeSpan ContextSavingsMaxWindow = TimeSpan.FromDays(30);
    private static readonly TimeSpan StartupDependencyFailureGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailureLogThrottle = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan TimeoutCooldown = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan TimeoutSummaryLogThrottle = TimeSpan.FromMinutes(15);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, SnapshotFailureState> _failureStates = new(StringComparer.Ordinal);
    private readonly DateTimeOffset _startedAtUtc = timeProvider.GetUtcNow();
    private readonly SemaphoreSlim _resourceLock = new(1, 1);
    private readonly List<DashboardResourceSampleResult> _resourceSamples = [];
    private DockerRuntimeSnapshot? _previousDockerSnapshot;

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        await CollectInitialSnapshotsAsync(cancellationToken);
        await base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var tasks = new[]
        {
            RunLoopAsync(DashboardSnapshotKeys.StatusCore, behavior => behavior.StatusCoreSeconds, CollectStatusCoreAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.EmbeddingRuntime, behavior => behavior.EmbeddingRuntimeSeconds, CollectEmbeddingRuntimeAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.DependenciesHealth, behavior => behavior.DependenciesHealthSeconds, CollectDependenciesHealthAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.DockerHost, behavior => behavior.DockerHostSeconds, CollectDockerHostAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.DependencyResources, behavior => behavior.DependencyResourcesSeconds, CollectDependencyResourcesAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.MonitoringStats, behavior => behavior.DependencyResourcesSeconds, CollectMonitoringStatsAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.RecentOperations, behavior => behavior.RecentOperationsSeconds, CollectRecentOperationsAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.DashboardJobs, behavior => behavior.RecentOperationsSeconds, CollectDashboardJobsAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.DashboardLogs, behavior => behavior.RecentOperationsSeconds, CollectDashboardLogsAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.DashboardProjectSuggestions, behavior => behavior.RecentOperationsSeconds, CollectDashboardProjectSuggestionsAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.StorageTableStats, behavior => behavior.RecentOperationsSeconds, CollectStorageTableStatsAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.StorageLargeTablePreview, behavior => behavior.RecentOperationsSeconds, CollectStorageLargeTablePreviewAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.ResourceChart, behavior => behavior.ResourceChartSeconds, CollectResourceChartAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.EvaluationSummary, behavior => behavior.RecentOperationsSeconds, CollectEvaluationSummaryAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.ContextSavings, behavior => Math.Max(ContextSavingsMinimumIntervalSeconds, behavior.RecentOperationsSeconds), CollectContextSavingsAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.DiscussionActivity, behavior => behavior.RecentOperationsSeconds, CollectDiscussionActivityAsync, stoppingToken),
            RunLoopAsync(DashboardSnapshotKeys.MemoryGraphIndex, behavior => behavior.MemoryGraphIndexSeconds, CollectMemoryGraphIndexAsync, stoppingToken)
        };

        await Task.WhenAll(tasks);
    }

    private async Task CollectInitialSnapshotsAsync(CancellationToken cancellationToken)
    {
        var settings = await GetPollingSettingsAsync(cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.StatusCore, settings.StatusCoreSeconds, CollectStatusCoreAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.EmbeddingRuntime, settings.EmbeddingRuntimeSeconds, CollectEmbeddingRuntimeAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.DependenciesHealth, settings.DependenciesHealthSeconds, CollectDependenciesHealthAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.DockerHost, settings.DockerHostSeconds, CollectDockerHostAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.DependencyResources, settings.DependencyResourcesSeconds, CollectDependencyResourcesAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.MonitoringStats, settings.DependencyResourcesSeconds, CollectMonitoringStatsAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.RecentOperations, settings.RecentOperationsSeconds, CollectRecentOperationsAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.DashboardJobs, settings.RecentOperationsSeconds, CollectDashboardJobsAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.DashboardLogs, settings.RecentOperationsSeconds, CollectDashboardLogsAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.DashboardProjectSuggestions, settings.RecentOperationsSeconds, CollectDashboardProjectSuggestionsAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.StorageTableStats, settings.RecentOperationsSeconds, CollectStorageTableStatsAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.StorageLargeTablePreview, settings.RecentOperationsSeconds, CollectStorageLargeTablePreviewAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.ResourceChart, settings.ResourceChartSeconds, CollectResourceChartAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.EvaluationSummary, settings.RecentOperationsSeconds, CollectEvaluationSummaryAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.ContextSavings, Math.Max(ContextSavingsMinimumIntervalSeconds, settings.RecentOperationsSeconds), CollectContextSavingsAsync, cancellationToken);
        await CollectWithErrorHandlingAsync(DashboardSnapshotKeys.DiscussionActivity, settings.RecentOperationsSeconds, CollectDiscussionActivityAsync, cancellationToken);
    }

    private async Task RunLoopAsync(
        string key,
        Func<DashboardSnapshotPollingSettingsResult, int> intervalSelector,
        Func<int, CancellationToken, Task> collectAsync,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var settings = await GetPollingSettingsAsync(cancellationToken);
            var intervalSeconds = Math.Max(1, intervalSelector(settings));
            await CollectWithErrorHandlingAsync(key, intervalSeconds, collectAsync, cancellationToken);

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task CollectWithErrorHandlingAsync(
        string key,
        int intervalSeconds,
        Func<int, CancellationToken, Task> collectAsync,
        CancellationToken cancellationToken)
    {
        if (TryGetActiveCooldown(key, out var cooldownUntilUtc))
        {
            logger.LogDebug(
                "Dashboard snapshot collector skipped {SnapshotKey} during timeout cooldown until {CooldownUntilUtc}.",
                key,
                cooldownUntilUtc);
            return;
        }

        try
        {
            await collectAsync(intervalSeconds, cancellationToken);
            LogRecoveryIfNeeded(key);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCollectorFailure(key, ex);
            await TryUpdateLastErrorAsync<object?>(key, intervalSeconds, ex.Message, cancellationToken);
        }
    }

    private void LogCollectorFailure(string key, Exception exception)
    {
        var now = timeProvider.GetUtcNow();
        var isTimeoutProtected = IsTimeoutProtectedSnapshot(key) && IsDatabaseReadTimeout(exception);
        var state = _failureStates.AddOrUpdate(
            key,
            _ => new SnapshotFailureState(
                1,
                now,
                now,
                null,
                null,
                isTimeoutProtected ? "databaseReadTimeout" : "unclassified"),
            (_, current) => current with
            {
                Count = current.Count + 1,
                LastFailureLoggedAtUtc = ShouldLogFailure(current.LastFailureLoggedAtUtc, now)
                    ? now
                    : current.LastFailureLoggedAtUtc,
                CooldownUntilUtc = isTimeoutProtected && current.Count + 1 >= 3
                    ? Max(current.CooldownUntilUtc, now.Add(TimeoutCooldown))
                    : current.CooldownUntilUtc,
                FailureKind = isTimeoutProtected ? "databaseReadTimeout" : current.FailureKind
            });

        if (isTimeoutProtected && state.Count >= 3)
        {
            if (ShouldLogTimeoutSummary(state.LastSummaryLoggedAtUtc, now))
            {
                MarkTimeoutSummaryLogged(key, now);
                logger.LogWarning(
                    exception,
                    "Dashboard snapshot collector database timeout cooldown active for {SnapshotKey}. ConsecutiveFailures={FailureCount}; LastExceptionType={ExceptionType}; CooldownUntilUtc={CooldownUntilUtc}",
                    key,
                    state.Count,
                    exception.GetType().Name,
                    state.CooldownUntilUtc);
                return;
            }

            logger.LogDebug(
                exception,
                "Dashboard snapshot collector database timeout suppressed for {SnapshotKey}. ConsecutiveFailures={FailureCount}; CooldownUntilUtc={CooldownUntilUtc}",
                key,
                state.Count,
                state.CooldownUntilUtc);
            return;
        }

        var firstFailure = state.Count == 1;
        var throttledRepeat = !firstFailure && state.LastFailureLoggedAtUtc != now;
        if (throttledRepeat)
        {
            logger.LogDebug(
                exception,
                "Dashboard snapshot collector repeated failure suppressed for {SnapshotKey}. ConsecutiveFailures={FailureCount}",
                key,
                state.Count);
            return;
        }

        if (IsWithinStartupGrace(now) && IsTransientDependencyFailure(exception))
        {
            logger.LogWarning(
                exception,
                "Dashboard snapshot collector transient dependency failure during startup for {SnapshotKey}. ConsecutiveFailures={FailureCount}",
                key,
                state.Count);
            return;
        }

        logger.LogError(
            exception,
            "Dashboard snapshot collector failed for {SnapshotKey}. ConsecutiveFailures={FailureCount}",
            key,
            state.Count);
    }

    private void LogRecoveryIfNeeded(string key)
    {
        if (!_failureStates.TryRemove(key, out var state))
        {
            return;
        }

        logger.LogInformation(
            "Dashboard snapshot collector recovered for {SnapshotKey} after {FailureCount} consecutive failures.",
            key,
            state.Count);
    }

    private bool IsWithinStartupGrace(DateTimeOffset now)
        => now - _startedAtUtc <= StartupDependencyFailureGrace;

    private static bool ShouldLogFailure(DateTimeOffset lastLoggedAtUtc, DateTimeOffset now)
        => now - lastLoggedAtUtc >= FailureLogThrottle;

    private bool TryGetActiveCooldown(string key, out DateTimeOffset cooldownUntilUtc)
    {
        cooldownUntilUtc = default;
        if (!_failureStates.TryGetValue(key, out var state) || state.CooldownUntilUtc is not { } cooldown)
        {
            return false;
        }

        var now = timeProvider.GetUtcNow();
        if (cooldown <= now)
        {
            return false;
        }

        cooldownUntilUtc = cooldown;
        return true;
    }

    private static bool ShouldLogTimeoutSummary(DateTimeOffset? lastSummaryLoggedAtUtc, DateTimeOffset now)
        => !lastSummaryLoggedAtUtc.HasValue || now - lastSummaryLoggedAtUtc.Value >= TimeoutSummaryLogThrottle;

    private void MarkTimeoutSummaryLogged(string key, DateTimeOffset now)
    {
        _failureStates.AddOrUpdate(
            key,
            _ => new SnapshotFailureState(1, now, now, now.Add(TimeoutCooldown), now, "databaseReadTimeout"),
            (_, current) => current with { LastSummaryLoggedAtUtc = now });
    }

    private static DateTimeOffset? Max(DateTimeOffset? left, DateTimeOffset right)
        => left.HasValue && left.Value >= right ? left : right;

    private static bool IsTimeoutProtectedSnapshot(string key)
        => string.Equals(key, DashboardSnapshotKeys.ContextSavings, StringComparison.Ordinal) ||
           string.Equals(key, DashboardSnapshotKeys.StorageLargeTablePreview, StringComparison.Ordinal);

    private static bool IsDatabaseReadTimeout(Exception exception)
    {
        var message = exception.ToString();
        return exception is OperationCanceledException ||
               ContainsException<TimeoutException>(exception) ||
               ContainsException<NpgsqlException>(exception) &&
               (message.Contains("Timeout during reading attempt", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("Exception while reading from stream", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("reading", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsTransientDependencyFailure(Exception exception)
        => ContainsException<NpgsqlException>(exception) ||
           ContainsException<RedisException>(exception) ||
           ContainsException<TimeoutException>(exception) ||
           exception.Message.Contains("transient failure", StringComparison.OrdinalIgnoreCase);

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

    private async Task<DashboardSnapshotPollingSettingsResult> GetPollingSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var behaviorSettingsAccessor = scope.ServiceProvider.GetRequiredService<IInstanceBehaviorSettingsAccessor>();
            var behavior = await behaviorSettingsAccessor.GetCurrentAsync(cancellationToken);
            return behavior.SnapshotPolling;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return DashboardSnapshotPollingDefaults.Create();
        }
    }

    private static int GetDefaultIntervalSeconds(string key)
    {
        var defaults = DashboardSnapshotPollingDefaults.Create();
        return key switch
        {
            DashboardSnapshotKeys.StatusCore => defaults.StatusCoreSeconds,
            DashboardSnapshotKeys.EmbeddingRuntime => defaults.EmbeddingRuntimeSeconds,
            DashboardSnapshotKeys.DependenciesHealth => defaults.DependenciesHealthSeconds,
            DashboardSnapshotKeys.DockerHost => defaults.DockerHostSeconds,
            DashboardSnapshotKeys.DependencyResources => defaults.DependencyResourcesSeconds,
            DashboardSnapshotKeys.MonitoringStats => defaults.DependencyResourcesSeconds,
            DashboardSnapshotKeys.RecentOperations => defaults.RecentOperationsSeconds,
            DashboardSnapshotKeys.DashboardJobs => defaults.RecentOperationsSeconds,
            DashboardSnapshotKeys.DashboardLogs => defaults.RecentOperationsSeconds,
            DashboardSnapshotKeys.DashboardProjectSuggestions => defaults.RecentOperationsSeconds,
            DashboardSnapshotKeys.StorageTableStats => defaults.RecentOperationsSeconds,
            DashboardSnapshotKeys.StorageLargeTablePreview => defaults.RecentOperationsSeconds,
            DashboardSnapshotKeys.ResourceChart => defaults.ResourceChartSeconds,
            DashboardSnapshotKeys.EvaluationSummary => defaults.RecentOperationsSeconds,
            DashboardSnapshotKeys.ContextSavings => ContextSavingsMinimumIntervalSeconds,
            DashboardSnapshotKeys.MemoryGraphIndex => defaults.MemoryGraphIndexSeconds,
            _ => defaults.StatusCoreSeconds
        };
    }

    private async Task CollectStatusCoreAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var runtime = runtimeConfigurationAccessor.Current;
        var payload = new DashboardStatusCoreSnapshotPayload(
            "mcp-server",
            runtime.Namespace,
            BuildMetadata.Current.Version,
            BuildMetadata.Current.TimestampUtc,
            runtime.EmbeddingProvider,
            runtime.ExecutionProvider,
            runtime.EmbeddingProfile,
            runtime.ModelKey,
            runtime.Dimensions,
            runtime.MaxTokens,
            runtime.InferenceThreads,
            runtime.BatchSize,
            runtime.BatchingEnabled,
            await cacheVersionStore.GetVersionAsync(cancellationToken));

        await WriteSnapshotAsync(DashboardSnapshotKeys.StatusCore, intervalSeconds, payload, cancellationToken);
    }

    private async Task CollectEmbeddingRuntimeAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var runtime = runtimeConfigurationAccessor.Current;
        var payload = new DashboardEmbeddingRuntimeSnapshotPayload(
            runtime.Namespace,
            BuildMetadata.Current.Version,
            BuildMetadata.Current.TimestampUtc,
            runtime.EmbeddingProvider,
            runtime.ExecutionProvider,
            runtime.EmbeddingProfile,
            runtime.ModelKey,
            runtime.Dimensions,
            runtime.MaxTokens,
            runtime.InferenceThreads,
            runtime.BatchSize,
            runtime.BatchingEnabled);

        await WriteSnapshotAsync(DashboardSnapshotKeys.EmbeddingRuntime, intervalSeconds, payload, cancellationToken);
    }

    private async Task CollectDependenciesHealthAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var report = await healthCheckService.CheckHealthAsync(registration => registration.Tags.Contains("ready"), cancellationToken);
        var payload = new DashboardDependenciesHealthSnapshotPayload(
            report.Entries
                .OrderBy(x => x.Key)
                .Select(x => new DashboardServiceHealthResult(
                    x.Key,
                    x.Value.Status.ToString(),
                    string.IsNullOrWhiteSpace(x.Value.Description)
                        ? (x.Value.Exception?.Message ?? string.Empty)
                        : x.Value.Description))
                .ToArray());

        await WriteSnapshotAsync(DashboardSnapshotKeys.DependenciesHealth, intervalSeconds, payload, cancellationToken);
    }

    private async Task CollectDockerHostAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var snapshot = await dockerMetricsService.GetSnapshotAsync(cancellationToken);
        var payload = new DashboardDockerHostResult(snapshot.Status, snapshot.Error, snapshot.Host);
        await WriteSnapshotAsync(DashboardSnapshotKeys.DockerHost, intervalSeconds, payload, cancellationToken);
    }

    private async Task CollectDependencyResourcesAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var snapshot = await dockerMetricsService.GetSnapshotAsync(cancellationToken);
        var payload = new DashboardDependencyResourcesResult(
            snapshot.Status,
            snapshot.Error,
            snapshot.Containers.Select(static x => x.Metric).ToArray(),
            snapshot.Volumes);
        await WriteSnapshotAsync(DashboardSnapshotKeys.DependencyResources, intervalSeconds, payload, cancellationToken);
    }

    private async Task CollectMonitoringStatsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var dockerSnapshot = await dockerMetricsService.GetSnapshotAsync(cancellationToken);
        var redisTelemetry = await CollectRedisTelemetryAsync(dockerSnapshot, cancellationToken);
        var postgresTelemetry = await CollectPostgresTelemetryAsync(dockerSnapshot, cancellationToken);
        var embeddingUsage = await embeddingUsageTelemetry.GetWindowsAsync(timeProvider.GetUtcNow(), cancellationToken);

        await WriteSnapshotAsync(
            DashboardSnapshotKeys.MonitoringStats,
            intervalSeconds,
            new DashboardMonitoringSnapshotPayload(redisTelemetry, postgresTelemetry, embeddingUsage),
            cancellationToken);
    }

    private async Task CollectRecentOperationsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var recentErrorCutoff = timeProvider.GetUtcNow().AddHours(-24);

        var memoryItemCount = await dbContext.MemoryItems.CountAsync(cancellationToken);
        var ownedMemoryItemCount = await dbContext.MemoryItems.CountAsync(
            x => x.TenantId != null && x.OwnerUserId != null,
            cancellationToken);
        var legacyOwnerlessMemoryItemCount = memoryItemCount - ownedMemoryItemCount;
        var defaultProjectMemoryItemCount = await dbContext.MemoryItems.CountAsync(
            x => x.ProjectId == ProjectContext.DefaultProjectId,
            cancellationToken);
        var sharedProjectMemoryItemCount = await dbContext.MemoryItems.CountAsync(
            x => x.ProjectId == ProjectContext.SharedProjectId,
            cancellationToken);
        var userProjectMemoryItemCount = await dbContext.MemoryItems.CountAsync(
            x => x.ProjectId == ProjectContext.UserProjectId,
            cancellationToken);
        var regularProjectMemoryItemCount = memoryItemCount - defaultProjectMemoryItemCount -
                                            sharedProjectMemoryItemCount - userProjectMemoryItemCount;
        var preferenceCount = await dbContext.MemoryItems.CountAsync(
            x => x.Scope == MemoryScope.User && x.MemoryType == MemoryType.Preference && x.Status == MemoryStatus.Active,
            cancellationToken);
        var systemProjectInformationCount = await dbContext.MemoryItems.CountAsync(
            x => x.ExternalKey == DurableMemoryGovernancePolicy.ProjectInformationExternalKey,
            cancellationToken);
        var artifactExchangeCount = await dbContext.MemoryItems.CountAsync(
            x => x.SourceType == "project-artifact-exchange",
            cancellationToken);
        var scopeCounts = (await dbContext.MemoryItems.GroupBy(x => x.Scope)
                .Select(x => new { Key = x.Key, Count = x.LongCount() }).ToListAsync(cancellationToken))
            .ToDictionary(x => x.Key.ToString(), x => x.Count, StringComparer.Ordinal);
        var memoryTypeCounts = (await dbContext.MemoryItems.GroupBy(x => x.MemoryType)
                .Select(x => new { Key = x.Key, Count = x.LongCount() }).ToListAsync(cancellationToken))
            .ToDictionary(x => x.Key.ToString(), x => x.Count, StringComparer.Ordinal);
        var statusCounts = (await dbContext.MemoryItems.GroupBy(x => x.Status)
                .Select(x => new { Key = x.Key, Count = x.LongCount() }).ToListAsync(cancellationToken))
            .ToDictionary(x => x.Key.ToString(), x => x.Count, StringComparer.Ordinal);
        var memoryInventory = new DashboardMemoryInventoryCompositionResult(
            "memoryItemRows",
            "InstanceInventory",
            memoryItemCount,
            ownedMemoryItemCount,
            legacyOwnerlessMemoryItemCount,
            defaultProjectMemoryItemCount,
            sharedProjectMemoryItemCount,
            userProjectMemoryItemCount,
            regularProjectMemoryItemCount,
            scopeCounts,
            memoryTypeCounts,
            statusCounts,
            systemProjectInformationCount,
            artifactExchangeCount,
            await dbContext.ResourceTombstones.LongCountAsync(cancellationToken),
            await dbContext.MemoryItemRevisions.LongCountAsync(cancellationToken),
            await dbContext.MemoryItemChunks.LongCountAsync(cancellationToken),
            await dbContext.MemoryChunkVectors.LongCountAsync(cancellationToken),
            await dbContext.ConversationInsights.LongCountAsync(cancellationToken));
        var activeJobCount = await dbContext.MemoryJobs.CountAsync(
            x => x.Status == MemoryJobStatus.Pending || x.Status == MemoryJobStatus.Running,
            cancellationToken);
        var errorLogCount = await dbContext.RuntimeLogEntries.CountAsync(
            x => x.CreatedAt >= recentErrorCutoff && (x.Level == "Error" || x.Level == "Critical"),
            cancellationToken);

        var activeJobs = await dbContext.MemoryJobs
            .Where(x => x.Status == MemoryJobStatus.Pending || x.Status == MemoryJobStatus.Running)
            .OrderBy(x => x.CreatedAt)
            .Take(10)
            .Select(x => new JobListItemResult(
                x.Id,
                x.JobType,
                x.Status,
                x.PayloadJson,
                x.Error,
                x.CreatedAt,
                x.StartedAt,
                x.CompletedAt,
                x.ProjectId))
            .ToListAsync(cancellationToken);

        var recentErrors = await dbContext.RuntimeLogEntries
            .Where(x => x.CreatedAt >= recentErrorCutoff && (x.Level == "Error" || x.Level == "Critical"))
            .OrderByDescending(x => x.CreatedAt)
            .Take(8)
            .Select(x => new LogEntryResult(
                x.Id,
                x.ServiceName,
                x.Category,
                x.Level,
                x.Message,
                x.Exception,
                x.TraceId,
                x.RequestId,
                x.PayloadJson,
                x.CreatedAt,
                x.ProjectId))
            .ToListAsync(cancellationToken);

        var payload = new DashboardRecentOperationsSnapshotPayload(
            [
                new DashboardOverviewMetricResult(
                    "memoryItems",
                    "全 Instance 記憶資料列",
                    memoryItemCount,
                    "rows",
                    "InstanceInventory",
                    "memory_items 全表資料列；包含所有 owner、default/shared/user、active/archived，不等同 actor-scoped durable governance coverage。"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", defaultProjectMemoryItemCount, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", preferenceCount, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", activeJobCount, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "近 24 小時錯誤", errorLogCount, "logs")
            ],
            activeJobs,
            recentErrors,
            memoryInventory);

        await WriteSnapshotAsync(DashboardSnapshotKeys.RecentOperations, intervalSeconds, payload, cancellationToken);
    }

    private async Task CollectDashboardJobsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var totalCount = await dbContext.MemoryJobs.CountAsync(cancellationToken);
        var jobs = await dbContext.MemoryJobs
            .OrderByDescending(x => x.CreatedAt)
            .Take(100)
            .Select(x => new JobListItemResult(
                x.Id,
                x.JobType,
                x.Status,
                x.PayloadJson,
                x.Error,
                x.CreatedAt,
                x.StartedAt,
                x.CompletedAt,
                x.ProjectId))
            .ToListAsync(cancellationToken);

        await WriteSnapshotAsync(
            DashboardSnapshotKeys.DashboardJobs,
            intervalSeconds,
            new DashboardJobsSnapshotPayload(new PagedResult<JobListItemResult>(jobs, 1, jobs.Count, totalCount)),
            cancellationToken);
    }

    private async Task CollectDashboardLogsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var recentErrors = await dbContext.RuntimeLogEntries
            .Where(x => x.Level == "Error" || x.Level == "Critical")
            .OrderByDescending(x => x.CreatedAt)
            .Take(50)
            .Select(x => new LogEntryResult(
                x.Id,
                x.ServiceName,
                x.Category,
                x.Level,
                x.Message,
                x.Exception,
                x.TraceId,
                x.RequestId,
                x.PayloadJson,
                x.CreatedAt,
                x.ProjectId))
            .ToListAsync(cancellationToken);

        await WriteSnapshotAsync(
            DashboardSnapshotKeys.DashboardLogs,
            intervalSeconds,
            new DashboardLogsSnapshotPayload(recentErrors),
            cancellationToken);
    }

    private async Task CollectDashboardProjectSuggestionsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var projectRows = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => x.ProjectId != ProjectContext.SharedProjectId && x.ProjectId != ProjectContext.UserProjectId)
            .GroupBy(x => x.ProjectId)
            .Select(group => new { ProjectId = group.Key, ItemCount = group.Count() })
            .OrderByDescending(x => x.ItemCount)
            .ThenBy(x => x.ProjectId)
            .Take(100)
            .ToListAsync(cancellationToken);
        var projects = projectRows
            .Select(x => new ProjectSuggestionResult(x.ProjectId, x.ItemCount))
            .ToList();

        await WriteSnapshotAsync(
            DashboardSnapshotKeys.DashboardProjectSuggestions,
            intervalSeconds,
            new DashboardProjectSuggestionsSnapshotPayload(projects),
            cancellationToken);
    }

    private async Task CollectEvaluationSummaryAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var latestRun = await dbContext.EvaluationRuns
            .AsNoTracking()
            .OrderByDescending(x => x.StartedAt)
            .FirstOrDefaultAsync(cancellationToken);

        DashboardEvaluationSummaryResult? summary = null;
        if (latestRun is not null)
        {
            var suiteName = await dbContext.EvaluationSuites
                .AsNoTracking()
                .Where(x => x.Id == latestRun.SuiteId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(cancellationToken);

            summary = new DashboardEvaluationSummaryResult(
                latestRun.Id,
                latestRun.SuiteId,
                suiteName ?? "Unnamed suite",
                latestRun.Status,
                latestRun.HitRate,
                latestRun.RecallAtK,
                latestRun.MeanReciprocalRank,
                latestRun.AverageLatencyMs,
                latestRun.StartedAt,
                latestRun.CompletedAt);
        }

        await WriteSnapshotAsync(
            DashboardSnapshotKeys.EvaluationSummary,
            intervalSeconds,
            new DashboardEvaluationSummarySnapshotPayload(summary),
            cancellationToken);
    }

    private async Task CollectContextSavingsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var windowStartedAt = now.Subtract(ContextSavingsMaxWindow);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        dbContext.Database.SetCommandTimeout(ContextSavingsQueryTimeoutSeconds);
        var events = await dbContext.RetrievalEvents
            .AsNoTracking()
            .Where(x => x.Success)
            .Where(x => x.CreatedAt >= windowStartedAt && x.CreatedAt <= now)
            .Where(x => x.EntryPoint == "build_working_context" ||
                        x.EntryPoint == "mcp.build_working_context" ||
                        x.EntryPoint == "/api/working-context")
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxContextSavingsTelemetryEvents)
            .Select(x => new ContextSavingsTelemetryEvent(
                x.CreatedAt,
                x.CacheHit,
                x.MetadataJson))
            .ToListAsync(cancellationToken);

        var toolCallCounts = await dbContext.McpToolCallEvents
            .AsNoTracking()
            .Where(x => x.CreatedAt >= windowStartedAt && x.CreatedAt <= now)
            .GroupBy(_ => 1)
            .Select(group => new McpToolCallWindowCounts(
                group.LongCount(x => x.CreatedAt >= now.AddHours(-24)),
                group.LongCount(x => x.CreatedAt >= now.AddDays(-3)),
                group.LongCount(x => x.CreatedAt >= now.AddDays(-7)),
                group.LongCount()))
            .SingleOrDefaultAsync(cancellationToken)
            ?? new McpToolCallWindowCounts(0, 0, 0, 0);

        var savings = BuildContextSavings(
            now,
            windowStartedAt,
            events.OrderBy(x => x.CreatedAt),
            toolCallCounts);
        await WriteSnapshotAsync(
            DashboardSnapshotKeys.ContextSavings,
            intervalSeconds,
            new DashboardContextSavingsSnapshotPayload(savings),
            cancellationToken);
    }

    private async Task CollectDiscussionActivityAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var windowStartedAt = now.AddHours(-DiscussionActivityTrendHours);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var threadCount = await dbContext.DiscussionThreads.LongCountAsync(cancellationToken);
        var openThreadCount = await dbContext.DiscussionThreads.LongCountAsync(
            x => x.Status == "Open",
            cancellationToken);
        var messages = await (
            from message in dbContext.DiscussionMessages.AsNoTracking()
            join thread in dbContext.DiscussionThreads.AsNoTracking() on message.ThreadId equals thread.Id
            where message.CreatedAt >= windowStartedAt && message.CreatedAt <= now
            select new DiscussionActivityMessage(thread.HostProjectId, message.CreatedAt))
            .ToListAsync(cancellationToken);

        var activity = BuildDiscussionActivity(now, windowStartedAt, threadCount, openThreadCount, messages);
        await WriteSnapshotAsync(
            DashboardSnapshotKeys.DiscussionActivity,
            intervalSeconds,
            new DashboardDiscussionActivitySnapshotPayload(activity),
            cancellationToken);
    }

    internal static DashboardDiscussionActivityResult BuildDiscussionActivity(
        DateTimeOffset now,
        DateTimeOffset windowStartedAt,
        long threadCount,
        long openThreadCount,
        IEnumerable<DiscussionActivityMessage> messages)
    {
        var recentMessages = messages
            .Where(x => x.CreatedAt >= windowStartedAt && x.CreatedAt <= now)
            .OrderBy(x => x.CreatedAt)
            .ToArray();
        var trend = Enumerable.Range(0, DiscussionActivityTrendHours)
            .Select(hour =>
            {
                var bucketStartedAt = windowStartedAt.AddHours(hour);
                var bucketEndedAt = bucketStartedAt.AddHours(1);
                return new DashboardDiscussionActivityTrendPointResult(
                    bucketStartedAt,
                    recentMessages.Count(x => x.CreatedAt >= bucketStartedAt && x.CreatedAt < bucketEndedAt));
            })
            .ToArray();
        var hostProjectCounts = recentMessages
            .GroupBy(x => x.HostProjectId, StringComparer.Ordinal)
            .Select(group => new DashboardDiscussionHostCountResult(group.Key, group.Count()))
            .OrderByDescending(x => x.MessageCount)
            .ThenBy(x => x.HostProjectId, StringComparer.Ordinal)
            .Take(5)
            .ToArray();

        return new DashboardDiscussionActivityResult(
            threadCount,
            openThreadCount,
            recentMessages.Length,
            windowStartedAt,
            now,
            recentMessages.LastOrDefault()?.CreatedAt,
            trend,
            hostProjectCounts);
    }

    internal static DashboardContextSavingsResult BuildContextSavings(
        DateTimeOffset now,
        DateTimeOffset windowStartedAt,
        IEnumerable<ContextSavingsTelemetryEvent> events,
        McpToolCallWindowCounts? toolCallCounts = null)
    {
        var samples = events
            .Select(x => new ContextSavingsTelemetrySample(
                x.CreatedAt,
                x.CacheHit,
                TryReadSavingsEstimate(x.MetadataJson)))
            .Where(x => x.Savings is not null)
            .OrderBy(x => x.CreatedAt)
            .ToArray();

        var windows = BuildContextSavingsWindows(
            now,
            samples,
            toolCallCounts ?? new McpToolCallWindowCounts(0, 0, 0, 0));
        var primaryWindow = windows[0];
        if (!primaryWindow.HasData)
        {
            return CreateEmptyContextSavings(now, now.AddHours(-24), windows);
        }

        return new DashboardContextSavingsResult(
            true,
            primaryWindow.SampleCount,
            primaryWindow.BaselineTokenEstimate,
            primaryWindow.ReturnedTokenEstimate,
            primaryWindow.EstimatedSavedTokens,
            primaryWindow.SavingPercent,
            primaryWindow.Confidence,
            primaryWindow.SourceCoveragePercent,
            primaryWindow.CacheHitPercent,
            primaryWindow.WindowStartedAtUtc,
            now,
            BuildContextSavingsTrend(samples, primaryWindow.WindowStartedAtUtc, now),
            true,
            primaryWindow.LastSampleAtUtc,
            primaryWindow.Label,
            windows,
            primaryWindow.ExactCoveragePercent,
            primaryWindow.TokenCountingMode,
            primaryWindow.ActualToolCallCount);
    }

    private static IReadOnlyList<DashboardContextSavingsWindowResult> BuildContextSavingsWindows(
        DateTimeOffset now,
        IReadOnlyList<ContextSavingsTelemetrySample> samples,
        McpToolCallWindowCounts toolCallCounts)
        =>
        [
            BuildContextSavingsWindow("24h", "24H", now.AddHours(-24), now, samples, toolCallCounts.TwentyFourHours),
            BuildContextSavingsWindow("3d", "3D", now.AddDays(-3), now, samples, toolCallCounts.ThreeDays),
            BuildContextSavingsWindow("7d", "7D", now.AddDays(-7), now, samples, toolCallCounts.SevenDays),
            BuildContextSavingsWindow("30d", "30D", now.AddDays(-30), now, samples, toolCallCounts.ThirtyDays)
        ];

    private static DashboardContextSavingsWindowResult BuildContextSavingsWindow(
        string key,
        string label,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt,
        IReadOnlyList<ContextSavingsTelemetrySample> samples,
        long actualToolCallCount)
    {
        var windowSamples = samples
            .Where(x => x.CreatedAt >= startedAt && x.CreatedAt <= endedAt)
            .ToArray();
        if (windowSamples.Length == 0)
        {
            return new DashboardContextSavingsWindowResult(
                key,
                label,
                false,
                0,
                0,
                0,
                0,
                0d,
                ContextSavingsEstimator.LowConfidence,
                0d,
                0d,
                startedAt,
                endedAt,
                null,
                ActualToolCallCount: actualToolCallCount);
        }

        var baseline = windowSamples.Sum(x => Math.Max(0, x.Savings!.BaselineTokenEstimate));
        var returned = windowSamples.Sum(x => Math.Max(0, x.Savings!.ReturnedTokenEstimate));
        var saved = windowSamples.Sum(x => Math.Max(0, x.Savings!.EstimatedSavedTokens));
        var savingPercent = baseline > 0
            ? saved / (double)baseline * 100d
            : 0d;
        var coveragePercent = WeightedAverageCoverage(windowSamples, baseline);
        var exactCoveragePercent = WeightedAverageExactCoverage(windowSamples, baseline);
        var cacheHitPercent = windowSamples.Count(x => x.CacheHit) / (double)windowSamples.Length * 100d;
        var tokenCountingMode = ResolveTokenCountingMode(windowSamples, exactCoveragePercent);

        return new DashboardContextSavingsWindowResult(
            key,
            label,
            true,
            windowSamples.Length,
            baseline,
            returned,
            saved,
            Math.Round(savingPercent, 2),
            ResolveSavingsConfidence(coveragePercent),
            Math.Round(coveragePercent, 2),
            Math.Round(cacheHitPercent, 2),
            startedAt,
            endedAt,
            windowSamples[^1].CreatedAt,
            Math.Round(exactCoveragePercent, 2),
            tokenCountingMode,
            actualToolCallCount);
    }

    private static IReadOnlyList<DashboardContextSavingsTrendPointResult> BuildContextSavingsTrend(
        IReadOnlyList<ContextSavingsTelemetrySample> samples,
        DateTimeOffset startedAt,
        DateTimeOffset endedAt)
    {
        var windowSamples = samples
            .Where(x => x.CreatedAt >= startedAt && x.CreatedAt <= endedAt)
            .ToArray();
        if (windowSamples.Length == 0)
        {
            return [];
        }

        var windowSeconds = Math.Max(1d, (endedAt - startedAt).TotalSeconds);
        var bucketSeconds = Math.Max(1d, Math.Ceiling(windowSeconds / MaxContextSavingsTrendPoints));
        return windowSamples
            .GroupBy(x => (int)Math.Floor(Math.Max(0d, (x.CreatedAt - startedAt).TotalSeconds) / bucketSeconds))
            .OrderBy(group => group.Key)
            .Select(group =>
            {
                var bucketSamples = group.ToArray();
                var baseline = bucketSamples.Sum(x => Math.Max(0, x.Savings!.BaselineTokenEstimate));
                var returned = bucketSamples.Sum(x => Math.Max(0, x.Savings!.ReturnedTokenEstimate));
                var saved = bucketSamples.Sum(x => Math.Max(0, x.Savings!.EstimatedSavedTokens));
                var savingPercent = baseline > 0
                    ? saved / (double)baseline * 100d
                    : 0d;
                var exactCoveragePercent = WeightedAverageExactCoverage(bucketSamples, baseline);
                return new DashboardContextSavingsTrendPointResult(
                    bucketSamples[^1].CreatedAt,
                    baseline,
                    returned,
                    saved,
                    Math.Round(savingPercent, 2),
                    Math.Round(exactCoveragePercent, 2),
                    ResolveTokenCountingMode(bucketSamples, exactCoveragePercent));
            })
            .ToArray();
    }

    private static double WeightedAverageCoverage(IReadOnlyList<ContextSavingsTelemetrySample> samples, int baseline)
    {
        if (samples.Count == 0)
        {
            return 0d;
        }

        if (baseline <= 0)
        {
            return samples.Average(x => x.Savings!.SourceCoveragePercent);
        }

        return samples.Sum(x => Math.Max(0, x.Savings!.BaselineTokenEstimate) * x.Savings!.SourceCoveragePercent) / baseline;
    }

    private static double WeightedAverageExactCoverage(IReadOnlyList<ContextSavingsTelemetrySample> samples, int baseline)
    {
        if (samples.Count == 0)
        {
            return 0d;
        }

        if (baseline <= 0)
        {
            return samples.Average(x => x.Savings!.ExactCoveragePercent);
        }

        return samples.Sum(x => Math.Max(0, x.Savings!.BaselineTokenEstimate) * x.Savings!.ExactCoveragePercent) / baseline;
    }

    private static string ResolveTokenCountingMode(IReadOnlyList<ContextSavingsTelemetrySample> samples, double exactCoveragePercent)
    {
        if (samples.Count == 0 || exactCoveragePercent <= 0d)
        {
            return TokenCountingModes.Approximate;
        }

        return exactCoveragePercent >= 80d &&
               samples.All(x => string.Equals(x.Savings!.TokenCountingMode, TokenCountingModes.Exact, StringComparison.OrdinalIgnoreCase))
            ? TokenCountingModes.Exact
            : TokenCountingModes.Mixed;
    }

    private static DashboardContextSavingsResult CreateEmptyContextSavings(
        DateTimeOffset now,
        DateTimeOffset windowStartedAt,
        IReadOnlyList<DashboardContextSavingsWindowResult>? windows = null)
        => new(
            false,
            0,
            0,
            0,
            0,
            0d,
            ContextSavingsEstimator.LowConfidence,
            0d,
            0d,
            windowStartedAt,
            now,
            [],
            false,
            null,
            "24H",
            windows ?? BuildContextSavingsWindows(now, [], new McpToolCallWindowCounts(0, 0, 0, 0)),
            0d,
            TokenCountingModes.Approximate,
            windows?.FirstOrDefault()?.ActualToolCallCount ?? 0);

    private static ContextSavingsEstimateResult? TryReadSavingsEstimate(string metadataJson)
    {
        if (string.IsNullOrWhiteSpace(metadataJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ContextSavingsTelemetryMetadata>(metadataJson, JsonOptions)?.Savings;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string ResolveSavingsConfidence(double sourceCoveragePercent)
        => sourceCoveragePercent switch
        {
            >= 80d => ContextSavingsEstimator.HighConfidence,
            >= 50d => ContextSavingsEstimator.MediumConfidence,
            _ => ContextSavingsEstimator.LowConfidence
        };

    private async Task CollectStorageTableStatsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var storageExplorerStore = scope.ServiceProvider.GetRequiredService<IStorageExplorerStore>();
        var tables = await storageExplorerStore.ListTablesAsync(cancellationToken);
        await WriteSnapshotAsync(
            DashboardSnapshotKeys.StorageTableStats,
            intervalSeconds,
            new DashboardStorageTableStatsSnapshotPayload(tables),
            cancellationToken);
    }

    private async Task CollectStorageLargeTablePreviewAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContextFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<MemoryDbContext>>();
        if (await IsTelemetryMaintenanceRunningAsync(dbContextFactory, cancellationToken))
        {
            await WriteSnapshotAsync(
                DashboardSnapshotKeys.StorageLargeTablePreview,
                intervalSeconds,
                new DashboardStorageLargeTablePreviewSnapshotPayload([], "Skipped during telemetry maintenance"),
                cancellationToken);
            return;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(StorageLargeTablePreviewQueryTimeoutSeconds));
        var timeoutToken = timeoutCts.Token;
        var storageExplorerStore = scope.ServiceProvider.GetRequiredService<IStorageExplorerStore>();
        var tables = new List<StorageTableRowsResult>();
        foreach (var table in DashboardStoragePolicy.LargeTableNames)
        {
            var preview = await storageExplorerStore.GetRowsAsync(
                new StorageRowsRequest(
                    table,
                    Page: 1,
                    PageSize: DashboardStoragePolicy.LargeTablePreviewPageSize),
                timeoutToken);
            tables.Add(preview with { DataSource = "redis" });
        }

        await WriteSnapshotAsync(
            DashboardSnapshotKeys.StorageLargeTablePreview,
            intervalSeconds,
            new DashboardStorageLargeTablePreviewSnapshotPayload(tables),
            cancellationToken);
    }

    private static async Task<bool> IsTelemetryMaintenanceRunningAsync(
        IDbContextFactory<MemoryDbContext> dbContextFactory,
        CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        return await dbContext.MaintenanceRuns
            .AsNoTracking()
            .AnyAsync(
                x => x.Status == MaintenanceRunStatus.Running &&
                     (x.MaintenanceType == MaintenanceRunType.RetrievalTelemetryRetention ||
                      x.MaintenanceType == MaintenanceRunType.VacuumFullReclaim ||
                      x.MaintenanceType == MaintenanceRunType.MemoryDataRetention),
                cancellationToken);
    }

    private async Task CollectResourceChartAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var snapshot = await dockerMetricsService.GetSnapshotAsync(cancellationToken);
        var requestSample = requestTrafficSnapshotAccessor.GetRecentSampleTotal(intervalSeconds);

        await _resourceLock.WaitAsync(cancellationToken);
        try
        {
            var sample = BuildResourceSample(snapshot, requestSample);
            _previousDockerSnapshot = snapshot;
            _resourceSamples.Add(sample);
            if (_resourceSamples.Count > MaxResourceSamples)
            {
                _resourceSamples.RemoveAt(0);
            }

            await WriteSnapshotAsync(
                DashboardSnapshotKeys.ResourceChart,
                intervalSeconds,
                new DashboardResourceChartSnapshotPayload(_resourceSamples.ToArray()),
                cancellationToken);
        }
        finally
        {
            _resourceLock.Release();
        }
    }

    private async Task CollectMemoryGraphIndexAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var previousActor = actorAccessor.Current;
        actorAccessor.Current = new ContextHubRequestActor(
            null,
            null,
            "dashboard-snapshot-collector",
            null,
            [SecurityScopes.MemoryRead],
            [],
            IsAuthenticated: true,
            IsServiceActor: true);

        var refreshService = scope.ServiceProvider.GetRequiredService<IDashboardMemoryGraphIndexRefreshService>();
        try
        {
            await refreshService.RefreshAsync("scheduled", intervalSeconds, cancellationToken);
        }
        finally
        {
            actorAccessor.Current = previousActor;
        }
    }

    private DashboardResourceSampleResult BuildResourceSample(DockerRuntimeSnapshot snapshot, RequestTrafficSampleResult requestSample)
    {
        var capturedAt = snapshot.Host.CapturedAtUtc;
        var memoryUsage = snapshot.Containers.Sum(x => x.Metric.MemoryUsageBytes);
        var networkRxBytes = snapshot.Containers.Sum(x => x.Metric.NetworkRxBytes);
        var networkTxBytes = snapshot.Containers.Sum(x => x.Metric.NetworkTxBytes);
        var diskReadBytes = snapshot.Containers.Sum(x => x.Metric.DiskReadBytes);
        var diskWriteBytes = snapshot.Containers.Sum(x => x.Metric.DiskWriteBytes);

        return new DashboardResourceSampleResult(
            capturedAt,
            Math.Max(snapshot.Containers.Sum(x => x.Metric.CpuPercent), 0d),
            snapshot.Host.TotalMemoryBytes <= 0
                ? 0d
                : Math.Clamp((double)memoryUsage / snapshot.Host.TotalMemoryBytes * 100d, 0d, 100d),
            memoryUsage,
            CalculateRate(networkRxBytes, x => x.Metric.NetworkRxBytes, capturedAt),
            CalculateRate(networkTxBytes, x => x.Metric.NetworkTxBytes, capturedAt),
            CalculateRate(diskReadBytes, x => x.Metric.DiskReadBytes, capturedAt),
            CalculateRate(diskWriteBytes, x => x.Metric.DiskWriteBytes, capturedAt),
            requestSample.InboundRequests,
            requestSample.OutboundRequests);
    }

    private double CalculateRate(long currentTotal, Func<DockerContainerRuntimeSnapshot, long> selector, DateTimeOffset currentCapturedAt)
    {
        if (_previousDockerSnapshot is null)
        {
            return 0d;
        }

        var elapsedSeconds = (currentCapturedAt - _previousDockerSnapshot.Host.CapturedAtUtc).TotalSeconds;
        if (elapsedSeconds <= 0)
        {
            return 0d;
        }

        var previousTotal = _previousDockerSnapshot.Containers.Sum(selector);
        var delta = currentTotal - previousTotal;
        return delta <= 0 ? 0d : delta / elapsedSeconds;
    }

    private async Task WriteSnapshotAsync<TPayload>(string key, int intervalSeconds, TPayload payload, CancellationToken cancellationToken)
    {
        var capturedAtUtc = timeProvider.GetUtcNow();
        await snapshotStore.SetAsync(
            new DashboardSnapshotEnvelope<TPayload>(
                key,
                capturedAtUtc,
                intervalSeconds,
                DashboardSnapshotStalenessPolicy.ComputeStaleAfter(capturedAtUtc, intervalSeconds),
                string.Empty,
                payload),
            cancellationToken);
    }

    private async Task UpdateLastErrorAsync<TPayload>(string key, int intervalSeconds, string error, CancellationToken cancellationToken)
    {
        var existing = await snapshotStore.GetAsync<TPayload>(key, cancellationToken);
        if (existing is null)
        {
            return;
        }

        await snapshotStore.SetAsync(existing with
        {
            RefreshIntervalSeconds = intervalSeconds,
            StaleAfterUtc = DashboardSnapshotStalenessPolicy.ComputeStaleAfter(existing.CapturedAtUtc, intervalSeconds),
            LastError = error
        }, cancellationToken);
    }

    private async Task TryUpdateLastErrorAsync<TPayload>(string key, int intervalSeconds, string error, CancellationToken cancellationToken)
    {
        try
        {
            await UpdateLastErrorAsync<TPayload>(key, intervalSeconds, error, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Dashboard snapshot collector could not update last error for {SnapshotKey}.", key);
        }
    }

    private sealed record SnapshotFailureState(
        int Count,
        DateTimeOffset FirstFailureAtUtc,
        DateTimeOffset LastFailureLoggedAtUtc,
        DateTimeOffset? CooldownUntilUtc,
        DateTimeOffset? LastSummaryLoggedAtUtc,
        string FailureKind);

    private async Task<DashboardRedisTelemetryResult> CollectRedisTelemetryAsync(
        DockerRuntimeSnapshot dockerSnapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = redis.GetEndPoints(configuredOnly: true).FirstOrDefault()
                ?? redis.GetEndPoints().FirstOrDefault()
                ?? throw new InvalidOperationException("Redis endpoint unavailable.");
            var server = redis.GetServer(endpoint);
            var database = redis.GetDatabase();
            var info = await server.ExecuteAsync("INFO");
            var infoMap = ParseRedisInfo(info.ToString());
            var cacheSnapshot = redisCacheTelemetry.GetSnapshot();
            var keyCountResult = await database.ExecuteAsync("DBSIZE");
            var keyCount = long.TryParse(keyCountResult.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedKeyCount)
                ? parsedKeyCount
                : 0L;
            var keyspaceHits = GetRedisInfoLong(infoMap, "keyspace_hits");
            var keyspaceMisses = GetRedisInfoLong(infoMap, "keyspace_misses");
            var keyspaceLookups = SumNonNegative(keyspaceHits, keyspaceMisses);
            var cacheLookups = SumNonNegative(cacheSnapshot.Hits, cacheSnapshot.Misses);
            var usedMemoryBytes = GetRedisInfoLong(infoMap, "used_memory");

            var container = dockerSnapshot.Containers.FirstOrDefault(x => string.Equals(x.Metric.Service, "redis", StringComparison.OrdinalIgnoreCase));
            var storage = DashboardPersistentStorageResolver.Resolve(
                dockerSnapshot,
                container,
                "/data",
                usedMemoryBytes,
                "Redis 邏輯使用量");
            var warning = storage is null
                ? "未偵測 Redis 持久化掛載；儲存量無法估算。"
                : string.Empty;
            var status = string.Equals(dockerSnapshot.Status, "Healthy", StringComparison.OrdinalIgnoreCase)
                ? "Healthy"
                : "Degraded";

            return new DashboardRedisTelemetryResult(
                status,
                warning,
                usedMemoryBytes,
                GetRedisInfoLong(infoMap, "maxmemory"),
                keyCount,
                GetRedisInfoLong(infoMap, "total_commands_processed"),
                GetRedisInfoLong(infoMap, "total_net_input_bytes"),
                GetRedisInfoLong(infoMap, "total_net_output_bytes"),
                GetRedisInfoDouble(infoMap, "instantaneous_input_kbps"),
                GetRedisInfoDouble(infoMap, "instantaneous_output_kbps"),
                GetRedisInfoLong(infoMap, "expired_keys"),
                GetRedisInfoLong(infoMap, "evicted_keys"),
                container?.Metric.NetworkRxBytes ?? 0,
                container?.Metric.NetworkTxBytes ?? 0,
                container?.Metric.DiskReadBytes ?? 0,
                container?.Metric.DiskWriteBytes ?? 0,
                storage?.SizeBytes ?? 0,
                storage?.DisplayName ?? "未配置",
                cacheSnapshot.Hits,
                cacheSnapshot.Misses,
                cacheSnapshot.Sets,
                cacheSnapshot.Bypasses,
                cacheSnapshot.Errors,
                keyspaceHits,
                keyspaceMisses,
                keyspaceLookups,
                CalculateHitPercent(keyspaceHits, keyspaceMisses),
                cacheLookups,
                CalculateHitPercent(cacheSnapshot.Hits, cacheSnapshot.Misses));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DashboardRedisTelemetryResult(
                "Unavailable",
                ex.Message,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "未配置");
        }
    }

    private async Task<DashboardPostgresTelemetryResult> CollectPostgresTelemetryAsync(
        DockerRuntimeSnapshot dockerSnapshot,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
            var connection = (NpgsqlConnection)dbContext.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
            {
                await connection.OpenAsync(cancellationToken);
            }

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                SELECT
                    numbackends,
                    xact_commit,
                    xact_rollback,
                    blks_read,
                    blks_hit,
                    tup_returned,
                    tup_fetched,
                    tup_inserted,
                    tup_updated,
                    tup_deleted,
                    temp_files,
                    temp_bytes,
                    deadlocks,
                    pg_database_size(current_database()) AS database_size_bytes
                FROM pg_stat_database
                WHERE datname = current_database();
                """;

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                throw new InvalidOperationException("pg_stat_database returned no row for current database.");
            }

            var blocksRead = reader.GetInt64(3);
            var blocksHit = reader.GetInt64(4);
            var blockAccesses = SumNonNegative(blocksRead, blocksHit);
            var databaseSizeBytes = reader.GetInt64(13);
            var container = dockerSnapshot.Containers.FirstOrDefault(x => string.Equals(x.Metric.Service, "postgres", StringComparison.OrdinalIgnoreCase));
            var storage = DashboardPersistentStorageResolver.Resolve(
                dockerSnapshot,
                container,
                "/var/lib/postgresql/data",
                databaseSizeBytes,
                "資料庫邏輯大小");
            var warning = storage is null
                ? "未偵測 PostgreSQL 持久化掛載；儲存量無法估算。"
                : string.Empty;
            var status = string.Equals(dockerSnapshot.Status, "Healthy", StringComparison.OrdinalIgnoreCase)
                ? "Healthy"
                : "Degraded";

            return new DashboardPostgresTelemetryResult(
                status,
                warning,
                reader.GetInt32(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                blocksRead,
                blocksHit,
                reader.GetInt64(5),
                reader.GetInt64(6),
                reader.GetInt64(7),
                reader.GetInt64(8),
                reader.GetInt64(9),
                reader.GetInt64(10),
                reader.GetInt64(11),
                reader.GetInt64(12),
                container?.Metric.NetworkRxBytes ?? 0,
                container?.Metric.NetworkTxBytes ?? 0,
                container?.Metric.DiskReadBytes ?? 0,
                container?.Metric.DiskWriteBytes ?? 0,
                storage?.SizeBytes ?? 0,
                storage?.DisplayName ?? "未配置",
                databaseSizeBytes,
                blockAccesses,
                CalculateHitPercent(blocksHit, blocksRead));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new DashboardPostgresTelemetryResult(
                "Unavailable",
                ex.Message,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                0,
                "未配置",
                0);
        }
    }

    private static Dictionary<string, string> ParseRedisInfo(string info)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in info.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith('#') || !line.Contains(':', StringComparison.Ordinal))
            {
                continue;
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex == line.Length - 1)
            {
                continue;
            }

            map[line[..separatorIndex]] = line[(separatorIndex + 1)..];
        }

        return map;
    }

    private static long GetRedisInfoLong(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var raw) &&
           long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0;

    private static double GetRedisInfoDouble(IReadOnlyDictionary<string, string> values, string key)
        => values.TryGetValue(key, out var raw) &&
           double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d;

    private static long SumNonNegative(long left, long right)
        => Math.Max(0, left) + Math.Max(0, right);

    private static double CalculateHitPercent(long hits, long misses)
    {
        var total = SumNonNegative(hits, misses);
        return total <= 0
            ? 0d
            : Math.Round(Math.Max(0, hits) * 100d / total, 2);
    }

    private sealed record ContextSavingsTelemetryMetadata(
        ContextSavingsEstimateResult? Savings);

    internal sealed record ContextSavingsTelemetryEvent(
        DateTimeOffset CreatedAt,
        bool CacheHit,
        string MetadataJson);

    internal sealed record McpToolCallWindowCounts(
        long TwentyFourHours,
        long ThreeDays,
        long SevenDays,
        long ThirtyDays);

    internal sealed record DiscussionActivityMessage(
        string HostProjectId,
        DateTimeOffset CreatedAt);

    private sealed record ContextSavingsTelemetrySample(
        DateTimeOffset CreatedAt,
        bool CacheHit,
        ContextSavingsEstimateResult? Savings);
}

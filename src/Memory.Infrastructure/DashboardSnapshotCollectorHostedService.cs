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
    TimeProvider timeProvider,
    ILogger<DashboardSnapshotCollectorHostedService> logger) : BackgroundService
{
    private const int MaxResourceSamples = 15;
    private const int ContextSavingsMinimumIntervalSeconds = 60;
    private static readonly TimeSpan StartupDependencyFailureGrace = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FailureLogThrottle = TimeSpan.FromMinutes(2);
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
        var state = _failureStates.AddOrUpdate(
            key,
            _ => new SnapshotFailureState(1, now, now),
            (_, current) => current with
            {
                Count = current.Count + 1,
                LastFailureLoggedAtUtc = ShouldLogFailure(current.LastFailureLoggedAtUtc, now)
                    ? now
                    : current.LastFailureLoggedAtUtc
            });

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

        await WriteSnapshotAsync(
            DashboardSnapshotKeys.MonitoringStats,
            intervalSeconds,
            new DashboardMonitoringSnapshotPayload(redisTelemetry, postgresTelemetry),
            cancellationToken);
    }

    private async Task CollectRecentOperationsAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);

        var memoryItemCount = await dbContext.MemoryItems.CountAsync(cancellationToken);
        var defaultProjectMemoryItemCount = await dbContext.MemoryItems.CountAsync(
            x => x.ProjectId == ProjectContext.DefaultProjectId,
            cancellationToken);
        var preferenceCount = await dbContext.MemoryItems.CountAsync(
            x => x.Scope == MemoryScope.User && x.MemoryType == MemoryType.Preference && x.Status == MemoryStatus.Active,
            cancellationToken);
        var activeJobCount = await dbContext.MemoryJobs.CountAsync(
            x => x.Status == MemoryJobStatus.Pending || x.Status == MemoryJobStatus.Running,
            cancellationToken);
        var errorLogCount = await dbContext.RuntimeLogEntries.CountAsync(
            x => x.Level == "Error" || x.Level == "Critical",
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
            .Where(x => x.Level == "Error" || x.Level == "Critical")
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
                new DashboardOverviewMetricResult("memoryItems", "記憶條目", memoryItemCount, "items"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", defaultProjectMemoryItemCount, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", preferenceCount, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", activeJobCount, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "錯誤日誌", errorLogCount, "logs")
            ],
            activeJobs,
            recentErrors);

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
        var windowStartedAt = now.AddHours(-24);
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var events = await dbContext.RetrievalEvents
            .AsNoTracking()
            .Where(x => x.Success)
            .Where(x => x.CreatedAt >= windowStartedAt && x.CreatedAt <= now)
            .OrderByDescending(x => x.CreatedAt)
            .Take(250)
            .Select(x => new ContextSavingsTelemetryEvent(
                x.CreatedAt,
                x.CacheHit,
                x.MetadataJson))
            .ToListAsync(cancellationToken);

        var savings = BuildContextSavings(now, windowStartedAt, events);
        await WriteSnapshotAsync(
            DashboardSnapshotKeys.ContextSavings,
            intervalSeconds,
            new DashboardContextSavingsSnapshotPayload(savings),
            cancellationToken);
    }

    private static DashboardContextSavingsResult BuildContextSavings(
        DateTimeOffset now,
        DateTimeOffset windowStartedAt,
        IEnumerable<ContextSavingsTelemetryEvent> events)
    {
        var samples = events
            .Select(x => new ContextSavingsTelemetrySample(
                x.CreatedAt,
                x.CacheHit,
                TryReadSavingsEstimate(x.MetadataJson)))
            .Where(x => x.Savings is not null)
            .OrderBy(x => x.CreatedAt)
            .ToArray();

        if (samples.Length == 0)
        {
            return CreateEmptyContextSavings(now, windowStartedAt);
        }

        var baseline = samples.Sum(x => x.Savings!.BaselineTokenEstimate);
        var returned = samples.Sum(x => x.Savings!.ReturnedTokenEstimate);
        var saved = samples.Sum(x => x.Savings!.EstimatedSavedTokens);
        var averageSavingPercent = samples.Average(x => x.Savings!.EstimatedSavingPercent);
        var averageCoverage = samples.Average(x => x.Savings!.SourceCoveragePercent);
        var cacheHitPercent = samples.Count(x => x.CacheHit) / (double)samples.Length * 100d;
        var trend = samples
            .TakeLast(24)
            .Select(x => new DashboardContextSavingsTrendPointResult(
                x.CreatedAt,
                x.Savings!.BaselineTokenEstimate,
                x.Savings.ReturnedTokenEstimate,
                x.Savings.EstimatedSavedTokens,
                x.Savings.EstimatedSavingPercent))
            .ToArray();

        return new DashboardContextSavingsResult(
            true,
            samples.Length,
            baseline,
            returned,
            saved,
            Math.Round(averageSavingPercent, 2),
            ResolveSavingsConfidence(averageCoverage),
            Math.Round(averageCoverage, 2),
            Math.Round(cacheHitPercent, 2),
            windowStartedAt,
            now,
            trend);
    }

    private static DashboardContextSavingsResult CreateEmptyContextSavings(DateTimeOffset now, DateTimeOffset windowStartedAt)
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
            []);

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

        var storageExplorerStore = scope.ServiceProvider.GetRequiredService<IStorageExplorerStore>();
        var tables = new List<StorageTableRowsResult>();
        foreach (var table in DashboardStoragePolicy.LargeTableNames)
        {
            var preview = await storageExplorerStore.GetRowsAsync(
                new StorageRowsRequest(
                    table,
                    Page: 1,
                    PageSize: DashboardStoragePolicy.LargeTablePreviewPageSize),
                cancellationToken);
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
                      x.MaintenanceType == MaintenanceRunType.VacuumFullReclaim),
                cancellationToken);
    }

    private async Task CollectResourceChartAsync(int intervalSeconds, CancellationToken cancellationToken)
    {
        var snapshot = await dockerMetricsService.GetSnapshotAsync(cancellationToken);
        var requestTraffic = requestTrafficSnapshotAccessor.GetRecentSamples(MaxResourceSamples);
        var requestSample = requestTraffic.LastOrDefault() ?? new RequestTrafficSampleResult(timeProvider.GetUtcNow(), 0, 0);

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
        var refreshService = scope.ServiceProvider.GetRequiredService<IDashboardMemoryGraphIndexRefreshService>();
        await refreshService.RefreshAsync("scheduled", intervalSeconds, cancellationToken);
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
            requestSample.TimestampUtc,
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
        DateTimeOffset LastFailureLoggedAtUtc);

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

            var container = dockerSnapshot.Containers.FirstOrDefault(x => string.Equals(x.Metric.Service, "redis", StringComparison.OrdinalIgnoreCase));
            var storage = DashboardPersistentStorageResolver.Resolve(dockerSnapshot, container, "/data");
            var warning = storage is null
                ? "未配置 Redis 專屬 volume；磁碟空間僅能回報容器 I/O。"
                : string.Empty;
            var status = string.Equals(dockerSnapshot.Status, "Healthy", StringComparison.OrdinalIgnoreCase)
                ? "Healthy"
                : "Degraded";

            return new DashboardRedisTelemetryResult(
                status,
                warning,
                GetRedisInfoLong(infoMap, "used_memory"),
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
                cacheSnapshot.Errors);
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

            var container = dockerSnapshot.Containers.FirstOrDefault(x => string.Equals(x.Metric.Service, "postgres", StringComparison.OrdinalIgnoreCase));
            var storage = DashboardPersistentStorageResolver.Resolve(dockerSnapshot, container, "/var/lib/postgresql/data");
            var warning = storage is null
                ? "未偵測 PostgreSQL 專屬 volume；磁碟空間僅回報資料庫大小。"
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
                reader.GetInt64(3),
                reader.GetInt64(4),
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
                reader.GetInt64(13));
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

    private sealed record ContextSavingsTelemetryMetadata(
        ContextSavingsEstimateResult? Savings);

    private sealed record ContextSavingsTelemetryEvent(
        DateTimeOffset CreatedAt,
        bool CacheHit,
        string MetadataJson);

    private sealed record ContextSavingsTelemetrySample(
        DateTimeOffset CreatedAt,
        bool CacheHit,
        ContextSavingsEstimateResult? Savings);
}

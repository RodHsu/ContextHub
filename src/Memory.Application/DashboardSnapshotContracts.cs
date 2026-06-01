namespace Memory.Application;

public static class DashboardSnapshotKeys
{
    public const string StatusCore = "statusCore";
    public const string EmbeddingRuntime = "embeddingRuntime";
    public const string DependenciesHealth = "dependenciesHealth";
    public const string DockerHost = "dockerHost";
    public const string DependencyResources = "dependencyResources";
    public const string RecentOperations = "recentOperations";
    public const string ResourceChart = "resourceChart";
    public const string MonitoringStats = "monitoringStats";
    public const string MemoryGraphIndex = "memoryGraphIndex";
    public const string StorageTableStats = "storageTableStats";
    public const string StorageLargeTablePreview = "storageLargeTablePreview";
    public const string DashboardJobs = "dashboardJobs";
    public const string DashboardLogs = "dashboardLogs";
    public const string DashboardProjectSuggestions = "dashboardProjectSuggestions";
}

public static class DashboardSnapshotStalenessPolicy
{
    public const int WarningThresholdSeconds = 15;

    public static DateTimeOffset ComputeStaleAfter(DateTimeOffset capturedAtUtc)
        => capturedAtUtc.AddSeconds(WarningThresholdSeconds);
}

public sealed record DashboardSnapshotEnvelope<TPayload>(
    string Key,
    DateTimeOffset CapturedAtUtc,
    int RefreshIntervalSeconds,
    DateTimeOffset StaleAfterUtc,
    string LastError,
    TPayload Payload);

public sealed record DashboardStatusCoreSnapshotPayload(
    string Service,
    string Namespace,
    string BuildVersion,
    DateTimeOffset BuildTimestampUtc,
    string EmbeddingProvider,
    string ExecutionProvider,
    string EmbeddingProfile,
    string ModelKey,
    int Dimensions,
    int MaxTokens,
    int InferenceThreads,
    int BatchSize,
    bool BatchingEnabled,
    long CacheVersion);

public sealed record DashboardEmbeddingRuntimeSnapshotPayload(
    string Namespace,
    string BuildVersion,
    DateTimeOffset BuildTimestampUtc,
    string EmbeddingProvider,
    string ExecutionProvider,
    string EmbeddingProfile,
    string ModelKey,
    int Dimensions,
    int MaxTokens,
    int InferenceThreads,
    int BatchSize,
    bool BatchingEnabled);

public sealed record DashboardDependenciesHealthSnapshotPayload(
    IReadOnlyList<DashboardServiceHealthResult> Services);

public sealed record DashboardRecentOperationsSnapshotPayload(
    IReadOnlyList<DashboardOverviewMetricResult> Metrics,
    IReadOnlyList<JobListItemResult> ActiveJobs,
    IReadOnlyList<LogEntryResult> RecentErrors);

public sealed record DashboardResourceChartSnapshotPayload(
    IReadOnlyList<DashboardResourceSampleResult> Samples);

public sealed record DashboardMonitoringSnapshotPayload(
    DashboardRedisTelemetryResult Redis,
    DashboardPostgresTelemetryResult Postgres);

public sealed record DashboardMemoryGraphIndexSnapshotPayload(
    MemoryGraphResult Graph);

public sealed record DashboardStorageTableStatsSnapshotPayload(
    IReadOnlyList<StorageTableSummaryResult> Tables);

public sealed record DashboardStorageLargeTablePreviewSnapshotPayload(
    IReadOnlyList<StorageTableRowsResult> Tables,
    string Warning = "");

public sealed record DashboardJobsSnapshotPayload(
    PagedResult<JobListItemResult> RecentJobs);

public sealed record DashboardLogsSnapshotPayload(
    IReadOnlyList<LogEntryResult> RecentErrors);

public sealed record DashboardProjectSuggestionsSnapshotPayload(
    IReadOnlyList<ProjectSuggestionResult> Projects);

public sealed record DashboardMemoryGraphIndexRefreshResult(
    DateTimeOffset CapturedAtUtc,
    int RefreshIntervalSeconds,
    string Trigger,
    int NodeCount,
    int EdgeCount,
    bool Truncated);

public interface IDashboardSnapshotStore
{
    Task<DashboardSnapshotEnvelope<TPayload>?> GetAsync<TPayload>(string key, CancellationToken cancellationToken);
    Task SetAsync<TPayload>(DashboardSnapshotEnvelope<TPayload> envelope, CancellationToken cancellationToken);
}

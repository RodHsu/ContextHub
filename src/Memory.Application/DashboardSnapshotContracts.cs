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

public interface IDashboardSnapshotStore
{
    Task<DashboardSnapshotEnvelope<TPayload>?> GetAsync<TPayload>(string key, CancellationToken cancellationToken);
    Task SetAsync<TPayload>(DashboardSnapshotEnvelope<TPayload> envelope, CancellationToken cancellationToken);
}

using Memory.Domain;

namespace Memory.Application;

public sealed record PagedResult<T>(
    IReadOnlyList<T> Items,
    int Page,
    int PageSize,
    int TotalCount);

public sealed record DashboardServiceHealthResult(
    string Name,
    string Status,
    string Description);

public sealed record DashboardOverviewMetricResult(
    string Key,
    string Label,
    long Value,
    string Unit);

public sealed record DashboardSnapshotSectionStatusResult(
    string Key,
    string Label,
    DateTimeOffset CapturedAtUtc,
    int RefreshIntervalSeconds,
    bool IsStale,
    string LastError,
    string Warning);

public sealed record DashboardPageSnapshotStatusResult(
    DateTimeOffset SnapshotAtUtc,
    bool IsStale,
    string Warning,
    IReadOnlyList<DashboardSnapshotSectionStatusResult> Sections);

public sealed record RequestTrafficSampleResult(
    DateTimeOffset TimestampUtc,
    int InboundRequests,
    int OutboundRequests);

public sealed record DockerHostSummaryResult(
    string HostName,
    string ServerVersion,
    string OperatingSystem,
    string KernelVersion,
    int CpuCount,
    long TotalMemoryBytes,
    long EstimatedAvailableMemoryBytes,
    long ActiveContainerCount,
    long ImageCount,
    long VolumeCount,
    DateTimeOffset CapturedAtUtc);

public sealed record DockerContainerMetricResult(
    string Name,
    string Service,
    string Image,
    string State,
    string Health,
    int RestartCount,
    double CpuPercent,
    long MemoryUsageBytes,
    long MemoryLimitBytes,
    long NetworkRxBytes,
    long NetworkTxBytes,
    long DiskReadBytes,
    long DiskWriteBytes);

public sealed record DockerVolumeSummaryResult(
    string Name,
    string Driver,
    long SizeBytes,
    string Mountpoint);

public sealed record DashboardDockerHostResult(
    string Status,
    string Error,
    DockerHostSummaryResult Host);

public sealed record DashboardDependencyResourcesResult(
    string Status,
    string Error,
    IReadOnlyList<DockerContainerMetricResult> Containers,
    IReadOnlyList<DockerVolumeSummaryResult> Volumes);

public sealed record DashboardResourceSampleResult(
    DateTimeOffset TimestampUtc,
    double CpuPercent,
    double MemoryPercent,
    long MemoryUsageBytes,
    double NetworkRxBytesPerSecond,
    double NetworkTxBytesPerSecond,
    double DiskReadBytesPerSecond,
    double DiskWriteBytesPerSecond,
    int InboundRequests,
    int OutboundRequests);

public sealed record DashboardOverviewResult(
    string Namespace,
    string BuildVersion,
    DateTimeOffset BuildTimestampUtc,
    string EmbeddingProfile,
    string ModelKey,
    int Dimensions,
    int MaxTokens,
    long CacheVersion,
    IReadOnlyList<DashboardServiceHealthResult> Services,
    IReadOnlyList<DashboardOverviewMetricResult> Metrics,
    IReadOnlyList<RequestTrafficSampleResult> RequestTraffic,
    IReadOnlyList<JobListItemResult> ActiveJobs,
    IReadOnlyList<LogEntryResult> RecentErrors,
    DateTimeOffset SnapshotAtUtc,
    DashboardPageSnapshotStatusResult? SnapshotStatus = null,
    DashboardDockerHostResult? DockerHost = null,
    DashboardDependencyResourcesResult? DependencyResources = null,
    IReadOnlyList<DashboardResourceSampleResult>? ResourceSamples = null);

public sealed record DashboardRuntimeParameterResult(
    string Section,
    string Key,
    string Value,
    bool IsSecret);

public sealed record DashboardRuntimeResult(
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
    IReadOnlyList<DashboardServiceHealthResult> Services,
    IReadOnlyList<DashboardRuntimeParameterResult> Parameters,
    DateTimeOffset SnapshotAtUtc,
    DashboardPageSnapshotStatusResult? SnapshotStatus = null,
    DashboardDockerHostResult? DockerHost = null,
    DashboardDependencyResourcesResult? DependencyResources = null);

public sealed record DashboardRedisTelemetryResult(
    string Status,
    string Warning,
    long UsedMemoryBytes,
    long MaxMemoryBytes,
    long KeyCount,
    long TotalCommandsProcessed,
    long TotalNetInputBytes,
    long TotalNetOutputBytes,
    double InstantaneousInputKbps,
    double InstantaneousOutputKbps,
    long ExpiredKeys,
    long EvictedKeys,
    long NetworkRxBytes,
    long NetworkTxBytes,
    long DiskReadBytes,
    long DiskWriteBytes,
    long PersistentStorageBytes,
    string PersistentStorageName);

public sealed record DashboardPostgresTelemetryResult(
    string Status,
    string Warning,
    int ActiveConnections,
    long TransactionsCommitted,
    long TransactionsRolledBack,
    long BlocksRead,
    long BlocksHit,
    long RowsReturned,
    long RowsFetched,
    long RowsInserted,
    long RowsUpdated,
    long RowsDeleted,
    long TempFiles,
    long TempBytes,
    long Deadlocks,
    long NetworkRxBytes,
    long NetworkTxBytes,
    long DiskReadBytes,
    long DiskWriteBytes,
    long PersistentStorageBytes,
    string PersistentStorageName,
    long DatabaseSizeBytes);

public sealed record DashboardMonitoringResult(
    string Namespace,
    string BuildVersion,
    DateTimeOffset BuildTimestampUtc,
    IReadOnlyList<DashboardServiceHealthResult> Services,
    DateTimeOffset SnapshotAtUtc,
    DashboardRedisTelemetryResult Redis,
    DashboardPostgresTelemetryResult Postgres,
    DashboardPageSnapshotStatusResult? SnapshotStatus = null,
    DashboardDockerHostResult? DockerHost = null,
    DashboardDependencyResourcesResult? DependencyResources = null,
    IReadOnlyList<DashboardResourceSampleResult>? ResourceSamples = null);

public sealed record RuntimeConfigurationResult(
    string Namespace,
    string EmbeddingProvider,
    string ExecutionProvider,
    string EmbeddingProfile,
    string ModelKey,
    int Dimensions,
    int MaxTokens,
    int InferenceThreads,
    int BatchSize,
    bool BatchingEnabled);

public sealed record MemoryListRequest(
    string? Query = null,
    MemoryScope? Scope = null,
    MemoryType? MemoryType = null,
    MemoryStatus? Status = null,
    string? SourceType = null,
    string? Tag = null,
    string? ProjectId = null,
    string? ProjectQuery = null,
    IReadOnlyList<string>? IncludedProjectIds = null,
    MemoryQueryMode QueryMode = MemoryQueryMode.CurrentOnly,
    bool UseSummaryLayer = false,
    int Page = 1,
    int PageSize = 25);

public sealed record ProjectSuggestionResult(
    string ProjectId,
    int ItemCount);

public sealed record MemoryListItemResult(
    Guid Id,
    string ProjectId,
    string ExternalKey,
    MemoryScope Scope,
    MemoryType MemoryType,
    string Title,
    string Summary,
    string SourceType,
    string SourceRef,
    IReadOnlyList<string> Tags,
    decimal Importance,
    decimal Confidence,
    int Version,
    MemoryStatus Status,
    DateTimeOffset UpdatedAt,
    bool IsReadOnly = false);

public sealed record MemoryRevisionResult(
    Guid Id,
    int Version,
    string Title,
    string Summary,
    string ChangedBy,
    DateTimeOffset CreatedAt);

public sealed record MemoryVectorResult(
    Guid Id,
    string ModelKey,
    int Dimension,
    string Status,
    DateTimeOffset CreatedAt);

public sealed record MemoryChunkResult(
    Guid Id,
    ChunkKind ChunkKind,
    int ChunkIndex,
    string ChunkText,
    string MetadataJson,
    DateTimeOffset CreatedAt,
    IReadOnlyList<MemoryVectorResult> Vectors);

public sealed record MemoryDetailsResult(
    MemoryDocument Document,
    IReadOnlyList<MemoryRevisionResult> Revisions,
    IReadOnlyList<MemoryChunkResult> Chunks);

public sealed record MemoryExportRequest(
    string? Query = null,
    MemoryScope? Scope = null,
    MemoryType? MemoryType = null,
    MemoryStatus? Status = null,
    string? SourceType = null,
    string? Tag = null,
    string ProjectId = ProjectContext.DefaultProjectId,
    IReadOnlyList<string>? IncludedProjectIds = null,
    MemoryQueryMode QueryMode = MemoryQueryMode.CurrentOnly,
    bool UseSummaryLayer = false,
    string? Passphrase = null);

public sealed record MemoryTransferDownloadResult(
    string FileName,
    string ContentType,
    string PayloadBase64,
    int ItemCount,
    bool Encrypted);

public sealed record MemoryImportRequest(
    string PackageBase64,
    string? Passphrase = null,
    bool ForceOverwrite = false,
    string? TargetProjectId = null);

public sealed record MemoryImportConflictResult(
    string ProjectId,
    string ExternalKey,
    Guid ExistingId,
    string ExistingTitle,
    string IncomingTitle,
    DateTimeOffset ExistingUpdatedAt);

public sealed record MemoryImportPreviewResult(
    string Namespace,
    int TotalItems,
    int NewItems,
    int ConflictItems,
    bool Encrypted,
    bool RequiresPassphrase,
    IReadOnlyList<MemoryImportConflictResult> Conflicts);

public sealed record MemoryImportApplyResult(
    int ImportedItems,
    int OverwrittenItems,
    IReadOnlyList<Guid> MemoryIds);

public sealed record JobListRequest(
    MemoryJobStatus? Status = null,
    MemoryJobType? JobType = null,
    int Page = 1,
    int PageSize = 25);

public sealed record JobListItemResult(
    Guid Id,
    MemoryJobType JobType,
    MemoryJobStatus Status,
    string PayloadJson,
    string Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record StorageTableSummaryResult(
    string Name,
    string Description,
    int RowCount,
    IReadOnlyList<string> Columns);

public sealed record StorageRowsRequest(
    string Table,
    string? Query = null,
    string? Column = null,
    int Page = 1,
    int PageSize = 50);

public sealed record StorageRowResult(
    IReadOnlyDictionary<string, string?> Values);

public sealed record StorageTableRowsResult(
    string Table,
    string Description,
    IReadOnlyList<string> Columns,
    IReadOnlyList<string> SearchableColumns,
    string? AppliedQuery,
    string? AppliedColumn,
    PagedResult<StorageRowResult> Rows);

public interface IDashboardQueryService
{
    Task<DashboardOverviewResult> GetOverviewAsync(CancellationToken cancellationToken);
    Task<DashboardRuntimeResult> GetRuntimeAsync(CancellationToken cancellationToken);
    Task<DashboardMonitoringResult> GetMonitoringAsync(CancellationToken cancellationToken);
    Task<PagedResult<MemoryListItemResult>> GetMemoriesAsync(MemoryListRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectSuggestionResult>> GetProjectSuggestionsAsync(string? query, int limit, CancellationToken cancellationToken);
    Task<MemoryDetailsResult?> GetMemoryDetailsAsync(Guid id, CancellationToken cancellationToken);
    Task<PagedResult<JobListItemResult>> GetJobsAsync(JobListRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken);
    Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken);
}

public interface IMemoryTransferService
{
    Task<MemoryTransferDownloadResult> ExportAsync(MemoryExportRequest request, CancellationToken cancellationToken);
    Task<MemoryImportPreviewResult> PreviewImportAsync(MemoryImportRequest request, CancellationToken cancellationToken);
    Task<MemoryImportApplyResult> ApplyImportAsync(MemoryImportRequest request, CancellationToken cancellationToken);
}

public interface IStorageExplorerStore
{
    Task<IReadOnlyList<StorageTableSummaryResult>> ListTablesAsync(CancellationToken cancellationToken);
    Task<StorageTableRowsResult> GetRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken);
}

public interface IRuntimeConfigurationAccessor
{
    RuntimeConfigurationResult Current { get; }
}

public interface IServiceHealthAccessor
{
    Task<IReadOnlyList<DashboardServiceHealthResult>> GetServicesAsync(CancellationToken cancellationToken);
}

public interface IRequestTrafficSnapshotAccessor
{
    IReadOnlyList<RequestTrafficSampleResult> GetRecentSamples(int limit);
}

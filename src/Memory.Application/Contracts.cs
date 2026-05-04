using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed record MemoryUpsertRequest(
    string ExternalKey,
    MemoryScope Scope,
    MemoryType MemoryType,
    string Title,
    string Content,
    string Summary,
    string SourceType,
    string SourceRef,
    IReadOnlyList<string> Tags,
    decimal Importance,
    decimal Confidence,
    string MetadataJson = "{}",
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record MemoryUpdateRequest(
    Guid Id,
    string? Title = null,
    string? Content = null,
    string? Summary = null,
    IReadOnlyList<string>? Tags = null,
    decimal? Importance = null,
    decimal? Confidence = null,
    string? MetadataJson = null,
    string? ProjectId = null);

public sealed record MemorySearchRequest(
    string Query,
    int Limit = 10,
    bool IncludeArchived = false,
    string ProjectId = ProjectContext.DefaultProjectId,
    IReadOnlyList<string>? IncludedProjectIds = null,
    MemoryQueryMode QueryMode = MemoryQueryMode.CurrentOnly,
    bool UseSummaryLayer = false,
    RetrievalTelemetryContext? Telemetry = null);

public sealed record MemorySearchHit(
    Guid MemoryId,
    string Title,
    MemoryType MemoryType,
    MemoryScope Scope,
    decimal Score,
    string Excerpt,
    string SourceType,
    string SourceRef,
    IReadOnlyList<string> Tags,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record MemoryDocument(
    Guid Id,
    string ExternalKey,
    MemoryScope Scope,
    MemoryType MemoryType,
    string Title,
    string Content,
    string Summary,
    string SourceType,
    string SourceRef,
    IReadOnlyList<string> Tags,
    decimal Importance,
    decimal Confidence,
    int Version,
    MemoryStatus Status,
    string MetadataJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string ProjectId = ProjectContext.DefaultProjectId,
    bool IsReadOnly = false);

public sealed record WorkingContextRequest(
    string Query,
    int Limit = 5,
    int RecentLogLimit = 5,
    string ProjectId = ProjectContext.DefaultProjectId,
    IReadOnlyList<string>? IncludedProjectIds = null,
    MemoryQueryMode QueryMode = MemoryQueryMode.CurrentOnly,
    bool UseSummaryLayer = false,
    RetrievalTelemetryContext? Telemetry = null);

public sealed record RetrievalTelemetryContext(
    string EntryPoint,
    string Channel,
    string? Purpose = null,
    bool Enabled = true);

public sealed record RetrievalTelemetryHitWriteRequest(
    int Rank,
    Guid? MemoryId,
    string Title,
    string MemoryType,
    string SourceType,
    string SourceRef,
    decimal? Score,
    string Excerpt,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record RetrievalTelemetryWriteRequest(
    string ProjectId,
    string Channel,
    string EntryPoint,
    string Purpose,
    string QueryText,
    string QueryMode,
    IReadOnlyList<string> IncludedProjectIds,
    bool UseSummaryLayer,
    int Limit,
    bool CacheHit,
    int ResultCount,
    double DurationMs,
    bool Success,
    string Error,
    string MetadataJson,
    string TraceId,
    string RequestId,
    IReadOnlyList<RetrievalTelemetryHitWriteRequest> Hits);

public sealed record WorkingContextSection(
    Guid MemoryId,
    string Title,
    string Summary,
    string Excerpt,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record WorkingContextCitation(
    Guid MemoryId,
    Guid? ChunkId,
    string SourceRef,
    string Excerpt,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record WorkingContextResult(
    IReadOnlyList<WorkingContextSection> Facts,
    IReadOnlyList<WorkingContextSection> Decisions,
    IReadOnlyList<WorkingContextSection> Episodes,
    IReadOnlyList<WorkingContextSection> Artifacts,
    IReadOnlyList<LogEntryResult> RecentLogs,
    IReadOnlyList<UserPreferenceResult> UserPreferences,
    IReadOnlyList<string> SuggestedTests,
    IReadOnlyList<WorkingContextCitation> Citations);

public sealed record EnqueueReindexRequest(
    string? ModelKey = null,
    Guid? MemoryItemId = null,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record EnqueueSummaryRefreshRequest(
    string? ProjectId = null,
    IReadOnlyList<string>? IncludedProjectIds = null);

public sealed record EnqueueReindexResult(Guid JobId, MemoryJobStatus Status);
public sealed record EnqueueSummaryRefreshResult(Guid JobId, MemoryJobStatus Status);

public sealed record JobResult(
    Guid Id,
    MemoryJobType JobType,
    MemoryJobStatus Status,
    string PayloadJson,
    string Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record LogQueryRequest(
    string? Query = null,
    string? ServiceName = null,
    string? Level = null,
    string? TraceId = null,
    string? RequestId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Limit = 50,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record LogEntryResult(
    long Id,
    string ServiceName,
    string Category,
    string Level,
    string Message,
    string Exception,
    string TraceId,
    string RequestId,
    string PayloadJson,
    DateTimeOffset CreatedAt,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record PromoteLogSliceRequest(
    string Title,
    string? Query = null,
    string? ServiceName = null,
    string? TraceId = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyList<string>? Tags = null,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record UserPreferenceUpsertRequest(
    string Key,
    UserPreferenceKind Kind,
    string Title,
    string Content,
    string Rationale,
    IReadOnlyList<string>? Tags = null,
    decimal Importance = 0.95m,
    decimal Confidence = 0.95m);

public sealed record UserPreferenceListRequest(
    UserPreferenceKind? Kind = null,
    bool IncludeArchived = false,
    int Limit = 50);

public sealed record UserPreferenceArchiveRequest(
    Guid Id,
    bool Archived = true);

public sealed record UserPreferenceResult(
    Guid Id,
    string Key,
    UserPreferenceKind Kind,
    string Title,
    string Content,
    string Rationale,
    IReadOnlyList<string> Tags,
    decimal Importance,
    decimal Confidence,
    MemoryStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ChunkDraft(ChunkKind Kind, int Index, string Text, string MetadataJson);

public sealed record ChunkSearchHit(Guid MemoryId, Guid ChunkId, decimal Score, string Excerpt);

public enum EmbeddingPurpose
{
    Document,
    Query
}

public sealed record EmbeddingVector(string ModelKey, int Dimensions, float[] Values);

public sealed record BatchEmbeddingItem(string Text, EmbeddingPurpose Purpose);

public sealed record SystemStatusResult(
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
    long CacheVersion,
    DateTimeOffset UtcNow,
    DateTimeOffset SnapshotCapturedAtUtc,
    int RefreshIntervalSeconds,
    bool IsStale,
    string LastError,
    string Warning);

public enum PerformanceMeasurementMode
{
    Iterations,
    Duration
}

public sealed record PerformanceMeasureRequest(
    string Query,
    string? Document = null,
    MemoryType DocumentMemoryType = MemoryType.Artifact,
    string DocumentSourceType = "document",
    int SearchLimit = 10,
    bool IncludeArchived = false,
    int WarmupIterations = 1,
    int MeasurementIterations = 3,
    PerformanceMeasurementMode MeasurementMode = PerformanceMeasurementMode.Iterations,
    int MeasurementDurationSeconds = 0,
    int MaxMeasurementIterations = 5000);

public sealed record PerformanceMetricResult(
    string Unit,
    int Iterations,
    double AverageMilliseconds,
    double MinMilliseconds,
    double MaxMilliseconds,
    double P95Milliseconds,
    double ThroughputPerSecond);

public sealed record PerformanceMeasureResult(
    string EmbeddingProvider,
    string EmbeddingProfile,
    string ModelKey,
    int Dimensions,
    int SearchLimit,
    bool IncludeArchived,
    int WarmupIterations,
    int MeasurementIterations,
    int ChunkCount,
    int DocumentTokenEstimate,
    int KeywordHitCount,
    int VectorHitCount,
    int HybridHitCount,
    PerformanceMeasurementMode MeasurementMode,
    int RequestedMeasurementDurationSeconds,
    int MaxMeasurementIterations,
    double TotalMeasurementMilliseconds,
    PerformanceMetricResult Chunking,
    PerformanceMetricResult QueryEmbedding,
    PerformanceMetricResult DocumentEmbedding,
    PerformanceMetricResult KeywordSearch,
    PerformanceMetricResult VectorSearch,
    PerformanceMetricResult HybridSearch,
    DateTimeOffset MeasuredAtUtc);

public sealed record DashboardSnapshotPollingSettingsResult(
    int StatusCoreSeconds,
    int EmbeddingRuntimeSeconds,
    int DependenciesHealthSeconds,
    int DockerHostSeconds,
    int DependencyResourcesSeconds,
    int RecentOperationsSeconds,
    int ResourceChartSeconds);

public sealed record InstanceBehaviorSettingsResult(
    bool ConversationAutomationEnabled,
    bool HostEventIngestionEnabled,
    bool AgentSupplementalIngestionEnabled,
    int IdleThresholdMinutes,
    string PromotionMode,
    int ExcerptMaxLength,
    string DefaultProjectId,
    MemoryQueryMode DefaultQueryMode,
    bool DefaultUseSummaryLayer,
    bool SharedSummaryAutoRefreshEnabled,
    DashboardSnapshotPollingSettingsResult SnapshotPolling,
    int OverviewPollingSeconds,
    int MetricsPollingSeconds,
    int JobsPollingSeconds,
    int LogsPollingSeconds,
    int PerformancePollingSeconds);

public sealed record InstanceDashboardAuthSettingsResult(
    string AdminUsername,
    int SessionTimeoutMinutes);

public sealed record ConversationAutomationStatusResult(
    int RecentCheckpoints,
    int PendingInsights,
    int PendingPromotions,
    string LastPromotionError);

public sealed record ConversationToolCallRequest(
    string ToolName,
    string? InputSummary = null,
    string? OutputSummary = null,
    bool Success = true,
    string? SourceRef = null,
    string? ProjectId = null,
    string? ProjectName = null);

public sealed record ConversationIngestRequest(
    string ConversationId,
    string TurnId,
    ConversationEventType EventType,
    ConversationSourceKind SourceKind,
    string SourceSystem,
    string SourceRef,
    string? ProjectId = null,
    string? ProjectName = null,
    string? TaskId = null,
    string? UserMessageSummary = null,
    string? AgentMessageSummary = null,
    string? SessionSummary = null,
    string? ShortExcerpt = null,
    IReadOnlyList<ConversationToolCallRequest>? ToolCalls = null);

public sealed record ConversationIngestResult(
    Guid SessionId,
    Guid CheckpointId,
    Guid? JobId,
    string EffectiveProjectId,
    string ProjectName,
    bool AutomationScheduled);

public sealed record ConversationSessionListRequest(
    string? ProjectId = null,
    string? SourceSystem = null,
    string? ConversationId = null,
    int Limit = 50);

public sealed record ConversationSessionResult(
    Guid Id,
    string ConversationId,
    string ProjectId,
    string ProjectName,
    string TaskId,
    string SourceSystem,
    string Status,
    string LastTurnId,
    DateTimeOffset StartedAt,
    DateTimeOffset LastCheckpointAt,
    DateTimeOffset UpdatedAt);

public sealed record ConversationInsightListRequest(
    string? ProjectId = null,
    string? ConversationId = null,
    ConversationPromotionStatus? PromotionStatus = null,
    ConversationInsightType? InsightType = null,
    int Limit = 100);

public sealed record ConversationInsightResult(
    Guid Id,
    Guid SessionId,
    Guid CheckpointId,
    string ConversationId,
    string TurnId,
    string ProjectId,
    string ProjectName,
    string TaskId,
    string SourceSystem,
    ConversationSourceKind SourceKind,
    ConversationInsightType InsightType,
    string Title,
    string Content,
    string Summary,
    string SourceRef,
    IReadOnlyList<string> Tags,
    decimal Importance,
    decimal Confidence,
    string DedupKey,
    ConversationPromotionStatus PromotionStatus,
    Guid? PromotedMemoryId,
    string Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record InstanceSettingsSnapshot(
    string InstanceId,
    string Namespace,
    string ComposeProject,
    string BuildVersion,
    DateTimeOffset BuildTimestampUtc,
    int SettingsRevision,
    DateTimeOffset? SettingsUpdatedAtUtc,
    InstanceBehaviorSettingsResult Behavior,
    InstanceDashboardAuthSettingsResult DashboardAuth,
    ConversationAutomationStatusResult AutomationStatus);

public sealed record DashboardSnapshotPollingSettingsUpdateRequest(
    int StatusCoreSeconds,
    int EmbeddingRuntimeSeconds,
    int DependenciesHealthSeconds,
    int DockerHostSeconds,
    int DependencyResourcesSeconds,
    int RecentOperationsSeconds,
    int ResourceChartSeconds);

public sealed record InstanceBehaviorSettingsUpdateRequest(
    bool ConversationAutomationEnabled,
    bool HostEventIngestionEnabled,
    bool AgentSupplementalIngestionEnabled,
    int IdleThresholdMinutes,
    string PromotionMode,
    int ExcerptMaxLength,
    string DefaultProjectId,
    MemoryQueryMode DefaultQueryMode,
    bool DefaultUseSummaryLayer,
    bool SharedSummaryAutoRefreshEnabled,
    DashboardSnapshotPollingSettingsUpdateRequest SnapshotPolling,
    int OverviewPollingSeconds,
    int MetricsPollingSeconds,
    int JobsPollingSeconds,
    int LogsPollingSeconds,
    int PerformancePollingSeconds);

public sealed record InstanceDashboardAuthUpdateRequest(
    string AdminUsername,
    string? NewPassword,
    string? ConfirmPassword,
    int SessionTimeoutMinutes);

public sealed record InstanceSettingsUpdateRequest(
    InstanceBehaviorSettingsUpdateRequest Behavior,
    InstanceDashboardAuthUpdateRequest DashboardAuth);

public sealed record RestartAppContainersRequest(
    IReadOnlyList<string>? Services = null);

public sealed record RestartAppContainersResult(
    string InstanceId,
    string ComposeProject,
    IReadOnlyList<string> RestartedServices,
    IReadOnlyList<string> SkippedServices,
    DateTimeOffset RestartedAtUtc);

public sealed record DashboardAuthenticationSettings(
    string AdminUsername,
    string AdminPasswordHash,
    int SessionTimeoutMinutes);

public interface IApplicationDbContext
{
    DbSet<InstanceSetting> InstanceSettings { get; }
    DbSet<MemoryItem> MemoryItems { get; }
    DbSet<MemoryItemRevision> MemoryItemRevisions { get; }
    DbSet<MemoryItemChunk> MemoryItemChunks { get; }
    DbSet<MemoryChunkVector> MemoryChunkVectors { get; }
    DbSet<MemoryLink> MemoryLinks { get; }
    DbSet<MemoryJob> MemoryJobs { get; }
    DbSet<RuntimeLogEntry> RuntimeLogEntries { get; }
    DbSet<LogIngestionCheckpoint> LogIngestionCheckpoints { get; }
    DbSet<SourceConnection> SourceConnections { get; }
    DbSet<SourceSyncRun> SourceSyncRuns { get; }
    DbSet<GovernanceFinding> GovernanceFindings { get; }
    DbSet<EvaluationSuite> EvaluationSuites { get; }
    DbSet<EvaluationCase> EvaluationCases { get; }
    DbSet<EvaluationRun> EvaluationRuns { get; }
    DbSet<EvaluationRunItem> EvaluationRunItems { get; }
    DbSet<SuggestedAction> SuggestedActions { get; }
    DbSet<ConversationSession> ConversationSessions { get; }
    DbSet<ConversationCheckpoint> ConversationCheckpoints { get; }
    DbSet<ConversationInsight> ConversationInsights { get; }
    Task<int> SaveChangesAsync(CancellationToken cancellationToken = default);
}

public interface IChunkingService
{
    IReadOnlyList<ChunkDraft> Chunk(MemoryType memoryType, string sourceType, string content);
}

public interface IRetrievalTelemetryService
{
    Task RecordAsync(RetrievalTelemetryWriteRequest request, CancellationToken cancellationToken);
}

public interface IHybridSearchStore
{
    Task<IReadOnlyList<ChunkSearchHit>> SearchKeywordChunksAsync(string query, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChunkSearchHit>> SearchVectorChunksAsync(EmbeddingVector vector, int limit, CancellationToken cancellationToken);
}

public interface IVectorStore
{
    Task ReplaceChunkVectorAsync(Guid chunkId, EmbeddingVector vector, CancellationToken cancellationToken);
}

public interface IEmbeddingProvider
{
    string ProviderName { get; }
    string ExecutionProvider { get; }
    string EmbeddingProfile { get; }
    string ModelKey { get; }
    int Dimensions { get; }
    int MaxTokens { get; }
    int InferenceThreads { get; }
    int BatchSize { get; }
    bool BatchingEnabled { get; }
    Task<EmbeddingVector> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken cancellationToken);
    Task<IReadOnlyList<EmbeddingVector>> EmbedBatchAsync(IReadOnlyList<BatchEmbeddingItem> items, CancellationToken cancellationToken);
}

public interface ICacheVersionStore
{
    Task<long> GetVersionAsync(CancellationToken cancellationToken);
    Task<long> IncrementAsync(CancellationToken cancellationToken);
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken);
    Task PublishJobSignalAsync(Guid jobId, CancellationToken cancellationToken);
    Task<bool> WaitForJobSignalAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IMemoryService
{
    Task<MemoryDocument> UpsertAsync(MemoryUpsertRequest request, CancellationToken cancellationToken);
    Task<MemoryDocument> UpdateAsync(MemoryUpdateRequest request, CancellationToken cancellationToken);
    Task<MemoryDocument?> GetAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<MemorySearchHit>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken);
    Task<WorkingContextResult> BuildWorkingContextAsync(WorkingContextRequest request, CancellationToken cancellationToken);
    Task<EnqueueReindexResult> EnqueueReindexAsync(EnqueueReindexRequest request, CancellationToken cancellationToken);
    Task<EnqueueSummaryRefreshResult> EnqueueSummaryRefreshAsync(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken);
    Task<JobResult?> GetJobAsync(Guid id, CancellationToken cancellationToken);
    Task<MemoryDocument> PromoteLogSliceAsync(PromoteLogSliceRequest request, CancellationToken cancellationToken);
    Task<UserPreferenceResult> UpsertUserPreferenceAsync(UserPreferenceUpsertRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserPreferenceResult>> ListUserPreferencesAsync(UserPreferenceListRequest request, CancellationToken cancellationToken);
    Task<UserPreferenceResult> ArchiveUserPreferenceAsync(UserPreferenceArchiveRequest request, CancellationToken cancellationToken);
}

public interface IConversationAutomationService
{
    Task<ConversationIngestResult> IngestAsync(ConversationIngestRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationSessionResult>> ListSessionsAsync(ConversationSessionListRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationInsightResult>> ListInsightsAsync(ConversationInsightListRequest request, CancellationToken cancellationToken);
    Task<ConversationAutomationStatusResult> GetAutomationStatusAsync(CancellationToken cancellationToken);
    Task ProcessCheckpointJobAsync(Guid checkpointId, CancellationToken cancellationToken);
    Task PromotePendingInsightsAsync(string? conversationId, string? projectId, CancellationToken cancellationToken);
}

public interface ILogQueryService
{
    Task<IReadOnlyList<LogEntryResult>> SearchAsync(LogQueryRequest request, CancellationToken cancellationToken);
    Task<LogEntryResult?> GetAsync(long id, CancellationToken cancellationToken);
}

public interface IPerformanceProbeService
{
    Task<PerformanceMeasureResult> MeasureAsync(PerformanceMeasureRequest request, CancellationToken cancellationToken);
}

public interface IBackgroundJobProcessor
{
    Task<JobResult?> ProcessNextAsync(CancellationToken cancellationToken);
}

public interface IInstanceSettingsService
{
    Task<InstanceSettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<InstanceSettingsSnapshot> UpdateAsync(InstanceSettingsUpdateRequest request, string updatedBy, CancellationToken cancellationToken);
    Task<InstanceSettingsSnapshot> ResetAsync(string updatedBy, CancellationToken cancellationToken);
    Task<DashboardAuthenticationSettings> GetDashboardAuthenticationSettingsAsync(CancellationToken cancellationToken);
}

public interface IInstanceBehaviorSettingsAccessor
{
    Task<InstanceBehaviorSettingsResult> GetCurrentAsync(CancellationToken cancellationToken);
}

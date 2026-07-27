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

public sealed record MemoryArchiveRequest(
    Guid Id,
    string? ProjectId = null,
    bool Archived = true,
    string? Reason = null);

public sealed record MemoryMoveRequest(
    Guid Id,
    string TargetProjectId,
    string? SourceProjectId = null,
    string? Reason = null);

public sealed record MemoryDeleteRequest(
    Guid Id,
    string? ProjectId = null,
    string? Reason = null);

public enum ProjectCleanupAction
{
    Archive,
    Delete
}

public sealed record ProjectCleanupPreviewRequest(
    string ProjectId,
    int Limit = 200,
    bool IncludeArchived = true);

public sealed record ProjectCleanupApplyRequest(
    string ProjectId,
    IReadOnlyList<Guid> MemoryIds,
    ProjectCleanupAction Action = ProjectCleanupAction.Delete,
    string? Reason = null);

public sealed record MemoryDeleteResult(
    Guid Id,
    string ProjectId,
    bool Deleted);

public sealed record ProjectCleanupCandidate(
    Guid Id,
    string Title,
    MemoryType MemoryType,
    MemoryStatus Status,
    IReadOnlyList<string> Tags,
    decimal Importance,
    decimal Confidence,
    string RecommendedAction,
    string Rationale,
    bool IsSafeToApply);

public sealed record ProjectCleanupPreviewResult(
    string ProjectId,
    int TotalScanned,
    IReadOnlyList<ProjectCleanupCandidate> Candidates);

public sealed record ProjectCleanupApplyResult(
    string ProjectId,
    ProjectCleanupAction Action,
    IReadOnlyList<Guid> AppliedMemoryIds,
    IReadOnlyList<Guid> SkippedMemoryIds,
    int ArchivedCount,
    int DeletedCount);

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
    string ProjectId = ProjectContext.DefaultProjectId,
    int SourceTokenEstimate = 0);

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

public enum ProjectArtifactKind
{
    Summary,
    Snippet,
    FileReference,
    ExternalObject
}

public sealed record ProjectArtifactObjectRef(
    string Provider,
    string Bucket,
    string Key,
    string? Uri = null,
    DateTimeOffset? ExpiresAt = null,
    string? Sha256 = null,
    long? SizeBytes = null,
    string? ContentType = null);

public sealed record ProjectArtifactPublishRequest(
    string ProjectId,
    string Title,
    string Summary,
    string Content,
    ProjectArtifactKind Kind = ProjectArtifactKind.Summary,
    string SourceSystem = "codex",
    string SourceRef = "",
    IReadOnlyList<string>? Tags = null,
    string? ExternalKey = null,
    ProjectArtifactObjectRef? ObjectRef = null,
    DateTimeOffset? ExpiresAt = null,
    string MetadataJson = "{}");

public sealed record ProjectArtifactManagedObjectPublishRequest(
    string ProjectId,
    string Title,
    string Summary,
    string ContentBase64,
    string FileName,
    string ContentType,
    DateTimeOffset ExpiresAt,
    string SourceSystem = "codex",
    string SourceRef = "",
    IReadOnlyList<string>? Tags = null,
    string? ExternalKey = null,
    string MetadataJson = "{}");

public sealed record ProjectArtifactListRequest(
    string ProjectId,
    string? Query = null,
    ProjectArtifactKind? Kind = null,
    string? SourceSystem = null,
    bool IncludeExpired = false,
    int Limit = 50);

public sealed record ProjectArtifactSearchRequest(
    string ProjectId,
    string Query,
    ProjectArtifactKind? Kind = null,
    string? SourceSystem = null,
    bool IncludeExpired = false,
    int Limit = 10);

public sealed record ProjectArtifactResult(
    Guid MemoryId,
    string ExternalKey,
    string ProjectId,
    ProjectArtifactKind Kind,
    string Title,
    string Summary,
    string ContentPreview,
    string SourceSystem,
    string SourceRef,
    IReadOnlyList<string> Tags,
    ProjectArtifactObjectRef? ObjectRef,
    DateTimeOffset? ExpiresAt,
    bool IsExpired,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ProjectArtifactObjectUploadRequest(
    string ProjectId,
    string FileName,
    string ContentType,
    byte[] Content,
    DateTimeOffset ExpiresAt,
    string SourceSystem,
    string SourceRef,
    IReadOnlyDictionary<string, string> Metadata);

public sealed record ProjectArtifactExpiredObjectPruneRequest(
    string? ProjectId = null,
    int Limit = 100,
    bool DryRun = false);

public sealed record ProjectArtifactExpiredObjectPruneResult(
    int ScannedCount,
    int DeletedObjectCount,
    int ArchivedArtifactCount,
    int FailedCount,
    IReadOnlyList<ProjectArtifactExpiredObjectPruneItem> Items);

public sealed record ProjectArtifactExpiredObjectPruneItem(
    Guid MemoryId,
    string ProjectId,
    string Title,
    ProjectArtifactKind Kind,
    string Bucket,
    string Key,
    DateTimeOffset? ExpiresAt,
    bool DeletedObject,
    bool ArchivedArtifact,
    string Error);

public interface IProjectArtifactObjectStore
{
    Task<ProjectArtifactObjectRef> UploadAsync(ProjectArtifactObjectUploadRequest request, CancellationToken cancellationToken);
    Task DeleteAsync(ProjectArtifactObjectRef objectRef, CancellationToken cancellationToken);
}

public sealed record WorkingContextRequest(
    string Query,
    int Limit = 5,
    int RecentLogLimit = 5,
    string ProjectId = ProjectContext.DefaultProjectId,
    IReadOnlyList<string>? IncludedProjectIds = null,
    MemoryQueryMode QueryMode = MemoryQueryMode.CurrentOnly,
    bool UseSummaryLayer = false,
    RetrievalTelemetryContext? Telemetry = null);

public enum RetrievalTelemetryDetailLevel
{
    Full,
    SummaryOnly
}

public sealed record RetrievalTelemetryContext(
    string EntryPoint,
    string Channel,
    string? Purpose = null,
    bool Enabled = true,
    RetrievalTelemetryDetailLevel DetailLevel = RetrievalTelemetryDetailLevel.Full);

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

public sealed record ProjectInformationUpdateRequest(
    string ProjectId,
    string? DisplayName,
    string Description);

public sealed record ProjectInformationResult(
    Guid MemoryId,
    string ProjectId,
    string DisplayName,
    string Description,
    DateTimeOffset UpdatedAt,
    bool IsHidden = false,
    DateTimeOffset? ArchivedAt = null,
    DateTimeOffset? SafeDeleteEligibleAt = null)
{
    public bool IsArchived => ArchivedAt is not null;
}

public enum ProjectLifecycleAction
{
    Hide,
    Unhide,
    Archive,
    Restore
}

public sealed record ProjectLifecycleUpdateRequest(
    string ProjectId,
    ProjectLifecycleAction Action);

public sealed record ProjectInformationListItem(
    ProjectInformationResult Information,
    int ItemCount);

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
    IReadOnlyList<WorkingContextCitation> Citations,
    ContextSavingsEstimateResult? SavingsEstimate = null,
    MaintenanceStatusResult? Maintenance = null,
    ProjectInformationResult? ProjectInformation = null);

public sealed record ContextHubBootstrapRequest(string? ProjectId = null);

public sealed record ContextHubBootstrapResult(
    ContextHubBootstrapServiceInfo Service,
    ContextHubBootstrapProjectInfo Project,
    ContextHubBootstrapCapabilities Capabilities,
    IReadOnlyList<string> RecommendedStartupFlow,
    ContextHubBootstrapUserPreferencesInfo UserPreferences,
    IReadOnlyList<string> Warnings);

public sealed record ContextHubBootstrapServiceInfo(
    string Name,
    string Purpose,
    string ContractVersion);

public sealed record ContextHubBootstrapProjectInfo(
    string? ProjectId,
    bool ProjectIdProvided,
    bool ProjectIdRequiredForWork,
    string Guidance,
    string? RecommendedWorkingContextCall = null);

public sealed record ContextHubBootstrapCapabilities(
    bool WorkingContext,
    bool MemorySearch,
    bool MemoryReadWrite,
    bool ConversationCheckpoint,
    bool UserPreferences,
    bool RuntimeLogs,
    bool MaintenanceStatus);

public sealed record ContextHubBootstrapUserPreferencesInfo(
    bool IncludedByDefaultInWorkingContext,
    string BootstrapDisclosure,
    IReadOnlyList<string> AvailableKinds);

public sealed record ContextSavingsEstimateResult(
    int BaselineTokenEstimate,
    int ReturnedTokenEstimate,
    int EstimatedSavedTokens,
    double EstimatedSavingPercent,
    string Confidence,
    double SourceCoveragePercent,
    int ApproxBaselineTokens = 0,
    int ApproxReturnedTokens = 0,
    int ApproxSavedTokens = 0,
    int? ExactBaselineTokens = null,
    int? ExactReturnedTokens = null,
    int? ExactSavedTokens = null,
    double ExactCoveragePercent = 0d,
    string TokenCountingMode = TokenCountingModes.Approximate);

public static class TokenCountingModes
{
    public const string Approximate = "Approximate";
    public const string Exact = "Exact";
    public const string Mixed = "Mixed";
}

public sealed record TokenCountRequest(string Text);

public sealed record TokenCountResult(
    int ApproximateTokens,
    int? ExactTokens,
    bool ExactAvailable,
    string CountingMode);

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

public sealed record MaintenanceRunResult(
    Guid Id,
    MaintenanceRunType MaintenanceType,
    MaintenanceRunStatus Status,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string TriggeredBy,
    string PolicyJson,
    string ResultJson,
    string Error);

public sealed record MaintenanceModeStateResult(
    bool Active,
    string Reason,
    string Message,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EstimatedEndsAtUtc,
    Guid? RunId,
    string TriggeredBy);

public sealed record MaintenanceModeRequest(
    string? Reason = null,
    string? Message = null,
    DateTimeOffset? EstimatedEndsAtUtc = null,
    int? EstimatedDurationMinutes = null,
    string? TriggeredBy = null);

public enum MaintenancePhase
{
    Inactive,
    Scheduled,
    Draining,
    Running,
    Completed,
    Failed,
    Cancelled
}

public sealed record MaintenanceWindowRequest(
    string? Reason = null,
    string? Message = null,
    DateTimeOffset? ScheduledStartAtUtc = null,
    DateTimeOffset? EstimatedEndsAtUtc = null,
    int? EstimatedDurationMinutes = null,
    int? MaxDrainWaitMinutes = null,
    string? TriggeredBy = null);

public sealed record MaintenanceStatusResult(
    MaintenancePhase Phase,
    bool Active,
    string Reason,
    string Message,
    DateTimeOffset? ScheduledStartAtUtc,
    DateTimeOffset? StartedAtUtc,
    DateTimeOffset? EstimatedEndsAtUtc,
    Guid? RunId,
    string TriggeredBy,
    int MaxDrainWaitMinutes,
    int ActiveLeaseCount,
    IReadOnlyList<MaintenanceLeaseResult> ActiveLeases);

public sealed record MaintenanceLeaseHeartbeatRequest(
    Guid? LeaseId = null,
    string? AgentId = null,
    string? ProjectId = null,
    string? ConversationId = null,
    string? TaskId = null,
    string? ActivityKind = null,
    int? TtlSeconds = null,
    bool BlocksMaintenance = true);

public sealed record MaintenanceLeaseCompleteRequest(Guid LeaseId);

public sealed record MaintenanceLeaseResult(
    Guid LeaseId,
    string AgentId,
    string ProjectId,
    string ConversationId,
    string TaskId,
    string ActivityKind,
    bool BlocksMaintenance,
    DateTimeOffset LastSeenAtUtc,
    DateTimeOffset ExpiresAtUtc);

public sealed record MaintenanceLeaseHeartbeatResult(
    MaintenanceLeaseResult Lease,
    MaintenanceStatusResult Maintenance);

public sealed record RetrievalTelemetryRetentionRunRequest(
    string? TriggeredBy = null,
    int? BatchSize = null,
    int? EventBatchSize = null,
    int? TimeWindowDays = null,
    int? DelayBetweenBatchesMs = null,
    int? CommandTimeoutSeconds = null,
    int? MaxDurationMinutes = null,
    bool? RunVacuumAnalyzeAfterRetention = null,
    bool? RunVacuumFullAutomatically = null);

public sealed record RetrievalTelemetryRetentionRunResult(
    Guid RunId,
    DateTimeOffset HitsCutoffUtc,
    DateTimeOffset EventsCutoffUtc,
    long DeletedHits,
    long DeletedEvents,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string ResultJson);

public enum MemoryDataRetentionRunMode
{
    Classify,
    PreviewDelete,
    ApplyAutoDelete,
    ApplyMaintenanceCleanup
}

public enum MemoryRetentionRecommendedAction
{
    Keep,
    Archive,
    Merge,
    Delete,
    Restore,
    NeedsReview
}

public sealed record MemoryDataRetentionRunRequest(
    string? TriggeredBy = null,
    MemoryDataRetentionRunMode Mode = MemoryDataRetentionRunMode.Classify,
    int? ArchivedItemsRetentionDays = null,
    int? BatchSize = null,
    int? DelayBetweenBatchesMs = null,
    int? CommandTimeoutSeconds = null,
    int? MaxDurationMinutes = null,
    bool PreviewOnly = false,
    int? HitWindowDays = null,
    long? MaxRecentHitCount = null,
    int? MaxLinkDegree = null,
    decimal? MaxImportance = null,
    decimal? MaxConfidence = null,
    int? PreviewLimit = null,
    bool IncludeCandidateDetails = true,
    IReadOnlyList<string>? ProjectIds = null,
    Guid? TenantId = null,
    int? RevisionRetentionDays = null,
    int? MinRevisionsToKeep = null,
    int? MaxChunksPerMemoryItem = null);

public sealed record MemoryDataRetentionPolicyThresholds(
    int ArchivedItemsRetentionDays,
    int HitWindowDays,
    long MaxRecentHitCount,
    int MaxLinkDegree,
    decimal MaxImportance,
    decimal MaxConfidence,
    int PreviewLimit,
    int RevisionRetentionDays,
    int MinRevisionsToKeep,
    int MaxChunksPerMemoryItem);

public sealed record MemoryDataRetentionCandidateResult(
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
    MemoryRetentionRecommendedAction RecommendedAction,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> BlockedReasons);

public sealed record MemoryDataRetentionRunResult(
    Guid RunId,
    DateTimeOffset CutoffUtc,
    long DeletedMemoryItems,
    long DeletedLinks,
    long DeletedRevisions,
    long DeletedChunks,
    long DeletedVectors,
    IReadOnlyList<string> AffectedProjectIds,
    bool PreviewOnly,
    MemoryDataRetentionRunMode Mode,
    MemoryDataRetentionPolicyThresholds PolicyThresholds,
    long AutoDeleteCandidateCount,
    long ReviewCandidateCount,
    IReadOnlyList<MemoryDataRetentionCandidateResult> AutoDeleteCandidates,
    IReadOnlyList<MemoryDataRetentionCandidateResult> ReviewCandidates,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> BlockedReasons,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string ResultJson);

public sealed record VacuumFullReclaimRunRequest(
    string? TriggeredBy = null);

public sealed record VacuumFullReclaimRunResult(
    Guid RunId,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    string ResultJson);

public sealed record DomainOwnerRepairRequest(
    bool Apply = false,
    Guid? AdminTenantId = null,
    Guid? AdminUserId = null,
    bool IncludeSmallTables = true,
    bool IncludeRetrievalEvents = false,
    int? RetrievalEventBatchSize = null,
    int? MaxRetrievalEventBatches = null,
    int? CommandTimeoutSeconds = null,
    string? TriggeredBy = null);

public sealed record DomainOwnerDistributionResult(
    string TableName,
    Guid? TenantId,
    Guid? OwnerUserId,
    long RowCount);

public sealed record DomainOwnerConflictResult(
    string ProjectId,
    string ExternalKey,
    long RowCount,
    IReadOnlyList<Guid> MemoryIds);

public sealed record DomainOwnerRepairTableResult(
    string TableName,
    long UpdatedRows);

public sealed record DomainOwnerRepairResult(
    Guid? RunId,
    bool Applied,
    Guid AdminTenantId,
    Guid AdminUserId,
    IReadOnlyList<DomainOwnerDistributionResult> DistributionBefore,
    IReadOnlyList<DomainOwnerDistributionResult> DistributionAfter,
    IReadOnlyList<DomainOwnerConflictResult> Conflicts,
    IReadOnlyList<DomainOwnerRepairTableResult> TableResults,
    IReadOnlyList<string> AffectedProjectIds,
    string ResultJson);

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

public sealed record MemorySearchScope(IReadOnlyList<string>? ProjectIds = null)
{
    public static MemorySearchScope Unscoped { get; } = new();

    public IReadOnlyList<string> NormalizedProjectIds { get; } = (ProjectIds ?? Array.Empty<string>())
        .Select(projectId => ProjectContext.Normalize(projectId))
        .Where(projectId => !string.IsNullOrWhiteSpace(projectId))
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();
}

public enum EmbeddingPurpose
{
    Document,
    Query
}

public sealed record EmbeddingVector(string ModelKey, int Dimensions, float[] Values);

public sealed record BatchEmbeddingItem(string Text, EmbeddingPurpose Purpose, string SourceKind = "unknown");

public sealed record EmbeddingUsageTelemetryItem(
    DateTimeOffset CreatedAtUtc,
    string Provider,
    string Profile,
    EmbeddingPurpose Purpose,
    string SourceKind,
    int MaxTokens,
    int TokenCount,
    bool Truncated);

public sealed record EmbeddingUsageWindowResult(
    string Key,
    string Label,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset EndedAtUtc,
    long TotalInputs,
    long TruncatedInputs,
    double TruncationRatePercent,
    int ApproxP95TokenCount,
    int MaxTokenCount,
    IReadOnlyList<EmbeddingUsageGroupResult> TopGroups);

public sealed record EmbeddingUsageGroupResult(
    string ServiceName,
    string Provider,
    string Profile,
    string Purpose,
    string SourceKind,
    int MaxTokens,
    long TotalInputs,
    long TruncatedInputs,
    double TruncationRatePercent,
    int ApproxP95TokenCount,
    int MaxTokenCount);

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
    int ResourceChartSeconds,
    int MemoryGraphIndexSeconds = 15);

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

public sealed record TenantCreateRequest(
    string Slug,
    string DisplayName);

public sealed record TenantResult(
    Guid Id,
    string Slug,
    string DisplayName,
    TenantStatus Status,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TenantUserCreateRequest(
    Guid TenantId,
    string Username,
    string DisplayName,
    string Email,
    TenantUserRole Role = TenantUserRole.Member,
    string PasswordHash = "");

public sealed record TenantUserUpdateRequest(
    string? DisplayName = null,
    string? Email = null,
    TenantUserRole? Role = null,
    TenantUserStatus? Status = null,
    string? PasswordHash = null);

public sealed record TenantUserResult(
    Guid Id,
    Guid TenantId,
    string Username,
    string DisplayName,
    string Email,
    TenantUserRole Role,
    TenantUserStatus Status,
    DateTimeOffset? LastLoginAt,
    DateTimeOffset? PasswordUpdatedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record TenantProjectGrantUpsertRequest(
    Guid TenantId,
    string ProjectId,
    bool CanRead = true,
    bool CanWrite = false,
    bool CanManageTokens = false);

public sealed record TenantProjectGrantResult(
    Guid Id,
    Guid TenantId,
    string ProjectId,
    bool CanRead,
    bool CanWrite,
    bool CanManageTokens,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApiTokenCreateRequest(
    Guid TenantId,
    Guid OwnerUserId,
    string Name,
    string? Notes = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? AllowedProjectIds = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record ApiTokenUpdateRequest(
    string? Name = null,
    string? Notes = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? AllowedProjectIds = null,
    DateTimeOffset? ExpiresAt = null);

public sealed record ApiTokenResult(
    Guid Id,
    Guid TenantId,
    Guid OwnerUserId,
    string Name,
    string Notes,
    string TokenPrefix,
    string TokenLastFour,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> AllowedProjectIds,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    DateTimeOffset? LastUsedAt,
    string LastUsedIp,
    string LastUsedUserAgent,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApiTokenCreatedResult(
    ApiTokenResult Token,
    string PlainToken);

public sealed record ApiTokenAuthenticationResult(
    bool Succeeded,
    string FailureReason,
    Guid? TenantId = null,
    Guid? OwnerUserId = null,
    Guid? ApiTokenId = null,
    string? TenantSlug = null,
    string? Username = null,
    TenantUserRole? Role = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? AllowedProjectIds = null);

public sealed record CurrentUserResult(
    Guid TenantId,
    Guid UserId,
    string Username,
    string DisplayName,
    string Email,
    TenantUserRole Role);

public sealed record SecurityAuditEventResult(
    Guid Id,
    Guid? TenantId,
    Guid? ActorUserId,
    Guid? ApiTokenId,
    SecurityAuditEventType EventType,
    string Outcome,
    string IpAddress,
    string UserAgent,
    string DetailsJson,
    DateTimeOffset CreatedAt);

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

public sealed record AccessibleProjectResult(
    string ProjectId,
    bool CanRead,
    bool CanWrite);

public sealed record DailyMemoryReviewResult(
    IReadOnlyList<AccessibleProjectResult> Projects,
    MemoryDataRetentionRunResult Retention,
    IReadOnlyList<ConversationInsightResult> HighSignalConversationInsights,
    IReadOnlyList<SuggestedActionResult> PendingSuggestedActions,
    IReadOnlyList<UserPreferenceResult> UserPreferences,
    IReadOnlyList<ChatGptProposalResult> PendingProposals);

public sealed record KnowledgeReviewRequest(IReadOnlyList<string>? ProjectIds = null, int LimitPerSection = 100);
public sealed record KnowledgeReviewResult(
    IReadOnlyList<AccessibleProjectResult> Projects,
    MemoryDataRetentionRunResult ProjectKnowledge,
    IReadOnlyList<MemoryDataRetentionCandidateResult> SharedKnowledgeCandidates,
    IReadOnlyList<UserPreferenceResult> UserPreferences,
    IReadOnlyList<DiscussionThreadResult> Discussions,
    IReadOnlyList<ProjectWorkItemResult> WorkItems,
    IReadOnlyList<ConversationInsightResult> HighSignalConversationInsights,
    IReadOnlyList<SuggestedActionResult> PendingSuggestedActions,
    IReadOnlyList<ChatGptProposalResult> PendingProposals);

public sealed record ConversationCheckpointSearchRequest(
    string? Query = null,
    string? ProjectId = null,
    string? ConversationId = null,
    int Limit = 20);

public sealed record ConversationCheckpointSearchResult(
    Guid Id,
    Guid SessionId,
    string ConversationId,
    string TurnId,
    string ProjectId,
    string ProjectName,
    string TaskId,
    string SourceSystem,
    ConversationEventType EventType,
    ConversationSourceKind SourceKind,
    string SourceRef,
    string ShortExcerpt,
    DateTimeOffset CreatedAt,
    string PipelineStatus,
    int InsightCount,
    ConversationPromotionStatus? PromotionStatus,
    Guid? PromotedMemoryId,
    string Error);

public sealed record ConversationPipelineJobResult(
    Guid Id,
    MemoryJobType JobType,
    MemoryJobStatus Status,
    string Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record ConversationPipelineStatusResult(
    Guid CheckpointId,
    Guid SessionId,
    string ConversationId,
    string TurnId,
    string ProjectId,
    string ProjectName,
    string TaskId,
    string SourceSystem,
    ConversationEventType EventType,
    ConversationSourceKind SourceKind,
    string SourceRef,
    string ShortExcerpt,
    DateTimeOffset CreatedAt,
    string PipelineStatus,
    ConversationPipelineJobResult? IngestJob,
    ConversationPipelineJobResult? PromotionJob,
    IReadOnlyList<ConversationInsightResult> Insights);

public sealed record ConversationPromotionRetryRequest(
    string? ConversationId = null,
    string? ProjectId = null);

public sealed record ConversationPromotionRetryResult(
    string? ConversationId,
    string? ProjectId,
    ConversationAutomationStatusResult AutomationStatus);

public enum ChatGptProposalStatus
{
    Pending,
    Applied,
    Rejected,
    Failed
}

public sealed record ChatGptProposalCreateRequest(
    string ToolName,
    string ProjectId,
    string PayloadJson,
    string Title,
    string Summary,
    string OAuthSubject = "",
    string OAuthEmail = "",
    string OAuthName = "");

public sealed record ChatGptProposalListRequest(
    string? ProjectId = null,
    ChatGptProposalStatus? Status = null,
    int Limit = 50);

public sealed record ChatGptProposalDecisionRequest(
    Guid ProposalId,
    string Note = "");

public sealed record ChatGptProposalResult(
    Guid Id,
    string ToolName,
    ChatGptProposalStatus Status,
    string ProjectId,
    string ProjectName,
    string Title,
    string Summary,
    string PayloadJson,
    string OAuthSubject,
    string OAuthEmail,
    string OAuthName,
    Guid? AppliedResourceId,
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
    int ResourceChartSeconds,
    int MemoryGraphIndexSeconds = 15);

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

public enum AgentConnectivityTelemetryProfile
{
    Off,
    Minimal,
    Balanced,
    Aggressive,
    Custom
}

public sealed record AgentConnectivityObservationWriteRequest(
    string AgentId,
    string AgentName,
    string AgentVersion,
    string BridgeVersion,
    string EndpointHost,
    string Transport,
    string McpMethod,
    string? ToolName,
    int Attempt,
    bool Success,
    int? StatusCode,
    string? ErrorKind,
    double ClientElapsedMs,
    double? ServerElapsedMs,
    bool SessionWasInitialized,
    bool ReconnectAttempted,
    string? CorrelationId,
    string? Source,
    DateTimeOffset ObservedAtUtc);

public sealed record AgentConnectivityObservationBatchRequest(
    string ProjectId,
    IReadOnlyList<AgentConnectivityObservationWriteRequest> Observations);

public sealed record AgentConnectivityIngestResult(
    int Accepted,
    int Rejected,
    DateTimeOffset RecordedAtUtc);

public sealed record AgentConnectivitySettingsResult(
    bool Enabled,
    AgentConnectivityTelemetryProfile Profile,
    double SuccessSampleRate,
    double FailureSampleRate,
    int ProbeIntervalSeconds,
    int UploadIntervalSeconds,
    int MaxBatchSize,
    int MaxSamplesPerAgentMethodPerMinute,
    int RawRetentionDays,
    int SummaryRetentionDays);

public sealed record AgentConnectivityStatusResult(
    string ProjectId,
    AgentConnectivityStatus Status,
    DateTimeOffset? LastObservedAtUtc,
    int RecentSampleCount,
    int RecentFailureCount,
    double? RecentFailureRate,
    double? RecentP95ClientElapsedMs,
    string Message);

public sealed record AgentConnectivitySummaryQuery(
    string? ProjectId = null,
    string? AgentId = null,
    string? McpMethod = null,
    DateTimeOffset? FromUtc = null,
    DateTimeOffset? ToUtc = null,
    int Limit = 200);

public sealed record AgentConnectivitySummaryResult(
    DateTimeOffset BucketStartUtc,
    int BucketMinutes,
    string ProjectId,
    string AgentId,
    string EndpointHost,
    string Transport,
    string McpMethod,
    string ToolName,
    int SampleCount,
    int SuccessCount,
    int FailureCount,
    int TimeoutCount,
    int AuthFailureCount,
    int ReconnectCount,
    double AvgClientElapsedMs,
    double P95ClientElapsedMs,
    double MaxClientElapsedMs,
    DateTimeOffset LastObservedAtUtc,
    AgentConnectivityStatus Status);

public sealed record AgentConnectivityRecentObservationResult(
    Guid Id,
    string ProjectId,
    string AgentId,
    string EndpointHost,
    string McpMethod,
    string ToolName,
    bool Success,
    int? StatusCode,
    string ErrorKind,
    double ClientElapsedMs,
    bool ReconnectAttempted,
    string CorrelationId,
    DateTimeOffset ObservedAtUtc);

public interface IApplicationDbContext
{
    DbSet<InstanceSetting> InstanceSettings { get; }
    DbSet<AgentConnectivityObservation> AgentConnectivityObservations { get; }
    DbSet<AgentConnectivitySummary> AgentConnectivitySummaries { get; }
    DbSet<Tenant> Tenants { get; }
    DbSet<TenantUser> TenantUsers { get; }
    DbSet<TenantProjectGrant> TenantProjectGrants { get; }
    DbSet<ApiToken> ApiTokens { get; }
    DbSet<SecurityAuditEvent> SecurityAuditEvents { get; }
    DbSet<MemoryItem> MemoryItems { get; }
    DbSet<MemoryItemRevision> MemoryItemRevisions { get; }
    DbSet<MemoryItemChunk> MemoryItemChunks { get; }
    DbSet<MemoryChunkVector> MemoryChunkVectors { get; }
    DbSet<MemoryLink> MemoryLinks { get; }
    DbSet<MemoryJob> MemoryJobs { get; }
    DbSet<MaintenanceRun> MaintenanceRuns { get; }
    DbSet<RetrievalEvent> RetrievalEvents { get; }
    DbSet<RetrievalHit> RetrievalHits { get; }
    DbSet<RetrievalTelemetryDailySummary> RetrievalTelemetryDailySummaries { get; }
    DbSet<RetrievalTelemetryDailyHitSummary> RetrievalTelemetryDailyHitSummaries { get; }
    DbSet<EmbeddingUsageHourly> EmbeddingUsageHourly { get; }
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
    DbSet<ProjectHierarchy> ProjectHierarchies { get; }
    DbSet<DiscussionThread> DiscussionThreads { get; }
    DbSet<DiscussionParticipant> DiscussionParticipants { get; }
    DbSet<DiscussionMessage> DiscussionMessages { get; }
    DbSet<ProjectWorkItem> ProjectWorkItems { get; }
    DbSet<ProjectWorkItemChecklistItem> ProjectWorkItemChecklistItems { get; }
    void ClearTrackedChanges();
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

public interface IEmbeddingUsageTelemetry
{
    Task RecordAsync(IReadOnlyList<EmbeddingUsageTelemetryItem> items, CancellationToken cancellationToken);
    Task<IReadOnlyList<EmbeddingUsageWindowResult>> GetWindowsAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken);
}

public interface IAgentConnectivityService
{
    Task<AgentConnectivityIngestResult> IngestAsync(AgentConnectivityObservationBatchRequest request, CancellationToken cancellationToken);
    AgentConnectivitySettingsResult GetSettings();
    Task<AgentConnectivityStatusResult> GetStatusAsync(string? projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentConnectivitySummaryResult>> GetSummariesAsync(AgentConnectivitySummaryQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentConnectivityRecentObservationResult>> GetRecentAsync(string? projectId, string? agentId, int limit, CancellationToken cancellationToken);
}

public interface IHybridSearchStore
{
    Task<IReadOnlyList<ChunkSearchHit>> SearchKeywordChunksAsync(string query, int limit, MemorySearchScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChunkSearchHit>> SearchVectorChunksAsync(EmbeddingVector vector, int limit, MemorySearchScope scope, CancellationToken cancellationToken);
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

public interface ITokenCountingService
{
    Task<IReadOnlyList<TokenCountResult>> CountAsync(IReadOnlyList<TokenCountRequest> requests, CancellationToken cancellationToken);
}

public interface ICacheVersionStore
{
    Task<long> GetVersionAsync(CancellationToken cancellationToken);
    Task<CacheVersionStamp> GetVersionStampAsync(
        IReadOnlyList<string> projectIds,
        ContextHubRequestActor actor,
        bool includeShared,
        CancellationToken cancellationToken);
    Task<long> IncrementAsync(CancellationToken cancellationToken);
    Task<long> IncrementProjectAsync(string projectId, CancellationToken cancellationToken);
    Task<long> IncrementUserAsync(ContextHubRequestActor actor, CancellationToken cancellationToken);
    Task<long> IncrementSharedAsync(CancellationToken cancellationToken);
    Task<long> IncrementSecurityAsync(CancellationToken cancellationToken);
    Task<long> GetJobVersionAsync(CancellationToken cancellationToken);
    Task<long> IncrementJobsAsync(CancellationToken cancellationToken);
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken);
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken);
    Task PublishJobSignalAsync(Guid jobId, CancellationToken cancellationToken);
    Task<bool> WaitForJobSignalAsync(TimeSpan timeout, CancellationToken cancellationToken);
}

public sealed record CacheVersionStamp(
    string Value,
    long GlobalVersion,
    long SecurityVersion,
    long SharedVersion,
    long UserVersion,
    IReadOnlyDictionary<string, long> ProjectVersions);

public sealed record RedisCacheLookup<T>(bool Hit, T? Value);

public sealed record RedisCacheKindTelemetry(long Hits, long Misses, long Sets, long Bypasses, long Errors);

public sealed record RedisCacheTelemetrySnapshot(
    long Hits,
    long Misses,
    long Sets,
    long Bypasses,
    long Errors,
    IReadOnlyDictionary<string, RedisCacheKindTelemetry> Kinds);

public interface IRedisObjectCache
{
    Task<RedisCacheLookup<T>> GetAsync<T>(string key, string kind, CancellationToken cancellationToken);
    Task SetAsync<T>(string key, string kind, T value, TimeSpan ttl, CancellationToken cancellationToken);
}

public interface IRedisCacheTelemetry
{
    RedisCacheTelemetrySnapshot GetSnapshot();
}

public interface IRedisCachePolicy
{
    bool Enabled { get; }
    TimeSpan SearchTtl { get; }
    TimeSpan WorkingContextTtl { get; }
    TimeSpan EmbeddingTtl { get; }
    TimeSpan SemanticHitTtl { get; }
    TimeSpan MetadataTtl { get; }
    TimeSpan SecurityTtl { get; }
}

public interface IBackgroundJobQueue
{
    Task<Guid> EnqueueAsync(MemoryJob job, CancellationToken cancellationToken);
    Task PublishSignalAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IClock
{
    DateTimeOffset UtcNow { get; }
}

public interface IMemoryService
{
    Task<MemoryDocument> UpsertAsync(MemoryUpsertRequest request, CancellationToken cancellationToken);
    Task<MemoryDocument> UpdateAsync(MemoryUpdateRequest request, CancellationToken cancellationToken);
    Task<MemoryDocument> ArchiveAsync(MemoryArchiveRequest request, CancellationToken cancellationToken);
    Task<MemoryDocument> MoveAsync(MemoryMoveRequest request, CancellationToken cancellationToken);
    Task<MemoryDeleteResult> DeleteAsync(MemoryDeleteRequest request, CancellationToken cancellationToken);
    Task<ProjectCleanupPreviewResult> PreviewProjectCleanupAsync(ProjectCleanupPreviewRequest request, CancellationToken cancellationToken);
    Task<ProjectCleanupApplyResult> ApplyProjectCleanupAsync(ProjectCleanupApplyRequest request, CancellationToken cancellationToken);
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

public interface IProjectInformationService
{
    Task<ProjectInformationResult?> GetAsync(string projectId, CancellationToken cancellationToken);
    Task<ProjectInformationResult> UpsertAsync(ProjectInformationUpdateRequest request, CancellationToken cancellationToken);
    Task<ProjectInformationResult> UpdateLifecycleAsync(ProjectLifecycleUpdateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectInformationListItem>> ListAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetArchivedProjectIdsAsync(IReadOnlyList<string> projectIds, CancellationToken cancellationToken);
}

public interface IAccessibleProjectService
{
    Task<IReadOnlyList<AccessibleProjectResult>> ListAsync(int limit, CancellationToken cancellationToken);
}

public sealed record ProjectHierarchySetChildrenRequest(string ParentProjectId, IReadOnlyList<string> ChildProjectIds);
public sealed record ProjectHierarchyResult(string ParentProjectId, IReadOnlyList<string> ChildProjectIds, DateTimeOffset UpdatedAt);

public sealed record DiscussionThreadCreateRequest(string HostProjectId, string SenderProjectId, string Title, IReadOnlyList<string> ParticipantProjectIds, string InitialMessage);
public sealed record DiscussionMessageCreateRequest(Guid ThreadId, string SenderProjectId, string Content);
public sealed record DiscussionThreadListRequest(string? ProjectId = null, string? HostProjectId = null, string? Status = null, int Limit = 50);
public sealed record DiscussionMessageResult(Guid Id, string SenderProjectId, string Content, DateTimeOffset CreatedAt);
public sealed record DiscussionThreadResult(Guid Id, string HostProjectId, string Title, string Status, IReadOnlyList<string> ParticipantProjectIds, int UnreadCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record DiscussionThreadDetailResult(Guid Id, string HostProjectId, string Title, string Status, IReadOnlyList<string> ParticipantProjectIds, IReadOnlyList<DiscussionMessageResult> Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string ReaderProjectId = "", IReadOnlyList<Guid>? UnreadMessageIds = null);

public sealed record ProjectWorkItemCreateRequest(string ProjectId, string Title, string? Description = null, IReadOnlyList<string>? Tags = null, IReadOnlyList<string>? ChecklistItems = null, int Priority = 0, DateTimeOffset? DueAt = null);
public sealed record ProjectWorkItemUpdateRequest(Guid Id, string? Title = null, string? Description = null, IReadOnlyList<string>? Tags = null, ProjectWorkItemStatus? Status = null, int? Priority = null, DateTimeOffset? DueAt = null);
public sealed record ProjectWorkItemListRequest(string ProjectId, ProjectWorkItemStatus? Status = null, int Limit = 100);
public sealed record ProjectWorkItemChecklistItemResult(Guid Id, string Content, bool IsCompleted, int SortOrder);
public sealed record ProjectWorkItemResult(Guid Id, string ProjectId, string Title, string Description, IReadOnlyList<string> Tags, IReadOnlyList<ProjectWorkItemChecklistItemResult> ChecklistItems, ProjectWorkItemStatus Status, int Priority, DateTimeOffset? DueAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt);

public interface IProjectDiscussionService
{
    Task<ProjectHierarchyResult> SetChildrenAsync(ProjectHierarchySetChildrenRequest request, CancellationToken cancellationToken);
    Task<ProjectHierarchyResult> GetChildrenAsync(string parentProjectId, CancellationToken cancellationToken);
    Task<DiscussionThreadDetailResult> CreateThreadAsync(DiscussionThreadCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiscussionThreadResult>> ListThreadsAsync(DiscussionThreadListRequest request, CancellationToken cancellationToken);
    Task<DiscussionThreadDetailResult?> GetThreadAsync(Guid threadId, string? readerProjectId, CancellationToken cancellationToken);
    Task<DiscussionThreadResult?> CloseThreadAsync(Guid threadId, CancellationToken cancellationToken);
    Task<DiscussionThreadResult?> AdvanceThreadReadCursorAsync(Guid threadId, string? readerProjectId, Guid lastReadMessageId, CancellationToken cancellationToken);
    Task<DiscussionMessageResult> AddMessageAsync(DiscussionMessageCreateRequest request, CancellationToken cancellationToken);
}

public interface IProjectWorkItemService
{
    Task<ProjectWorkItemResult> CreateAsync(ProjectWorkItemCreateRequest request, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> UpdateAsync(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectWorkItemResult>> ListAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken);
}

public interface IDailyMemoryReviewService
{
    Task<DailyMemoryReviewResult> ReviewAsync(CancellationToken cancellationToken);
}

public interface IKnowledgeReviewService
{
    Task<KnowledgeReviewResult> ReviewAsync(KnowledgeReviewRequest request, CancellationToken cancellationToken);
}

public interface IProjectArtifactExchangeService
{
    Task<ProjectArtifactResult> PublishAsync(ProjectArtifactPublishRequest request, CancellationToken cancellationToken);
    Task<ProjectArtifactResult> UploadManagedObjectAsync(ProjectArtifactManagedObjectPublishRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectArtifactResult>> ListAsync(ProjectArtifactListRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectArtifactResult>> SearchAsync(ProjectArtifactSearchRequest request, CancellationToken cancellationToken);
    Task<ProjectArtifactResult?> GetAsync(Guid memoryId, CancellationToken cancellationToken);
    Task<ProjectArtifactExpiredObjectPruneResult> PruneExpiredObjectsAsync(ProjectArtifactExpiredObjectPruneRequest request, CancellationToken cancellationToken);
}

public interface IContextHubBootstrapService
{
    ContextHubBootstrapResult Describe(ContextHubBootstrapRequest request);
}

public interface ITenantSecurityService
{
    Task<TenantResult> CreateTenantAsync(TenantCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantResult>> ListTenantsAsync(bool includeArchived, int limit, CancellationToken cancellationToken);
    Task<TenantUserResult> CreateUserAsync(TenantUserCreateRequest request, CancellationToken cancellationToken);
    Task<TenantUserResult> UpdateUserAsync(Guid userId, TenantUserUpdateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantUserResult>> ListUsersAsync(Guid tenantId, bool includeArchived, CancellationToken cancellationToken);
    Task<TenantProjectGrantResult> UpsertProjectGrantAsync(TenantProjectGrantUpsertRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantProjectGrantResult>> ListProjectGrantsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<ApiTokenCreatedResult> CreateTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken);
    Task<ApiTokenResult> UpdateTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken);
    Task<ApiTokenCreatedResult> RegenerateTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<ApiTokenResult> RevokeTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApiTokenResult>> ListTokensAsync(Guid tenantId, bool includeRevoked, CancellationToken cancellationToken);
    Task<ApiTokenCreatedResult> CreateMyTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken);
    Task<ApiTokenResult> UpdateMyTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken);
    Task<ApiTokenCreatedResult> RegenerateMyTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<ApiTokenResult> RevokeMyTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApiTokenResult>> ListMyTokensAsync(bool includeRevoked, CancellationToken cancellationToken);
    Task<ApiTokenAuthenticationResult> AuthenticateTokenAsync(string token, string ipAddress, string userAgent, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecurityAuditEventResult>> ListAuditEventsAsync(Guid? tenantId, int limit, CancellationToken cancellationToken);
}

public interface IConversationAutomationService
{
    Task<ConversationIngestResult> IngestAsync(ConversationIngestRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationSessionResult>> ListSessionsAsync(ConversationSessionListRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationInsightResult>> ListInsightsAsync(ConversationInsightListRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ConversationCheckpointSearchResult>> SearchCheckpointsAsync(ConversationCheckpointSearchRequest request, CancellationToken cancellationToken);
    Task<ConversationPipelineStatusResult?> GetPipelineStatusAsync(Guid checkpointId, CancellationToken cancellationToken);
    Task<ConversationPipelineStatusResult> ProcessCheckpointNowAsync(Guid checkpointId, CancellationToken cancellationToken);
    Task<ConversationPromotionRetryResult> RetryPromotionAsync(ConversationPromotionRetryRequest request, CancellationToken cancellationToken);
    Task<ConversationAutomationStatusResult> GetAutomationStatusAsync(CancellationToken cancellationToken);
    Task ProcessCheckpointJobAsync(Guid checkpointId, CancellationToken cancellationToken);
    Task PromotePendingInsightsAsync(string? conversationId, string? projectId, CancellationToken cancellationToken);
}

public interface IChatGptProposalService
{
    Task<ChatGptProposalResult> CreateAsync(ChatGptProposalCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatGptProposalResult>> ListAsync(ChatGptProposalListRequest request, CancellationToken cancellationToken);
    Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken);
    Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken);
}

public interface ILogQueryService
{
    Task<IReadOnlyList<LogEntryResult>> SearchAsync(LogQueryRequest request, CancellationToken cancellationToken);
    Task<LogEntryResult?> GetAsync(long id, CancellationToken cancellationToken);
}

public interface IMaintenanceModeStore
{
    Task<MaintenanceModeStateResult> GetAsync(CancellationToken cancellationToken);
    Task<MaintenanceModeStateResult> EnableAsync(MaintenanceModeRequest request, string triggeredBy, CancellationToken cancellationToken);
    Task<MaintenanceModeStateResult> DisableAsync(string triggeredBy, CancellationToken cancellationToken);
}

public interface IMaintenanceCoordinator
{
    Task<MaintenanceStatusResult> GetStatusAsync(CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> ScheduleAsync(MaintenanceWindowRequest request, string triggeredBy, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> StartDrainAsync(Guid? runId, string triggeredBy, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> StartRunningAsync(Guid? runId, string triggeredBy, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> CompleteAsync(Guid? runId, string triggeredBy, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> CancelAsync(Guid? runId, string triggeredBy, CancellationToken cancellationToken);
    Task<MaintenanceLeaseHeartbeatResult> HeartbeatLeaseAsync(MaintenanceLeaseHeartbeatRequest request, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> CompleteLeaseAsync(MaintenanceLeaseCompleteRequest request, CancellationToken cancellationToken);
    Task EnsureWriteAllowedAsync(string operation, CancellationToken cancellationToken);
    Task<bool> CanStartBackgroundJobAsync(CancellationToken cancellationToken);
}

public sealed class MaintenanceUnavailableException(string message, MaintenanceStatusResult status) : InvalidOperationException(message)
{
    public MaintenanceStatusResult Status { get; } = status;
}

public interface IMaintenanceRunQueryService
{
    Task<IReadOnlyList<MaintenanceRunResult>> ListRunsAsync(int limit, CancellationToken cancellationToken);
}

public interface IRetrievalTelemetryRetentionService
{
    Task<RetrievalTelemetryRetentionRunResult> RunAsync(string triggeredBy, CancellationToken cancellationToken);
    Task<RetrievalTelemetryRetentionRunResult> RunAsync(RetrievalTelemetryRetentionRunRequest request, string fallbackTriggeredBy, CancellationToken cancellationToken);
}

public interface IMemoryDataRetentionService
{
    Task<MemoryDataRetentionRunResult> RunAsync(string triggeredBy, CancellationToken cancellationToken);
    Task<MemoryDataRetentionRunResult> RunAsync(MemoryDataRetentionRunRequest request, string fallbackTriggeredBy, CancellationToken cancellationToken);
}

public interface IVacuumFullReclaimService
{
    Task<VacuumFullReclaimRunResult> RunAsync(string triggeredBy, CancellationToken cancellationToken);
}

public interface IDomainOwnerRepairService
{
    Task<DomainOwnerRepairResult> RunAsync(DomainOwnerRepairRequest request, string fallbackTriggeredBy, CancellationToken cancellationToken);
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

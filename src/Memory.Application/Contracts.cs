using Microsoft.EntityFrameworkCore;
using Memory.Domain;
using System.Text.Json.Serialization;

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

public sealed record McpToolCallTelemetryWriteRequest(
    string ProjectId,
    string ServiceName,
    string ToolName,
    bool Success,
    double DurationMs);

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

public sealed record ProjectInformationAgentUpdateRequest(
    string ProjectId,
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
    ContextHubBootstrapToolCatalogInfo ToolCatalog,
    IReadOnlyList<string> RecommendedStartupFlow,
    ContextHubBootstrapUserPreferencesInfo UserPreferences,
    IReadOnlyList<string> Warnings);

public sealed record ContextHubBootstrapToolCatalogInfo(
    int BackendToolCount,
    int AppFacingToolCount,
    int QueryToolCount,
    int MutationToolCount,
    int DeleteCapableToolCount,
    int ProposalGatedToolCount,
    string PublishedCatalogVersion);

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
    int PreviewOffset = 0,
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
    int Limit = 50,
    int Offset = 0);

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
    int Limit = 100,
    int Offset = 0);

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
    DateTimeOffset UpdatedAt)
{
    public string GovernanceReason { get; init; } = string.Empty;
    public string GovernanceRunId { get; init; } = string.Empty;
    public int GovernanceRetryCount { get; init; }
    public DateTimeOffset? GovernanceUpdatedAt { get; init; }
    public DateTimeOffset? GovernanceBlockedAt { get; init; }
    public DateTimeOffset? GovernanceLastReevaluatedAt { get; init; }
    public string GovernanceBlockingLayer { get; init; } = string.Empty;
    public string GovernanceReasonClass { get; init; } = string.Empty;
    public string GovernanceRelatedTool { get; init; } = string.Empty;
    public bool GovernanceEvidenceChangedSinceBlock { get; init; }
}

public sealed record ConversationInsightGovernanceRequest(
    Guid InsightId,
    string? GovernanceRunId = null,
    string? Reason = null);

public sealed record ConversationInsightDispositionRequest(
    Guid InsightId,
    ConversationInsightDisposition Disposition,
    string Reason,
    string? GovernanceRunId = null,
    string? BlockingLayer = null,
    string? ReasonClass = null,
    string? RelatedTool = null);

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

public sealed record KnowledgeReviewRequest(
    IReadOnlyList<string>? ProjectIds = null,
    int LimitPerSection = 100,
    int Offset = 0,
    string? GovernanceRunId = null,
    bool IsReReview = false)
{
    [JsonIgnore]
    public GovernanceReceiptContractIdentity? ReceiptContractIdentity { get; init; }
}

public sealed record KnowledgeReviewPageResult(
    int Offset,
    int Limit,
    int ReturnedCount,
    int TotalCount,
    bool HasMore)
{
    public string? Continuation { get; init; }
}

public sealed record KnowledgeGovernanceCandidateResult(
    Guid FindingId,
    Guid MemoryId,
    Guid? RelatedMemoryId,
    string ProjectId,
    GovernanceFindingType Classification,
    string Title,
    string Summary,
    string RecommendedAction,
    string? TargetProjectId,
    IReadOnlyList<string> ReasonCodes,
    bool RequiresExplicitApproval,
    DateTimeOffset UpdatedAt);

public sealed record KnowledgeGovernanceCoverageResult(
    Guid SnapshotId,
    string SnapshotToken,
    DateTimeOffset SnapshotCreatedAt,
    int TotalCount,
    int ScannedCount,
    int ActiveCount,
    int ArchivedCount,
    int ProjectKnowledgeCount,
    int SharedKnowledgeCount,
    bool CoverageComplete,
    bool HasMore,
    string? Continuation)
{
    public int AuthorizedGovernanceDurableMemoryCount { get; init; }
    public int GovernanceCoveredDurableMemoryCount { get; init; }
    public int SystemMetadataCount { get; init; }
    public int NonRetrievalSystemMetadataCount { get; init; }
    public string ScopeContractVersion { get; init; } = DurableMemoryGovernancePolicy.ScopeContractVersion;
    public IReadOnlyList<string> GovernanceProjectIds { get; init; } = [];
    public bool CountInvariantSatisfied =>
        ScannedCount == TotalCount &&
        GovernanceCoveredDurableMemoryCount == AuthorizedGovernanceDurableMemoryCount &&
        GovernanceCoveredDurableMemoryCount == TotalCount;
}

public sealed record KnowledgeGovernanceSectionResult(
    IReadOnlyList<KnowledgeGovernanceCandidateResult> Candidates,
    KnowledgeReviewPageResult Pagination);

public enum GovernanceItemKind
{
    Project,
    ProjectHierarchy,
    Memory,
    UserPreference,
    Artifact,
    Discussion,
    WorkItem,
    ConversationInsight,
    SuggestedAction,
    Proposal,
    LogPartition,
    LogCandidate,
    Retention
}

public sealed record GovernanceReviewItem(
    string ItemKey,
    GovernanceItemKind ItemKind,
    string ProjectId,
    string Classification,
    string RecommendedAction,
    GovernanceBatchRiskLevel RiskLevel,
    bool RequiresExplicitApproval,
    Guid? AuthorityResourceId,
    IReadOnlyList<Guid> RelatedResourceIds,
    IReadOnlyList<string> ReasonCodes,
    string GovernanceRunId)
{
    public decimal SemanticConfidence { get; init; }
    public bool IsReversible { get; init; }
    public string RetentionPolicyVersion { get; init; } = string.Empty;
    public DateTimeOffset? DeleteEligibleAt { get; init; }
}

public sealed record GovernanceSurfaceCoverageResult(
    int TotalCount,
    int ScannedCount,
    int CandidateCount,
    int ActionableCount,
    int DeferredCount,
    int RequiresUserDecisionCount,
    int HostBlockedCount,
    bool HasMore,
    bool CoverageComplete);

public sealed record FullGovernanceCoverageResult(
    GovernanceSurfaceCoverageResult ProjectCoverage,
    GovernanceSurfaceCoverageResult HierarchyCoverage,
    GovernanceSurfaceCoverageResult MemoryCoverage,
    GovernanceSurfaceCoverageResult PreferenceCoverage,
    GovernanceSurfaceCoverageResult ArtifactCoverage,
    GovernanceSurfaceCoverageResult DiscussionCoverage,
    GovernanceSurfaceCoverageResult WorkItemCoverage,
    GovernanceSurfaceCoverageResult InsightCoverage,
    GovernanceSurfaceCoverageResult SuggestedActionCoverage,
    GovernanceSurfaceCoverageResult ProposalCoverage,
    GovernanceSurfaceCoverageResult LogCoverage)
{
    public bool HasMore => Surfaces.Any(x => x.HasMore);
    public bool CoverageComplete => Surfaces.All(x => x.CoverageComplete && !x.HasMore);

    private IReadOnlyList<GovernanceSurfaceCoverageResult> Surfaces =>
    [
        ProjectCoverage, HierarchyCoverage, MemoryCoverage, PreferenceCoverage, ArtifactCoverage,
        DiscussionCoverage, WorkItemCoverage, InsightCoverage, SuggestedActionCoverage,
        ProposalCoverage, LogCoverage
    ];
}

public sealed record FullGovernancePlanResult(
    IReadOnlyList<GovernanceReviewItem> Items,
    FullGovernanceCoverageResult Coverage,
    int GovernanceActionableCount,
    int BusinessWorkItemActionableCount,
    int GovernedExceptionCount)
{
    public AutonomousRetentionReviewResult Retention { get; init; } = AutonomousRetentionReviewResult.Empty;
    public int SemanticAutoResolvableCount { get; init; }
    public int RemainingHumanDecisionCount { get; init; }
}

public sealed record KnowledgeReviewPaginationResult(
    KnowledgeReviewPageResult ProjectKnowledgeCandidates,
    KnowledgeReviewPageResult SharedKnowledgeCandidates,
    KnowledgeReviewPageResult UserPreferences,
    KnowledgeReviewPageResult Discussions,
    KnowledgeReviewPageResult WorkItems,
    KnowledgeReviewPageResult HighSignalConversationInsights,
    KnowledgeReviewPageResult PendingSuggestedActions,
    KnowledgeReviewPageResult PendingProposals)
{
    public bool HasMore =>
        ProjectKnowledgeCandidates.HasMore ||
        SharedKnowledgeCandidates.HasMore ||
        UserPreferences.HasMore ||
        Discussions.HasMore ||
        WorkItems.HasMore ||
        HighSignalConversationInsights.HasMore ||
        PendingSuggestedActions.HasMore ||
        PendingProposals.HasMore;
}

public sealed record KnowledgeReviewConvergenceResult(
    string Status,
    int ActionableItemCount,
    bool RequiresReReview,
    bool IsConverged)
{
    public bool CoverageComplete { get; init; }
    public int DeferredCount { get; init; }
    public int RequiresUserDecisionCount { get; init; }
    public int HostBlockedCount { get; init; }
    public int WorkItemActionableCount { get; init; }
    public int GovernanceActionableCount { get; init; }
    public int BusinessWorkItemActionableCount { get; init; }
    public int GovernedExceptionCount { get; init; }
    public int CandidateCount { get; init; }
    public int ExecutionActionableCount { get; init; }
    public int ExcludedGovernanceTrackerCount { get; init; }
    public int ExceptionCount => DeferredCount + RequiresUserDecisionCount + HostBlockedCount;
}

public sealed record KnowledgeReviewResult(
    IReadOnlyList<AccessibleProjectResult> Projects,
    MemoryDataRetentionRunResult ProjectKnowledge,
    IReadOnlyList<MemoryDataRetentionCandidateResult> SharedKnowledgeCandidates,
    IReadOnlyList<UserPreferenceResult> UserPreferences,
    IReadOnlyList<DiscussionThreadResult> Discussions,
    IReadOnlyList<ProjectWorkItemResult> WorkItems,
    IReadOnlyList<ConversationInsightResult> HighSignalConversationInsights,
    IReadOnlyList<SuggestedActionResult> PendingSuggestedActions,
    IReadOnlyList<ChatGptProposalResult> PendingProposals,
    string GovernanceRunId,
    bool IsReReview,
    KnowledgeReviewPaginationResult Pagination,
    KnowledgeReviewConvergenceResult Convergence)
{
    public KnowledgeGovernanceCoverageResult? DurableMemoryCoverage { get; init; }
    public KnowledgeGovernanceSectionResult? ProjectKnowledgeGovernance { get; init; }
    public KnowledgeGovernanceSectionResult? SharedKnowledgeGovernance { get; init; }
    public IReadOnlyList<GovernanceReviewItem> GovernancePlan { get; init; } = [];
    public FullGovernanceCoverageResult? GovernanceCoverage { get; init; }
    public int QuarantinedCount { get; init; }
    public int DeleteEligibleCount { get; init; }
    public int DeleteMaturedCount { get; init; }
    public int AutoDeletedCount { get; init; }
    public int DeleteCancelledCount { get; init; }
    public int TombstonedCount { get; init; }
    public int SemanticAutoResolvedCount { get; init; }
    public int RemainingHumanDecisionCount { get; init; }
    public int ProtectedRetentionCount { get; init; }
    public int CandidateCount { get; init; }
    public int ExecutionActionableCount { get; init; }
    public int GovernedExceptionCount { get; init; }
    public IReadOnlyList<GovernanceExceptionStateResult> GovernedExceptionStates { get; init; } = [];
    [JsonIgnore]
    public GovernanceReceiptContractIdentity? ReceiptContractIdentity { get; init; }
}

public sealed record GovernanceExceptionStateResult(
    string Key,
    string Kind,
    string Disposition,
    int Severity);

public sealed record GovernanceExceptionDeltaResult(
    int New,
    int Resolved,
    int Unchanged,
    int Escalated);

public enum GovernanceBatchExecutionMode
{
    Scheduled,
    Interactive,
    Manual
}

public enum GovernanceBatchRiskLevel
{
    Low,
    Medium,
    High,
    Critical
}

public enum GovernanceBatchActionType
{
    Merge,
    Update,
    Move,
    Archive,
    Reindex,
    DeleteProposal,
    SuggestedActionReconcile,
    ConversationInsightDisposition,
    ProposalApply,
    Restore,
    LifecycleReconcile,
    HierarchyReconcile,
    PreferenceReconcile,
    ArtifactReconcile,
    DiscussionReconcile,
    WorkItemReconcile,
    LogPromote,
    LogArchive,
    LogRetentionProposal,
    Quarantine,
    MaturedDelete,
    SemanticReevaluate
}

public enum GovernanceBatchItemDisposition
{
    Applied,
    NoOp,
    Failed,
    Deferred,
    RequiresUserDecision,
    UnknownResult
}

public enum GovernanceBatchErrorCode
{
    None,
    ReReviewRequired,
    InvalidCursor,
    CursorExpired,
    CursorActorMismatch,
    CursorScopeMismatch,
    CursorPolicyMismatch,
    CursorSnapshotMismatch,
    ReplayPayloadMismatch,
    SchemaCapabilityMismatch,
    HostBlockedMaturedDelete
}

public sealed class GovernanceBatchException(GovernanceBatchErrorCode code, string message) : InvalidOperationException(message)
{
    public GovernanceBatchErrorCode Code { get; } = code;
}

public sealed record GovernanceBatchExecuteRequest(
    string GovernanceRunId,
    IReadOnlyList<string>? ProjectIds = null,
    string? SnapshotToken = null,
    string? Cursor = null,
    int MaxMutations = 100,
    int MaxDurationSeconds = 120,
    IReadOnlyList<GovernanceBatchActionType>? AllowedActionTypes = null,
    GovernanceBatchRiskLevel MaxRiskLevel = GovernanceBatchRiskLevel.Low,
    bool DryRun = false,
    bool AllowHardDelete = false,
    bool IsReReview = false,
    GovernanceBatchExecutionMode ExecutionMode = GovernanceBatchExecutionMode.Scheduled,
    bool AllowMaturedDelete = false,
    decimal SemanticAutoResolutionConfidenceThreshold = 0.90m,
    string? ToolContractVersion = null,
    string? SchemaHash = null)
{
    [JsonIgnore]
    public GovernanceReceiptContractIdentity? ReceiptContractIdentity { get; init; }
}

public sealed record GovernanceReceiptContractIdentity(
    string ToolContractVersion,
    string SchemaHash,
    string PublishedCatalogVersion);

public sealed record GovernanceToolContractResult(
    string ToolName,
    string ToolContractVersion,
    string SchemaHash,
    string PublishedCatalogVersion,
    IReadOnlyList<string> SupportedActions);

public sealed record GovernanceBatchItemResult(
    string ItemKey,
    string ItemKind,
    Guid ResourceId,
    string ProjectId,
    GovernanceBatchActionType? ActionType,
    GovernanceBatchItemDisposition Disposition,
    string Summary,
    string Error,
    bool Retryable,
    string CursorDisposition,
    IReadOnlyList<Guid> AuditIds,
    IReadOnlyList<Guid> ProposalIds,
    IReadOnlyList<Guid> ResourceIds)
{
    public bool IsReplay { get; init; }
    public bool SemanticAutoResolved { get; init; }
    public Guid? TombstoneId { get; init; }
}

public sealed record GovernanceBatchExecuteResult(
    int ScannedCount,
    int AttemptedCount,
    int AppliedCount,
    int NoOpCount,
    int FailedCount,
    int DeferredCount,
    int RequiresUserDecisionCount,
    int MergedCount,
    int UpdatedCount,
    int MovedCount,
    int ArchivedCount,
    int ReindexedCount,
    int DeleteProposalCount,
    string? NextCursor,
    bool HasMore,
    bool RequiresReReview,
    IReadOnlyList<GovernanceBatchItemResult> Items,
    IReadOnlyList<Guid> AuditIds,
    string SnapshotToken,
    string StoppedReason)
{
    public bool IsReplay { get; init; }
    public string GovernanceRunId { get; init; } = string.Empty;
    public long ElapsedMilliseconds { get; init; }
    public GovernanceBatchErrorCode ErrorCode { get; init; }
    public int QuarantinedCount { get; init; }
    public int DeleteEligibleCount { get; init; }
    public int DeleteMaturedCount { get; init; }
    public int AutoDeletedCount { get; init; }
    public int DeleteCancelledCount { get; init; }
    public int TombstonedCount { get; init; }
    public int SemanticAutoResolvedCount { get; init; }
    public int RemainingHumanDecisionCount { get; init; }
    public int ProtectedRetentionCount { get; init; }
    public bool Succeeded => ErrorCode == GovernanceBatchErrorCode.None;

    public static GovernanceBatchExecuteResult Failure(
        GovernanceBatchExecuteRequest request,
        GovernanceBatchException exception)
        => new(0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            request.Cursor, true, exception.Code == GovernanceBatchErrorCode.ReReviewRequired,
            [], [], request.SnapshotToken ?? string.Empty, exception.Code.ToString())
        {
            GovernanceRunId = request.GovernanceRunId,
            ErrorCode = exception.Code
        };
}

public sealed record AutonomousRetentionCandidateResult(
    Guid ResourceId,
    string ProjectId,
    string Classification,
    string RecommendedAction,
    string PolicyKind,
    string PolicyVersion,
    int GracePeriodDays,
    DateTimeOffset? QuarantinedAt,
    DateTimeOffset? DeleteEligibleAt,
    bool DeleteEligible,
    bool Matured,
    Guid? ReplacementResourceId,
    IReadOnlyList<string> ReasonCodes,
    IReadOnlyList<string> BlockedReasons);

public sealed record AutonomousRetentionReviewResult(
    IReadOnlyList<AutonomousRetentionCandidateResult> Candidates,
    int QuarantinedCount,
    int DeleteEligibleCount,
    int DeleteMaturedCount,
    int DeleteCancelledCount,
    int ProtectedRetentionCount)
{
    public static AutonomousRetentionReviewResult Empty { get; } = new([], 0, 0, 0, 0, 0);
}

public sealed record MaturedDeleteResult(
    Guid ResourceId,
    string ProjectId,
    bool Deleted,
    bool IsReplay,
    Guid TombstoneId,
    Guid AuditId,
    long DeletedRevisionCount,
    long DeletedChunkCount,
    long DeletedVectorCount,
    IReadOnlyList<string> ReasonCodes);

public sealed record ResourceTombstoneResult(
    Guid TombstoneId,
    Guid ResourceId,
    string ResourceType,
    string ProjectId,
    string ContentHash,
    string Classification,
    DateTimeOffset ArchivedAt,
    DateTimeOffset DeletedAt,
    string RetentionPolicyVersion,
    IReadOnlyList<string> ReasonCodes,
    Guid? ReplacementResourceId,
    string GovernanceRunId,
    Guid AuditId);

public sealed record GovernanceRunReceiptResult(
    Guid ReceiptId,
    string GovernanceRunId,
    string Actor,
    string ExecutionMode,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt,
    string ToolContractVersion,
    string SchemaHash,
    string PublishedCatalogVersion,
    string InitialSnapshotToken,
    string FinalSnapshotToken,
    bool CoverageComplete,
    int InitialGovernanceActionable,
    int FinalGovernanceActionable,
    int CandidateCount,
    int ExecutionActionableCount,
    int GovernedExceptionCount,
    int Applied,
    int Failed,
    int Deferred,
    int RequiresUserDecision,
    int HostBlocked,
    int Quarantined,
    int DeleteEligible,
    int DeleteMatured,
    int AutoDeleted,
    int DeleteCancelled,
    int Tombstoned,
    int SemanticAutoResolved,
    int BusinessWorkItemActionable,
    string FinalConvergenceStatus,
    string StoppedReason,
    IReadOnlyList<Guid> AuditIds,
    IReadOnlyList<string> ProjectIds,
    bool IsReplay,
    bool RunExists,
    string Status,
    bool LatestBatchReceived,
    string RequestIdentityHash,
    GovernanceBatchOutcomeResult? LatestBatch)
{
    public GovernanceExceptionDeltaResult ExceptionDelta { get; init; } = new(0, 0, 0, 0);
    public IReadOnlyList<GovernanceExceptionStateResult> GovernedExceptionStates { get; init; } = [];
}

public sealed record GovernanceBatchOutcomeResult(
    bool Received,
    bool Executed,
    string RequestIdentityHash,
    string RequestHash,
    string Status,
    string FailurePhase,
    DateTimeOffset ReceivedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    string SnapshotToken,
    int SnapshotGeneration,
    bool IsReReview,
    string CursorBefore,
    string? NextCursor,
    bool HasMore,
    bool RequiresReReview,
    string StoppedReason,
    int Scanned,
    int Attempted,
    int Applied,
    int NoOp,
    int Failed,
    int Deferred,
    int RequiresUserDecision,
    int Quarantined,
    int DeleteEligible,
    int DeleteMatured,
    int AutoDeleted,
    int DeleteCancelled,
    int Tombstoned,
    int SemanticAutoResolved,
    int RemainingHumanDecision,
    int ProtectedRetention,
    IReadOnlyList<Guid> AuditIds,
    bool IsReplay);

public sealed record GovernanceRunReceiptListRequest(
    string? ProjectId = null,
    int Limit = 50,
    int Offset = 0);

public sealed record InternalMaturedDeleteBatchResult(
    string GovernanceRunId,
    int ScannedCount,
    int DeletedCount,
    int CancelledCount,
    int FailedCount,
    IReadOnlyList<Guid> TombstoneIds,
    IReadOnlyList<Guid> AuditIds,
    IReadOnlyList<string> ProjectIds,
    string StoppedReason);

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
    string OAuthName = "",
    string? GovernanceRunId = null);

public sealed record ChatGptGovernanceProposalRequest(
    string ToolName,
    string ProjectId,
    string PayloadJson,
    string Title,
    string Summary,
    string GovernanceRunId);

public sealed record ChatGptProposalListRequest(
    string? ProjectId = null,
    ChatGptProposalStatus? Status = null,
    int Limit = 50,
    int Offset = 0);

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
    DateTimeOffset UpdatedAt,
    string GovernanceRunId = "");

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
    DbSet<KnowledgeGovernanceSnapshot> KnowledgeGovernanceSnapshots { get; }
    DbSet<GovernanceBatchRun> GovernanceBatchRuns { get; }
    DbSet<GovernanceBatchExecution> GovernanceBatchExecutions { get; }
    DbSet<MemoryRetentionState> MemoryRetentionStates { get; }
    DbSet<ResourceTombstone> ResourceTombstones { get; }
    DbSet<GovernanceRunReceipt> GovernanceRunReceipts { get; }
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

public interface IMcpToolCallTelemetryService
{
    Task RecordAsync(McpToolCallTelemetryWriteRequest request, CancellationToken cancellationToken);
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

public interface ISuggestedActionReconciliationService
{
    Task<int> ReconcileForMemoriesAsync(
        IReadOnlyCollection<Guid> memoryIds,
        IReadOnlyCollection<string> projectIds,
        CancellationToken cancellationToken);
}

public interface IProjectInformationService
{
    Task<ProjectInformationResult?> GetAsync(string projectId, CancellationToken cancellationToken);
    Task<ProjectInformationResult> UpsertAsync(ProjectInformationUpdateRequest request, CancellationToken cancellationToken);
    Task<ProjectInformationResult> UpdateFromAgentAsync(ProjectInformationAgentUpdateRequest request, CancellationToken cancellationToken);
    Task<ProjectInformationResult> UpdateLifecycleAsync(ProjectLifecycleUpdateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectInformationListItem>> ListAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<IReadOnlyList<string>> GetArchivedProjectIdsAsync(IReadOnlyList<string> projectIds, CancellationToken cancellationToken);
}

public interface IAccessibleProjectService
{
    Task<IReadOnlyList<AccessibleProjectResult>> ListAsync(int limit, CancellationToken cancellationToken);
}

public interface IGovernanceProjectScopeResolver
{
    Task<IReadOnlyList<AccessibleProjectResult>> ResolveAsync(
        IReadOnlyList<string>? requestedProjectIds,
        CancellationToken cancellationToken);
}

public sealed record ProjectHierarchySetChildrenRequest(string ParentProjectId, IReadOnlyList<string> ChildProjectIds);
public sealed record ProjectHierarchyResult(string ParentProjectId, IReadOnlyList<string> ChildProjectIds, DateTimeOffset UpdatedAt);

public sealed record DiscussionThreadCreateRequest(string HostProjectId, string SenderProjectId, string Title, IReadOnlyList<string> ParticipantProjectIds, string InitialMessage);
public sealed record DiscussionMessageCreateRequest(Guid ThreadId, string SenderProjectId, string Content);
public sealed record DiscussionThreadListRequest(string? ProjectId = null, string? HostProjectId = null, string? Status = null, int Limit = 50, bool IncludeArchived = false, int Offset = 0);
public sealed record DiscussionThreadArchiveRequest(Guid ThreadId, bool Archived = true);
public sealed record DiscussionMessageResult(Guid Id, string SenderProjectId, string Content, DateTimeOffset CreatedAt);
public sealed record DiscussionThreadResult(Guid Id, string HostProjectId, string Title, string Status, IReadOnlyList<string> ParticipantProjectIds, int UnreadCount, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? ArchivedAt = null)
{
    public bool IsArchived => ArchivedAt.HasValue;
}
public sealed record DiscussionThreadDetailResult(Guid Id, string HostProjectId, string Title, string Status, IReadOnlyList<string> ParticipantProjectIds, IReadOnlyList<DiscussionMessageResult> Messages, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string ReaderProjectId = "", IReadOnlyList<Guid>? UnreadMessageIds = null, DateTimeOffset? ArchivedAt = null)
{
    public bool IsArchived => ArchivedAt.HasValue;
}

public sealed record ProjectWorkItemCreateRequest(string ProjectId, string Title, string? Description = null, IReadOnlyList<string>? Tags = null, IReadOnlyList<string>? ChecklistItems = null, int Priority = 0, DateTimeOffset? DueAt = null);
public sealed record ProjectWorkItemUpdateRequest(Guid Id, string? Title = null, string? Description = null, IReadOnlyList<string>? Tags = null, ProjectWorkItemStatus? Status = null, int? Priority = null, DateTimeOffset? DueAt = null);
public sealed record ProjectWorkItemGovernanceExclusionRequest(Guid WorkItemId, string ProjectId, string GovernanceRunId, string Reason, bool Excluded = true);
public sealed record ProjectWorkItemGovernanceExclusionResult(string GovernanceRunId, string Reason, string Actor, DateTimeOffset UpdatedAt, DateTimeOffset? RevokedAt = null)
{
    public bool IsActive => RevokedAt is null;
}
public sealed record ProjectWorkItemListRequest(string ProjectId, ProjectWorkItemStatus? Status = null, int Limit = 100, bool IncludeArchived = false, int Offset = 0);
public sealed record ProjectWorkItemArchiveRequest(Guid WorkItemId, bool Archived = true);
public sealed record ProjectWorkItemChecklistItemResult(Guid Id, string Content, bool IsCompleted, int SortOrder);
public sealed record ProjectWorkItemResult(Guid Id, string ProjectId, string Title, string Description, IReadOnlyList<string> Tags, IReadOnlyList<ProjectWorkItemChecklistItemResult> ChecklistItems, ProjectWorkItemStatus Status, int Priority, DateTimeOffset? DueAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, DateTimeOffset? CompletedAt, DateTimeOffset? ArchivedAt = null)
{
    public bool IsArchived => ArchivedAt.HasValue;
    public IReadOnlyList<ProjectWorkItemGovernanceExclusionResult> GovernanceExclusions { get; init; } = [];
}

public interface IProjectDiscussionService
{
    Task<ProjectHierarchyResult> SetChildrenAsync(ProjectHierarchySetChildrenRequest request, CancellationToken cancellationToken);
    Task<ProjectHierarchyResult> GetChildrenAsync(string parentProjectId, CancellationToken cancellationToken);
    Task<DiscussionThreadDetailResult> CreateThreadAsync(DiscussionThreadCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiscussionThreadResult>> ListThreadsAsync(DiscussionThreadListRequest request, CancellationToken cancellationToken);
    Task<DiscussionThreadDetailResult?> GetThreadAsync(Guid threadId, string? readerProjectId, CancellationToken cancellationToken);
    Task<DiscussionThreadResult?> CloseThreadAsync(Guid threadId, CancellationToken cancellationToken);
    Task<DiscussionThreadResult?> SetThreadArchivedAsync(Guid threadId, bool archived, CancellationToken cancellationToken);
    Task<DiscussionThreadResult?> AdvanceThreadReadCursorAsync(Guid threadId, string? readerProjectId, Guid lastReadMessageId, CancellationToken cancellationToken);
    Task<DiscussionMessageResult> AddMessageAsync(DiscussionMessageCreateRequest request, CancellationToken cancellationToken);
}

public interface IProjectWorkItemService
{
    Task<ProjectWorkItemResult> CreateAsync(ProjectWorkItemCreateRequest request, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> UpdateAsync(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectWorkItemResult>> ListAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> SetArchivedAsync(Guid workItemId, bool archived, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> SetGovernanceExclusionAsync(ProjectWorkItemGovernanceExclusionRequest request, CancellationToken cancellationToken);
}

public interface IDailyMemoryReviewService
{
    Task<DailyMemoryReviewResult> ReviewAsync(CancellationToken cancellationToken);
}

public interface IKnowledgeReviewService
{
    Task<KnowledgeReviewResult> ReviewAsync(KnowledgeReviewRequest request, CancellationToken cancellationToken);
}

public interface IFullGovernancePlanService
{
    Task<FullGovernancePlanResult> BuildAsync(
        IReadOnlyList<string> projectIds,
        string governanceRunId,
        DurableMemoryGovernanceSnapshotResult memorySnapshot,
        CancellationToken cancellationToken);
}

public interface IGovernanceBatchExecutor
{
    Task<GovernanceBatchExecuteResult> ExecuteAsync(GovernanceBatchExecuteRequest request, CancellationToken cancellationToken);
}

public interface IGovernanceRunReceiptService
{
    Task RecordReviewAsync(KnowledgeReviewResult result, DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task RecordExecutionStartedAsync(GovernanceBatchExecuteRequest request, DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task RecordExecutionAsync(GovernanceBatchExecuteRequest request, GovernanceBatchExecuteResult result, DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task RecordExecutionStoppedAsync(GovernanceBatchExecuteRequest request, DateTimeOffset startedAt, string status, string stoppedReason, string failurePhase, CancellationToken cancellationToken);
    Task<GovernanceBatchExecuteResult?> GetTerminalPreExecutionReplayAsync(GovernanceBatchExecuteRequest request, CancellationToken cancellationToken);
    Task RecordInternalRetentionAsync(InternalMaturedDeleteBatchResult result, DateTimeOffset startedAt, CancellationToken cancellationToken);
    Task<GovernanceRunReceiptResult?> GetAsync(string governanceRunId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GovernanceRunReceiptResult>> ListAsync(GovernanceRunReceiptListRequest request, CancellationToken cancellationToken);
}

public interface IDurableMemoryGovernanceService
{
    Task<DurableMemoryGovernanceSnapshotResult> GetOrCreateSnapshotAsync(
        IReadOnlyList<string> projectIds,
        string governanceRunId,
        bool isReReview,
        CancellationToken cancellationToken);
    Task<DurableMemoryGovernanceSnapshotResult> GetSnapshotAsync(
        string governanceRunId,
        string snapshotToken,
        bool isReReview,
        bool requireWriteAuthorization,
        CancellationToken cancellationToken);
}

public sealed record DurableMemoryGovernanceSnapshotResult(
    KnowledgeGovernanceCoverageResult Coverage,
    IReadOnlyList<KnowledgeGovernanceCandidateResult> ProjectCandidates,
    IReadOnlyList<KnowledgeGovernanceCandidateResult> SharedCandidates)
{
    public int DeferredCount { get; init; }
    public int RequiresUserDecisionCount { get; init; }
    public int HostBlockedCount { get; init; }
    public IReadOnlyList<Guid> FindingIds { get; init; } = [];
    public IReadOnlyList<GovernanceExceptionStateResult> ExceptionStates { get; init; } = [];
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
    Task<ConversationInsightResult?> GetInsightAsync(Guid insightId, CancellationToken cancellationToken);
    Task<ConversationInsightResult> RetryInsightAsync(ConversationInsightGovernanceRequest request, CancellationToken cancellationToken);
    Task<ConversationInsightResult> SkipInsightAsync(ConversationInsightGovernanceRequest request, CancellationToken cancellationToken);
    Task<ConversationInsightResult> SetInsightDispositionAsync(ConversationInsightDispositionRequest request, CancellationToken cancellationToken);
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

public interface IAutonomousRetentionService
{
    Task<AutonomousRetentionReviewResult> ReviewAsync(
        IReadOnlyList<string> projectIds,
        string governanceRunId,
        CancellationToken cancellationToken);
    Task<AutonomousRetentionCandidateResult> QuarantineAsync(
        Guid resourceId,
        string projectId,
        string governanceRunId,
        CancellationToken cancellationToken);
    Task<MaturedDeleteResult> DeleteMaturedAsync(
        Guid resourceId,
        string projectId,
        string governanceRunId,
        CancellationToken cancellationToken);
    Task<ResourceTombstoneResult?> GetTombstoneAsync(
        Guid resourceId,
        string? projectId,
        CancellationToken cancellationToken);
}

public interface IInternalMaturedDeleteExecutor
{
    Task<InternalMaturedDeleteBatchResult> ExecuteNextBatchAsync(CancellationToken cancellationToken);
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

namespace Memory.Domain;

public enum MemoryScope
{
    User,
    Repo,
    Project,
    Task
}

public enum MemoryType
{
    Fact,
    Decision,
    Episode,
    Artifact,
    Summary,
    Preference
}

public enum UserPreferenceKind
{
    CommunicationStyle,
    EngineeringPrinciple,
    ToolingPreference,
    Constraint,
    AntiPattern
}

public enum MemoryStatus
{
    Active,
    Stale,
    Superseded,
    Archived
}

public enum ChunkKind
{
    Document,
    Code,
    Log
}

public enum VectorStatus
{
    Active,
    Superseded,
    Failed
}

public enum MemoryJobType
{
    Reindex,
    Cleanup,
    RefreshSummary,
    IngestConversation,
    PromoteConversationInsights,
    SyncSource,
    AnalyzeGovernance,
    RunEvaluation,
    ExecuteSuggestedAction
}

public enum SourceKind
{
    LocalRepo,
    LocalDocs,
    RuntimeLogRule,
    GitHubPull
}

public enum SourceSyncTrigger
{
    Manual,
    Scheduled,
    Action,
    Recovery
}

public enum SourceSyncStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public enum GovernanceFindingType
{
    DuplicateCandidate,
    ConflictCandidate,
    StaleSource,
    MissingSource,
    ReindexRequired
}

public enum GovernanceFindingStatus
{
    Open,
    Accepted,
    Dismissed,
    Resolved
}

public enum EvaluationRunStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public enum SuggestedActionType
{
    SyncSourceNow,
    ArchiveStaleMemory,
    MergeDuplicateCandidate,
    ReviewConflictCandidate,
    ReindexProject,
    RefreshSharedSummary
}

public enum SuggestedActionStatus
{
    Pending,
    Accepted,
    Dismissed,
    Executed,
    Failed
}

public enum MemoryJobStatus
{
    Pending,
    Running,
    Completed,
    Failed
}

public enum ConversationEventType
{
    TurnCompleted,
    SessionCheckpoint,
    Idle,
    ProjectSwitched,
    TaskSwitched
}

public enum ConversationSourceKind
{
    HostEvent,
    AgentSupplemental
}

public enum ConversationInsightType
{
    Fact,
    Decision,
    Artifact,
    Episode,
    PreferenceCandidate
}

public enum ConversationPromotionStatus
{
    Pending,
    Promoted,
    Skipped,
    Failed
}

public sealed class InstanceSetting
{
    public string InstanceId { get; set; } = string.Empty;
    public string SettingKey { get; set; } = string.Empty;
    public string ValueJson { get; set; } = "{}";
    public int Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public string UpdatedBy { get; set; } = "system";
}

public sealed class MemoryItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = "default";
    public string ExternalKey { get; set; } = string.Empty;
    public MemoryScope Scope { get; set; }
    public MemoryType MemoryType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string[] Tags { get; set; } = [];
    public string SourceType { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public decimal Importance { get; set; }
    public decimal Confidence { get; set; }
    public int Version { get; set; } = 1;
    public MemoryStatus Status { get; set; } = MemoryStatus.Active;
    public bool IsReadOnly { get; set; }
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<MemoryItemRevision> Revisions { get; set; } = [];
    public List<MemoryItemChunk> Chunks { get; set; } = [];
    public List<MemoryLink> OutgoingLinks { get; set; } = [];
    public List<MemoryLink> IncomingLinks { get; set; } = [];
}

public sealed class MemoryItemRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MemoryItemId { get; set; }
    public int Version { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public string ChangedBy { get; set; } = "system";
    public DateTimeOffset CreatedAt { get; set; }
    public MemoryItem? MemoryItem { get; set; }
}

public sealed class MemoryItemChunk
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid MemoryItemId { get; set; }
    public ChunkKind ChunkKind { get; set; }
    public int ChunkIndex { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public MemoryItem? MemoryItem { get; set; }
    public List<MemoryChunkVector> Vectors { get; set; } = [];
}

public sealed class MemoryChunkVector
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ChunkId { get; set; }
    public string ModelKey { get; set; } = string.Empty;
    public int Dimension { get; set; }
    public string Status { get; set; } = VectorStatus.Active.ToString();
    public DateTimeOffset CreatedAt { get; set; }
    public MemoryItemChunk? Chunk { get; set; }
}

public sealed class MemoryLink
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid FromId { get; set; }
    public Guid ToId { get; set; }
    public string LinkType { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public MemoryItem? From { get; set; }
    public MemoryItem? To { get; set; }
}

public sealed class MemoryJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = "default";
    public MemoryJobType JobType { get; set; }
    public MemoryJobStatus Status { get; set; } = MemoryJobStatus.Pending;
    public string PayloadJson { get; set; } = "{}";
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}

public sealed class RuntimeLogEntry
{
    public long Id { get; set; }
    public string ProjectId { get; set; } = "default";
    public string ServiceName { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string Level { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Exception { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class RetrievalEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = "default";
    public string Channel { get; set; } = string.Empty;
    public string EntryPoint { get; set; } = string.Empty;
    public string Purpose { get; set; } = string.Empty;
    public string QueryText { get; set; } = string.Empty;
    public string QueryHash { get; set; } = string.Empty;
    public string QueryMode { get; set; } = string.Empty;
    public string[] IncludedProjectIds { get; set; } = [];
    public bool UseSummaryLayer { get; set; }
    public int Limit { get; set; }
    public bool CacheHit { get; set; }
    public int ResultCount { get; set; }
    public double DurationMs { get; set; }
    public bool Success { get; set; }
    public string Error { get; set; } = string.Empty;
    public string TraceId { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public List<RetrievalHit> Hits { get; set; } = [];
}

public sealed class RetrievalHit
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RetrievalEventId { get; set; }
    public int Rank { get; set; }
    public Guid? MemoryId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string MemoryType { get; set; } = string.Empty;
    public string SourceType { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public decimal? Score { get; set; }
    public string Excerpt { get; set; } = string.Empty;
    public string ProjectId { get; set; } = "default";
    public RetrievalEvent? RetrievalEvent { get; set; }
}

public sealed class LogIngestionCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ServiceName { get; set; } = string.Empty;
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class SourceConnection
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = "default";
    public string Name { get; set; } = string.Empty;
    public SourceKind SourceKind { get; set; }
    public bool Enabled { get; set; } = true;
    public string ConfigJson { get; set; } = "{}";
    public string SecretJsonProtected { get; set; } = string.Empty;
    public string LastCursor { get; set; } = string.Empty;
    public DateTimeOffset? LastSuccessfulSyncAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<SourceSyncRun> SyncRuns { get; set; } = [];
}

public sealed class SourceSyncRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SourceConnectionId { get; set; }
    public string ProjectId { get; set; } = "default";
    public SourceSyncTrigger Trigger { get; set; } = SourceSyncTrigger.Manual;
    public SourceSyncStatus Status { get; set; } = SourceSyncStatus.Pending;
    public int ScannedCount { get; set; }
    public int UpsertedCount { get; set; }
    public int ArchivedCount { get; set; }
    public int ErrorCount { get; set; }
    public string CursorBefore { get; set; } = string.Empty;
    public string CursorAfter { get; set; } = string.Empty;
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public SourceConnection? SourceConnection { get; set; }
}

public sealed class GovernanceFinding
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = "default";
    public Guid? SourceConnectionId { get; set; }
    public Guid? PrimaryMemoryId { get; set; }
    public Guid? SecondaryMemoryId { get; set; }
    public GovernanceFindingType Type { get; set; }
    public GovernanceFindingStatus Status { get; set; } = GovernanceFindingStatus.Open;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
    public string DedupKey { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class EvaluationSuite
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = "default";
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<EvaluationCase> Cases { get; set; } = [];
    public List<EvaluationRun> Runs { get; set; } = [];
}

public sealed class EvaluationCase
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SuiteId { get; set; }
    public string ProjectId { get; set; } = "default";
    public string ScenarioLabel { get; set; } = string.Empty;
    public string Query { get; set; } = string.Empty;
    public string[] ExpectedMemoryIds { get; set; } = [];
    public string[] ExpectedExternalKeys { get; set; } = [];
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public EvaluationSuite? Suite { get; set; }
    public List<EvaluationRunItem> RunItems { get; set; } = [];
}

public sealed class EvaluationRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SuiteId { get; set; }
    public string ProjectId { get; set; } = "default";
    public EvaluationRunStatus Status { get; set; } = EvaluationRunStatus.Pending;
    public string EmbeddingProfile { get; set; } = string.Empty;
    public string QueryMode { get; set; } = string.Empty;
    public bool UseSummaryLayer { get; set; }
    public int TopK { get; set; } = 5;
    public decimal HitRate { get; set; }
    public decimal RecallAtK { get; set; }
    public decimal MeanReciprocalRank { get; set; }
    public double AverageLatencyMs { get; set; }
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public EvaluationSuite? Suite { get; set; }
    public List<EvaluationRunItem> Items { get; set; } = [];
}

public sealed class EvaluationRunItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public Guid CaseId { get; set; }
    public string Query { get; set; } = string.Empty;
    public string ScenarioLabel { get; set; } = string.Empty;
    public string[] ExpectedMemoryIds { get; set; } = [];
    public string[] ExpectedExternalKeys { get; set; } = [];
    public string[] HitMemoryIds { get; set; } = [];
    public string[] HitExternalKeys { get; set; } = [];
    public bool HitAtK { get; set; }
    public decimal RecallAtK { get; set; }
    public decimal ReciprocalRank { get; set; }
    public double LatencyMs { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public EvaluationRun? Run { get; set; }
    public EvaluationCase? Case { get; set; }
}

public sealed class SuggestedAction
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ProjectId { get; set; } = "default";
    public SuggestedActionType Type { get; set; }
    public SuggestedActionStatus Status { get; set; } = SuggestedActionStatus.Pending;
    public string Title { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public string Error { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? ExecutedAt { get; set; }
}

public sealed class ConversationSession
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ConversationId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = "default";
    public string ProjectName { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public string Status { get; set; } = "Active";
    public string LastTurnId { get; set; } = string.Empty;
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset LastCheckpointAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public List<ConversationCheckpoint> Checkpoints { get; set; } = [];
    public List<ConversationInsight> Insights { get; set; } = [];
}

public sealed class ConversationCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = "default";
    public string ProjectName { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public ConversationEventType EventType { get; set; }
    public ConversationSourceKind SourceKind { get; set; }
    public string SourceRef { get; set; } = string.Empty;
    public string UserMessageSummary { get; set; } = string.Empty;
    public string AgentMessageSummary { get; set; } = string.Empty;
    public string ToolCallsJson { get; set; } = "[]";
    public string SessionSummary { get; set; } = string.Empty;
    public string ShortExcerpt { get; set; } = string.Empty;
    public string DedupKey { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public ConversationSession? Session { get; set; }
    public List<ConversationInsight> Insights { get; set; } = [];
}

public sealed class ConversationInsight
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SessionId { get; set; }
    public Guid CheckpointId { get; set; }
    public string ConversationId { get; set; } = string.Empty;
    public string TurnId { get; set; } = string.Empty;
    public string ProjectId { get; set; } = "default";
    public string ProjectName { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string SourceSystem { get; set; } = string.Empty;
    public ConversationSourceKind SourceKind { get; set; }
    public ConversationInsightType InsightType { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public string Summary { get; set; } = string.Empty;
    public string SourceRef { get; set; } = string.Empty;
    public string[] Tags { get; set; } = [];
    public decimal Importance { get; set; }
    public decimal Confidence { get; set; }
    public string DedupKey { get; set; } = string.Empty;
    public ConversationPromotionStatus PromotionStatus { get; set; } = ConversationPromotionStatus.Pending;
    public Guid? PromotedMemoryId { get; set; }
    public string Error { get; set; } = string.Empty;
    public string MetadataJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public ConversationSession? Session { get; set; }
    public ConversationCheckpoint? Checkpoint { get; set; }
}

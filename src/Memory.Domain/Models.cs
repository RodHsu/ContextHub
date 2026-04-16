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
    PromoteConversationInsights
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

public sealed class LogIngestionCheckpoint
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string ServiceName { get; set; } = string.Empty;
    public DateTimeOffset LastSeenAt { get; set; }
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

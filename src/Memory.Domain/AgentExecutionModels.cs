namespace Memory.Domain;

public enum AgentExecutionStatus
{
    Ready,
    Claimed,
    Running,
    Blocked,
    FailedRetryable,
    FailedTerminal,
    Completed,
    Abandoned,
    Expired,
    Cancelled
}

public enum AgentExecutionEventType
{
    Prepared,
    Claimed,
    Started,
    Heartbeat,
    Checkpoint,
    Blocked,
    Failed,
    Completed,
    Abandoned,
    Expired,
    Cancelled,
    SkillSnapshotRevalidated
}

public sealed class AgentExecution
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid WorkItemId { get; set; }
    public Guid? ParentExecutionId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string RepositoryId { get; set; } = string.Empty;
    public string AgentType { get; set; } = string.Empty;
    public string RequiredCapabilitiesJson { get; set; } = "[]";
    public string AllowedActionsJson { get; set; } = "[]";
    public string PackageJson { get; set; } = "{}";
    public string PackageHash { get; set; } = string.Empty;
    public string PackageContextVersion { get; set; } = string.Empty;
    public string? SkillSnapshotJson { get; set; }
    public AgentExecutionStatus Status { get; set; } = AgentExecutionStatus.Ready;
    public int Priority { get; set; }
    public int Attempt { get; set; } = 1;
    public int MaxAttempts { get; set; } = 3;
    public string ClaimedByAgentId { get; set; } = string.Empty;
    public string LeaseTokenHash { get; set; } = string.Empty;
    public long LeaseVersion { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public string FailureClass { get; set; } = string.Empty;
    public string StructuredReasonJson { get; set; } = "{}";
    public DateTimeOffset EligibleAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ProjectWorkItem? WorkItem { get; set; }
    public ICollection<AgentExecutionEvent> Events { get; set; } = [];
}

public sealed class AgentExecutionEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExecutionId { get; set; }
    public AgentExecutionEventType EventType { get; set; }
    public long Sequence { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public AgentExecution? Execution { get; set; }
}

public sealed class AgentExecutionOperation
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public Guid? ExecutionId { get; set; }
    public string AgentId { get; set; } = string.Empty;
    public string Operation { get; set; } = string.Empty;
    public string IdempotencyKey { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ProtectedResultJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public AgentExecution? Execution { get; set; }
}

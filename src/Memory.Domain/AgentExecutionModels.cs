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
    SkillSnapshotRevalidated,
    ResourcesResolved,
    ResourceResolutionBlocked,
    ResourceApprovalGranted
}

public enum AgentExecutionResourceKind
{
    File,
    Credential,
    ConnectionProfile,
    Skill
}

public enum AgentExecutionResourceResolutionMode
{
    Exact,
    LogicalCurrent
}

public enum AgentExecutionResourceRetryMode
{
    ReResolve,
    ReuseSnapshot
}

public enum AgentExecutionResolutionOutcome
{
    Resolved,
    RequiresStepUp,
    RequiresExternalApproval,
    HumanDecision,
    Denied
}

public enum AgentExecutionResourceApprovalStatus
{
    Active,
    Consumed,
    Expired,
    Revoked
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
    public ICollection<AgentExecutionResolutionSnapshot> ResolutionSnapshots { get; set; } = [];
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

public sealed class AgentExecutionResolutionSnapshot
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExecutionId { get; set; }
    public int Attempt { get; set; }
    public int ResolutionSequence { get; set; }
    public AgentExecutionResourceRetryMode RetryMode { get; set; }
    public AgentExecutionResolutionOutcome Outcome { get; set; }
    public string AuthorityContextHash { get; set; } = string.Empty;
    public string SnapshotHash { get; set; } = string.Empty;
    public string EvidenceRefsJson { get; set; } = "[]";
    public DateTimeOffset ResolvedAt { get; set; }
    public AgentExecution? Execution { get; set; }
    public ICollection<AgentExecutionResolutionItem> Items { get; set; } = [];
}

public sealed class AgentExecutionResolutionItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid SnapshotId { get; set; }
    public Guid RequirementId { get; set; }
    public AgentExecutionResourceKind Kind { get; set; }
    public AgentExecutionResolutionOutcome Outcome { get; set; }
    public Guid LogicalResourceId { get; set; }
    public Guid? ResolvedVersionId { get; set; }
    public string IntegrityIdentity { get; set; } = string.Empty;
    public long AuthorityRevision { get; set; }
    public string PolicyRevision { get; set; } = string.Empty;
    public Guid? CapabilityLeaseId { get; set; }
    public DateTimeOffset? CapabilityExpiresAt { get; set; }
    public string ReasonCode { get; set; } = string.Empty;
    public string EvidenceRefsJson { get; set; } = "[]";
    public DateTimeOffset CreatedAt { get; set; }
    public AgentExecutionResolutionSnapshot? Snapshot { get; set; }
}

public sealed class AgentExecutionResourceApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ExecutionId { get; set; }
    public Guid RequirementId { get; set; }
    public int Attempt { get; set; }
    public Guid ApprovedByUserId { get; set; }
    public Guid AssertionId { get; set; }
    public long AuthorityRevision { get; set; }
    public string PolicyRevision { get; set; } = string.Empty;
    public AgentExecutionResourceApprovalStatus Status { get; set; } = AgentExecutionResourceApprovalStatus.Active;
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

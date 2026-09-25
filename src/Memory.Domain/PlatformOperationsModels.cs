namespace Memory.Domain;

public enum PlatformBackgroundMode
{
    Incremental,
    Full
}

public enum PlatformBackgroundRunStatus
{
    Pending,
    Running,
    RetryScheduled,
    Completed,
    FailedTerminal,
    Cancelled,
    DeadLetter
}

public enum PlatformBackgroundEventType
{
    Prepared,
    Claimed,
    Checkpoint,
    Retrying,
    Completed,
    Failed,
    Cancelled,
    LeaseLost
}

public enum PlatformOutboxDeliveryStatus
{
    Pending,
    Delivered,
    RetryScheduled,
    DeadLetter
}

/// <summary>
/// Immutable, sanitized authority event. Delivery state is deliberately stored separately so
/// business commits never depend on monitoring availability and the audit row remains append-only.
/// </summary>
public sealed class AuthorityOutboxEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public long Sequence { get; set; }
    public Guid? TenantId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public long AuthorityRevision { get; set; }
    public bool SecurityCritical { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
}

public sealed class PlatformOutboxDelivery
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OutboxEventId { get; set; }
    public string Consumer { get; set; } = string.Empty;
    public PlatformOutboxDeliveryStatus Status { get; set; } = PlatformOutboxDeliveryStatus.Pending;
    public int Attempt { get; set; }
    public string LastErrorCode { get; set; } = string.Empty;
    public DateTimeOffset EligibleAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class MonitoringActivityProjection
{
    public Guid OutboxEventId { get; set; }
    public long AuthoritySequence { get; set; }
    public long Generation { get; set; }
    public Guid? TenantId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public string AggregateId { get; set; } = string.Empty;
    public string EventType { get; set; } = string.Empty;
    public long AuthorityRevision { get; set; }
    public bool SecurityCritical { get; set; }
    public string RedactedPayloadJson { get; set; } = "{}";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ProjectedAt { get; set; }
}

public sealed class MonitoringProjectionState
{
    public string ProjectionName { get; set; } = string.Empty;
    public string TenantScopeKey { get; set; } = string.Empty;
    public Guid? TenantId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public long Generation { get; set; }
    public long AuthoritySequence { get; set; }
    public long Cursor { get; set; }
    public DateTimeOffset? LastSuccessAt { get; set; }
    public DateTimeOffset? NextRunAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public sealed class PlatformBackgroundRun
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid? TenantId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string JobType { get; set; } = string.Empty;
    public string ScopeKey { get; set; } = string.Empty;
    public PlatformBackgroundMode Mode { get; set; }
    public PlatformBackgroundRunStatus Status { get; set; } = PlatformBackgroundRunStatus.Pending;
    public long Generation { get; set; }
    public long AuthoritySequenceBoundary { get; set; }
    public long Cursor { get; set; }
    public long ExpectedCount { get; set; }
    public long ScannedCount { get; set; }
    public bool CoverageComplete { get; set; }
    public long StaleCount { get; set; }
    public long DriftCount { get; set; }
    public long RepairedCount { get; set; }
    public long RebuiltCount { get; set; }
    public long FailedCount { get; set; }
    public int Attempt { get; set; } = 1;
    public int MaxAttempts { get; set; } = 5;
    public string OwnerId { get; set; } = string.Empty;
    public string LeaseTokenHash { get; set; } = string.Empty;
    public long LeaseVersion { get; set; }
    public DateTimeOffset? LeaseExpiresAt { get; set; }
    public DateTimeOffset EligibleAt { get; set; }
    public string FailureCode { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public ICollection<PlatformBackgroundEvent> Events { get; set; } = [];
}

public sealed class PlatformBackgroundEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid RunId { get; set; }
    public long Sequence { get; set; }
    public PlatformBackgroundEventType EventType { get; set; }
    public string PayloadJson { get; set; } = "{}";
    public DateTimeOffset CreatedAt { get; set; }
    public PlatformBackgroundRun? Run { get; set; }
}

namespace Memory.Domain;

public sealed class WorkItemEditHistory
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid? OwnerUserId { get; set; }
    public Guid WorkItemId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public long PreviousRevision { get; set; }
    public long Revision { get; set; }
    public string Operation { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string BeforeJson { get; set; } = string.Empty;
    public string AfterJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

public sealed class WorkItemEditReceiptRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public Guid WorkItemId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public string RequestId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string ResultJson { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

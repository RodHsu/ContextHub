namespace Memory.Domain;

public enum WorkItemProposalOperationState { Dispatched, Conflict, Applied }

/// <summary>
/// One durable dispatch decision per proposal. This is coordination evidence, not an
/// alternative WorkItem edit receipt or a claim that a dispatched mutation completed.
/// </summary>
public sealed class WorkItemProposalOperation
{
    public Guid ProposalId { get; set; }
    public Guid TenantId { get; set; }
    public Guid ActorUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public string ProjectKey { get; set; } = string.Empty;
    public Guid WorkItemId { get; set; }
    public long OriginalRevision { get; set; }
    public string RequestId { get; set; } = string.Empty;
    public string RequestHash { get; set; } = string.Empty;
    public string HashVersion { get; set; } = string.Empty;
    public bool Archived { get; set; }
    public WorkItemProposalOperationState State { get; set; } = WorkItemProposalOperationState.Dispatched;
    public string? ConflictCode { get; set; }
    public Guid? ReceiptId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? FinalizedAt { get; set; }
}

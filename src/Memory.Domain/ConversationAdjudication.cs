namespace Memory.Domain;

/// <summary>Append-only receipt. Closing staging never executes the underlying proposal.</summary>
public sealed class ConversationAdjudication
{
    public Guid Id { get; set; }
    public Guid TenantId { get; set; }
    public Guid OwnerUserId { get; set; }
    public string ProjectId { get; set; } = string.Empty;
    public Guid InsightId { get; set; }
    public string RequestHash { get; set; } = string.Empty;
    public string EvidenceFingerprint { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Outcome { get; set; } = string.Empty;
    public string PreviousStatus { get; set; } = string.Empty;
    public string ResultStatus { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string GovernanceRunId { get; set; } = string.Empty;
    public string Actor { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; }
}

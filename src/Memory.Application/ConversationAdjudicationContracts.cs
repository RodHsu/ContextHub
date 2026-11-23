using System.Text.Json.Serialization;
using Memory.Domain;

namespace Memory.Application;

public enum ConversationAdjudicationAction
{
    ResolveSkipped,
    RejectProposal,
    Reconcile,
    Reopen
}

public sealed record ConversationAdjudicationPreviewRequest(
    string ProjectId,
    int Offset = 0,
    int Limit = 100,
    string? GovernanceRunId = null,
    bool IncludeInventoryToken = false,
    string? ExpectedInventoryToken = null);

public sealed record ConversationAdjudicationRequest(
    string ProjectId,
    Guid InsightId,
    Guid OperationId,
    ConversationAdjudicationAction Action,
    string ExpectedEvidenceToken,
    string Reason,
    string GovernanceRunId);

public sealed record ConversationAdjudicationCandidate(
    Guid InsightId,
    string ProjectId,
    string Title,
    ConversationPromotionStatus Status,
    string EvidenceToken,
    string EvidenceFingerprint,
    string Classification,
    bool CanReconcile,
    bool CanReopen);

public sealed record ConversationAdjudicationPreviewResult(
    string ProjectId,
    int TotalCount,
    int UnresolvedHumanDecisionCount,
    int ResolvedHumanDecisionCount,
    int Offset,
    bool HasMore,
    IReadOnlyList<ConversationAdjudicationCandidate> Items)
{
    public string? CounterGovernanceRunId { get; init; }
    public int NewlyResolvedCount { get; init; }
    public int ReopenedCount { get; init; }
    public int UnchangedCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? InventoryToken { get; init; }
}

public sealed class ConversationAdjudicationInventoryException(string message) : InvalidOperationException(message)
{
}

public sealed record ConversationAdjudicationReceipt(
    Guid OperationId,
    Guid InsightId,
    string ProjectId,
    string Action,
    string Outcome,
    string PreviousStatus,
    string ResultStatus,
    string Reason,
    string GovernanceRunId,
    string EvidenceFingerprint,
    DateTimeOffset CreatedAt,
    bool IsReplay);

public interface IConversationAdjudicationService
{
    Task<ConversationAdjudicationPreviewResult> PreviewAsync(ConversationAdjudicationPreviewRequest request, CancellationToken cancellationToken);
    Task<ConversationAdjudicationReceipt> AdjudicateAsync(ConversationAdjudicationRequest request, CancellationToken cancellationToken);
}

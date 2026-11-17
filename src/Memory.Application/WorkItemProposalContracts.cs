using Memory.Domain;

namespace Memory.Application;

public enum WorkItemProposalPreparationMode { Dispatch, Recover, Applied, Conflict }

/// <summary>Server-derived durable attempt; never bound from an external request.</summary>
public sealed record WorkItemProposalPreparation(Guid ProposalId, Guid TenantId, Guid ActorUserId,
    WorkItemEditCommand Command, string RequestHash, WorkItemProposalPreparationMode Mode,
    ChatGptProposalResult? AppliedResult = null, string? ConflictCode = null);

public sealed class WorkItemProposalOutcomeUnknownException()
    : Exception("The original proposal execution could not be confirmed. Keep the proposal and recover its exact receipt; do not create another request.");

public interface IWorkItemProposalStore
{
    // A Dispatch result may be returned only after its one-time marker commits in an
    // owned transaction. Recovery must never turn absence of a receipt into permission to dispatch.
    Task<WorkItemProposalPreparation> PrepareAsync(Guid proposalId, CancellationToken ct);
    Task<ChatGptProposalResult> FinalizeAsync(WorkItemProposalPreparation original, WorkItemEditReceipt receipt, string note, CancellationToken ct);
    Task RecordConflictAsync(WorkItemProposalPreparation original, string conflictCode, CancellationToken ct);
    Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken ct);
}

public interface IWorkItemProposalWorkflow
{
    Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken ct);
    Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken ct);
}

public static class WorkItemProposalContract
{
    public const string ToolName = "project_work_item_archive";

    public static WorkItemEditCommand Command(string projectId, ProjectWorkItemArchiveRequest request)
        => WorkItemEditContract.Normalize(new(projectId, request.WorkItemId, request.ExpectedRevision,
            request.RequestId ?? string.Empty, request.Archived ? WorkItemEditOperation.Archive : WorkItemEditOperation.Restore, new()));

    public static void RequireSameIntent(WorkItemProposalPreparation original, WorkItemEditReceipt receipt)
    {
        var command = WorkItemEditContract.Normalize(original.Command);
        var hash = WorkItemEditContract.Hash(command);
        if (hash != original.RequestHash || receipt.ReceiptId == Guid.Empty || receipt.Result is null ||
            receipt.WorkItemId != command.WorkItemId || !ProjectContext.Matches(receipt.ProjectId, command.ProjectId) ||
            receipt.RequestId != command.RequestId || receipt.RequestHash != hash || receipt.HashVersion != WorkItemEditContract.IntentHashVersion ||
            receipt.Operation != command.Operation || receipt.PreviousRevision != command.ExpectedRevision ||
            receipt.Result.Id != command.WorkItemId || !ProjectContext.Matches(receipt.Result.ProjectId, command.ProjectId) ||
            receipt.Result.Revision != receipt.Revision ||
            (receipt.Changed ? receipt.HistoryId is null || receipt.HistoryId == Guid.Empty || receipt.PreviousRevision == long.MaxValue ||
                receipt.Revision != receipt.PreviousRevision + 1 : receipt.HistoryId is not null || receipt.Revision != receipt.PreviousRevision))
            throw new InvalidDataException("The committed receipt does not bind the original proposal intent.");
    }
}

using Microsoft.Extensions.Logging;

namespace Memory.Application;

/// <summary>
/// Owns no transaction across the edit. A durable one-shot dispatch marker precedes
/// the core's independent commit; any later attempt can only recover and finalize.
/// </summary>
public sealed class WorkItemProposalWorkflow(IWorkItemProposalStore store, IWorkItemEditingService editing,
    IRequestActorAccessor actors, ILogger<WorkItemProposalWorkflow> logger) : IWorkItemProposalWorkflow
{
    public async Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        WorkItemProposalPreparation original;
        try { original = await store.PrepareAsync(request.ProposalId, ct); }
        catch (Exception error) when (error is not (SafeEditConflictException or UnauthorizedAccessException or KeyNotFoundException or ArgumentException or OperationCanceledException))
        { throw Unknown(error, request.ProposalId); }
        if (original.Mode == WorkItemProposalPreparationMode.Applied)
        {
            RequireSameActor(original);
            return original.AppliedResult ?? throw new WorkItemProposalOutcomeUnknownException();
        }
        if (original.Mode == WorkItemProposalPreparationMode.Conflict)
        {
            RequireSameActor(original);
            throw new SafeEditConflictException(original.ConflictCode ?? SafeEditConflictCodes.RequestIdentityMismatch);
        }

        var definiteDispatchConflict = false;
        try
        {
            RequireSameActor(original);
            WorkItemEditReceipt? receipt;
            if (original.Mode == WorkItemProposalPreparationMode.Dispatch)
            {
                try { receipt = await editing.ApplyAsync(original.Command, ct); }
                catch (SafeEditConflictException conflict)
                {
                    await store.RecordConflictAsync(original, conflict.Code, ct);
                    definiteDispatchConflict = true;
                    throw;
                }
            }
            else if (original.Mode == WorkItemProposalPreparationMode.Recover)
            {
                ActorAuthorization.EnsureScopeAllowed(actors.Current, SecurityScopes.MemoryRead);
                receipt = await editing.ReadReceiptAsync(new(original.Command.ProjectId, original.Command.WorkItemId,
                    original.Command.RequestId, original.RequestHash, WorkItemEditContract.IntentHashVersion), ct);
            }
            else throw new InvalidDataException("Unsupported proposal preparation state.");
            if (receipt is null) throw new WorkItemProposalOutcomeUnknownException();
            WorkItemProposalContract.RequireSameIntent(original, receipt);
            RequireSameActor(original);
            return await store.FinalizeAsync(original, receipt, request.Note, ct);
        }
        catch (SafeEditConflictException) when (definiteDispatchConflict) { throw; }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (WorkItemProposalOutcomeUnknownException) { throw; }
        catch (Exception error) { throw Unknown(error, request.ProposalId); }
    }

    public Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken ct)
        => store.RejectAsync(request, ct);

    private void RequireSameActor(WorkItemProposalPreparation original)
    {
        var actor = actors.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, original.Command.ProjectId, write: true);
        if (actor.TenantId != original.TenantId || actor.UserId != original.ActorUserId)
            throw new UnauthorizedAccessException("The original proposal actor is unavailable.");
    }

    private WorkItemProposalOutcomeUnknownException Unknown(Exception error, Guid proposalId)
    {
        logger.LogWarning(error, "Work item proposal {ProposalId} outcome requires exact receipt recovery.", proposalId);
        return new();
    }
}

using Memory.Application;

namespace Memory.Dashboard.Services;

public interface IWorkItemEditingApi
{
    // Implementations may use NotSupportedException only before dispatch. A response
    // decoding/transport failure after submission must remain an unknown edit outcome.
    Task<ProjectWorkItemResult?> GetWorkItemForEditAsync(string projectId, Guid workItemId, CancellationToken ct)
        => Task.FromException<ProjectWorkItemResult?>(new NotSupportedException("Versioned work item reads are unavailable."));
    Task<WorkItemEditReceipt> ApplyWorkItemEditAsync(WorkItemEditCommand command, CancellationToken ct)
        => Task.FromException<WorkItemEditReceipt>(new NotSupportedException("Versioned work item edits are unavailable."));
    Task<WorkItemEditReceipt?> RecoverWorkItemEditAsync(WorkItemEditRecoveryRequest request, CancellationToken ct)
        => Task.FromException<WorkItemEditReceipt?>(new NotSupportedException("Work item receipt recovery is unavailable."));
}

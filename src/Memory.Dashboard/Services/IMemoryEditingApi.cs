using Memory.Application;

namespace Memory.Dashboard.Services;

public interface IMemoryEditingApi
{
    Task<MemoryDocument?> GetMemoryForEditAsync(string projectId, Guid memoryId, CancellationToken cancellationToken)
        => Task.FromException<MemoryDocument?>(new NotSupportedException("Versioned memory reads are unavailable."));

    Task<MemoryEditReceipt> ApplyMemoryEditAsync(MemoryEditCommand command, CancellationToken cancellationToken)
        => Task.FromException<MemoryEditReceipt>(new NotSupportedException("Versioned memory edits are unavailable."));

    Task<MemoryEditReceipt?> RecoverMemoryEditAsync(MemoryEditRecoveryRequest request, CancellationToken cancellationToken)
        => Task.FromException<MemoryEditReceipt?>(new NotSupportedException("Memory edit receipt recovery is unavailable."));
}

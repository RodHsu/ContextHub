using System.Text.Json;
using System.Text.Json.Serialization;

namespace Memory.Application;

public interface IMemoryEditProposalWorkflow
{
    Task<ChatGptProposalResult> CreateAsync(ChatGptProposalCreateRequest request, CancellationToken cancellationToken);
    Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken);
    Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken);
}

public static class MemoryEditProposalContract
{
    public const string ToolName = "memory_edit";
    public const string ToolTag = "tool:" + ToolName;
    private const string ReceiptPrefix = "memory-proposal:";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public static MemoryEditCommand ReadCommand(string projectId, string payloadJson)
    {
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("An explicit proposal ProjectId is required.");
        MemoryEditCommand? command;
        try { command = JsonSerializer.Deserialize<MemoryEditCommand>(payloadJson, Json); }
        catch (JsonException) { throw new ArgumentException("Invalid memory edit proposal payload."); }
        if (command is null) throw new ArgumentException("A memory edit proposal command is required.");
        command = MemoryEditContract.Normalize(command);
        if (!ProjectContext.Matches(projectId.Trim(), command.ProjectId))
            throw new ArgumentException("Memory edit proposal ProjectId does not match its command.");
        RequireDirectRequestId(command.RequestId);
        return command;
    }

    public static string ReceiptRequestId(Guid proposalId) => ReceiptPrefix + proposalId.ToString("N");

    internal static void RequireDirectRequestId(string requestId)
    {
        if (requestId.StartsWith(ReceiptPrefix, StringComparison.Ordinal))
            throw new ArgumentException("This memory edit RequestId is reserved for the proposal lifecycle.");
    }
}

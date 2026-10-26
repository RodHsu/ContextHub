using System.Text.Json;
using Memory.Domain;

namespace Memory.Application;

public sealed record ChatGptProposalMetadata(string ToolName, string PayloadJson, string OAuthSubject,
    string OAuthEmail, string OAuthName, string GovernanceRunId);

/// <summary>Projection only: callers must authorize the proposal before parsing or returning it.</summary>
public static class ChatGptProposalProjection
{
    public static ChatGptProposalMetadata ReadMetadata(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new(root.GetProperty("toolName").GetString() ?? string.Empty, root.GetProperty("payload").GetRawText(),
                Read(root, "oauthSubject"), Read(root, "oauthEmail"), Read(root, "oauthName"), Read(root, "governanceRunId"));
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException)
        {
            throw new InvalidDataException("Stored proposal metadata is unavailable.", error);
        }
    }

    public static ChatGptProposalResult ToResult(ConversationInsight item)
    {
        var metadata = ReadMetadata(item.MetadataJson);
        var status = item.PromotionStatus switch
        {
            ConversationPromotionStatus.Pending => ChatGptProposalStatus.Pending,
            ConversationPromotionStatus.Promoted => ChatGptProposalStatus.Applied,
            ConversationPromotionStatus.Skipped => ChatGptProposalStatus.Rejected,
            ConversationPromotionStatus.Failed => ChatGptProposalStatus.Failed,
            _ => throw new InvalidOperationException("Unsupported proposal lifecycle state.")
        };
        return new(item.Id, metadata.ToolName, status, item.ProjectId, item.ProjectName, item.Title, item.Summary,
            metadata.PayloadJson, metadata.OAuthSubject, metadata.OAuthEmail, metadata.OAuthName,
            item.PromotedMemoryId, item.Error, item.CreatedAt, item.UpdatedAt, metadata.GovernanceRunId);
    }

    private static string Read(JsonElement root, string name)
        => root.TryGetProperty(name, out var value) ? value.GetString() ?? string.Empty : string.Empty;
}

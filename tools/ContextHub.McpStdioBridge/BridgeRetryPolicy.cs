using System.Text.Json;

namespace ContextHub.McpStdioBridge;

public sealed class BridgeRetryPolicy
{
    private static readonly HashSet<string> RetryableMethods = new(StringComparer.Ordinal)
    {
        "tools/list",
        "resources/list",
        "resources/templates/list",
        "resources/read",
        "prompts/list",
        "prompts/get"
    };

    private static readonly HashSet<string> RetryableTools = new(StringComparer.Ordinal)
    {
        "describe_context_hub",
        "maintenance_status",
        "build_working_context",
        "memory_search",
        "memory_get",
        "log_search",
        "log_read",
        "conversation_sessions_list",
        "conversation_insights_list",
        "user_preference_list"
    };

    public static readonly BridgeRetryPolicy Default = new();

    public bool CanRetry(JsonElement localMessage, string method)
    {
        if (RetryableMethods.Contains(method))
        {
            return true;
        }

        return string.Equals(method, "tools/call", StringComparison.Ordinal) &&
               TryGetToolName(localMessage, out var toolName) &&
               RetryableTools.Contains(toolName);
    }

    private static bool TryGetToolName(JsonElement localMessage, out string toolName)
    {
        toolName = string.Empty;
        if (!localMessage.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        toolName = name.GetString() ?? string.Empty;
        return !string.IsNullOrWhiteSpace(toolName);
    }
}

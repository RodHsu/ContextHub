using System.Text.Json;

namespace Memory.ChatGptGateway;

public static class ChatGptAppCatalogProjection
{
    private static readonly IReadOnlySet<string> RequiredReadOnlyTools = new HashSet<string>(StringComparer.Ordinal)
    {
        "governance_contract_get",
        "governance_run_get",
        "governance_runs_list"
    };

    public static ChatGptAppCatalogProjectionResult Project(JsonElement tools)
    {
        if (tools.ValueKind != JsonValueKind.Array)
        {
            throw new ArgumentException("ChatGPT App tool catalog must be a JSON array.", nameof(tools));
        }

        var descriptors = tools.EnumerateArray()
            .Select(ProjectTool)
            .OrderBy(tool => tool.Name, StringComparer.Ordinal)
            .ToArray();
        var publishedNames = descriptors.Select(tool => tool.Name).ToHashSet(StringComparer.Ordinal);
        var callableNames = descriptors
            .Where(tool => tool.IsAppCallable)
            .Select(tool => tool.Name)
            .ToHashSet(StringComparer.Ordinal);

        return new ChatGptAppCatalogProjectionResult(
            ChatGptGatewayToolCatalog.PublishedCatalogVersion,
            ChatGptGatewayToolCatalog.PublishedCatalogHash,
            ChatGptGatewayToolCatalog.PublishedToolNames.Count,
            descriptors.Length,
            callableNames.Count,
            ChatGptGatewayToolCatalog.PublishedToolNames.Except(publishedNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            publishedNames.Except(ChatGptGatewayToolCatalog.PublishedToolNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            ChatGptGatewayToolCatalog.PublishedToolNames.Except(callableNames, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(),
            descriptors);
    }

    private static ChatGptAppToolProjection ProjectTool(JsonElement tool)
    {
        var name = tool.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? string.Empty : string.Empty;
        var invalidReasons = new List<string>();
        if (string.IsNullOrWhiteSpace(name))
        {
            invalidReasons.Add("missing-name");
        }

        if (!tool.TryGetProperty("description", out var description) ||
            description.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(description.GetString()))
        {
            invalidReasons.Add("missing-description");
        }

        if (!tool.TryGetProperty("inputSchema", out var inputSchema) ||
            !HasSupportedSchemaType(inputSchema, allowArray: false))
        {
            invalidReasons.Add("invalid-input-schema");
        }

        if (!tool.TryGetProperty("outputSchema", out var outputSchema) ||
            !HasSupportedSchemaType(outputSchema, allowArray: true))
        {
            invalidReasons.Add("invalid-output-schema");
        }

        var isRequiredReadOnlyTool = RequiredReadOnlyTools.Contains(name);
        if (isRequiredReadOnlyTool)
        {
            if (!tool.TryGetProperty("annotations", out var annotations) || annotations.ValueKind != JsonValueKind.Object)
            {
                invalidReasons.Add("missing-read-only-annotations");
            }
            else
            {
                RequireBoolean(annotations, "readOnlyHint", true, invalidReasons);
                RequireBoolean(annotations, "destructiveHint", false, invalidReasons);
                RequireBoolean(annotations, "openWorldHint", false, invalidReasons);
                RequireBoolean(annotations, "idempotentHint", true, invalidReasons);
            }
        }

        return new ChatGptAppToolProjection(
            name,
            invalidReasons.Count == 0,
            isRequiredReadOnlyTool,
            ["model", "app"],
            invalidReasons);
    }

    private static bool HasSupportedSchemaType(JsonElement schema, bool allowArray)
    {
        if (schema.ValueKind != JsonValueKind.Object ||
            !schema.TryGetProperty("type", out var type) ||
            type.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        return type.GetString() is "object" || (allowArray && type.GetString() is "array");
    }

    private static void RequireBoolean(
        JsonElement annotations,
        string propertyName,
        bool expected,
        ICollection<string> invalidReasons)
    {
        if (!annotations.TryGetProperty(propertyName, out var value) ||
            value.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
            value.GetBoolean() != expected)
        {
            invalidReasons.Add($"{propertyName}-must-be-{expected.ToString().ToLowerInvariant()}");
        }
    }
}

public sealed record ChatGptAppCatalogProjectionResult(
    string CatalogVersion,
    string CatalogHash,
    int ExpectedToolCount,
    int PublishedToolCount,
    int AppCallableToolCount,
    IReadOnlyList<string> MissingPublishedTools,
    IReadOnlyList<string> UnexpectedPublishedTools,
    IReadOnlyList<string> MissingAppCallableTools,
    IReadOnlyList<ChatGptAppToolProjection> Tools)
{
    public bool IsValid =>
        ExpectedToolCount == PublishedToolCount &&
        ExpectedToolCount == AppCallableToolCount &&
        MissingPublishedTools.Count == 0 &&
        UnexpectedPublishedTools.Count == 0 &&
        MissingAppCallableTools.Count == 0;
}

public sealed record ChatGptAppToolProjection(
    string Name,
    bool IsAppCallable,
    bool IsRequiredReadOnlyTool,
    IReadOnlyList<string> EffectiveVisibility,
    IReadOnlyList<string> InvalidReasons);

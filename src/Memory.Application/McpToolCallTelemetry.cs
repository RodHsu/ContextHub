using System.Text.Json;

namespace Memory.Application;

public static class McpToolCallTelemetry
{
    public static string ResolveGovernanceRunId(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return string.Empty;
        }

        if (TryReadString(arguments, "governanceRunId", out var governanceRunId))
        {
            return NormalizeGovernanceRunId(governanceRunId);
        }

        var request = arguments.FirstOrDefault(pair =>
            string.Equals(pair.Key, "request", StringComparison.OrdinalIgnoreCase));
        if (request.Value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        foreach (var property in request.Value.EnumerateObject())
        {
            if (string.Equals(property.Name, "governanceRunId", StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return NormalizeGovernanceRunId(property.Value.GetString());
            }
        }

        return string.Empty;
    }

    private static string NormalizeGovernanceRunId(string? value)
    {
        var normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= 128 && normalized.All(character =>
            char.IsLetterOrDigit(character) || character is '-' or '_' or '.' or ':')
            ? normalized
            : string.Empty;
    }

    public static string ResolveProjectId(IDictionary<string, JsonElement>? arguments)
    {
        if (arguments is null)
        {
            return ProjectContext.DefaultProjectId;
        }

        if (TryReadProjectId(arguments, out var projectId))
        {
            return ProjectContext.Normalize(projectId);
        }

        var request = arguments.FirstOrDefault(pair =>
            string.Equals(pair.Key, "request", StringComparison.OrdinalIgnoreCase));
        if (request.Value.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in request.Value.EnumerateObject())
            {
                if (string.Equals(property.Name, "projectId", StringComparison.OrdinalIgnoreCase) &&
                    property.Value.ValueKind == JsonValueKind.String)
                {
                    return ProjectContext.Normalize(property.Value.GetString());
                }
            }
        }

        return ProjectContext.DefaultProjectId;
    }

    public static async Task TryRecordAsync(
        IServiceProvider? services,
        string serviceName,
        string toolName,
        IDictionary<string, JsonElement>? arguments,
        bool success,
        double durationMs)
    {
        try
        {
            if (services is null)
            {
                return;
            }

            if (services.GetService(typeof(IMcpToolCallTelemetryService)) is not IMcpToolCallTelemetryService telemetry)
            {
                return;
            }

            await telemetry.RecordAsync(
                new McpToolCallTelemetryWriteRequest(
                    ResolveProjectId(arguments),
                    serviceName,
                    toolName,
                    success,
                    durationMs),
                CancellationToken.None);
        }
        catch
        {
            // Tool-call telemetry must never change the MCP tool result.
        }
    }

    private static bool TryReadProjectId(
        IDictionary<string, JsonElement> arguments,
        out string? projectId)
    {
        return TryReadString(arguments, "projectId", out projectId);
    }

    private static bool TryReadString(
        IDictionary<string, JsonElement> arguments,
        string name,
        out string? value)
    {
        var pair = arguments.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, name, StringComparison.OrdinalIgnoreCase));
        if (pair.Value.ValueKind == JsonValueKind.String)
        {
            value = pair.Value.GetString();
            return true;
        }

        value = null;
        return false;
    }
}

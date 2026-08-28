using System.Text.Json;

namespace Memory.Application;

public static class McpToolCallTelemetry
{
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
        var pair = arguments.FirstOrDefault(candidate =>
            string.Equals(candidate.Key, "projectId", StringComparison.OrdinalIgnoreCase));
        if (pair.Value.ValueKind == JsonValueKind.String)
        {
            projectId = pair.Value.GetString();
            return true;
        }

        projectId = null;
        return false;
    }
}

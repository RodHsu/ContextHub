using System.Text.Json;
using Memory.Application;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Primitives;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Memory.McpServer;

internal static class WorkingContextMcpResources
{
    private const string WorkingContextScheme = "working-context";
    private const string WorkingContextPrefix = WorkingContextScheme + "://";
    private const string JsonMimeType = "application/json";
    private const string WorkingContextTemplate = "working-context://{projectId}{?query,limit,recentLogLimit,queryMode,useSummaryLayer,includedProjectIds}";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    internal static ValueTask<ListResourceTemplatesResult> ListTemplatesAsync(
        RequestContext<ListResourceTemplatesRequestParams> _,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(new ListResourceTemplatesResult
        {
            ResourceTemplates =
            [
                new ResourceTemplate
                {
                    Name = "working_context",
                    Title = "Working Context",
                    UriTemplate = WorkingContextTemplate,
                    Description = "Build a structured working context for a target project using the same payload shape as build_working_context.",
                    MimeType = JsonMimeType
                }
            ]
        });
    }

    internal static async ValueTask<ReadResourceResult> ReadAsync(
        RequestContext<ReadResourceRequestParams> request,
        CancellationToken cancellationToken)
    {
        var resourceUri = request.Params?.Uri
            ?? throw InvalidParams("Resource URI is required.");
        var services = request.Services
            ?? throw new McpProtocolException("Request services are unavailable.");
        var workingContextRequest = ParseRequest(resourceUri);
        var memoryService = services.GetRequiredService<IMemoryService>();
        var result = await memoryService.BuildWorkingContextAsync(workingContextRequest, cancellationToken);

        return new ReadResourceResult
        {
            Contents =
            [
                new TextResourceContents
                {
                    Uri = resourceUri,
                    MimeType = JsonMimeType,
                    Text = JsonSerializer.Serialize(result, JsonOptions)
                }
            ]
        };
    }

    private static WorkingContextRequest ParseRequest(string resourceUri)
    {
        if (string.IsNullOrWhiteSpace(resourceUri) ||
            !resourceUri.StartsWith(WorkingContextPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw InvalidParams($"Unknown resource URI: '{resourceUri}'");
        }

        var uriRemainder = resourceUri[WorkingContextPrefix.Length..];
        var queryDelimiterIndex = uriRemainder.IndexOf('?', StringComparison.Ordinal);
        var projectId = ExtractProjectId(
            queryDelimiterIndex >= 0 ? uriRemainder[..queryDelimiterIndex] : uriRemainder,
            resourceUri);
        var queryString = queryDelimiterIndex >= 0 ? uriRemainder[(queryDelimiterIndex + 1)..] : string.Empty;
        var query = QueryHelpers.ParseQuery(queryString.Length > 0 ? "?" + queryString : string.Empty);
        var contextQuery = GetRequiredString(query, "query", "working-context resource requires query.");

        return new WorkingContextRequest(
            Query: contextQuery,
            Limit: ParsePositiveInt(query, "limit", 5),
            RecentLogLimit: ParsePositiveInt(query, "recentLogLimit", 5),
            ProjectId: ProjectContext.Normalize(projectId),
            IncludedProjectIds: ParseIncludedProjectIds(query),
            QueryMode: ParseQueryMode(query),
            UseSummaryLayer: ParseBool(query, "useSummaryLayer"),
            Telemetry: new RetrievalTelemetryContext("working_context_resource", "mcp-resource", "resource bootstrap"));
    }

    private static string ExtractProjectId(string projectPart, string resourceUri)
    {
        var trimmedProjectPart = projectPart.TrimStart('/');
        if (string.IsNullOrWhiteSpace(trimmedProjectPart))
        {
            throw InvalidParams("working-context resource requires projectId.");
        }

        if (trimmedProjectPart.Contains('/', StringComparison.Ordinal))
        {
            throw InvalidParams($"Unsupported working-context resource URI: '{resourceUri}'");
        }

        try
        {
            var projectId = Uri.UnescapeDataString(trimmedProjectPart);
            return string.IsNullOrWhiteSpace(projectId)
                ? throw InvalidParams("working-context resource requires projectId.")
                : projectId;
        }
        catch (UriFormatException ex)
        {
            throw InvalidParams($"Invalid working-context projectId in resource URI: '{resourceUri}'", ex);
        }
    }

    private static IReadOnlyList<string>? ParseIncludedProjectIds(Dictionary<string, StringValues> query)
    {
        if (!query.TryGetValue("includedProjectIds", out var values))
        {
            return null;
        }

        var projectIds = values
            .SelectMany(value => (value ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return projectIds.Length == 0 ? null : projectIds;
    }

    private static MemoryQueryMode ParseQueryMode(Dictionary<string, StringValues> query)
    {
        if (!query.TryGetValue("queryMode", out var values))
        {
            return MemoryQueryMode.CurrentOnly;
        }

        var raw = GetSingleValue(values);
        if (Enum.TryParse<MemoryQueryMode>(raw, ignoreCase: true, out var parsed))
        {
            return parsed;
        }

        throw InvalidParams("working-context resource queryMode must be a valid MemoryQueryMode value.");
    }

    private static int ParsePositiveInt(Dictionary<string, StringValues> query, string key, int fallback)
    {
        if (!query.TryGetValue(key, out var values))
        {
            return fallback;
        }

        var raw = GetSingleValue(values);
        return int.TryParse(raw, out var value) && value > 0
            ? value
            : throw InvalidParams($"working-context resource {key} must be a positive integer.");
    }

    private static bool ParseBool(Dictionary<string, StringValues> query, string key)
    {
        if (!query.TryGetValue(key, out var values))
        {
            return false;
        }

        var raw = GetSingleValue(values);
        return bool.TryParse(raw, out var parsed)
            ? parsed
            : throw InvalidParams($"working-context resource {key} must be a boolean value.");
    }

    private static string GetRequiredString(
        Dictionary<string, StringValues> query,
        string key,
        string errorMessage)
    {
        var raw = GetSingleValue(query, key);
        return string.IsNullOrWhiteSpace(raw) ? throw InvalidParams(errorMessage) : raw.Trim();
    }

    private static string? GetSingleValue(Dictionary<string, StringValues> query, string key)
        => query.TryGetValue(key, out var value)
            ? GetSingleValue(value)
            : null;

    private static string? GetSingleValue(StringValues value)
        => value.Count > 0 ? value[0] : null;

    private static McpProtocolException InvalidParams(string message)
        => new(message, McpErrorCode.InvalidParams);

    private static McpProtocolException InvalidParams(string message, Exception innerException)
        => new(message, innerException, McpErrorCode.InvalidParams);
}

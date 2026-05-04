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
            ?? throw new McpProtocolException("Resource URI is required.");
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
        if (!Uri.TryCreate(resourceUri, UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, WorkingContextScheme, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpProtocolException($"Unknown resource URI: '{resourceUri}'");
        }

        var query = QueryHelpers.ParseQuery(uri.Query);
        var projectId = ExtractProjectId(resourceUri, uri);

        return new WorkingContextRequest(
            Query: GetSingleValue(query, "query") ?? string.Empty,
            Limit: ParseInt(query, "limit", 5),
            RecentLogLimit: ParseInt(query, "recentLogLimit", 5),
            ProjectId: ProjectContext.Normalize(projectId),
            IncludedProjectIds: ParseIncludedProjectIds(query),
            QueryMode: ParseQueryMode(query),
            UseSummaryLayer: ParseBool(query, "useSummaryLayer"),
            Telemetry: new RetrievalTelemetryContext("working_context_resource", "mcp-resource", "resource bootstrap"));
    }

    private static string ExtractProjectId(string resourceUri, Uri uri)
    {
        var schemeSeparator = resourceUri.IndexOf("://", StringComparison.Ordinal);
        if (schemeSeparator >= 0)
        {
            var remainder = resourceUri[(schemeSeparator + 3)..];
            var delimiterIndex = remainder.IndexOfAny(['?', '/']);
            var authority = delimiterIndex >= 0 ? remainder[..delimiterIndex] : remainder;
            if (!string.IsNullOrWhiteSpace(authority))
            {
                return Uri.UnescapeDataString(authority);
            }
        }

        var path = uri.AbsolutePath.Trim('/');
        return string.IsNullOrWhiteSpace(path)
            ? ProjectContext.DefaultProjectId
            : Uri.UnescapeDataString(path);
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
        var raw = GetSingleValue(query, "queryMode");
        return Enum.TryParse<MemoryQueryMode>(raw, ignoreCase: true, out var parsed)
            ? parsed
            : MemoryQueryMode.CurrentOnly;
    }

    private static int ParseInt(Dictionary<string, StringValues> query, string key, int fallback)
    {
        var raw = GetSingleValue(query, key);
        return int.TryParse(raw, out var value) && value > 0 ? value : fallback;
    }

    private static bool ParseBool(Dictionary<string, StringValues> query, string key)
    {
        var raw = GetSingleValue(query, key);
        return bool.TryParse(raw, out var parsed) && parsed;
    }

    private static string? GetSingleValue(Dictionary<string, StringValues> query, string key)
        => query.TryGetValue(key, out var value)
            ? value.Count > 0 ? value[0] : null
            : null;
}

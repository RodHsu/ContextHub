using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Memory.McpTransport;

public sealed class McpProtocolCompatibilityMiddleware(
    RequestDelegate next,
    ILogger<McpProtocolCompatibilityMiddleware> logger)
{
    internal const string November2025ProtocolVersion = "2025-11-25";
    internal const string July2026ProtocolVersion = "2026-07-28";
    internal const string ProtocolVersionMetaKey = "io.modelcontextprotocol/protocolVersion";
    internal const string ClientInfoMetaKey = "io.modelcontextprotocol/clientInfo";
    internal const string ClientCapabilitiesMetaKey = "io.modelcontextprotocol/clientCapabilities";

    private static readonly HashSet<string> SupportedLegacyProtocolVersions = new(StringComparer.Ordinal)
    {
        "2024-11-05",
        "2025-03-26",
        "2025-06-18",
        November2025ProtocolVersion
    };

    public async Task InvokeAsync(HttpContext context)
    {
        if (!HttpMethods.IsPost(context.Request.Method) || !IsMcpPath(context.Request.Path))
        {
            await next(context);
            return;
        }

        context.Request.EnableBuffering();
        JsonObject? request;
        try
        {
            request = await JsonNode.ParseAsync(
                context.Request.Body,
                cancellationToken: context.RequestAborted) as JsonObject;
            context.Request.Body.Position = 0;
        }
        catch (JsonException)
        {
            context.Request.Body.Position = 0;
            await next(context);
            return;
        }

        if (request is null)
        {
            await next(context);
            return;
        }

        var result = Normalize(request, ReadSingleHeader(context.Request, "MCP-Protocol-Version"),
            context.Request.Headers.ContainsKey("Mcp-Session-Id"));
        if (!result.Success)
        {
            logger.LogWarning("Rejected MCP request at the protocol compatibility boundary: {Reason}", result.Error);
            await WriteProtocolErrorAsync(context, request["id"], result.Error!);
            return;
        }

        if (result.RemovedLegacySessionHeader)
        {
            context.Request.Headers.Remove("Mcp-Session-Id");
        }

        if (result.ChangedBody)
        {
            var bytes = Encoding.UTF8.GetBytes(request.ToJsonString());
            var normalizedBody = new MemoryStream(bytes);
            context.Response.RegisterForDispose(normalizedBody);
            context.Request.Body = normalizedBody;
            context.Request.ContentLength = bytes.Length;
        }

        if (result.ChangedBody || result.RemovedLegacySessionHeader)
        {
            logger.LogDebug(
                "Normalized known ChatGPT MCP legacy compatibility metadata for protocol {ProtocolVersion}; removedReservedMetadata={RemovedReservedMetadata}; removedStaleSessionHeader={RemovedStaleSessionHeader}.",
                result.ProtocolVersion,
                result.ChangedBody,
                result.RemovedLegacySessionHeader);
        }

        await next(context);
    }

    internal static McpProtocolNormalizationResult Normalize(
        JsonObject request,
        string? headerProtocolVersion,
        bool hasSessionHeader)
    {
        var parameters = request["params"] as JsonObject;
        var metadata = parameters?["_meta"] as JsonObject;
        var initializeProtocolVersion = string.Equals(ReadString(request, "method"), "initialize", StringComparison.Ordinal)
            ? ReadString(parameters, "protocolVersion")
            : null;
        var metadataProtocolVersion = ReadString(metadata, ProtocolVersionMetaKey);
        headerProtocolVersion = NormalizeVersion(headerProtocolVersion);

        var declaredVersions = new[] { headerProtocolVersion, initializeProtocolVersion, metadataProtocolVersion }
            .Where(version => version is not null)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (declaredVersions.Length > 1)
        {
            return McpProtocolNormalizationResult.Fail(
                $"MCP protocol version mismatch: header, initialize, and per-request metadata must declare the same version; received {string.Join(", ", declaredVersions)}.");
        }

        var protocolVersion = declaredVersions.SingleOrDefault();
        if (protocolVersion is null && hasSessionHeader)
        {
            protocolVersion = November2025ProtocolVersion;
        }

        var hasReservedMetadata = metadata is not null &&
            (metadata.ContainsKey(ProtocolVersionMetaKey) ||
             metadata.ContainsKey(ClientInfoMetaKey) ||
             metadata.ContainsKey(ClientCapabilitiesMetaKey));

        if (protocolVersion is null)
        {
            return hasReservedMetadata
                ? McpProtocolNormalizationResult.Fail(
                    "Reserved MCP per-request metadata requires an explicit MCP-Protocol-Version header.")
                : McpProtocolNormalizationResult.Unchanged(null);
        }

        if (IsPerRequestMetadataProtocol(protocolVersion))
        {
            if (hasSessionHeader)
            {
                return McpProtocolNormalizationResult.Fail(
                    $"Mcp-Session-Id is not valid with stateless MCP protocol version '{protocolVersion}'.");
            }

            if (!string.Equals(headerProtocolVersion, protocolVersion, StringComparison.Ordinal) ||
                !string.Equals(metadataProtocolVersion, protocolVersion, StringComparison.Ordinal))
            {
                return McpProtocolNormalizationResult.Fail(
                    $"MCP protocol version '{protocolVersion}' requires matching header and per-request metadata declarations.");
            }

            if (metadata is null || !metadata.ContainsKey(ClientCapabilitiesMetaKey))
            {
                return McpProtocolNormalizationResult.Fail(
                    $"MCP protocol version '{protocolVersion}' requires '_meta/{ClientCapabilitiesMetaKey}'.");
            }

            return McpProtocolNormalizationResult.Unchanged(protocolVersion);
        }

        if (!SupportedLegacyProtocolVersions.Contains(protocolVersion))
        {
            return hasReservedMetadata
                ? McpProtocolNormalizationResult.Fail(
                    $"Reserved MCP per-request metadata cannot be normalized for unsupported protocol version '{protocolVersion}'.")
                : McpProtocolNormalizationResult.Unchanged(protocolVersion);
        }

        var changedBody = false;
        if (metadata is not null)
        {
            changedBody |= metadata.Remove(ProtocolVersionMetaKey);
            changedBody |= metadata.Remove(ClientInfoMetaKey);
            changedBody |= metadata.Remove(ClientCapabilitiesMetaKey);
            if (metadata.Count == 0)
            {
                parameters!.Remove("_meta");
            }
        }

        return new McpProtocolNormalizationResult(
            true,
            changedBody,
            hasSessionHeader,
            protocolVersion,
            null);
    }

    private static bool IsMcpPath(PathString path)
        => path.Equals("/mcp", StringComparison.OrdinalIgnoreCase) ||
           path.Equals("/mcp-chat", StringComparison.OrdinalIgnoreCase);

    private static bool IsPerRequestMetadataProtocol(string protocolVersion)
        => DateOnly.TryParseExact(protocolVersion, "yyyy-MM-dd", out var parsed) &&
           parsed >= new DateOnly(2026, 7, 28);

    private static string? ReadSingleHeader(HttpRequest request, string name)
    {
        var values = request.Headers[name];
        return values.Count == 1 ? NormalizeVersion(values[0]) : null;
    }

    private static string? ReadString(JsonObject? value, string name)
        => value?[name] is JsonValue jsonValue && jsonValue.TryGetValue<string>(out var text)
            ? NormalizeVersion(text)
            : null;

    private static string? NormalizeVersion(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static async Task WriteProtocolErrorAsync(HttpContext context, JsonNode? id, string error)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        context.Response.ContentType = "application/json";
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id?.DeepClone(),
            ["error"] = new JsonObject
            {
                ["code"] = -32600,
                ["message"] = error
            }
        };
        await context.Response.WriteAsync(response.ToJsonString(), context.RequestAborted);
    }
}

public static class McpProtocolCompatibilityApplicationBuilderExtensions
{
    public static IApplicationBuilder UseMcpProtocolCompatibility(this IApplicationBuilder app)
        => app.UseMiddleware<McpProtocolCompatibilityMiddleware>();
}

internal sealed record McpProtocolNormalizationResult(
    bool Success,
    bool ChangedBody,
    bool RemovedLegacySessionHeader,
    string? ProtocolVersion,
    string? Error)
{
    internal static McpProtocolNormalizationResult Unchanged(string? protocolVersion)
        => new(true, false, false, protocolVersion, null);

    internal static McpProtocolNormalizationResult Fail(string error)
        => new(false, false, false, null, error);
}

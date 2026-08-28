using System.Net.Http.Headers;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContextHub.McpStdioBridge;

public sealed class RemoteMcpClient
{
    private const string ModernProtocolVersion = "2026-07-28";
    private const string PreferredLegacyProtocolVersion = "2025-11-25";

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient httpClient;
    private readonly BridgeOptions options;
    private readonly BridgeLogger logger;
    private readonly IAgentConnectivityTelemetrySink telemetry;
    private RemoteProtocolMode remoteProtocolMode;
    private string? remoteProtocolVersion;
    private string? remoteSessionId;
    private long nextRemoteRequestId = 1;

    public RemoteMcpClient(
        HttpClient httpClient,
        BridgeOptions options,
        BridgeLogger logger,
        IAgentConnectivityTelemetrySink? telemetry = null)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
        this.telemetry = telemetry ?? NoOpAgentConnectivityTelemetrySink.Instance;
    }

    public async Task<JsonDocument> ForwardAsync(
        string method,
        JsonElement localMessage,
        bool allowReconnectRetry,
        CancellationToken cancellationToken = default)
    {
        var maxAttempts = options.ReconnectOnError && allowReconnectRetry ? 2 : 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var stopwatch = Stopwatch.StartNew();
            var protocolWasNegotiated = remoteProtocolMode == RemoteProtocolMode.Unknown;
            try
            {
                logger.Log($"forwarding {method} to {options.Endpoint}; attempt {attempt}");
                await EnsureRemoteProtocolAsync(cancellationToken);
                var payload = CreateForwardPayload(method, localMessage, remoteProtocolMode);
                var response = await SendRemoteJsonRpcAsync(
                    payload,
                    remoteProtocolVersion ?? throw new InvalidOperationException("Remote MCP protocol version was not negotiated."),
                    requireSession: remoteProtocolMode == RemoteProtocolMode.Legacy,
                    cancellationToken);
                logger.Log($"completed {method}");
                await RecordTelemetryAsync(
                    method,
                    localMessage,
                    attempt,
                    success: true,
                    statusCode: null,
                    errorKind: null,
                    stopwatch.Elapsed.TotalMilliseconds,
                    protocolWasNegotiated,
                    reconnectAttempted: attempt > 1,
                    cancellationToken);
                return response;
            }
            catch (RemoteMcpRequestException ex) when (attempt < maxAttempts && ex.CanReconnectRetry)
            {
                await RecordTelemetryAsync(
                    method,
                    localMessage,
                    attempt,
                    success: false,
                    ex.StatusCode,
                    ClassifyError(ex),
                    stopwatch.Elapsed.TotalMilliseconds,
                    protocolWasNegotiated,
                    reconnectAttempted: true,
                    cancellationToken);
                logger.Log($"remote {method} failed with reconnectable error: {ex.Message}; renegotiating remote MCP protocol");
                ClearRemoteProtocol();
                await Task.Delay(options.RetryDelay, cancellationToken);
            }
            catch (RemoteMcpRequestException ex)
            {
                await RecordTelemetryAsync(
                    method,
                    localMessage,
                    attempt,
                    success: false,
                    ex.StatusCode,
                    ClassifyError(ex),
                    stopwatch.Elapsed.TotalMilliseconds,
                    protocolWasNegotiated,
                    reconnectAttempted: attempt > 1,
                    cancellationToken);
                throw;
            }
            catch (Exception ex)
            {
                await RecordTelemetryAsync(
                    method,
                    localMessage,
                    attempt,
                    success: false,
                    statusCode: null,
                    ex is OperationCanceledException ? "timeout" : "unknown",
                    stopwatch.Elapsed.TotalMilliseconds,
                    protocolWasNegotiated,
                    reconnectAttempted: attempt > 1,
                    cancellationToken);
                throw;
            }
        }

        throw new InvalidOperationException("Remote ContextHub MCP request failed without producing a response.");
    }

    private async ValueTask RecordTelemetryAsync(
        string method,
        JsonElement localMessage,
        int attempt,
        bool success,
        int? statusCode,
        string? errorKind,
        double clientElapsedMs,
        bool sessionWasInitialized,
        bool reconnectAttempted,
        CancellationToken cancellationToken)
    {
        var observation = new AgentConnectivityObservation(
            options.AgentId,
            options.AgentName,
            options.AgentVersion,
            BridgeOptions.BridgeVersion,
            options.Endpoint.Host,
            "mcp-streamable-http",
            method,
            ExtractToolName(method, localMessage),
            attempt,
            success,
            statusCode,
            errorKind,
            clientElapsedMs,
            null,
            sessionWasInitialized,
            reconnectAttempted,
            Guid.NewGuid().ToString("N"),
            "stdio-bridge",
            DateTimeOffset.UtcNow);
        await telemetry.RecordAsync(observation, cancellationToken);
    }

    private static string? ExtractToolName(string method, JsonElement localMessage)
    {
        if (!string.Equals(method, "tools/call", StringComparison.Ordinal) ||
            !localMessage.TryGetProperty("params", out var parameters) ||
            !parameters.TryGetProperty("name", out var name) ||
            name.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        return name.GetString();
    }

    private static string ClassifyError(RemoteMcpRequestException exception)
        => exception.StatusCode switch
        {
            401 or 403 => "auth",
            408 or 504 => "timeout",
            409 or 410 => "session",
            429 => "rate-limit",
            >= 500 => "server",
            _ when exception.InnerException is TaskCanceledException => "timeout",
            _ when exception.InnerException is HttpRequestException => "http",
            _ when exception.InnerException is JsonException => "parse",
            _ => "remote"
        };

    private JsonObject CreateForwardPayload(
        string method,
        JsonElement localMessage,
        RemoteProtocolMode protocolMode)
    {
        JsonObject parameters = new();
        if (localMessage.TryGetProperty("params", out var paramsElement) &&
            paramsElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            parameters = JsonNode.Parse(paramsElement.GetRawText()) as JsonObject
                ?? throw new RemoteMcpRequestException(
                    "MCP request params must be a JSON object.",
                    canReconnectRetry: false);
        }

        if (protocolMode == RemoteProtocolMode.Modern)
        {
            parameters["_meta"] = CreateModernRequestMeta();
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref nextRemoteRequestId),
            ["method"] = method,
            ["params"] = parameters
        };
    }

    private async Task EnsureRemoteProtocolAsync(CancellationToken cancellationToken)
    {
        if (remoteProtocolMode != RemoteProtocolMode.Unknown)
        {
            return;
        }

        try
        {
            var discoverPayload = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = Interlocked.Increment(ref nextRemoteRequestId),
                ["method"] = "server/discover",
                ["params"] = new JsonObject
                {
                    ["_meta"] = CreateModernRequestMeta()
                }
            };
            using var discoverResponse = await SendRemoteJsonRpcAsync(
                discoverPayload,
                ModernProtocolVersion,
                requireSession: false,
                cancellationToken);
            if (SupportsModernProtocol(discoverResponse.RootElement))
            {
                remoteProtocolMode = RemoteProtocolMode.Modern;
                remoteProtocolVersion = ModernProtocolVersion;
                remoteSessionId = null;
                logger.Log($"negotiated remote MCP protocol {ModernProtocolVersion}");
                return;
            }

            if (!IsLegacyDiscoveryResponse(discoverResponse.RootElement))
            {
                throw new RemoteMcpRequestException(
                    "Remote MCP server/discover response did not advertise a supported protocol version.",
                    canReconnectRetry: false);
            }
        }
        catch (RemoteMcpRequestException ex) when (CanFallbackToLegacy(ex))
        {
            logger.Log($"remote MCP discovery is unavailable ({ex.Message}); falling back to initialize");
        }

        var initializePayload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref nextRemoteRequestId),
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = PreferredLegacyProtocolVersion,
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "ContextHub.McpStdioBridge",
                    ["version"] = "1.0.0"
                }
            }
        };

        using var initializeResponse = await SendRemoteJsonRpcAsync(
            initializePayload,
            PreferredLegacyProtocolVersion,
            requireSession: false,
            cancellationToken);
        if (initializeResponse.RootElement.TryGetProperty("error", out var error))
        {
            throw new RemoteMcpRequestException(
                $"Remote ContextHub MCP initialize failed: {TrimForError(error.GetRawText())}",
                canReconnectRetry: false);
        }

        remoteProtocolMode = RemoteProtocolMode.Legacy;
        remoteProtocolVersion = ReadNegotiatedLegacyVersion(initializeResponse.RootElement);
        logger.Log($"negotiated legacy remote MCP protocol {remoteProtocolVersion}" +
            (string.IsNullOrWhiteSpace(remoteSessionId) ? " without a transport session" : " with a transport session"));
    }

    private async Task<JsonDocument> SendRemoteJsonRpcAsync(
        JsonObject payload,
        string protocolVersion,
        bool requireSession,
        CancellationToken cancellationToken)
    {
        var body = payload.ToJsonString(JsonOptions);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, options.Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            request.Headers.Accept.ParseAdd("application/json");
            request.Headers.Accept.ParseAdd("text/event-stream");
            request.Headers.Add("MCP-Protocol-Version", protocolVersion);
            if (string.Equals(protocolVersion, ModernProtocolVersion, StringComparison.Ordinal))
            {
                AddModernRoutingHeaders(request, payload);
            }
            if (requireSession && !string.IsNullOrWhiteSpace(remoteSessionId))
            {
                request.Headers.Add("Mcp-Session-Id", remoteSessionId);
            }

            request.Content = new StringContent(body, Encoding.UTF8, "application/json");

            using var response = await httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);
            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                throw CreateStatusException((int)response.StatusCode, content);
            }

            if (!string.Equals(protocolVersion, ModernProtocolVersion, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(remoteSessionId) &&
                response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
            {
                remoteSessionId = sessionValues.FirstOrDefault();
            }

            var json = ExtractJsonPayload(content);
            return JsonDocument.Parse(json);
        }
        catch (RemoteMcpRequestException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            throw new RemoteMcpRequestException(
                $"Remote ContextHub MCP HTTP request failed: {ex.Message}",
                canReconnectRetry: true,
                innerException: ex);
        }
        catch (TaskCanceledException ex)
        {
            throw new RemoteMcpRequestException(
                "Remote ContextHub MCP request timed out or was canceled.",
                canReconnectRetry: true,
                innerException: ex);
        }
        catch (JsonException ex)
        {
            throw new RemoteMcpRequestException(
                $"Remote ContextHub MCP response was not valid JSON: {ex.Message}",
                canReconnectRetry: true,
                innerException: ex);
        }
    }

    private RemoteMcpRequestException CreateStatusException(int statusCode, string content)
    {
        var trimmedContent = TrimForError(content);
        var message = $"Remote ContextHub MCP returned {statusCode}: {trimmedContent}";
        var canReconnectRetry = statusCode switch
        {
            401 or 403 => false,
            404 or 408 or 409 or 410 or 502 or 503 or 504 => true,
            >= 500 => true,
            _ => IsSessionInvalidContent(content)
        };

        return new RemoteMcpRequestException(message, canReconnectRetry, statusCode);
    }

    private void ClearRemoteProtocol()
    {
        if (!string.IsNullOrWhiteSpace(remoteSessionId))
        {
            logger.Log("clearing legacy remote MCP session id before reconnect");
        }

        remoteProtocolMode = RemoteProtocolMode.Unknown;
        remoteProtocolVersion = null;
        remoteSessionId = null;
    }

    private static JsonObject CreateModernRequestMeta()
        => new()
        {
            ["io.modelcontextprotocol/protocolVersion"] = ModernProtocolVersion,
            ["io.modelcontextprotocol/clientInfo"] = new JsonObject
            {
                ["name"] = "ContextHub.McpStdioBridge",
                ["version"] = BridgeOptions.BridgeVersion
            },
            ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
        };

    private static void AddModernRoutingHeaders(HttpRequestMessage request, JsonObject payload)
    {
        if (payload["method"]?.GetValue<string>() is not { Length: > 0 } method)
        {
            return;
        }

        request.Headers.Add("Mcp-Method", EncodeRoutingHeaderValue(method));
        if (GetModernRoutingName(method, payload["params"] as JsonObject) is { Length: > 0 } name)
        {
            request.Headers.Add("Mcp-Name", EncodeRoutingHeaderValue(name));
        }
    }

    private static string? GetModernRoutingName(string method, JsonObject? parameters)
        => method switch
        {
            "resources/read" => parameters?["uri"]?.GetValue<string>(),
            "prompts/get" or "tools/call" => parameters?["name"]?.GetValue<string>(),
            _ => null
        };

    private static string EncodeRoutingHeaderValue(string value)
    {
        var requiresEncoding = value.Length > 0 &&
            (char.IsWhiteSpace(value[0]) ||
             char.IsWhiteSpace(value[^1]) ||
             value.StartsWith("=?base64?", StringComparison.OrdinalIgnoreCase) ||
             value.Any(character => character is < '\u0020' or > '\u007e'));

        return requiresEncoding
            ? $"=?base64?{Convert.ToBase64String(Encoding.UTF8.GetBytes(value))}?="
            : value;
    }

    private static bool SupportsModernProtocol(JsonElement response)
    {
        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("supportedVersions", out var versions) ||
            versions.ValueKind != JsonValueKind.Array)
        {
            return false;
        }

        return versions.EnumerateArray().Any(version =>
            version.ValueKind == JsonValueKind.String &&
            string.Equals(version.GetString(), ModernProtocolVersion, StringComparison.Ordinal));
    }

    private static bool IsLegacyDiscoveryResponse(JsonElement response)
    {
        if (!response.TryGetProperty("error", out var error))
        {
            return response.TryGetProperty("result", out _);
        }

        return error.TryGetProperty("code", out var code) &&
               code.TryGetInt32(out var value) &&
               value is -32020 or -32601 or -32602;
    }

    private static bool CanFallbackToLegacy(RemoteMcpRequestException exception)
        => exception.StatusCode is 400 or 404 or 405;

    private static string ReadNegotiatedLegacyVersion(JsonElement response)
    {
        if (response.TryGetProperty("result", out var result) &&
            result.TryGetProperty("protocolVersion", out var protocolVersion) &&
            protocolVersion.ValueKind == JsonValueKind.String &&
            protocolVersion.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        return PreferredLegacyProtocolVersion;
    }

    private static string ExtractJsonPayload(string content)
    {
        var trimmed = content.Trim();
        if (trimmed.StartsWith('{'))
        {
            return trimmed;
        }

        var builder = new StringBuilder();
        foreach (var line in content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (line.StartsWith("data: ", StringComparison.Ordinal))
            {
                builder.AppendLine(line["data: ".Length..]);
            }
        }

        var json = builder.ToString().Trim();
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new RemoteMcpRequestException(
                "Remote ContextHub MCP response did not contain JSON or SSE data.",
                canReconnectRetry: true);
        }

        return json;
    }

    private static bool IsSessionInvalidContent(string content)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        var normalized = content.ToLowerInvariant();
        return normalized.Contains("session", StringComparison.Ordinal) &&
               (normalized.Contains("invalid", StringComparison.Ordinal) ||
                normalized.Contains("expired", StringComparison.Ordinal) ||
                normalized.Contains("not found", StringComparison.Ordinal));
    }

    private static string TrimForError(string value)
        => value.Length <= 500 ? value : value[..500];

    private enum RemoteProtocolMode
    {
        Unknown,
        Modern,
        Legacy
    }
}

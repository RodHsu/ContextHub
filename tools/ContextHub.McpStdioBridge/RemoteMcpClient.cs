using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContextHub.McpStdioBridge;

public sealed class RemoteMcpClient
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient httpClient;
    private readonly BridgeOptions options;
    private readonly BridgeLogger logger;
    private string? remoteSessionId;
    private long nextRemoteRequestId = 1;

    public RemoteMcpClient(HttpClient httpClient, BridgeOptions options, BridgeLogger logger)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
    }

    public async Task<JsonDocument> ForwardAsync(
        string method,
        JsonElement localMessage,
        bool allowReconnectRetry,
        CancellationToken cancellationToken = default)
    {
        var payload = CreateForwardPayload(method, localMessage);
        var maxAttempts = options.ReconnectOnError && allowReconnectRetry ? 2 : 1;

        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                logger.Log($"forwarding {method} to {options.Endpoint}; attempt {attempt}");
                await EnsureRemoteSessionAsync(cancellationToken);
                var response = await SendRemoteJsonRpcAsync(payload, requireSession: true, cancellationToken);
                logger.Log($"completed {method}");
                return response;
            }
            catch (RemoteMcpRequestException ex) when (attempt < maxAttempts && ex.CanReconnectRetry)
            {
                logger.Log($"remote {method} failed with reconnectable error: {ex.Message}; rebuilding remote MCP session");
                ClearRemoteSession();
                await Task.Delay(options.RetryDelay, cancellationToken);
            }
        }

        throw new InvalidOperationException("Remote ContextHub MCP request failed without producing a response.");
    }

    private JsonObject CreateForwardPayload(string method, JsonElement localMessage)
    {
        JsonNode? parameters = null;
        if (localMessage.TryGetProperty("params", out var paramsElement) &&
            paramsElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            parameters = JsonNode.Parse(paramsElement.GetRawText());
        }

        return new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref nextRemoteRequestId),
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject()
        };
    }

    private async Task EnsureRemoteSessionAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(remoteSessionId))
        {
            return;
        }

        var initializePayload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref nextRemoteRequestId),
            ["method"] = "initialize",
            ["params"] = new JsonObject
            {
                ["protocolVersion"] = "2025-06-18",
                ["capabilities"] = new JsonObject(),
                ["clientInfo"] = new JsonObject
                {
                    ["name"] = "ContextHub.McpStdioBridge",
                    ["version"] = "1.0.0"
                }
            }
        };

        _ = await SendRemoteJsonRpcAsync(initializePayload, requireSession: false, cancellationToken);
        if (string.IsNullOrWhiteSpace(remoteSessionId))
        {
            throw new RemoteMcpRequestException(
                "Remote ContextHub MCP initialize did not return Mcp-Session-Id.",
                canReconnectRetry: false);
        }
    }

    private async Task<JsonDocument> SendRemoteJsonRpcAsync(
        JsonObject payload,
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
            request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
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

            if (string.IsNullOrWhiteSpace(remoteSessionId) &&
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

    private void ClearRemoteSession()
    {
        if (!string.IsNullOrWhiteSpace(remoteSessionId))
        {
            logger.Log("clearing remote MCP session id before reconnect");
        }

        remoteSessionId = null;
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
}

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

var endpoint = Environment.GetEnvironmentVariable("CONTEXTHUB_MCP_ENDPOINT")
               ?? "https://context-hub.wjcy.org/mcp";
var token = ResolveToken();
var logPath = Environment.GetEnvironmentVariable("CONTEXTHUB_MCP_BRIDGE_LOG_PATH");
var bridge = new ContextHubMcpStdioBridge(new Uri(endpoint), token, logPath);
await bridge.RunAsync();

static string ResolveToken()
{
    var token = Environment.GetEnvironmentVariable("CONTEXTHUB_MCP_TOKEN");
    if (!string.IsNullOrWhiteSpace(token))
    {
        return token;
    }

    token = Environment.GetEnvironmentVariable("CONTEXTHUB_MCP_TOKEN", EnvironmentVariableTarget.User);
    if (!string.IsNullOrWhiteSpace(token))
    {
        return token;
    }

    token = Environment.GetEnvironmentVariable("CONTEXTHUB_MCP_TOKEN", EnvironmentVariableTarget.Machine);
    if (!string.IsNullOrWhiteSpace(token))
    {
        return token;
    }

    throw new InvalidOperationException("CONTEXTHUB_MCP_TOKEN is not set in process, user, or machine environment.");
}

internal sealed class ContextHubMcpStdioBridge(Uri endpoint, string token, string? logPath)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    private readonly HttpClient httpClient = new();
    private string? remoteSessionId;
    private long nextRemoteRequestId = 1;

    public async Task RunAsync()
    {
        Log("bridge started");
        string? line;
        while ((line = await Console.In.ReadLineAsync()) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                await HandleMessageAsync(document.RootElement);
            }
            catch (Exception ex)
            {
                Log($"unhandled message error: {ex}");
                await WriteResponseAsync(null, error: new JsonRpcError(-32603, ex.Message));
            }
        }

        Log("bridge stdin closed");
    }

    private async Task HandleMessageAsync(JsonElement message)
    {
        if (!message.TryGetProperty("method", out var methodElement))
        {
            return;
        }

        var method = methodElement.GetString();
        var hasId = message.TryGetProperty("id", out var id);

        if (!hasId)
        {
            if (string.Equals(method, "notifications/initialized", StringComparison.Ordinal))
            {
                return;
            }

            return;
        }

        try
        {
            switch (method)
            {
                case "initialize":
                    await WriteResponseAsync(id, result: CreateInitializeResult(message));
                    break;
                case "ping":
                    await WriteResponseAsync(id, result: new JsonObject());
                    break;
                case "tools/list":
                case "tools/call":
                case "resources/list":
                case "resources/templates/list":
                case "resources/read":
                case "prompts/list":
                case "prompts/get":
                    var remoteResponse = await ForwardToRemoteAsync(method, message);
                    await WriteRemoteResponseAsync(id, remoteResponse);
                    break;
                default:
                    await WriteResponseAsync(id, error: new JsonRpcError(-32601, $"Unsupported method: {method}"));
                    break;
            }
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(id, error: new JsonRpcError(-32603, ex.Message));
        }
    }

    private static JsonObject CreateInitializeResult(JsonElement message)
    {
        var requestedProtocolVersion = "2025-06-18";
        if (message.TryGetProperty("params", out var parameters) &&
            parameters.TryGetProperty("protocolVersion", out var protocolVersion) &&
            protocolVersion.ValueKind == JsonValueKind.String)
        {
            requestedProtocolVersion = protocolVersion.GetString() ?? requestedProtocolVersion;
        }

        return new JsonObject
        {
            ["protocolVersion"] = requestedProtocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = true
                },
                ["resources"] = new JsonObject(),
                ["prompts"] = new JsonObject()
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "ContextHub.McpStdioBridge",
                ["version"] = "1.0.0"
            }
        };
    }

    private async Task<JsonDocument> ForwardToRemoteAsync(string method, JsonElement localMessage)
    {
        Log($"forwarding {method} to {endpoint}");
        await EnsureRemoteSessionAsync();

        JsonNode? parameters = null;
        if (localMessage.TryGetProperty("params", out var paramsElement) &&
            paramsElement.ValueKind is not JsonValueKind.Undefined and not JsonValueKind.Null)
        {
            parameters = JsonNode.Parse(paramsElement.GetRawText());
        }

        var remotePayload = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = Interlocked.Increment(ref nextRemoteRequestId),
            ["method"] = method,
            ["params"] = parameters ?? new JsonObject()
        };

        var response = await SendRemoteJsonRpcAsync(remotePayload, requireSession: true);
        if (string.Equals(method, "tools/list", StringComparison.Ordinal))
        {
            response = RemoveNonStandardToolMetadata(response);
        }

        Log($"completed {method}");
        return response;
    }

    private static JsonDocument RemoveNonStandardToolMetadata(JsonDocument response)
    {
        var node = JsonNode.Parse(response.RootElement.GetRawText());
        if (node?["result"]?["tools"] is not JsonArray tools)
        {
            return response;
        }

        foreach (var tool in tools.OfType<JsonObject>())
        {
            tool.Remove("execution");
        }

        response.Dispose();
        return JsonDocument.Parse(node.ToJsonString(JsonOptions));
    }

    private async Task EnsureRemoteSessionAsync()
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

        _ = await SendRemoteJsonRpcAsync(initializePayload, requireSession: false);
        if (string.IsNullOrWhiteSpace(remoteSessionId))
        {
            throw new InvalidOperationException("Remote ContextHub MCP initialize did not return Mcp-Session-Id.");
        }
    }

    private async Task<JsonDocument> SendRemoteJsonRpcAsync(JsonObject payload, bool requireSession)
    {
        var body = payload.ToJsonString(JsonOptions);
        string? lastError = null;

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                request.Headers.Accept.ParseAdd("application/json");
                request.Headers.Accept.ParseAdd("text/event-stream");
                request.Headers.Add("MCP-Protocol-Version", "2025-06-18");
                if (requireSession && !string.IsNullOrWhiteSpace(remoteSessionId))
                {
                    request.Headers.Add("Mcp-Session-Id", remoteSessionId);
                }

                request.Content = new StringContent(body, Encoding.UTF8, "application/json");

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseContentRead);
                var content = await response.Content.ReadAsStringAsync();
                if (!response.IsSuccessStatusCode)
                {
                    lastError = $"Remote ContextHub MCP returned {(int)response.StatusCode}: {TrimForError(content)}";
                    if ((int)response.StatusCode >= 500 && attempt < 3)
                    {
                        Log($"{lastError}; retrying attempt {attempt + 1}");
                        await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt));
                        continue;
                    }

                    throw new InvalidOperationException(lastError);
                }

                if (string.IsNullOrWhiteSpace(remoteSessionId) &&
                    response.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues))
                {
                    remoteSessionId = sessionValues.FirstOrDefault();
                }

                var json = ExtractJsonPayload(content);
                return JsonDocument.Parse(json);
            }
            catch (HttpRequestException ex) when (attempt < 3)
            {
                lastError = ex.Message;
                Log($"HTTP exception: {ex.Message}; retrying attempt {attempt + 1}");
                await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt));
            }
        }

        throw new InvalidOperationException(lastError ?? "Remote ContextHub MCP request failed.");
    }

    private void Log(string message)
    {
        if (string.IsNullOrWhiteSpace(logPath))
        {
            return;
        }

        try
        {
            File.AppendAllText(logPath, $"[{DateTimeOffset.UtcNow:O}] {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must never interfere with stdio protocol traffic.
        }
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
            throw new InvalidOperationException("Remote ContextHub MCP response did not contain JSON or SSE data.");
        }

        return json;
    }

    private async Task WriteRemoteResponseAsync(JsonElement localId, JsonDocument remoteResponse)
    {
        var root = remoteResponse.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            await WriteResponseAsync(localId, errorElement: error);
            return;
        }

        if (!root.TryGetProperty("result", out var result))
        {
            await WriteResponseAsync(localId, error: new JsonRpcError(-32603, "Remote MCP response did not include result."));
            return;
        }

        await WriteResponseAsync(localId, resultElement: result);
    }

    private static async Task WriteResponseAsync(
        JsonElement? id,
        JsonObject? result = null,
        JsonElement? resultElement = null,
        JsonRpcError? error = null,
        JsonElement? errorElement = null)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0"
        };

        response["id"] = id.HasValue
            ? JsonNode.Parse(id.Value.GetRawText())
            : null;

        if (errorElement.HasValue)
        {
            response["error"] = JsonNode.Parse(errorElement.Value.GetRawText());
        }
        else if (error is not null)
        {
            response["error"] = new JsonObject
            {
                ["code"] = error.Code,
                ["message"] = error.Message
            };
        }
        else if (resultElement.HasValue)
        {
            response["result"] = JsonNode.Parse(resultElement.Value.GetRawText());
        }
        else
        {
            response["result"] = result ?? new JsonObject();
        }

        await Console.Out.WriteLineAsync(response.ToJsonString(JsonOptions));
        await Console.Out.FlushAsync();
    }

    private static string TrimForError(string value)
        => value.Length <= 500 ? value : value[..500];

    private sealed record JsonRpcError(int Code, string Message);
}

using System.Text.Json;
using System.Text.Json.Nodes;

namespace ContextHub.McpStdioBridge;

public sealed class StdioBridge
{
    private readonly RemoteMcpClient remoteClient;
    private readonly BridgeRetryPolicy retryPolicy;
    private readonly BridgeLogger logger;

    public StdioBridge(RemoteMcpClient remoteClient, BridgeRetryPolicy retryPolicy, BridgeLogger logger)
    {
        this.remoteClient = remoteClient;
        this.retryPolicy = retryPolicy;
        this.logger = logger;
    }

    public async Task RunAsync(TextReader input, TextWriter output, CancellationToken cancellationToken = default)
    {
        logger.Log("bridge started");
        string? line;
        while ((line = await input.ReadLineAsync(cancellationToken)) is not null)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line);
                await HandleMessageAsync(document.RootElement, output, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.Log($"unhandled message error: {ex}");
                await WriteResponseAsync(output, null, error: new JsonRpcError(-32603, ex.Message), cancellationToken: cancellationToken);
            }
        }

        logger.Log("bridge stdin closed");
    }

    public async Task HandleMessageAsync(
        JsonElement message,
        TextWriter output,
        CancellationToken cancellationToken = default)
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
                    await WriteResponseAsync(output, id, result: CreateInitializeResult(message), cancellationToken: cancellationToken);
                    break;
                case "ping":
                    await WriteResponseAsync(output, id, result: new JsonObject(), cancellationToken: cancellationToken);
                    break;
                case "tools/list":
                case "tools/call":
                case "resources/list":
                case "resources/templates/list":
                case "resources/read":
                case "prompts/list":
                case "prompts/get":
                    var allowRetry = retryPolicy.CanRetry(message, method);
                    var remoteResponse = await remoteClient.ForwardAsync(method, message, allowRetry, cancellationToken);
                    if (string.Equals(method, "tools/list", StringComparison.Ordinal))
                    {
                        remoteResponse = RemoveNonStandardToolMetadata(remoteResponse);
                    }

                    await WriteRemoteResponseAsync(output, id, remoteResponse, cancellationToken);
                    break;
                default:
                    await WriteResponseAsync(output, id, error: new JsonRpcError(-32601, $"Unsupported method: {method}"), cancellationToken: cancellationToken);
                    break;
            }
        }
        catch (Exception ex)
        {
            await WriteResponseAsync(output, id, error: new JsonRpcError(-32603, ex.Message), cancellationToken: cancellationToken);
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
        return JsonDocument.Parse(node.ToJsonString(RemoteMcpClient.JsonOptions));
    }

    private static async Task WriteRemoteResponseAsync(
        TextWriter output,
        JsonElement localId,
        JsonDocument remoteResponse,
        CancellationToken cancellationToken)
    {
        var root = remoteResponse.RootElement;
        if (root.TryGetProperty("error", out var error))
        {
            await WriteResponseAsync(output, localId, errorElement: error, cancellationToken: cancellationToken);
            return;
        }

        if (!root.TryGetProperty("result", out var result))
        {
            await WriteResponseAsync(
                output,
                localId,
                error: new JsonRpcError(-32603, "Remote MCP response did not include result."),
                cancellationToken: cancellationToken);
            return;
        }

        await WriteResponseAsync(output, localId, resultElement: result, cancellationToken: cancellationToken);
    }

    private static async Task WriteResponseAsync(
        TextWriter output,
        JsonElement? id,
        JsonObject? result = null,
        JsonElement? resultElement = null,
        JsonRpcError? error = null,
        JsonElement? errorElement = null,
        CancellationToken cancellationToken = default)
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

        await output.WriteLineAsync(response.ToJsonString(RemoteMcpClient.JsonOptions).AsMemory(), cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private sealed record JsonRpcError(int Code, string Message);
}

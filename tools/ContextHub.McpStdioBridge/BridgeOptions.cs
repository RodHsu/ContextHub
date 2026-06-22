namespace ContextHub.McpStdioBridge;

public sealed record BridgeOptions(
    Uri Endpoint,
    string Token,
    string? LogPath,
    TimeSpan RemoteTimeout,
    TimeSpan RetryDelay,
    bool ReconnectOnError)
{
    public static BridgeOptions FromEnvironment()
    {
        var endpoint = Environment.GetEnvironmentVariable("CONTEXTHUB_MCP_ENDPOINT")
                       ?? "https://context-hub.wjcy.org/mcp";

        return new BridgeOptions(
            new Uri(endpoint),
            ResolveToken(),
            Environment.GetEnvironmentVariable("CONTEXTHUB_MCP_BRIDGE_LOG_PATH"),
            TimeSpan.FromSeconds(ReadPositiveInt("CONTEXTHUB_MCP_BRIDGE_REMOTE_TIMEOUT_SECONDS", 45)),
            TimeSpan.FromMilliseconds(ReadPositiveInt("CONTEXTHUB_MCP_BRIDGE_RETRY_DELAY_MS", 350)),
            ReadBoolean("CONTEXTHUB_MCP_BRIDGE_RECONNECT_ON_ERROR", defaultValue: true));
    }

    private static string ResolveToken()
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

    private static int ReadPositiveInt(string name, int defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return int.TryParse(value, out var parsed) && parsed > 0 ? parsed : defaultValue;
    }

    private static bool ReadBoolean(string name, bool defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return defaultValue;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "1" or "true" or "yes" => true,
            "0" or "false" or "no" => false,
            _ => defaultValue
        };
    }
}

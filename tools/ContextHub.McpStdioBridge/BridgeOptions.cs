namespace ContextHub.McpStdioBridge;

public sealed record BridgeOptions(
    Uri Endpoint,
    string Token,
    string? LogPath,
    TimeSpan RemoteTimeout,
    TimeSpan RetryDelay,
    bool ReconnectOnError,
    string ProjectId = "ContextHub",
    string AgentId = "stdio-bridge",
    string AgentName = "ContextHub MCP stdio bridge",
    string AgentVersion = "",
    bool AgentTelemetryEnabled = true,
    string AgentTelemetryProfile = "Balanced",
    double AgentTelemetrySuccessSampleRate = 0.2,
    double AgentTelemetryFailureSampleRate = 1.0,
    TimeSpan AgentTelemetryUploadInterval = default,
    int AgentTelemetryMaxBatchSize = 100)
{
    public const string BridgeVersion = "1.0.0";

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
            ReadBoolean("CONTEXTHUB_MCP_BRIDGE_RECONNECT_ON_ERROR", defaultValue: true),
            ReadString("CONTEXTHUB_PROJECT_ID", "ContextHub"),
            ReadString("CONTEXTHUB_AGENT_ID", "stdio-bridge"),
            ReadString("CONTEXTHUB_AGENT_NAME", "ContextHub MCP stdio bridge"),
            ReadString("CONTEXTHUB_AGENT_VERSION", string.Empty),
            ReadBoolean("CONTEXTHUB_AGENT_TELEMETRY_ENABLED", defaultValue: true),
            ReadString("CONTEXTHUB_AGENT_TELEMETRY_PROFILE", "Balanced"),
            ReadRate("CONTEXTHUB_AGENT_TELEMETRY_SUCCESS_SAMPLE_RATE", 0.2),
            ReadRate("CONTEXTHUB_AGENT_TELEMETRY_FAILURE_SAMPLE_RATE", 1.0),
            TimeSpan.FromSeconds(ReadPositiveInt("CONTEXTHUB_AGENT_TELEMETRY_UPLOAD_INTERVAL_SECONDS", 15)),
            ReadPositiveInt("CONTEXTHUB_AGENT_TELEMETRY_MAX_BATCH_SIZE", 100));
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

    private static string ReadString(string name, string defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
    }

    private static double ReadRate(string name, double defaultValue)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return double.TryParse(value, out var parsed) ? Math.Clamp(parsed, 0, 1) : defaultValue;
    }
}

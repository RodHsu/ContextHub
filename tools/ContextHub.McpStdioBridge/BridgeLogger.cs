namespace ContextHub.McpStdioBridge;

public sealed class BridgeLogger
{
    public static readonly BridgeLogger None = new(null);

    private readonly string? logPath;

    private BridgeLogger(string? logPath)
    {
        this.logPath = logPath;
    }

    public static BridgeLogger FromPath(string? logPath)
        => string.IsNullOrWhiteSpace(logPath) ? None : new BridgeLogger(logPath);

    public void Log(string message)
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
}

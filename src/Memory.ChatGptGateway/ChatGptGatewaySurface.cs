namespace Memory.ChatGptGateway;

public enum ChatGptGatewaySurface
{
    General,
    Automation
}

public static class ChatGptGatewaySurfaceResolver
{
    public static ChatGptGatewaySurface Resolve(string? value)
        => Enum.TryParse<ChatGptGatewaySurface>(value?.Trim(), ignoreCase: true, out var surface)
            ? surface
            : throw new InvalidOperationException("ChatGptGateway:Surface must be General or Automation.");
}

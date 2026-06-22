namespace ContextHub.McpStdioBridge;

public sealed class RemoteMcpRequestException : Exception
{
    public RemoteMcpRequestException(string message, bool canReconnectRetry, int? statusCode = null, Exception? innerException = null)
        : base(message, innerException)
    {
        CanReconnectRetry = canReconnectRetry;
        StatusCode = statusCode;
    }

    public bool CanReconnectRetry { get; }

    public int? StatusCode { get; }
}

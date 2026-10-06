namespace Memory.Application;

/// <summary>Reindex selects an already configured generation; it cannot relabel inference output.</summary>
public static class EmbeddingModelIdentity
{
    public static string RequireConfigured(string? requestedKey, string configuredKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configuredKey);
        if (!string.IsNullOrWhiteSpace(requestedKey) &&
            !string.Equals(requestedKey.Trim(), configuredKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Reindex model key must match the configured embedding provider.");
        }

        return configuredKey;
    }
}

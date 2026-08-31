namespace Memory.ChatGptGateway;

internal static class OAuthKnowledgeBaseIdentity
{
    private static readonly string[] ReservedEmailDomains =
    [
        "example.com",
        "example.net",
        "example.org"
    ];

    public static string ResolveAccountName(string? username, string? displayName)
    {
        var normalizedUsername = username?.Trim() ?? string.Empty;
        var normalizedDisplayName = displayName?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalizedDisplayName)
            ? normalizedUsername
            : normalizedDisplayName;
    }

    public static string? ResolvePublishedEmail(string? email)
    {
        var normalized = email?.Trim() ?? string.Empty;
        var separator = normalized.LastIndexOf('@');
        if (separator <= 0 || separator == normalized.Length - 1)
        {
            return null;
        }

        var domain = normalized[(separator + 1)..];
        if (ReservedEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase) ||
            domain.EndsWith(".example", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith(".invalid", StringComparison.OrdinalIgnoreCase) ||
            domain.EndsWith(".test", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return normalized;
    }
}

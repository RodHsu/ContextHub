using Microsoft.AspNetCore.Http;

namespace Memory.ChatGptGateway;

public static class GatewayPublicPaths
{
    private static readonly IReadOnlySet<string> ResourceNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "mcp-chat",
        "mcp-automation"
    };

    public static bool IsProtectedResourceMetadata(PathString path)
        => IsMetadataPath(path, "/.well-known/oauth-protected-resource");

    public static bool IsAuthorizationServerMetadata(PathString path)
        => IsMetadataPath(path, "/.well-known/oauth-authorization-server");

    public static bool IsOpenIdConfiguration(PathString path)
        => IsMetadataPath(path, "/.well-known/openid-configuration");

    public static bool IsActorBootstrapPath(PathString path)
        => path.StartsWithSegments("/health/live", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/health/ready", StringComparison.OrdinalIgnoreCase) ||
           IsProtectedResourceMetadata(path) ||
           IsAuthorizationServerMetadata(path) ||
           IsOpenIdConfiguration(path) ||
           path.StartsWithSegments("/oauth/chat/authorize", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/oauth/chat/register", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/oauth/chat/token", StringComparison.OrdinalIgnoreCase);

    private static bool IsMetadataPath(PathString path, string root)
    {
        var value = path.Value ?? string.Empty;
        if (string.Equals(value, root, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!value.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var resource = value[(root.Length + 1)..];
        return ResourceNames.Contains(resource);
    }
}

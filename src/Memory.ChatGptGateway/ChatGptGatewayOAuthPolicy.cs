using Memory.Application;

namespace Memory.ChatGptGateway;

internal static class ChatGptGatewayOAuthPolicy
{
    public static string[] ResolveEffectiveScopes(ChatGptGatewayOptions options)
    {
        var scopes = options.OAuth.Scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var surface = ChatGptGatewaySurfaceResolver.Resolve(options.Surface);
        if (surface == ChatGptGatewaySurface.Automation)
        {
            if (!scopes.Contains(SecurityScopes.ScheduledGovernance, StringComparer.Ordinal))
            {
                scopes.Add(SecurityScopes.ScheduledGovernance);
            }
        }
        else
        {
            scopes.RemoveAll(scope => string.Equals(scope, SecurityScopes.ScheduledGovernance, StringComparison.Ordinal));
        }

        return scopes.ToArray();
    }

    public static bool TryResolveRequestedScopes(
        string requested,
        string resource,
        ChatGptGatewayOptions options,
        out string normalized)
    {
        var supported = ResolveEffectiveScopesForResource(resource, options);
        if (supported.Length == 0)
        {
            normalized = string.Empty;
            return false;
        }

        var supportedSet = supported.ToHashSet(StringComparer.Ordinal);
        var requestedScopes = requested
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (requestedScopes.Any(scope => !supportedSet.Contains(scope)))
        {
            normalized = string.Empty;
            return false;
        }

        var accepted = requestedScopes.Length == 0 ? supported : requestedScopes.Distinct(StringComparer.Ordinal).ToArray();
        if (IsScheduledGovernanceResource(resource, options) &&
            !accepted.Contains(SecurityScopes.ScheduledGovernance, StringComparer.Ordinal))
        {
            normalized = string.Empty;
            return false;
        }

        normalized = string.Join(' ', accepted);
        return true;
    }

    public static bool IsExpectedResource(string? resource, ChatGptGatewayOptions options)
        => !string.IsNullOrWhiteSpace(resource) &&
           !string.IsNullOrWhiteSpace(options.PublicMcpUrl) &&
           (string.Equals(resource.Trim(), options.PublicMcpUrl.Trim(), StringComparison.Ordinal) ||
            ChatGptGatewaySurfaceResolver.Resolve(options.Surface) == ChatGptGatewaySurface.General &&
            IsScheduledGovernanceResource(resource, options));

    private static string[] ResolveEffectiveScopesForResource(string resource, ChatGptGatewayOptions options)
    {
        if (!IsExpectedResource(resource, options))
        {
            return [];
        }

        var scopes = options.OAuth.Scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (IsScheduledGovernanceResource(resource, options))
        {
            if (!scopes.Contains(SecurityScopes.ScheduledGovernance, StringComparer.Ordinal))
            {
                scopes.Add(SecurityScopes.ScheduledGovernance);
            }
        }
        else
        {
            scopes.RemoveAll(scope => string.Equals(scope, SecurityScopes.ScheduledGovernance, StringComparison.Ordinal));
        }

        return scopes.ToArray();
    }

    private static bool IsScheduledGovernanceResource(string? resource, ChatGptGatewayOptions options)
        => !string.IsNullOrWhiteSpace(resource) &&
           !string.IsNullOrWhiteSpace(options.OAuth.ScheduledGovernanceResource) &&
           string.Equals(
               resource.Trim(),
               options.OAuth.ScheduledGovernanceResource.Trim(),
               StringComparison.Ordinal);
}

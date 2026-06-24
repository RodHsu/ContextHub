using Memory.Domain;

namespace Memory.Application;

public sealed record ContextHubRequestActor(
    Guid? TenantId,
    Guid? UserId,
    string Username,
    TenantUserRole? Role,
    IReadOnlyList<string> Scopes,
    IReadOnlyList<string> AllowedProjectIds,
    bool IsAuthenticated,
    bool IsServiceActor = false)
{
    public static ContextHubRequestActor Unrestricted { get; } = new(
        null,
        null,
        string.Empty,
        null,
        [],
        [],
        false);

    public bool HasUser => TenantId.HasValue && UserId.HasValue;

    public bool IsAdmin =>
        Role is TenantUserRole.Owner or TenantUserRole.Admin ||
        HasScope(SecurityScopes.SecurityManage);

    public bool HasScope(string scope)
        => Scopes.Any(value => string.Equals(value, scope, StringComparison.OrdinalIgnoreCase));
}

public static class SecurityScopes
{
    public const string MemoryRead = "memory:read";
    public const string MemoryWrite = "memory:write";
    public const string PreferencesRead = "preferences:read";
    public const string PreferencesWrite = "preferences:write";
    public const string TokenManage = "token:manage";
    public const string SecurityManage = "security:manage";
    public const string DashboardActAs = "dashboard:act-as";
    public const string LogsRead = "logs:read";
}

public static class ActorAuthorization
{
    public static void EnsureAuthenticatedUser(ContextHubRequestActor actor)
    {
        if (!actor.IsAuthenticated)
        {
            throw new UnauthorizedAccessException("Authentication is required.");
        }

        if (!actor.HasUser)
        {
            throw new UnauthorizedAccessException("Authenticated requests must resolve to a tenant user.");
        }
    }

    public static void EnsureScopeAllowed(ContextHubRequestActor actor, string scope)
    {
        EnsureAuthenticatedUser(actor);
        if (!actor.HasScope(scope))
        {
            throw new UnauthorizedAccessException($"Scope '{scope}' is required.");
        }
    }

    public static void EnsureAdminOrScopeAllowed(ContextHubRequestActor actor, string scope)
    {
        EnsureAuthenticatedUser(actor);
        if (!actor.IsAdmin && !actor.HasScope(scope))
        {
            throw new UnauthorizedAccessException($"Admin role or scope '{scope}' is required.");
        }
    }

    public static void EnsureProjectAllowed(ContextHubRequestActor actor, string projectId, bool write)
    {
        if (!actor.HasUser)
        {
            return;
        }

        if (ProjectContext.IsShared(projectId) || ProjectContext.IsUser(projectId))
        {
            return;
        }

        if (actor.AllowedProjectIds.Count == 0 ||
            actor.AllowedProjectIds.Any(x => string.Equals(x, projectId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        throw new UnauthorizedAccessException(write
            ? $"Project '{projectId}' is not writable for the current token."
            : $"Project '{projectId}' is not readable for the current token.");
    }

    public static void EnsureProjectsAllowed(ContextHubRequestActor actor, IReadOnlyList<string> projectIds, bool write)
    {
        foreach (var projectId in projectIds)
        {
            EnsureProjectAllowed(actor, projectId, write);
        }
    }
}

public interface IRequestActorAccessor
{
    ContextHubRequestActor Current { get; set; }
}

public sealed class RequestActorAccessor : IRequestActorAccessor
{
    private static readonly AsyncLocal<ContextHubRequestActor?> CurrentActor = new();

    public ContextHubRequestActor Current
    {
        get => CurrentActor.Value ?? ContextHubRequestActor.Unrestricted;
        set => CurrentActor.Value = value;
    }
}

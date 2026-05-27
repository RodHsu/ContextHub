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
}

public interface IRequestActorAccessor
{
    ContextHubRequestActor Current { get; set; }
}

public sealed class RequestActorAccessor : IRequestActorAccessor
{
    public ContextHubRequestActor Current { get; set; } = ContextHubRequestActor.Unrestricted;
}

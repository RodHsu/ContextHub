using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed record SecretManagementExpiry(Guid SecretId, long Revision, int? CurrentVersion, SecretVersionState? VersionState, DateTimeOffset? ExpiresAt);
public sealed record SecretManagementLeaseEntry(SecretLeaseKind Kind, SecretLeaseState State, DateTimeOffset ExpiresAt, bool IsExpired, int UsedCount, int RemainingUses, int ActiveUses);
public sealed record SecretManagementHistoryEntry(SecretAccessOperation Operation, DateTimeOffset CreatedAt);
public sealed record SecretManagementLeasePage(Guid SecretId, IReadOnlyList<SecretManagementLeaseEntry> Items, int Limit, int Offset, bool HasMore)
{
    public bool SensitiveDetailsMasked => true;
}
public sealed record SecretManagementHistoryPage(Guid SecretId, IReadOnlyList<SecretManagementHistoryEntry> Items, int Limit, int Offset, bool HasMore)
{
    public bool ActorMasked => true;
    public bool PurposeMasked => true;
    public bool TargetMasked => true;
    public bool DeniedDetailsMasked => true;
}

public interface ISecretManagementReadService
{
    Task<SecretManagementExpiry?> ReadExpiryAsync(string projectId, Guid secretId, CancellationToken ct);
    Task<SecretManagementLeasePage?> ReadLeasesAsync(string projectId, Guid secretId, int limit, int offset, CancellationToken ct);
    Task<SecretManagementHistoryPage?> ReadHistoryAsync(string projectId, Guid secretId, int limit, int offset, CancellationToken ct);
}

public sealed class SecretManagementReadService(IApplicationDbContext dbContext, IRequestActorAccessor actorAccessor,
    IPlatformFoundationStore foundation, IClock clock, TimeProvider? timeProvider = null) : ISecretManagementReadService
{
    public async Task<SecretManagementExpiry?> ReadExpiryAsync(string projectId, Guid secretId, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = operation.Token;
        var authorized = await AuthorizeAsync(projectId, secretId, ct);
        if (authorized is null) return null;
        // Only safe scalar version fields enter the projection; encrypted material is never loaded.
        var version = await dbContext.SecretVersions.AsNoTracking()
            .Where(x => x.SecretId == secretId && x.Id == authorized.CurrentVersionId)
            .Select(x => new { x.VersionNumber, x.State, x.ExpiresAt }).SingleOrDefaultAsync(ct);
        return new(secretId, authorized.Revision, version?.VersionNumber, version?.State, version?.ExpiresAt);
    }

    public async Task<SecretManagementLeasePage?> ReadLeasesAsync(string projectId, Guid secretId, int limit, int offset, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = operation.Token;
        var authorized = await AuthorizeAsync(projectId, secretId, ct);
        if (authorized is null) return null;
        var actor = actorAccessor.Current;
        var pageLimit = Math.Clamp(limit, 1, 100);
        var pageOffset = Math.Max(0, offset);
        var now = clock.UtcNow;
        var rows = await Scope(dbContext.SecretLeases.AsNoTracking(), actor)
            .Where(x => x.SecretId == secretId && ProjectContext.Matches(x.ProjectId, authorized.ProjectId))
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(pageOffset).Take(pageLimit + 1)
            .Select(x => new SecretManagementLeaseEntry(x.Kind, x.State, x.ExpiresAt, x.ExpiresAt <= now,
                x.UsedCount, x.MaxUses > x.UsedCount ? x.MaxUses - x.UsedCount : 0, x.ActiveUses))
            .ToArrayAsync(ct);
        return new(secretId, rows.Take(pageLimit).ToArray(), pageLimit, pageOffset, rows.Length > pageLimit);
    }

    public async Task<SecretManagementHistoryPage?> ReadHistoryAsync(string projectId, Guid secretId, int limit, int offset, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = operation.Token;
        var authorized = await AuthorizeAsync(projectId, secretId, ct);
        if (authorized is null) return null;
        var actor = actorAccessor.Current;
        var pageLimit = Math.Clamp(limit, 1, 100);
        var pageOffset = Math.Max(0, offset);
        // Denied rows are excluded before pagination and never contribute to a count or flag.
        var rows = await Scope(dbContext.SecretAccessEvents.AsNoTracking(), actor)
            .Where(x => x.SecretId == secretId && ProjectContext.Matches(x.ProjectId, authorized.ProjectId) && x.Allowed)
            .OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip(pageOffset).Take(pageLimit + 1)
            .Select(x => new SecretManagementHistoryEntry(x.Operation, x.CreatedAt)).ToArrayAsync(ct);
        return new(secretId, rows.Take(pageLimit).ToArray(), pageLimit, pageOffset, rows.Length > pageLimit);
    }

    private async Task<VisibleSecret?> AuthorizeAsync(string projectId, Guid secretId, CancellationToken ct)
    {
        var actor = actorAccessor.Current;
        // Existing management identity, read/manage scopes and resource policy intersect. No new role or grant.
        if (!actor.IsAuthenticated || !actor.HasUser || !actor.IsAdmin) throw Unavailable();
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.SecretsRead);
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.SecretsManage);
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("An explicit project is required.");
        var project = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
        var secret = await Scope(dbContext.Secrets.AsNoTracking(), actor)
            .Where(x => x.Id == secretId && ProjectContext.Matches(x.ProjectId, project))
            .Select(x => new VisibleSecret(x.ProjectId, x.Revision, x.CurrentVersionId)).SingleOrDefaultAsync(ct);
        if (secret is null) return null;
        var principal = actor.UserId!.Value.ToString("D");
        foreach (var right in new[] { "secret.metadata", "secret.manage" })
        {
            var decision = await foundation.EvaluateAsync(project, principal, [right], "Secret", secretId.ToString("D"), ct);
            if (decision.Decisions.Count != 1 || !decision.Decisions[0].Allowed) return null;
        }
        // Same resource -> secret policy -> project policy precedence as SecretManagementService.
        var now = clock.UtcNow;
        var grants = await Scope(dbContext.SecretGrants.AsNoTracking(), actor)
            .Where(x => x.SecretId == secretId && (x.PrincipalId == principal || x.PrincipalId == "*")
                && x.Right == SecretRight.Manage && (x.ExpiresAt == null || x.ExpiresAt > now))
            .Select(x => x.Effect).ToArrayAsync(ct);
        var secretPolicies = await Scope(dbContext.SecretPolicies.AsNoTracking(), actor)
            .Where(x => ProjectContext.Matches(x.ProjectId, project) && x.SecretId == secretId
                && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == SecretRight.Manage)
            .Select(x => x.Effect).ToArrayAsync(ct);
        var projectPolicies = await Scope(dbContext.SecretPolicies.AsNoTracking(), actor)
            .Where(x => ProjectContext.Matches(x.ProjectId, project) && x.SecretId == null
                && (x.PrincipalId == principal || x.PrincipalId == "*") && x.Right == SecretRight.Manage)
            .Select(x => x.Effect).ToArrayAsync(ct);
        var tier = grants.Length > 0 ? grants : secretPolicies.Length > 0 ? secretPolicies : projectPolicies;
        return tier.Length > 0 && !tier.Contains(AuthorizationEffect.Deny) && tier.Contains(AuthorizationEffect.Allow) ? secret : null;
    }

    private static IQueryable<T> Scope<T>(IQueryable<T> query, ContextHubRequestActor actor) where T : class
    {
        query = actor.IsServiceActor
            ? query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId)
            : query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId && EF.Property<Guid?>(x, "OwnerUserId") == actor.UserId);
        if (actor.AllowedProjectIds.Count == 0) return query;
        var allowed = ProjectContext.IdentityKeys(actor.AllowedProjectIds);
        return query.Where(x => allowed.Contains(ProjectContext.IdentityKey(EF.Property<string>(x, "ProjectId"))) ||
                                ProjectContext.Matches(EF.Property<string>(x, "ProjectId"), ProjectContext.SharedProjectId) ||
                                ProjectContext.Matches(EF.Property<string>(x, "ProjectId"), ProjectContext.UserProjectId));
    }

    private static UnauthorizedAccessException Unavailable() => new("Secret management read is unavailable.");
    private sealed record VisibleSecret(string ProjectId, long Revision, Guid? CurrentVersionId);
}

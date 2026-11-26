using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class GovernanceProjectScopeResolver(
    IApplicationDbContext dbContext,
    IAccessibleProjectService accessibleProjects,
    IRequestActorAccessor actorAccessor) : IGovernanceProjectScopeResolver
{
    internal static async Task EnsureDurableGrantsAsync(
        IApplicationDbContext dbContext, Guid tenantId, IReadOnlyList<string> projectIds,
        bool requireWrite, CancellationToken cancellationToken)
    {
        var projectKeys = ProjectContext.IdentityKeys(projectIds);
        var denied = await DeniedGrants(dbContext, tenantId, requireWrite)
            .AnyAsync(grant => projectKeys.Contains(ProjectContext.IdentityKey(grant.ProjectId)), cancellationToken);
        if (denied)
        {
            throw new UnauthorizedAccessException("A durable project grant no longer authorizes the governance scope.");
        }
    }

    private static IQueryable<TenantProjectGrant> DeniedGrants(
        IApplicationDbContext dbContext, Guid tenantId, bool requireWrite)
        => dbContext.TenantProjectGrants.AsNoTracking()
            .Where(grant => grant.TenantId == tenantId && (!grant.CanRead || (requireWrite && !grant.CanWrite)));

    public static async Task<HashSet<string>> ReadDeniedProjectsAsync(
        IApplicationDbContext dbContext, Guid tenantId, CancellationToken cancellationToken)
    {
        var projects = await DeniedGrants(dbContext, tenantId, requireWrite: false)
            .Select(grant => grant.ProjectId)
            .ToArrayAsync(cancellationToken);
        return projects.ToHashSet(ProjectContext.IdentityComparer);
    }

    public static bool CanReadRecordedScope(
        ContextHubRequestActor actor, IReadOnlyCollection<string> projects, IReadOnlySet<string> denied)
        => (actor.AllowedProjectIds.Count == 0 || projects.Count != 0) &&
            projects.All(project => !denied.Contains(project) &&
                (actor.AllowedProjectIds.Count == 0 || ProjectContext.IsShared(project) || ProjectContext.IsUser(project) ||
                 actor.AllowedProjectIds.Contains(project, ProjectContext.IdentityComparer)));

    public static async Task<ILookup<string, string>> ReadRecordedRunScopesAsync(
        IApplicationDbContext dbContext, ContextHubRequestActor actor, string[] runIds, CancellationToken cancellationToken)
    {
        var receiptScopes = dbContext.GovernanceRunReceipts.AsNoTracking()
            .Where(row => row.TenantId == actor.TenantId && row.OwnerUserId == actor.UserId && runIds.Contains(row.GovernanceRunId))
            .Select(row => new { row.GovernanceRunId, row.ProjectIdsJson });
        var batchScopes = dbContext.GovernanceBatchRuns.AsNoTracking()
            .Where(row => row.TenantId == actor.TenantId && row.OwnerUserId == actor.UserId && runIds.Contains(row.GovernanceRunId))
            .Select(row => new { row.GovernanceRunId, row.ProjectIdsJson });
        var scopes = await receiptScopes.Union(batchScopes).ToArrayAsync(cancellationToken);
        return scopes.SelectMany(scope => (JsonSerializer.Deserialize<string[]>(scope.ProjectIdsJson) ?? [])
                .Select(projectId => (scope.GovernanceRunId, ProjectId: projectId)))
            .ToLookup(scope => scope.GovernanceRunId, scope => scope.ProjectId, StringComparer.Ordinal);
    }

    public async Task<IReadOnlyList<AccessibleProjectResult>> ResolveAsync(
        IReadOnlyList<string>? requestedProjectIds,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var requested = requestedProjectIds?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => ProjectContext.Normalize(x))
            .Distinct(ProjectContext.IdentityComparer)
            .ToArray();
        var durableGrants = actor.TenantId.HasValue
            ? await dbContext.TenantProjectGrants
                .AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId.Value)
                .ToDictionaryAsync(x => x.ProjectId, ProjectContext.IdentityComparer, cancellationToken)
            : new Dictionary<string, TenantProjectGrant>(ProjectContext.IdentityComparer);

        if (requested is { Length: > 0 })
        {
            return requested.Select(projectId =>
            {
                ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
                durableGrants.TryGetValue(projectId, out var grant);
                return new AccessibleProjectResult(
                    projectId,
                    CanRead: grant?.CanRead ?? true,
                    CanWrite: actor.HasScope(SecurityScopes.MemoryWrite) &&
                              (grant?.CanWrite ?? true) &&
                              (actor.AllowedProjectIds.Count == 0 ||
                               actor.AllowedProjectIds.Contains(projectId, Memory.Application.ProjectContext.IdentityComparer)));
            }).ToArray();
        }

        var visibleProjects = await accessibleProjects.ListAsync(0, cancellationToken);
        var durableProjectIds = await dbContext.MemoryItems
            .AsNoTracking()
            .ForActor(actor)
            .Where(x => !ProjectContext.Matches(x.ProjectId, ProjectContext.SharedProjectId) && !ProjectContext.Matches(x.ProjectId, ProjectContext.UserProjectId))
            .Select(x => x.ProjectId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var projectIds = visibleProjects.Where(x => x.CanRead).Select(x => x.ProjectId)
            .Concat(durableProjectIds)
            .Select(x => ProjectContext.Normalize(x))
            .Where(projectId => actor.AllowedProjectIds.Count == 0 ||
                                actor.AllowedProjectIds.Contains(projectId, Memory.Application.ProjectContext.IdentityComparer))
            .Distinct(ProjectContext.IdentityComparer)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return projectIds.Select(projectId =>
        {
            durableGrants.TryGetValue(projectId, out var grant);
            return new AccessibleProjectResult(
                projectId,
                CanRead: grant?.CanRead ?? true,
                CanWrite: actor.HasScope(SecurityScopes.MemoryWrite) &&
                          (grant?.CanWrite ?? true) &&
                          (actor.AllowedProjectIds.Count == 0 ||
                           actor.AllowedProjectIds.Contains(projectId, Memory.Application.ProjectContext.IdentityComparer)));
        })
            .ToArray();
    }
}

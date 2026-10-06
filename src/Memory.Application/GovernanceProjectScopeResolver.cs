using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class GovernanceProjectScopeResolver(
    IApplicationDbContext dbContext,
    IAccessibleProjectService accessibleProjects,
    IRequestActorAccessor actorAccessor) : IGovernanceProjectScopeResolver
{
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

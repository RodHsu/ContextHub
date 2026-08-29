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
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (requested is { Length: > 0 })
        {
            return requested.Select(projectId =>
            {
                ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
                return new AccessibleProjectResult(
                    projectId,
                    CanRead: true,
                    CanWrite: actor.HasScope(SecurityScopes.MemoryWrite) &&
                              (actor.AllowedProjectIds.Count == 0 ||
                               actor.AllowedProjectIds.Contains(projectId, StringComparer.OrdinalIgnoreCase)));
            }).ToArray();
        }

        var visibleProjects = await accessibleProjects.ListAsync(0, cancellationToken);
        var durableProjectIds = await dbContext.MemoryItems
            .AsNoTracking()
            .ForActor(actor)
            .Where(x => x.ProjectId != ProjectContext.SharedProjectId && x.ProjectId != ProjectContext.UserProjectId)
            .Select(x => x.ProjectId)
            .Distinct()
            .ToListAsync(cancellationToken);

        var projectIds = visibleProjects.Where(x => x.CanRead).Select(x => x.ProjectId)
            .Concat(durableProjectIds)
            .Select(x => ProjectContext.Normalize(x))
            .Where(projectId => actor.AllowedProjectIds.Count == 0 ||
                                actor.AllowedProjectIds.Contains(projectId, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return projectIds.Select(projectId => new AccessibleProjectResult(
            projectId,
            CanRead: true,
            CanWrite: actor.HasScope(SecurityScopes.MemoryWrite) &&
                      (actor.AllowedProjectIds.Count == 0 ||
                       actor.AllowedProjectIds.Contains(projectId, StringComparer.OrdinalIgnoreCase))))
            .ToArray();
    }
}

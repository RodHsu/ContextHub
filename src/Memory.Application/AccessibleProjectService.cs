using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class AccessibleProjectService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor) : IAccessibleProjectService
{
    public async Task<IReadOnlyList<AccessibleProjectResult>> ListAsync(int limit, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var take = Math.Clamp(limit, 1, 200);

        if (actor.AllowedProjectIds.Count > 0)
        {
            return actor.AllowedProjectIds
                .Select(projectId => ProjectContext.Normalize(projectId))
                .Where(id => !string.Equals(id, ProjectContext.DefaultProjectId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
                .Take(take)
                .Select(id => new AccessibleProjectResult(id, true, actor.HasScope(SecurityScopes.MemoryWrite)))
                .ToArray();
        }

        var knownProjectIds = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
            .Select(x => x.ProjectId)
            .Concat(dbContext.ConversationSessions.AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
                .Select(x => x.ProjectId))
            .Concat(dbContext.ConversationCheckpoints.AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
                .Select(x => x.ProjectId))
            .Concat(dbContext.ConversationInsights.AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
                .Select(x => x.ProjectId))
            .Concat(dbContext.TenantProjectGrants.AsNoTracking()
                .Where(x => x.TenantId == actor.TenantId && x.CanRead)
                .Select(x => x.ProjectId))
            .ToListAsync(cancellationToken);

        return knownProjectIds
            .Select(projectId => ProjectContext.Normalize(projectId))
            .Where(id => !string.Equals(id, ProjectContext.DefaultProjectId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .Take(take)
            .Select(id => new AccessibleProjectResult(id, true, actor.HasScope(SecurityScopes.MemoryWrite)))
            .ToArray();
    }
}

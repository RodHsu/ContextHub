using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class ProjectInformationService(
    IApplicationDbContext dbContext,
    ICacheVersionStore cacheStore,
    IClock clock,
    IRequestActorAccessor actorAccessor,
    IMaintenanceCoordinator maintenanceCoordinator) : IProjectInformationService
{
    private const string ExternalKey = "system:project-information";
    private const string SourceType = "project-information";

    public async Task<ProjectInformationResult?> GetAsync(string projectId, CancellationToken cancellationToken)
    {
        var normalizedProjectId = ProjectContext.Normalize(projectId);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        ActorAuthorization.EnsureProjectAllowed(actor, normalizedProjectId, write: false);

        var item = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.ProjectId == normalizedProjectId && x.ExternalKey == ExternalKey)
            .Where(x => !actor.HasUser || (x.TenantId == actor.TenantId && (actor.IsServiceActor || x.OwnerUserId == actor.UserId)))
            .FirstOrDefaultAsync(cancellationToken);

        return item is null ? null : Map(item);
    }

    public async Task<ProjectInformationResult> UpsertAsync(ProjectInformationUpdateRequest request, CancellationToken cancellationToken)
    {
        var normalizedProjectId = ProjectContext.Normalize(request.ProjectId);
        if (ProjectContext.IsShared(normalizedProjectId) || ProjectContext.IsUser(normalizedProjectId))
        {
            throw new InvalidOperationException("Project information can only be stored for a regular ProjectId.");
        }

        var description = request.Description?.Trim() ?? string.Empty;
        if (description.Length > 12_000)
        {
            throw new InvalidOperationException("Project information description must not exceed 12000 characters.");
        }

        var displayName = string.IsNullOrWhiteSpace(request.DisplayName) ? normalizedProjectId : request.DisplayName.Trim();
        if (displayName.Length > 200)
        {
            throw new InvalidOperationException("Project display name must not exceed 200 characters.");
        }

        if (!actorAccessor.Current.IsServiceActor)
        {
            await maintenanceCoordinator.EnsureWriteAllowedAsync("project_information_upsert", cancellationToken);
        }

        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, normalizedProjectId, write: true);

        var item = await dbContext.MemoryItems
            .Where(x => x.ProjectId == normalizedProjectId && x.ExternalKey == ExternalKey)
            .Where(x => !actor.HasUser || (x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId))
            .FirstOrDefaultAsync(cancellationToken);
        var now = clock.UtcNow;

        if (item is null)
        {
            item = new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = normalizedProjectId,
                ExternalKey = ExternalKey,
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                SourceType = SourceType,
                SourceRef = normalizedProjectId,
                Tags = ["project-information"],
                Importance = 1m,
                Confidence = 1m,
                CreatedAt = now
            };
            await dbContext.MemoryItems.AddAsync(item, cancellationToken);
        }
        else
        {
            item.Version++;
        }

        item.Title = displayName;
        item.Content = description;
        item.Summary = description.Length <= 500 ? description : description[..500];
        item.Status = MemoryStatus.Active;
        item.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementProjectAsync(normalizedProjectId, cancellationToken);

        return Map(item);
    }

    private static ProjectInformationResult Map(MemoryItem item)
        => new(item.Id, item.ProjectId, item.Title, item.Content, item.UpdatedAt);
}

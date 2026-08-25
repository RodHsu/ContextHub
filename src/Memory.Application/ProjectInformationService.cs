using Microsoft.EntityFrameworkCore;
using Memory.Domain;
using System.Text.Json;

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
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

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

    public Task<ProjectInformationResult> UpsertAsync(ProjectInformationUpdateRequest request, CancellationToken cancellationToken)
        => UpsertCoreAsync(request, allowInteractiveDisplayNameUpdate: true, cancellationToken);

    public Task<ProjectInformationResult> UpdateFromAgentAsync(ProjectInformationAgentUpdateRequest request, CancellationToken cancellationToken)
        => UpsertCoreAsync(
            new ProjectInformationUpdateRequest(request.ProjectId, null, request.Description),
            allowInteractiveDisplayNameUpdate: false,
            cancellationToken);

    private async Task<ProjectInformationResult> UpsertCoreAsync(
        ProjectInformationUpdateRequest request,
        bool allowInteractiveDisplayNameUpdate,
        CancellationToken cancellationToken)
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

        var actor = actorAccessor.Current;
        var requestedDisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? normalizedProjectId : request.DisplayName.Trim();
        if (allowInteractiveDisplayNameUpdate && actor.IsInteractiveUser && requestedDisplayName.Length > 200)
        {
            throw new InvalidOperationException("Project display name must not exceed 200 characters.");
        }

        if (!actorAccessor.Current.IsServiceActor)
        {
            await maintenanceCoordinator.EnsureWriteAllowedAsync("project_information_upsert", cancellationToken);
        }

        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, normalizedProjectId, write: true);

        var item = await dbContext.MemoryItems
            .Where(x => x.ProjectId == normalizedProjectId && x.ExternalKey == ExternalKey)
            .Where(x => !actor.HasUser || (x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId))
            .FirstOrDefaultAsync(cancellationToken);
        var now = clock.UtcNow;
        var previousDisplayName = item?.Title;
        var displayName = allowInteractiveDisplayNameUpdate && actor.IsInteractiveUser
            ? requestedDisplayName
            : string.IsNullOrWhiteSpace(previousDisplayName)
                ? normalizedProjectId
                : previousDisplayName;

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
        item.UpdatedAt = now;
        if (allowInteractiveDisplayNameUpdate && actor.IsInteractiveUser && !string.Equals(previousDisplayName ?? normalizedProjectId, displayName, StringComparison.Ordinal))
        {
            await dbContext.SecurityAuditEvents.AddAsync(new SecurityAuditEvent
            {
                TenantId = actor.TenantId,
                ActorUserId = actor.UserId,
                EventType = SecurityAuditEventType.ProjectDisplayNameUpdated,
                Outcome = "Succeeded",
                DetailsJson = JsonSerializer.Serialize(new
                {
                    projectId = normalizedProjectId,
                    previousDisplayName = previousDisplayName ?? normalizedProjectId,
                    displayName
                }, JsonOptions),
                CreatedAt = now
            }, cancellationToken);
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementProjectAsync(normalizedProjectId, cancellationToken);

        return Map(item);
    }

    public async Task<ProjectInformationResult> UpdateLifecycleAsync(ProjectLifecycleUpdateRequest request, CancellationToken cancellationToken)
    {
        var normalizedProjectId = ProjectContext.Normalize(request.ProjectId);
        if (ProjectContext.IsShared(normalizedProjectId) || ProjectContext.IsUser(normalizedProjectId))
        {
            throw new InvalidOperationException("Project lifecycle can only be managed for a regular ProjectId.");
        }

        if (!actorAccessor.Current.IsServiceActor)
        {
            await maintenanceCoordinator.EnsureWriteAllowedAsync("project_information_lifecycle", cancellationToken);
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
                Title = normalizedProjectId,
                CreatedAt = now
            };
            await dbContext.MemoryItems.AddAsync(item, cancellationToken);
        }
        else
        {
            item.Version++;
        }

        var lifecycle = ReadLifecycle(item.MetadataJson);
        lifecycle = request.Action switch
        {
            ProjectLifecycleAction.Hide => lifecycle with { IsHidden = true },
            ProjectLifecycleAction.Unhide => lifecycle with { IsHidden = false },
            ProjectLifecycleAction.Archive => lifecycle with { ArchivedAt = lifecycle.ArchivedAt ?? now },
            ProjectLifecycleAction.Restore => lifecycle with { ArchivedAt = null },
            _ => throw new InvalidOperationException("Unsupported project lifecycle action.")
        };

        item.Status = lifecycle.ArchivedAt is null ? MemoryStatus.Active : MemoryStatus.Archived;
        item.Tags = lifecycle.IsHidden
            ? item.Tags.Append("project-hidden").Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : item.Tags.Where(tag => !string.Equals(tag, "project-hidden", StringComparison.OrdinalIgnoreCase)).ToArray();
        item.MetadataJson = JsonSerializer.Serialize(lifecycle, JsonOptions);
        item.UpdatedAt = now;
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementProjectAsync(normalizedProjectId, cancellationToken);
        return Map(item);
    }

    public async Task<IReadOnlyList<ProjectInformationListItem>> ListAsync(bool includeInactive, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var items = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.ProjectId != ProjectContext.SharedProjectId && x.ProjectId != ProjectContext.UserProjectId)
            .Where(x => !actor.HasUser || (x.TenantId == actor.TenantId && (actor.IsServiceActor || x.OwnerUserId == actor.UserId)))
            .ToListAsync(cancellationToken);
        var informationByProject = items
            .Where(x => x.ExternalKey == ExternalKey)
            .GroupBy(x => x.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.OrderByDescending(item => item.UpdatedAt).First(), StringComparer.OrdinalIgnoreCase);

        return items.GroupBy(x => x.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Select(group =>
            {
                informationByProject.TryGetValue(group.Key, out var information);
                var result = information is null
                    ? new ProjectInformationResult(Guid.Empty, group.Key, group.Key, string.Empty, group.Max(item => item.UpdatedAt))
                    : Map(information);
                return new ProjectInformationListItem(result, group.Count());
            })
            .Where(item => includeInactive || (!item.Information.IsHidden && !item.Information.IsArchived))
            .OrderBy(item => item.Information.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public async Task<IReadOnlyList<string>> GetArchivedProjectIdsAsync(IReadOnlyList<string> projectIds, CancellationToken cancellationToken)
    {
        if (projectIds.Count == 0)
        {
            return [];
        }

        var actor = actorAccessor.Current;
        return await dbContext.MemoryItems.AsNoTracking()
            .Where(x => projectIds.Contains(x.ProjectId) && x.ExternalKey == ExternalKey && x.Status == MemoryStatus.Archived)
            .Where(x => !actor.HasUser || (x.TenantId == actor.TenantId && (actor.IsServiceActor || x.OwnerUserId == actor.UserId)))
            .Select(x => x.ProjectId)
            .ToArrayAsync(cancellationToken);
    }

    private static ProjectInformationResult Map(MemoryItem item)
    {
        var lifecycle = ReadLifecycle(item.MetadataJson);
        return new(
            item.Id,
            item.ProjectId,
            string.IsNullOrWhiteSpace(item.Title) ? item.ProjectId : item.Title,
            item.Content,
            item.UpdatedAt,
            lifecycle.IsHidden,
            lifecycle.ArchivedAt,
            lifecycle.ArchivedAt?.AddDays(7));
    }

    private static ProjectLifecycleMetadata ReadLifecycle(string? metadataJson)
    {
        try
        {
            return string.IsNullOrWhiteSpace(metadataJson)
                ? new ProjectLifecycleMetadata()
                : JsonSerializer.Deserialize<ProjectLifecycleMetadata>(metadataJson, JsonOptions) ?? new ProjectLifecycleMetadata();
        }
        catch (JsonException)
        {
            return new ProjectLifecycleMetadata();
        }
    }

    private sealed record ProjectLifecycleMetadata(bool IsHidden = false, DateTimeOffset? ArchivedAt = null);
}

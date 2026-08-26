using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class ProjectWorkItemService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock) : IProjectWorkItemService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<ProjectWorkItemResult> CreateAsync(ProjectWorkItemCreateRequest request, CancellationToken cancellationToken)
    {
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ValidateText(request.Title, 200, "Work item title");
        ValidateOptionalText(request.Description, 12000, "Work item description");
        var tags = NormalizeTags(request.Tags);
        var checklist = NormalizeChecklist(request.ChecklistItems);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        if (ProjectContext.IsShared(projectId) || ProjectContext.IsUser(projectId))
        {
            throw new InvalidOperationException("Project work items require a regular ProjectId.");
        }

        var now = clock.UtcNow;
        var entity = new ProjectWorkItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            Title = request.Title.Trim(),
            Description = request.Description?.Trim() ?? string.Empty,
            Tags = tags,
            Priority = Math.Clamp(request.Priority, 0, 100),
            DueAt = request.DueAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        entity.ChecklistItems = checklist.Select((content, index) => new ProjectWorkItemChecklistItem
        {
            WorkItemId = entity.Id,
            Content = content,
            SortOrder = index,
            CreatedAt = now,
            UpdatedAt = now
        }).ToArray();
        await dbContext.ProjectWorkItems.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<ProjectWorkItemResult> UpdateAsync(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var entity = await ApplyActorScope(dbContext.ProjectWorkItems.Include(x => x.ChecklistItems), actor).SingleOrDefaultAsync(x => x.Id == request.Id, cancellationToken)
            ?? throw new InvalidOperationException($"Project work item '{request.Id}' was not found.");
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: true);
        EnsureNotArchived(entity);
        if (request.Title is not null)
        {
            ValidateText(request.Title, 200, "Work item title");
            entity.Title = request.Title.Trim();
        }
        if (request.Description is not null)
        {
            ValidateOptionalText(request.Description, 12000, "Work item description");
            entity.Description = request.Description.Trim();
        }
        if (request.Tags is not null) entity.Tags = NormalizeTags(request.Tags);
        if (request.Priority.HasValue) entity.Priority = Math.Clamp(request.Priority.Value, 0, 100);
        if (request.DueAt.HasValue) entity.DueAt = request.DueAt;
        if (request.Status.HasValue)
        {
            if (request.Status == ProjectWorkItemStatus.Completed && entity.ChecklistItems.Any(x => !x.IsCompleted))
            {
                throw new InvalidOperationException("Complete every checklist item before completing this work item.");
            }
            entity.Status = request.Status.Value;
            entity.CompletedAt = request.Status is ProjectWorkItemStatus.Completed or ProjectWorkItemStatus.Cancelled ? clock.UtcNow : null;
        }
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<IReadOnlyList<ProjectWorkItemResult>> ListAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
        var query = ApplyActorScope(dbContext.ProjectWorkItems.AsNoTracking().Include(x => x.ChecklistItems), actor).Where(x => x.ProjectId == projectId);
        if (!request.IncludeArchived) query = query.Where(x => x.ArchivedAt == null);
        if (request.Status.HasValue) query = query.Where(x => x.Status == request.Status.Value);
        var items = await query.OrderBy(x => x.Status == ProjectWorkItemStatus.Completed || x.Status == ProjectWorkItemStatus.Cancelled)
            .ThenByDescending(x => x.Priority).ThenBy(x => x.DueAt).ThenByDescending(x => x.UpdatedAt)
            .Skip(Math.Max(0, request.Offset))
            .Take(Math.Clamp(request.Limit, 1, 200)).ToListAsync(cancellationToken);
        return items.Select(Map).ToArray();
    }

    public async Task<ProjectWorkItemResult> SetArchivedAsync(Guid workItemId, bool archived, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var entity = await ApplyActorScope(dbContext.ProjectWorkItems.Include(x => x.ChecklistItems), actor).SingleOrDefaultAsync(x => x.Id == workItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Project work item '{workItemId}' was not found.");
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: true);

        var shouldChange = archived ? entity.ArchivedAt is null : entity.ArchivedAt is not null;
        if (shouldChange)
        {
            var now = clock.UtcNow;
            entity.ArchivedAt = archived ? now : null;
            entity.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return Map(entity);
    }

    public async Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var entity = await ApplyActorScope(dbContext.ProjectWorkItems.Include(x => x.ChecklistItems), actor).SingleOrDefaultAsync(x => x.Id == workItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Project work item '{workItemId}' was not found.");
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: true);
        EnsureNotArchived(entity);
        var item = entity.ChecklistItems.SingleOrDefault(x => x.Id == checklistItemId)
            ?? throw new InvalidOperationException($"Checklist item '{checklistItemId}' was not found.");
        item.IsCompleted = isCompleted;
        item.UpdatedAt = clock.UtcNow;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<ProjectWorkItemResult> SetGovernanceExclusionAsync(
        ProjectWorkItemGovernanceExclusionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.GovernanceTrackerManage);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("Only a tenant owner or administrator may change governance tracker exclusions.");
        }

        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        var entity = await ApplyActorScope(dbContext.ProjectWorkItems.Include(x => x.ChecklistItems), actor)
            .SingleOrDefaultAsync(x => x.Id == request.WorkItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Project work item '{request.WorkItemId}' was not found.");
        if (!string.Equals(entity.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("The work item does not belong to the requested ProjectId.");
        }
        EnsureNotArchived(entity);

        var governanceRunId = NormalizeGovernanceRunId(request.GovernanceRunId);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new InvalidOperationException("A governance tracker exclusion reason is required.");
        }

        var relatedSnapshots = await dbContext.KnowledgeGovernanceSnapshots
            .AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.GovernanceRunId == governanceRunId)
            .Select(x => x.ProjectIdsJson)
            .ToListAsync(cancellationToken);
        if (!relatedSnapshots.Any(json => ReadProjectIds(json).Contains(projectId, StringComparer.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("GovernanceRunId does not identify an authorized review snapshot containing this ProjectId.");
        }

        var exclusions = ReadExclusions(entity.GovernanceExclusionsJson).ToList();
        var index = exclusions.FindIndex(x => string.Equals(x.GovernanceRunId, governanceRunId, StringComparison.Ordinal));
        var now = clock.UtcNow;
        var reason = request.Reason.Trim();
        ProjectWorkItemGovernanceExclusionResult updated;
        if (request.Excluded)
        {
            if (index >= 0 && exclusions[index].IsActive && string.Equals(exclusions[index].Reason, reason, StringComparison.Ordinal))
            {
                return Map(entity);
            }
            updated = new ProjectWorkItemGovernanceExclusionResult(governanceRunId, reason, actor.Username, now);
        }
        else
        {
            if (index < 0 || !exclusions[index].IsActive)
            {
                return Map(entity);
            }
            updated = exclusions[index] with { Reason = reason, Actor = actor.Username, UpdatedAt = now, RevokedAt = now };
        }

        if (index >= 0) exclusions[index] = updated;
        else exclusions.Add(updated);
        entity.GovernanceExclusionsJson = JsonSerializer.Serialize(exclusions, JsonOptions);
        entity.UpdatedAt = now;
        await dbContext.SecurityAuditEvents.AddAsync(new SecurityAuditEvent
        {
            TenantId = actor.TenantId,
            ActorUserId = actor.UserId,
            EventType = SecurityAuditEventType.ProjectWorkItemGovernanceExclusionUpdated,
            Outcome = request.Excluded ? "Excluded" : "Revoked",
            DetailsJson = JsonSerializer.Serialize(new
            {
                workItemId = entity.Id,
                projectId,
                governanceRunId,
                reason
            }, JsonOptions),
            CreatedAt = now
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    private static IQueryable<ProjectWorkItem> ApplyActorScope(IQueryable<ProjectWorkItem> query, ContextHubRequestActor actor)
        => !actor.HasUser ? query : actor.IsServiceActor
            ? query.Where(x => x.TenantId == actor.TenantId)
            : query.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
    private static ProjectWorkItemResult Map(ProjectWorkItem x) => new(x.Id, x.ProjectId, x.Title, x.Description, x.Tags, x.ChecklistItems.OrderBy(item => item.SortOrder).Select(item => new ProjectWorkItemChecklistItemResult(item.Id, item.Content, item.IsCompleted, item.SortOrder)).ToArray(), x.Status, x.Priority, x.DueAt, x.CreatedAt, x.UpdatedAt, x.CompletedAt, x.ArchivedAt)
    {
        GovernanceExclusions = ReadExclusions(x.GovernanceExclusionsJson)
    };
    private static void EnsureNotArchived(ProjectWorkItem entity)
    {
        if (entity.ArchivedAt.HasValue) throw new InvalidOperationException("Project work item is archived. Restore it before making changes.");
    }
    private static string[] NormalizeTags(IReadOnlyList<string>? tags)
        => (tags ?? []).Select(tag => tag.Trim()).Where(tag => tag.Length is > 0 and <= 50).Distinct(StringComparer.OrdinalIgnoreCase).Take(12).ToArray();
    private static string[] NormalizeChecklist(IReadOnlyList<string>? items)
    {
        var result = (items ?? []).Select(item => item.Trim()).Where(item => item.Length > 0).Take(100).ToArray();
        if (result.Any(item => item.Length > 500)) throw new InvalidOperationException("Checklist item must not exceed 500 characters.");
        return result;
    }
    private static void ValidateText(string value, int max, string field)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > max) throw new InvalidOperationException($"{field} is required and must not exceed {max} characters.");
    }
    private static void ValidateOptionalText(string? value, int max, string field)
    {
        if (value is not null && value.Trim().Length > max) throw new InvalidOperationException($"{field} must not exceed {max} characters.");
    }

    private static IReadOnlyList<ProjectWorkItemGovernanceExclusionResult> ReadExclusions(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<ProjectWorkItemGovernanceExclusionResult[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException exception)
        {
            throw new InvalidOperationException("Persisted work item governance exclusions are invalid.", exception);
        }
    }

    private static IReadOnlyList<string> ReadProjectIds(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string NormalizeGovernanceRunId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 128)
        {
            throw new InvalidOperationException("GovernanceRunId is required and must not exceed 128 characters.");
        }
        return value.Trim();
    }
}

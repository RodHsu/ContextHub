using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class ProjectWorkItemService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock) : IProjectWorkItemService
{
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
        if (request.Status.HasValue) query = query.Where(x => x.Status == request.Status.Value);
        var items = await query.OrderBy(x => x.Status == ProjectWorkItemStatus.Completed || x.Status == ProjectWorkItemStatus.Cancelled)
            .ThenByDescending(x => x.Priority).ThenBy(x => x.DueAt).ThenByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(request.Limit, 1, 200)).ToListAsync(cancellationToken);
        return items.Select(Map).ToArray();
    }

    public async Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var entity = await ApplyActorScope(dbContext.ProjectWorkItems.Include(x => x.ChecklistItems), actor).SingleOrDefaultAsync(x => x.Id == workItemId, cancellationToken)
            ?? throw new InvalidOperationException($"Project work item '{workItemId}' was not found.");
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: true);
        var item = entity.ChecklistItems.SingleOrDefault(x => x.Id == checklistItemId)
            ?? throw new InvalidOperationException($"Checklist item '{checklistItemId}' was not found.");
        item.IsCompleted = isCompleted;
        item.UpdatedAt = clock.UtcNow;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    private static IQueryable<ProjectWorkItem> ApplyActorScope(IQueryable<ProjectWorkItem> query, ContextHubRequestActor actor)
        => !actor.HasUser ? query : actor.IsServiceActor
            ? query.Where(x => x.TenantId == actor.TenantId)
            : query.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
    private static ProjectWorkItemResult Map(ProjectWorkItem x) => new(x.Id, x.ProjectId, x.Title, x.Description, x.Tags, x.ChecklistItems.OrderBy(item => item.SortOrder).Select(item => new ProjectWorkItemChecklistItemResult(item.Id, item.Content, item.IsCompleted, item.SortOrder)).ToArray(), x.Status, x.Priority, x.DueAt, x.CreatedAt, x.UpdatedAt, x.CompletedAt);
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
}

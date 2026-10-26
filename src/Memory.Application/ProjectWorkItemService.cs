using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class ProjectWorkItemService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    Func<IWorkItemEditingService>? editing = null) : IProjectWorkItemService
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
            DefinitionState = request.DefinitionState,
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

    public Task<ProjectWorkItemResult> UpdateAsync(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken)
    {
        var hasFlat = request.Title is not null || request.Description is not null || request.Tags is not null ||
            request.Status.HasValue || request.Priority.HasValue || request.DueAt.HasValue || request.DefinitionState.HasValue;
        if (request.Edit is not null && hasFlat) throw new ArgumentException("Use either a typed edit patch or legacy update fields, not both.");
        var patch = request.Edit ?? new WorkItemEditPatch(request.Title, request.Description, request.Tags, request.Priority,
            request.Status, request.DefinitionState, request.DueAt.HasValue ? new(EditDueAtMode.Set, request.DueAt) : new());
        return ApplyVersionedAsync(request.Id, request.ExpectedRevision, request.RequestId, request.Operation, patch, cancellationToken);
    }

    public async Task<ProjectWorkItemResult?> GetAsync(Guid workItemId, bool includeArchived, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var query = ApplyActorScope(dbContext.ProjectWorkItems.AsNoTracking().Include(x => x.ChecklistItems), actor);
        if (!includeArchived) query = query.Where(x => x.ArchivedAt == null);
        var entity = await query.SingleOrDefaultAsync(x => x.Id == workItemId, cancellationToken);
        if (entity is null) return null;
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: false);
        return Map(entity);
    }

    public async Task<IReadOnlyList<ProjectWorkItemResult>> ListAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
        var query = ApplyActorScope(dbContext.ProjectWorkItems.AsNoTracking().Include(x => x.ChecklistItems), actor).Where(x => Memory.Application.ProjectContext.Matches(x.ProjectId, projectId));
        if (!request.IncludeArchived) query = query.Where(x => x.ArchivedAt == null);
        if (request.Status.HasValue) query = query.Where(x => x.Status == request.Status.Value);
        if (request.DefinitionState.HasValue) query = query.Where(x => x.DefinitionState == request.DefinitionState.Value);
        var items = await query.OrderBy(x => x.Status == ProjectWorkItemStatus.Completed || x.Status == ProjectWorkItemStatus.Cancelled)
            .ThenByDescending(x => x.Priority).ThenBy(x => x.DueAt).ThenByDescending(x => x.UpdatedAt).ThenBy(x => x.Id)
            .Skip(Math.Max(0, request.Offset))
            .Take(Math.Clamp(request.Limit, 1, 200)).ToListAsync(cancellationToken);
        return items.Select(Map).ToArray();
    }

    public Task<ProjectWorkItemResult> SetArchivedAsync(Guid workItemId, bool archived, CancellationToken cancellationToken)
        => SetArchivedAsync(new ProjectWorkItemArchiveRequest(workItemId, archived), cancellationToken);

    public Task<ProjectWorkItemResult> SetArchivedAsync(ProjectWorkItemArchiveRequest request, CancellationToken cancellationToken)
        => ApplyVersionedAsync(request.WorkItemId, request.ExpectedRevision, request.RequestId,
            request.Archived ? WorkItemEditOperation.Archive : WorkItemEditOperation.Restore, new(), cancellationToken);

    public Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken)
        => SetChecklistItemCompletionAsync(new ProjectWorkItemChecklistUpdateRequest(workItemId, checklistItemId, isCompleted), cancellationToken);

    public Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(ProjectWorkItemChecklistUpdateRequest request, CancellationToken cancellationToken)
        => ApplyVersionedAsync(request.WorkItemId, request.ExpectedRevision, request.RequestId, WorkItemEditOperation.ChecklistComplete,
            new(ChecklistItemId: request.ChecklistItemId, IsCompleted: request.IsCompleted), cancellationToken);

    public Task<ProjectWorkItemResult> SetGovernanceExclusionAsync(ProjectWorkItemGovernanceExclusionRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.GovernanceTrackerManage);
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("Governance tracker management is unavailable.");
        if (string.IsNullOrWhiteSpace(request.ProjectId)) throw new ArgumentException("An explicit ProjectId is required.");
        return ApplyVersionedAsync(request.WorkItemId, request.ExpectedRevision, request.RequestId,
            WorkItemEditOperation.GovernanceExclusion, new(GovernanceExclusion: new(request.GovernanceRunId, request.Reason, request.Excluded)),
            cancellationToken, request.ProjectId);
    }

    private async Task<ProjectWorkItemResult> ApplyVersionedAsync(Guid workItemId, long? expectedRevision, string? requestId,
        WorkItemEditOperation operation, WorkItemEditPatch patch, CancellationToken cancellationToken, string? expectedProject = null)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        // Resolve only authorized scope, never an original-version precondition on behalf of a caller.
        var project = await ApplyActorScope(dbContext.ProjectWorkItems.AsNoTracking(), actor).Where(row => row.Id == workItemId)
            .Select(row => row.ProjectId).SingleOrDefaultAsync(cancellationToken) ?? throw new KeyNotFoundException("Work item is unavailable.");
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: true);
        if (expectedProject is not null && !ProjectContext.Matches(project, ProjectContext.Normalize(expectedProject)))
            throw new UnauthorizedAccessException("Work item is unavailable.");
        var command = WorkItemEditContract.Normalize(new(project, workItemId, expectedRevision, requestId ?? string.Empty, operation, patch));
        var service = editing?.Invoke() ?? throw new NotSupportedException("Versioned work item editing is not configured.");
        var receipt = await service.ApplyAsync(command, cancellationToken);
        return receipt.Result with { EditReceipt = WorkItemEditReceiptMetadata.From(receipt) };
    }

    private static IQueryable<ProjectWorkItem> ApplyActorScope(IQueryable<ProjectWorkItem> query, ContextHubRequestActor actor)
        => !actor.HasUser ? query : actor.IsServiceActor
            ? query.Where(x => x.TenantId == actor.TenantId)
            : query.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
    private static ProjectWorkItemResult Map(ProjectWorkItem x) => new(x.Id, x.ProjectId, x.Title, x.Description, x.Tags, x.ChecklistItems.OrderBy(item => item.SortOrder).Select(item => new ProjectWorkItemChecklistItemResult(item.Id, item.Content, item.IsCompleted, item.SortOrder)).ToArray(), x.Status, x.Priority, x.DueAt, x.CreatedAt, x.UpdatedAt, x.CompletedAt, x.ArchivedAt)
    {
        Revision = x.Revision,
        DefinitionState = x.DefinitionState,
        GovernanceExclusions = ReadExclusions(x.GovernanceExclusionsJson)
    };

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

}

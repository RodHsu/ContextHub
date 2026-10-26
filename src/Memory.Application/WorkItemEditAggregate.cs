using Memory.Domain;

namespace Memory.Application;

/// <summary>
/// Applies an already-authorized edit to one loaded aggregate. This helper does not
/// acquire locks, authorize actors, persist history or commit transactions. The edit
/// service must perform those operations around this call. Validation and overflow
/// checks finish before any caller-owned entity is changed.
/// </summary>
public static class WorkItemEditAggregate
{
    public static bool Apply(ProjectWorkItem aggregate, WorkItemEditCommand command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(aggregate);
        var normalized = WorkItemEditContract.Normalize(command);
        if (aggregate.Id != normalized.WorkItemId || !ProjectContext.Matches(aggregate.ProjectId, normalized.ProjectId))
            throw new InvalidOperationException("The edit does not identify this work item.");
        WorkItemEditContract.RequireRevision(normalized.ExpectedRevision, aggregate.Revision);
        if (aggregate.ArchivedAt.HasValue && normalized.Operation is not (WorkItemEditOperation.Archive or WorkItemEditOperation.Restore))
            throw new InvalidOperationException("Project work item is archived. Restore it before making changes.");

        ValidateStoredChecklist(aggregate);
        RequireExplicitReopen(aggregate, normalized);
        var candidate = Copy(aggregate);
        var timestamp = now.ToUniversalTime();
        ApplyCandidate(candidate, normalized, timestamp);
        // Lifecycle/checklist edits may not create or retain an invalid Completed
        // aggregate. Metadata-only and archive/restore edits retain historical business state.
        var validatesCompletion = normalized.Operation is WorkItemEditOperation.ChecklistComplete or WorkItemEditOperation.ChecklistAdd or
            WorkItemEditOperation.ChecklistText or WorkItemEditOperation.ChecklistRemove or WorkItemEditOperation.ChecklistReorder ||
            (normalized.Operation == WorkItemEditOperation.Update && normalized.Patch.Status.HasValue);
        if (validatesCompletion &&
            candidate.Status == ProjectWorkItemStatus.Completed && candidate.ChecklistItems.Any(row => !row.IsCompleted))
            throw new InvalidOperationException("Complete every checklist item before completing this work item.");
        if (!Changed(aggregate, candidate)) return false;

        var revision = checked(aggregate.Revision + 1);
        Commit(aggregate, candidate, timestamp);
        aggregate.Revision = revision;
        aggregate.UpdatedAt = timestamp;
        return true;
    }

    private static void RequireExplicitReopen(ProjectWorkItem aggregate, WorkItemEditCommand command)
    {
        if (aggregate.Status != ProjectWorkItemStatus.Completed) return;
        var incompleteAddition = command.Operation == WorkItemEditOperation.ChecklistAdd &&
            !aggregate.ChecklistItems.Any(row => row.Id == command.Patch.ChecklistItemId);
        var uncheck = command.Operation == WorkItemEditOperation.ChecklistComplete && command.Patch.IsCompleted == false &&
            aggregate.ChecklistItems.Any(row => row.Id == command.Patch.ChecklistItemId && row.IsCompleted);
        var implicitReopen = command.Operation == WorkItemEditOperation.Update &&
            command.Patch.Status.HasValue && command.Patch.Status != ProjectWorkItemStatus.Completed;
        if (incompleteAddition || uncheck || implicitReopen)
            throw new SafeEditConflictException(SafeEditConflictCodes.RequiresReopen, aggregate.Revision);
    }

    private static void ApplyCandidate(ProjectWorkItem item, WorkItemEditCommand command, DateTimeOffset now)
    {
        var patch = command.Patch;
        switch (command.Operation)
        {
            case WorkItemEditOperation.Update:
                if (patch.Title is not null) item.Title = patch.Title;
                if (patch.Description is not null) item.Description = patch.Description;
                if (patch.Tags is not null) item.Tags = patch.Tags.ToArray();
                if (patch.Priority.HasValue) item.Priority = patch.Priority.Value;
                if (patch.Status.HasValue && patch.Status != item.Status)
                {
                    item.Status = patch.Status.Value;
                    item.CompletedAt = item.Status is ProjectWorkItemStatus.Completed or ProjectWorkItemStatus.Cancelled ? now : null;
                }
                if (patch.DefinitionState.HasValue && patch.DefinitionState != item.DefinitionState)
                {
                    RequireDefinitionTransition(item.DefinitionState, patch.DefinitionState.Value);
                    item.DefinitionState = patch.DefinitionState.Value;
                }
                item.DueAt = patch.DueAt!.Mode switch
                {
                    EditDueAtMode.Keep => item.DueAt,
                    EditDueAtMode.Clear => null,
                    EditDueAtMode.Set => patch.DueAt.Value,
                    _ => throw new ArgumentException("Invalid due date operation.")
                };
                break;
            case WorkItemEditOperation.ChecklistComplete:
                Find(item, patch.ChecklistItemId!.Value).IsCompleted = patch.IsCompleted!.Value;
                break;
            case WorkItemEditOperation.ChecklistAdd:
                if (item.ChecklistItems.Count >= 100) throw new InvalidOperationException("A work item may have at most 100 checklist items.");
                if (item.ChecklistItems.Any(row => row.Id == patch.ChecklistItemId))
                    throw new InvalidOperationException("The checklist identity already exists.");
                item.ChecklistItems.Add(new ProjectWorkItemChecklistItem
                {
                    Id = patch.ChecklistItemId!.Value,
                    WorkItemId = item.Id,
                    Content = patch.ChecklistContent!,
                    SortOrder = item.ChecklistItems.Count,
                    CreatedAt = now,
                    UpdatedAt = now
                });
                Reindex(item.ChecklistItems);
                break;
            case WorkItemEditOperation.ChecklistText:
                Find(item, patch.ChecklistItemId!.Value).Content = patch.ChecklistContent!;
                break;
            case WorkItemEditOperation.ChecklistRemove:
                item.ChecklistItems.Remove(Find(item, patch.ChecklistItemId!.Value));
                Reindex(item.ChecklistItems);
                break;
            case WorkItemEditOperation.ChecklistReorder:
                var rows = item.ChecklistItems.ToDictionary(row => row.Id);
                var order = patch.ChecklistOrder!;
                if (order.Count != rows.Count || !rows.Keys.ToHashSet().SetEquals(order))
                    throw new InvalidOperationException("Checklist order must contain each existing identity exactly once.");
                item.ChecklistItems = order.Select(id => rows[id]).ToList();
                Reindex(item.ChecklistItems);
                break;
            case WorkItemEditOperation.Reopen:
                if (item.Status != ProjectWorkItemStatus.Completed)
                    throw new InvalidOperationException("Only a completed work item can be reopened.");
                item.Status = ProjectWorkItemStatus.InProgress;
                item.CompletedAt = null;
                break;
            case WorkItemEditOperation.Archive:
                item.ArchivedAt ??= now;
                break;
            case WorkItemEditOperation.Restore:
                item.ArchivedAt = null;
                break;
            default:
                throw new ArgumentException("Invalid work item operation.");
        }
    }

    private static void RequireDefinitionTransition(ProjectWorkItemDefinitionState current, ProjectWorkItemDefinitionState requested)
    {
        var allowed = current switch
        {
            ProjectWorkItemDefinitionState.Discussing => requested is ProjectWorkItemDefinitionState.Draft or ProjectWorkItemDefinitionState.ReadyForDevelopment or ProjectWorkItemDefinitionState.Superseded,
            ProjectWorkItemDefinitionState.Draft => requested is ProjectWorkItemDefinitionState.Discussing or ProjectWorkItemDefinitionState.ReadyForDevelopment or ProjectWorkItemDefinitionState.Superseded,
            ProjectWorkItemDefinitionState.ReadyForDevelopment => requested is ProjectWorkItemDefinitionState.Draft or ProjectWorkItemDefinitionState.Frozen or ProjectWorkItemDefinitionState.Superseded,
            ProjectWorkItemDefinitionState.Frozen => requested is ProjectWorkItemDefinitionState.ReadyForDevelopment or ProjectWorkItemDefinitionState.Superseded,
            ProjectWorkItemDefinitionState.Superseded => false,
            _ => false
        };
        if (!allowed) throw new InvalidOperationException("DefinitionState transition is not allowed.");
    }

    private static ProjectWorkItemChecklistItem Find(ProjectWorkItem item, Guid id)
        => item.ChecklistItems.SingleOrDefault(row => row.Id == id)
            ?? throw new InvalidOperationException("Checklist item was not found in this work item.");

    private static void ValidateStoredChecklist(ProjectWorkItem item)
    {
        if (item.ChecklistItems.Any(row => row.Id == Guid.Empty || row.WorkItemId != item.Id) ||
            item.ChecklistItems.Select(row => row.Id).Distinct().Count() != item.ChecklistItems.Count)
            throw new InvalidOperationException("Stored checklist identities are inconsistent.");
    }

    private static void Reindex(IEnumerable<ProjectWorkItemChecklistItem> rows)
    {
        var index = 0;
        foreach (var row in rows) row.SortOrder = index++;
    }

    private static ProjectWorkItem Copy(ProjectWorkItem item) => new()
    {
        Id = item.Id,
        ProjectId = item.ProjectId,
        Title = item.Title,
        Description = item.Description,
        Tags = item.Tags.ToArray(),
        Priority = item.Priority,
        Status = item.Status,
        DefinitionState = item.DefinitionState,
        DueAt = item.DueAt,
        CompletedAt = item.CompletedAt,
        ArchivedAt = item.ArchivedAt,
        ChecklistItems = item.ChecklistItems.OrderBy(row => row.SortOrder).ThenBy(row => row.Id).Select(row => new ProjectWorkItemChecklistItem
        {
            Id = row.Id,
            WorkItemId = row.WorkItemId,
            Content = row.Content,
            IsCompleted = row.IsCompleted,
            SortOrder = row.SortOrder,
            CreatedAt = row.CreatedAt,
            UpdatedAt = row.UpdatedAt
        }).ToList()
    };

    private static bool Changed(ProjectWorkItem before, ProjectWorkItem after)
    {
        if (before.Title != after.Title || before.Description != after.Description || !before.Tags.SequenceEqual(after.Tags) ||
            before.Priority != after.Priority || before.Status != after.Status || before.DefinitionState != after.DefinitionState ||
            before.DueAt != after.DueAt || before.CompletedAt != after.CompletedAt || before.ArchivedAt != after.ArchivedAt ||
            before.ChecklistItems.Count != after.ChecklistItems.Count) return true;
        var rows = before.ChecklistItems.ToDictionary(row => row.Id);
        return after.ChecklistItems.Any(row => !rows.TryGetValue(row.Id, out var old) ||
            row.Content != old.Content || row.IsCompleted != old.IsCompleted || row.SortOrder != old.SortOrder);
    }

    private static void Commit(ProjectWorkItem target, ProjectWorkItem candidate, DateTimeOffset now)
    {
        var next = candidate.ChecklistItems.ToDictionary(row => row.Id);
        var existingIds = target.ChecklistItems.Select(row => row.Id).ToHashSet();
        if (target.ChecklistItems.IsReadOnly && !existingIds.SetEquals(next.Keys))
            target.ChecklistItems = target.ChecklistItems.ToList();
        target.Title = candidate.Title;
        target.Description = candidate.Description;
        target.Tags = candidate.Tags;
        target.Priority = candidate.Priority;
        target.Status = candidate.Status;
        target.DefinitionState = candidate.DefinitionState;
        target.DueAt = candidate.DueAt;
        target.CompletedAt = candidate.CompletedAt;
        target.ArchivedAt = candidate.ArchivedAt;
        foreach (var old in target.ChecklistItems.Where(row => !next.ContainsKey(row.Id)).ToArray()) target.ChecklistItems.Remove(old);
        var existing = target.ChecklistItems.ToDictionary(row => row.Id);
        foreach (var row in candidate.ChecklistItems)
        {
            if (!existing.TryGetValue(row.Id, out var current))
            {
                row.WorkItem = target;
                target.ChecklistItems.Add(row);
                continue;
            }
            if (current.Content != row.Content || current.IsCompleted != row.IsCompleted || current.SortOrder != row.SortOrder)
            {
                current.Content = row.Content;
                current.IsCompleted = row.IsCompleted;
                current.SortOrder = row.SortOrder;
                current.UpdatedAt = now;
            }
        }
    }
}

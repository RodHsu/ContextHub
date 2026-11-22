using System.Security.Cryptography;
using System.Text.Json;
using Memory.Domain;

namespace Memory.Application;

public enum WorkItemEditOperation
{
    Update,
    ChecklistComplete,
    ChecklistAdd,
    ChecklistText,
    ChecklistRemove,
    ChecklistReorder,
    Reopen,
    Archive,
    Restore
}

public enum EditDueAtMode { Keep, Clear, Set }
public sealed record EditDueAt(EditDueAtMode Mode = EditDueAtMode.Keep, DateTimeOffset? Value = null);

public sealed record WorkItemEditPatch(
    string? Title = null,
    string? Description = null,
    IReadOnlyList<string>? Tags = null,
    int? Priority = null,
    ProjectWorkItemStatus? Status = null,
    ProjectWorkItemDefinitionState? DefinitionState = null,
    EditDueAt? DueAt = null,
    Guid? ChecklistItemId = null,
    bool? IsCompleted = null,
    string? ChecklistContent = null,
    IReadOnlyList<Guid>? ChecklistOrder = null);

public sealed record WorkItemEditCommand(
    string ProjectId,
    Guid WorkItemId,
    long? ExpectedRevision,
    string RequestId,
    WorkItemEditOperation Operation,
    WorkItemEditPatch Patch);

public static class SafeEditConflictCodes
{
    public const string MissingReadVersion = nameof(MissingReadVersion);
    public const string StaleReadVersion = nameof(StaleReadVersion);
    public const string RequestIdentityMismatch = nameof(RequestIdentityMismatch);
    public const string RequiresReopen = nameof(RequiresReopen);
}

/// <summary>Only expose CurrentRevision after the caller's resource scope has been authorized.</summary>
public sealed class SafeEditConflictException(string code, long? currentRevision = null)
    : InvalidOperationException(MessageFor(code))
{
    public string Code { get; } = code;
    public long? CurrentRevision { get; } = currentRevision;

    private static string MessageFor(string code) => code switch
    {
        SafeEditConflictCodes.MissingReadVersion => "The original read revision is required. Reload before saving.",
        SafeEditConflictCodes.StaleReadVersion => "The original read revision is stale. Reload and compare before saving.",
        SafeEditConflictCodes.RequestIdentityMismatch => "This request identity belongs to a different edit intent.",
        SafeEditConflictCodes.RequiresReopen => "Explicitly reopen this completed work item before changing its completion.",
        _ => "The edit conflicts with its required state. Reload before saving."
    };
}

/// <summary>
/// Pure validation of the actual edit command. It grants no authority and performs no persistence.
/// The store must authorize the target, reconcile the scoped request receipt, and compare the
/// original revision inside the same transaction before applying a normalized command.
/// </summary>
public static class WorkItemEditContract
{
    public const string IntentHashVersion = "WorkItemEditIntent/v1";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static WorkItemEditCommand Normalize(WorkItemEditCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.ProjectId)) throw Invalid("An explicit ProjectId is required.");
        var project = ProjectContext.Normalize(command.ProjectId);
        if (ProjectContext.IsShared(project) || ProjectContext.IsUser(project) || project == ProjectContext.AllProjectIdsSentinel)
            throw Invalid("Work item edits require one regular ProjectId.");
        if (command.WorkItemId == Guid.Empty) throw Invalid("A work item identity is required.");
        if (!command.ExpectedRevision.HasValue || command.ExpectedRevision.Value <= 0)
            throw new SafeEditConflictException(SafeEditConflictCodes.MissingReadVersion);
        var requestId = Text(command.RequestId, 200, "RequestId", required: true)!;
        if (!Enum.IsDefined(command.Operation)) throw Invalid("The edit operation is invalid.");
        if (command.Patch is null) throw Invalid("An edit patch is required.");
        var patch = command.Patch;
        if (patch.Status.HasValue && !Enum.IsDefined(patch.Status.Value)) throw Invalid("The work item status is invalid.");
        if (patch.DefinitionState.HasValue && !Enum.IsDefined(patch.DefinitionState.Value)) throw Invalid("The definition state is invalid.");

        var dueAt = NormalizeDueAt(patch.DueAt);
        ValidateShape(command.Operation, patch, dueAt);
        var title = Text(patch.Title, 200, "Title", required: patch.Title is not null);
        var description = Text(patch.Description, 12000, "Description", required: false);
        var content = Text(patch.ChecklistContent, 500, "Checklist content", required: patch.ChecklistContent is not null);
        var tags = NormalizeTags(patch.Tags);
        Guid[]? order = null;
        if (patch.ChecklistOrder is not null)
        {
            if (patch.ChecklistOrder.Count > 100) throw Invalid("A checklist order must not exceed 100 entries.");
            order = patch.ChecklistOrder.ToArray();
            if (order.Any(id => id == Guid.Empty) || order.Distinct().Count() != order.Length)
                throw Invalid("A checklist order must contain distinct, nonempty identities.");
        }
        var normalized = patch with
        {
            Title = title,
            Description = description,
            Tags = tags is null ? null : Array.AsReadOnly(tags),
            Priority = patch.Priority.HasValue ? Math.Clamp(patch.Priority.Value, 0, 100) : null,
            DueAt = dueAt,
            ChecklistContent = content,
            ChecklistOrder = order is null ? null : Array.AsReadOnly(order)
        };
        return command with { ProjectId = project, RequestId = requestId, Patch = normalized };
    }

    /// <summary>
    /// Canonical effective-intent hash. RequestId and the server-resolved actor belong to the
    /// store's separate tenant/actor/project/resource/request identity key, not this hash.
    /// Null versus explicit empty text/list and Keep/Clear/Set are retained when meaningful.
    /// </summary>
    public static string Hash(WorkItemEditCommand command)
    {
        var normalized = Normalize(command);
        var patch = normalized.Patch;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            version = IntentHashVersion,
            projectKey = ProjectContext.IdentityKey(normalized.ProjectId),
            normalized.WorkItemId,
            normalized.ExpectedRevision,
            operation = normalized.Operation.ToString(),
            patch = new
            {
                patch.Title,
                patch.Description,
                patch.Tags,
                patch.Priority,
                status = patch.Status?.ToString(),
                definitionState = patch.DefinitionState?.ToString(),
                dueAt = new { mode = patch.DueAt!.Mode.ToString(), patch.DueAt.Value },
                patch.ChecklistItemId,
                patch.IsCompleted,
                patch.ChecklistContent,
                patch.ChecklistOrder
            }
        }, Json);
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static void RequireRevision(long? expectedRevision, long actualRevision)
    {
        if (actualRevision <= 0) throw new ArgumentOutOfRangeException(nameof(actualRevision));
        if (!expectedRevision.HasValue || expectedRevision.Value <= 0)
            throw new SafeEditConflictException(SafeEditConflictCodes.MissingReadVersion);
        if (expectedRevision.Value != actualRevision)
            throw new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, actualRevision);
    }

    private static EditDueAt NormalizeDueAt(EditDueAt? dueAt)
    {
        if (dueAt is null) return new();
        if (!Enum.IsDefined(dueAt.Mode)) throw Invalid("The due date change mode is invalid.");
        if ((dueAt.Mode == EditDueAtMode.Set) != dueAt.Value.HasValue)
            throw Invalid("Set requires a due date; Keep and Clear must not include one.");
        return dueAt with { Value = dueAt.Value?.ToUniversalTime() };
    }

    private static void ValidateShape(WorkItemEditOperation operation, WorkItemEditPatch patch, EditDueAt dueAt)
    {
        var update = patch.Title is not null || patch.Description is not null || patch.Tags is not null ||
                     patch.Priority.HasValue || patch.Status.HasValue || patch.DefinitionState.HasValue || dueAt.Mode != EditDueAtMode.Keep;
        var checklist = patch.ChecklistItemId.HasValue || patch.IsCompleted.HasValue ||
                        patch.ChecklistContent is not null || patch.ChecklistOrder is not null;
        if (operation == WorkItemEditOperation.Update)
        {
            if (checklist) throw Invalid("Checklist edits require a specific checklist operation.");
            return;
        }
        if (update) throw Invalid("This operation does not accept ordinary update fields.");
        if (operation is WorkItemEditOperation.Reopen or WorkItemEditOperation.Archive or WorkItemEditOperation.Restore)
        {
            if (checklist) throw Invalid("Lifecycle operations require an empty patch.");
            return;
        }
        if (operation == WorkItemEditOperation.ChecklistReorder)
        {
            if (patch.ChecklistOrder is null || patch.ChecklistItemId.HasValue || patch.IsCompleted.HasValue || patch.ChecklistContent is not null)
                throw Invalid("Checklist reorder requires only the complete ordered identity list.");
            return;
        }
        if (!patch.ChecklistItemId.HasValue || patch.ChecklistItemId.Value == Guid.Empty || patch.ChecklistOrder is not null)
            throw Invalid("A distinct checklist identity is required.");
        var valid = operation switch
        {
            WorkItemEditOperation.ChecklistComplete => patch.IsCompleted.HasValue && patch.ChecklistContent is null,
            WorkItemEditOperation.ChecklistAdd or WorkItemEditOperation.ChecklistText => !patch.IsCompleted.HasValue && patch.ChecklistContent is not null,
            WorkItemEditOperation.ChecklistRemove => !patch.IsCompleted.HasValue && patch.ChecklistContent is null,
            _ => false
        };
        if (!valid) throw Invalid("The checklist patch does not match its operation.");
    }

    private static string? Text(string? value, int max, string field, bool required)
    {
        if (value is null)
        {
            if (required) throw Invalid($"{field} is required.");
            return null;
        }
        var trimmed = value.Trim();
        if ((required && trimmed.Length == 0) || trimmed.Length > max) throw Invalid($"{field} exceeds its allowed bounds.");
        return trimmed;
    }

    private static string[]? NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null) return null;
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var tag in tags)
        {
            if (tag is null) throw Invalid("A tag must be text.");
            var trimmed = Text(tag, 50, "Tag", required: false)!;
            if (trimmed.Length == 0 || !seen.Add(trimmed)) continue;
            result.Add(trimmed);
            if (result.Count > 12) throw Invalid("A work item must not exceed 12 distinct tags.");
        }
        return result.ToArray();
    }

    private static ArgumentException Invalid(string message) => new(message);
}

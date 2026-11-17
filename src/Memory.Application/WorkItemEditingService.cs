using System.Text.Json;
using Memory.Domain;

namespace Memory.Application;

/// <summary>Server-derived scope only. Clients do not supply actor or tenant identity.</summary>
public sealed record WorkItemEditScope(Guid TenantId, Guid ActorUserId, bool IsServiceActor, string ProjectId,
    bool CanManageGovernanceTrackers = false, string ActorName = "");

/// <summary>A committed edit result, not a signed proof or a current-state snapshot.</summary>
public sealed record WorkItemEditReceipt(Guid ReceiptId, Guid? HistoryId, string ProjectId, Guid WorkItemId,
    string RequestId, string RequestHash, string HashVersion, WorkItemEditOperation Operation,
    long PreviousRevision, long Revision, bool Changed, DateTimeOffset AppliedAt,
    ProjectWorkItemResult Result, bool Replayed = false);

public sealed record WorkItemEditReceiptMetadata(Guid ReceiptId, Guid? HistoryId, string RequestId, string RequestHash,
    string HashVersion, WorkItemEditOperation Operation, long PreviousRevision, long Revision, bool Changed, DateTimeOffset AppliedAt, bool Replayed)
{
    public static WorkItemEditReceiptMetadata From(WorkItemEditReceipt value) => new(value.ReceiptId, value.HistoryId,
        value.RequestId, value.RequestHash, value.HashVersion, value.Operation, value.PreviousRevision, value.Revision, value.Changed, value.AppliedAt, value.Replayed);
}

public sealed record WorkItemEditRecoveryRequest(string ProjectId, Guid WorkItemId, string RequestId,
    string ExpectedRequestHash, string HashVersion = WorkItemEditContract.IntentHashVersion);

public static class WorkItemEditRecovery
{
    public static WorkItemEditRecoveryRequest Normalize(WorkItemEditRecoveryRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.ProjectId) || request.WorkItemId == Guid.Empty ||
            string.IsNullOrWhiteSpace(request.RequestId) || request.RequestId.Trim().Length > 200 ||
            request.HashVersion != WorkItemEditContract.IntentHashVersion || request.ExpectedRequestHash is null ||
            request.ExpectedRequestHash.Length != 64 || request.ExpectedRequestHash.Any(c => !char.IsAsciiHexDigit(c)))
            throw new ArgumentException("An exact request identity and supported original intent fingerprint are required.");
        return request with { ProjectId = ProjectContext.Normalize(request.ProjectId), RequestId = request.RequestId.Trim(), ExpectedRequestHash = request.ExpectedRequestHash.ToLowerInvariant() };
    }
}

public interface IWorkItemEditStore
{
    Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditScope scope, WorkItemEditCommand command, CancellationToken ct);
    Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditScope scope, WorkItemEditRecoveryRequest request, CancellationToken ct);
}

public interface IWorkItemEditingService
{
    Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditCommand command, CancellationToken ct);
    Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditRecoveryRequest request, CancellationToken ct);
}

public sealed class WorkItemEditingService(IWorkItemEditStore store, IRequestActorAccessor actors) : IWorkItemEditingService
{
    public Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditCommand command, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(command);
        var project = RequireProject(command.ProjectId);
        var scope = Scope(project, SecurityScopes.MemoryWrite, write: true);
        if (command.Operation == WorkItemEditOperation.GovernanceExclusion && !scope.CanManageGovernanceTrackers)
            throw new UnauthorizedAccessException("Governance tracker management is unavailable.");
        var normalized = WorkItemEditContract.Normalize(command);
        return store.ApplyAsync(scope, normalized, ct);
    }

    public Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditRecoveryRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var project = RequireProject(request.ProjectId);
        var scope = Scope(project, SecurityScopes.MemoryRead, write: false);
        return store.ReadReceiptAsync(scope, WorkItemEditRecovery.Normalize(request), ct);
    }

    private WorkItemEditScope Scope(string project, string requiredScope, bool write)
    {
        var actor = actors.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, requiredScope);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write);
        return new(actor.TenantId!.Value, actor.UserId!.Value, actor.IsServiceActor, project,
            actor.IsAdmin && actor.HasScope(SecurityScopes.GovernanceTrackerManage), actor.Username);
    }

    private static string RequireProject(string project)
    {
        if (string.IsNullOrWhiteSpace(project)) throw new ArgumentException("An explicit ProjectId is required.");
        var value = ProjectContext.Normalize(project);
        if (ProjectContext.IsShared(value) || ProjectContext.IsUser(value)) throw new ArgumentException("A regular ProjectId is required.");
        return value;
    }
}

public static class WorkItemEditProjection
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static ProjectWorkItemResult ToResult(ProjectWorkItem entity) => new(entity.Id, entity.ProjectId,
        entity.Title, entity.Description, entity.Tags.ToArray(), entity.ChecklistItems.OrderBy(row => row.SortOrder).ThenBy(row => row.Id)
            .Select(row => new ProjectWorkItemChecklistItemResult(row.Id, row.Content, row.IsCompleted, row.SortOrder)).ToArray(),
        entity.Status, entity.Priority, entity.DueAt, entity.CreatedAt, entity.UpdatedAt, entity.CompletedAt, entity.ArchivedAt)
    {
        Revision = entity.Revision,
        DefinitionState = entity.DefinitionState,
        GovernanceExclusions = JsonSerializer.Deserialize<ProjectWorkItemGovernanceExclusionResult[]>(entity.GovernanceExclusionsJson, Json) ?? []
    };
}

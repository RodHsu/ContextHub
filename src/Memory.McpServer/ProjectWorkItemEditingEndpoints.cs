using Memory.Application;
using Memory.Domain;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using System.Text.Json.Serialization;

namespace Memory.McpServer;

public static class ProjectWorkItemEditingEndpoints
{
    public static RouteGroupBuilder MapWorkItemEditingEndpoints(this RouteGroupBuilder workItems, bool requireAuthentication)
    {
        workItems.MapPost("/{id:guid}/edits", (Guid id, WorkItemEditCommand command, IWorkItemEditingService service, HttpContext context)
            => RunAsync(async () => id != command.WorkItemId ? Results.BadRequest(new { code = "TargetMismatch" })
                : Results.Ok(await service.ApplyAsync(command, context.RequestAborted)), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
        workItems.MapGet("/{id:guid}/edit-receipts", (Guid id, string projectId, string requestId,
            string expectedRequestHash, string? hashVersion, IWorkItemEditingService service, HttpContext context)
            => RunAsync(async () => await service.ReadReceiptAsync(new(projectId, id, requestId, expectedRequestHash,
                hashVersion ?? WorkItemEditContract.IntentHashVersion), context.RequestAborted) is { } receipt
                ? Results.Ok(receipt) : Results.NotFound(), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);

        workItems.MapPut("/{id:guid}", (Guid id, ProjectWorkItemUpdateBody body, IProjectWorkItemService service, HttpContext context)
            => RunAsync(async () => Results.Ok(await service.UpdateAsync(body.ToRequest(id), context.RequestAborted)), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
        workItems.MapPost("/{id:guid}/archive", (Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] WorkItemEditPreconditionBody? body,
            IProjectWorkItemService service, HttpContext context)
            => RunAsync(async () => Results.Ok(await service.SetArchivedAsync(new ProjectWorkItemArchiveRequest(id, true,
                body?.ExpectedRevision, body?.RequestId), context.RequestAborted)), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
        workItems.MapPost("/{id:guid}/restore", (Guid id, [FromBody(EmptyBodyBehavior = EmptyBodyBehavior.Allow)] WorkItemEditPreconditionBody? body,
            IProjectWorkItemService service, HttpContext context)
            => RunAsync(async () => Results.Ok(await service.SetArchivedAsync(new ProjectWorkItemArchiveRequest(id, false,
                body?.ExpectedRevision, body?.RequestId), context.RequestAborted)), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
        workItems.MapPut("/{workItemId:guid}/checklist/{checklistItemId:guid}", (Guid workItemId, Guid checklistItemId,
            ProjectWorkItemChecklistCompletionBody body, IProjectWorkItemService service, HttpContext context)
            => RunAsync(async () => Results.Ok(await service.SetChecklistItemCompletionAsync(new ProjectWorkItemChecklistUpdateRequest(
                workItemId, checklistItemId, body.IsCompleted, body.ExpectedRevision, body.RequestId), context.RequestAborted)), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
        workItems.MapPut("/{workItemId:guid}/governance-exclusion", (Guid workItemId, ProjectWorkItemGovernanceExclusionBody body,
            IProjectWorkItemService service, HttpContext context)
            => RunAsync(async () => Results.Ok(await service.SetGovernanceExclusionAsync(new(workItemId, body.ProjectId,
                body.GovernanceRunId, body.Reason, body.Excluded, body.ExpectedRevision, body.RequestId), context.RequestAborted)), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
        return workItems;
    }

    private static async Task<IResult> RunAsync(Func<Task<IResult>> operation, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return await operation(); }
        catch (SafeEditConflictException error)
        {
            return Results.Conflict(new { code = error.Code, currentRevision = error.CurrentRevision, detail = error.Message });
        }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException) { return Results.BadRequest(new { code = "InvalidEditIntent", detail = "Verify the original read version and explicit edit fields." }); }
        catch (InvalidOperationException) { return Results.BadRequest(new { code = "InvalidEditState", detail = "Reload and review the current edit state." }); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("WorkItemEditing")
                .LogError(error, "Work item edit/read outcome could not be confirmed. Request {RequestId}", context.TraceIdentifier);
            return Results.Problem(statusCode: 503, title: "The edit outcome could not be confirmed.",
                extensions: new Dictionary<string, object?> { ["code"] = "RequestOutcomeUnknown", ["requestId"] = context.TraceIdentifier });
        }
    }
}

internal sealed record WorkItemEditPreconditionBody(long? ExpectedRevision = null, string? RequestId = null);
internal sealed record ProjectWorkItemChecklistCompletionBody(bool IsCompleted, long? ExpectedRevision = null, string? RequestId = null);
internal sealed record ProjectWorkItemGovernanceExclusionBody(string ProjectId, string GovernanceRunId, string Reason,
    bool Excluded = true, long? ExpectedRevision = null, string? RequestId = null);

internal sealed class ProjectWorkItemUpdateBody
{
    public string? Title { get; init; }
    public string? Description { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public ProjectWorkItemStatus? Status { get; init; }
    public int? Priority { get; init; }
    private DateTimeOffset? _dueAt;
    public DateTimeOffset? DueAt { get => _dueAt; init { _dueAt = value; DueAtSpecified = true; } }
    [JsonIgnore] public bool DueAtSpecified { get; private set; }
    public ProjectWorkItemDefinitionState? DefinitionState { get; init; }
    public long? ExpectedRevision { get; init; }
    public string? RequestId { get; init; }
    public WorkItemEditOperation Operation { get; init; }
    public WorkItemEditPatch? Edit { get; init; }

    public ProjectWorkItemUpdateRequest ToRequest(Guid id)
    {
        var hasFlat = Title is not null || Description is not null || Tags is not null || Status.HasValue || Priority.HasValue || DueAtSpecified || DefinitionState.HasValue;
        if (Edit is not null && hasFlat) throw new ArgumentException("Use one edit representation.");
        var patch = Edit ?? new WorkItemEditPatch(Title, Description, Tags, Priority, Status, DefinitionState,
            DueAtSpecified ? DueAt.HasValue ? new(EditDueAtMode.Set, DueAt) : new(EditDueAtMode.Clear) : new());
        return new(id, ExpectedRevision: ExpectedRevision, RequestId: RequestId, Operation: Operation, Edit: patch);
    }
}

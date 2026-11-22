using Memory.Application;

namespace Memory.McpServer;

public static class ChatGptProposalDecisionEndpoints
{
    public static RouteGroupBuilder MapProposalDecisionEndpoints(this RouteGroupBuilder group, bool requireAuthentication)
    {
        group.MapPost("/proposals/{proposalId:guid}/approve", (Guid proposalId, ChatGptProposalDecisionBody body,
            IChatGptProposalService service, HttpContext context) => RunAsync(() => service.ApproveAsync(new(proposalId, body.Note ?? ""), context.RequestAborted), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
        group.MapPost("/proposals/{proposalId:guid}/reject", (Guid proposalId, ChatGptProposalDecisionBody body,
            IChatGptProposalService service, HttpContext context) => RunAsync(() => service.RejectAsync(new(proposalId, body.Note ?? ""), context.RequestAborted), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
        return group;
    }

    private static async Task<IResult> RunAsync(Func<Task<ChatGptProposalResult>> action, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        try { return Results.Ok(await action()); }
        catch (MemoryScoreValidationException) { throw; }
        catch (SafeEditConflictException error)
        { return Results.Conflict(new { code = error.Code, currentRevision = error.CurrentRevision, detail = error.Message }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (KeyNotFoundException) { return Results.NotFound(); }
        catch (ArgumentException) { return Results.BadRequest(new { code = "InvalidProposalIntent", detail = "Verify the original proposal payload and read version." }); }
        catch (InvalidOperationException) { return Results.BadRequest(new { code = "InvalidProposalState", detail = "Reload the proposal without replacing its original intent." }); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("WorkItemProposal")
                .LogWarning(error, "Proposal outcome is unconfirmed. Trace {TraceId}", context.TraceIdentifier);
            return Results.Problem(statusCode: 503, title: "The original proposal outcome could not be confirmed.",
                extensions: new Dictionary<string, object?> { ["code"] = "ProposalOutcomeUnknown", ["traceId"] = context.TraceIdentifier });
        }
    }
}

internal sealed record ChatGptProposalDecisionBody(string? Note = null);

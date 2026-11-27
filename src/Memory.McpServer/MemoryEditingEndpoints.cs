using Memory.Application;

namespace Memory.McpServer;

public static class MemoryEditingEndpoints
{
    public static RouteGroupBuilder MapMemoryEditingEndpoints(this RouteGroupBuilder memories, bool requireAuthentication)
    {
        memories.MapPost("/{id:guid}/edits",
            (Guid id, MemoryEditCommand command, IMemoryEditingService service, HttpContext context) =>
                RunAsync(id, command.MemoryId, async () => await service.ApplyAsync(command, context.RequestAborted), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

        memories.MapPost("/{id:guid}/edits/receipt",
            (Guid id, MemoryEditRecoveryRequest request, IMemoryEditingService service, HttpContext context) =>
                RunAsync(id, request.MemoryId, () => service.ReadReceiptAsync(request, context.RequestAborted), context))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);

        return memories;
    }

    private static async Task<IResult> RunAsync(Guid id, Guid bodyId, Func<Task<MemoryEditReceipt?>> operation, HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (id == Guid.Empty || id != bodyId)
            return Results.BadRequest(new { code = "TargetMismatch" });

        try
        {
            var receipt = await operation();
            return receipt is null ? Results.NotFound() : Results.Ok(receipt);
        }
        catch (SafeEditConflictException error) when (error.Code is SafeEditConflictCodes.MissingReadVersion or
            SafeEditConflictCodes.StaleReadVersion or SafeEditConflictCodes.RequestIdentityMismatch)
        {
            return Results.Conflict(new
            {
                code = error.Code,
                currentVersion = error.Code == SafeEditConflictCodes.StaleReadVersion ? error.CurrentRevision : null
            });
        }
        catch (Exception error) when (error is UnauthorizedAccessException or KeyNotFoundException)
        {
            return Results.NotFound();
        }
        catch (Exception error) when (error is ArgumentException or MemoryScoreValidationException)
        {
            return Results.BadRequest(new { code = "InvalidEditIntent" });
        }
        catch (MaintenanceUnavailableException)
        {
            throw;
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            context.RequestServices.GetService<ILoggerFactory>()?.CreateLogger("MemoryEditing")
                .LogWarning("The original memory edit outcome could not be confirmed. Trace {TraceId}", context.TraceIdentifier);
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "The original memory edit outcome could not be confirmed.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = "RequestOutcomeUnknown",
                    ["traceId"] = context.TraceIdentifier
                });
        }
    }
}

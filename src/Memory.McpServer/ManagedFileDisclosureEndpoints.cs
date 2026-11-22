using Memory.Application;

namespace Memory.McpServer;

public static class ManagedFileDisclosureEndpoints
{
    public static RouteGroupBuilder MapFileDisclosureReadEndpoints(this RouteGroupBuilder files, bool requireAuthentication)
    {
        files.MapPost("/{fileId:guid}/versions/{versionId:guid}/content-metadata", (Guid fileId, Guid versionId,
            FileContentMetadataRequest request, IManagedFileDisclosureService service, HttpResponse response, CancellationToken ct)
            => ReadAsync(async () => await service.ReadContentMetadataAsync(request.ProjectId, fileId, versionId, request.Purpose, ct) is { } result
                ? Results.Ok(result) : Results.NotFound(), response, ct))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        files.MapGet("/{fileId:guid}/versions/{versionId:guid}/audit", (string projectId, Guid fileId, Guid versionId,
            int? limit, int? offset, IManagedFileDisclosureService service, HttpResponse response, CancellationToken ct)
            => ReadAsync(async () => await service.ReadAuditAsync(projectId, fileId, versionId, limit ?? 25, offset ?? 0, ct) is { } result
                ? Results.Ok(result) : Results.NotFound(), response, ct))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        return files;
    }

    private static async Task<IResult> ReadAsync(Func<Task<IResult>> read, HttpResponse response, CancellationToken caller)
    {
        response.Headers.CacheControl = "no-store";
        try { return await read(); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "An explicit project and bounded purpose are required." }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 503, title: "File disclosure read did not complete.",
                extensions: new Dictionary<string, object?> { ["code"] = "MetadataReadIncomplete" });
        }
    }
}

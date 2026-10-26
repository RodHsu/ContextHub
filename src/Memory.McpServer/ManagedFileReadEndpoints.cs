using Memory.Application;

namespace Memory.McpServer;

public static class ManagedFileReadEndpoints
{
    public static RouteGroupBuilder MapFileMetadataReadEndpoints(this RouteGroupBuilder files, bool requireAuthentication)
    {
        files.MapGet("/inventory", (string projectId, int? limit, int? offset, IManagedFileReadService service, CancellationToken ct)
            => ReadAsync(async () => Results.Ok(await service.ListAsync(projectId, limit ?? 50, offset ?? 0, ct)), ct))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        files.MapGet("/{fileId:guid}", (string projectId, Guid fileId, IManagedFileReadService service, CancellationToken ct)
            => ReadAsync(async () => await service.GetAsync(projectId, fileId, ct) is { } result ? Results.Ok(result) : Results.NotFound(), ct))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        files.MapGet("/{fileId:guid}/versions", (string projectId, Guid fileId, int? limit, int? offset, IManagedFileReadService service, CancellationToken ct)
            => ReadAsync(async () => await service.ListVersionsAsync(projectId, fileId, limit ?? 50, offset ?? 0, ct) is { } result ? Results.Ok(result) : Results.NotFound(), ct))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        files.MapGet("/{fileId:guid}/versions/{versionId:guid}", (string projectId, Guid fileId, Guid versionId, IManagedFileReadService service, CancellationToken ct)
            => ReadAsync(async () => await service.GetVersionAsync(projectId, fileId, versionId, ct) is { } result ? Results.Ok(result) : Results.NotFound(), ct))
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        return files;
    }

    private static async Task<IResult> ReadAsync(Func<Task<IResult>> read, CancellationToken caller)
    {
        try { return await read(); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return Results.Problem(statusCode: StatusCodes.Status503ServiceUnavailable,
                title: "File metadata read did not complete.", extensions: new Dictionary<string, object?> { ["code"] = "MetadataReadIncomplete" });
        }
    }
}

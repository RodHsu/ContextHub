using Memory.Application;

namespace Memory.McpServer;

public static class ManagedFileDownloadEndpoints
{
    public static RouteGroupBuilder MapLogicalFileDownloadEndpoints(this RouteGroupBuilder files, bool requireAuthentication)
    {
        files.MapPost("/{fileId:guid}/versions/{fileVersionId:guid}/downloads", async (
            Guid fileId, Guid fileVersionId, CreateLogicalFileDownloadRequest request,
            IManagedFileDownloadService service, HttpResponse response, CancellationToken ct) =>
        {
            // This response contains a bearer capability for a trusted transport client only.
            response.Headers.CacheControl = "no-store";
            response.Headers.Pragma = "no-cache";
            try
            {
                var result = await service.CreateAsync(fileId, fileVersionId, request, ct);
                return result is null ? Results.NotFound() : Results.Ok(result);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "An explicit project, purpose and request identity are required (purpose/identity at most 200 characters)." });
            }
        }).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        return files;
    }
}

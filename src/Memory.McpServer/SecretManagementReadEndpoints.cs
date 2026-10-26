using Memory.Application;

namespace Memory.McpServer;

public static class SecretManagementReadEndpoints
{
    public static RouteGroupBuilder MapSecretManagementReadEndpoints(this RouteGroupBuilder secrets, bool requireAuthentication)
    {
        var management = secrets.MapGroup("/{secretId:guid}/management");
        management.RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsRead);
        management.RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsManage);
        management.MapGet("/expiry", (string projectId, Guid secretId, ISecretManagementReadService service, HttpResponse response, CancellationToken ct)
            => ReadAsync(async () => await service.ReadExpiryAsync(projectId, secretId, ct) is { } result ? Results.Ok(result) : Results.NotFound(), response, ct));
        management.MapGet("/leases", (string projectId, Guid secretId, int? limit, int? offset, ISecretManagementReadService service, HttpResponse response, CancellationToken ct)
            => ReadAsync(async () => await service.ReadLeasesAsync(projectId, secretId, limit ?? 25, offset ?? 0, ct) is { } result ? Results.Ok(result) : Results.NotFound(), response, ct));
        management.MapGet("/history", (string projectId, Guid secretId, int? limit, int? offset, ISecretManagementReadService service, HttpResponse response, CancellationToken ct)
            => ReadAsync(async () => await service.ReadHistoryAsync(projectId, secretId, limit ?? 25, offset ?? 0, ct) is { } result ? Results.Ok(result) : Results.NotFound(), response, ct));
        return secrets;
    }

    private static async Task<IResult> ReadAsync(Func<Task<IResult>> read, HttpResponse response, CancellationToken caller)
    {
        response.Headers.CacheControl = "no-store";
        try { return await read(); }
        catch (ArgumentException) { return Results.BadRequest(new { error = "An explicit project is required." }); }
        catch (UnauthorizedAccessException) { return Results.Forbid(); }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            return Results.Problem(statusCode: 503, title: "Secret management read did not complete.",
                extensions: new Dictionary<string, object?> { ["code"] = "MetadataReadIncomplete" });
        }
    }
}

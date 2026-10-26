using Memory.Application;
using Memory.Domain;

namespace Memory.McpServer;

public static class ProjectWorkItemReadEndpoints
{
    public static RouteGroupBuilder MapWorkItemReadEndpoints(this RouteGroupBuilder workItems, bool requireAuthentication)
    {
        workItems.MapGet(string.Empty, async (string projectId, string? status, string? definitionState, int? limit, int? offset, bool? includeArchived, IProjectWorkItemService service, CancellationToken cancellationToken) =>
        {
            if (!EnumParser.TryParse(status, out ProjectWorkItemStatus? parsedStatus, out var error))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [error ?? "Unsupported ProjectWorkItemStatus value."] });
            }
            if (!EnumParser.TryParse(definitionState, out ProjectWorkItemDefinitionState? parsedDefinitionState, out var definitionError))
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["definitionState"] = [definitionError ?? "Unsupported ProjectWorkItemDefinitionState value."] });
            }
            return Results.Ok(await service.ListAsync(new ProjectWorkItemListRequest(ProjectContext.Normalize(projectId), parsedStatus, limit ?? 100, includeArchived ?? false, Offset: offset ?? 0, DefinitionState: parsedDefinitionState), cancellationToken));
        }).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        workItems.MapGet("/{id:guid}", async (Guid id, bool? includeArchived, IProjectWorkItemService service, CancellationToken cancellationToken)
            => await service.GetAsync(id, includeArchived ?? false, cancellationToken) is { } item ? Results.Ok(item) : Results.NotFound())
            .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
        return workItems;
    }
}

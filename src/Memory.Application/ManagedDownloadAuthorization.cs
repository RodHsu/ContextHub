using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

// The existing transfer download checks, shared with mutation-free disclosure reads.
internal static class ManagedDownloadAuthorization
{
    internal static async Task<ManagedObject> AuthorizeAsync(IApplicationDbContext dbContext,
        IPlatformFoundationStore foundation, ContextHubRequestActor actor, string projectId,
        Guid objectId, Guid? fileVersionId, string purpose, CancellationToken cancellationToken)
    {
        var managedObject = await Scope(dbContext.ManagedObjects.AsNoTracking(), actor)
            .SingleOrDefaultAsync(x => x.Id == objectId && Memory.Application.ProjectContext.Matches(x.ProjectId, projectId), cancellationToken)
            ?? throw new KeyNotFoundException("Managed object was not found.");
        if (managedObject.State != ManagedObjectState.Ready)
        {
            throw new InvalidOperationException("Managed object is not available for transfer.");
        }

        var linkedVersions = dbContext.FileVersions.AsNoTracking()
            .Where(x => x.ManagedObjectId == managedObject.Id);
        if (!fileVersionId.HasValue)
        {
            if (await linkedVersions.AnyAsync(cancellationToken))
                throw new UnauthorizedAccessException("A logical FileVersion reference is required for managed file download.");

            return managedObject;
        }

        var linked = await linkedVersions.Include(x => x.FileAsset)
            .SingleOrDefaultAsync(x => x.Id == fileVersionId.Value, cancellationToken);
        if (linked?.FileAsset is not { } asset || asset.State == FileAssetState.LogicalDeleted ||
            asset.TenantId != actor.TenantId || (!actor.IsAdmin && !actor.IsServiceActor && asset.OwnerUserId != actor.UserId) ||
            (asset.ProjectId is not null && !ProjectContext.Matches(asset.ProjectId, projectId)))
        {
            throw new UnauthorizedAccessException("Managed file download reference is invalid.");
        }
        if (linked.Lifecycle != FileVersionLifecycle.Ready || linked.NeedsRescan)
        {
            throw new UnauthorizedAccessException("Managed file download requires a successfully scanned ready version.");
        }
        var classificationDecision = FileSecurityPolicy.Authorize(linked.Classification, FileOperation.Download, purpose, actor.IsAdmin || actor.HasScope(SecurityScopes.SecurityManage));
        if (!classificationDecision.Allowed) throw new UnauthorizedAccessException("Managed file classification denies download.");
        if (asset.ProjectId is string fileProjectId)
        {
            var principal = actor.UserId?.ToString("D") ?? actor.Username;
            var rights = await foundation.EvaluateAsync(fileProjectId, principal, ["download"], "File", linked.FileAssetId.ToString("D"), cancellationToken);
            if (!rights.Decisions.Single().Allowed) throw new UnauthorizedAccessException("Managed file effective rights deny download.");
        }
        else if (!actor.IsAdmin && asset.OwnerUserId != actor.UserId)
        {
            throw new UnauthorizedAccessException("Unassigned files are uploader/admin only.");
        }
        return managedObject;
    }

    private static IQueryable<T> Scope<T>(IQueryable<T> query, ContextHubRequestActor actor) where T : class
        => !actor.HasUser ? query : actor.IsServiceActor || actor.IsAdmin
            ? query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId)
            : query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId && EF.Property<Guid?>(x, "OwnerUserId") == actor.UserId);
}

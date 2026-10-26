using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed record CreateLogicalFileDownloadRequest(
    string ProjectId,
    string Purpose,
    string IdempotencyKey,
    string? AgentId = null,
    Guid? ExecutionId = null);

public sealed record LogicalFileDownloadSessionResult(
    Guid FileId,
    Guid FileVersionId,
    Guid SessionId,
    Guid CapabilityId,
    string Capability,
    ManagedTransferOperation Operation,
    long Revision,
    int ChunkBytes,
    long MaxBytes,
    DateTimeOffset ExpiresAt,
    string ResourcePath);

public interface IManagedFileDownloadService
{
    Task<LogicalFileDownloadSessionResult?> CreateAsync(Guid fileId, Guid fileVersionId,
        CreateLogicalFileDownloadRequest request, CancellationToken cancellationToken);
}

public sealed class ManagedFileDownloadService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IManagedTransferService transfers) : IManagedFileDownloadService
{
    public async Task<LogicalFileDownloadSessionResult?> CreateAsync(Guid fileId, Guid fileVersionId,
        CreateLogicalFileDownloadRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        if (string.IsNullOrWhiteSpace(request.ProjectId)) throw new ArgumentException("An explicit project is required.");
        var projectId = ProjectContext.Normalize(request.ProjectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
        if (string.IsNullOrWhiteSpace(request.Purpose) || request.Purpose.Trim().Length > 200
            || string.IsNullOrWhiteSpace(request.IdempotencyKey) || request.IdempotencyKey.Trim().Length > 200)
        {
            throw new ArgumentException("Transfer purpose and request identity are required and must not exceed 200 characters.");
        }

        // Resolve only the exact logical pin in the caller's existing inventory scope.
        // No metadata right is substituted for the content/download authorization below.
        var objectId = await ResolveObject(actor, projectId, fileId, fileVersionId).SingleOrDefaultAsync(cancellationToken);
        if (!objectId.HasValue) return null;

        ManagedTransferSessionResult session;
        try
        {
            // Reuse object scope/project, lifecycle, DLP/purpose and effective download rights.
            // The persisted capability remains actor-bound; the raw bearer is never recovered/replayed here.
            session = await transfers.CreateDownloadAsync(new CreateManagedDownloadRequest(projectId, objectId.Value,
                request.Purpose, request.IdempotencyKey, request.AgentId, request.ExecutionId, fileVersionId), cancellationToken);
        }
        catch (KeyNotFoundException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (InvalidOperationException exception) when (exception.Message == "Managed object is not available for transfer.")
        {
            return null;
        }

        return new(fileId, fileVersionId, session.SessionId, session.CapabilityId, session.Capability,
            session.Operation, session.Revision, session.ChunkBytes, session.MaxBytes, session.ExpiresAt, session.ResourcePath);
    }

    private IQueryable<Guid?> ResolveObject(ContextHubRequestActor actor, string projectId, Guid fileId, Guid fileVersionId)
    {
        var assets = dbContext.FileAssets.AsNoTracking().Where(asset => asset.TenantId == actor.TenantId
            && (actor.IsAdmin || actor.IsServiceActor || asset.OwnerUserId == actor.UserId)
            && asset.State != FileAssetState.LogicalDeleted
            && (asset.ProjectId == null || ProjectContext.Matches(asset.ProjectId, projectId)));
        return from version in dbContext.FileVersions.AsNoTracking()
               join asset in assets on version.FileAssetId equals asset.Id
               where asset.Id == fileId && version.Id == fileVersionId
                   && version.Lifecycle == FileVersionLifecycle.Ready && !version.NeedsRescan
               select (Guid?)version.ManagedObjectId;
    }
}

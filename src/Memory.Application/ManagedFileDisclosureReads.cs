using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed record FileContentMetadataRequest(string ProjectId, string Purpose);
public sealed record ManagedFileContentMetadata(Guid FileId, Guid FileVersionId, long? SizeBytes, string? ContentSha256);
public sealed record ManagedFileAuditEntry(Guid AuditId, FileOperation Operation, DateTimeOffset CreatedAt);
public sealed record ManagedFileAuditPage(Guid FileId, Guid FileVersionId, IReadOnlyList<ManagedFileAuditEntry> Items, int Limit, int Offset, bool HasMore)
{
    public bool ActorMasked => true;
    public bool PurposeMasked => true;
    public bool DeniedDetailsMasked => true;
}

public interface IManagedFileDisclosureService
{
    Task<ManagedFileContentMetadata?> ReadContentMetadataAsync(string projectId, Guid fileId, Guid versionId, string purpose, CancellationToken ct);
    Task<ManagedFileAuditPage?> ReadAuditAsync(string projectId, Guid fileId, Guid versionId, int limit, int offset, CancellationToken ct);
}

public sealed class ManagedFileDisclosureService(IApplicationDbContext dbContext, IRequestActorAccessor actorAccessor,
    IPlatformFoundationStore foundation, IManagedFileReadService metadata, TimeProvider? timeProvider = null) : IManagedFileDisclosureService
{
    public async Task<ManagedFileContentMetadata?> ReadContentMetadataAsync(string projectId, Guid fileId, Guid versionId, string purpose, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = operation.Token;
        var (actor, project) = RequireActor(projectId);
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Trim().Length > 200) throw new ArgumentException("A bounded explicit purpose is required.");
        var objectId = await ManagedFileDownloadService.ResolveLogicalObject(dbContext, actor, project, fileId, versionId).SingleOrDefaultAsync(ct);
        if (!objectId.HasValue) return null;
        ManagedObject authorized;
        try
        {
            authorized = await ManagedDownloadAuthorization.AuthorizeAsync(dbContext, foundation, actor, project, objectId.Value, versionId, purpose, ct);
        }
        catch (UnauthorizedAccessException) { return null; }
        catch (KeyNotFoundException) { return null; }
        catch (InvalidOperationException exception) when (exception.Message == "Managed object is not available for transfer.") { return null; }
        var version = await dbContext.FileVersions.AsNoTracking()
            .Where(v => v.Id == versionId && v.FileAssetId == fileId && v.ManagedObjectId == authorized.Id
                && v.Lifecycle == FileVersionLifecycle.Ready && !v.NeedsRescan)
            .Select(v => new { v.ContentSha256 }).SingleOrDefaultAsync(ct);
        if (version is null) return null;
        var hash = version.ContentSha256;
        return new(fileId, versionId, authorized.PlaintextLength >= 0 ? authorized.PlaintextLength : null,
            hash is { Length: 64 } && hash.All(Uri.IsHexDigit) ? hash.ToLowerInvariant() : null);
    }

    public async Task<ManagedFileAuditPage?> ReadAuditAsync(string projectId, Guid fileId, Guid versionId, int limit, int offset, CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        ct = operation.Token;
        var (actor, project) = RequireActor(projectId);
        // Exact reader predicate already used by /api/security/audit-events. No new role/right.
        if (!actor.IsAdmin) throw new UnauthorizedAccessException("File audit read is unavailable.");
        var visible = await metadata.GetVersionAsync(project, fileId, versionId, ct);
        if (visible is null) return null;
        var pageLimit = Math.Clamp(limit, 1, 100);
        var pageOffset = Math.Max(0, offset);
        var eventProject = visible.ProjectId ?? ProjectContext.DefaultProjectId;
        var query = VisibleAuditEntries(actor, eventProject, fileId, versionId);
        var rows = new List<ManagedFileAuditEntry>(pageLimit + 1);
        while (rows.Count <= pageLimit)
        {
            var take = Math.Min(100, pageLimit + 1 - rows.Count);
            var batch = await query.Skip(checked(pageOffset + rows.Count)).Take(take).ToArrayAsync(ct);
            rows.AddRange(batch);
            if (batch.Length < take) break;
        }
        return new(fileId, versionId, rows.Take(pageLimit).ToArray(), pageLimit, pageOffset, rows.Count > pageLimit);
    }

    private IQueryable<ManagedFileAuditEntry> VisibleAuditEntries(ContextHubRequestActor actor, string eventProject, Guid fileId, Guid versionId)
        => dbContext.FileAccessEvents.AsNoTracking()
            .Where(e => e.TenantId == actor.TenantId && e.FileAssetId == fileId && e.FileVersionId == versionId
                && ProjectContext.Matches(e.ProjectId, eventProject) && e.Allowed)
            .OrderByDescending(e => e.CreatedAt).ThenBy(e => e.Id)
            // Raw actor, purpose, reason and denied rows never enter this projection.
            .Select(e => new ManagedFileAuditEntry(e.Id, e.Operation, e.CreatedAt));

    private (ContextHubRequestActor Actor, string Project) RequireActor(string projectId)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        if (string.IsNullOrWhiteSpace(projectId)) throw new ArgumentException("An explicit project is required.");
        var project = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
        return (actor, project);
    }
}

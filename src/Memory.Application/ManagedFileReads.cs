using System.Linq.Expressions;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed record ManagedFileMetadataPage(
    IReadOnlyList<ManagedFileInventoryResult> Items,
    int Limit,
    int Offset,
    bool HasMore);

/// <summary>
/// Counts the latest visible version of each active file. The status counts can overlap.
/// Initial pending means integrity verified with no recorded assessment. A failed assessment contributes to NeedsRescan.
/// </summary>
public sealed record ManagedFileDlpSummary(
    long VisibleLatestVersions,
    long Quarantined,
    long NeedsRescan,
    long InitialPending);

public interface IManagedFileReadService
{
    Task<ManagedFileMetadataPage> ListAsync(string projectId, int limit, int offset, CancellationToken cancellationToken);
    Task<ManagedFileDlpSummary> GetDlpSummaryAsync(string? projectId, CancellationToken cancellationToken);
    Task<ManagedFileInventoryResult?> GetAsync(string projectId, Guid fileId, CancellationToken cancellationToken);
    Task<ManagedFileMetadataPage?> ListVersionsAsync(string projectId, Guid fileId, int limit, int offset, CancellationToken cancellationToken);
    Task<ManagedFileInventoryResult?> GetVersionAsync(string projectId, Guid fileId, Guid versionId, CancellationToken cancellationToken);
}

/// <summary>Reads the same safe status metadata as the inventory, without granting content or audit access.</summary>
public sealed class ManagedFileReadService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IPlatformFoundationStore foundation,
    TimeProvider? timeProvider = null) : IManagedFileReadService
{
    private const int CandidateBatchSize = 100;

    public async Task<ManagedFileDlpSummary> GetDlpSummaryAsync(string? projectId, CancellationToken cancellationToken)
    {
        var requestedProject = ProjectContext.Normalize(projectId);
        if (ProjectContext.Matches(requestedProject, ProjectContext.DefaultProjectId) ||
            requestedProject.Contains(ProjectContext.AllProjectIdsSentinel, StringComparison.Ordinal))
        {
            throw new ArgumentException("An explicit project is required.", nameof(projectId));
        }

        var (actor, project) = RequireActor(requestedProject);
        var tenantId = actor.TenantId ?? throw new UnauthorizedAccessException("A tenant-scoped actor is required.");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = operation.Token;

        Guid? afterFileId = null;
        long visible = 0, quarantined = 0, needsRescan = 0, initialPending = 0;
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var batch = await DlpSummaryBatch(actor, project, tenantId, afterFileId).ToArrayAsync(cancellationToken);
            foreach (var version in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!await CanReadMetadataAsync(actor, project, version.FileAssetId, cancellationToken)) continue;

                visible++;
                if (version.Classification == FileClassification.Quarantined) quarantined++;
                if (version.NeedsRescan) needsRescan++;
                if (version.Lifecycle == FileVersionLifecycle.IntegrityVerified && version.LastScanAt is null) initialPending++;
            }

            if (batch.Length < CandidateBatchSize) break;
            afterFileId = batch[^1].FileAssetId;
        }

        cancellationToken.ThrowIfCancellationRequested();
        return new(visible, quarantined, needsRescan, initialPending);
    }

    public async Task<ManagedFileMetadataPage> ListAsync(string projectId, int limit, int offset, CancellationToken cancellationToken)
    {
        // Match the existing Dashboard client window; cancellation never becomes a partial/empty page.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = operation.Token;
        var (actor, project) = RequireActor(projectId);
        var pageLimit = Math.Clamp(limit, 1, 100);
        var pageOffset = Math.Max(0, offset);
        var results = new List<ManagedFileInventoryResult>(pageLimit + 1);
        var visible = 0;
        var candidateOffset = 0;
        while (results.Count <= pageLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var ids = await Assets(actor, project)
                .Where(asset => asset.Versions.Any(version => version.Lifecycle != FileVersionLifecycle.LogicalDeleted))
                .OrderByDescending(asset => asset.UpdatedAt).ThenBy(asset => asset.Id)
                .Skip(candidateOffset).Take(CandidateBatchSize)
                .Select(asset => asset.Id).ToArrayAsync(cancellationToken);
            if (ids.Length == 0) break;
            // Project scalar counts rather than loading all versions, relations or findings.
            var latest = await LatestMetadata(actor, project, ids).ToArrayAsync(cancellationToken);
            var byAsset = latest.ToDictionary(row => row.FileId);
            foreach (var id in ids)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!byAsset.TryGetValue(id, out var row) || !await CanReadMetadataAsync(actor, project, id, cancellationToken)) continue;
                if (visible++ < pageOffset) continue;
                results.Add(row);
                if (results.Count > pageLimit) break;
            }
            if (ids.Length < CandidateBatchSize) break;
            candidateOffset = checked(candidateOffset + ids.Length);
        }
        return Page(results, pageLimit, pageOffset);
    }

    public async Task<ManagedFileInventoryResult?> GetAsync(string projectId, Guid fileId, CancellationToken cancellationToken)
    {
        // Match the existing Dashboard client window; cancellation never becomes a partial/empty page.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = operation.Token;
        var (actor, project) = RequireActor(projectId);
        if (!await IsVisibleAsync(actor, project, fileId, cancellationToken)) return null;
        return await Versions(actor, project).Where(version => version.FileAssetId == fileId)
            .OrderByDescending(version => version.VersionNumber).ThenBy(version => version.Id)
            .Select(MetadataProjection).FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<ManagedFileMetadataPage?> ListVersionsAsync(string projectId, Guid fileId, int limit, int offset, CancellationToken cancellationToken)
    {
        // Match the existing Dashboard client window; cancellation never becomes a partial/empty page.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = operation.Token;
        var (actor, project) = RequireActor(projectId);
        if (!await IsVisibleAsync(actor, project, fileId, cancellationToken)) return null;
        var pageLimit = Math.Clamp(limit, 1, 100);
        var pageOffset = Math.Max(0, offset);
        var rows = new List<ManagedFileInventoryResult>(pageLimit + 1);
        // Even the look-ahead query stays at <=100 candidate rows.
        while (rows.Count <= pageLimit)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var take = Math.Min(CandidateBatchSize, pageLimit + 1 - rows.Count);
            var batch = await Versions(actor, project).Where(version => version.FileAssetId == fileId)
                .OrderByDescending(version => version.VersionNumber).ThenBy(version => version.Id)
                .Skip(checked(pageOffset + rows.Count)).Take(take)
                .Select(MetadataProjection).ToArrayAsync(cancellationToken);
            rows.AddRange(batch);
            if (batch.Length < take) break;
        }
        return Page(rows, pageLimit, pageOffset);
    }

    public async Task<ManagedFileInventoryResult?> GetVersionAsync(string projectId, Guid fileId, Guid versionId, CancellationToken cancellationToken)
    {
        // Match the existing Dashboard client window; cancellation never becomes a partial/empty page.
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15), timeProvider ?? TimeProvider.System);
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        cancellationToken = operation.Token;
        var (actor, project) = RequireActor(projectId);
        if (!await IsVisibleAsync(actor, project, fileId, cancellationToken)) return null;
        return await Versions(actor, project)
            .Where(version => version.FileAssetId == fileId && version.Id == versionId)
            .Select(MetadataProjection).SingleOrDefaultAsync(cancellationToken);
    }

    private (ContextHubRequestActor Actor, string Project) RequireActor(string projectId)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var project = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
        return (actor, project);
    }

    private IQueryable<FileAsset> Assets(ContextHubRequestActor actor, string project)
    {
        var query = dbContext.FileAssets.AsNoTracking()
            .Where(asset => ProjectContext.Matches(asset.ProjectId, project) && asset.State != FileAssetState.LogicalDeleted);
        if (!actor.HasUser) return query;
        return actor.IsServiceActor || actor.IsAdmin
            ? query.Where(asset => asset.TenantId == actor.TenantId)
            : query.Where(asset => asset.TenantId == actor.TenantId && asset.OwnerUserId == actor.UserId);
    }

    private IQueryable<FileVersion> Versions(ContextHubRequestActor actor, string project)
        => from version in dbContext.FileVersions.AsNoTracking()
           join asset in Assets(actor, project) on version.FileAssetId equals asset.Id
           where version.Lifecycle != FileVersionLifecycle.LogicalDeleted
           select version;

    private IQueryable<FileVersion> LatestVersions(ContextHubRequestActor actor, string project)
        => Versions(actor, project)
            .Where(version => version.VersionNumber ==
                dbContext.FileVersions.Where(other => other.FileAssetId == version.FileAssetId &&
                    other.Lifecycle != FileVersionLifecycle.LogicalDeleted).Max(other => other.VersionNumber));

    private IQueryable<DlpSummaryCandidate> DlpSummaryBatch(ContextHubRequestActor actor, string project, Guid tenantId, Guid? afterFileId)
    {
        var versions = LatestVersions(actor, project).Where(version => version.FileAsset!.TenantId == tenantId);
        if (afterFileId is Guid after)
        {
            versions = versions.Where(version => version.FileAssetId.CompareTo(after) > 0);
        }
        return versions.OrderBy(version => version.FileAssetId).Take(CandidateBatchSize)
            .Select(version => new DlpSummaryCandidate(version.FileAssetId, version.Lifecycle, version.Classification,
                version.NeedsRescan, version.LastScanAt));
    }

    private sealed record DlpSummaryCandidate(Guid FileAssetId, FileVersionLifecycle Lifecycle, FileClassification Classification,
        bool NeedsRescan, DateTimeOffset? LastScanAt);

    private IQueryable<ManagedFileInventoryResult> LatestMetadata(ContextHubRequestActor actor, string project, Guid[] ids)
        => LatestVersions(actor, project).Where(version => ids.Contains(version.FileAssetId))
            .Select(MetadataProjection);

    private async Task<bool> IsVisibleAsync(ContextHubRequestActor actor, string project, Guid fileId, CancellationToken cancellationToken)
        => await Assets(actor, project).AnyAsync(asset => asset.Id == fileId && asset.Versions.Any(version => version.Lifecycle != FileVersionLifecycle.LogicalDeleted), cancellationToken) &&
           await CanReadMetadataAsync(actor, project, fileId, cancellationToken);

    private async Task<bool> CanReadMetadataAsync(ContextHubRequestActor actor, string project, Guid fileId, CancellationToken cancellationToken)
    {
        var result = await foundation.EvaluateAsync(project, actor.UserId?.ToString("D") ?? actor.Username,
            ["metadata"], "File", fileId.ToString("D"), cancellationToken);
        return result.Decisions.Count == 1 && result.Decisions[0].Right == "metadata" && result.Decisions[0].Allowed;
    }

    private static ManagedFileMetadataPage Page(List<ManagedFileInventoryResult> rows, int limit, int offset)
        => new(rows.Take(limit).ToArray(), limit, offset, rows.Count > limit);

    private static readonly Expression<Func<FileVersion, ManagedFileInventoryResult>> MetadataProjection = version =>
        new ManagedFileInventoryResult(
            version.FileAssetId, version.Id, version.FileAsset!.LogicalFileName, version.FileAsset.ProjectId!,
            version.FileAsset.State, version.VersionNumber, version.Lifecycle, version.Classification,
            version.ClassificationRevision, version.NeedsRescan, version.LastScanAt,
            version.Findings.Count(finding => finding.Disposition == SecurityFindingDisposition.Open || finding.Disposition == SecurityFindingDisposition.Confirmed),
            (SecurityFindingSeverity?)version.Findings
                .Where(finding => finding.Disposition == SecurityFindingDisposition.Open || finding.Disposition == SecurityFindingDisposition.Confirmed)
                .Max(finding => finding.Severity == SecurityFindingSeverity.Critical ? (int?)4
                    : finding.Severity == SecurityFindingSeverity.High ? 3
                    : finding.Severity == SecurityFindingSeverity.Medium ? 2
                    : finding.Severity == SecurityFindingSeverity.Low ? 1 : 0),
            version.FileAsset.Relations.Count, version.UpdatedAt);
}

using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public enum ManagedFileCreateIntent
{
    Detect,
    CreateNewFile,
    CreateNewVersion,
    CreateSeparateLogicalFile
}

public enum ManagedFileCreateOutcome
{
    Created,
    ExactDuplicate,
    PossibleExistingFile,
    RequiresSemanticPurposeDecision
}

public sealed record CreateManagedFileRequest(
    Guid ManagedObjectId,
    string FileName,
    string ContentType,
    string Purpose,
    string IdempotencyKey,
    string? ProjectId = null,
    ManagedFileCreateIntent Intent = ManagedFileCreateIntent.Detect,
    Guid? ExistingFileId = null,
    bool SemanticPurposeConfirmed = false);

public sealed record ManagedFileCreateResult(
    ManagedFileCreateOutcome Outcome,
    Guid? FileId,
    Guid? FileVersionId,
    int? VersionNumber,
    FileClassification? Classification,
    FileVersionLifecycle? Lifecycle,
    IReadOnlyList<Guid> CandidateFileIds,
    bool BinaryReused);

public sealed record FileSecurityFindingDraft(
    string ScannerRuleId,
    SecurityFindingCategory Category,
    SecurityFindingSeverity Severity,
    decimal Confidence,
    string ScannerVersion,
    string EvidenceHash,
    string RedactedEvidence = "");

public sealed record FileSecurityAssessment(
    bool ScannerAvailable,
    bool ParserSupported,
    string ScannerSetVersion,
    string SecurityPolicyVersion,
    IReadOnlyList<FileSecurityFindingDraft> Findings);

public sealed record FileOperationDecision(bool Allowed, string ReasonCode, FileClassification Classification, long ClassificationRevision);
public sealed record ManagedFileSearchResult(Guid FileId, Guid FileVersionId, string FileName, string ProjectId, FileClassification Classification, string Snippet);
public sealed record ManagedFileInventoryResult(
    Guid FileId,
    Guid FileVersionId,
    string FileName,
    string ProjectId,
    FileAssetState State,
    int VersionNumber,
    FileVersionLifecycle Lifecycle,
    FileClassification Classification,
    long ClassificationRevision,
    bool NeedsRescan,
    DateTimeOffset? LastScanAt,
    int OpenSecurityFindings,
    SecurityFindingSeverity? HighestOpenSeverity,
    int RelationCount,
    DateTimeOffset UpdatedAt);

public sealed record CreateFileRepresentationRequest(
    Guid FileVersionId,
    FileRepresentationKind Kind,
    string ContentHash,
    FileClassification Classification,
    Guid? ManagedObjectId = null);

public sealed record QuarantineReleaseRequest(Guid FileVersionId, string Reason, string ExternalApprovalReference, long ExpectedClassificationRevision);

public sealed record QuarantineReleaseResult(bool StartedRescan, bool RequiresExternalApproval, long ClassificationRevision, FileVersionLifecycle Lifecycle);

public sealed record FileDeleteRequest(
    Guid FileId,
    string Reason,
    string RequestId);

public sealed record FileDeleteResult(bool RequiresUserDecision, FileDeletionState State, IReadOnlyList<FileDeletionRecord> Records);

public interface IHighAssuranceApprovalVerifier
{
    Task<bool> VerifyAsync(string actorId, string approvalReference, string purpose, CancellationToken cancellationToken);
}

public sealed class DenyHighAssuranceApprovalVerifier : IHighAssuranceApprovalVerifier
{
    public Task<bool> VerifyAsync(string actorId, string approvalReference, string purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(false);
    }
}

public interface IManagedFileService
{
    Task<ManagedFileCreateResult> CreateAsync(CreateManagedFileRequest request, CancellationToken cancellationToken);
    Task<FileVersion> ApplySecurityAssessmentAsync(Guid fileVersionId, FileSecurityAssessment assessment, CancellationToken cancellationToken);
    Task<FileOperationDecision> AuthorizeOperationAsync(Guid fileVersionId, FileOperation operation, string purpose, CancellationToken cancellationToken);
    Task<FileRepresentation> AddRepresentationAsync(CreateFileRepresentationRequest request, CancellationToken cancellationToken);
    Task<QuarantineReleaseResult> RequestQuarantineReleaseAsync(QuarantineReleaseRequest request, CancellationToken cancellationToken);
    Task<FileDeleteResult> RequestDeleteAsync(FileDeleteRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ManagedFileInventoryResult>> ListAsync(string projectId, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<ManagedFileSearchResult>> SearchAsync(string projectId, string query, int limit, CancellationToken cancellationToken);
}

public sealed class ManagedFileService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    IPlatformFoundationStore foundation,
    IHighAssuranceApprovalVerifier approvalVerifier) : IManagedFileService
{
    public Task<ManagedFileCreateResult> CreateAsync(CreateManagedFileRequest request, CancellationToken cancellationToken)
        => dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var actor = RequireActor(SecurityScopes.MemoryWrite);
            var fileName = RequireText(request.FileName, nameof(request.FileName), 512);
            var normalizedFileName = NormalizeFileName(fileName);
            var purpose = RequireText(request.Purpose, nameof(request.Purpose), 200);
            var idempotencyKey = RequireText(request.IdempotencyKey, nameof(request.IdempotencyKey), 200);
            var projectId = string.IsNullOrWhiteSpace(request.ProjectId) ? null : ProjectContext.Normalize(request.ProjectId);
            if (projectId is not null) ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);

            await dbContext.AcquireTransactionLockAsync($"managed-file:{actor.TenantId}:{actor.UserId}:{projectId}:{normalizedFileName}", ct);
            var managedObject = await Scope(dbContext.ManagedObjects, actor)
                .SingleOrDefaultAsync(x => x.Id == request.ManagedObjectId, ct)
                ?? throw new UnauthorizedAccessException("Managed object is not available.");
            if (managedObject.SecurityDomain != ManagedObjectSecurityDomain.ManagedFile || managedObject.State != ManagedObjectState.Ready || string.IsNullOrWhiteSpace(managedObject.PlaintextSha256))
            {
                throw new InvalidOperationException("Managed file content must be integrity verified and ready before logical file creation.");
            }

            var replay = await Scope(dbContext.FileAccessEvents.AsNoTracking(), actor)
                .Where(x => x.Operation == FileOperation.Metadata && x.ReasonCode == "Created:" + idempotencyKey)
                .OrderByDescending(x => x.CreatedAt)
                .FirstOrDefaultAsync(ct);
            if (replay?.FileVersionId is Guid replayVersionId)
            {
                var replayVersion = await dbContext.FileVersions.AsNoTracking().SingleAsync(x => x.Id == replayVersionId, ct);
                return Result(ManagedFileCreateOutcome.Created, replay.FileAssetId, replayVersion, [], true);
            }

            var visibleAssets = await Scope(dbContext.FileAssets, actor)
                .Include(x => x.Versions)
                .Where(x => x.State != FileAssetState.LogicalDeleted && x.ProjectId == projectId)
                .ToArrayAsync(ct);
            var sameName = visibleAssets.Where(x => x.NormalizedFileName == normalizedFileName).ToArray();
            var exact = sameName.SelectMany(x => x.Versions.Select(v => (Asset: x, Version: v)))
                .FirstOrDefault(x => x.Version.ContentSha256 == managedObject.PlaintextSha256 && x.Version.Lifecycle != FileVersionLifecycle.LogicalDeleted);
            if (exact.Version is not null)
            {
                if (managedObject.Id != exact.Version.ManagedObjectId)
                {
                    managedObject.State = ManagedObjectState.Orphaned;
                    managedObject.UpdatedAt = clock.UtcNow;
                    await dbContext.SaveChangesAsync(ct);
                }
                return Result(ManagedFileCreateOutcome.ExactDuplicate, exact.Asset.Id, exact.Version, [], true);
            }

            var sameHash = visibleAssets.SelectMany(x => x.Versions.Select(v => (Asset: x, Version: v)))
                .Where(x => x.Version.ContentSha256 == managedObject.PlaintextSha256 && x.Version.Lifecycle != FileVersionLifecycle.LogicalDeleted)
                .ToArray();
            if (sameName.Length > 0 && request.Intent == ManagedFileCreateIntent.Detect)
            {
                return new(ManagedFileCreateOutcome.PossibleExistingFile, null, null, null, null, null, sameName.Select(x => x.Id).Distinct().ToArray(), false);
            }
            if (sameHash.Length > 0 && !request.SemanticPurposeConfirmed && request.Intent is not ManagedFileCreateIntent.CreateNewVersion)
            {
                return new(ManagedFileCreateOutcome.RequiresSemanticPurposeDecision, null, null, null, null, null, sameHash.Select(x => x.Asset.Id).Distinct().ToArray(), true);
            }

            FileAsset asset;
            if (request.Intent == ManagedFileCreateIntent.CreateNewVersion)
            {
                if (!request.ExistingFileId.HasValue) throw new InvalidOperationException("CreateNewVersion requires ExistingFileId.");
                asset = visibleAssets.SingleOrDefault(x => x.Id == request.ExistingFileId.Value)
                    ?? throw new UnauthorizedAccessException("Existing file is not available.");
                if (!string.Equals(asset.NormalizedFileName, normalizedFileName, StringComparison.Ordinal)) throw new InvalidOperationException("A new version must retain the logical filename.");
            }
            else
            {
                asset = new FileAsset
                {
                    TenantId = actor.TenantId,
                    OwnerUserId = actor.UserId,
                    ProjectId = projectId,
                    LogicalFileName = fileName,
                    NormalizedFileName = normalizedFileName,
                    State = projectId is null ? FileAssetState.Unassigned : FileAssetState.Active,
                    CreatedByActorId = ActorId(actor),
                    GovernanceReminderAt = projectId is null ? clock.UtcNow.AddDays(7) : null,
                    CreatedAt = clock.UtcNow,
                    UpdatedAt = clock.UtcNow
                };
                await dbContext.FileAssets.AddAsync(asset, ct);
            }

            var versionNumber = asset.Versions.Count == 0
                ? await dbContext.FileVersions.CountAsync(x => x.FileAssetId == asset.Id, ct) + 1
                : asset.Versions.Max(x => x.VersionNumber) + 1;
            var dedupKey = Hash(string.Join('|', actor.TenantId, actor.UserId, projectId ?? "unassigned", normalizedFileName, managedObject.PlaintextSha256));
            var version = new FileVersion
            {
                FileAssetId = asset.Id,
                ManagedObjectId = sameHash.FirstOrDefault().Version?.ManagedObjectId ?? managedObject.Id,
                VersionNumber = versionNumber,
                ContentSha256 = managedObject.PlaintextSha256,
                ContentType = NormalizeContentType(request.ContentType),
                DeduplicationScopeKey = dedupKey,
                Lifecycle = FileVersionLifecycle.IntegrityVerified,
                Classification = FileClassification.Restricted,
                SearchProjectionAllowed = false,
                EmbeddingAllowed = false,
                NeedsRescan = true,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            if (sameHash.Length > 0 && managedObject.Id != version.ManagedObjectId)
            {
                managedObject.State = ManagedObjectState.Orphaned;
                managedObject.UpdatedAt = clock.UtcNow;
            }
            await dbContext.FileVersions.AddAsync(version, ct);
            await dbContext.FileAccessEvents.AddAsync(new FileAccessEvent
            {
                FileAssetId = asset.Id,
                FileVersionId = version.Id,
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId ?? string.Empty,
                ActorId = ActorId(actor),
                Operation = FileOperation.Metadata,
                Purpose = purpose,
                Allowed = true,
                ReasonCode = "Created:" + idempotencyKey,
                CreatedAt = clock.UtcNow
            }, ct);
            await dbContext.SaveChangesAsync(ct);
            return Result(ManagedFileCreateOutcome.Created, asset.Id, version, [], sameHash.Length > 0);
        }, cancellationToken);

    public Task<FileVersion> ApplySecurityAssessmentAsync(Guid fileVersionId, FileSecurityAssessment assessment, CancellationToken cancellationToken)
        => dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var actor = RequireActor(SecurityScopes.SecurityManage);
            var version = await LoadVersionAsync(fileVersionId, actor, ct);
            await dbContext.AcquireTransactionLockAsync($"file-security:{fileVersionId}", ct);
            var now = clock.UtcNow;
            var drafts = assessment.Findings.Select(ValidateFinding).ToArray();
            if (!assessment.ScannerAvailable)
            {
                drafts = [new("scanner-unavailable", SecurityFindingCategory.ScanFailure, SecurityFindingSeverity.High, 1m, assessment.ScannerSetVersion, Hash("scanner-unavailable"), "scanner unavailable")];
            }
            else if (!assessment.ParserSupported)
            {
                drafts = drafts.Append(new("parser-unsupported", SecurityFindingCategory.ScanFailure, SecurityFindingSeverity.High, 1m, assessment.ScannerSetVersion, Hash("parser-unsupported"), "parser unsupported")).ToArray();
            }

            var next = FileSecurityPolicy.Classify(drafts, assessment.ScannerAvailable, assessment.ParserSupported);
            var escalation = next > version.Classification;
            if (next < version.Classification && version.LastScanAt.HasValue)
            {
                throw new InvalidOperationException("Security classification cannot be downgraded automatically.");
            }
            foreach (var draft in drafts)
            {
                var exists = await dbContext.FileSecurityFindings.AnyAsync(x => x.FileVersionId == fileVersionId && x.ScannerRuleId == draft.ScannerRuleId && x.EvidenceHash == draft.EvidenceHash, ct);
                if (!exists) await dbContext.FileSecurityFindings.AddAsync(new FileSecurityFinding
                {
                    FileVersionId = fileVersionId,
                    ScannerRuleId = draft.ScannerRuleId,
                    Category = draft.Category,
                    Severity = draft.Severity,
                    Confidence = draft.Confidence,
                    ScannerVersion = draft.ScannerVersion,
                    DetectedAt = now,
                    Disposition = SecurityFindingDisposition.Confirmed,
                    EvidenceHash = draft.EvidenceHash,
                    RedactedEvidence = draft.RedactedEvidence
                }, ct);
            }
            version.Classification = next;
            version.ClassificationRevision++;
            version.LastScanAt = now;
            version.ScannerSetVersion = RequireText(assessment.ScannerSetVersion, nameof(assessment.ScannerSetVersion), 100);
            version.SecurityPolicyVersion = RequireText(assessment.SecurityPolicyVersion, nameof(assessment.SecurityPolicyVersion), 100);
            version.NeedsRescan = !assessment.ScannerAvailable || !assessment.ParserSupported;
            version.Lifecycle = next == FileClassification.Quarantined
                ? FileVersionLifecycle.Quarantined
                : version.NeedsRescan ? FileVersionLifecycle.NeedsRescan : FileVersionLifecycle.Ready;
            version.SearchProjectionAllowed = next is FileClassification.Normal or FileClassification.Sensitive;
            version.EmbeddingAllowed = next is FileClassification.Normal or FileClassification.Sensitive;
            version.UpdatedAt = now;
            if (escalation || !version.SearchProjectionAllowed) await InvalidateRepresentationsAsync(version, now, ct);
            await UpsertSearchProjectionAsync(version, now, ct);
            await dbContext.SaveChangesAsync(ct);
            return version;
        }, cancellationToken);

    public async Task<FileOperationDecision> AuthorizeOperationAsync(Guid fileVersionId, FileOperation operation, string purpose, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var version = await LoadVersionAsync(fileVersionId, actor, cancellationToken);
        var projectId = version.FileAsset!.ProjectId;
        var normalizedPurpose = purpose?.Trim() ?? string.Empty;
        var policy = FileSecurityPolicy.Authorize(version.Classification, operation, normalizedPurpose, actor.IsAdmin || actor.HasScope(SecurityScopes.SecurityManage));
        var allowed = policy.Allowed;
        var reason = policy.ReasonCode;
        if (allowed && projectId is not null && operation is not FileOperation.SecurityRescan and not FileOperation.SecurityRelease)
        {
            var principal = actor.UserId?.ToString("D") ?? actor.Username;
            var right = operation.ToString().ToLowerInvariant();
            var effective = await foundation.EvaluateAsync(projectId, principal, [right], "File", version.FileAssetId.ToString("D"), cancellationToken);
            allowed = effective.Decisions.Single().Allowed;
            if (!allowed) reason = "EffectiveRightDenied";
        }
        if (projectId is null && !actor.IsAdmin && version.FileAsset.OwnerUserId != actor.UserId)
        {
            allowed = false;
            reason = "UnassignedUploaderOrAdminOnly";
        }
        await RecordAccessAsync(version, actor, operation, normalizedPurpose, allowed, reason, cancellationToken);
        return new(allowed, reason, version.Classification, version.ClassificationRevision);
    }

    public async Task<FileRepresentation> AddRepresentationAsync(CreateFileRepresentationRequest request, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryWrite);
        var version = await LoadVersionAsync(request.FileVersionId, actor, cancellationToken);
        if (request.Classification < version.Classification) throw new InvalidOperationException("Derived representations cannot downgrade source classification.");
        var operation = request.Kind switch
        {
            FileRepresentationKind.Ocr => FileOperation.Ocr,
            FileRepresentationKind.SearchProjection => FileOperation.SearchIndex,
            _ => FileOperation.Extract
        };
        var decision = FileSecurityPolicy.Authorize(version.Classification, operation, "controlled-service", actor.IsAdmin || actor.HasScope(SecurityScopes.SecurityManage));
        if (!decision.Allowed) throw new UnauthorizedAccessException("The current classification forbids this derived representation.");
        var representation = new FileRepresentation
        {
            FileVersionId = version.Id,
            ManagedObjectId = request.ManagedObjectId,
            Kind = request.Kind,
            Classification = request.Classification,
            SourceClassificationRevision = version.ClassificationRevision,
            ContentHash = NormalizeHash(request.ContentHash),
            CreatedAt = clock.UtcNow
        };
        await dbContext.FileRepresentations.AddAsync(representation, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return representation;
    }

    public Task<QuarantineReleaseResult> RequestQuarantineReleaseAsync(QuarantineReleaseRequest request, CancellationToken cancellationToken)
        => dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var actor = RequireActor(SecurityScopes.SecurityManage);
            var version = await LoadVersionAsync(request.FileVersionId, actor, ct);
            if (version.Classification != FileClassification.Quarantined || version.Lifecycle != FileVersionLifecycle.Quarantined) throw new InvalidOperationException("Only quarantined files can enter release review.");
            if (version.ClassificationRevision != request.ExpectedClassificationRevision) throw new DbUpdateConcurrencyException("Classification revision conflict; reload before retrying.");
            var reason = RequireText(request.Reason, nameof(request.Reason), 1000);
            var approval = request.ExternalApprovalReference?.Trim() ?? string.Empty;
            var verified = approval.Length > 0 && await approvalVerifier.VerifyAsync(ActorId(actor), approval, "quarantine-release", ct);
            if (!verified)
            {
                await RecordAccessAsync(version, actor, FileOperation.SecurityRelease, reason, false, "RequiresExternalApproval", ct);
                return new QuarantineReleaseResult(false, true, version.ClassificationRevision, version.Lifecycle);
            }
            version.Lifecycle = FileVersionLifecycle.SecurityScanning;
            version.NeedsRescan = true;
            version.ClassificationRevision++;
            version.UpdatedAt = clock.UtcNow;
            await InvalidateRepresentationsAsync(version, clock.UtcNow, ct);
            await RecordAccessAsync(version, actor, FileOperation.SecurityRelease, reason, true, "ExternalApprovalVerifiedRescanRequired", ct);
            await dbContext.SaveChangesAsync(ct);
            return new QuarantineReleaseResult(true, false, version.ClassificationRevision, version.Lifecycle);
        }, cancellationToken);

    public Task<FileDeleteResult> RequestDeleteAsync(FileDeleteRequest request, CancellationToken cancellationToken)
        => dbContext.ExecuteInTransactionAsync<FileDeleteResult>(async ct =>
        {
            var actor = RequireActor(SecurityScopes.MemoryWrite);
            var asset = await Scope(dbContext.FileAssets, actor).Include(x => x.Versions).Include(x => x.Relations).SingleOrDefaultAsync(x => x.Id == request.FileId, ct)
                ?? throw new UnauthorizedAccessException("File is not available.");
            var requestId = RequireText(request.RequestId, nameof(request.RequestId), 200);
            await dbContext.AcquireTransactionLockAsync($"managed-file-delete:{asset.Id:D}", ct);
            var existing = await dbContext.FileDeletionRecords.Where(x => x.FileAssetId == asset.Id).OrderBy(x => x.CreatedAt).ToArrayAsync(ct);
            if (existing.Length > 0)
            {
                if (existing.Any(x => x.RequestId != requestId)) throw new InvalidOperationException("A governed deletion lifecycle already exists for this file.");
                var existingState = existing.Any(x => x.State == FileDeletionState.DeleteRequested)
                    ? FileDeletionState.DeleteRequested
                    : existing.OrderByDescending(x => x.State).First().State;
                return new(existingState == FileDeletionState.DeleteRequested, existingState, existing);
            }
            if (asset.Versions.Count == 0) throw new InvalidOperationException("File has no version.");
            if (asset.Versions.Any(x => x.Classification == FileClassification.Quarantined))
            {
                ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.SecurityManage);
            }
            var legalHold = asset.Relations.Any(x => x.Kind is FileRelationKind.LegalHold or FileRelationKind.SecurityHold);
            var referenceBlocked = asset.Relations.Any(x => x.Kind is FileRelationKind.Discussion or FileRelationKind.WorkItem);
            var blocked = legalHold || referenceBlocked;
            var now = clock.UtcNow;
            var records = asset.Versions.Select(x => x.ManagedObjectId).Distinct().Select(managedObjectId => new FileDeletionRecord
            {
                FileAssetId = asset.Id,
                ManagedObjectId = managedObjectId,
                State = FileDeletionState.DeleteRequested,
                LegalHold = legalHold,
                ReferenceBlocked = referenceBlocked,
                Reason = RequireText(request.Reason, nameof(request.Reason), 1000),
                RequestId = requestId,
                CreatedAt = now,
                UpdatedAt = now
            }).ToArray();
            await dbContext.FileDeletionRecords.AddRangeAsync(records, ct);
            if (!blocked)
            {
                asset.State = FileAssetState.LogicalDeleted;
                asset.DeletedAt = now;
                asset.UpdatedAt = now;
                asset.Revision++;
                foreach (var version in asset.Versions)
                {
                    version.Lifecycle = FileVersionLifecycle.LogicalDeleted;
                    version.SearchProjectionAllowed = false;
                    version.EmbeddingAllowed = false;
                    version.UpdatedAt = now;
                    await InvalidateRepresentationsAsync(version, now, ct);
                }
                foreach (var record in records) record.State = FileDeletionState.PhysicalDeletePending;
            }
            await dbContext.SaveChangesAsync(ct);
            return new(blocked, blocked ? FileDeletionState.DeleteRequested : FileDeletionState.PhysicalDeletePending, records);
        }, cancellationToken);

    public async Task<IReadOnlyList<ManagedFileSearchResult>> SearchAsync(string projectId, string query, int limit, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var normalizedProject = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, normalizedProject, write: false);
        var term = RequireText(query, nameof(query), 500);
        var normalizedTerm = term.ToLowerInvariant();
        var boundedLimit = Math.Clamp(limit, 1, 100);
        var candidates = await ScopeByAsset(dbContext.FileVersions.Include(x => x.FileAsset), actor)
            .Join(dbContext.FileSearchProjections, version => version.Id, projection => projection.FileVersionId, (version, projection) => new { version, projection })
            .Where(x => x.version.FileAsset!.ProjectId == normalizedProject && x.version.Lifecycle == FileVersionLifecycle.Ready &&
                        x.projection.ContentSearchEnabled && x.projection.InvalidatedAt == null &&
                        x.projection.ClassificationRevision == x.version.ClassificationRevision &&
                        x.version.Classification <= FileClassification.Sensitive &&
                        (x.version.FileAsset.LogicalFileName.ToLower().Contains(normalizedTerm) || x.projection.RedactedText.ToLower().Contains(normalizedTerm)))
            .OrderByDescending(x => x.version.UpdatedAt)
            .Take(boundedLimit * 3)
            .ToArrayAsync(cancellationToken);
        var results = new List<ManagedFileSearchResult>(boundedLimit);
        var principal = actor.UserId?.ToString("D") ?? actor.Username;
        foreach (var candidate in candidates)
        {
            var effective = await foundation.EvaluateAsync(normalizedProject, principal, ["searchindex"], "File", candidate.version.FileAssetId.ToString("D"), cancellationToken);
            if (!effective.Decisions.Single().Allowed) continue;
            results.Add(new(candidate.version.FileAssetId, candidate.version.Id, candidate.version.FileAsset!.LogicalFileName, normalizedProject, candidate.version.Classification,
                candidate.version.Classification == FileClassification.Sensitive ? string.Empty : candidate.projection.RedactedText));
            if (results.Count == boundedLimit) break;
        }
        return results;
    }

    public async Task<IReadOnlyList<ManagedFileInventoryResult>> ListAsync(string projectId, int limit, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var normalizedProject = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, normalizedProject, write: false);
        var boundedLimit = Math.Clamp(limit, 1, 100);
        var assets = await Scope(dbContext.FileAssets.AsNoTracking(), actor)
            .Include(x => x.Relations)
            .Include(x => x.Versions)
                .ThenInclude(x => x.Findings)
            .Where(x => x.ProjectId == normalizedProject && x.State != FileAssetState.LogicalDeleted)
            .OrderByDescending(x => x.UpdatedAt)
            .Take(boundedLimit * 3)
            .ToArrayAsync(cancellationToken);
        var principal = actor.UserId?.ToString("D") ?? actor.Username;
        var results = new List<ManagedFileInventoryResult>(boundedLimit);
        foreach (var asset in assets)
        {
            var version = asset.Versions
                .Where(x => x.Lifecycle != FileVersionLifecycle.LogicalDeleted)
                .OrderByDescending(x => x.VersionNumber)
                .FirstOrDefault();
            if (version is null) continue;
            var effective = await foundation.EvaluateAsync(normalizedProject, principal, ["metadata"], "File", asset.Id.ToString("D"), cancellationToken);
            if (!effective.Decisions.Single().Allowed) continue;
            var openFindings = version.Findings.Where(x => x.Disposition is SecurityFindingDisposition.Open or SecurityFindingDisposition.Confirmed).ToArray();
            results.Add(new ManagedFileInventoryResult(
                asset.Id,
                version.Id,
                asset.LogicalFileName,
                normalizedProject,
                asset.State,
                version.VersionNumber,
                version.Lifecycle,
                version.Classification,
                version.ClassificationRevision,
                version.NeedsRescan,
                version.LastScanAt,
                openFindings.Length,
                openFindings.Length == 0 ? null : openFindings.Max(x => x.Severity),
                asset.Relations.Count,
                version.UpdatedAt));
            if (results.Count == boundedLimit) break;
        }
        return results;
    }

    private async Task<FileVersion> LoadVersionAsync(Guid id, ContextHubRequestActor actor, CancellationToken cancellationToken)
    {
        var query = dbContext.FileVersions.Include(x => x.FileAsset).Include(x => x.Representations);
        var version = await ScopeByAsset(query, actor).SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
        return version ?? throw new UnauthorizedAccessException("File version is not available.");
    }

    private async Task InvalidateRepresentationsAsync(FileVersion version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var representations = version.Representations.Count > 0
            ? version.Representations.ToArray()
            : await dbContext.FileRepresentations.Where(x => x.FileVersionId == version.Id && x.InvalidatedAt == null).ToArrayAsync(cancellationToken);
        foreach (var representation in representations.Where(x => x.InvalidatedAt is null)) representation.InvalidatedAt = now;
        var projection = await dbContext.FileSearchProjections.SingleOrDefaultAsync(x => x.FileVersionId == version.Id, cancellationToken);
        if (projection is not null)
        {
            projection.ContentSearchEnabled = false;
            projection.EmbeddingEnabled = false;
            projection.RedactedText = string.Empty;
            projection.InvalidatedAt = now;
            projection.UpdatedAt = now;
        }
    }

    private async Task UpsertSearchProjectionAsync(FileVersion version, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var projection = await dbContext.FileSearchProjections.SingleOrDefaultAsync(x => x.FileVersionId == version.Id, cancellationToken);
        var isNew = projection is null;
        projection ??= new FileSearchProjection { FileVersionId = version.Id };
        projection.ProjectId = version.FileAsset?.ProjectId ?? string.Empty;
        projection.Classification = version.Classification;
        projection.ClassificationRevision = version.ClassificationRevision;
        projection.ContentSearchEnabled = version.SearchProjectionAllowed;
        projection.EmbeddingEnabled = version.EmbeddingAllowed;
        projection.RedactedText = string.Empty;
        projection.UpdatedAt = now;
        projection.InvalidatedAt = version.SearchProjectionAllowed ? null : now;
        if (isNew) await dbContext.FileSearchProjections.AddAsync(projection, cancellationToken);
    }

    private async Task RecordAccessAsync(FileVersion version, ContextHubRequestActor actor, FileOperation operation, string purpose, bool allowed, string reason, CancellationToken cancellationToken)
    {
        await dbContext.FileAccessEvents.AddAsync(new FileAccessEvent
        {
            FileAssetId = version.FileAssetId,
            FileVersionId = version.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = version.FileAsset?.ProjectId ?? string.Empty,
            ActorId = ActorId(actor),
            Operation = operation,
            Purpose = purpose.Length > 200 ? purpose[..200] : purpose,
            Allowed = allowed,
            ReasonCode = reason,
            CreatedAt = clock.UtcNow
        }, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private ContextHubRequestActor RequireActor(string scope)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, scope);
        return actor;
    }

    private static IQueryable<T> Scope<T>(IQueryable<T> query, ContextHubRequestActor actor) where T : class
        => !actor.HasUser ? query : actor.IsServiceActor || actor.IsAdmin
            ? query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId)
            : query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId && EF.Property<Guid?>(x, "OwnerUserId") == actor.UserId);

    private static IQueryable<FileVersion> ScopeByAsset(IQueryable<FileVersion> query, ContextHubRequestActor actor)
        => !actor.HasUser ? query : actor.IsServiceActor || actor.IsAdmin
            ? query.Where(x => x.FileAsset!.TenantId == actor.TenantId)
            : query.Where(x => x.FileAsset!.TenantId == actor.TenantId && x.FileAsset.OwnerUserId == actor.UserId);

    private static ManagedFileCreateResult Result(ManagedFileCreateOutcome outcome, Guid fileId, FileVersion version, IReadOnlyList<Guid> candidates, bool reused)
        => new(outcome, fileId, version.Id, version.VersionNumber, version.Classification, version.Lifecycle, candidates, reused);

    private static FileSecurityFindingDraft ValidateFinding(FileSecurityFindingDraft finding)
        => finding with
        {
            ScannerRuleId = RequireText(finding.ScannerRuleId, nameof(finding.ScannerRuleId), 200),
            ScannerVersion = RequireText(finding.ScannerVersion, nameof(finding.ScannerVersion), 100),
            EvidenceHash = NormalizeHash(finding.EvidenceHash),
            RedactedEvidence = finding.RedactedEvidence.Length > 512 ? finding.RedactedEvidence[..512] : finding.RedactedEvidence,
            Confidence = Math.Clamp(finding.Confidence, 0m, 1m)
        };

    private static string NormalizeFileName(string value)
        => value.Trim().Normalize(NormalizationForm.FormKC).ToLowerInvariant();

    private static string NormalizeContentType(string value)
    {
        var normalized = value?.Split(';', 2)[0].Trim().ToLowerInvariant() ?? string.Empty;
        return string.IsNullOrWhiteSpace(normalized) || normalized.Length > 200 ? "application/octet-stream" : normalized;
    }

    private static string NormalizeHash(string value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidOperationException("SHA-256 value is invalid.");
        return normalized;
    }

    private static string RequireText(string? value, string name, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength) throw new InvalidOperationException($"{name} is required and must not exceed {maxLength} characters.");
        return normalized;
    }

    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static string ActorId(ContextHubRequestActor actor) => actor.UserId?.ToString("D") ?? actor.Username;
}

public static class FileSecurityPolicy
{
    public static FileClassification Classify(IReadOnlyList<FileSecurityFindingDraft> findings, bool scannerAvailable = true, bool parserSupported = true)
    {
        if (!scannerAvailable) return FileClassification.Restricted;
        if (!parserSupported) return FileClassification.Restricted;
        if (findings.Any(x => x.Severity == SecurityFindingSeverity.Critical && x.Category is SecurityFindingCategory.Malware or SecurityFindingCategory.ArchiveBomb or SecurityFindingCategory.EmbeddedPayload or SecurityFindingCategory.ScanFailure)) return FileClassification.Quarantined;
        if (findings.Any(x => x.Category is SecurityFindingCategory.PrivateKey or SecurityFindingCategory.Token or SecurityFindingCategory.CredentialSecret)) return FileClassification.Restricted;
        if (findings.Any(x => x.Category == SecurityFindingCategory.Pii && x.Severity >= SecurityFindingSeverity.High)) return FileClassification.Restricted;
        if (findings.Any(x => x.Severity >= SecurityFindingSeverity.High && x.Category is SecurityFindingCategory.Executable or SecurityFindingCategory.ContentTypeMismatch or SecurityFindingCategory.PolicyViolation)) return FileClassification.Restricted;
        return findings.Count == 0 ? FileClassification.Normal : FileClassification.Sensitive;
    }

    public static FileOperationDecision Authorize(FileClassification classification, FileOperation operation, string purpose, bool securityAuthority)
    {
        if (classification == FileClassification.Quarantined)
        {
            var allowed = securityAuthority && operation is FileOperation.SecurityRescan or FileOperation.SecurityRelease or FileOperation.Delete;
            return new(allowed, allowed ? "SecurityAuthorityOnly" : "QuarantineDenied", classification, 0);
        }
        if (classification == FileClassification.Restricted)
        {
            if (operation is FileOperation.Embed or FileOperation.SearchIndex or FileOperation.AutoShare) return new(false, "RestrictedProjectionDenied", classification, 0);
            if (operation is FileOperation.Read or FileOperation.Download && string.IsNullOrWhiteSpace(purpose)) return new(false, "ExplicitPurposeRequired", classification, 0);
            if (operation is FileOperation.Extract or FileOperation.Ocr && !securityAuthority) return new(false, "ControlledServiceRequired", classification, 0);
            if (operation == FileOperation.CrossProjectShare && !securityAuthority) return new(false, "HighRiskApprovalRequired", classification, 0);
        }
        if (classification == FileClassification.Sensitive && operation is FileOperation.AutoShare) return new(false, "SensitiveAutoShareDisabled", classification, 0);
        return new(true, "ClassificationAllowed", classification, 0);
    }
}

public sealed class ManagedFileContentScanner
{
    private static readonly Regex EmailPattern = new(@"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100));

    public FileSecurityAssessment Scan(ReadOnlySpan<byte> content, string contentType, string scannerVersion, string policyVersion)
    {
        var bytes = content.ToArray();
        var text = Encoding.UTF8.GetString(bytes);
        var findings = new List<FileSecurityFindingDraft>();
        AddIf(text.Contains("EICAR-STANDARD-ANTIVIRUS-TEST-FILE", StringComparison.Ordinal), "malware-eicar", SecurityFindingCategory.Malware, SecurityFindingSeverity.Critical, findings, bytes, scannerVersion);
        AddIf(text.Contains("-----BEGIN PRIVATE KEY-----", StringComparison.Ordinal) || text.Contains("-----BEGIN OPENSSH PRIVATE KEY-----", StringComparison.Ordinal), "private-key", SecurityFindingCategory.PrivateKey, SecurityFindingSeverity.High, findings, bytes, scannerVersion);
        AddIf(text.Contains("password=", StringComparison.OrdinalIgnoreCase), "credential-password", SecurityFindingCategory.CredentialSecret, SecurityFindingSeverity.High, findings, bytes, scannerVersion);
        AddIf(text.Contains("token=", StringComparison.OrdinalIgnoreCase) || text.Contains("authorization: bearer", StringComparison.OrdinalIgnoreCase), "credential-token", SecurityFindingCategory.Token, SecurityFindingSeverity.High, findings, bytes, scannerVersion);
        var emails = EmailPattern.Matches(text).Count;
        if (emails > 0) Add("pii-email", SecurityFindingCategory.Pii, emails >= 5 ? SecurityFindingSeverity.High : SecurityFindingSeverity.Medium, findings, bytes, scannerVersion);
        AddIf(text.Contains("ARCHIVE_BOMB", StringComparison.OrdinalIgnoreCase) || text.Contains("ARCHIVE_DEPTH=10", StringComparison.OrdinalIgnoreCase), "archive-bomb", SecurityFindingCategory.ArchiveBomb, SecurityFindingSeverity.Critical, findings, bytes, scannerVersion);
        AddIf(text.Contains("EMBEDDED_ACTIVE_PAYLOAD", StringComparison.OrdinalIgnoreCase), "embedded-payload", SecurityFindingCategory.EmbeddedPayload, SecurityFindingSeverity.Critical, findings, bytes, scannerVersion);
        if (text.Contains("UNSIGNED_MACRO", StringComparison.OrdinalIgnoreCase)) Add("macro-unsigned", SecurityFindingCategory.Macro, SecurityFindingSeverity.High, findings, bytes, scannerVersion);
        else if (text.Contains("SIGNED_MACRO", StringComparison.OrdinalIgnoreCase)) Add("macro-signed", SecurityFindingCategory.Macro, SecurityFindingSeverity.Medium, findings, bytes, scannerVersion);
        if (bytes.Length >= 2 && bytes[0] == (byte)'M' && bytes[1] == (byte)'Z' && !contentType.Contains("executable", StringComparison.OrdinalIgnoreCase)) Add("content-type-mismatch", SecurityFindingCategory.ContentTypeMismatch, SecurityFindingSeverity.High, findings, bytes, scannerVersion);
        return new(true, true, scannerVersion, policyVersion, findings);
    }

    private static void AddIf(bool condition, string rule, SecurityFindingCategory category, SecurityFindingSeverity severity, List<FileSecurityFindingDraft> findings, byte[] content, string scannerVersion)
    {
        if (condition) Add(rule, category, severity, findings, content, scannerVersion);
    }

    private static void Add(string rule, SecurityFindingCategory category, SecurityFindingSeverity severity, List<FileSecurityFindingDraft> findings, byte[] content, string scannerVersion)
        => findings.Add(new(rule, category, severity, 1m, scannerVersion, Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant(), $"{category}:{severity}"));
}

public interface IManagedFileDeletionReconciler
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

public interface IManagedFileProjectionReconciler
{
    Task<int> RunAsync(CancellationToken cancellationToken);
}

public sealed class ManagedFileProjectionReconciler(IApplicationDbContext dbContext, IClock clock) : IManagedFileProjectionReconciler
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var stale = await dbContext.FileSearchProjections
            .Join(dbContext.FileVersions, projection => projection.FileVersionId, version => version.Id, (projection, version) => new { projection, version })
            .Where(x => x.projection.ClassificationRevision != x.version.ClassificationRevision ||
                        (x.version.Classification >= FileClassification.Restricted && (x.projection.ContentSearchEnabled || x.projection.EmbeddingEnabled)))
            .ToArrayAsync(cancellationToken);
        foreach (var row in stale)
        {
            row.projection.Classification = row.version.Classification;
            row.projection.ClassificationRevision = row.version.ClassificationRevision;
            row.projection.ContentSearchEnabled = row.version.SearchProjectionAllowed && row.version.Classification <= FileClassification.Sensitive;
            row.projection.EmbeddingEnabled = row.version.EmbeddingAllowed && row.version.Classification <= FileClassification.Sensitive;
            if (!row.projection.ContentSearchEnabled) row.projection.RedactedText = string.Empty;
            row.projection.InvalidatedAt = row.projection.ContentSearchEnabled ? null : clock.UtcNow;
            row.projection.UpdatedAt = clock.UtcNow;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return stale.Length;
    }
}

public sealed class ManagedFileDeletionReconciler(IApplicationDbContext dbContext, IManagedObjectStore objectStore, IClock clock) : IManagedFileDeletionReconciler
{
    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var pending = await dbContext.FileDeletionRecords.Where(x => x.State == FileDeletionState.PhysicalDeletePending).ToArrayAsync(cancellationToken);
        var completed = 0;
        foreach (var record in pending)
        {
            if (record.LegalHold || record.ReferenceBlocked || record.RetainUntil > now) continue;
            var hasLiveReference = await dbContext.FileVersions.AnyAsync(
                x => x.ManagedObjectId == record.ManagedObjectId && x.Lifecycle != FileVersionLifecycle.LogicalDeleted,
                cancellationToken);
            if (hasLiveReference) continue;
            var managedObject = await dbContext.ManagedObjects.SingleOrDefaultAsync(x => x.Id == record.ManagedObjectId, cancellationToken);
            if (managedObject is null) continue;
            if (managedObject.State == ManagedObjectState.Tombstoned)
            {
                record.State = FileDeletionState.PrimaryDeleted;
                record.PrimaryDeletedAt ??= managedObject.TombstonedAt ?? now;
                record.UpdatedAt = now;
                completed++;
                continue;
            }
            try
            {
                await objectStore.DeleteObjectAsync(managedObject.StorageId, cancellationToken);
                managedObject.State = ManagedObjectState.Tombstoned;
                managedObject.TombstonedAt = now;
                managedObject.UpdatedAt = now;
                record.AttemptCount++;
                record.State = FileDeletionState.PrimaryDeleted;
                record.PrimaryDeletedAt = now;
                record.UpdatedAt = now;
                completed++;
            }
            catch (IOException)
            {
                record.AttemptCount++;
                record.UpdatedAt = now;
            }
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return completed;
    }
}

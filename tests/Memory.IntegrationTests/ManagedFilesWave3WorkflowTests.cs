using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.IntegrationTests;

public sealed class ManagedFilesWave3WorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    private static ReadOnlySpan<byte> QuarantineFixtureContent => "inert quarantine review fixture"u8;

    [DockerRequiredFact]
    public async Task File_readiness_gates_content_operations_and_representations_preserving_metadata_and_audit()
    {
        var factory = environment.GetFactory();
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseBootstrapActor(services);
        var actors = services.GetRequiredService<IRequestActorAccessor>();
        var db = services.GetRequiredService<MemoryDbContext>();
        var files = services.GetRequiredService<IManagedFileService>();
        var foundation = services.GetRequiredService<IPlatformFoundationStore>();
        var root = Path.Combine(Path.GetTempPath(), "contexthub-file-readiness", Guid.NewGuid().ToString("N"));
        var project = $"file-readiness-{Guid.NewGuid():N}";
        var operations = new[]
        {
            FileOperation.Read, FileOperation.Download, FileOperation.Extract, FileOperation.Ocr,
            FileOperation.Embed, FileOperation.SearchIndex, FileOperation.AutoShare, FileOperation.CrossProjectShare
        };

        try
        {
            var transfer = CreateTransfer(services, db, root);
            var objectId = await UploadAsync(transfer, project, "synthetic readiness fixture"u8.ToArray(), "readiness");
            var created = await files.CreateAsync(new(objectId, "readiness.txt", "text/plain", "readiness", "file", project,
                ManagedFileCreateIntent.CreateNewFile), CancellationToken.None);
            var versionId = created.FileVersionId!.Value;
            var fileId = created.FileId!.Value;
            foreach (var operation in operations.Append(FileOperation.Metadata))
            {
                await foundation.UpsertExplicitGrantAsync(new(null, project, actor.UserId!.Value.ToString("D"),
                    operation.ToString().ToLowerInvariant(), AuthorizationEffect.Allow, "readiness-fixture", 0,
                    "File", fileId.ToString("D")), CancellationToken.None);
            }

            using var client = factory.CreateClient();
            var authorizePath = $"/api/files/{versionId:D}/authorize";
            var representationPath = $"/api/files/{versionId:D}/representations";
            var pending = await DecideAsync(FileOperation.Read, "pending");
            pending.Allowed.Should().BeFalse();
            pending.ReasonCode.Should().Be("SecurityScanRequired");
            (await DecideAsync(FileOperation.Embed, "pending")).ReasonCode.Should().Be("RestrictedProjectionDenied");
            (await DecideAsync(FileOperation.Metadata, "pending-metadata")).Allowed.Should().BeTrue();
            using var pendingWrite = await client.PostAsJsonAsync(representationPath,
                new { Kind = FileRepresentationKind.Preview, ContentHash = Hash("pending"), Classification = FileClassification.Restricted });
            pendingWrite.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            foreach (var unauthorized in new[]
                     {
                         actor with { AllowedProjectIds = [$"{project}-other"] },
                         actor with { Role = TenantUserRole.Member, UserId = Guid.NewGuid(), Scopes = [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite] },
                         actor with { TenantId = Guid.NewGuid() }
                     })
            {
                actors.Current = unauthorized;
                var unauthorizedDecision = () => files.AuthorizeOperationAsync(versionId, FileOperation.Metadata, "out-of-scope", CancellationToken.None);
                var unauthorizedWrite = () => files.AddRepresentationAsync(new(versionId, FileRepresentationKind.Preview,
                    Hash("out-of-scope"), FileClassification.Restricted), CancellationToken.None);
                await unauthorizedDecision.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("File version is not available.");
                await unauthorizedWrite.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("File version is not available.");
            }
            actors.Current = actor;

            var assessed = await files.ApplySecurityAssessmentAsync(versionId,
                new FileSecurityAssessment(true, true, "fixture-scanner", "fixture-policy", []), CancellationToken.None);
            assessed.Lifecycle.Should().Be(FileVersionLifecycle.Ready);
            foreach (var operation in operations)
                (await DecideAsync(operation, "fresh")).Allowed.Should().BeTrue();
            using var readyWrite = await client.PostAsJsonAsync(representationPath,
                new { Kind = FileRepresentationKind.Preview, ContentHash = Hash("fresh"), Classification = FileClassification.Normal });
            readyWrite.StatusCode.Should().Be(HttpStatusCode.OK);

            await db.FileVersions.Where(version => version.Id == versionId).ExecuteUpdateAsync(set => set.SetProperty(version => version.NeedsRescan, true));
            foreach (var operation in operations)
            {
                var stale = await DecideAsync(operation, "rescan-required");
                stale.Allowed.Should().BeFalse();
                stale.ReasonCode.Should().Be("SecurityScanRequired");
            }
            (await DecideAsync(FileOperation.Metadata, "stale-metadata")).Allowed.Should().BeTrue();
            // This service scope still tracks the formerly fresh source. A new derived write must read the current state.
            var staleWrite = () => files.AddRepresentationAsync(new(versionId, FileRepresentationKind.SearchProjection,
                Hash("stale"), FileClassification.Normal), CancellationToken.None);
            await staleWrite.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("File content requires a completed security scan.");

            var retained = await db.FileRepresentations.AsNoTracking().SingleAsync(row => row.FileVersionId == versionId);
            retained.SourceClassificationRevision.Should().Be(assessed.ClassificationRevision);
            retained.ContentHash.Should().Be(Hash("fresh"));
            var events = await db.FileAccessEvents.AsNoTracking().Where(row => row.FileVersionId == versionId).ToArrayAsync();
            events.Should().NotContain(row => row.Purpose == "out-of-scope");
            events.Should().ContainSingle(row => row.Purpose == "pending" && row.Operation == FileOperation.Read && !row.Allowed &&
                row.ReasonCode == "SecurityScanRequired");
            var blocked = events.Where(row => row.Purpose == "rescan-required").ToArray();
            blocked.Select(row => row.Operation).Should().BeEquivalentTo(operations);
            blocked.Should().OnlyContain(row => !row.Allowed && row.ReasonCode == "SecurityScanRequired");

            async Task<FileOperationDecision> DecideAsync(FileOperation operation, string purpose)
            {
                using var response = await client.PostAsJsonAsync(authorizePath, new { Operation = operation, Purpose = purpose });
                response.StatusCode.Should().Be(HttpStatusCode.OK);
                return (await response.Content.ReadFromJsonAsync<FileOperationDecision>())!;
            }
        }
        finally
        {
            actors.Current = actor;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [DockerRequiredFact]
    public async Task Approved_quarantine_release_requires_current_complete_covered_rescan_and_preserves_evidence()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseBootstrapActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var root = Path.Combine(Path.GetTempPath(), "contexthub-quarantine-release", Guid.NewGuid().ToString("N"));
        var project = "quarantine-release-" + Guid.NewGuid().ToString("N");
        try
        {
            var files = services.GetRequiredService<IManagedFileService>();
            var (versionId, quarantinedAt, dangerousReport) = await CreateQuarantinedFileAsync(services, db, root, project);
            var report = new ManagedFileContentScanner().Scan(QuarantineFixtureContent, "text/plain", "scanner-2", "policy-2");
            var priorFinding = await db.FileSecurityFindings.AsNoTracking().SingleAsync(x => x.FileVersionId == versionId);

            var withoutApproval = () => files.ApplySecurityAssessmentAsync(versionId, report, CancellationToken.None);
            await withoutApproval.Should().ThrowAsync<InvalidOperationException>().WithMessage("*cannot be downgraded*");
            // A legacy pending flag alone cannot grant authority.
            await db.FileVersions.Where(x => x.Id == versionId)
                .ExecuteUpdateAsync(update => update.SetProperty(x => x.NeedsRescan, true));
            db.ChangeTracker.Clear();
            var withoutApprovalEvent = () => files.ApplySecurityAssessmentAsync(versionId,
                report with { ExpectedClassificationRevision = quarantinedAt }, CancellationToken.None);
            await withoutApprovalEvent.Should().ThrowAsync<InvalidOperationException>().WithMessage("*requires recorded approval*");
            var approval = new ScopedQuarantineApproval(actor.UserId!.Value, versionId, quarantinedAt);
            var verifiedFiles = CreateFileService(services, db, approval);
            var denied = await verifiedFiles.RequestQuarantineReleaseAsync(new(versionId, "review", "wrong-approval", quarantinedAt), CancellationToken.None);
            denied.RequiresExternalApproval.Should().BeTrue();

            var accessor = services.GetRequiredService<IRequestActorAccessor>();
            accessor.Current = actor with { AllowedProjectIds = ["another-project"] };
            var wrongProject = () => verifiedFiles.RequestQuarantineReleaseAsync(new(versionId, "review", approval.Reference, quarantinedAt), CancellationToken.None);
            await wrongProject.Should().ThrowAsync<UnauthorizedAccessException>();
            accessor.Current = actor;

            var release = await verifiedFiles.RequestQuarantineReleaseAsync(new(versionId, "review", approval.Reference, quarantinedAt), CancellationToken.None);
            release.Should().Be(new QuarantineReleaseResult(true, false, quarantinedAt + 1, FileVersionLifecycle.Quarantined));
            var reviewRevision = release.ClassificationRevision;
            var pending = await db.FileVersions.AsNoTracking().SingleAsync(x => x.Id == versionId);
            pending.Classification.Should().Be(FileClassification.Quarantined);
            pending.NeedsRescan.Should().BeTrue();
            pending.SearchProjectionAllowed.Should().BeFalse();
            var missingRevision = () => files.ApplySecurityAssessmentAsync(versionId, report, CancellationToken.None);
            await missingRevision.Should().ThrowAsync<FileClassificationConflictException>();
            using var client = environment.GetFactory().CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MemoryApplicationFactory.TestBootstrapToken);
            var assessmentEndpoint = $"/api/files/{versionId:D}/security-assessments";
            using var staleAssessment = await client.PostAsJsonAsync(assessmentEndpoint,
                report with { ExpectedClassificationRevision = quarantinedAt });
            using var staleRelease = await client.PostAsJsonAsync($"/api/files/{versionId:D}/quarantine-release", new
            {
                reason = "retry",
                externalApprovalReference = approval.Reference,
                expectedClassificationRevision = quarantinedAt
            });
            foreach (var conflict in new[] { staleAssessment, staleRelease })
            {
                conflict.StatusCode.Should().Be(HttpStatusCode.Conflict);
                var problem = await conflict.Content.ReadFromJsonAsync<JsonElement>();
                problem.GetProperty("code").GetString().Should().Be("FileClassificationConflict");
                problem.GetRawText().Should().NotContain(versionId.ToString("D")).And.NotContain(approval.Reference);
            }

            var complete = report with
            {
                ExpectedClassificationRevision = reviewRevision,
                CompletedScannerRuleIds = dangerousReport.Findings.Select(x => x.ScannerRuleId).ToArray()
            };
            foreach (var incomplete in new[]
            {
                complete with { ScannerAvailable = false },
                complete with { ParserSupported = false },
                complete with { ScannerSetVersion = "scanner-1" },
                complete with { SecurityPolicyVersion = "policy-1" },
                complete with { CompletedScannerRuleIds = ["different-rule"] },
                complete with { CompletedScannerRuleIds = null }
            })
            {
                var held = await files.ApplySecurityAssessmentAsync(versionId, incomplete, CancellationToken.None);
                held.Classification.Should().Be(FileClassification.Quarantined);
                held.Lifecycle.Should().Be(FileVersionLifecycle.Quarantined);
                held.ClassificationRevision.Should().Be(reviewRevision);
                held.NeedsRescan.Should().BeTrue();
                held.SearchProjectionAllowed.Should().BeFalse();
            }
            (await db.FileVersions.AsNoTracking().SingleAsync(x => x.Id == versionId)).LastScanAt.Should().Be(pending.LastScanAt);
            (await db.FileSecurityFindings.AsNoTracking().SingleAsync(x => x.FileVersionId == versionId))
                .Disposition.Should().Be(SecurityFindingDisposition.Confirmed);

            using var clearedResponse = await client.PostAsJsonAsync(assessmentEndpoint, complete);
            clearedResponse.StatusCode.Should().Be(HttpStatusCode.OK);
            db.ChangeTracker.Clear();
            var cleared = await db.FileVersions.AsNoTracking().SingleAsync(x => x.Id == versionId);
            cleared.Classification.Should().Be(FileClassification.Normal);
            cleared.Lifecycle.Should().Be(FileVersionLifecycle.Ready);
            cleared.ClassificationRevision.Should().Be(reviewRevision + 1);
            cleared.NeedsRescan.Should().BeFalse();
            var historical = await db.FileSecurityFindings.AsNoTracking().SingleAsync(x => x.FileVersionId == versionId);
            historical.Should().BeEquivalentTo(priorFinding, options => options.Excluding(x => x.Disposition));
            historical.Disposition.Should().Be(SecurityFindingDisposition.Resolved);
            var projection = await db.FileSearchProjections.AsNoTracking().SingleAsync(x => x.FileVersionId == versionId);
            projection.ClassificationRevision.Should().Be(cleared.ClassificationRevision);
            projection.RedactedText.Should().BeEmpty();
            var events = await db.FileAccessEvents.AsNoTracking().Where(x => x.FileVersionId == versionId).ToArrayAsync();
            events.Should().ContainSingle(x => x.FileAssetId == cleared.FileAssetId && x.Operation == FileOperation.SecurityRelease && x.Allowed &&
                x.ReasonCode == $"ExternalApprovalVerifiedRescanRequired:{reviewRevision.ToString(CultureInfo.InvariantCulture)}");
            events.Should().ContainSingle(x => x.Operation == FileOperation.SecurityRescan && x.Allowed &&
                x.ReasonCode == "QuarantineClearedAfterVerifiedRescan");
            events.Should().Contain(x => x.Operation == FileOperation.SecurityRescan && !x.Allowed && x.ReasonCode == "ScannerRuleCoverageRequired");

            var repeated = await files.ApplySecurityAssessmentAsync(versionId, dangerousReport, CancellationToken.None);
            repeated.Classification.Should().Be(FileClassification.Quarantined);
            (await db.FileSecurityFindings.AsNoTracking().SingleAsync(x => x.FileVersionId == versionId))
                .Disposition.Should().Be(SecurityFindingDisposition.Confirmed);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [DockerRequiredFact]
    public async Task Approved_quarantine_rescan_retains_reproduced_high_findings_and_accepts_only_one_concurrent_completion()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var actor = UseBootstrapActor(services);
        var db = services.GetRequiredService<MemoryDbContext>();
        var root = Path.Combine(Path.GetTempPath(), "contexthub-quarantine-race", Guid.NewGuid().ToString("N"));
        var project = "quarantine-race-" + Guid.NewGuid().ToString("N");
        try
        {
            var (versionId, quarantinedAt, dangerousReport) = await CreateQuarantinedFileAsync(services, db, root, project);
            var approval = new ScopedQuarantineApproval(actor.UserId!.Value, versionId, quarantinedAt);
            var verifiedFiles = CreateFileService(services, db, approval);
            var release = await verifiedFiles.RequestQuarantineReleaseAsync(new(versionId, "review", approval.Reference, quarantinedAt), CancellationToken.None);
            var reproducedFinding = dangerousReport.Findings.Single();
            var rules = new[] { reproducedFinding.ScannerRuleId };
            var held = await verifiedFiles.ApplySecurityAssessmentAsync(versionId, dangerousReport with
            {
                Findings = [reproducedFinding with { Severity = SecurityFindingSeverity.Low }],
                ExpectedClassificationRevision = release.ClassificationRevision,
                CompletedScannerRuleIds = rules
            }, CancellationToken.None);
            held.Classification.Should().Be(FileClassification.Quarantined);
            held.Lifecycle.Should().Be(FileVersionLifecycle.Quarantined);
            var heldRevision = held.ClassificationRevision;
            var persistedFinding = await db.FileSecurityFindings.AsNoTracking().SingleAsync(x => x.FileVersionId == versionId);
            persistedFinding.Disposition.Should().Be(SecurityFindingDisposition.Confirmed);
            persistedFinding.Severity.Should().Be(reproducedFinding.Severity);
            var usedApproval = await verifiedFiles.RequestQuarantineReleaseAsync(new(versionId, "review", approval.Reference, heldRevision), CancellationToken.None);
            usedApproval.RequiresExternalApproval.Should().BeTrue();

            var newApproval = new ScopedQuarantineApproval(actor.UserId.Value, versionId, heldRevision);
            var newRelease = await CreateFileService(services, db, newApproval).RequestQuarantineReleaseAsync(
                new(versionId, "review", newApproval.Reference, heldRevision), CancellationToken.None);
            var clean = dangerousReport with
            {
                Findings = [],
                ExpectedClassificationRevision = newRelease.ClassificationRevision,
                CompletedScannerRuleIds = rules
            };
            async Task<bool> CompleteInFreshScopeAsync()
            {
                using var candidate = environment.GetFactory().Services.CreateScope();
                candidate.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
                try
                {
                    var result = await candidate.ServiceProvider.GetRequiredService<IManagedFileService>()
                        .ApplySecurityAssessmentAsync(versionId, clean, CancellationToken.None);
                    result.Classification.Should().Be(FileClassification.Normal);
                    return true;
                }
                catch (FileClassificationConflictException)
                {
                    return false;
                }
            }

            (await Task.WhenAll(CompleteInFreshScopeAsync(), CompleteInFreshScopeAsync())).Should().BeEquivalentTo(new[] { true, false });
            var events = await db.FileAccessEvents.AsNoTracking().Where(x => x.FileVersionId == versionId && x.Operation == FileOperation.SecurityRescan).ToArrayAsync();
            events.Should().ContainSingle(x => x.Allowed && x.ReasonCode == "QuarantineClearedAfterVerifiedRescan");
            events.Should().ContainSingle(x => !x.Allowed && x.ReasonCode == "RescanConfirmedHighFinding");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [DockerRequiredFact]
    public async Task Dedup_is_concurrency_safe_and_does_not_disclose_cross_owner_existence_or_unassigned_files()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var owner = UseBootstrapActor(scope.ServiceProvider);
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var root = Path.Combine(Path.GetTempPath(), "contexthub-wave3-dedup", Guid.NewGuid().ToString("N"));
        var transfer = CreateTransfer(scope.ServiceProvider, db, root);
        var projectId = "wave-3-dedup-" + Guid.NewGuid().ToString("N");
        var bytes = "same authorized content"u8.ToArray();
        try
        {
            var objectA = await UploadAsync(transfer, projectId, bytes, "race-a");
            var objectB = await UploadAsync(transfer, projectId, bytes, "race-b");
            async Task<ManagedFileCreateResult> CreateFromFreshScope(Guid objectId, string key)
            {
                using var child = environment.GetFactory().Services.CreateScope();
                child.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = owner;
                return await child.ServiceProvider.GetRequiredService<IManagedFileService>().CreateAsync(
                    new(objectId, "race.txt", "text/plain", "concurrency", key, projectId, ManagedFileCreateIntent.CreateNewFile), CancellationToken.None);
            }

            var concurrent = await Task.WhenAll(CreateFromFreshScope(objectA, "race-create-a"), CreateFromFreshScope(objectB, "race-create-b"));
            concurrent.Select(x => x.Outcome).Should().Contain(ManagedFileCreateOutcome.Created).And.Contain(ManagedFileCreateOutcome.ExactDuplicate);
            var fileId = concurrent.Single(x => x.Outcome == ManagedFileCreateOutcome.Created).FileId;
            (await db.FileVersions.CountAsync(x => x.FileAssetId == fileId)).Should().Be(1);

            var unassignedObject = await UploadAsync(transfer, projectId, "unassigned"u8.ToArray(), "unassigned-upload");
            var unassigned = await scope.ServiceProvider.GetRequiredService<IManagedFileService>().CreateAsync(
                new(unassignedObject, "private-draft.txt", "text/plain", "draft", "unassigned-create", Intent: ManagedFileCreateIntent.CreateNewFile), CancellationToken.None);
            (await db.FileAssets.SingleAsync(x => x.Id == unassigned.FileId)).State.Should().Be(FileAssetState.Unassigned);

            var secondUser = new TenantUser
            {
                TenantId = owner.TenantId!.Value,
                Username = "wave3-member-" + Guid.NewGuid().ToString("N"),
                DisplayName = "Wave 3 Member",
                Email = Guid.NewGuid().ToString("N") + "@example.test",
                PasswordHash = "not-used",
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            db.TenantUsers.Add(secondUser);
            await db.SaveChangesAsync();
            var member = new ContextHubRequestActor(
                secondUser.TenantId,
                secondUser.Id,
                secondUser.Username,
                secondUser.Role,
                [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite],
                [],
                IsAuthenticated: true);
            accessor.Current = member;
            var memberObject = await UploadAsync(transfer, projectId, bytes, "member-upload");
            var memberCreate = await scope.ServiceProvider.GetRequiredService<IManagedFileService>().CreateAsync(
                new(memberObject, "race.txt", "text/plain", "member", "member-create", projectId, ManagedFileCreateIntent.CreateNewFile), CancellationToken.None);
            memberCreate.Outcome.Should().Be(ManagedFileCreateOutcome.Created, "another owner's matching file and hash must be non-observable");
            memberCreate.CandidateFileIds.Should().BeEmpty();
            var readUnassigned = () => scope.ServiceProvider.GetRequiredService<IManagedFileService>().AuthorizeOperationAsync(unassigned.FileVersionId!.Value, FileOperation.Metadata, "inspect", CancellationToken.None);
            await readUnassigned.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*not available*");

            var memberUnassignedObject = await UploadAsync(transfer, projectId, "member unassigned"u8.ToArray(), "member-unassigned-upload");
            var memberUnassigned = await scope.ServiceProvider.GetRequiredService<IManagedFileService>().CreateAsync(
                new(memberUnassignedObject, "member-private-draft.txt", "text/plain", "member draft", "member-unassigned-create", Intent: ManagedFileCreateIntent.CreateNewFile), CancellationToken.None);

            accessor.Current = owner;
            var adminDecision = await scope.ServiceProvider.GetRequiredService<IManagedFileService>().AuthorizeOperationAsync(
                memberUnassigned.FileVersionId!.Value, FileOperation.Metadata, "admin-inspect", CancellationToken.None);
            adminDecision.Allowed.Should().BeTrue("tenant admins must be able to inspect another uploader's unassigned file");
        }
        finally
        {
            accessor.Current = owner;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [DockerRequiredFact]
    public async Task Managed_file_dedup_dlp_projection_release_and_deletion_paths_are_fail_closed_and_replay_safe()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var root = Path.Combine(Path.GetTempPath(), "contexthub-wave3-files", Guid.NewGuid().ToString("N"));
        var transfer = CreateTransfer(scope.ServiceProvider, db, root);
        var files = scope.ServiceProvider.GetRequiredService<IManagedFileService>();
        var projectId = "wave-3-files-" + Guid.NewGuid().ToString("N");
        var payload = "wave 3 benign fixture person@example.test"u8.ToArray();

        try
        {
            var firstObjectId = await UploadAsync(transfer, projectId, payload, "first");
            var unpinnedSession = await transfer.CreateDownloadAsync(
                new(projectId, firstObjectId, "review", "before-file-registration"), CancellationToken.None);
            var first = await files.CreateAsync(new(firstObjectId, "security.txt", "text/plain", "test", "create-1", projectId, ManagedFileCreateIntent.CreateNewFile), CancellationToken.None);
            first.Outcome.Should().Be(ManagedFileCreateOutcome.Created);
            first.Classification.Should().Be(FileClassification.Restricted, "new files must fail closed until scanned");
            var unscannedDownload = () => transfer.CreateDownloadAsync(new(projectId, firstObjectId, "review", "download-unscanned", FileVersionId: first.FileVersionId), CancellationToken.None);
            await unscannedDownload.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*successfully scanned*");

            var duplicateObjectId = await UploadAsync(transfer, projectId, payload, "duplicate");
            var duplicate = await files.CreateAsync(new(duplicateObjectId, "security.txt", "text/plain", "test", "create-2", projectId), CancellationToken.None);
            duplicate.Outcome.Should().Be(ManagedFileCreateOutcome.ExactDuplicate);
            duplicate.FileId.Should().Be(first.FileId);
            duplicate.FileVersionId.Should().Be(first.FileVersionId);
            (await db.ManagedObjects.SingleAsync(x => x.Id == duplicateObjectId)).State.Should().Be(ManagedObjectState.Orphaned);

            var changedObjectId = await UploadAsync(transfer, projectId, "different content"u8.ToArray(), "changed");
            var possible = await files.CreateAsync(new(changedObjectId, "security.txt", "text/plain", "test", "create-3", projectId), CancellationToken.None);
            possible.Outcome.Should().Be(ManagedFileCreateOutcome.PossibleExistingFile);
            var version2 = await files.CreateAsync(new(changedObjectId, "security.txt", "text/plain", "test", "create-4", projectId, ManagedFileCreateIntent.CreateNewVersion, first.FileId), CancellationToken.None);
            version2.VersionNumber.Should().Be(2);

            var aliasObjectId = await UploadAsync(transfer, projectId, payload, "alias");
            var semanticDecision = await files.CreateAsync(new(aliasObjectId, "alternate-name.txt", "text/plain", "test", "create-5", projectId), CancellationToken.None);
            semanticDecision.Outcome.Should().Be(ManagedFileCreateOutcome.RequiresSemanticPurposeDecision);
            var alias = await files.CreateAsync(new(aliasObjectId, "alternate-name.txt", "text/plain", "test", "create-6", projectId, ManagedFileCreateIntent.CreateSeparateLogicalFile, SemanticPurposeConfirmed: true), CancellationToken.None);
            alias.BinaryReused.Should().BeTrue();
            (await db.FileVersions.SingleAsync(x => x.Id == alias.FileVersionId)).ManagedObjectId.Should().Be(firstObjectId);

            var scanner = new ManagedFileContentScanner();
            var sensitive = scanner.Scan(payload, "text/plain", "scanner-1", "policy-1");
            var assessed = await files.ApplySecurityAssessmentAsync(first.FileVersionId!.Value, sensitive, CancellationToken.None);
            assessed.Classification.Should().Be(FileClassification.Sensitive);
            assessed.Lifecycle.Should().Be(FileVersionLifecycle.Ready);

            var foundation = scope.ServiceProvider.GetRequiredService<IPlatformFoundationStore>();
            var ruleRequest = new AuthorizationRuleUpsertRequest(Guid.NewGuid(), projectId, actor.UserId!.Value.ToString("D"),
                "download", AuthorizationEffect.Allow, "integration-test", 0, "File", first.FileId!.Value.ToString("D"));
            await foundation.UpsertExplicitGrantAsync(ruleRequest, CancellationToken.None);
            var pinnedSession = await transfer.CreateDownloadAsync(new(projectId, firstObjectId, "review",
                "before-policy-change", FileVersionId: first.FileVersionId), CancellationToken.None);
            await foundation.UpsertExplicitGrantAsync(ruleRequest with
            {
                Effect = AuthorizationEffect.Deny,
                ExpectedRevision = 1
            }, CancellationToken.None);
            await using var deniedContent = new MemoryStream();
            var revokedGrant = () => transfer.CopyRangeAsync(new(pinnedSession.SessionId, pinnedSession.Capability,
                pinnedSession.Revision, "revoked-grant", 0, payload.Length), deniedContent, CancellationToken.None);
            await revokedGrant.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*effective rights*");
            deniedContent.Length.Should().Be(0);
            await foundation.UpsertExplicitGrantAsync(ruleRequest with
            {
                ExpectedRevision = 2
            }, CancellationToken.None);
            var unpinnedRead = () => transfer.CopyRangeAsync(new(unpinnedSession.SessionId, unpinnedSession.Capability,
                unpinnedSession.Revision, "no-longer-unlinked", 0, payload.Length), deniedContent, CancellationToken.None);
            await unpinnedRead.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*FileVersion*");
            deniedContent.Length.Should().Be(0);

            var olderPin = await transfer.CreateDownloadAsync(new(projectId, firstObjectId, "review",
                "still-authorized-old-version", FileVersionId: first.FileVersionId), CancellationToken.None);
            await using var allowedContent = new MemoryStream();
            await transfer.CopyRangeAsync(new(olderPin.SessionId, olderPin.Capability, olderPin.Revision,
                "older-version-range", 0, payload.Length), allowedContent, CancellationToken.None);
            allowedContent.ToArray().Should().Equal(payload);

            await files.ApplySecurityAssessmentAsync(alias.FileVersionId!.Value, sensitive, CancellationToken.None);
            var representation = await files.AddRepresentationAsync(new(first.FileVersionId.Value, FileRepresentationKind.SearchProjection, Hash("projection"), FileClassification.Sensitive), CancellationToken.None);
            representation.InvalidatedAt.Should().BeNull();
            var quarantined = await files.ApplySecurityAssessmentAsync(first.FileVersionId.Value, scanner.Scan("EICAR-STANDARD-ANTIVIRUS-TEST-FILE"u8, "text/plain", "scanner-2", "policy-2"), CancellationToken.None);
            quarantined.Classification.Should().Be(FileClassification.Quarantined);
            quarantined.SearchProjectionAllowed.Should().BeFalse();
            using var rangeScope = environment.GetFactory().Services.CreateScope();
            UseBootstrapActor(rangeScope.ServiceProvider);
            var rangeDb = rangeScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var rangeTransfer = CreateTransfer(rangeScope.ServiceProvider, rangeDb, root);
            var quarantinedRead = () => rangeTransfer.CopyRangeAsync(new(pinnedSession.SessionId, pinnedSession.Capability,
                pinnedSession.Revision, "after-quarantine", 0, payload.Length), deniedContent, CancellationToken.None);
            await quarantinedRead.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*successfully scanned*");
            deniedContent.Length.Should().Be(0);
            var unchanged = await rangeDb.ManagedTransferSessions.AsNoTracking()
                .SingleAsync(session => session.Id == pinnedSession.SessionId);
            unchanged.Revision.Should().Be(pinnedSession.Revision);
            unchanged.UsedBytes.Should().Be(0);
            (await rangeDb.ManagedTransferOperationRecords.CountAsync(row => row.SessionId == pinnedSession.SessionId)).Should().Be(1);
            (await db.FileRepresentations.SingleAsync(x => x.Id == representation.Id)).InvalidatedAt.Should().NotBeNull();
            var projection = await db.FileSearchProjections.SingleAsync(x => x.FileVersionId == first.FileVersionId);
            projection.ContentSearchEnabled.Should().BeFalse();
            projection.EmbeddingEnabled.Should().BeFalse();
            projection.RedactedText.Should().BeEmpty();
            var missingLogicalRef = () => transfer.CreateDownloadAsync(new(projectId, firstObjectId, "review", "download-no-file-ref"), CancellationToken.None);
            await missingLogicalRef.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*FileVersion*");
            var quarantinedDownload = () => transfer.CreateDownloadAsync(new(projectId, firstObjectId, "review", "download-quarantined", FileVersionId: first.FileVersionId), CancellationToken.None);
            await quarantinedDownload.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*successfully scanned*");

            var release = await files.RequestQuarantineReleaseAsync(new(first.FileVersionId.Value, "false-positive review", "unverified-approval", quarantined.ClassificationRevision), CancellationToken.None);
            release.RequiresExternalApproval.Should().BeTrue();
            release.StartedRescan.Should().BeFalse();
            (await db.FileVersions.SingleAsync(x => x.Id == first.FileVersionId)).Lifecycle.Should().Be(FileVersionLifecycle.Quarantined);

            actorAccessor.Current = actor with { Role = TenantUserRole.Member, Scopes = [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite] };
            var unauthorizedDelete = () => files.RequestDeleteAsync(new(first.FileId!.Value, "unauthorized quarantine delete", "delete-without-security"), CancellationToken.None);
            await unauthorizedDelete.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*security:manage*");
            (await db.FileDeletionRecords.AnyAsync(x => x.FileAssetId == first.FileId)).Should().BeFalse();
            actorAccessor.Current = actor;

            db.FileRelations.Add(new FileRelation { FileAssetId = first.FileId!.Value, Kind = FileRelationKind.LegalHold, TargetProjectId = projectId, TargetId = "hold-1", Purpose = "retention", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            var blockedDelete = await files.RequestDeleteAsync(new(first.FileId.Value, "retention", "delete-1"), CancellationToken.None);
            blockedDelete.State.Should().Be(FileDeletionState.DeleteRequested);
            blockedDelete.RequiresUserDecision.Should().BeTrue();
            var replay = await files.RequestDeleteAsync(new(first.FileId.Value, "retention", "delete-1"), CancellationToken.None);
            replay.Records.Select(x => x.Id).Should().BeEquivalentTo(blockedDelete.Records.Select(x => x.Id));
            (await db.FileAssets.SingleAsync(x => x.Id == first.FileId)).State.Should().Be(FileAssetState.Active);

            actor.Username.Should().NotContain("ChatGPT").And.NotContain("Codex").And.NotContain("Gemini");
        }
        finally
        {
            actorAccessor.Current = actor;
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [DockerRequiredFact]
    public async Task Canonical_tag_self_merge_is_rejected_without_changing_bindings_or_audit()
    {
        var factory = environment.GetFactory();
        var projectId = $"canonical-tag-invariant-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var tag = new CanonicalTagDefinition
        {
            ProjectId = projectId,
            CanonicalName = "Synthetic tag",
            NormalizedName = "synthetic-tag",
            CreatedAt = now,
            UpdatedAt = now
        };
        using (var setup = factory.Services.CreateScope())
        {
            var actor = UseBootstrapActor(setup.ServiceProvider);
            var db = setup.ServiceProvider.GetRequiredService<MemoryDbContext>();
            tag.TenantId = actor.TenantId;
            tag.OwnerUserId = actor.UserId;
            db.CanonicalTagDefinitions.Add(tag);
            await db.SaveChangesAsync();
            db.CanonicalTagBindings.Add(new CanonicalTagBinding
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                DefinitionId = tag.Id,
                ResourceType = "File",
                ResourceId = "synthetic-file",
                Source = "Manual",
                Confidence = 1m,
                Status = "Active",
                CreatedAt = now
            });
            await db.SaveChangesAsync();
        }

        using (var operation = factory.Services.CreateScope())
        {
            UseBootstrapActor(operation.ServiceProvider);
            var service = operation.ServiceProvider.GetRequiredService<ICanonicalTagGovernanceService>();
            var preview = () => service.PreviewMergeAsync(projectId, tag.Id, tag.Id, CancellationToken.None);
            var apply = () => service.ApplyMergeAsync(projectId, tag.Id, tag.Id, tag.Revision, CancellationToken.None);
            await preview.Should().ThrowAsync<InvalidOperationException>().WithMessage("A tag cannot merge into itself.");
            await apply.Should().ThrowAsync<InvalidOperationException>().WithMessage("A tag cannot merge into itself.");
        }

        using var verification = factory.Services.CreateScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var definition = await verificationDb.CanonicalTagDefinitions.AsNoTracking().SingleAsync(item => item.Id == tag.Id);
        definition.Status.Should().Be(CanonicalTagStatus.Active);
        definition.Revision.Should().Be(tag.Revision);
        definition.RedirectToId.Should().BeNull();
        var binding = await verificationDb.CanonicalTagBindings.AsNoTracking().SingleAsync(item => item.DefinitionId == tag.Id);
        binding.Status.Should().Be("Active");
        binding.LastValidatedAt.Should().BeNull();
        (await verificationDb.CanonicalTagAliases.AnyAsync(item => item.DefinitionId == tag.Id)).Should().BeFalse();
        (await verificationDb.CanonicalTagGovernanceProposals.AnyAsync(item => item.SourceDefinitionId == tag.Id)).Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Canonical_tag_merge_preserves_definition_owner_when_run_by_a_tenant_administrator()
    {
        var factory = environment.GetFactory();
        var projectId = $"canonical-tag-merge-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        Guid actorId;
        Guid ownerId;
        CanonicalTagDefinition source;
        CanonicalTagDefinition target;
        CanonicalTagMergePreview preview;
        using (var setup = factory.Services.CreateScope())
        {
            var administrator = UseBootstrapActor(setup.ServiceProvider);
            administrator.IsAdmin.Should().BeTrue();
            var db = setup.ServiceProvider.GetRequiredService<MemoryDbContext>();
            actorId = administrator.UserId!.Value;
            var owner = new TenantUser
            {
                TenantId = administrator.TenantId!.Value,
                Username = $"tag-owner-{Guid.NewGuid():N}",
                DisplayName = "Synthetic Tag Owner",
                Role = TenantUserRole.Member,
                Status = TenantUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            ownerId = owner.Id;
            db.TenantUsers.Add(owner);
            await db.SaveChangesAsync();

            source = Definition("Old Tag", "old-tag");
            target = Definition("New Tag", "new-tag");
            db.CanonicalTagDefinitions.AddRange(source, target);
            await db.SaveChangesAsync();

            db.CanonicalTagBindings.AddRange(
                Binding(source.Id, ownerId, "owned-resource"),
                Binding(source.Id, actorId, "legacy-resource"),
                Binding(target.Id, actorId, "owned-resource"));
            await db.SaveChangesAsync();

            CanonicalTagDefinition Definition(string name, string normalizedName) => new()
            {
                TenantId = owner.TenantId,
                OwnerUserId = ownerId,
                ProjectId = projectId,
                CanonicalName = name,
                NormalizedName = normalizedName,
                CreatedAt = now,
                UpdatedAt = now
            };
            CanonicalTagBinding Binding(Guid definitionId, Guid bindingOwnerId, string resourceId) => new()
            {
                TenantId = owner.TenantId,
                OwnerUserId = bindingOwnerId,
                DefinitionId = definitionId,
                ProjectId = projectId,
                ResourceType = "Skill",
                ResourceId = resourceId,
                Status = "Active",
                CreatedAt = now
            };
        }

        using (var operation = factory.Services.CreateScope())
        {
            UseBootstrapActor(operation.ServiceProvider);
            var service = operation.ServiceProvider.GetRequiredService<ICanonicalTagGovernanceService>();
            preview = await service.PreviewMergeAsync(projectId, source.Id, target.Id, CancellationToken.None);
            preview.AffectedBindings.Should().Be(1);
            preview.ConflictingBindings.Should().Be(0);
            preview.SampleResourceIds.Should().Equal("Skill:owned-resource");
            await service.ApplyMergeAsync(projectId, source.Id, target.Id, source.Revision, CancellationToken.None);
        }

        using var verification = factory.Services.CreateScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var redirect = await verificationDb.CanonicalTagDefinitions.AsNoTracking().SingleAsync(item => item.Id == source.Id);
        redirect.Status.Should().Be(CanonicalTagStatus.Superseded);
        redirect.RedirectToId.Should().Be(target.Id);
        var alias = await verificationDb.CanonicalTagAliases.AsNoTracking().SingleAsync(item => item.DefinitionId == target.Id);
        alias.OwnerUserId.Should().Be(ownerId);
        alias.TenantId.Should().Be(target.TenantId);
        alias.NormalizedAlias.Should().Be(source.NormalizedName);

        var bindings = await verificationDb.CanonicalTagBindings.AsNoTracking()
            .Where(item => item.DefinitionId == source.Id || item.DefinitionId == target.Id).ToArrayAsync();
        var migrated = bindings.Should().ContainSingle(item => item.DefinitionId == target.Id && item.OwnerUserId == ownerId).Subject;
        migrated.Status.Should().Be("Active");
        migrated.TenantId.Should().Be(target.TenantId);
        migrated.ResourceId.Should().Be("owned-resource");
        bindings.Should().ContainSingle(item => item.DefinitionId == source.Id && item.OwnerUserId == ownerId && item.Status == "Superseded");
        bindings.Where(item => item.OwnerUserId == actorId).Should().HaveCount(2).And.OnlyContain(item => item.Status == "Active");

        var proposal = await verificationDb.CanonicalTagGovernanceProposals.AsNoTracking()
            .SingleAsync(item => item.SourceDefinitionId == source.Id);
        proposal.OwnerUserId.Should().Be(actorId);
        proposal.Status.Should().Be(CanonicalTagGovernanceProposalStatus.Applied);
        proposal.AffectedBindingCount.Should().Be(preview.AffectedBindings);
    }

    [DockerRequiredFact]
    public async Task Canonical_tag_telemetry_aggregates_quality_merge_split_and_query_redaction_are_governed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<ICanonicalTagGovernanceService>();
        var projectId = "wave-3-tags-" + Guid.NewGuid().ToString("N");
        var now = DateTimeOffset.UtcNow;
        var source = new CanonicalTagDefinition { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ProjectId = projectId, CanonicalName = "Database", NormalizedName = "database", Description = "", CreatedAt = now, UpdatedAt = now };
        var target = new CanonicalTagDefinition { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ProjectId = projectId, CanonicalName = "PostgreSQL", NormalizedName = "postgresql", Description = "", CreatedAt = now, UpdatedAt = now };
        db.CanonicalTagDefinitions.AddRange(source, target);
        for (var index = 0; index < 2100; index++)
        {
            db.CanonicalTagDefinitions.Add(new CanonicalTagDefinition { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ProjectId = projectId, CanonicalName = "scale-" + index, NormalizedName = "scale-" + index, Description = "", CreatedAt = now, UpdatedAt = now });
        }
        await db.SaveChangesAsync();
        db.CanonicalTagBindings.Add(new CanonicalTagBinding { TenantId = actor.TenantId, OwnerUserId = actor.UserId, DefinitionId = source.Id, ProjectId = projectId, ResourceType = "File", ResourceId = "file-1", Source = "Manual", Confidence = 1m, Status = "Active", CreatedAt = now });
        await db.SaveChangesAsync();

        const string sensitiveQuery = "customer secret query must not persist";
        for (var index = 0; index < 25; index++)
        {
            await service.RecordAsync(new(source.Id, projectId, CanonicalTagTelemetryKind.SearchImpression, CanonicalTagReasonCodes.SearchCandidateShown, sensitiveQuery, "File", "Agent"), CancellationToken.None);
        }
        await service.RecordAsync(new(source.Id, projectId, CanonicalTagTelemetryKind.Selection, CanonicalTagReasonCodes.ResultSelected, sensitiveQuery, "File", "Agent"), CancellationToken.None);
        await service.RecordAsync(new(source.Id, projectId, CanonicalTagTelemetryKind.Rejection, CanonicalTagReasonCodes.UserRejected, sensitiveQuery, "File", "Agent"), CancellationToken.None);
        await service.RecordAsync(new(source.Id, projectId, CanonicalTagTelemetryKind.Mismatch, CanonicalTagReasonCodes.IntentMismatch, sensitiveQuery, "File", "Agent"), CancellationToken.None);
        await service.ReconcileAsync(projectId, DateOnly.FromDateTime(DateTime.UtcNow), CancellationToken.None);

        var telemetry = await db.CanonicalTagTelemetryEvents.Where(x => x.DefinitionId == source.Id).ToArrayAsync();
        telemetry.Should().OnlyContain(x => x.QueryHash.Length == 64);
        JsonSerializer.Serialize(telemetry).Should().NotContain(sensitiveQuery);
        var quality = (await service.GetQualityAsync(projectId, 7, 20, CancellationToken.None)).Single(x => x.DefinitionId == source.Id);
        quality.MeetsGovernanceThreshold.Should().BeTrue();
        quality.Signals.Should().Contain(CanonicalTagReasonCodes.HighImpressionLowSelection);

        var preview = await service.PreviewMergeAsync(projectId, source.Id, target.Id, CancellationToken.None);
        preview.AffectedBindings.Should().Be(1);
        await service.ApplyMergeAsync(projectId, source.Id, target.Id, source.Revision, CancellationToken.None);
        db.ChangeTracker.Clear();
        var redirected = await db.CanonicalTagDefinitions.SingleAsync(x => x.Id == source.Id);
        redirected.Status.Should().Be(CanonicalTagStatus.Superseded);
        redirected.RedirectToId.Should().Be(target.Id);
        (await db.CanonicalTagBindings.SingleAsync(x => x.DefinitionId == target.Id && x.ResourceId == "file-1")).Status.Should().Be("Active");

        var split = await service.PreviewSplitAsync(projectId, target.Id, ["file-1"], CancellationToken.None);
        split.CandidateResourceIds.Should().Equal("file-1");
        (await db.CanonicalTagGovernanceProposals.SingleAsync(x => x.Kind == CanonicalTagGovernanceProposalKind.Split && x.SourceDefinitionId == target.Id)).Status.Should().Be(CanonicalTagGovernanceProposalStatus.Pending);
    }

    private static async Task<Guid> UploadAsync(IManagedTransferService transfer, string projectId, byte[] content, string key)
    {
        var upload = await transfer.CreateUploadAsync(new(projectId, content.Length, "wave-3-test", key), CancellationToken.None);
        var result = await transfer.UploadChunkAsync(new(upload.SessionId, upload.Capability, upload.Revision, key + "-chunk", 0, content.ToArray(), Hash(content)), CancellationToken.None);
        await transfer.CompleteUploadAsync(upload.SessionId, upload.Capability, result.Revision, key + "-complete", CancellationToken.None);
        return upload.Object.ObjectId;
    }

    private static string Hash(string value) => Hash(System.Text.Encoding.UTF8.GetBytes(value));
    private static string Hash(byte[] value) => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static IManagedTransferService CreateTransfer(IServiceProvider services, MemoryDbContext db, string root)
    {
        var keys = new ManagedFileKeyAuthorityOptions { CurrentKeyId = "wave3-kek" };
        keys.Keys["wave3-kek"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return new ManagedTransferService(
            db,
            services.GetRequiredService<IRequestActorAccessor>(),
            services.GetRequiredService<IClock>(),
            new FileSystemManagedObjectStore(Options.Create(new ManagedObjectStorageOptions { Enabled = true, RootPath = root })),
            new AesGcmManagedFileKeyAuthority(Options.Create(keys)),
            services.GetRequiredService<IPlatformFoundationStore>(),
            Options.Create(new ManagedTransferOptions { ChunkBytes = 64 * 1024 }));
    }

    private static IManagedFileService CreateFileService(IServiceProvider services, MemoryDbContext db, IHighAssuranceApprovalVerifier approval)
        => new ManagedFileService(db, services.GetRequiredService<IRequestActorAccessor>(), services.GetRequiredService<IClock>(),
            services.GetRequiredService<IPlatformFoundationStore>(), approval);

    private static async Task<(Guid VersionId, long Revision, FileSecurityAssessment Report)> CreateQuarantinedFileAsync(
        IServiceProvider services, MemoryDbContext db, string root, string project)
    {
        var payload = QuarantineFixtureContent.ToArray();
        var objectId = await UploadAsync(CreateTransfer(services, db, root), project, payload, project + "-upload");
        var service = services.GetRequiredService<IManagedFileService>();
        var file = await service.CreateAsync(new(objectId, "quarantine-fixture.txt", "text/plain", "test", project + "-create", project),
            CancellationToken.None);
        var falsePositive = new ManagedFileContentScanner().Scan("EICAR-STANDARD-ANTIVIRUS-TEST-FILE"u8, "text/plain", "scanner-2", "policy-2");
        var report = falsePositive with { Findings = [falsePositive.Findings.Single() with { EvidenceHash = Hash(payload) }] };
        var quarantined = await service.ApplySecurityAssessmentAsync(file.FileVersionId!.Value, report, CancellationToken.None);
        quarantined.Classification.Should().Be(FileClassification.Quarantined);
        quarantined.FileAsset!.ProjectId.Should().Be(project);
        return (quarantined.Id, quarantined.ClassificationRevision, report);
    }

    private sealed class ScopedQuarantineApproval(Guid actorId, Guid fileVersionId, long expectedRevision) : IHighAssuranceApprovalVerifier
    {
        public string Reference { get; } = "test-external-approval";

        public Task<bool> VerifyAsync(string callerActorId, string approvalReference, string purpose, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var expectedPurpose = $"quarantine-release:{fileVersionId:D}:{expectedRevision.ToString(CultureInfo.InvariantCulture)}";
            return Task.FromResult(callerActorId == actorId.ToString("D") && approvalReference == Reference && purpose == expectedPurpose);
        }
    }

    private static ContextHubRequestActor UseBootstrapActor(IServiceProvider services)
    {
        var dbContext = services.GetRequiredService<MemoryDbContext>();
        var user = dbContext.TenantUsers.Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SecurityManage],
            [],
            IsAuthenticated: true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }
}

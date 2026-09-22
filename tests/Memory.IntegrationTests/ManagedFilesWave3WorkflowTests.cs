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

            var representation = await files.AddRepresentationAsync(new(first.FileVersionId.Value, FileRepresentationKind.SearchProjection, Hash("projection"), FileClassification.Sensitive), CancellationToken.None);
            representation.InvalidatedAt.Should().BeNull();
            var quarantined = await files.ApplySecurityAssessmentAsync(first.FileVersionId.Value, scanner.Scan("EICAR-STANDARD-ANTIVIRUS-TEST-FILE"u8, "text/plain", "scanner-2", "policy-2"), CancellationToken.None);
            quarantined.Classification.Should().Be(FileClassification.Quarantined);
            quarantined.SearchProjectionAllowed.Should().BeFalse();
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

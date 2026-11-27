using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.ApiContractTests;

public sealed class ManagedFileAssessmentEndpointTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Assessment_enforces_current_project_and_lifecycle_even_without_revision()
    {
        var project = $"assessment-{Guid.NewGuid():N}";
        var otherProject = $"{project}-other";
        var plainToken = RandomNumberGenerator.GetHexString(48);
        var factory = environment.GetFactory();
        Guid allowed;
        Guid forbidden;
        Guid deletedAsset;
        Guid deletedVersion;
        using (var seed = factory.Services.CreateScope())
        {
            var db = seed.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var actor = await db.TenantUsers.AsNoTracking().SingleAsync(user => user.Username == "contract-test-admin");
            var now = DateTimeOffset.UtcNow;
            db.ApiTokens.Add(new ApiToken
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.Id,
                Name = project,
                TokenHash = TenantSecurityService.HashToken(plainToken),
                TokenPrefix = "fixture",
                TokenLastFour = plainToken[^4..],
                Scopes = [SecurityScopes.SecurityManage],
                AllowedProjectIds = [project.ToUpperInvariant()],
                ExpiresAt = now.AddMinutes(10),
                CreatedAt = now,
                UpdatedAt = now
            });
            allowed = await SeedVersionAsync(project, FileAssetState.Active, FileVersionLifecycle.IntegrityVerified);
            forbidden = await SeedVersionAsync(otherProject, FileAssetState.Active, FileVersionLifecycle.IntegrityVerified);
            deletedAsset = await SeedVersionAsync(project, FileAssetState.LogicalDeleted, FileVersionLifecycle.IntegrityVerified);
            deletedVersion = await SeedVersionAsync(project, FileAssetState.Active, FileVersionLifecycle.LogicalDeleted);

            async Task<Guid> SeedVersionAsync(string projectId, FileAssetState assetState, FileVersionLifecycle lifecycle)
            {
                var file = new FileAsset
                {
                    TenantId = actor.TenantId,
                    OwnerUserId = actor.Id,
                    ProjectId = projectId,
                    LogicalFileName = "fixture.txt",
                    NormalizedFileName = "fixture.txt",
                    State = assetState,
                    CreatedByActorId = actor.Id.ToString("D"),
                    CreatedAt = now,
                    UpdatedAt = now,
                    DeletedAt = assetState == FileAssetState.LogicalDeleted ? now : null
                };
                var managedObject = new ManagedObject
                {
                    TenantId = actor.TenantId,
                    OwnerUserId = actor.Id,
                    ProjectId = projectId,
                    State = ManagedObjectState.Ready,
                    StorageId = RandomNumberGenerator.GetHexString(64, lowercase: true),
                    PlaintextLength = 10,
                    ChunkSize = 65536,
                    ChunkCount = 1,
                    KeyId = "test-key",
                    WrappedDek = new byte[32],
                    WrapNonce = new byte[12],
                    WrapTag = new byte[16],
                    PlaintextSha256 = new string('b', 64),
                    StagedUntil = now.AddHours(1),
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.FileAssets.Add(file);
                db.ManagedObjects.Add(managedObject);
                await db.SaveChangesAsync();
                var version = new FileVersion
                {
                    FileAssetId = file.Id,
                    ManagedObjectId = managedObject.Id,
                    VersionNumber = 1,
                    ContentSha256 = managedObject.PlaintextSha256,
                    ContentType = "text/plain",
                    DeduplicationScopeKey = RandomNumberGenerator.GetHexString(64, lowercase: true),
                    Lifecycle = lifecycle,
                    Classification = FileClassification.Restricted,
                    NeedsRescan = true,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.FileVersions.Add(version);
                await db.SaveChangesAsync();
                return version.Id;
            }
        }

        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plainToken);
        var report = new FileSecurityAssessment(true, true, "fixture-scanner", "fixture-policy",
            [new("fixture-pii", SecurityFindingCategory.Pii, SecurityFindingSeverity.Medium, 1m,
                "fixture-scanner", new string('c', 64), "Synthetic PII finding")]);
        foreach (var denied in new[] { forbidden, deletedAsset, deletedVersion })
        {
            using var legacy = await client.PostAsJsonAsync($"/api/files/{denied:D}/security-assessments", report);
            using var current = await client.PostAsJsonAsync($"/api/files/{denied:D}/security-assessments",
                report with { ExpectedClassificationRevision = 1 });
            legacy.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            current.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            foreach (var response in new[] { legacy, current })
            {
                var problem = await response.Content.ReadAsStringAsync();
                problem.Should().Contain("File version is not available.")
                    .And.NotContain(otherProject).And.NotContain(denied.ToString("D"));
            }
        }

        using var first = await client.PostAsJsonAsync($"/api/files/{allowed:D}/security-assessments", report);
        using var repeated = await client.PostAsJsonAsync($"/api/files/{allowed:D}/security-assessments", report);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        repeated.StatusCode.Should().Be(HttpStatusCode.OK);

        using var verification = factory.Services.CreateScope();
        var readback = verification.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var stale = new[] { forbidden, deletedAsset, deletedVersion };
        var versions = await readback.FileVersions.AsNoTracking().Where(version => stale.Contains(version.Id)).ToArrayAsync();
        versions.Should().HaveCount(3).And.OnlyContain(version => version.ClassificationRevision == 1 &&
            version.Classification == FileClassification.Restricted && version.LastScanAt == null &&
            version.NeedsRescan && !version.SearchProjectionAllowed && !version.EmbeddingAllowed);
        versions.Single(version => version.Id == deletedVersion).Lifecycle.Should().Be(FileVersionLifecycle.LogicalDeleted);
        (await readback.FileSecurityFindings.AnyAsync(finding => stale.Contains(finding.FileVersionId))).Should().BeFalse();
        (await readback.FileSearchProjections.AnyAsync(projection => stale.Contains(projection.FileVersionId))).Should().BeFalse();
        (await readback.FileAccessEvents.AnyAsync(entry => entry.FileVersionId.HasValue && stale.Contains(entry.FileVersionId.Value))).Should().BeFalse();

        var accepted = await readback.FileVersions.AsNoTracking().SingleAsync(version => version.Id == allowed);
        accepted.Classification.Should().Be(FileClassification.Sensitive);
        accepted.ClassificationRevision.Should().Be(3);
        (await readback.FileSecurityFindings.CountAsync(finding => finding.FileVersionId == allowed)).Should().Be(1);
    }
}

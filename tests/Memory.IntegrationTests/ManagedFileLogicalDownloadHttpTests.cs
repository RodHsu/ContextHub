using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ManagedFileLogicalDownloadHttpTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Logical_download_uses_the_requested_historical_version_through_the_registered_http_routes()
    {
        var storage = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "managed-storage-tests", Guid.NewGuid().ToString("N")));
        try
        {
            await using var factory = environment.GetFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.PostConfigure<ManagedObjectStorageOptions>(options =>
                {
                    options.Enabled = true;
                    options.RootPath = storage.FullName;
                });
                services.PostConfigure<ManagedFileKeyAuthorityOptions>(options =>
                {
                    options.CurrentKeyId = "logical-download-fixture";
                    options.Keys[options.CurrentKeyId] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                });
            }));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MemoryApplicationFactory.TestBootstrapToken);

            var projectId = $"logical-download-{Guid.NewGuid():N}";
            var otherProjectId = $"{projectId}-other";
            var historicalBytes = "historical fixture content"u8.ToArray();
            var latestBytes = "separate latest fixture content"u8.ToArray();
            Guid fileId;
            Guid historicalVersionId;
            Guid historicalObjectId;
            using (var seed = factory.Services.CreateScope())
            {
                var db = seed.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var owner = await db.TenantUsers.AsNoTracking().SingleAsync(user => user.Username == "contract-test-admin");
                seed.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
                    owner.TenantId, owner.Id, owner.Username, owner.Role,
                    [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SecurityManage], [projectId], IsAuthenticated: true);
                var transfers = seed.ServiceProvider.GetRequiredService<IManagedTransferService>();
                var files = seed.ServiceProvider.GetRequiredService<IManagedFileService>();

                historicalObjectId = await Upload(transfers, projectId, historicalBytes, "old");
                var first = await files.CreateAsync(new(historicalObjectId, "versioned.txt", "text/plain", "test", "file-old", projectId,
                    ManagedFileCreateIntent.CreateNewFile), CancellationToken.None);
                first.Outcome.Should().Be(ManagedFileCreateOutcome.Created);
                fileId = first.FileId!.Value;
                historicalVersionId = first.FileVersionId!.Value;

                var latestObjectId = await Upload(transfers, projectId, latestBytes, "new");
                var latest = await files.CreateAsync(new(latestObjectId, "versioned.txt", "text/plain", "test", "file-new", projectId,
                    ManagedFileCreateIntent.CreateNewVersion, fileId), CancellationToken.None);
                latest.Outcome.Should().Be(ManagedFileCreateOutcome.Created);
                latest.FileId.Should().Be(fileId);
                latest.VersionNumber.Should().Be(2);
                latest.FileVersionId.Should().NotBe(historicalVersionId);

                var scanner = new ManagedFileContentScanner();
                var historicalAssessment = scanner.Scan(historicalBytes, "text/plain", "fixture-scanner", "fixture-policy");
                var latestAssessment = scanner.Scan(latestBytes, "text/plain", "fixture-scanner", "fixture-policy");
                (await files.ApplySecurityAssessmentAsync(historicalVersionId, historicalAssessment, CancellationToken.None))
                    .Lifecycle.Should().Be(FileVersionLifecycle.Ready);
                (await files.ApplySecurityAssessmentAsync(latest.FileVersionId!.Value, latestAssessment, CancellationToken.None))
                    .Lifecycle.Should().Be(FileVersionLifecycle.Ready);

                var rights = seed.ServiceProvider.GetRequiredService<IPlatformFoundationStore>();
                await rights.UpsertExplicitGrantAsync(new(null, projectId, owner.Id.ToString("D"), "download", AuthorizationEffect.Allow,
                    "integration-fixture", 0, "File", fileId.ToString("D")), CancellationToken.None);
            }

            var request = new CreateLogicalFileDownloadRequest(projectId, "review", "historical-download");
            var endpoint = $"/api/files/{fileId:D}/versions/{historicalVersionId:D}/downloads";
            using var created = await client.PostAsJsonAsync(endpoint, request);
            created.StatusCode.Should().Be(HttpStatusCode.OK);
            created.Headers.CacheControl!.NoStore.Should().BeTrue();
            created.Headers.Location.Should().BeNull();
            var body = await created.Content.ReadAsStringAsync();
            using var json = JsonDocument.Parse(body);
            json.RootElement.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(new[]
            {
                "fileId", "fileVersionId", "sessionId", "capabilityId", "capability", "operation", "revision",
                "chunkBytes", "maxBytes", "expiresAt", "resourcePath"
            });
            body.Contains(historicalObjectId.ToString("D"), StringComparison.OrdinalIgnoreCase).Should().BeFalse();
            body.Contains(storage.Name, StringComparison.Ordinal).Should().BeFalse();
            var session = json.RootElement.Deserialize<LogicalFileDownloadSessionResult>(JsonSerializerOptions.Web)!;
            session.FileId.Should().Be(fileId);
            session.FileVersionId.Should().Be(historicalVersionId);
            session.Operation.Should().Be(ManagedTransferOperation.Download);
            session.ResourcePath.Should().Be($"/api/transfers/{session.SessionId:D}");

            using var range = new HttpRequestMessage(HttpMethod.Get, $"{session.ResourcePath}/content?offset=0&length={historicalBytes.Length}");
            range.Headers.Add("X-ContextHub-Capability", session.Capability);
            range.Headers.Add("X-Transfer-Revision", session.Revision.ToString(CultureInfo.InvariantCulture));
            range.Headers.Add("X-Request-Id", "historical-range");
            using var content = await client.SendAsync(range);
            content.StatusCode.Should().Be(HttpStatusCode.OK);
            content.Headers.CacheControl!.NoStore.Should().BeTrue();
            content.Content.Headers.ContentType!.MediaType.Should().Be("application/octet-stream");
            (await content.Content.ReadAsByteArrayAsync()).Should().Equal(historicalBytes);

            using var wrongProject = await client.PostAsJsonAsync(endpoint, request with { ProjectId = otherProjectId, IdempotencyKey = "wrong-project" });
            wrongProject.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await wrongProject.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
            using var missingPin = await client.PostAsJsonAsync($"/api/files/{fileId:D}/versions/{Guid.NewGuid():D}/downloads",
                request with { IdempotencyKey = "missing-pin" });
            missingPin.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await missingPin.Content.ReadAsByteArrayAsync()).Should().BeEmpty();

            using var verification = factory.Services.CreateScope();
            var database = verification.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var sessions = await database.ManagedTransferSessions.AsNoTracking()
                .Where(row => row.Operation == ManagedTransferOperation.Download && (row.ProjectId == projectId || row.ProjectId == otherProjectId))
                .ToArrayAsync();
            var persisted = sessions.Should().ContainSingle().Which;
            persisted.Id.Should().Be(session.SessionId);
            persisted.FileVersionId.Should().Be(historicalVersionId);
            persisted.ManagedObjectId.Should().Be(historicalObjectId);
            persisted.UsedBytes.Should().Be(historicalBytes.Length);
            var legacyHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(session.Capability)));
            var versionInput = $"ContextHub.ManagedFile.PinnedDownloadCapability.v1|{historicalVersionId:D}|{session.Capability}";
            var versionHash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(versionInput)));
            byte[] pinnedInput = [0xff, .. Encoding.ASCII.GetBytes(versionHash)];
            var expectedPinnedHash = Convert.ToHexStringLower(SHA256.HashData(pinnedInput));
            persisted.CapabilityHash.Should().Be(expectedPinnedHash).And.NotBe(legacyHash);
        }
        finally
        {
            storage.Delete(recursive: true);
        }
    }

    private static async Task<Guid> Upload(IManagedTransferService transfers, string projectId, byte[] bytes, string key)
    {
        var session = await transfers.CreateUploadAsync(new(projectId, bytes.Length, "test", $"{key}-upload"), CancellationToken.None);
        var hash = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
        var chunk = await transfers.UploadChunkAsync(new(session.SessionId, session.Capability, session.Revision, $"{key}-chunk", 0,
            bytes.ToArray(), hash), CancellationToken.None);
        await transfers.CompleteUploadAsync(session.SessionId, session.Capability, chunk.Revision, $"{key}-complete", CancellationToken.None);
        return session.Object.ObjectId;
    }
}

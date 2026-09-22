using System.Security.Cryptography;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.IntegrationTests;

public sealed class ManagedStorageWorkflowTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Encrypted_chunk_upload_range_download_rewrap_and_tamper_paths_should_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var root = Path.Combine(Path.GetTempPath(), "contexthub-managed-workflow", Guid.NewGuid().ToString("N"));
        var keySettings = new ManagedFileKeyAuthorityOptions
        {
            CurrentKeyId = "kek-old",
            Keys =
            {
                ["kek-old"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["kek-new"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        };
        var transferSettings = new ManagedTransferOptions { ChunkBytes = 64 * 1024, MaxRangeBytes = 256 * 1024 };
        var store = new FileSystemManagedObjectStore(Options.Create(new ManagedObjectStorageOptions { Enabled = true, RootPath = root }));
        var authority = new AesGcmManagedFileKeyAuthority(Options.Create(keySettings));
        var service = new ManagedTransferService(
            db,
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(),
            scope.ServiceProvider.GetRequiredService<IClock>(),
            store,
            authority,
            scope.ServiceProvider.GetRequiredService<IPlatformFoundationStore>(),
            Options.Create(transferSettings));
        try
        {
            var original = RandomNumberGenerator.GetBytes(150_123);
            var upload = await service.CreateUploadAsync(new CreateManagedUploadRequest(
                "managed-storage-test",
                original.Length,
                "integration-test",
                Guid.NewGuid().ToString("N"),
                AgentId: "agent-neutral-test"), CancellationToken.None);

            long revision = upload.Revision;
            for (var index = 0; index < 3; index++)
            {
                var offset = index * transferSettings.NormalizedChunkBytes;
                var length = Math.Min(transferSettings.NormalizedChunkBytes, original.Length - offset);
                var chunk = original.AsSpan(offset, length).ToArray();
                var checksum = Convert.ToHexString(SHA256.HashData(chunk)).ToLowerInvariant();
                var result = await service.UploadChunkAsync(new ManagedChunkWriteRequest(
                    upload.SessionId, upload.Capability, revision, $"chunk-{index}", index, chunk, checksum), CancellationToken.None);
                revision = result.Revision;
            }

            var stale = () => service.UploadChunkAsync(new ManagedChunkWriteRequest(
                upload.SessionId, upload.Capability, 1, "stale-replay", 0, new byte[transferSettings.NormalizedChunkBytes], new string('0', 64)), CancellationToken.None);
            await stale.Should().ThrowAsync<UnauthorizedAccessException>();
            var foreign = () => service.CompleteUploadAsync(upload.SessionId, Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), revision, "foreign", CancellationToken.None);
            await foreign.Should().ThrowAsync<UnauthorizedAccessException>();
            var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            actorAccessor.Current = actor with { TenantId = Guid.NewGuid(), UserId = Guid.NewGuid() };
            var foreignTenant = () => service.CompleteUploadAsync(upload.SessionId, upload.Capability, revision, "foreign-tenant", CancellationToken.None);
            await foreignTenant.Should().ThrowAsync<UnauthorizedAccessException>();
            actorAccessor.Current = actor;

            await service.CompleteUploadAsync(upload.SessionId, upload.Capability, revision, "complete", CancellationToken.None);
            var persisted = await db.ManagedObjects.Include(x => x.Chunks).SingleAsync(x => x.Id == upload.Object.ObjectId);
            persisted.State.Should().Be(ManagedObjectState.Ready);
            persisted.Chunks.Select(x => Convert.ToHexString(x.Nonce)).Should().OnlyHaveUniqueItems();
            persisted.WrappedDek.Should().NotEqual(original.AsSpan(0, 32).ToArray());
            var firstCiphertextPath = Path.Combine(root, persisted.StorageId[..2], persisted.StorageId, "0000000000.bin");
            var firstCiphertext = await File.ReadAllBytesAsync(firstCiphertextPath);
            firstCiphertext.Should().NotEqual(original.AsSpan(0, firstCiphertext.Length).ToArray());

            var download = await service.CreateDownloadAsync(new CreateManagedDownloadRequest(
                persisted.ProjectId, persisted.Id, "range-test", Guid.NewGuid().ToString("N"), AgentId: "another-agent"), CancellationToken.None);
            await using var output = new MemoryStream();
            var copied = await service.CopyRangeAsync(new ManagedRangeReadRequest(
                download.SessionId, download.Capability, download.Revision, "range-1", 60_000, 80_000), output, CancellationToken.None);
            copied.Should().Be(80_000);
            output.ToArray().Should().Equal(original.AsSpan(60_000, 80_000).ToArray());

            keySettings.CurrentKeyId = "kek-new";
            await service.RewrapObjectKeyAsync(persisted.Id, CancellationToken.None);
            db.ChangeTracker.Clear();
            (await db.ManagedObjects.SingleAsync(x => x.Id == persisted.Id)).KeyId.Should().Be("kek-new");
            var afterRotation = await service.CreateDownloadAsync(new CreateManagedDownloadRequest(
                persisted.ProjectId, persisted.Id, "post-rotation", Guid.NewGuid().ToString("N")), CancellationToken.None);
            await using var rotatedOutput = new MemoryStream();
            await service.CopyRangeAsync(new ManagedRangeReadRequest(
                afterRotation.SessionId, afterRotation.Capability, afterRotation.Revision, "range-rotation", 0, 1024), rotatedOutput, CancellationToken.None);
            rotatedOutput.ToArray().Should().Equal(original.AsSpan(0, 1024).ToArray());

            firstCiphertext[0] ^= 0x80;
            await File.WriteAllBytesAsync(firstCiphertextPath, firstCiphertext);
            var tampered = await service.CreateDownloadAsync(new CreateManagedDownloadRequest(
                persisted.ProjectId, persisted.Id, "tamper", Guid.NewGuid().ToString("N")), CancellationToken.None);
            await using var sink = new MemoryStream();
            var tamperRead = () => service.CopyRangeAsync(new ManagedRangeReadRequest(
                tampered.SessionId, tampered.Capability, tampered.Revision, "range-tamper", 0, 1024), sink, CancellationToken.None);
            await tamperRead.Should().ThrowAsync<CryptographicException>();

            transferSettings.MaxBytesPerSecond = 64 * 1024;
            var rateLimited = await service.CreateUploadAsync(new CreateManagedUploadRequest(
                persisted.ProjectId, 128 * 1024, "rate-limit", Guid.NewGuid().ToString("N")), CancellationToken.None);
            var rateChunk = RandomNumberGenerator.GetBytes(64 * 1024);
            var rateChecksum = Convert.ToHexString(SHA256.HashData(rateChunk)).ToLowerInvariant();
            var firstRateWrite = await service.UploadChunkAsync(new ManagedChunkWriteRequest(
                rateLimited.SessionId, rateLimited.Capability, rateLimited.Revision, "rate-1", 0, rateChunk, rateChecksum), CancellationToken.None);
            var secondRateChunk = RandomNumberGenerator.GetBytes(64 * 1024);
            var secondRateChecksum = Convert.ToHexString(SHA256.HashData(secondRateChunk)).ToLowerInvariant();
            var rateExceeded = () => service.UploadChunkAsync(new ManagedChunkWriteRequest(
                rateLimited.SessionId, rateLimited.Capability, firstRateWrite.Revision, "rate-2", 1, secondRateChunk, secondRateChecksum), CancellationToken.None);
            await rateExceeded.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rate limit exceeded*");

            actor.Username.Should().NotContain("ChatGPT").And.NotContain("Codex").And.NotContain("Gemini");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
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

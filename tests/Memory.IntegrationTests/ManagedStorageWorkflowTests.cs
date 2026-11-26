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
        var root = Path.Combine(FindRepositoryRoot(), ".agent", "local", "managed-storage-tests", Guid.NewGuid().ToString("N"));
        var clock = new MutableClock(DateTimeOffset.UtcNow);
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
            clock,
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

            var completed = await service.CompleteUploadAsync(upload.SessionId, upload.Capability, revision, "complete", CancellationToken.None);
            var replayedCompletion = await service.CompleteUploadAsync(upload.SessionId, upload.Capability, revision, "complete", CancellationToken.None);
            replayedCompletion.Should().Be(completed with { Replayed = true });

            var firstChunk = original.AsSpan(0, transferSettings.NormalizedChunkBytes).ToArray();
            var firstHash = Convert.ToHexString(SHA256.HashData(firstChunk)).ToLowerInvariant();
            var firstRequest = new ManagedChunkWriteRequest(
                upload.SessionId, upload.Capability, upload.Revision, "chunk-0", 0, firstChunk, firstHash);
            var replayedChunk = await service.UploadChunkAsync(firstRequest, CancellationToken.None);
            replayedChunk.Should().Be(new ManagedTransferMutationResult(
                upload.SessionId, upload.Revision + 1, transferSettings.NormalizedChunkBytes, true));

            var alteredChunk = original.AsSpan(0, transferSettings.NormalizedChunkBytes).ToArray();
            alteredChunk[0] ^= 1;
            var alteredHash = Convert.ToHexString(SHA256.HashData(alteredChunk)).ToLowerInvariant();
            var alteredReplay = () => service.UploadChunkAsync(
                firstRequest with { Plaintext = alteredChunk, PlaintextSha256 = alteredHash }, CancellationToken.None);
            await alteredReplay.Should().ThrowAsync<UnauthorizedAccessException>();
            var newCompletion = () => service.CompleteUploadAsync(
                upload.SessionId, upload.Capability, completed.Revision, "new-completion", CancellationToken.None);
            await newCompletion.Should().ThrowAsync<UnauthorizedAccessException>();
            (await db.ManagedTransferOperationRecords.CountAsync(x => x.SessionId == upload.SessionId)).Should().Be(5);
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
                rateLimited.SessionId, rateLimited.Capability, firstRateWrite.Revision, "rate-2", 1, secondRateChunk.ToArray(), secondRateChecksum), CancellationToken.None);
            await rateExceeded.Should().ThrowAsync<InvalidOperationException>().WithMessage("*rate limit exceeded*");

            clock.UtcNow = clock.UtcNow.AddSeconds(2);
            await service.UploadChunkAsync(new ManagedChunkWriteRequest(
                rateLimited.SessionId, rateLimited.Capability, firstRateWrite.Revision, "rate-2", 1, secondRateChunk, secondRateChecksum), CancellationToken.None);

            actor.Username.Should().NotContain("ChatGPT").And.NotContain("Codex").And.NotContain("Gemini");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [DockerRequiredFact]
    public async Task Concurrent_exact_retries_commit_once_and_multichunk_ranges_preserve_content()
    {
        var factory = environment.GetFactory();
        using var first = factory.Services.CreateScope();
        using var second = factory.Services.CreateScope();
        UseBootstrapActor(first.ServiceProvider);
        UseBootstrapActor(second.ServiceProvider);
        var db = first.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var secondDb = second.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var root = Path.Combine(FindRepositoryRoot(), ".agent", "local", "managed-storage-tests", Guid.NewGuid().ToString("N"));
        var clock = new MutableClock(DateTimeOffset.UtcNow);
        var settings = new ManagedTransferOptions
        {
            ChunkBytes = 1024 * 1024,
            MaxRangeBytes = 4 * 1024 * 1024,
            MaxBytesPerSecond = 128L * 1024 * 1024
        };
        var store = new FileSystemManagedObjectStore(Options.Create(new ManagedObjectStorageOptions
        {
            Enabled = true,
            RootPath = root
        }));
        var authority = new AesGcmManagedFileKeyAuthority(Options.Create(new ManagedFileKeyAuthorityOptions
        {
            CurrentKeyId = "test-key",
            Keys = { ["test-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
        }));
        ManagedTransferService CreateTransfer(IServiceProvider services) => new(
            services.GetRequiredService<MemoryDbContext>(),
            services.GetRequiredService<IRequestActorAccessor>(), clock, store, authority,
            services.GetRequiredService<IPlatformFoundationStore>(), Options.Create(settings));

        var writer = CreateTransfer(first.ServiceProvider);
        var retry = CreateTransfer(second.ServiceProvider);
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var token = timeout.Token;
        try
        {
            var original = RandomNumberGenerator.GetBytes(32 * settings.ChunkBytes + 13);
            var fileHash = SHA256.HashData(original);
            var project = $"multichunk-{Guid.NewGuid():N}";
            var upload = await writer.CreateUploadAsync(new CreateManagedUploadRequest(
                project, original.Length, "integration", "create"), token);

            ManagedChunkWriteRequest Chunk(int index, long revision)
            {
                var offset = index * settings.ChunkBytes;
                var bytes = original.AsSpan(offset, Math.Min(settings.ChunkBytes, original.Length - offset)).ToArray();
                return new ManagedChunkWriteRequest(upload.SessionId, upload.Capability, revision, $"chunk-{index}", index,
                    bytes, Convert.ToHexString(SHA256.HashData(bytes)));
            }

            var firstChunk = await Task.WhenAll(
                writer.UploadChunkAsync(Chunk(0, upload.Revision), token),
                retry.UploadChunkAsync(Chunk(0, upload.Revision), token));
            firstChunk.Should().ContainSingle(result => result.Replayed);
            firstChunk.Should().OnlyContain(result =>
                result.Revision == upload.Revision + 1 && result.BytesTransferred == settings.ChunkBytes);
            db.ChangeTracker.Clear();
            secondDb.ChangeTracker.Clear();
            var revision = firstChunk[0].Revision;
            var chunkCount = (original.Length + settings.ChunkBytes - 1) / settings.ChunkBytes;
            for (var index = 1; index < chunkCount; index++)
            {
                var result = await writer.UploadChunkAsync(Chunk(index, revision), token);
                result.Replayed.Should().BeFalse();
                revision = result.Revision;
            }

            var completion = await Task.WhenAll(
                writer.CompleteUploadAsync(upload.SessionId, upload.Capability, revision, "finish", token),
                retry.CompleteUploadAsync(upload.SessionId, upload.Capability, revision, "finish", token));
            completion.Should().ContainSingle(result => result.Replayed);
            completion.Should().OnlyContain(result => result.Revision == revision + 1 && result.BytesTransferred == 0);

            db.ChangeTracker.Clear();
            var session = await db.ManagedTransferSessions.AsNoTracking()
                .SingleAsync(item => item.Id == upload.SessionId, token);
            session.UsedBytes.Should().Be(original.Length);
            session.Revision.Should().Be(upload.Revision + chunkCount + 1);
            (await db.ManagedTransferOperationRecords.CountAsync(item => item.SessionId == upload.SessionId, token))
                .Should().Be(chunkCount + 2);
            var stored = await db.ManagedObjects.AsNoTracking().Include(item => item.Chunks)
                .SingleAsync(item => item.Id == upload.Object.ObjectId, token);
            stored.Chunks.Should().HaveCount(chunkCount);
            stored.PlaintextSha256.Should().Be(Convert.ToHexString(fileHash).ToLowerInvariant());

            var download = await writer.CreateDownloadAsync(new CreateManagedDownloadRequest(
                project, stored.Id, "integration", "download"), token);
            var downloadedPath = Path.Combine(root, "downloaded.bin");
            await using (var output = File.Create(downloadedPath))
            {
                var rangeBytes = 3 * settings.ChunkBytes + 23;
                var rangeRevision = download.Revision;
                for (var offset = 0; offset < original.Length; offset += rangeBytes)
                {
                    var length = Math.Min(rangeBytes, original.Length - offset);
                    var copied = await writer.CopyRangeAsync(new ManagedRangeReadRequest(
                        download.SessionId, download.Capability, rangeRevision, $"range-{offset}", offset, length), output, token);
                    copied.Should().Be(length);
                    rangeRevision++;
                }
            }

            await using var downloaded = File.OpenRead(downloadedPath);
            downloaded.Length.Should().Be(original.Length);
            (await SHA256.HashDataAsync(downloaded, token)).Should().Equal(fileHash);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    private sealed class MutableClock(DateTimeOffset value) : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = value;
    }

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "ContextHub.slnx"))) return directory.FullName;
        }

        throw new InvalidOperationException("Repository-local ignored test artifact root is unavailable.");
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

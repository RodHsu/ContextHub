using System.Data.Common;
using System.Security.Cryptography;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.IntegrationTests;

public sealed class ManagedChunkAttemptRecoveryTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Rolled_back_chunk_can_be_retried_without_overwriting_ciphertext_and_legacy_reads_stay_exact()
    {
        var payload = "synthetic managed chunk"u8.ToArray();
        var checksum = Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant();
        var projectId = $"attempt-recovery-{Guid.NewGuid():N}";
        var services = environment.GetFactory().Services;
        using var verification = services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.AsNoTracking().SingleAsync(row => row.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [projectId], IsAuthenticated: true);

        var directory = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "managed-storage-tests", Guid.NewGuid().ToString("N")));
        try
        {
            var objectStore = new FileSystemManagedObjectStore(Options.Create(
                new ManagedObjectStorageOptions { Enabled = true, RootPath = directory.FullName }));
            var keyOptions = new ManagedFileKeyAuthorityOptions { CurrentKeyId = "attempt-fixture" };
            keyOptions.Keys[keyOptions.CurrentKeyId] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
            var authority = new AesGcmManagedFileKeyAuthority(Options.Create(keyOptions));
            var transferOptions = Options.Create(new ManagedTransferOptions { ChunkBytes = 64 * 1024 });
            var upload = await AsTransfer(transfers => transfers.CreateUploadAsync(
                new(projectId, payload.Length, "test", "create-upload"), CancellationToken.None));
            var objectId = upload.Object.ObjectId;
            var managed = await db.ManagedObjects.AsNoTracking().SingleAsync(row => row.Id == objectId);
            var storagePath = Path.Combine(directory.FullName, managed.StorageId[..2], managed.StorageId);
            var intercepted = new FailFirstCommit();

            await FluentActions.Awaiting(() => Write(intercepted)).Should().ThrowAsync<InjectedCommitFailure>();
            intercepted.DidFail.Should().BeTrue();
            var abandonedFile = Directory.GetFiles(storagePath, "*.bin").Should().ContainSingle().Which;
            var abandonedCiphertext = await File.ReadAllBytesAsync(abandonedFile);
            (await db.ManagedObjectChunks.AsNoTracking().AnyAsync(row => row.ManagedObjectId == objectId)).Should().BeFalse();
            (await db.ManagedTransferOperationRecords.AsNoTracking()
                .AnyAsync(row => row.SessionId == upload.SessionId && row.RequestId == "chunk")).Should().BeFalse();
            var rolledBackSession = await db.ManagedTransferSessions.AsNoTracking().SingleAsync(row => row.Id == upload.SessionId);
            rolledBackSession.Revision.Should().Be(upload.Revision);
            rolledBackSession.UsedBytes.Should().Be(0);

            var retries = await Task.WhenAll(Write(), Write());
            retries.Select(result => result.Replayed).Should().BeEquivalentTo(new[] { false, true });
            retries.Should().OnlyContain(result => result.Revision == upload.Revision + 1 && result.BytesTransferred == payload.Length);
            var chunk = await db.ManagedObjectChunks.AsNoTracking().SingleAsync(row => row.ManagedObjectId == objectId);
            chunk.LocatorFormat.Should().Be(ManagedChunkLocatorFormat.ImmutableAttempt);
            (await db.ManagedTransferOperationRecords.AsNoTracking()
                .CountAsync(row => row.SessionId == upload.SessionId && row.RequestId == "chunk")).Should().Be(1);
            var committed = await db.ManagedTransferSessions.AsNoTracking().SingleAsync(row => row.Id == upload.SessionId);
            committed.Revision.Should().Be(upload.Revision + 1);
            committed.UsedBytes.Should().Be(payload.Length);
            var committedPath = Path.Combine(storagePath, $"{chunk.ChunkIndex:D10}.{chunk.Id:N}.bin");
            committedPath.Should().NotBe(abandonedFile);
            Directory.GetFiles(storagePath, "*.bin").Should().HaveCount(2);
            (await File.ReadAllBytesAsync(abandonedFile)).Should().Equal(abandonedCiphertext);

            await AsTransfer(transfers => transfers.CompleteUploadAsync(upload.SessionId, upload.Capability,
                committed.Revision, "complete", CancellationToken.None));
            await using var bytes = new MemoryStream();
            await Read(bytes, "attempt-read");
            bytes.ToArray().Should().Equal(payload);

            // Make the legacy path contain genuine ciphertext for these same metadata. A missing
            // immutable address must still fail instead of reading whichever path happens to exist.
            var legacyPath = Path.Combine(storagePath, $"{chunk.ChunkIndex:D10}.bin");
            File.Move(committedPath, legacyPath, overwrite: false);
            await using var missing = new MemoryStream();
            await FluentActions.Awaiting(() => Read(missing, "missing-attempt"))
                .Should().ThrowAsync<InvalidOperationException>().WithMessage("Managed object content is unavailable.");
            missing.Length.Should().Be(0);

            // The migrated legacy format continues to read the authentic old layout.
            (await db.ManagedObjectChunks.Where(row => row.Id == chunk.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.LocatorFormat, ManagedChunkLocatorFormat.LegacyIndex)))
                .Should().Be(1);
            await using var legacy = new MemoryStream();
            await Read(legacy, "legacy-read");
            legacy.ToArray().Should().Equal(payload);

            Task<ManagedTransferMutationResult> Write(DbTransactionInterceptor? interceptor = null)
                => AsTransfer(transfers => transfers.UploadChunkAsync(new(upload.SessionId, upload.Capability, upload.Revision,
                    "chunk", 0, payload.ToArray(), checksum), CancellationToken.None), interceptor);

            async Task Read(Stream destination, string requestId)
            {
                var download = await AsTransfer(transfers => transfers.CreateDownloadAsync(
                    new(projectId, objectId, "test", $"create-{requestId}"), CancellationToken.None));
                await AsTransfer(transfers => transfers.CopyRangeAsync(new(download.SessionId, download.Capability,
                    download.Revision, requestId, 0, payload.Length), destination, CancellationToken.None));
            }

            async Task<T> AsTransfer<T>(Func<ManagedTransferService, Task<T>> action, DbTransactionInterceptor? interceptor = null)
            {
                using var scope = services.CreateScope();
                var provider = scope.ServiceProvider;
                var dbOptions = new DbContextOptionsBuilder<MemoryDbContext>(provider.GetRequiredService<DbContextOptions<MemoryDbContext>>());
                if (interceptor is not null) dbOptions.AddInterceptors(interceptor);
                await using var context = new MemoryDbContext(dbOptions.Options);
                var accessor = provider.GetRequiredService<IRequestActorAccessor>();
                accessor.Current = actor;
                return await action(new ManagedTransferService(context, accessor, provider.GetRequiredService<IClock>(), objectStore,
                    authority, provider.GetRequiredService<IPlatformFoundationStore>(), transferOptions));
            }
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class FailFirstCommit : DbTransactionInterceptor
    {
        public bool DidFail { get; private set; }

        public override ValueTask<InterceptionResult> TransactionCommittingAsync(
            DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (DidFail) return ValueTask.FromResult(result);
            DidFail = true;
            throw new InjectedCommitFailure();
        }
    }

    private sealed class InjectedCommitFailure : Exception
    {
    }
}

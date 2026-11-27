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

public sealed class ManagedStorageReconciliationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Unavailable_root_preserves_object_metadata_and_recovery_distinguishes_missing_chunks()
    {
        var services = environment.GetFactory().Services;
        var fixture = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "managed-storage-tests", Guid.NewGuid().ToString("N")));
        var root = Path.Combine(fixture.FullName, "store");
        var settings = new ManagedObjectStorageOptions { Enabled = true, RootPath = root };
        var store = new FileSystemManagedObjectStore(Options.Create(settings));
        var now = DateTimeOffset.UtcNow;
        using var setup = services.CreateScope();
        var db = setup.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await db.TenantUsers.AsNoTracking().SingleAsync(user => user.Username == "contract-test-admin");
        var clock = new ReconciliationClock(now);
        try
        {
            var ready = CreateObject(ManagedObjectState.Ready);
            var staged = CreateObject(ManagedObjectState.Staged);
            await db.ManagedObjects.AddRangeAsync(ready, staged);
            await db.SaveChangesAsync();
            foreach (var item in new[] { ready, staged })
            {
                var opaque = RandomNumberGenerator.GetBytes(8);
                var hash = Convert.ToHexString(SHA256.HashData(opaque)).ToLowerInvariant();
                await store.PutChunkAsync(item.StorageId, 0, opaque, CancellationToken.None);
                db.ManagedObjectChunks.Add(new ManagedObjectChunk
                {
                    ManagedObjectId = item.Id,
                    PlaintextLength = opaque.Length,
                    CiphertextLength = opaque.Length,
                    Nonce = new byte[12],
                    AuthenticationTag = new byte[16],
                    PlaintextSha256 = hash,
                    CiphertextSha256 = hash,
                    CreatedAt = now
                });
            }
            await db.SaveChangesAsync();

            settings.RootPath = Path.Combine(fixture.FullName, "offline-store");
            var probe = () => store.ChunkExistsAsync(ready.StorageId, 0, CancellationToken.None);
            var delete = () => store.DeleteObjectAsync(staged.StorageId, CancellationToken.None);
            await probe.Should().ThrowAsync<IOException>().WithMessage("Managed object storage is unavailable.");
            await delete.Should().ThrowAsync<IOException>().WithMessage("Managed object storage is unavailable.");
            await FluentActions.Awaiting(Run).Should().ThrowAsync<IOException>();
            (await ReadState(ready.Id)).Should().Be(ManagedObjectState.Ready);
            (await ReadState(staged.Id)).Should().Be(ManagedObjectState.Staged);

            settings.RootPath = root;
            (await store.ChunkExistsAsync(ready.StorageId, 0, CancellationToken.None)).Should().BeTrue();
            (await store.ChunkExistsAsync(staged.StorageId, 0, CancellationToken.None)).Should().BeTrue();
            (await Run()).Should().Be(new ManagedObjectReconciliationResult(1, 0, 2));
            (await ReadState(ready.Id)).Should().Be(ManagedObjectState.Ready);
            (await ReadState(staged.Id)).Should().Be(ManagedObjectState.Orphaned);
            (await store.ChunkExistsAsync(staged.StorageId, 0, CancellationToken.None)).Should().BeFalse();
            await delete();

            await store.DeleteObjectAsync(ready.StorageId, CancellationToken.None);
            (await Run()).Should().Be(new ManagedObjectReconciliationResult(0, 1, 1));
            (await ReadState(ready.Id)).Should().Be(ManagedObjectState.Missing);
        }
        finally
        {
            fixture.Delete(recursive: true);
        }

        ManagedObject CreateObject(ManagedObjectState state) => new()
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.Id,
            ProjectId = "managed-storage-reconciliation-test",
            State = state,
            StorageId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            PlaintextLength = 8,
            ChunkSize = 64 * 1024,
            ChunkCount = 1,
            KeyId = "synthetic-opaque-chunk",
            WrappedDek = new byte[32],
            WrapNonce = new byte[12],
            WrapTag = new byte[16],
            StagedUntil = now.AddMinutes(-1),
            CreatedAt = now.AddHours(-1),
            UpdatedAt = now.AddHours(-1)
        };

        async Task<ManagedObjectReconciliationResult> Run()
        {
            using var scope = services.CreateScope();
            var context = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            return await new ManagedObjectReconciliationService(context, store, clock).RunAsync(CancellationToken.None);
        }

        async Task<ManagedObjectState> ReadState(Guid id)
        {
            var item = await db.ManagedObjects.AsNoTracking().SingleAsync(item => item.Id == id);
            return item.State;
        }
    }

    private sealed class ReconciliationClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }
}

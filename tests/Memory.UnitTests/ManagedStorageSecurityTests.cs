using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Memory.UnitTests;

public sealed class ManagedStorageSecurityTests
{
    [Fact]
    public void Key_authority_should_authenticate_context_and_rewrap_without_changing_the_dek()
    {
        var settings = new ManagedFileKeyAuthorityOptions
        {
            CurrentKeyId = "kek-2026-01",
            Keys =
            {
                ["kek-2026-01"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
                ["kek-2026-02"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
            }
        };
        var authority = new AesGcmManagedFileKeyAuthority(Options.Create(settings));
        var objectId = Guid.NewGuid();
        var dek = RandomNumberGenerator.GetBytes(32);

        var wrapped = authority.Wrap(objectId, 1, dek);
        authority.Unwrap(objectId, 1, wrapped).Should().Equal(dek);
        var wrongObject = () => authority.Unwrap(Guid.NewGuid(), 1, wrapped);
        wrongObject.Should().Throw<CryptographicException>();
        var wrongGeneration = () => authority.Unwrap(objectId, 2, wrapped);
        wrongGeneration.Should().Throw<CryptographicException>();
        var missingKeyAuthority = new AesGcmManagedFileKeyAuthority(Options.Create(new ManagedFileKeyAuthorityOptions
        {
            CurrentKeyId = "future",
            Keys = { ["future"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
        }));
        var missingHistoricalKey = () => missingKeyAuthority.Unwrap(objectId, 1, wrapped);
        missingHistoricalKey.Should().Throw<CryptographicException>();

        settings.CurrentKeyId = "kek-2026-02";
        var rewrapped = authority.Rewrap(objectId, 1, wrapped);
        rewrapped.KeyId.Should().Be("kek-2026-02");
        rewrapped.Ciphertext.Should().NotEqual(wrapped.Ciphertext);
        authority.Unwrap(objectId, 1, rewrapped).Should().Equal(dek);
    }

    [Fact]
    public async Task Object_store_should_persist_only_caller_supplied_opaque_bytes_and_reject_path_injection()
    {
        var root = Path.Combine(Path.GetTempPath(), "contexthub-managed-store-tests", Guid.NewGuid().ToString("N"));
        try
        {
            var store = new FileSystemManagedObjectStore(Options.Create(new ManagedObjectStorageOptions { Enabled = true, RootPath = root }));
            var storageId = new string('a', 64);
            var opaque = RandomNumberGenerator.GetBytes(1024);
            await store.PutChunkAsync(storageId, 0, opaque, CancellationToken.None);
            var destination = new byte[opaque.Length];
            (await store.ReadChunkAsync(storageId, 0, destination, CancellationToken.None)).Should().Be(opaque.Length);
            destination.Should().Equal(opaque);
            (await store.ChunkExistsAsync(storageId, 0, CancellationToken.None)).Should().BeTrue();

            var traversal = () => store.PutChunkAsync("../provider-bucket", 0, opaque, CancellationToken.None);
            await traversal.Should().ThrowAsync<InvalidOperationException>();
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void External_transfer_contracts_should_be_provider_opaque_and_agent_neutral()
    {
        var externalTypes = new[]
        {
            typeof(ManagedObjectRef),
            typeof(ManagedTransferSessionResult),
            typeof(CreateManagedUploadRequest),
            typeof(CreateManagedDownloadRequest),
            typeof(ManagedTransferMutationResult)
        };
        var forbidden = new[] { "provider", "vendor", "endpoint", "bucket", "container", "objectkey", "presigned", "directurl", "uri" };
        var names = externalTypes.SelectMany(type => type.GetProperties()).Select(property => property.Name.ToLowerInvariant()).ToArray();
        names.Should().NotContain(name => forbidden.Any(name.Contains));
        string.Join('|', names).Should().NotContain("chatgpt").And.NotContain("codex").And.NotContain("gemini");
    }

    [Fact]
    public void Multi_gib_layout_should_remain_chunk_bounded()
    {
        var options = new ManagedTransferOptions { ChunkBytes = 4 * 1024 * 1024, MaxObjectBytes = 5L * 1024 * 1024 * 1024 };
        var length = 4L * 1024 * 1024 * 1024 + 123;
        var chunkCount = checked((int)((length + options.NormalizedChunkBytes - 1) / options.NormalizedChunkBytes));
        chunkCount.Should().Be(1025);
        options.NormalizedChunkBytes.Should().Be(4 * 1024 * 1024);
        ((long)options.NormalizedMaxRangeBytes).Should().BeLessThan(length);
        options.NormalizedMaxBytesPerSecond.Should().Be(32L * 1024 * 1024);
    }
}

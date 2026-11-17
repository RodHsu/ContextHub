using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query;

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

    [Theory]
    [InlineData(long.MaxValue, 1)]
    [InlineData(long.MaxValue - 5, 10)]
    [InlineData(long.MaxValue - 1, 2)]
    [InlineData(-1L, 1)]
    [InlineData(long.MinValue, 1)]
    [InlineData(0L, 0)]
    [InlineData(0L, -1)]
    [InlineData(0L, 65537)]
    [InlineData(100L, 1)]
    [InlineData(99L, 2)]
    [InlineData(101L, 1)]
    public async Task Invalid_range_is_rejected_before_key_content_stream_or_accounting_access(long offset, int length)
    {
        var fixture = new RangeFixture();
        using var output = new MemoryStream();
        Func<Task> operation = () => fixture.CopyAsync(offset, length, output);

        await operation.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Requested range is outside the authorized managed object boundary.");
        fixture.Keys.Unwraps.Should().Be(0);
        fixture.Store.Reads.Should().Be(0);
        output.Length.Should().Be(0);
        fixture.Proxy.OperationReads.Should().Be(0);
        fixture.Proxy.Saves.Should().Be(0);
        fixture.Operations.Should().BeEmpty();
        fixture.Session.UsedBytes.Should().Be(0);
        fixture.Session.Revision.Should().Be(1);
        fixture.Object.State.Should().Be(ManagedObjectState.Ready);
    }

    [Theory]
    [InlineData(-1L)]
    [InlineData(long.MinValue)]
    public async Task Invalid_stored_length_is_rejected_before_subtraction_or_content_access(long total)
    {
        var fixture = new RangeFixture();
        fixture.Object.PlaintextLength = total;
        using var output = new MemoryStream();

        await FluentActions.Awaiting(() => fixture.CopyAsync(0, 1, output))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Requested range is outside the authorized managed object boundary.");
        fixture.Keys.Unwraps.Should().Be(0);
        fixture.Store.Reads.Should().Be(0);
        output.Length.Should().Be(0);
        fixture.Proxy.OperationReads.Should().Be(0);
        fixture.Proxy.Saves.Should().Be(0);
        fixture.Session.UsedBytes.Should().Be(0);
        fixture.Session.Revision.Should().Be(1);
    }

    [Theory]
    [InlineData(0L, 100)]
    [InlineData(20L, 30)]
    [InlineData(99L, 1)]
    [InlineData(0L, 1)]
    public async Task Valid_range_including_exact_end_retains_plaintext_and_accounting(long offset, int length)
    {
        var fixture = new RangeFixture();
        using var output = new MemoryStream();

        (await fixture.CopyAsync(offset, length, output)).Should().Be(length);
        output.ToArray().Should().Equal(fixture.Content.Skip((int)offset).Take(length));
        fixture.Keys.Unwraps.Should().Be(1);
        fixture.Store.Reads.Should().Be(1);
        fixture.Proxy.Saves.Should().Be(1);
        fixture.Session.UsedBytes.Should().Be(length);
        fixture.Session.Revision.Should().Be(2);
        fixture.Operations.Should().ContainSingle().Which.BytesTransferred.Should().Be(length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t\r\n")]
    public Task Invalid_range_request_identity_is_rejected_before_content_or_accounting(string? requestId)
        => AssertInvalidRangeRequestIdentityAsync(requestId!);

    [Fact]
    public Task Oversized_range_request_identity_is_rejected_before_content_or_accounting()
        => AssertInvalidRangeRequestIdentityAsync(new string('r', 201));

    private static async Task AssertInvalidRangeRequestIdentityAsync(string requestId)
    {
        var fixture = new RangeFixture();
        using var output = new MemoryStream();

        await FluentActions.Awaiting(() => fixture.CopyAsync(0, 1, output, requestId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Transfer request identity is required and must not exceed 200 characters.");
        fixture.Keys.Unwraps.Should().Be(0);
        fixture.Store.Reads.Should().Be(0);
        output.Length.Should().Be(0);
        fixture.Proxy.OperationReads.Should().Be(0);
        fixture.Proxy.Saves.Should().Be(0);
        fixture.Operations.Should().BeEmpty();
        fixture.Session.UsedBytes.Should().Be(0);
        fixture.Session.Revision.Should().Be(1);
        fixture.Object.State.Should().Be(ManagedObjectState.Ready);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(200)]
    public async Task Valid_range_request_identity_boundaries_preserve_delivery_and_accounting(int identityLength)
    {
        var fixture = new RangeFixture();
        using var output = new MemoryStream();
        var requestId = new string('r', identityLength);

        (await fixture.CopyAsync(0, 1, output, requestId)).Should().Be(1);
        output.ToArray().Should().Equal(fixture.Content.Take(1));
        fixture.Keys.Unwraps.Should().Be(1);
        fixture.Store.Reads.Should().Be(1);
        fixture.Proxy.Saves.Should().Be(1);
        fixture.Session.UsedBytes.Should().Be(1);
        fixture.Session.Revision.Should().Be(2);
        fixture.Operations.Should().ContainSingle().Which.RequestId.Should().Be(requestId);
    }

    [Fact]
    public async Task Range_identity_keeps_existing_trimmed_length_and_stored_identity_rules()
    {
        var fixture = new RangeFixture();
        using var output = new MemoryStream();
        var trimmedId = new string('r', 200);

        (await fixture.CopyAsync(0, 1, output, " " + trimmedId + " ")).Should().Be(1);
        output.ToArray().Should().Equal(fixture.Content.Take(1));
        fixture.Operations.Should().ContainSingle().Which.RequestId.Should().Be(trimmedId);
        fixture.Proxy.Saves.Should().Be(1);
        fixture.Session.UsedBytes.Should().Be(1);
        fixture.Session.Revision.Should().Be(2);
    }

    [Theory]
    [InlineData("consumed-range")]
    [InlineData(" consumed-range ")]
    [InlineData("\tconsumed-range\r\n")]
    public async Task Same_session_consumed_range_identity_is_rejected_before_content_or_accounting(string requestId)
    {
        var fixture = new RangeFixture();
        var original = new ManagedTransferOperationRecord
        {
            SessionId = fixture.Session.Id,
            RequestId = "consumed-range",
            RequestHash = "synthetic-prior-range-hash",
            BytesTransferred = 7,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        fixture.Operations.Add(original);
        using var output = new MemoryStream();

        await FluentActions.Awaiting(() => fixture.CopyAsync(0, 1, output, requestId))
            .Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Download request identity has already been consumed.");
        fixture.Keys.Unwraps.Should().Be(0);
        fixture.Store.Reads.Should().Be(0);
        output.Length.Should().Be(0);
        fixture.Proxy.OperationReads.Should().Be(1);
        fixture.Proxy.Saves.Should().Be(0);
        fixture.Session.UsedBytes.Should().Be(0);
        fixture.Session.Revision.Should().Be(1);
        fixture.Object.State.Should().Be(ManagedObjectState.Ready);
        fixture.Operations.Should().ContainSingle().Which.Should().BeSameAs(original);
        original.RequestId.Should().Be("consumed-range");
        original.RequestHash.Should().Be("synthetic-prior-range-hash");
        original.BytesTransferred.Should().Be(7);
    }

    [Fact]
    public async Task Range_identity_does_not_gain_case_folding()
    {
        var fixture = new RangeFixture();
        fixture.Operations.Add(new ManagedTransferOperationRecord
        {
            SessionId = fixture.Session.Id,
            RequestId = "consumed-range"
        });
        using var output = new MemoryStream();

        (await fixture.CopyAsync(0, 1, output, "CONSUMED-RANGE")).Should().Be(1);
        output.ToArray().Should().Equal(fixture.Content.Take(1));
        fixture.Keys.Unwraps.Should().Be(1);
        fixture.Store.Reads.Should().Be(1);
        fixture.Proxy.Saves.Should().Be(1);
        fixture.Session.UsedBytes.Should().Be(1);
        fixture.Session.Revision.Should().Be(2);
        fixture.Operations.Should().HaveCount(2);
        fixture.Operations[1].SessionId.Should().Be(fixture.Session.Id);
        fixture.Operations[1].RequestId.Should().Be("CONSUMED-RANGE");
    }

    [Fact]
    public async Task Consumed_range_identity_in_other_session_does_not_block_current_session()
    {
        var fixture = new RangeFixture();
        fixture.Operations.Add(new ManagedTransferOperationRecord
        {
            SessionId = Guid.NewGuid(),
            RequestId = "consumed-range"
        });
        using var output = new MemoryStream();

        (await fixture.CopyAsync(0, 1, output, " consumed-range ")).Should().Be(1);
        output.ToArray().Should().Equal(fixture.Content.Take(1));
        fixture.Keys.Unwraps.Should().Be(1);
        fixture.Store.Reads.Should().Be(1);
        fixture.Proxy.Saves.Should().Be(1);
        fixture.Session.UsedBytes.Should().Be(1);
        fixture.Session.Revision.Should().Be(2);
        fixture.Operations.Should().HaveCount(2);
        fixture.Operations[1].SessionId.Should().Be(fixture.Session.Id);
        fixture.Operations[1].RequestId.Should().Be("consumed-range");
    }

    // These seeded current-revision records isolate identity lookup, not historical commit recovery.
    // They do not prove a real post-commit retry can pass the unchanged revision/state gates.
    [Theory]
    [InlineData(false, "lookup-id")]
    [InlineData(false, " \tlookup-id\r\n ")]
    [InlineData(true, "lookup-id")]
    [InlineData(true, " \tlookup-id\r\n ")]
    public async Task Upload_and_complete_lookup_uses_existing_trimmed_storage_identity(bool complete, string requestId)
    {
        var fixture = new RangeFixture(ManagedTransferOperation.Upload);
        var original = fixture.AddUploadLookupRecord(complete, matchingHash: true);

        var result = await fixture.UploadAsync(complete, requestId);

        result.Should().Be(new ManagedTransferMutationResult(fixture.Session.Id, 1, original.BytesTransferred, true));
        AssertUploadLookupHasNoWrites(fixture, original);
        fixture.Proxy.OperationReads.Should().Be(1);
    }

    [Theory]
    [InlineData(false, "lookup-id")]
    [InlineData(false, " lookup-id ")]
    [InlineData(true, "lookup-id")]
    [InlineData(true, " lookup-id ")]
    public async Task Upload_and_complete_identity_lookup_keeps_original_hash_mismatch_rejection(bool complete, string requestId)
    {
        var fixture = new RangeFixture(ManagedTransferOperation.Upload);
        var original = fixture.AddUploadLookupRecord(complete, matchingHash: false);

        await FluentActions.Awaiting(() => fixture.UploadAsync(complete, requestId))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Transfer request identity replay does not match the original operation.");
        AssertUploadLookupHasNoWrites(fixture, original);
        fixture.Proxy.OperationReads.Should().Be(1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identity_lookup_does_not_bypass_expected_revision_gate(bool complete)
    {
        var fixture = new RangeFixture(ManagedTransferOperation.Upload);
        var original = fixture.AddUploadLookupRecord(complete, matchingHash: true);

        await FluentActions.Awaiting(() => fixture.UploadAsync(complete, " lookup-id ", fixture.Session.Revision + 1))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Transfer capability revision is stale.");
        AssertUploadLookupHasNoWrites(fixture, original);
        fixture.Proxy.OperationReads.Should().Be(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Identity_lookup_does_not_enable_completed_session_replay(bool complete)
    {
        var fixture = new RangeFixture(ManagedTransferOperation.Upload);
        var original = fixture.AddUploadLookupRecord(complete, matchingHash: true);
        fixture.Session.State = ManagedTransferSessionState.Completed;

        await FluentActions.Awaiting(() => fixture.UploadAsync(complete, " lookup-id "))
            .Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("Transfer capability is not active.");
        AssertUploadLookupHasNoWrites(fixture, original);
        fixture.Session.State.Should().Be(ManagedTransferSessionState.Completed);
        fixture.Proxy.OperationReads.Should().Be(0);
    }

    private static void AssertUploadLookupHasNoWrites(RangeFixture fixture, ManagedTransferOperationRecord original)
    {
        fixture.Keys.Unwraps.Should().Be(0);
        fixture.Store.Reads.Should().Be(0);
        fixture.Proxy.Saves.Should().Be(0);
        fixture.Operations.Should().ContainSingle().Which.Should().BeSameAs(original);
        original.RequestId.Should().Be("lookup-id");
        fixture.Session.UsedBytes.Should().Be(0);
        fixture.Session.Revision.Should().Be(1);
        fixture.Object.State.Should().Be(ManagedObjectState.Ready);
    }

    // Exercises the production service with a valid capability and actual AES-GCM content.
    // Only persistence/transaction boundaries are in memory; no database or host service is used.
    private sealed class RangeFixture
    {
        private const string Bearer = "synthetic-range-capability";
        public byte[] Content { get; } = Enumerable.Range(0, 100).Select(value => (byte)value).ToArray();
        public ManagedObject Object { get; }
        public ManagedTransferSession Session { get; }
        public List<ManagedTransferOperationRecord> Operations { get; } = [];
        public RangeDbProxy Proxy { get; }
        public RangeKeys Keys { get; }
        public RangeStore Store { get; }
        private readonly ManagedTransferService service;

        public RangeFixture(ManagedTransferOperation operation = ManagedTransferOperation.Download)
        {
            var actor = new RequestActorAccessor
            {
                Current = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic-range-reader", TenantUserRole.Member,
                    [operation == ManagedTransferOperation.Upload ? SecurityScopes.MemoryWrite : SecurityScopes.MemoryRead], ["ContextHub"], true)
            };
            var authority = new AesGcmManagedFileKeyAuthority(Options.Create(new ManagedFileKeyAuthorityOptions
            {
                CurrentKeyId = "synthetic-range-key",
                Keys = { ["synthetic-range-key"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)) }
            }));
            Keys = new RangeKeys(authority);
            Object = new ManagedObject
            {
                TenantId = actor.Current.TenantId,
                OwnerUserId = actor.Current.UserId,
                ProjectId = "ContextHub",
                State = ManagedObjectState.Ready,
                PlaintextLength = Content.Length,
                ChunkSize = 65536,
                ChunkCount = 1,
                StorageId = "synthetic-range-object"
            };
            var dek = RandomNumberGenerator.GetBytes(32);
            var wrapped = authority.Wrap(Object.Id, Object.EncryptionGeneration, dek);
            Object.KeyId = wrapped.KeyId;
            Object.WrappedDek = wrapped.Ciphertext;
            Object.WrapNonce = wrapped.Nonce;
            Object.WrapTag = wrapped.Tag;
            var nonce = RandomNumberGenerator.GetBytes(12);
            var tag = new byte[16];
            var ciphertext = new byte[Content.Length];
            var aad = Encoding.UTF8.GetBytes(string.Join('|', "ContextHub.ManagedFile", Object.EncryptionSchemaVersion,
                Object.TenantId, Object.ProjectId, Object.Id, Object.EncryptionGeneration, 0, Content.Length));
            try
            {
                using var aes = new AesGcm(dek, 16);
                aes.Encrypt(nonce, Content, ciphertext, tag, aad);
            }
            finally { CryptographicOperations.ZeroMemory(dek); }
            Object.Chunks.Add(new ManagedObjectChunk
            {
                ManagedObjectId = Object.Id,
                ChunkIndex = 0,
                PlaintextOffset = 0,
                PlaintextLength = Content.Length,
                CiphertextLength = ciphertext.Length,
                Nonce = nonce,
                AuthenticationTag = tag,
                CiphertextSha256 = Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant()
            });
            Store = new RangeStore(ciphertext);
            Session = new ManagedTransferSession
            {
                ManagedObjectId = Object.Id,
                ManagedObject = Object,
                TenantId = actor.Current.TenantId,
                OwnerUserId = actor.Current.UserId,
                ProjectId = "ContextHub",
                Operation = operation,
                CapabilityHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Bearer))).ToLowerInvariant(),
                EncryptionGeneration = Object.EncryptionGeneration,
                MaxBytes = Content.Length,
                MaxBytesPerSecond = 65536,
                ExpiresAt = DateTimeOffset.UnixEpoch.AddMinutes(1),
                CreatedAt = DateTimeOffset.UnixEpoch
            };
            var db = DispatchProxy.Create<IApplicationDbContext, RangeDbProxy>();
            Proxy = (RangeDbProxy)(object)db;
            Proxy.SessionSet = new RangeSet<ManagedTransferSession>([Session]);
            Proxy.OperationSet = new RangeSet<ManagedTransferOperationRecord>(Operations);
            service = new ManagedTransferService(db, actor, new RangeClock(), Store, Keys, null!,
                Options.Create(new ManagedTransferOptions { MaxRangeBytes = 65536 }));
        }

        public ManagedTransferOperationRecord AddUploadLookupRecord(bool complete, bool matchingHash)
        {
            var contentHash = Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant();
            var values = complete
                ? new[] { "complete", Session.Id.ToString("D"), Session.Revision.ToString() }
                : new[] { "upload", Session.Id.ToString("D"), Session.Revision.ToString(), "0", contentHash };
            var operation = new ManagedTransferOperationRecord
            {
                SessionId = Session.Id,
                RequestId = "lookup-id",
                RequestHash = matchingHash
                    ? Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', values)))).ToLowerInvariant()
                    : new string('0', 64),
                BytesTransferred = complete ? 0 : Content.Length,
                CreatedAt = DateTimeOffset.UnixEpoch
            };
            Operations.Add(operation);
            return operation;
        }

        public Task<ManagedTransferMutationResult> UploadAsync(bool complete, string requestId, long? expectedRevision = null)
            => complete
                ? service.CompleteUploadAsync(Session.Id, Bearer, expectedRevision ?? Session.Revision, requestId, default)
                : service.UploadChunkAsync(new(Session.Id, Bearer, expectedRevision ?? Session.Revision, requestId, 0,
                    Content.ToArray(), Convert.ToHexString(SHA256.HashData(Content)).ToLowerInvariant()), default);

        public Task<long> CopyAsync(long offset, int length, Stream output, string requestId = "synthetic-range-request")
            => service.CopyRangeAsync(new(Session.Id, Bearer, Session.Revision, requestId, offset, length), output, default);
    }

    public class RangeDbProxy : DispatchProxy
    {
        public DbSet<ManagedTransferSession> SessionSet { get; set; } = null!;
        public DbSet<ManagedTransferOperationRecord> OperationSet { get; set; } = null!;
        public int OperationReads { get; private set; }
        public int Saves { get; private set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            switch (method!.Name)
            {
                case "get_ManagedTransferSessions": return SessionSet;
                case "get_ManagedTransferOperationRecords": OperationReads++; return OperationSet;
                case "AcquireTransactionLockAsync": return Task.CompletedTask;
                case "ExecuteInTransactionAsync":
                    return args![0] switch
                    {
                        Func<CancellationToken, Task<long>> range => range((CancellationToken)args[1]!),
                        Func<CancellationToken, Task<ManagedTransferMutationResult>> mutation => mutation((CancellationToken)args[1]!),
                        _ => throw new NotSupportedException("Unexpected synthetic transaction result type.")
                    };
                case "SaveChangesAsync": Saves++; return Task.FromResult(1);
                default: throw new NotSupportedException(method.Name);
            }
        }
    }

    private sealed class RangeKeys(IManagedFileKeyAuthority inner) : IManagedFileKeyAuthority
    {
        public int Unwraps { get; private set; }
        public byte[] Unwrap(Guid id, int generation, WrappedManagedKey key) { Unwraps++; return inner.Unwrap(id, generation, key); }
        public WrappedManagedKey Wrap(Guid id, int generation, ReadOnlySpan<byte> key) => throw new NotSupportedException();
        public WrappedManagedKey Rewrap(Guid id, int generation, WrappedManagedKey key) => throw new NotSupportedException();
    }

    private sealed class RangeStore(byte[] ciphertext) : IManagedObjectStore
    {
        public int Reads { get; private set; }
        public Task<int> ReadChunkAsync(string id, int index, Memory<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Reads++;
            ciphertext.AsMemory().CopyTo(destination);
            return Task.FromResult(ciphertext.Length);
        }
        public Task PutChunkAsync(string id, int index, ReadOnlyMemory<byte> value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> ChunkExistsAsync(string id, int index, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task DeleteObjectAsync(string id, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class RangeClock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }

    private sealed class RangeSet<T>(List<T> values) : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T> where T : class
    {
        private readonly RangeQuery<T> query = new(values);
        public override Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType => throw new NotSupportedException();
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => ((IQueryable<T>)query).Expression;
        IQueryProvider IQueryable.Provider => new RangeProvider(query);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();
        public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default) => new RangeEnumerator<T>(values.GetEnumerator(), ct);
        public override ValueTask<EntityEntry<T>> AddAsync(T entity, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); values.Add(entity); return ValueTask.FromResult<EntityEntry<T>>(null!); }
    }

    private sealed class RangeQuery<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        public RangeQuery(IEnumerable<T> values) : base(values) { }
        public RangeQuery(Expression expression) : base(expression) { }
        IQueryProvider IQueryable.Provider => new RangeProvider(this);
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default) => new RangeEnumerator<T>(this.AsEnumerable().GetEnumerator(), ct);
    }

    private sealed class RangeProperties : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(EF) || node.Method.Name != nameof(EF.Property)) return base.VisitMethodCall(node);
            var target = Visit(node.Arguments[0]);
            while (target is UnaryExpression { NodeType: ExpressionType.Convert } conversion) target = conversion.Operand;
            return Expression.Property(target, (string)((ConstantExpression)node.Arguments[1]).Value!);
        }
    }

    private sealed class RangeProvider(IQueryProvider inner) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => (IQueryable)Activator.CreateInstance(typeof(RangeQuery<>).MakeGenericType(expression.Type.GetGenericArguments()[0]), new RangeProperties().Visit(expression))!;
        public IQueryable<T> CreateQuery<T>(Expression expression) => new RangeQuery<T>(new RangeProperties().Visit(expression));
        public object? Execute(Expression expression) => inner.Execute(new RangeProperties().Visit(expression));
        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(new RangeProperties().Visit(expression));
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var resultType = typeof(TResult).GetGenericArguments()[0];
            var execute = typeof(IQueryProvider).GetMethods().Single(method => method.Name == nameof(Execute) && method.IsGenericMethod);
            var result = execute.MakeGenericMethod(resultType).Invoke(inner, [new RangeProperties().Visit(expression)]);
            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result])!;
        }
    }

    private sealed class RangeEnumerator<T>(IEnumerator<T> inner, CancellationToken ct) : IAsyncEnumerator<T>
    {
        public T Current => inner.Current;
        public ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
        public ValueTask<bool> MoveNextAsync() { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(inner.MoveNext()); }
    }
}

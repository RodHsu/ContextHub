using System.Buffers;
using System.Security.Cryptography;
using System.Text;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Application;

public sealed class ManagedTransferOptions
{
    public const string SectionName = "ManagedTransfers";
    public int ChunkBytes { get; set; } = 4 * 1024 * 1024;
    public long MaxObjectBytes { get; set; } = 5L * 1024 * 1024 * 1024;
    public int MaxRangeBytes { get; set; } = 16 * 1024 * 1024;
    public int SessionTtlMinutes { get; set; } = 15;
    public int StagedObjectTtlHours { get; set; } = 24;
    public int MaxConcurrency { get; set; } = 1;
    public int ReconciliationIntervalMinutes { get; set; } = 30;

    public int NormalizedChunkBytes => Math.Clamp(ChunkBytes, 64 * 1024, 16 * 1024 * 1024);
    public long NormalizedMaxObjectBytes => Math.Clamp(MaxObjectBytes, NormalizedChunkBytes, 5L * 1024 * 1024 * 1024 * 1024);
    public int NormalizedMaxRangeBytes => Math.Clamp(MaxRangeBytes, 64 * 1024, 64 * 1024 * 1024);
    public TimeSpan NormalizedSessionTtl => TimeSpan.FromMinutes(Math.Clamp(SessionTtlMinutes, 1, 60));
    public TimeSpan NormalizedStagedObjectTtl => TimeSpan.FromHours(Math.Clamp(StagedObjectTtlHours, 1, 168));
    public int NormalizedMaxConcurrency => Math.Clamp(MaxConcurrency, 1, 8);
}

public sealed record ManagedObjectRef(Guid ObjectId, int Generation);

public sealed record ManagedTransferSessionResult(
    Guid SessionId,
    Guid CapabilityId,
    string Capability,
    ManagedObjectRef Object,
    ManagedTransferOperation Operation,
    long Revision,
    int ChunkBytes,
    long MaxBytes,
    DateTimeOffset ExpiresAt,
    string ResourcePath);

public sealed record CreateManagedUploadRequest(
    string ProjectId,
    long PlaintextLength,
    string Purpose,
    string IdempotencyKey,
    string? AgentId = null,
    Guid? ExecutionId = null);

public sealed record CreateManagedDownloadRequest(
    string ProjectId,
    Guid ObjectId,
    string Purpose,
    string IdempotencyKey,
    string? AgentId = null,
    Guid? ExecutionId = null);

public sealed record ManagedChunkWriteRequest(
    Guid SessionId,
    string Capability,
    long ExpectedRevision,
    string RequestId,
    int ChunkIndex,
    byte[] Plaintext,
    string PlaintextSha256);

public sealed record ManagedTransferMutationResult(Guid SessionId, long Revision, long BytesTransferred, bool Replayed);

public sealed record ManagedRangeReadRequest(
    Guid SessionId,
    string Capability,
    long ExpectedRevision,
    string RequestId,
    long Offset,
    int Length);

public sealed record WrappedManagedKey(string KeyId, byte[] Ciphertext, byte[] Nonce, byte[] Tag);

public interface IManagedFileKeyAuthority
{
    WrappedManagedKey Wrap(Guid objectId, int generation, ReadOnlySpan<byte> plaintextKey);
    byte[] Unwrap(Guid objectId, int generation, WrappedManagedKey wrappedKey);
    WrappedManagedKey Rewrap(Guid objectId, int generation, WrappedManagedKey wrappedKey);
}

public interface IManagedObjectStore
{
    Task PutChunkAsync(string storageId, int chunkIndex, ReadOnlyMemory<byte> ciphertext, CancellationToken cancellationToken);
    Task<int> ReadChunkAsync(string storageId, int chunkIndex, Memory<byte> destination, CancellationToken cancellationToken);
    Task<bool> ChunkExistsAsync(string storageId, int chunkIndex, CancellationToken cancellationToken);
    Task DeleteObjectAsync(string storageId, CancellationToken cancellationToken);
}

public interface IManagedTransferService
{
    Task<ManagedTransferSessionResult> CreateUploadAsync(CreateManagedUploadRequest request, CancellationToken cancellationToken);
    Task<ManagedTransferSessionResult> CreateDownloadAsync(CreateManagedDownloadRequest request, CancellationToken cancellationToken);
    Task<ManagedTransferMutationResult> UploadChunkAsync(ManagedChunkWriteRequest request, CancellationToken cancellationToken);
    Task<ManagedTransferMutationResult> CompleteUploadAsync(Guid sessionId, string capability, long expectedRevision, string requestId, CancellationToken cancellationToken);
    Task<long> CopyRangeAsync(ManagedRangeReadRequest request, Stream destination, CancellationToken cancellationToken);
    Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken);
    Task RewrapObjectKeyAsync(Guid objectId, CancellationToken cancellationToken);
}

public sealed class ManagedTransferService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock,
    IManagedObjectStore objectStore,
    IManagedFileKeyAuthority keyAuthority,
    IOptions<ManagedTransferOptions> options) : IManagedTransferService
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;
    private const int DekBytes = 32;

    public async Task<ManagedTransferSessionResult> CreateUploadAsync(CreateManagedUploadRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var actor = RequireActor(projectId, write: true);
        ValidatePurposeAndIdempotency(request.Purpose, request.IdempotencyKey);
        if (request.PlaintextLength <= 0 || request.PlaintextLength > settings.NormalizedMaxObjectBytes)
        {
            throw new InvalidOperationException("Managed object length is outside the configured transfer limit.");
        }

        var existing = await Scope(dbContext.ManagedTransferSessions.AsNoTracking(), actor)
            .Where(x => x.Operation == ManagedTransferOperation.Upload && x.ProjectId == projectId && x.Purpose == request.Purpose)
            .Join(dbContext.ManagedTransferOperationRecords, session => session.Id, operation => operation.SessionId, (session, operation) => new { session, operation })
            .SingleOrDefaultAsync(x => x.operation.RequestId == request.IdempotencyKey, cancellationToken);
        if (existing is not null)
        {
            throw new InvalidOperationException("The upload idempotency identity has already been consumed.");
        }

        var now = clock.UtcNow;
        var objectId = Guid.NewGuid();
        var dek = RandomNumberGenerator.GetBytes(DekBytes);
        try
        {
            var wrapped = keyAuthority.Wrap(objectId, 1, dek);
            var managedObject = new ManagedObject
            {
                Id = objectId,
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                StorageId = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
                PlaintextLength = request.PlaintextLength,
                ChunkSize = settings.NormalizedChunkBytes,
                ChunkCount = checked((int)((request.PlaintextLength + settings.NormalizedChunkBytes - 1) / settings.NormalizedChunkBytes)),
                KeyId = wrapped.KeyId,
                WrappedDek = wrapped.Ciphertext,
                WrapNonce = wrapped.Nonce,
                WrapTag = wrapped.Tag,
                StagedUntil = now + settings.NormalizedStagedObjectTtl,
                CreatedAt = now,
                UpdatedAt = now
            };
            var (session, token) = CreateSession(managedObject, actor, request.Purpose, request.AgentId, request.ExecutionId, ManagedTransferOperation.Upload, request.PlaintextLength, now, settings);
            session.Operations.Add(new ManagedTransferOperationRecord
            {
                RequestId = request.IdempotencyKey,
                RequestHash = HashRequest("create-upload", projectId, request.PlaintextLength.ToString(), request.Purpose),
                CreatedAt = now
            });
            await dbContext.ManagedObjects.AddAsync(managedObject, cancellationToken);
            await dbContext.ManagedTransferSessions.AddAsync(session, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
            return Result(session, managedObject, token, settings.NormalizedChunkBytes);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<ManagedTransferSessionResult> CreateDownloadAsync(CreateManagedDownloadRequest request, CancellationToken cancellationToken)
    {
        var settings = options.Value;
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var actor = RequireActor(projectId, write: false);
        ValidatePurposeAndIdempotency(request.Purpose, request.IdempotencyKey);
        var managedObject = await Scope(dbContext.ManagedObjects, actor)
            .SingleOrDefaultAsync(x => x.Id == request.ObjectId && x.ProjectId == projectId, cancellationToken)
            ?? throw new KeyNotFoundException("Managed object was not found.");
        if (managedObject.State != ManagedObjectState.Ready)
        {
            throw new InvalidOperationException("Managed object is not available for transfer.");
        }

        var now = clock.UtcNow;
        var (session, token) = CreateSession(managedObject, actor, request.Purpose, request.AgentId, request.ExecutionId, ManagedTransferOperation.Download, managedObject.PlaintextLength, now, settings);
        session.Operations.Add(new ManagedTransferOperationRecord
        {
            RequestId = request.IdempotencyKey,
            RequestHash = HashRequest("create-download", projectId, request.ObjectId.ToString("D"), request.Purpose),
            CreatedAt = now
        });
        await dbContext.ManagedTransferSessions.AddAsync(session, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Result(session, managedObject, token, managedObject.ChunkSize);
    }

    public async Task<ManagedTransferMutationResult> UploadChunkAsync(ManagedChunkWriteRequest request, CancellationToken cancellationToken)
    {
        if (request.Plaintext is null) throw new InvalidOperationException("Chunk content is required.");
        try
        {
            return await dbContext.ExecuteInTransactionAsync(async transactionToken =>
            {
                await dbContext.AcquireTransactionLockAsync($"managed-transfer:{request.SessionId:D}", transactionToken);
                var session = await LoadSessionAsync(request.SessionId, request.Capability, ManagedTransferOperation.Upload, request.ExpectedRevision, transactionToken);
                var managedObject = session.ManagedObject!;
                if (request.ChunkIndex < 0 || request.ChunkIndex >= managedObject.ChunkCount)
                {
                    throw new InvalidOperationException("Chunk index is outside the managed object boundary.");
                }

                var expectedLength = request.ChunkIndex == managedObject.ChunkCount - 1
                    ? checked((int)(managedObject.PlaintextLength - ((long)request.ChunkIndex * managedObject.ChunkSize)))
                    : managedObject.ChunkSize;
                if (request.Plaintext.Length != expectedLength)
                {
                    throw new InvalidOperationException("Chunk length does not match the declared managed object layout.");
                }

                var suppliedHash = NormalizeSha256(request.PlaintextSha256);
                var actualHash = Convert.ToHexString(SHA256.HashData(request.Plaintext)).ToLowerInvariant();
                if (!FixedEquals(suppliedHash, actualHash)) throw new CryptographicException("Chunk integrity validation failed.");
                var requestHash = HashRequest("upload", session.Id.ToString("D"), session.Revision.ToString(), request.ChunkIndex.ToString(), actualHash);
                var replay = await CheckReplayAsync(session, request.RequestId, requestHash, transactionToken);
                if (replay is not null) return replay;
                if (managedObject.Chunks.Any(x => x.ChunkIndex == request.ChunkIndex)) throw new InvalidOperationException("Chunk already exists; use the original request identity for an idempotent retry.");

                var dek = Unwrap(managedObject);
                var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
                var ciphertext = new byte[request.Plaintext.Length];
                var tag = new byte[TagBytes];
                try
                {
                    var aad = BuildAad(managedObject, request.ChunkIndex, request.Plaintext.Length);
                    using var aes = new AesGcm(dek, TagBytes);
                    aes.Encrypt(nonce, request.Plaintext, ciphertext, tag, aad);
                    await objectStore.PutChunkAsync(managedObject.StorageId, request.ChunkIndex, ciphertext, transactionToken);
                    var chunk = new ManagedObjectChunk
                    {
                        ManagedObjectId = managedObject.Id,
                        ManagedObject = managedObject,
                        ChunkIndex = request.ChunkIndex,
                        PlaintextOffset = (long)request.ChunkIndex * managedObject.ChunkSize,
                        PlaintextLength = request.Plaintext.Length,
                        CiphertextLength = ciphertext.Length,
                        Nonce = nonce,
                        AuthenticationTag = tag,
                        PlaintextSha256 = actualHash,
                        CiphertextSha256 = Convert.ToHexString(SHA256.HashData(ciphertext)).ToLowerInvariant(),
                        CreatedAt = clock.UtcNow
                    };
                    await dbContext.ManagedObjectChunks.AddAsync(chunk, transactionToken);
                    session.UsedBytes = checked(session.UsedBytes + request.Plaintext.Length);
                    session.Revision++;
                    session.UpdatedAt = clock.UtcNow;
                    var operation = NewOperation(request.RequestId, requestHash, request.Plaintext.Length);
                    operation.SessionId = session.Id;
                    await dbContext.ManagedTransferOperationRecords.AddAsync(operation, transactionToken);
                    try
                    {
                        await dbContext.SaveChangesAsync(transactionToken);
                    }
                    catch (DbUpdateConcurrencyException exception)
                    {
                        throw new InvalidOperationException($"Managed transfer persistence conflict: {string.Join(',', exception.Entries.Select(x => $"{x.Metadata.ClrType.Name}:{x.State}"))}.", exception);
                    }
                    return new ManagedTransferMutationResult(session.Id, session.Revision, request.Plaintext.Length, false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(dek);
                    CryptographicOperations.ZeroMemory(ciphertext);
                }
            }, cancellationToken);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(request.Plaintext);
        }
    }

    public Task<ManagedTransferMutationResult> CompleteUploadAsync(Guid sessionId, string capability, long expectedRevision, string requestId, CancellationToken cancellationToken)
        => dbContext.ExecuteInTransactionAsync(async transactionToken =>
        {
            await dbContext.AcquireTransactionLockAsync($"managed-transfer:{sessionId:D}", transactionToken);
            var session = await LoadSessionAsync(sessionId, capability, ManagedTransferOperation.Upload, expectedRevision, transactionToken);
            var managedObject = session.ManagedObject!;
            var requestHash = HashRequest("complete", session.Id.ToString("D"), session.Revision.ToString());
            var replay = await CheckReplayAsync(session, requestId, requestHash, transactionToken);
            if (replay is not null) return replay;
            if (managedObject.Chunks.Count != managedObject.ChunkCount || session.UsedBytes != managedObject.PlaintextLength)
            {
                throw new InvalidOperationException("Managed upload is incomplete.");
            }

            managedObject.State = ManagedObjectState.Ready;
            managedObject.UpdatedAt = clock.UtcNow;
            session.State = ManagedTransferSessionState.Completed;
            session.Revision++;
            session.UpdatedAt = clock.UtcNow;
            var operation = NewOperation(requestId, requestHash, 0);
            operation.SessionId = session.Id;
            await dbContext.ManagedTransferOperationRecords.AddAsync(operation, transactionToken);
            await dbContext.SaveChangesAsync(transactionToken);
            return new ManagedTransferMutationResult(session.Id, session.Revision, 0, false);
        }, cancellationToken);

    public Task<long> CopyRangeAsync(ManagedRangeReadRequest request, Stream destination, CancellationToken cancellationToken)
        => dbContext.ExecuteInTransactionAsync(async transactionToken =>
        {
            await dbContext.AcquireTransactionLockAsync($"managed-transfer:{request.SessionId:D}", transactionToken);
            var session = await LoadSessionAsync(request.SessionId, request.Capability, ManagedTransferOperation.Download, request.ExpectedRevision, transactionToken);
            var managedObject = session.ManagedObject!;
            if (request.Offset < 0 || request.Length <= 0 || request.Length > options.Value.NormalizedMaxRangeBytes || request.Offset + request.Length > managedObject.PlaintextLength)
            {
                throw new InvalidOperationException("Requested range is outside the authorized managed object boundary.");
            }
            if (session.UsedBytes + request.Length > session.MaxBytes) throw new InvalidOperationException("Transfer capability byte limit exceeded.");
            var requestHash = HashRequest("download", session.Id.ToString("D"), session.Revision.ToString(), request.Offset.ToString(), request.Length.ToString());
            if (await dbContext.ManagedTransferOperationRecords.AnyAsync(x => x.SessionId == session.Id && x.RequestId == request.RequestId, transactionToken))
            {
                throw new InvalidOperationException("Download request identity has already been consumed.");
            }

            var dek = Unwrap(managedObject);
            var buffer = ArrayPool<byte>.Shared.Rent(managedObject.ChunkSize);
            var plaintext = ArrayPool<byte>.Shared.Rent(managedObject.ChunkSize);
            long copied = 0;
            try
            {
                var end = request.Offset + request.Length;
                var firstChunk = checked((int)(request.Offset / managedObject.ChunkSize));
                var lastChunk = checked((int)((end - 1) / managedObject.ChunkSize));
                for (var index = firstChunk; index <= lastChunk; index++)
                {
                    var chunk = managedObject.Chunks.SingleOrDefault(x => x.ChunkIndex == index)
                        ?? throw new CryptographicException("Managed object chunk metadata is incomplete.");
                    var read = await objectStore.ReadChunkAsync(managedObject.StorageId, index, buffer.AsMemory(0, chunk.CiphertextLength), transactionToken);
                    if (read != chunk.CiphertextLength || !FixedEquals(chunk.CiphertextSha256, Convert.ToHexString(SHA256.HashData(buffer.AsSpan(0, read))).ToLowerInvariant()))
                    {
                        managedObject.State = ManagedObjectState.Corrupt;
                        throw new CryptographicException("Managed object integrity validation failed.");
                    }
                    using var aes = new AesGcm(dek, TagBytes);
                    aes.Decrypt(chunk.Nonce, buffer.AsSpan(0, read), chunk.AuthenticationTag, plaintext.AsSpan(0, chunk.PlaintextLength), BuildAad(managedObject, index, chunk.PlaintextLength));
                    var chunkStart = chunk.PlaintextOffset;
                    var copyStart = Math.Max(request.Offset, chunkStart);
                    var copyEnd = Math.Min(end, chunkStart + chunk.PlaintextLength);
                    var sourceOffset = checked((int)(copyStart - chunkStart));
                    var count = checked((int)(copyEnd - copyStart));
                    await destination.WriteAsync(plaintext.AsMemory(sourceOffset, count), transactionToken);
                    copied += count;
                    CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, chunk.PlaintextLength));
                }
                session.UsedBytes = checked(session.UsedBytes + copied);
                session.Revision++;
                session.UpdatedAt = clock.UtcNow;
                var operation = NewOperation(request.RequestId, requestHash, copied);
                operation.SessionId = session.Id;
                await dbContext.ManagedTransferOperationRecords.AddAsync(operation, transactionToken);
                await dbContext.SaveChangesAsync(transactionToken);
                return copied;
            }
            catch (FileNotFoundException)
            {
                managedObject.State = ManagedObjectState.Missing;
                throw new InvalidOperationException("Managed object content is unavailable.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(dek);
                CryptographicOperations.ZeroMemory(buffer);
                CryptographicOperations.ZeroMemory(plaintext);
                ArrayPool<byte>.Shared.Return(buffer);
                ArrayPool<byte>.Shared.Return(plaintext);
            }
        }, cancellationToken);

    public async Task RevokeAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var session = await Scope(dbContext.ManagedTransferSessions, actor).SingleOrDefaultAsync(x => x.Id == sessionId, cancellationToken)
            ?? throw new KeyNotFoundException("Transfer session was not found.");
        ActorAuthorization.EnsureProjectAllowed(actor, session.ProjectId, write: true);
        session.State = ManagedTransferSessionState.Revoked;
        session.Revision++;
        session.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    public async Task RewrapObjectKeyAsync(Guid objectId, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureAdminOrScopeAllowed(actor, SecurityScopes.SecurityManage);
        var managedObject = await Scope(dbContext.ManagedObjects, actor).SingleOrDefaultAsync(x => x.Id == objectId, cancellationToken)
            ?? throw new KeyNotFoundException("Managed object was not found.");
        var rewrapped = keyAuthority.Rewrap(managedObject.Id, managedObject.EncryptionGeneration, Wrapped(managedObject));
        managedObject.KeyId = rewrapped.KeyId;
        managedObject.WrappedDek = rewrapped.Ciphertext;
        managedObject.WrapNonce = rewrapped.Nonce;
        managedObject.WrapTag = rewrapped.Tag;
        managedObject.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private ContextHubRequestActor RequireActor(string projectId, bool write)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, write ? SecurityScopes.MemoryWrite : SecurityScopes.MemoryRead);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write);
        return actor;
    }

    private async Task<ManagedTransferSession> LoadSessionAsync(Guid id, string capability, ManagedTransferOperation operation, long expectedRevision, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, operation == ManagedTransferOperation.Upload ? SecurityScopes.MemoryWrite : SecurityScopes.MemoryRead);
        var session = await Scope(dbContext.ManagedTransferSessions, actor)
            .Include(x => x.ManagedObject)!.ThenInclude(x => x!.Chunks)
            .Include(x => x.Operations)
            .SingleOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new UnauthorizedAccessException("Transfer capability is invalid.");
        ActorAuthorization.EnsureProjectAllowed(actor, session.ProjectId, operation == ManagedTransferOperation.Upload);
        if (session.Operation != operation || session.State != ManagedTransferSessionState.Active || session.ExpiresAt <= clock.UtcNow)
        {
            throw new UnauthorizedAccessException("Transfer capability is not active.");
        }
        if (session.Revision != expectedRevision || session.EncryptionGeneration != session.ManagedObject!.EncryptionGeneration)
        {
            throw new UnauthorizedAccessException("Transfer capability revision is stale.");
        }
        var tokenHash = HashCapability(capability);
        if (!FixedEquals(session.CapabilityHash, tokenHash)) throw new UnauthorizedAccessException("Transfer capability is invalid.");
        return session;
    }

    private static (ManagedTransferSession Session, string Token) CreateSession(
        ManagedObject managedObject,
        ContextHubRequestActor actor,
        string purpose,
        string? agentId,
        Guid? executionId,
        ManagedTransferOperation operation,
        long maxBytes,
        DateTimeOffset now,
        ManagedTransferOptions settings)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        return (new ManagedTransferSession
        {
            ManagedObjectId = managedObject.Id,
            ManagedObject = managedObject,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = managedObject.ProjectId,
            ActorId = actor.UserId?.ToString("D") ?? throw new UnauthorizedAccessException("A tenant actor is required."),
            AgentId = string.IsNullOrWhiteSpace(agentId) ? null : agentId.Trim(),
            ExecutionId = executionId,
            CapabilityHash = HashCapability(token),
            Operation = operation,
            Purpose = purpose.Trim(),
            MaxBytes = maxBytes,
            MaxConcurrency = settings.NormalizedMaxConcurrency,
            EncryptionGeneration = managedObject.EncryptionGeneration,
            ExpiresAt = now + settings.NormalizedSessionTtl,
            CreatedAt = now,
            UpdatedAt = now
        }, token);
    }

    private static ManagedTransferSessionResult Result(ManagedTransferSession session, ManagedObject managedObject, string token, int chunkBytes)
        => new(session.Id, session.CapabilityId, token, new ManagedObjectRef(managedObject.Id, managedObject.EncryptionGeneration), session.Operation, session.Revision, chunkBytes, session.MaxBytes, session.ExpiresAt, $"/api/transfers/{session.Id:D}");

    private async Task<ManagedTransferMutationResult?> CheckReplayAsync(ManagedTransferSession session, string requestId, string requestHash, CancellationToken cancellationToken)
    {
        ValidateRequestId(requestId);
        var existing = await dbContext.ManagedTransferOperationRecords.AsNoTracking().SingleOrDefaultAsync(x => x.SessionId == session.Id && x.RequestId == requestId, cancellationToken);
        if (existing is null) return null;
        if (!FixedEquals(existing.RequestHash, requestHash)) throw new UnauthorizedAccessException("Transfer request identity replay does not match the original operation.");
        return new ManagedTransferMutationResult(session.Id, session.Revision, existing.BytesTransferred, true);
    }

    private ManagedTransferOperationRecord NewOperation(string requestId, string requestHash, long bytes)
    {
        ValidateRequestId(requestId);
        return new ManagedTransferOperationRecord { RequestId = requestId.Trim(), RequestHash = requestHash, BytesTransferred = bytes, CreatedAt = clock.UtcNow };
    }

    private byte[] Unwrap(ManagedObject managedObject)
        => keyAuthority.Unwrap(managedObject.Id, managedObject.EncryptionGeneration, Wrapped(managedObject));

    private static WrappedManagedKey Wrapped(ManagedObject managedObject)
        => new(managedObject.KeyId, managedObject.WrappedDek, managedObject.WrapNonce, managedObject.WrapTag);

    private static byte[] BuildAad(ManagedObject value, int chunkIndex, int plaintextLength)
        => Encoding.UTF8.GetBytes(string.Join('|', "ContextHub.ManagedFile", value.EncryptionSchemaVersion, value.TenantId, value.ProjectId, value.Id, value.EncryptionGeneration, chunkIndex, plaintextLength));

    private static string HashCapability(string capability)
    {
        if (string.IsNullOrWhiteSpace(capability)) throw new UnauthorizedAccessException("Transfer capability is required.");
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(capability))).ToLowerInvariant();
    }

    private static string HashRequest(params string[] values)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('|', values)))).ToLowerInvariant();

    private static bool FixedEquals(string left, string right)
    {
        var a = Encoding.ASCII.GetBytes(left);
        var b = Encoding.ASCII.GetBytes(right);
        return a.Length == b.Length && CryptographicOperations.FixedTimeEquals(a, b);
    }

    private static string NormalizeSha256(string value)
    {
        var normalized = value?.Trim().ToLowerInvariant() ?? string.Empty;
        if (normalized.Length != 64 || normalized.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidOperationException("A lowercase or uppercase SHA-256 checksum is required.");
        return normalized;
    }

    private static void ValidatePurposeAndIdempotency(string purpose, string idempotencyKey)
    {
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Trim().Length > 200) throw new InvalidOperationException("Transfer purpose is required and must not exceed 200 characters.");
        ValidateRequestId(idempotencyKey);
    }

    private static void ValidateRequestId(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length > 200) throw new InvalidOperationException("Transfer request identity is required and must not exceed 200 characters.");
    }

    private static IQueryable<T> Scope<T>(IQueryable<T> query, ContextHubRequestActor actor) where T : class
        => !actor.HasUser ? query : actor.IsServiceActor
            ? query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId)
            : query.Where(x => EF.Property<Guid?>(x, "TenantId") == actor.TenantId && EF.Property<Guid?>(x, "OwnerUserId") == actor.UserId);
}

public interface IManagedObjectReconciliationService
{
    Task<ManagedObjectReconciliationResult> RunAsync(CancellationToken cancellationToken);
}

public sealed record ManagedObjectReconciliationResult(int Orphaned, int Missing, int Checked);

public sealed class ManagedObjectReconciliationService(
    IApplicationDbContext dbContext,
    IManagedObjectStore objectStore,
    IClock clock) : IManagedObjectReconciliationService
{
    public async Task<ManagedObjectReconciliationResult> RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var candidates = await dbContext.ManagedObjects.Include(x => x.Chunks)
            .Where(x => (x.State == ManagedObjectState.Staged && x.StagedUntil <= now) || x.State == ManagedObjectState.Ready)
            .ToArrayAsync(cancellationToken);
        var orphaned = 0;
        var missing = 0;
        foreach (var item in candidates)
        {
            if (item.State == ManagedObjectState.Staged && item.StagedUntil <= now)
            {
                await objectStore.DeleteObjectAsync(item.StorageId, cancellationToken);
                item.State = ManagedObjectState.Orphaned;
                item.UpdatedAt = now;
                orphaned++;
                continue;
            }
            foreach (var chunk in item.Chunks)
            {
                if (!await objectStore.ChunkExistsAsync(item.StorageId, chunk.ChunkIndex, cancellationToken))
                {
                    item.State = ManagedObjectState.Missing;
                    item.UpdatedAt = now;
                    missing++;
                    break;
                }
            }
        }
        var expiredSessions = await dbContext.ManagedTransferSessions
            .Where(x => x.State == ManagedTransferSessionState.Active && x.ExpiresAt <= now)
            .ToArrayAsync(cancellationToken);
        foreach (var session in expiredSessions)
        {
            session.State = ManagedTransferSessionState.Expired;
            session.Revision++;
            session.UpdatedAt = now;
        }
        await dbContext.SaveChangesAsync(cancellationToken);
        return new ManagedObjectReconciliationResult(orphaned, missing, candidates.Length);
    }
}

using System.Security.Cryptography;
using DotNet.Testcontainers.Images;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Memory.IntegrationTests;

public sealed class ManagedFileDisasterRecoveryTests
{
    private const string PostgresUser = "contexthub";
    private const string SourceDatabase = "contexthub_dr_source";
    private const string RecoveryDatabase = "contexthub_dr_recovered";
    private const string ProjectId = "managed-file-recovery-test";
    private const string HistoricalKey = "v1";
    private const string RotatedKey = "v2";
    private const int ChunkBytes = 64 * 1024;
    private static readonly IClock Clock = new SystemClock();
    private static readonly IOptions<ManagedTransferOptions> TransferOptions = Options.Create(new ManagedTransferOptions
    {
        ChunkBytes = ChunkBytes,
        MaxRangeBytes = 256 * 1024
    });

    [DockerRequiredFact]
    public async Task Restored_file_requires_ciphertext_metadata_and_matching_key_version_at_each_recovery_point()
    {
        await using var postgres = new PostgreSqlBuilder(new DockerImage(repository: "pgvector/pgvector", tag: "pg17"))
            .WithDatabase(SourceDatabase)
            .WithUsername(PostgresUser)
            .WithPassword("ContextHub_TestFixture_Only")
            .WithName($"contexthub-encrypted-file-dr-{Guid.NewGuid():N}")
            .Build();
        await postgres.StartAsync();
        var sourceConnection = ConnectTo(postgres, SourceDatabase);
        var recoveryConnection = ConnectTo(postgres, RecoveryDatabase);
        await RunPostgresToolAsync(postgres, "createdb", "--no-password", "--username", PostgresUser, RecoveryDatabase);
        await MigrateAsync(sourceConnection);
        await MigrateAsync(recoveryConnection);

        var root = Directory.CreateDirectory(Path.Combine(FindRepositoryRoot(), ".agent", "local", "managed-file-dr", Guid.NewGuid().ToString("N")));
        var sourceStorage = Directory.CreateDirectory(Path.Combine(root.FullName, "source")).FullName;
        var ciphertextFirstStorage = Directory.CreateDirectory(Path.Combine(root.FullName, "ciphertext-first")).FullName;
        var metadataFirstStorage = Directory.CreateDirectory(Path.Combine(root.FullName, "metadata-first")).FullName;
        var v1 = NewSyntheticKey();
        var v2 = NewSyntheticKey();
        var wrongV1 = NewSyntheticKey();
        var original = RandomNumberGenerator.GetBytes(2 * ChunkBytes + 29);
        var originalDigest = SHA256.HashData(original);
        var noKeys = KeyAuthority(string.Empty);
        var v1Only = KeyAuthority(HistoricalKey, (HistoricalKey, v1));
        var v2Only = KeyAuthority(RotatedKey, (RotatedKey, v2));
        var bothKeys = KeyAuthority(RotatedKey, (HistoricalKey, v1), (RotatedKey, v2));
        var wrongHistoricalKey = KeyAuthority(HistoricalKey, (HistoricalKey, wrongV1));

        try
        {
            var file = await SeedFileAsync(sourceConnection, sourceStorage, v1Only, original);
            const string v1Archive = "/tmp/contexthub-dr-v1.dump";
            const string v2Archive = "/tmp/contexthub-dr-v2.dump";
            await BackupAsync(postgres, v1Archive);

            var ciphertext = CaptureCiphertext(sourceStorage);
            ciphertext.Should().HaveCount(3);
            RestoreCiphertext(ciphertextFirstStorage, ciphertext);
            await using (var emptyMetadata = OpenDatabase(recoveryConnection))
            {
                var transfers = CreateTransfer(emptyMetadata, file.Actor, ciphertextFirstStorage, v1Only);
                var downloads = new ManagedFileDownloadService(emptyMetadata, Actors(file.Actor), transfers);
                (await downloads.CreateAsync(file.FileId, file.VersionId,
                    new(ProjectId, "recovery fixture", "ciphertext-without-metadata"), CancellationToken.None)).Should().BeNull();
                (await emptyMetadata.ManagedTransferSessions.CountAsync()).Should().Be(0);
            }

            await RestoreMetadataAsync(postgres, v1Archive);
            await AssertMetadataAsync(recoveryConnection, file, HistoricalKey);
            await AssertRecoveredReadAsync(recoveryConnection, ciphertextFirstStorage, file, v1Only, originalDigest);

            foreach (var chunk in ciphertext)
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(metadataFirstStorage, chunk.RelativePath))!);
            var recoveredRequest = await OpenReadAsync(recoveryConnection, metadataFirstStorage, file, bothKeys);
            await AssertRejectedReadAsync<InvalidOperationException>(recoveryConnection, metadataFirstStorage, file, bothKeys,
                recoveredRequest, "*content is unavailable*");
            RestoreCiphertext(metadataFirstStorage, ciphertext.Take(1));
            var missingRange = recoveredRequest with { Offset = ChunkBytes, Length = ChunkBytes, RequestId = "missing-restored-chunk" };
            await AssertRejectedReadAsync<InvalidOperationException>(recoveryConnection, metadataFirstStorage, file, bothKeys,
                missingRange, "*content is unavailable*");

            RestoreCiphertext(metadataFirstStorage, ciphertext.Skip(1));
            await AssertRejectedReadAsync<CryptographicException>(recoveryConnection, metadataFirstStorage, file, noKeys,
                recoveredRequest, "*does not contain the required key version*");
            await AssertRejectedReadAsync<CryptographicException>(recoveryConnection, metadataFirstStorage, file, v2Only,
                recoveredRequest, "*does not contain the required key version*");
            await AssertRejectedReadAsync<CryptographicException>(recoveryConnection, metadataFirstStorage, file, wrongHistoricalKey,
                recoveredRequest, "*could not authenticate the wrapped key material*");
            await AssertReadAsync(recoveryConnection, metadataFirstStorage, file, bothKeys, recoveredRequest, originalDigest);

            await using (var rotatorDb = OpenDatabase(sourceConnection))
            {
                var rotator = CreateTransfer(rotatorDb, file.Actor, sourceStorage, bothKeys);
                await rotator.RewrapObjectKeyAsync(file.ObjectId, CancellationToken.None);
            }
            await AssertMetadataAsync(sourceConnection, file, RotatedKey);
            CaptureCiphertext(sourceStorage).Select(chunk => chunk.Digest).Should().Equal(ciphertext.Select(chunk => chunk.Digest));
            await BackupAsync(postgres, v2Archive);

            await RestoreMetadataAsync(postgres, v2Archive);
            await AssertMetadataAsync(recoveryConnection, file, RotatedKey);
            var rotatedRequest = await OpenReadAsync(recoveryConnection, metadataFirstStorage, file, v1Only);
            await AssertRejectedReadAsync<CryptographicException>(recoveryConnection, metadataFirstStorage, file, v1Only,
                rotatedRequest, "*does not contain the required key version*");
            await AssertReadAsync(recoveryConnection, metadataFirstStorage, file, v2Only, rotatedRequest, originalDigest);

            // Restore the earlier metadata into the same recovery database without rewriting the recovered ciphertext.
            await RestoreMetadataAsync(postgres, v1Archive);
            await AssertMetadataAsync(recoveryConnection, file, HistoricalKey);
            var rolledBackRequest = await OpenReadAsync(recoveryConnection, metadataFirstStorage, file, v2Only);
            await AssertRejectedReadAsync<CryptographicException>(recoveryConnection, metadataFirstStorage, file, v2Only,
                rolledBackRequest, "*does not contain the required key version*");
            await AssertReadAsync(recoveryConnection, metadataFirstStorage, file, v1Only, rolledBackRequest, originalDigest);
            CaptureCiphertext(metadataFirstStorage).Select(chunk => chunk.Digest).Should().Equal(ciphertext.Select(chunk => chunk.Digest));
        }
        finally
        {
            root.Delete(recursive: true);
            CryptographicOperations.ZeroMemory(original);
        }
    }

    private static async Task<RestorableFile> SeedFileAsync(string connection, string storageRoot, IManagedFileKeyAuthority keyAuthority, byte[] original)
    {
        await using var db = OpenDatabase(connection);
        var now = Clock.UtcNow;
        var tenant = new Tenant { Slug = "managed-dr-fixture", DisplayName = "Managed DR Fixture", CreatedAt = now, UpdatedAt = now };
        var user = new TenantUser
        {
            TenantId = tenant.Id,
            Username = "managed-dr-owner",
            DisplayName = "Recovery Fixture Owner",
            Email = "managed-dr-owner@example.test",
            PasswordHash = "test-actor-does-not-login",
            Role = TenantUserRole.Owner,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.Tenants.Add(tenant);
        await db.SaveChangesAsync();
        db.TenantUsers.Add(user);
        await db.SaveChangesAsync();
        var actor = new ContextHubRequestActor(tenant.Id, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SecurityManage], [ProjectId], IsAuthenticated: true);

        var transfer = CreateTransfer(db, actor, storageRoot, keyAuthority);
        var upload = await transfer.CreateUploadAsync(new(ProjectId, original.Length, "recovery fixture", "seed-upload"), CancellationToken.None);
        var revision = upload.Revision;
        for (var index = 0; index * ChunkBytes < original.Length; index++)
        {
            var offset = index * ChunkBytes;
            var contents = original.AsSpan(offset, Math.Min(ChunkBytes, original.Length - offset)).ToArray();
            var written = await transfer.UploadChunkAsync(new(upload.SessionId, upload.Capability, revision, $"chunk-{index}",
                index, contents, Digest(contents)), CancellationToken.None);
            revision = written.Revision;
        }
        await transfer.CompleteUploadAsync(upload.SessionId, upload.Capability, revision, "seed-complete", CancellationToken.None);

        var accessor = Actors(actor);
        var foundation = new PlatformFoundationStore(db, accessor, Clock, new EffectiveRightsEvaluator());
        var files = new ManagedFileService(db, accessor, Clock, foundation, new DenyHighAssuranceApprovalVerifier());
        var registered = await files.CreateAsync(new(upload.Object.ObjectId, "recovery-fixture.bin", "application/octet-stream",
            "recovery fixture", "seed-register", ProjectId), CancellationToken.None);
        var fileId = registered.FileId!.Value;
        var versionId = registered.FileVersionId!.Value;
        var assessment = new FileSecurityAssessment(true, true, "fixture-scanner", "fixture-policy", []);
        (await files.ApplySecurityAssessmentAsync(versionId, assessment, CancellationToken.None)).Lifecycle.Should().Be(FileVersionLifecycle.Ready);
        await foundation.UpsertExplicitGrantAsync(new(null, ProjectId, user.Id.ToString("D"), "download", AuthorizationEffect.Allow,
            "recovery-fixture", 0, "File", fileId.ToString("D")), CancellationToken.None);
        var metadata = await db.ManagedObjects.AsNoTracking().SingleAsync(item => item.Id == upload.Object.ObjectId);
        return new(fileId, versionId, metadata.Id, metadata.StorageId, metadata.EncryptionGeneration, original.Length, actor);
    }

    private static async Task<ManagedRangeReadRequest> OpenReadAsync(string connection, string storageRoot, RestorableFile file,
        IManagedFileKeyAuthority keyAuthority)
    {
        await using var db = OpenDatabase(connection);
        var transfer = CreateTransfer(db, file.Actor, storageRoot, keyAuthority);
        var downloads = new ManagedFileDownloadService(db, Actors(file.Actor), transfer);
        var session = await downloads.CreateAsync(file.FileId, file.VersionId,
            new(ProjectId, "recovered file", Guid.NewGuid().ToString("N")), CancellationToken.None);
        session.Should().NotBeNull();
        session!.FileVersionId.Should().Be(file.VersionId);
        return new(session.SessionId, session.Capability, session.Revision, "recovered-range", 0, file.Length);
    }

    private static async Task AssertRejectedReadAsync<TException>(string connection, string storageRoot, RestorableFile file,
        IManagedFileKeyAuthority keyAuthority, ManagedRangeReadRequest request, string safeError)
        where TException : Exception
    {
        await using (var db = OpenDatabase(connection))
        await using (var output = new MemoryStream())
        {
            var transfer = CreateTransfer(db, file.Actor, storageRoot, keyAuthority);
            var attempt = () => transfer.CopyRangeAsync(request, output, CancellationToken.None);
            await attempt.Should().ThrowAsync<TException>().WithMessage(safeError);
            output.Length.Should().Be(0);
        }
        await AssertRangeReceiptAsync(connection, request, transferred: false);
    }

    private static async Task AssertRecoveredReadAsync(string connection, string storageRoot, RestorableFile file,
        IManagedFileKeyAuthority keyAuthority, byte[] expectedDigest)
    {
        var request = await OpenReadAsync(connection, storageRoot, file, keyAuthority);
        await AssertReadAsync(connection, storageRoot, file, keyAuthority, request, expectedDigest);
    }

    private static async Task AssertReadAsync(string connection, string storageRoot, RestorableFile file,
        IManagedFileKeyAuthority keyAuthority, ManagedRangeReadRequest request, byte[] expectedDigest)
    {
        await using var db = OpenDatabase(connection);
        await using var output = new MemoryStream();
        var transfer = CreateTransfer(db, file.Actor, storageRoot, keyAuthority);
        (await transfer.CopyRangeAsync(request, output, CancellationToken.None)).Should().Be(file.Length);
        output.Position = 0;
        (await SHA256.HashDataAsync(output)).Should().Equal(expectedDigest);
        await AssertRangeReceiptAsync(connection, request, transferred: true);
    }

    private static async Task AssertRangeReceiptAsync(string connection, ManagedRangeReadRequest request, bool transferred)
    {
        await using var db = OpenDatabase(connection);
        var session = await db.ManagedTransferSessions.AsNoTracking().SingleAsync(item => item.Id == request.SessionId);
        session.Revision.Should().Be(request.ExpectedRevision + (transferred ? 1 : 0));
        session.UsedBytes.Should().Be(transferred ? request.Length : 0);
        var receipted = await db.ManagedTransferOperationRecords.AnyAsync(item =>
            item.SessionId == request.SessionId && item.RequestId == request.RequestId);
        receipted.Should().Be(transferred);
    }

    private static async Task AssertMetadataAsync(string connection, RestorableFile file, string keyId)
    {
        await using var db = OpenDatabase(connection);
        var metadata = await db.ManagedObjects.AsNoTracking().Include(item => item.Chunks).SingleAsync(item => item.Id == file.ObjectId);
        metadata.KeyId.Should().Be(keyId);
        metadata.StorageId.Should().Be(file.StorageId);
        metadata.EncryptionGeneration.Should().Be(file.Generation);
        metadata.State.Should().Be(ManagedObjectState.Ready);
        metadata.WrappedDek.Should().HaveCount(32);
        metadata.Chunks.Should().HaveCount(3);
        var version = await db.FileVersions.AsNoTracking().SingleAsync(item => item.Id == file.VersionId);
        version.FileAssetId.Should().Be(file.FileId);
        version.ManagedObjectId.Should().Be(file.ObjectId);
    }

    private static CiphertextChunk[] CaptureCiphertext(string root)
        => Directory.EnumerateFiles(root, "*.bin", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => new CiphertextChunk(Path.GetRelativePath(root, path), File.ReadAllBytes(path))).ToArray();

    private static void RestoreCiphertext(string root, IEnumerable<CiphertextChunk> chunks)
    {
        foreach (var chunk in chunks)
        {
            var target = Path.Combine(root, chunk.RelativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, chunk.Contents);
        }
    }

    private static string ConnectTo(PostgreSqlContainer postgres, string database)
        => new NpgsqlConnectionStringBuilder(postgres.GetConnectionString()) { Database = database, Pooling = false }.ConnectionString;

    private static MemoryDbContext OpenDatabase(string connection)
        => new(new DbContextOptionsBuilder<MemoryDbContext>().UseNpgsql(connection, npgsql => npgsql.UseVector()).Options);

    private static async Task MigrateAsync(string connection)
    {
        await using var dataSource = NpgsqlDataSource.Create(connection);
        await new DatabaseMigrationHostedService(dataSource, NullLogger<DatabaseMigrationHostedService>.Instance).StartAsync(CancellationToken.None);
    }

    private static Task BackupAsync(PostgreSqlContainer postgres, string archive)
        => RunPostgresToolAsync(postgres, "pg_dump", "--no-password", "--username", PostgresUser, "--format=custom", "--file", archive, SourceDatabase);

    private static Task RestoreMetadataAsync(PostgreSqlContainer postgres, string archive)
        => RunPostgresToolAsync(postgres, "pg_restore", "--no-password", "--username", PostgresUser, "--clean", "--if-exists",
            "--single-transaction", "--exit-on-error", "--dbname", RecoveryDatabase, archive);

    private static async Task RunPostgresToolAsync(PostgreSqlContainer postgres, params string[] arguments)
    {
        var result = await postgres.ExecAsync(arguments);
        result.ExitCode.Should().Be(0, "the disposable PostgreSQL fixture must complete {0}", arguments[0]);
    }

    private static ManagedTransferService CreateTransfer(MemoryDbContext db, ContextHubRequestActor actor, string root,
        IManagedFileKeyAuthority keyAuthority)
    {
        var accessor = Actors(actor);
        return new(db, accessor, Clock, new FileSystemManagedObjectStore(Options.Create(new ManagedObjectStorageOptions
        {
            Enabled = true,
            RootPath = root
        })), keyAuthority, new PlatformFoundationStore(db, accessor, Clock, new EffectiveRightsEvaluator()), TransferOptions);
    }

    private static RequestActorAccessor Actors(ContextHubRequestActor actor) => new() { Current = actor };

    private static AesGcmManagedFileKeyAuthority KeyAuthority(string current, params (string Id, string Value)[] keys)
        => new(Options.Create(new ManagedFileKeyAuthorityOptions
        {
            CurrentKeyId = current,
            Keys = keys.ToDictionary(key => key.Id, key => key.Value, StringComparer.Ordinal)
        }));

    private static string NewSyntheticKey()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        try { return Convert.ToBase64String(key); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    private static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "ContextHub.slnx"))) return directory.FullName;
        throw new InvalidOperationException("Repository-local ignored test artifact root is unavailable.");
    }

    private sealed record RestorableFile(Guid FileId, Guid VersionId, Guid ObjectId, string StorageId,
        int Generation, int Length, ContextHubRequestActor Actor);

    private sealed record CiphertextChunk(string RelativePath, byte[] Contents)
    {
        public string Digest => ManagedFileDisasterRecoveryTests.Digest(Contents);
    }
}

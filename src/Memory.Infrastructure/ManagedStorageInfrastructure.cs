using System.Security.Cryptography;
using System.Text;
using Memory.Application;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class ManagedObjectStorageOptions
{
    public const string SectionName = "ManagedStorage";
    public bool Enabled { get; set; }
    public string RootPath { get; set; } = string.Empty;
}

public sealed class ManagedFileKeyAuthorityOptions
{
    public const string SectionName = "ManagedFileKeyAuthority";
    public string CurrentKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> Keys { get; set; } = new(StringComparer.Ordinal);
}

public sealed class AesGcmManagedFileKeyAuthority(IOptions<ManagedFileKeyAuthorityOptions> options) : IManagedFileKeyAuthority
{
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public WrappedManagedKey Wrap(Guid objectId, int generation, ReadOnlySpan<byte> plaintextKey)
    {
        var settings = options.Value;
        var keyId = RequireCurrentKeyId(settings);
        var kek = ReadKey(settings, keyId);
        var ciphertext = new byte[plaintextKey.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(kek, TagBytes);
            aes.Encrypt(nonce, plaintextKey, ciphertext, tag, BuildAad(objectId, generation, keyId));
            return new WrappedManagedKey(keyId, ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public byte[] Unwrap(Guid objectId, int generation, WrappedManagedKey wrappedKey)
    {
        ValidateWrapped(wrappedKey);
        var kek = ReadKey(options.Value, wrappedKey.KeyId);
        var plaintext = new byte[wrappedKey.Ciphertext.Length];
        try
        {
            using var aes = new AesGcm(kek, TagBytes);
            aes.Decrypt(wrappedKey.Nonce, wrappedKey.Ciphertext, wrappedKey.Tag, plaintext, BuildAad(objectId, generation, wrappedKey.KeyId));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("Managed key authority could not authenticate the wrapped key material.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public WrappedManagedKey Rewrap(Guid objectId, int generation, WrappedManagedKey wrappedKey)
    {
        var plaintext = Unwrap(objectId, generation, wrappedKey);
        try
        {
            return Wrap(objectId, generation, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private static string RequireCurrentKeyId(ManagedFileKeyAuthorityOptions settings)
    {
        var keyId = settings.CurrentKeyId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(keyId)) throw new CryptographicException("Managed file Key Authority is not configured.");
        return keyId;
    }

    private static byte[] ReadKey(ManagedFileKeyAuthorityOptions settings, string keyId)
    {
        if (!settings.Keys.TryGetValue(keyId, out var encoded) || string.IsNullOrWhiteSpace(encoded))
        {
            throw new CryptographicException("Managed file Key Authority does not contain the required key version.");
        }
        try
        {
            var key = Convert.FromBase64String(encoded);
            if (key.Length != 32)
            {
                CryptographicOperations.ZeroMemory(key);
                throw new CryptographicException("Managed file Key Authority key material is invalid.");
            }
            return key;
        }
        catch (FormatException)
        {
            throw new CryptographicException("Managed file Key Authority key material is invalid.");
        }
    }

    private static byte[] BuildAad(Guid objectId, int generation, string keyId)
        => Encoding.UTF8.GetBytes(string.Join('|', "ContextHub.ManagedFile.KEK", 1, objectId, generation, keyId));

    private static void ValidateWrapped(WrappedManagedKey value)
    {
        if (string.IsNullOrWhiteSpace(value.KeyId) || value.Ciphertext.Length != 32 || value.Nonce.Length != NonceBytes || value.Tag.Length != TagBytes)
        {
            throw new CryptographicException("Wrapped managed key metadata is invalid.");
        }
    }
}

public sealed class FileSystemManagedObjectStore(IOptions<ManagedObjectStorageOptions> options) : IManagedObjectStore
{
    public async Task PutChunkAsync(string storageId, int chunkIndex, ReadOnlyMemory<byte> ciphertext, CancellationToken cancellationToken)
    {
        var path = ResolveChunkPath(storageId, chunkIndex, createDirectory: true);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".pending";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(ciphertext, cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }
            File.Move(temporary, path, overwrite: false);
        }
        catch
        {
            TryDelete(temporary);
            throw new IOException("Managed object storage operation failed.");
        }
    }

    public async Task<int> ReadChunkAsync(string storageId, int chunkIndex, Memory<byte> destination, CancellationToken cancellationToken)
    {
        var path = ResolveChunkPath(storageId, chunkIndex, createDirectory: false);
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var total = 0;
            while (total < destination.Length)
            {
                var read = await stream.ReadAsync(destination[total..], cancellationToken);
                if (read == 0) break;
                total += read;
            }
            if (stream.ReadByte() != -1) throw new IOException("Managed object chunk exceeds its authenticated metadata boundary.");
            return total;
        }
        catch (FileNotFoundException)
        {
            throw;
        }
        catch
        {
            throw new IOException("Managed object storage operation failed.");
        }
    }

    public Task<bool> ChunkExistsAsync(string storageId, int chunkIndex, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(ResolveChunkPath(storageId, chunkIndex, createDirectory: false)));
    }

    public Task DeleteObjectAsync(string storageId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = ResolveObjectDirectory(storageId, createDirectory: false);
        try
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
            return Task.CompletedTask;
        }
        catch
        {
            throw new IOException("Managed object storage operation failed.");
        }
    }

    private string ResolveChunkPath(string storageId, int chunkIndex, bool createDirectory)
    {
        if (chunkIndex < 0) throw new InvalidOperationException("Managed object chunk index is invalid.");
        return Path.Combine(ResolveObjectDirectory(storageId, createDirectory), chunkIndex.ToString("D10") + ".bin");
    }

    private string ResolveObjectDirectory(string storageId, bool createDirectory)
    {
        var settings = options.Value;
        if (!settings.Enabled || string.IsNullOrWhiteSpace(settings.RootPath)) throw new IOException("Managed object storage is unavailable.");
        if (storageId.Length != 64 || storageId.Any(ch => !Uri.IsHexDigit(ch))) throw new InvalidOperationException("Managed object storage identity is invalid.");
        var root = Path.GetFullPath(settings.RootPath);
        var directory = Path.GetFullPath(Path.Combine(root, storageId[..2], storageId));
        if (!directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("Managed object storage identity is invalid.");
        if (createDirectory) Directory.CreateDirectory(directory);
        return directory;
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }
}

public sealed class ManagedObjectReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ManagedTransferOptions> options,
    ILogger<ManagedObjectReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(options.Value.ReconciliationIntervalMinutes, 5, 1440)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<IManagedObjectReconciliationService>().RunAsync(stoppingToken);
                logger.LogInformation("Managed object reconciliation checked {Checked}; orphaned {Orphaned}; missing {Missing}.", result.Checked, result.Orphaned, result.Missing);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Managed object reconciliation failed closed.");
            }
        }
    }
}

public sealed class ManagedFileReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<ManagedTransferOptions> options,
    ILogger<ManagedFileReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(options.Value.ReconciliationIntervalMinutes, 5, 1440)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var projections = await scope.ServiceProvider.GetRequiredService<IManagedFileProjectionReconciler>().RunAsync(stoppingToken);
                var deletions = await scope.ServiceProvider.GetRequiredService<IManagedFileDeletionReconciler>().RunAsync(stoppingToken);
                var tags = await scope.ServiceProvider.GetRequiredService<ICanonicalTagBackgroundReconciler>().RunAsync(stoppingToken);
                logger.LogInformation("Managed file reconciliation repaired {ProjectionCount} projections, completed {DeletionCount} primary deletions, and reconciled {TagCount} tag rows.", projections, deletions, tags);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Managed file reconciliation failed closed.");
            }
        }
    }
}

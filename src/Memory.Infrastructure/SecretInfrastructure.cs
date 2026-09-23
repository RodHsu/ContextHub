using System.Security.Cryptography;
using System.Text;
using Memory.Application;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class SecretKeyAuthorityOptions
{
    public const string SectionName = "SecretKeyAuthority";
    public string CurrentKeyId { get; set; } = string.Empty;
    public Dictionary<string, string> KeyFiles { get; set; } = new(StringComparer.Ordinal);
}

public sealed class SecureFileSecretEnvelopeKeyAuthority(IOptions<SecretKeyAuthorityOptions> options) : ISecretEnvelopeKeyAuthority
{
    private const int KeyBytes = 32;
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    public WrappedSecretKey Wrap(Guid secretId, Guid versionId, ReadOnlySpan<byte> plaintextKey)
    {
        if (plaintextKey.Length != KeyBytes) throw new CryptographicException("Secret data key is invalid.");
        var keyId = RequireCurrentKeyId();
        var kek = ReadKey(keyId);
        var ciphertext = new byte[plaintextKey.Length];
        var nonce = RandomNumberGenerator.GetBytes(NonceBytes);
        var tag = new byte[TagBytes];
        try
        {
            using var aes = new AesGcm(kek, TagBytes);
            aes.Encrypt(nonce, plaintextKey, ciphertext, tag, BuildAad(secretId, versionId, keyId));
            return new WrappedSecretKey(keyId, ciphertext, nonce, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public byte[] Unwrap(Guid secretId, Guid versionId, WrappedSecretKey wrappedKey)
    {
        ValidateWrapped(wrappedKey);
        var kek = ReadKey(wrappedKey.KeyId);
        var plaintext = new byte[KeyBytes];
        try
        {
            using var aes = new AesGcm(kek, TagBytes);
            aes.Decrypt(wrappedKey.Nonce, wrappedKey.Ciphertext, wrappedKey.Tag, plaintext, BuildAad(secretId, versionId, wrappedKey.KeyId));
            return plaintext;
        }
        catch
        {
            CryptographicOperations.ZeroMemory(plaintext);
            throw new CryptographicException("Secret Key Authority could not authenticate the wrapped key material.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public WrappedSecretKey Rewrap(Guid secretId, Guid versionId, WrappedSecretKey wrappedKey)
    {
        var plaintext = Unwrap(secretId, versionId, wrappedKey);
        try
        {
            return Wrap(secretId, versionId, plaintext);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plaintext);
        }
    }

    private string RequireCurrentKeyId()
    {
        var keyId = options.Value.CurrentKeyId?.Trim() ?? string.Empty;
        if (keyId.Length == 0) throw new CryptographicException("Secret Key Authority is not configured.");
        return keyId;
    }

    private byte[] ReadKey(string keyId)
    {
        if (!options.Value.KeyFiles.TryGetValue(keyId, out var configuredPath) || string.IsNullOrWhiteSpace(configuredPath))
            throw new CryptographicException("Secret Key Authority does not contain the required key version.");
        if (!Path.IsPathFullyQualified(configuredPath))
            throw new CryptographicException("Secret Key Authority key file path must be absolute.");
        var path = Path.GetFullPath(configuredPath);
        if (!File.Exists(path)) throw new CryptographicException("Secret Key Authority key file is unavailable.");
        EnsureSecurePermissions(path);
        byte[] key;
        try
        {
            key = File.ReadAllBytes(path);
        }
        catch
        {
            throw new CryptographicException("Secret Key Authority key file is unavailable.");
        }
        if (key.Length != KeyBytes)
        {
            CryptographicOperations.ZeroMemory(key);
            throw new CryptographicException("Secret Key Authority key file is invalid.");
        }
        return key;
    }

    private static void EnsureSecurePermissions(string path)
    {
        if (OperatingSystem.IsWindows())
            throw new CryptographicException("Secret KEK files require an OS secure-store provider on Windows; mounted key files are Linux-only.");
        if (new FileInfo(path).LinkTarget is not null)
            throw new CryptographicException("Secret Key Authority key file must not be a symbolic link.");
        var mode = File.GetUnixFileMode(path);
        const UnixFileMode forbidden = UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                                       UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;
        if ((mode & forbidden) != 0 || (mode & UnixFileMode.UserRead) == 0)
            throw new CryptographicException("Secret Key Authority key file permissions are unsafe.");
    }

    private static byte[] BuildAad(Guid secretId, Guid versionId, string keyId)
        => Encoding.UTF8.GetBytes($"ContextHub.Secret.KEK|1|{secretId:D}|{versionId:D}|{keyId}");

    private static void ValidateWrapped(WrappedSecretKey value)
    {
        if (string.IsNullOrWhiteSpace(value.KeyId) || value.Ciphertext.Length != KeyBytes || value.Nonce.Length != NonceBytes || value.Tag.Length != TagBytes)
            throw new CryptographicException("Wrapped secret key metadata is invalid.");
    }
}

public sealed class IdentityPasswordCredentialVerifier(IPasswordHasher<object> passwordHasher) : IPasswordCredentialVerifier
{
    public bool Verify(string passwordHash, string password)
    {
        try
        {
            return passwordHasher.VerifyHashedPassword(new object(), passwordHash, password) != PasswordVerificationResult.Failed;
        }
        catch
        {
            return false;
        }
    }
}

public sealed class RsaSshBoundSigner : ISshBoundSigner
{
    public byte[] SignAuthenticationPayload(ReadOnlySpan<byte> privateKeyPkcs8, ReadOnlySpan<byte> canonicalPayload)
    {
        using var rsa = RSA.Create();
        try
        {
            rsa.ImportPkcs8PrivateKey(privateKeyPkcs8, out var bytesRead);
            if (bytesRead != privateKeyPkcs8.Length) throw new CryptographicException("SSH signer key contains trailing data.");
            return rsa.SignData(canonicalPayload, HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
        }
        catch
        {
            throw new CryptographicException("SSH signer failed closed.");
        }
    }
}

public sealed class SecretReconciliationHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<SecretManagementOptions> options,
    ILogger<SecretReconciliationHostedService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(Math.Clamp(options.Value.ReconciliationIntervalMinutes, 5, 1440)));
        while (await timer.WaitForNextTickAsync(stoppingToken))
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var result = await scope.ServiceProvider.GetRequiredService<ISecretReconciliationService>().RunAsync(stoppingToken);
                logger.LogInformation("Secret reconciliation expired {ExpiredLeaseCount} leases and {ExpiredCertificateCount} certificates; {StaleRelationCount} stale relations and {PendingKrlCount} KRL entries remain.",
                    result.ExpiredLeases, result.ExpiredCertificates, result.StaleRelations, result.PendingKrl);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Secret reconciliation failed closed.");
            }
        }
    }
}

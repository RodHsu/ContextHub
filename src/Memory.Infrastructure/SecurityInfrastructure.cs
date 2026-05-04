using System.Security.Cryptography;
using System.Text;
using Memory.Application;
using Microsoft.Extensions.Configuration;

namespace Memory.Infrastructure;

public sealed class AesSecretProtector(IConfiguration configuration) : ISecretProtector
{
    private readonly byte[] _key = DeriveKey(configuration);

    public string Protect(string plaintext)
    {
        if (string.IsNullOrWhiteSpace(plaintext))
        {
            return string.Empty;
        }

        var nonce = RandomNumberGenerator.GetBytes(12);
        var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
        var ciphertext = new byte[plaintextBytes.Length];
        var tag = new byte[16];
        using var aes = new AesGcm(_key, 16);
        aes.Encrypt(nonce, plaintextBytes, ciphertext, tag);

        var payload = new byte[nonce.Length + tag.Length + ciphertext.Length];
        Buffer.BlockCopy(nonce, 0, payload, 0, nonce.Length);
        Buffer.BlockCopy(tag, 0, payload, nonce.Length, tag.Length);
        Buffer.BlockCopy(ciphertext, 0, payload, nonce.Length + tag.Length, ciphertext.Length);
        return $"v1:{Convert.ToBase64String(payload)}";
    }

    public string Unprotect(string protectedText)
    {
        if (string.IsNullOrWhiteSpace(protectedText))
        {
            return string.Empty;
        }

        if (!protectedText.StartsWith("v1:", StringComparison.Ordinal))
        {
            return protectedText;
        }

        var payload = Convert.FromBase64String(protectedText["v1:".Length..]);
        var nonce = payload[..12];
        var tag = payload[12..28];
        var ciphertext = payload[28..];
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(_key, 16);
        aes.Decrypt(nonce, ciphertext, tag, plaintext);
        return Encoding.UTF8.GetString(plaintext);
    }

    private static byte[] DeriveKey(IConfiguration configuration)
    {
        var secretMaterial =
            configuration["ContextHub:SecretKey"]
            ?? configuration["Dashboard:SecretKey"]
            ?? string.Join(
                "|",
                configuration.GetConnectionString("Postgres") ?? string.Empty,
                configuration.GetConnectionString("Redis") ?? string.Empty,
                configuration["ContextHub:InstanceId"] ?? string.Empty,
                configuration["Dashboard:InstanceId"] ?? string.Empty,
                Environment.MachineName);
        return SHA256.HashData(Encoding.UTF8.GetBytes(secretMaterial));
    }
}

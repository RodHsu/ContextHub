using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Memory.Infrastructure;

public static class E5AssetBundle
{
    public static string NormalizeSha256(string? value)
    {
        var sha256 = value?.Trim() ?? string.Empty;
        if (sha256.Length != 64 || !sha256.All(char.IsAsciiHexDigit))
            throw new InvalidOperationException("Corrected E5 tokenizer requires an explicit SHA256 asset bundle identity.");
        return sha256.ToLowerInvariant();
    }

    public static async Task<string> ComputeAsync(string directory, IReadOnlyList<EmbeddingAssetFile> assets, CancellationToken cancellationToken)
    {
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var names = assets.Select(asset => asset.LocalPath).ToArray();
        if (names.Length == 0 || names.Distinct(StringComparer.Ordinal).Count() != names.Length)
            throw new InvalidOperationException("Embedding asset bundle must have distinct file identities.");
        var digests = new List<object>(assets.Count);
        foreach (var asset in assets.OrderBy(asset => asset.LocalPath, StringComparer.Ordinal))
        {
            var path = Path.GetFullPath(Path.Combine(root, asset.LocalPath));
            if (!path.StartsWith(root, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                throw new InvalidOperationException("Embedding asset path escapes its bundle directory.");
            await using var stream = File.OpenRead(path);
            var digest = await SHA256.HashDataAsync(stream, cancellationToken);
            digests.Add(new { Name = asset.LocalPath.Replace('\\', '/'), Bytes = stream.Length, Sha256 = Convert.ToHexString(digest).ToLowerInvariant() });
        }
        var canonical = JsonSerializer.Serialize(new { Contract = "e5-asset-bundle-v1", Files = digests });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }
}

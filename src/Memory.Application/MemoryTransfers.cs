using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class MemoryTransferService(
    IApplicationDbContext dbContext,
    IMemoryService memoryService,
    IRuntimeConfigurationAccessor runtimeConfigurationAccessor) : IMemoryTransferService
{
    private const int TransferFormatVersion = 1;
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<MemoryTransferDownloadResult> ExportAsync(MemoryExportRequest request, CancellationToken cancellationToken)
    {
        var items = await QueryItems(request)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new MemoryTransferItem(
                x.ProjectId,
                x.ExternalKey,
                x.Scope,
                x.MemoryType,
                x.Title,
                x.Content,
                x.Summary,
                x.SourceType,
                x.SourceRef,
                x.Tags,
                x.Importance,
                x.Confidence,
                x.MetadataJson,
                x.IsReadOnly,
                x.Status,
                x.CreatedAt,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);

        var bundle = new MemoryTransferBundle(
            TransferFormatVersion,
            runtimeConfigurationAccessor.Current.Namespace,
            DateTimeOffset.UtcNow,
            items);

        var bundleJson = JsonSerializer.Serialize(bundle, JsonOptions);
        var package = CreatePackage(bundleJson, request.Passphrase);
        var packageJson = JsonSerializer.Serialize(package, JsonOptions);
        var exportFileName = BuildFileName(package.Encrypted);

        return new MemoryTransferDownloadResult(
            exportFileName,
            "application/json",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(packageJson)),
            items.Count,
            package.Encrypted);
    }

    public async Task<MemoryImportPreviewResult> PreviewImportAsync(MemoryImportRequest request, CancellationToken cancellationToken)
    {
        var preview = await ParsePreviewAsync(request, cancellationToken);
        return preview.Result;
    }

    public async Task<MemoryImportApplyResult> ApplyImportAsync(MemoryImportRequest request, CancellationToken cancellationToken)
    {
        var preview = await ParsePreviewAsync(request, cancellationToken);
        if (preview.Result.ConflictItems > 0 && !request.ForceOverwrite)
        {
            throw new InvalidOperationException("Import contains existing external keys. Preview the package first and confirm overwrite.");
        }

        var importedIds = new List<Guid>(preview.Bundle.Items.Count);
        foreach (var item in preview.Bundle.Items)
        {
            var targetProjectId = ResolveTargetProjectId(item.ProjectId, request.TargetProjectId);
            var imported = await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    item.ExternalKey,
                    item.Scope,
                    item.MemoryType,
                    item.Title,
                    item.Content,
                    item.Summary,
                    item.SourceType,
                    item.SourceRef,
                    item.Tags,
                    item.Importance,
                    item.Confidence,
                    item.MetadataJson,
                    targetProjectId),
                cancellationToken);

            importedIds.Add(imported.Id);
        }

        return new MemoryImportApplyResult(
            importedIds.Count,
            preview.Result.ConflictItems,
            importedIds);
    }

    private IQueryable<Memory.Domain.MemoryItem> QueryItems(MemoryExportRequest request)
    {
        var query = dbContext.MemoryItems
            .AsNoTracking()
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var term = request.Query.Trim();
            query = query.Where(x =>
                x.Title.Contains(term) ||
                x.Summary.Contains(term) ||
                x.Content.Contains(term) ||
                x.SourceRef.Contains(term) ||
                x.ExternalKey.Contains(term));
        }

        if (request.Scope.HasValue)
        {
            query = query.Where(x => x.Scope == request.Scope.Value);
        }

        if (request.MemoryType.HasValue)
        {
            query = query.Where(x => x.MemoryType == request.MemoryType.Value);
        }

        if (request.Status.HasValue)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType))
        {
            query = query.Where(x => x.SourceType == request.SourceType);
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            query = query.Where(x => x.Tags.Contains(request.Tag));
        }

        var allowedProjects = ProjectContext.ResolveSearchProjects(request.ProjectId, request.IncludedProjectIds, request.QueryMode, request.UseSummaryLayer);
        query = query.Where(x => allowedProjects.Contains(x.ProjectId));

        return query;
    }

    private async Task<ParsedImportPreview> ParsePreviewAsync(MemoryImportRequest request, CancellationToken cancellationToken)
    {
        var package = ParsePackage(request.PackageBase64);
        var bundle = ParseBundle(package, request.Passphrase);
        var externalKeys = bundle.Items
            .Select(x => x.ExternalKey)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        var existing = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => externalKeys.Contains(x.ExternalKey))
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                x.ExternalKey,
                x.Title,
                x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var existingByKey = existing.ToDictionary(x => $"{x.ProjectId}:{x.ExternalKey}", StringComparer.Ordinal);
        var conflicts = bundle.Items
            .Where(x => existingByKey.ContainsKey($"{ResolveTargetProjectId(x.ProjectId, request.TargetProjectId)}:{x.ExternalKey}"))
            .Select(x =>
            {
                var matched = existingByKey[$"{ResolveTargetProjectId(x.ProjectId, request.TargetProjectId)}:{x.ExternalKey}"];
                return new MemoryImportConflictResult(
                    matched.ProjectId,
                    x.ExternalKey,
                    matched.Id,
                    matched.Title,
                    x.Title,
                    matched.UpdatedAt);
            })
            .OrderBy(x => x.ExternalKey, StringComparer.Ordinal)
            .ToArray();

        var preview = new MemoryImportPreviewResult(
            bundle.Namespace,
            bundle.Items.Count,
            bundle.Items.Count - conflicts.Length,
            conflicts.Length,
            package.Encrypted,
            package.Encrypted,
            conflicts);

        return new ParsedImportPreview(bundle, preview);
    }

    private static MemoryTransferPackage ParsePackage(string packageBase64)
    {
        if (string.IsNullOrWhiteSpace(packageBase64))
        {
            throw new InvalidOperationException("Import package is empty.");
        }

        try
        {
            var packageJson = Encoding.UTF8.GetString(Convert.FromBase64String(packageBase64));
            return JsonSerializer.Deserialize<MemoryTransferPackage>(packageJson, JsonOptions)
                ?? throw new InvalidOperationException("Import package is invalid.");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Import package is not valid Base64 content.", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Import package is not valid JSON.", ex);
        }
    }

    private static MemoryTransferBundle ParseBundle(MemoryTransferPackage package, string? passphrase)
    {
        if (package.Version != TransferFormatVersion)
        {
            throw new InvalidOperationException($"Unsupported memory transfer package version '{package.Version}'.");
        }

        if (!string.Equals(package.Format, "contexthub-memory-transfer", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Unsupported memory transfer package format.");
        }

        var payloadJson = package.Encrypted
            ? Decrypt(package, passphrase)
            : DecodeUtf8(package.PayloadBase64);

        try
        {
            return JsonSerializer.Deserialize<MemoryTransferBundle>(payloadJson, JsonOptions)
                ?? throw new InvalidOperationException("Transfer bundle payload is empty.");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("Transfer bundle payload is invalid.", ex);
        }
    }

    private static MemoryTransferPackage CreatePackage(string bundleJson, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return new MemoryTransferPackage(
                TransferFormatVersion,
                "contexthub-memory-transfer",
                false,
                "none",
                Convert.ToBase64String(Encoding.UTF8.GetBytes(bundleJson)),
                null,
                null);
        }

        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = Encoding.UTF8.GetBytes(bundleJson);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plaintext, ciphertext, tag);
        }

        var payload = new byte[ciphertext.Length + tag.Length];
        Buffer.BlockCopy(ciphertext, 0, payload, 0, ciphertext.Length);
        Buffer.BlockCopy(tag, 0, payload, ciphertext.Length, tag.Length);

        return new MemoryTransferPackage(
            TransferFormatVersion,
            "contexthub-memory-transfer",
            true,
            "AES-256-GCM",
            Convert.ToBase64String(payload),
            Convert.ToBase64String(salt),
            Convert.ToBase64String(nonce));
    }

    private static string Decrypt(MemoryTransferPackage package, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            throw new InvalidOperationException("This package is encrypted. Please provide the import passphrase.");
        }

        if (string.IsNullOrWhiteSpace(package.SaltBase64) || string.IsNullOrWhiteSpace(package.NonceBase64))
        {
            throw new InvalidOperationException("Encrypted package metadata is incomplete.");
        }

        var payload = Convert.FromBase64String(package.PayloadBase64);
        if (payload.Length < TagSize)
        {
            throw new InvalidOperationException("Encrypted package payload is invalid.");
        }

        var ciphertext = payload[..^TagSize];
        var tag = payload[^TagSize..];
        var salt = Convert.FromBase64String(package.SaltBase64);
        var nonce = Convert.FromBase64String(package.NonceBase64);
        var key = Rfc2898DeriveBytes.Pbkdf2(passphrase, salt, Pbkdf2Iterations, HashAlgorithmName.SHA256, 32);
        var plaintext = new byte[ciphertext.Length];

        try
        {
            using var aes = new AesGcm(key, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext);
            return Encoding.UTF8.GetString(plaintext);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Import passphrase is incorrect, or the package content is corrupted.", ex);
        }
    }

    private static string DecodeUtf8(string payloadBase64)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(payloadBase64));
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("Transfer package payload is not valid Base64 content.", ex);
        }
    }

    private static string BuildFileName(bool encrypted)
    {
        var suffix = encrypted ? "encrypted" : "plain";
        return $"contexthub-memories_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}_{suffix}.json";
    }

    private sealed record ParsedImportPreview(
        MemoryTransferBundle Bundle,
        MemoryImportPreviewResult Result);

    private sealed record MemoryTransferBundle(
        int Version,
        string Namespace,
        DateTimeOffset ExportedAtUtc,
        IReadOnlyList<MemoryTransferItem> Items);

    private sealed record MemoryTransferItem(
        string ProjectId,
        string ExternalKey,
        MemoryScope Scope,
        MemoryType MemoryType,
        string Title,
        string Content,
        string Summary,
        string SourceType,
        string SourceRef,
        IReadOnlyList<string> Tags,
        decimal Importance,
        decimal Confidence,
        string MetadataJson,
        bool IsReadOnly,
        MemoryStatus Status,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);

    private sealed record MemoryTransferPackage(
        int Version,
        string Format,
        bool Encrypted,
        string Algorithm,
        string PayloadBase64,
        string? SaltBase64,
        string? NonceBase64);

    private static string ResolveTargetProjectId(string sourceProjectId, string? targetProjectId)
        => ProjectContext.Normalize(targetProjectId, ProjectContext.Normalize(sourceProjectId));
}

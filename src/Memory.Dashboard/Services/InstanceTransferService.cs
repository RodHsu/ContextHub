using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Application;
using Memory.Domain;

namespace Memory.Dashboard.Services;

public enum InstanceTransferSection
{
    SystemSettings,
    Memories,
    UserPreferences
}

public sealed record InstanceTransferExportRequest(
    IReadOnlyList<InstanceTransferSection>? Sections = null,
    string? Passphrase = null);

public sealed record InstanceTransferImportRequest(
    string PackageBase64,
    IReadOnlyList<InstanceTransferSection>? Sections = null,
    string? Passphrase = null,
    bool ForceOverwrite = false);

public sealed record InstanceTransferSectionSummaryResult(
    InstanceTransferSection Section,
    string Label,
    int TotalItems,
    int NewItems,
    int ConflictItems,
    string Summary);

public sealed record InstanceTransferConflictResult(
    InstanceTransferSection Section,
    string Identifier,
    string ExistingTitle,
    string IncomingTitle,
    DateTimeOffset? ExistingUpdatedAt,
    string Description);

public sealed record InstanceTransferDownloadResult(
    string FileName,
    string ContentType,
    string PayloadBase64,
    IReadOnlyList<InstanceTransferSectionSummaryResult> Sections,
    bool Encrypted);

public sealed record InstanceTransferPreviewResult(
    string Namespace,
    bool Encrypted,
    bool RequiresPassphrase,
    IReadOnlyList<InstanceTransferSectionSummaryResult> Sections,
    IReadOnlyList<InstanceTransferConflictResult> Conflicts);

public sealed record InstanceTransferApplyResult(
    IReadOnlyList<InstanceTransferSectionSummaryResult> Sections,
    int AppliedItems,
    int OverwrittenItems);

public interface IInstanceTransferService
{
    Task<InstanceTransferDownloadResult> ExportAsync(InstanceTransferExportRequest request, CancellationToken cancellationToken);
    Task<InstanceTransferPreviewResult> PreviewImportAsync(InstanceTransferImportRequest request, CancellationToken cancellationToken);
    Task<InstanceTransferApplyResult> ApplyImportAsync(InstanceTransferImportRequest request, CancellationToken cancellationToken);
}

public sealed class InstanceTransferService(
    IInstanceSettingsService instanceSettingsService,
    IContextHubApiClient apiClient) : IInstanceTransferService
{
    private const int TransferFormatVersion = 1;
    private const int Pbkdf2Iterations = 100_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int UserPreferenceLimit = 2048;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public async Task<InstanceTransferDownloadResult> ExportAsync(InstanceTransferExportRequest request, CancellationToken cancellationToken)
    {
        var selectedSections = ResolveSelectedSections(request.Sections);
        var snapshot = await instanceSettingsService.GetSnapshotAsync(cancellationToken);
        var summaries = new List<InstanceTransferSectionSummaryResult>(selectedSections.Count);
        ExportedSystemSettings? systemSettings = null;
        string? memoryPackageBase64 = null;
        IReadOnlyList<ExportedUserPreference>? userPreferences = null;

        if (selectedSections.Contains(InstanceTransferSection.SystemSettings))
        {
            systemSettings = new ExportedSystemSettings(
                new InstanceBehaviorSettingsUpdateRequest(
                    snapshot.Behavior.ConversationAutomationEnabled,
                    snapshot.Behavior.HostEventIngestionEnabled,
                    snapshot.Behavior.AgentSupplementalIngestionEnabled,
                    snapshot.Behavior.IdleThresholdMinutes,
                    snapshot.Behavior.PromotionMode,
                    snapshot.Behavior.ExcerptMaxLength,
                    snapshot.Behavior.DefaultProjectId,
                    snapshot.Behavior.DefaultQueryMode,
                    snapshot.Behavior.DefaultUseSummaryLayer,
                    snapshot.Behavior.SharedSummaryAutoRefreshEnabled,
                    new DashboardSnapshotPollingSettingsUpdateRequest(
                        snapshot.Behavior.SnapshotPolling.StatusCoreSeconds,
                        snapshot.Behavior.SnapshotPolling.EmbeddingRuntimeSeconds,
                        snapshot.Behavior.SnapshotPolling.DependenciesHealthSeconds,
                        snapshot.Behavior.SnapshotPolling.DockerHostSeconds,
                        snapshot.Behavior.SnapshotPolling.DependencyResourcesSeconds,
                        snapshot.Behavior.SnapshotPolling.RecentOperationsSeconds,
                        snapshot.Behavior.SnapshotPolling.ResourceChartSeconds),
                    snapshot.Behavior.OverviewPollingSeconds,
                    snapshot.Behavior.MetricsPollingSeconds,
                    snapshot.Behavior.JobsPollingSeconds,
                    snapshot.Behavior.LogsPollingSeconds,
                    snapshot.Behavior.PerformancePollingSeconds),
                new ExportedDashboardAuthSettings(
                    snapshot.DashboardAuth.AdminUsername,
                    snapshot.DashboardAuth.SessionTimeoutMinutes));

            summaries.Add(BuildSummary(
                InstanceTransferSection.SystemSettings,
                1,
                1,
                0,
                "包含應用行為設定與 Dashboard 登入帳號 / session timeout，不含密碼 hash。"));
        }

        if (selectedSections.Contains(InstanceTransferSection.Memories))
        {
            var memoryExport = await apiClient.ExportMemoriesAsync(new MemoryExportRequest(Passphrase: null), cancellationToken);
            memoryPackageBase64 = memoryExport.PayloadBase64;
            summaries.Add(BuildSummary(
                InstanceTransferSection.Memories,
                memoryExport.ItemCount,
                memoryExport.ItemCount,
                0,
                memoryExport.ItemCount == 0 ? "未包含任何記憶資料。" : $"包含 {memoryExport.ItemCount} 筆記憶資料。"));
        }

        if (selectedSections.Contains(InstanceTransferSection.UserPreferences))
        {
            var exportedPreferences = await apiClient.GetPreferencesAsync(null, includeArchived: true, limit: UserPreferenceLimit, cancellationToken);
            userPreferences = exportedPreferences
                .Select(preference => new ExportedUserPreference(
                    preference.Key,
                    preference.Kind,
                    preference.Title,
                    preference.Content,
                    preference.Rationale,
                    preference.Tags,
                    preference.Importance,
                    preference.Confidence,
                    preference.Status))
                .ToArray();

            summaries.Add(BuildSummary(
                InstanceTransferSection.UserPreferences,
                userPreferences.Count,
                userPreferences.Count,
                0,
                userPreferences.Count == 0 ? "未包含任何使用者偏好。" : $"包含 {userPreferences.Count} 筆使用者偏好。"));
        }

        var bundle = new InstanceTransferBundle(
            TransferFormatVersion,
            snapshot.Namespace,
            DateTimeOffset.UtcNow,
            selectedSections,
            systemSettings,
            memoryPackageBase64,
            userPreferences);

        var bundleJson = JsonSerializer.Serialize(bundle, JsonOptions);
        var package = CreatePackage(bundleJson, request.Passphrase);
        var packageJson = JsonSerializer.Serialize(package, JsonOptions);

        return new InstanceTransferDownloadResult(
            BuildFileName(package.Encrypted),
            "application/json",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(packageJson)),
            summaries,
            package.Encrypted);
    }

    public async Task<InstanceTransferPreviewResult> PreviewImportAsync(InstanceTransferImportRequest request, CancellationToken cancellationToken)
    {
        var parsed = ParseBundle(request.PackageBase64, request.Passphrase);
        var selectedSections = ResolveImportSections(parsed.Bundle, request.Sections);
        return await BuildPreviewAsync(parsed, selectedSections, cancellationToken);
    }

    public async Task<InstanceTransferApplyResult> ApplyImportAsync(InstanceTransferImportRequest request, CancellationToken cancellationToken)
    {
        var parsed = ParseBundle(request.PackageBase64, request.Passphrase);
        var selectedSections = ResolveImportSections(parsed.Bundle, request.Sections);
        var preview = await BuildPreviewAsync(parsed, selectedSections, cancellationToken);

        var conflictCount = preview.Sections.Sum(section => section.ConflictItems);
        if (conflictCount > 0 && !request.ForceOverwrite)
        {
            throw new InvalidOperationException("匯入內容包含既有設定或同鍵值資料，請先預覽並確認覆蓋。");
        }

        var appliedItems = 0;
        var overwrittenItems = 0;
        foreach (var section in selectedSections)
        {
            switch (section)
            {
                case InstanceTransferSection.SystemSettings:
                    var systemSettings = parsed.Bundle.SystemSettings
                        ?? throw new InvalidOperationException("匯入套件不包含系統設定。");
                    await instanceSettingsService.UpdateAsync(
                        new InstanceSettingsUpdateRequest(
                            systemSettings.Behavior,
                            new InstanceDashboardAuthUpdateRequest(
                                systemSettings.DashboardAuth.AdminUsername,
                                null,
                                null,
                                systemSettings.DashboardAuth.SessionTimeoutMinutes)),
                        "dashboard-transfer",
                        cancellationToken);
                    appliedItems += 1;
                    overwrittenItems += 1;
                    break;

                case InstanceTransferSection.Memories:
                    if (string.IsNullOrWhiteSpace(parsed.Bundle.MemoryPackageBase64))
                    {
                        throw new InvalidOperationException("匯入套件不包含記憶資料。");
                    }

                    var memoryResult = await apiClient.ApplyMemoryImportAsync(
                        new MemoryImportRequest(
                            parsed.Bundle.MemoryPackageBase64,
                            Passphrase: null,
                            ForceOverwrite: preview.Sections.First(summary => summary.Section == InstanceTransferSection.Memories).ConflictItems > 0),
                        cancellationToken);
                    appliedItems += memoryResult.ImportedItems;
                    overwrittenItems += memoryResult.OverwrittenItems;
                    break;

                case InstanceTransferSection.UserPreferences:
                    var preferences = parsed.Bundle.UserPreferences
                        ?? throw new InvalidOperationException("匯入套件不包含使用者偏好。");

                    foreach (var preference in preferences)
                    {
                        var upserted = await apiClient.UpsertPreferenceAsync(
                            new UserPreferenceUpsertRequest(
                                preference.Key,
                                preference.Kind,
                                preference.Title,
                                preference.Content,
                                preference.Rationale,
                                preference.Tags,
                                preference.Importance,
                                preference.Confidence),
                            cancellationToken);

                        if (preference.Status == MemoryStatus.Archived)
                        {
                            await apiClient.ArchivePreferenceAsync(upserted.Id, archived: true, cancellationToken);
                        }
                    }

                    appliedItems += preferences.Count;
                    overwrittenItems += preview.Sections.First(summary => summary.Section == InstanceTransferSection.UserPreferences).ConflictItems;
                    break;
            }
        }

        return new InstanceTransferApplyResult(
            preview.Sections,
            appliedItems,
            overwrittenItems);
    }

    private async Task<InstanceTransferPreviewResult> BuildPreviewAsync(
        ParsedBundle parsed,
        IReadOnlyList<InstanceTransferSection> selectedSections,
        CancellationToken cancellationToken)
    {
        var summaries = new List<InstanceTransferSectionSummaryResult>(selectedSections.Count);
        var conflicts = new List<InstanceTransferConflictResult>();

        if (selectedSections.Contains(InstanceTransferSection.SystemSettings))
        {
            var settings = parsed.Bundle.SystemSettings
                ?? throw new InvalidOperationException("匯入套件不包含系統設定。");
            summaries.Add(BuildSummary(
                InstanceTransferSection.SystemSettings,
                1,
                0,
                1,
                $"會覆蓋目前 Dashboard 帳號 '{settings.DashboardAuth.AdminUsername}'、session timeout 與行為設定。"));
            conflicts.Add(new InstanceTransferConflictResult(
                InstanceTransferSection.SystemSettings,
                "instance-settings",
                "目前 instance 設定",
                "匯入的 instance 設定",
                null,
                "系統設定屬於覆蓋式匯入，套用後會取代目前的可匯出設定值。"));
        }

        if (selectedSections.Contains(InstanceTransferSection.Memories))
        {
            if (string.IsNullOrWhiteSpace(parsed.Bundle.MemoryPackageBase64))
            {
                throw new InvalidOperationException("匯入套件不包含記憶資料。");
            }

            var memoryPreview = await apiClient.PreviewMemoryImportAsync(
                new MemoryImportRequest(parsed.Bundle.MemoryPackageBase64, Passphrase: null),
                cancellationToken);

            summaries.Add(BuildSummary(
                InstanceTransferSection.Memories,
                memoryPreview.TotalItems,
                memoryPreview.NewItems,
                memoryPreview.ConflictItems,
                memoryPreview.ConflictItems == 0
                    ? $"可新增 {memoryPreview.NewItems} 筆記憶資料。"
                    : $"會覆蓋 {memoryPreview.ConflictItems} 筆既有 externalKey。"));

            conflicts.AddRange(memoryPreview.Conflicts.Select(conflict => new InstanceTransferConflictResult(
                InstanceTransferSection.Memories,
                $"{conflict.ProjectId}/{conflict.ExternalKey}",
                conflict.ExistingTitle,
                conflict.IncomingTitle,
                conflict.ExistingUpdatedAt,
                "記憶資料的 externalKey 已存在，套用後會覆蓋該筆內容。")));
        }

        if (selectedSections.Contains(InstanceTransferSection.UserPreferences))
        {
            var preferences = parsed.Bundle.UserPreferences
                ?? throw new InvalidOperationException("匯入套件不包含使用者偏好。");
            var existing = await apiClient.GetPreferencesAsync(null, includeArchived: true, limit: UserPreferenceLimit, cancellationToken);
            var existingByKey = existing.ToDictionary(
                preference => NormalizePreferenceKey(preference.Key),
                StringComparer.OrdinalIgnoreCase);

            var conflictCount = 0;
            foreach (var preference in preferences)
            {
                if (!existingByKey.TryGetValue(NormalizePreferenceKey(preference.Key), out var matched))
                {
                    continue;
                }

                conflictCount++;
                conflicts.Add(new InstanceTransferConflictResult(
                    InstanceTransferSection.UserPreferences,
                    preference.Key,
                    matched.Title,
                    preference.Title,
                    matched.UpdatedAt,
                    "使用者偏好以 key 為識別，匯入後會覆蓋同 key 的既有偏好。"));
            }

            summaries.Add(BuildSummary(
                InstanceTransferSection.UserPreferences,
                preferences.Count,
                preferences.Count - conflictCount,
                conflictCount,
                conflictCount == 0
                    ? $"可新增 {preferences.Count} 筆使用者偏好。"
                    : $"會覆蓋 {conflictCount} 筆同 key 的既有偏好。"));
        }

        return new InstanceTransferPreviewResult(
            parsed.Bundle.Namespace,
            parsed.Package.Encrypted,
            parsed.Package.Encrypted,
            summaries,
            conflicts
                .OrderBy(conflict => conflict.Section)
                .ThenBy(conflict => conflict.Identifier, StringComparer.OrdinalIgnoreCase)
                .ToArray());
    }

    private static IReadOnlyList<InstanceTransferSection> ResolveSelectedSections(IReadOnlyList<InstanceTransferSection>? sections)
    {
        var resolved = (sections ?? Array.Empty<InstanceTransferSection>())
            .Distinct()
            .ToArray();

        return resolved.Length == 0
            ? [InstanceTransferSection.SystemSettings, InstanceTransferSection.Memories, InstanceTransferSection.UserPreferences]
            : resolved;
    }

    private static IReadOnlyList<InstanceTransferSection> ResolveImportSections(
        InstanceTransferBundle bundle,
        IReadOnlyList<InstanceTransferSection>? requestedSections)
    {
        var available = bundle.Sections
            .Distinct()
            .ToArray();
        var selected = ResolveSelectedSections(requestedSections);
        var missing = selected
            .Where(section => !available.Contains(section))
            .ToArray();

        if (missing.Length > 0)
        {
            throw new InvalidOperationException($"匯入套件不包含以下項目：{string.Join("、", missing.Select(GetSectionLabel))}。");
        }

        return selected;
    }

    private static ParsedBundle ParseBundle(string packageBase64, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(packageBase64))
        {
            throw new InvalidOperationException("匯入套件內容不可為空。");
        }

        MemoryTransferPackage package;
        try
        {
            var packageJson = Encoding.UTF8.GetString(Convert.FromBase64String(packageBase64));
            package = JsonSerializer.Deserialize<MemoryTransferPackage>(packageJson, JsonOptions)
                ?? throw new InvalidOperationException("匯入套件格式無效。");
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException("匯入套件不是有效的 Base64 內容。", ex);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("匯入套件不是有效的 JSON。", ex);
        }

        if (package.Version != TransferFormatVersion)
        {
            throw new InvalidOperationException($"不支援的匯入套件版本 '{package.Version}'。");
        }

        if (!string.Equals(package.Format, "contexthub-instance-transfer", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("不支援的匯入套件格式。");
        }

        var payloadJson = package.Encrypted
            ? Decrypt(package, passphrase)
            : DecodeUtf8(package.PayloadBase64);

        try
        {
            var bundle = JsonSerializer.Deserialize<InstanceTransferBundle>(payloadJson, JsonOptions)
                ?? throw new InvalidOperationException("匯入套件內容為空。");
            return new ParsedBundle(package, bundle);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("匯入套件內容格式無效。", ex);
        }
    }

    private static MemoryTransferPackage CreatePackage(string bundleJson, string? passphrase)
    {
        if (string.IsNullOrWhiteSpace(passphrase))
        {
            return new MemoryTransferPackage(
                TransferFormatVersion,
                "contexthub-instance-transfer",
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
            "contexthub-instance-transfer",
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
            throw new InvalidOperationException("此匯入套件已加密，請提供匯入密碼。");
        }

        if (string.IsNullOrWhiteSpace(package.SaltBase64) || string.IsNullOrWhiteSpace(package.NonceBase64))
        {
            throw new InvalidOperationException("加密匯入套件缺少必要 metadata。");
        }

        var payload = Convert.FromBase64String(package.PayloadBase64);
        if (payload.Length < TagSize)
        {
            throw new InvalidOperationException("加密匯入套件內容無效。");
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
            throw new InvalidOperationException("匯入密碼錯誤，或套件內容已損毀。", ex);
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
            throw new InvalidOperationException("匯入套件內容不是有效的 Base64。", ex);
        }
    }

    private static string BuildFileName(bool encrypted)
        => $"contexthub-instance-transfer_{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}_{(encrypted ? "encrypted" : "plain")}.json";

    private static string GetSectionLabel(InstanceTransferSection section)
        => section switch
        {
            InstanceTransferSection.SystemSettings => "系統設定",
            InstanceTransferSection.Memories => "記憶資料",
            InstanceTransferSection.UserPreferences => "使用者偏好",
            _ => section.ToString()
        };

    private static InstanceTransferSectionSummaryResult BuildSummary(
        InstanceTransferSection section,
        int totalItems,
        int newItems,
        int conflictItems,
        string summary)
        => new(
            section,
            GetSectionLabel(section),
            totalItems,
            newItems,
            conflictItems,
            summary);

    private static string NormalizePreferenceKey(string key)
        => key.Trim().ToLowerInvariant();

    private sealed record ParsedBundle(
        MemoryTransferPackage Package,
        InstanceTransferBundle Bundle);

    private sealed record InstanceTransferBundle(
        int Version,
        string Namespace,
        DateTimeOffset ExportedAtUtc,
        IReadOnlyList<InstanceTransferSection> Sections,
        ExportedSystemSettings? SystemSettings,
        string? MemoryPackageBase64,
        IReadOnlyList<ExportedUserPreference>? UserPreferences);

    private sealed record ExportedSystemSettings(
        InstanceBehaviorSettingsUpdateRequest Behavior,
        ExportedDashboardAuthSettings DashboardAuth);

    private sealed record ExportedDashboardAuthSettings(
        string AdminUsername,
        int SessionTimeoutMinutes);

    private sealed record ExportedUserPreference(
        string Key,
        UserPreferenceKind Kind,
        string Title,
        string Content,
        string Rationale,
        IReadOnlyList<string> Tags,
        decimal Importance,
        decimal Confidence,
        MemoryStatus Status);

    private sealed record MemoryTransferPackage(
        int Version,
        string Format,
        bool Encrypted,
        string Algorithm,
        string PayloadBase64,
        string? SaltBase64,
        string? NonceBase64);
}

using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Dashboard.Services;

public interface IDashboardRuntimeSettingsAccessor
{
    Task<InstanceSettingsSnapshot> GetCurrentAsync(CancellationToken cancellationToken);
    Task RefreshAsync(CancellationToken cancellationToken);
}

public sealed class DashboardRuntimeSettingsAccessor(
    IServiceScopeFactory scopeFactory,
    IOptions<DashboardOptions> dashboardOptions,
    IOptions<MemoryOptions> memoryOptions) : IDashboardRuntimeSettingsAccessor
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private InstanceSettingsSnapshot? _cached;
    private DateTimeOffset _cachedAtUtc;

    public async Task<InstanceSettingsSnapshot> GetCurrentAsync(CancellationToken cancellationToken)
    {
        if (_cached is not null && DateTimeOffset.UtcNow - _cachedAtUtc <= TimeSpan.FromSeconds(10))
        {
            return _cached;
        }

        await RefreshAsync(cancellationToken);
        return _cached ?? CreateFallbackSnapshot(dashboardOptions.Value, memoryOptions.Value);
    }

    public async Task RefreshAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var service = scope.ServiceProvider.GetRequiredService<IInstanceSettingsService>();
            _cached = await service.GetSnapshotAsync(cancellationToken);
            _cachedAtUtc = DateTimeOffset.UtcNow;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static InstanceSettingsSnapshot CreateFallbackSnapshot(DashboardOptions dashboardOptions, MemoryOptions memoryOptions)
        => new(
            dashboardOptions.InstanceId,
            memoryOptions.Namespace,
            dashboardOptions.ComposeProject,
            BuildMetadata.Current.Version,
            BuildMetadata.Current.TimestampUtc,
            0,
            null,
            DashboardInstanceSettingsService.CreateDefaultBehaviorSettings(dashboardOptions),
            new InstanceDashboardAuthSettingsResult(
                dashboardOptions.AdminUsername,
                dashboardOptions.SessionTimeoutMinutes),
            new ConversationAutomationStatusResult(0, 0, 0, string.Empty));
}

public sealed class DashboardInstanceSettingsService(
    MemoryDbContext dbContext,
    IOptions<DashboardOptions> dashboardOptionsAccessor,
    IOptions<MemoryOptions> memoryOptionsAccessor,
    IPasswordHasher<object> passwordHasher) : IInstanceSettingsService
{
    private const string BehaviorSettingKey = "behavior";
    private const string DashboardAuthSettingKey = "dashboard-auth";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly DashboardOptions _dashboardOptions = dashboardOptionsAccessor.Value;
    private readonly MemoryOptions _memoryOptions = memoryOptionsAccessor.Value;

    public async Task<InstanceSettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var settings = await LoadSettingsAsync(cancellationToken);
        return await CreateSnapshotAsync(settings, cancellationToken);
    }

    public async Task<InstanceSettingsSnapshot> UpdateAsync(InstanceSettingsUpdateRequest request, string updatedBy, CancellationToken cancellationToken)
    {
        Validate(request);

        var settings = await LoadSettingsAsync(cancellationToken);
        var behavior = new StoredBehaviorSettings(
            request.Behavior.ConversationAutomationEnabled,
            request.Behavior.HostEventIngestionEnabled,
            request.Behavior.AgentSupplementalIngestionEnabled,
            request.Behavior.IdleThresholdMinutes,
            request.Behavior.PromotionMode.Trim(),
            request.Behavior.ExcerptMaxLength,
            ProjectContext.Normalize(request.Behavior.DefaultProjectId),
            request.Behavior.DefaultQueryMode,
            request.Behavior.DefaultUseSummaryLayer,
            request.Behavior.SharedSummaryAutoRefreshEnabled,
            new DashboardSnapshotPollingSettingsResult(
                request.Behavior.SnapshotPolling.StatusCoreSeconds,
                request.Behavior.SnapshotPolling.EmbeddingRuntimeSeconds,
                request.Behavior.SnapshotPolling.DependenciesHealthSeconds,
                request.Behavior.SnapshotPolling.DockerHostSeconds,
                request.Behavior.SnapshotPolling.DependencyResourcesSeconds,
                request.Behavior.SnapshotPolling.RecentOperationsSeconds,
                request.Behavior.SnapshotPolling.ResourceChartSeconds),
            request.Behavior.OverviewPollingSeconds,
            request.Behavior.MetricsPollingSeconds,
            request.Behavior.JobsPollingSeconds,
            request.Behavior.LogsPollingSeconds,
            request.Behavior.PerformancePollingSeconds);

        var passwordHash = string.IsNullOrWhiteSpace(request.DashboardAuth.NewPassword)
            ? settings.DashboardAuth.AdminPasswordHash
            : passwordHasher.HashPassword(new object(), request.DashboardAuth.NewPassword);

        var dashboardAuth = new StoredDashboardAuthSettings(
            request.DashboardAuth.AdminUsername.Trim(),
            passwordHash,
            request.DashboardAuth.SessionTimeoutMinutes);

        UpsertSetting(BehaviorSettingKey, behavior, updatedBy, settings.StoredSettings);
        UpsertSetting(DashboardAuthSettingKey, dashboardAuth, updatedBy, settings.StoredSettings);
        await dbContext.SaveChangesAsync(cancellationToken);
        return await CreateSnapshotAsync(await LoadSettingsAsync(cancellationToken), cancellationToken);
    }

    public async Task<InstanceSettingsSnapshot> ResetAsync(string updatedBy, CancellationToken cancellationToken)
    {
        var rows = await dbContext.InstanceSettings
            .Where(x => x.InstanceId == _dashboardOptions.InstanceId)
            .ToListAsync(cancellationToken);

        if (rows.Count > 0)
        {
            dbContext.InstanceSettings.RemoveRange(rows);
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        _ = updatedBy;
        return await CreateSnapshotAsync(await LoadSettingsAsync(cancellationToken), cancellationToken);
    }

    public async Task<DashboardAuthenticationSettings> GetDashboardAuthenticationSettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var settings = await LoadSettingsAsync(cancellationToken);
            return new DashboardAuthenticationSettings(
                settings.DashboardAuth.AdminUsername,
                settings.DashboardAuth.AdminPasswordHash,
                settings.DashboardAuth.SessionTimeoutMinutes);
        }
        catch
        {
            return new DashboardAuthenticationSettings(
                _dashboardOptions.AdminUsername,
                _dashboardOptions.AdminPasswordHash,
                _dashboardOptions.SessionTimeoutMinutes);
        }
    }

    internal static InstanceBehaviorSettingsResult CreateDefaultBehaviorSettings(DashboardOptions dashboardOptions)
        => new(
            ConversationAutomationEnabled: false,
            HostEventIngestionEnabled: true,
            AgentSupplementalIngestionEnabled: true,
            IdleThresholdMinutes: 20,
            PromotionMode: "Automatic",
            ExcerptMaxLength: 240,
            DefaultProjectId: ProjectContext.DefaultProjectId,
            DefaultQueryMode: MemoryQueryMode.CurrentOnly,
            DefaultUseSummaryLayer: false,
            SharedSummaryAutoRefreshEnabled: true,
            SnapshotPolling: DashboardSnapshotPollingDefaults.Create(),
            OverviewPollingSeconds: dashboardOptions.Polling.OverviewSeconds,
            MetricsPollingSeconds: dashboardOptions.Polling.MetricsSeconds,
            JobsPollingSeconds: dashboardOptions.Polling.JobsSeconds,
            LogsPollingSeconds: dashboardOptions.Polling.LogsSeconds,
            PerformancePollingSeconds: dashboardOptions.Polling.PerformanceSeconds);

    private static void Validate(InstanceSettingsUpdateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.DashboardAuth.AdminUsername))
        {
            throw new InvalidOperationException("AdminUsername is required.");
        }

        if (request.DashboardAuth.SessionTimeoutMinutes is < 30 or > 1440)
        {
            throw new InvalidOperationException("SessionTimeoutMinutes must be between 30 and 1440.");
        }

        if (request.Behavior.IdleThresholdMinutes is < 1 or > 1440)
        {
            throw new InvalidOperationException("IdleThresholdMinutes must be between 1 and 1440.");
        }

        if (request.Behavior.ExcerptMaxLength is < 32 or > 4000)
        {
            throw new InvalidOperationException("ExcerptMaxLength must be between 32 and 4000.");
        }

        if (string.IsNullOrWhiteSpace(request.Behavior.PromotionMode))
        {
            throw new InvalidOperationException("PromotionMode is required.");
        }

        if (string.IsNullOrWhiteSpace(request.Behavior.DefaultProjectId))
        {
            throw new InvalidOperationException("DefaultProjectId is required.");
        }

        var pollingValues = new[]
        {
            request.Behavior.OverviewPollingSeconds,
            request.Behavior.MetricsPollingSeconds,
            request.Behavior.JobsPollingSeconds,
            request.Behavior.LogsPollingSeconds,
            request.Behavior.PerformancePollingSeconds
        };

        if (pollingValues.Any(value => value is < 1 or > 3600))
        {
            throw new InvalidOperationException("Polling seconds must be between 1 and 3600.");
        }

        var snapshotPollingValues = new[]
        {
            request.Behavior.SnapshotPolling.StatusCoreSeconds,
            request.Behavior.SnapshotPolling.EmbeddingRuntimeSeconds,
            request.Behavior.SnapshotPolling.DependenciesHealthSeconds,
            request.Behavior.SnapshotPolling.DockerHostSeconds,
            request.Behavior.SnapshotPolling.DependencyResourcesSeconds,
            request.Behavior.SnapshotPolling.RecentOperationsSeconds,
            request.Behavior.SnapshotPolling.ResourceChartSeconds
        };

        if (snapshotPollingValues.Any(value => value is < 1 or > 3600))
        {
            throw new InvalidOperationException("Snapshot polling seconds must be between 1 and 3600.");
        }

        if (string.IsNullOrWhiteSpace(request.DashboardAuth.NewPassword) &&
            string.IsNullOrWhiteSpace(request.DashboardAuth.ConfirmPassword))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(request.DashboardAuth.NewPassword))
        {
            throw new InvalidOperationException("NewPassword is required when ConfirmPassword is provided.");
        }

        if (request.DashboardAuth.NewPassword.Length < 8)
        {
            throw new InvalidOperationException("NewPassword must be at least 8 characters.");
        }

        if (!string.Equals(request.DashboardAuth.NewPassword, request.DashboardAuth.ConfirmPassword, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("NewPassword and ConfirmPassword do not match.");
        }
    }

    private async Task<ResolvedSettings> LoadSettingsAsync(CancellationToken cancellationToken)
    {
        var rows = await dbContext.InstanceSettings
            .AsTracking()
            .Where(x => x.InstanceId == _dashboardOptions.InstanceId)
            .ToListAsync(cancellationToken);

        var defaultBehavior = CreateDefaultBehaviorSettings(_dashboardOptions);
        var defaultDashboardAuth = new StoredDashboardAuthSettings(
            _dashboardOptions.AdminUsername,
            _dashboardOptions.AdminPasswordHash,
            _dashboardOptions.SessionTimeoutMinutes);

        var storedBehavior = DeserializeBehavior(rows, defaultBehavior)
            ?? new StoredBehaviorSettings(
                defaultBehavior.ConversationAutomationEnabled,
                defaultBehavior.HostEventIngestionEnabled,
                defaultBehavior.AgentSupplementalIngestionEnabled,
                defaultBehavior.IdleThresholdMinutes,
                defaultBehavior.PromotionMode,
                defaultBehavior.ExcerptMaxLength,
                defaultBehavior.DefaultProjectId,
                defaultBehavior.DefaultQueryMode,
                defaultBehavior.DefaultUseSummaryLayer,
                defaultBehavior.SharedSummaryAutoRefreshEnabled,
                defaultBehavior.SnapshotPolling,
                defaultBehavior.OverviewPollingSeconds,
                defaultBehavior.MetricsPollingSeconds,
                defaultBehavior.JobsPollingSeconds,
                defaultBehavior.LogsPollingSeconds,
                defaultBehavior.PerformancePollingSeconds);

        var storedDashboardAuth = Deserialize<StoredDashboardAuthSettings>(rows, DashboardAuthSettingKey)
            ?? defaultDashboardAuth;

        return new ResolvedSettings(rows, storedBehavior, storedDashboardAuth);
    }

    private async Task<InstanceSettingsSnapshot> CreateSnapshotAsync(ResolvedSettings settings, CancellationToken cancellationToken)
    {
        var build = BuildMetadata.Current;
        DateTimeOffset? lastUpdatedAt = settings.StoredSettings.Count == 0
            ? null
            : settings.StoredSettings.Max(x => x.UpdatedAt);
        var revision = settings.StoredSettings.Sum(x => x.Revision);
        var automationStatus = await BuildAutomationStatusAsync(cancellationToken);

        return new InstanceSettingsSnapshot(
            _dashboardOptions.InstanceId,
            _memoryOptions.Namespace,
            _dashboardOptions.ComposeProject,
            build.Version,
            build.TimestampUtc,
            revision,
            lastUpdatedAt,
            new InstanceBehaviorSettingsResult(
                settings.Behavior.ConversationAutomationEnabled,
                settings.Behavior.HostEventIngestionEnabled,
                settings.Behavior.AgentSupplementalIngestionEnabled,
                settings.Behavior.IdleThresholdMinutes,
                settings.Behavior.PromotionMode,
                settings.Behavior.ExcerptMaxLength,
                settings.Behavior.DefaultProjectId,
                settings.Behavior.DefaultQueryMode,
                settings.Behavior.DefaultUseSummaryLayer,
                settings.Behavior.SharedSummaryAutoRefreshEnabled,
                settings.Behavior.SnapshotPolling,
                settings.Behavior.OverviewPollingSeconds,
                settings.Behavior.MetricsPollingSeconds,
                settings.Behavior.JobsPollingSeconds,
                settings.Behavior.LogsPollingSeconds,
                settings.Behavior.PerformancePollingSeconds),
            new InstanceDashboardAuthSettingsResult(
                settings.DashboardAuth.AdminUsername,
                settings.DashboardAuth.SessionTimeoutMinutes),
            automationStatus);
    }

    private async Task<ConversationAutomationStatusResult> BuildAutomationStatusAsync(CancellationToken cancellationToken)
    {
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var recentCheckpoints = await dbContext.ConversationCheckpoints.CountAsync(x => x.CreatedAt >= since, cancellationToken);
        var pendingInsights = await dbContext.ConversationInsights.CountAsync(x => x.PromotionStatus == ConversationPromotionStatus.Pending, cancellationToken);
        var pendingPromotions = await dbContext.MemoryJobs.CountAsync(
            x => x.JobType == MemoryJobType.PromoteConversationInsights &&
                 (x.Status == MemoryJobStatus.Pending || x.Status == MemoryJobStatus.Running),
            cancellationToken);
        var lastPromotionError = await dbContext.MemoryJobs
            .Where(x => x.JobType == MemoryJobType.PromoteConversationInsights && x.Status == MemoryJobStatus.Failed)
            .OrderByDescending(x => x.CompletedAt ?? x.CreatedAt)
            .Select(x => x.Error)
            .FirstOrDefaultAsync(cancellationToken);

        return new ConversationAutomationStatusResult(
            recentCheckpoints,
            pendingInsights,
            pendingPromotions,
            lastPromotionError ?? string.Empty);
    }

    private void UpsertSetting<TValue>(
        string key,
        TValue value,
        string updatedBy,
        IReadOnlyList<InstanceSetting> existingRows)
    {
        var row = existingRows.FirstOrDefault(x => string.Equals(x.SettingKey, key, StringComparison.Ordinal))
            ?? new InstanceSetting
            {
                InstanceId = _dashboardOptions.InstanceId,
                SettingKey = key,
                Revision = 0,
                UpdatedAt = DateTimeOffset.UtcNow
            };

        row.ValueJson = JsonSerializer.Serialize(value, JsonOptions);
        row.Revision = row.Revision <= 0 ? 1 : row.Revision + 1;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        row.UpdatedBy = string.IsNullOrWhiteSpace(updatedBy) ? "dashboard" : updatedBy;

        if (dbContext.Entry(row).State == EntityState.Detached)
        {
            dbContext.InstanceSettings.Add(row);
        }
    }

    private static T? Deserialize<T>(IReadOnlyList<InstanceSetting> rows, string key)
        where T : class
    {
        var row = rows.FirstOrDefault(x => string.Equals(x.SettingKey, key, StringComparison.Ordinal));
        return row is null
            ? null
            : JsonSerializer.Deserialize<T>(row.ValueJson, JsonOptions);
    }

    private static StoredBehaviorSettings? DeserializeBehavior(
        IReadOnlyList<InstanceSetting> rows,
        InstanceBehaviorSettingsResult defaultBehavior)
    {
        var row = rows.FirstOrDefault(x => string.Equals(x.SettingKey, BehaviorSettingKey, StringComparison.Ordinal));
        if (row is null)
        {
            return null;
        }

        var behavior = InstanceBehaviorSettingsSerializer.DeserializeOrDefault(row.ValueJson, () => defaultBehavior);
        return new StoredBehaviorSettings(
            behavior.ConversationAutomationEnabled,
            behavior.HostEventIngestionEnabled,
            behavior.AgentSupplementalIngestionEnabled,
            behavior.IdleThresholdMinutes,
            behavior.PromotionMode,
            behavior.ExcerptMaxLength,
            behavior.DefaultProjectId,
            behavior.DefaultQueryMode,
            behavior.DefaultUseSummaryLayer,
            behavior.SharedSummaryAutoRefreshEnabled,
            behavior.SnapshotPolling,
            behavior.OverviewPollingSeconds,
            behavior.MetricsPollingSeconds,
            behavior.JobsPollingSeconds,
            behavior.LogsPollingSeconds,
            behavior.PerformancePollingSeconds);
    }

    private sealed record ResolvedSettings(
        IReadOnlyList<InstanceSetting> StoredSettings,
        StoredBehaviorSettings Behavior,
        StoredDashboardAuthSettings DashboardAuth);

    private sealed record StoredBehaviorSettings(
        bool ConversationAutomationEnabled,
        bool HostEventIngestionEnabled,
        bool AgentSupplementalIngestionEnabled,
        int IdleThresholdMinutes,
        string PromotionMode,
        int ExcerptMaxLength,
        string DefaultProjectId,
        MemoryQueryMode DefaultQueryMode,
        bool DefaultUseSummaryLayer,
        bool SharedSummaryAutoRefreshEnabled,
        DashboardSnapshotPollingSettingsResult SnapshotPolling,
        int OverviewPollingSeconds,
        int MetricsPollingSeconds,
        int JobsPollingSeconds,
        int LogsPollingSeconds,
        int PerformancePollingSeconds);

    private sealed record StoredDashboardAuthSettings(
        string AdminUsername,
        string AdminPasswordHash,
        int SessionTimeoutMinutes);
}

public sealed class LocalOnlyInstanceSettingsService(
    IOptions<DashboardOptions> dashboardOptionsAccessor,
    IOptions<MemoryOptions> memoryOptionsAccessor) : IInstanceSettingsService
{
    private readonly DashboardOptions _dashboardOptions = dashboardOptionsAccessor.Value;
    private readonly MemoryOptions _memoryOptions = memoryOptionsAccessor.Value;

    public Task<InstanceSettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => Task.FromResult(CreateSnapshot());

    public Task<InstanceSettingsSnapshot> UpdateAsync(InstanceSettingsUpdateRequest request, string updatedBy, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Dashboard instance settings persistence requires ConnectionStrings:Postgres.");

    public Task<InstanceSettingsSnapshot> ResetAsync(string updatedBy, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Dashboard instance settings persistence requires ConnectionStrings:Postgres.");

    public Task<DashboardAuthenticationSettings> GetDashboardAuthenticationSettingsAsync(CancellationToken cancellationToken)
        => Task.FromResult(new DashboardAuthenticationSettings(
            _dashboardOptions.AdminUsername,
            _dashboardOptions.AdminPasswordHash,
            _dashboardOptions.SessionTimeoutMinutes));

    private InstanceSettingsSnapshot CreateSnapshot()
    {
        var build = BuildMetadata.Current;
        return new InstanceSettingsSnapshot(
            _dashboardOptions.InstanceId,
            _memoryOptions.Namespace,
            _dashboardOptions.ComposeProject,
            build.Version,
            build.TimestampUtc,
            0,
            null,
            DashboardInstanceSettingsService.CreateDefaultBehaviorSettings(_dashboardOptions),
            new InstanceDashboardAuthSettingsResult(
                _dashboardOptions.AdminUsername,
                _dashboardOptions.SessionTimeoutMinutes),
            new ConversationAutomationStatusResult(0, 0, 0, string.Empty));
    }
}

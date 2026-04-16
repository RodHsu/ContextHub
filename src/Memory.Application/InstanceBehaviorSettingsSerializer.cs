using System.Text.Json;

namespace Memory.Application;

public static class InstanceBehaviorSettingsSerializer
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static InstanceBehaviorSettingsResult DeserializeOrDefault(string? json, Func<InstanceBehaviorSettingsResult> defaultFactory)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return defaultFactory();
        }

        try
        {
            var current = JsonSerializer.Deserialize<InstanceBehaviorSettingsResult>(json, JsonOptions);
            if (current is not null)
            {
                return Normalize(current);
            }
        }
        catch (JsonException)
        {
        }

        try
        {
            var legacy = JsonSerializer.Deserialize<LegacyInstanceBehaviorSettings>(json, JsonOptions);
            if (legacy is not null)
            {
                return new InstanceBehaviorSettingsResult(
                    legacy.ConversationAutomationEnabled,
                    legacy.HostEventIngestionEnabled,
                    legacy.AgentSupplementalIngestionEnabled,
                    legacy.IdleThresholdMinutes,
                    legacy.PromotionMode,
                    legacy.ExcerptMaxLength,
                    legacy.DefaultProjectId,
                    legacy.DefaultQueryMode,
                    legacy.DefaultUseSummaryLayer,
                    legacy.SharedSummaryAutoRefreshEnabled,
                    DashboardSnapshotPollingDefaults.Create(),
                    legacy.OverviewPollingSeconds,
                    legacy.MetricsPollingSeconds,
                    legacy.JobsPollingSeconds,
                    legacy.LogsPollingSeconds,
                    legacy.PerformancePollingSeconds);
            }
        }
        catch (JsonException)
        {
        }

        return defaultFactory();
    }

    public static InstanceBehaviorSettingsResult Normalize(InstanceBehaviorSettingsResult settings)
        => settings with
        {
            SnapshotPolling = settings.SnapshotPolling ?? DashboardSnapshotPollingDefaults.Create()
        };

    private sealed record LegacyInstanceBehaviorSettings(
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
        int OverviewPollingSeconds,
        int MetricsPollingSeconds,
        int JobsPollingSeconds,
        int LogsPollingSeconds,
        int PerformancePollingSeconds);
}

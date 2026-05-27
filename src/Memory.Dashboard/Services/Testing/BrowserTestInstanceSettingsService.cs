using Memory.Application;

namespace Memory.Dashboard.Services.Testing;

internal sealed class BrowserTestInstanceSettingsService(
    DashboardBrowserTestProfileAccessor profileAccessor) : IInstanceSettingsService
{
    public Task<InstanceSettingsSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
        => Task.FromResult(CreateSnapshot());

    public Task<InstanceSettingsSnapshot> UpdateAsync(InstanceSettingsUpdateRequest request, string updatedBy, CancellationToken cancellationToken)
        => Task.FromResult(CreateSnapshot() with
        {
            Behavior = new InstanceBehaviorSettingsResult(
                request.Behavior.ConversationAutomationEnabled,
                request.Behavior.HostEventIngestionEnabled,
                request.Behavior.AgentSupplementalIngestionEnabled,
                request.Behavior.IdleThresholdMinutes,
                request.Behavior.PromotionMode,
                request.Behavior.ExcerptMaxLength,
                request.Behavior.DefaultProjectId,
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
                    request.Behavior.SnapshotPolling.ResourceChartSeconds,
                    request.Behavior.SnapshotPolling.MemoryGraphIndexSeconds),
                request.Behavior.OverviewPollingSeconds,
                request.Behavior.MetricsPollingSeconds,
                request.Behavior.JobsPollingSeconds,
                request.Behavior.LogsPollingSeconds,
                request.Behavior.PerformancePollingSeconds),
            DashboardAuth = new InstanceDashboardAuthSettingsResult(
                request.DashboardAuth.AdminUsername,
                request.DashboardAuth.SessionTimeoutMinutes),
            SettingsRevision = 3,
            SettingsUpdatedAtUtc = DateTimeOffset.UtcNow
        });

    public Task<InstanceSettingsSnapshot> ResetAsync(string updatedBy, CancellationToken cancellationToken)
        => Task.FromResult(CreateSnapshot() with
        {
            SettingsRevision = 0,
            SettingsUpdatedAtUtc = null
        });

    public Task<DashboardAuthenticationSettings> GetDashboardAuthenticationSettingsAsync(CancellationToken cancellationToken)
        => Task.FromResult(new DashboardAuthenticationSettings(
            "admin",
            "AQAAAAIAAYagAAAAEIbguUQEApMQehlC51gjy+uGulsE4ahRI7UtbdAlSsGMynNrNM3J3KfsJL+3IuBUxQ==",
            480));

    private InstanceSettingsSnapshot CreateSnapshot()
    {
        var profile = profileAccessor.GetProfile();
        var dense = profile == DashboardBrowserTestProfile.Dense;

        return new InstanceSettingsSnapshot(
            "browser-test",
            dense ? "context-hub-dense" : "context-hub",
            "contexthub",
            "2026.04.13-test",
            DateTimeOffset.Parse("2026-04-13T02:15:00+00:00"),
            dense ? 5 : 2,
            DateTimeOffset.UtcNow.AddMinutes(-4),
            new InstanceBehaviorSettingsResult(
                ConversationAutomationEnabled: true,
                HostEventIngestionEnabled: true,
                AgentSupplementalIngestionEnabled: dense,
                IdleThresholdMinutes: dense ? 35 : 20,
                PromotionMode: dense ? "Automatic + Staged Review" : "Automatic",
                ExcerptMaxLength: dense ? 512 : 240,
                DefaultProjectId: dense ? "context-hub-dev-project-with-long-name" : ProjectContext.DefaultProjectId,
                DefaultQueryMode: dense ? MemoryQueryMode.CurrentPlusReferencedProjects : MemoryQueryMode.CurrentOnly,
                DefaultUseSummaryLayer: dense,
                SharedSummaryAutoRefreshEnabled: true,
                SnapshotPolling: new DashboardSnapshotPollingSettingsResult(
                    StatusCoreSeconds: 5,
                    EmbeddingRuntimeSeconds: 5,
                    DependenciesHealthSeconds: 5,
                    DockerHostSeconds: 5,
                    DependencyResourcesSeconds: 5,
                    RecentOperationsSeconds: 5,
                    ResourceChartSeconds: 5,
                    MemoryGraphIndexSeconds: 15),
                OverviewPollingSeconds: 5,
                MetricsPollingSeconds: 5,
                JobsPollingSeconds: 5,
                LogsPollingSeconds: 5,
                PerformancePollingSeconds: 5),
            new InstanceDashboardAuthSettingsResult(
                dense ? "dashboard-admin-long-identifier" : "admin",
                480),
            new ConversationAutomationStatusResult(
                dense ? 12 : 0,
                dense ? 3 : 0,
                dense ? 1 : 0,
                string.Empty));
    }
}

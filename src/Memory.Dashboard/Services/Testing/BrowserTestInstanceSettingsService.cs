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
                    request.Behavior.SnapshotPolling.ResourceChartSeconds),
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
                    StatusCoreSeconds: 30,
                    EmbeddingRuntimeSeconds: 30,
                    DependenciesHealthSeconds: 10,
                    DockerHostSeconds: 30,
                    DependencyResourcesSeconds: 5,
                    RecentOperationsSeconds: 5,
                    ResourceChartSeconds: dense ? 1 : 3),
                OverviewPollingSeconds: 10,
                MetricsPollingSeconds: 3,
                JobsPollingSeconds: 8,
                LogsPollingSeconds: 10,
                PerformancePollingSeconds: 30),
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

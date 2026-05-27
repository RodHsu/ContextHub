namespace Memory.Application;

public static class DashboardSnapshotPollingDefaults
{
    public static DashboardSnapshotPollingSettingsResult Create()
        => new(
            StatusCoreSeconds: 5,
            EmbeddingRuntimeSeconds: 5,
            DependenciesHealthSeconds: 5,
            DockerHostSeconds: 5,
            DependencyResourcesSeconds: 5,
            RecentOperationsSeconds: 5,
            ResourceChartSeconds: 5,
            MemoryGraphIndexSeconds: 15);

    public static DashboardSnapshotPollingSettingsUpdateRequest CreateUpdate()
        => new(
            StatusCoreSeconds: 5,
            EmbeddingRuntimeSeconds: 5,
            DependenciesHealthSeconds: 5,
            DockerHostSeconds: 5,
            DependencyResourcesSeconds: 5,
            RecentOperationsSeconds: 5,
            ResourceChartSeconds: 5,
            MemoryGraphIndexSeconds: 15);
}

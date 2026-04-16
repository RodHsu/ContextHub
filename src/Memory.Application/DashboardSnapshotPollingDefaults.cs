namespace Memory.Application;

public static class DashboardSnapshotPollingDefaults
{
    public static DashboardSnapshotPollingSettingsResult Create()
        => new(
            StatusCoreSeconds: 30,
            EmbeddingRuntimeSeconds: 30,
            DependenciesHealthSeconds: 10,
            DockerHostSeconds: 30,
            DependencyResourcesSeconds: 5,
            RecentOperationsSeconds: 5,
            ResourceChartSeconds: 1);

    public static DashboardSnapshotPollingSettingsUpdateRequest CreateUpdate()
        => new(
            StatusCoreSeconds: 30,
            EmbeddingRuntimeSeconds: 30,
            DependenciesHealthSeconds: 10,
            DockerHostSeconds: 30,
            DependencyResourcesSeconds: 5,
            RecentOperationsSeconds: 5,
            ResourceChartSeconds: 1);
}

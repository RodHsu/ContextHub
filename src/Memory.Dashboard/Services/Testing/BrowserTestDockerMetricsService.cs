using Memory.Application;

namespace Memory.Dashboard.Services.Testing;

internal sealed class BrowserTestDockerMetricsService : IDockerMetricsService
{
    private readonly DashboardBrowserTestProfileAccessor _profileAccessor;

    public BrowserTestDockerMetricsService(DashboardBrowserTestProfileAccessor profileAccessor)
    {
        _profileAccessor = profileAccessor;
    }

    public Task<DockerStackSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var profile = _profileAccessor.Current;
        var now = DateTimeOffset.UtcNow;
        var containers = new[]
        {
            new DockerContainerMetricResult(
                "contexthub-postgres-1",
                "postgres",
                "pgvector/pgvector:pg17",
                "running",
                "healthy",
                0,
                0.8,
                1536L * 1024 * 1024,
                4096L * 1024 * 1024,
                24_000,
                22_000,
                18_000,
                12_000),
            new DockerContainerMetricResult(
                "contexthub-redis-1",
                "redis",
                "redis:7.4-alpine",
                "running",
                "healthy",
                1,
                0.3,
                192L * 1024 * 1024,
                1024L * 1024 * 1024,
                9_000,
                8_500,
                1_200,
                900),
            new DockerContainerMetricResult(
                "contexthub-embedding-service-1",
                "embedding-service",
                "context-hub/embedding-service:local",
                "running",
                "healthy",
                0,
                3.2,
                1024L * 1024 * 1024,
                4096L * 1024 * 1024,
                15_000,
                13_500,
                6_000,
                4_800),
            new DockerContainerMetricResult(
                "contexthub-mcp-server-1",
                "mcp-server",
                "context-hub/mcp-server:local",
                "running",
                "healthy",
                0,
                1.4,
                640L * 1024 * 1024,
                2048L * 1024 * 1024,
                12_500,
                11_800,
                4_200,
                3_800),
            new DockerContainerMetricResult(
                "contexthub-dashboard-1",
                "dashboard",
                "context-hub/dashboard:local",
                "running",
                "healthy",
                0,
                0.9,
                256L * 1024 * 1024,
                1024L * 1024 * 1024,
                8_000,
                7_600,
                1_000,
                780)
        };

        return Task.FromResult(new DockerStackSnapshotResult(
            "Healthy",
            string.Empty,
            new DockerHostSummaryResult(
                profile == DashboardBrowserTestProfile.Dense ? "dense-browser-docker-host" : "docker-desktop",
                "28.5.2",
                "Docker Desktop",
                "linux",
                profile == DashboardBrowserTestProfile.Dense ? 16 : 8,
                47L * 1024 * 1024 * 1024,
                31L * 1024 * 1024 * 1024,
                profile == DashboardBrowserTestProfile.Dense ? 12 : 5,
                profile == DashboardBrowserTestProfile.Dense ? 7 : 3,
                profile == DashboardBrowserTestProfile.Dense ? 5 : 2,
                now),
            containers,
            [
                new DockerImageSummaryResult("image-1", "context-hub/mcp:local", 512 * 1024 * 1024, 1),
                new DockerImageSummaryResult("image-2", "context-hub/dashboard:local", 390 * 1024 * 1024, 1)
            ],
            [
                new DockerVolumeSummaryResult("contexthub_postgres-data", "local", 1024 * 1024 * 1024, "/var/lib/docker/volumes/contexthub_postgres-data"),
                new DockerVolumeSummaryResult("contexthub_redis-data", "local", 256 * 1024 * 1024, "/var/lib/docker/volumes/contexthub_redis-data"),
                new DockerVolumeSummaryResult("contexthub_dashboard", "local", 128 * 1024 * 1024, "/var/lib/docker/volumes/contexthub_dashboard")
            ]));
    }

    public Task<RestartAppContainersResult> RestartAppContainersAsync(RestartAppContainersRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new RestartAppContainersResult(
            "browser-test",
            "contexthub",
            ["dashboard", "mcp-server", "worker", "embedding-service"],
            [],
            DateTimeOffset.UtcNow));
}

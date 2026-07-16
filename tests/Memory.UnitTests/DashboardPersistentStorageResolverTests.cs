using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class DashboardPersistentStorageResolverTests
{
    [Fact]
    public void Resolve_Should_Use_Mount_Destination_And_Volume_Name_For_Redis()
    {
        var snapshot = CreateSnapshot(
            new DockerContainerRuntimeSnapshot(
                new DockerContainerMetricResult("redis-1", "redis", "redis:7", "running", "healthy", 0, 0, 0, 0, 0, 0, 0, 0),
                [
                    new DockerContainerMountSnapshot("volume", "redis-data", "/var/lib/docker/volumes/redis-data/_data", "/data", true)
                ]),
            [
                new DockerVolumeSummaryResult("redis-data", "local", 123456, "/var/lib/docker/volumes/redis-data/_data")
            ]);

        var resolved = DashboardPersistentStorageResolver.Resolve(
            snapshot,
            snapshot.Containers.Single(),
            "/data");

        resolved.Should().NotBeNull();
        resolved!.DisplayName.Should().Be("redis-data");
        resolved.SizeBytes.Should().Be(123456);
        resolved.IsLogicalFallback.Should().BeFalse();
    }

    [Fact]
    public void Resolve_Should_Fall_Back_To_Bind_Source_When_Mount_Is_Bind()
    {
        var snapshot = CreateSnapshot(
            new DockerContainerRuntimeSnapshot(
                new DockerContainerMetricResult("postgres-1", "postgres", "pgvector:17", "running", "healthy", 0, 0, 0, 0, 0, 0, 0, 0),
                [
                    new DockerContainerMountSnapshot("bind", string.Empty, "T:/Docker/ContextHub/Data/postgres", "/var/lib/postgresql/data", true)
                ]),
            []);

        var resolved = DashboardPersistentStorageResolver.Resolve(
            snapshot,
            snapshot.Containers.Single(),
            "/var/lib/postgresql/data");

        resolved.Should().NotBeNull();
        resolved!.DisplayName.Should().Be("T:/Docker/ContextHub/Data/postgres");
        resolved.SizeBytes.Should().Be(0);
        resolved.IsLogicalFallback.Should().BeFalse();
    }

    [Fact]
    public void Resolve_Should_Return_Null_When_Target_Destination_Is_Not_Mounted()
    {
        var snapshot = CreateSnapshot(
            new DockerContainerRuntimeSnapshot(
                new DockerContainerMetricResult("redis-1", "redis", "redis:7", "running", "healthy", 0, 0, 0, 0, 0, 0, 0, 0),
                [
                    new DockerContainerMountSnapshot("volume", "redis-data", "/var/lib/docker/volumes/redis-data/_data", "/cache", true)
                ]),
            [
                new DockerVolumeSummaryResult("redis-data", "local", 123456, "/var/lib/docker/volumes/redis-data/_data")
            ]);

        var resolved = DashboardPersistentStorageResolver.Resolve(
            snapshot,
            snapshot.Containers.Single(),
            "/data");

        resolved.Should().BeNull();
    }

    [Fact]
    public void Resolve_Should_Use_Logical_Fallback_When_Docker_Does_Not_Report_Storage_Usage()
    {
        var snapshot = CreateSnapshot(
            new DockerContainerRuntimeSnapshot(
                new DockerContainerMetricResult("redis-1", "redis", "redis:7", "running", "healthy", 0, 0, 0, 0, 0, 0, 0, 0),
                [
                    new DockerContainerMountSnapshot("bind", string.Empty, "E:/Docker/ContextHub/Data/redis", "/data", true)
                ]),
            []);

        var resolved = DashboardPersistentStorageResolver.Resolve(
            snapshot,
            snapshot.Containers.Single(),
            "/data",
            654321,
            "Redis logical usage");

        resolved.Should().NotBeNull();
        resolved!.DisplayName.Should().Be("E:/Docker/ContextHub/Data/redis (Redis logical usage)");
        resolved.SizeBytes.Should().Be(654321);
        resolved.IsLogicalFallback.Should().BeTrue();
    }

    private static DockerRuntimeSnapshot CreateSnapshot(
        DockerContainerRuntimeSnapshot container,
        IReadOnlyList<DockerVolumeSummaryResult> volumes)
        => new(
            "Healthy",
            string.Empty,
            new DockerHostSummaryResult("docker-dev", "28.0", "Linux", "6.8", 8, 1024, 768, 3, 10, volumes.Count, DateTimeOffset.UtcNow),
            [container],
            volumes);
}

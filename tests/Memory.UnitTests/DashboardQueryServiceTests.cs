using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.UnitTests;

public sealed class DashboardQueryServiceTests
{
    [Fact]
    public async Task Monitoring_Should_Return_Unavailable_Telemetry_When_Snapshot_Is_Missing()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);
        var snapshotStore = new FakeDashboardSnapshotStore();
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardStatusCoreSnapshotPayload>(
            DashboardSnapshotKeys.StatusCore,
            now.AddSeconds(-5),
            30,
            now.AddSeconds(25),
            string.Empty,
            new DashboardStatusCoreSnapshotPayload(
                "mcp-server",
                "ContextHub",
                "1.2.3",
                now.AddMinutes(-2),
                "Http",
                "CPUExecutionProvider",
                "compact",
                "intfloat/multilingual-e5-small",
                384,
                512,
                6,
                8,
                true,
                42)));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardDependenciesHealthSnapshotPayload>(
            DashboardSnapshotKeys.DependenciesHealth,
            now.AddSeconds(-4),
            10,
            now.AddSeconds(6),
            string.Empty,
            new DashboardDependenciesHealthSnapshotPayload(
                [new DashboardServiceHealthResult("postgres", "Healthy", "ok")])));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardDockerHostResult>(
            DashboardSnapshotKeys.DockerHost,
            now.AddSeconds(-8),
            30,
            now.AddSeconds(22),
            string.Empty,
            new DashboardDockerHostResult(
                "Healthy",
                string.Empty,
                new DockerHostSummaryResult("docker-dev", "28.0", "Linux", "6.8", 8, 1024, 768, 5, 12, 3, now.AddSeconds(-8)))));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardDependencyResourcesResult>(
            DashboardSnapshotKeys.DependencyResources,
            now.AddSeconds(-4),
            5,
            now.AddSeconds(1),
            string.Empty,
            new DashboardDependencyResourcesResult("Healthy", string.Empty, [], [])));

        var service = new DashboardQueryService(
            new UnusedApplicationDbContext(),
            new UnusedStorageExplorerStore(),
            snapshotStore,
            new FixedTimeProvider(now));

        var monitoring = await service.GetMonitoringAsync(CancellationToken.None);

        monitoring.Redis.Status.Should().Be("Unavailable");
        monitoring.Postgres.Status.Should().Be("Unavailable");
        monitoring.SnapshotStatus.Should().NotBeNull();
        monitoring.SnapshotStatus!.Sections.Single(x => x.Key == DashboardSnapshotKeys.MonitoringStats).IsStale.Should().BeTrue();
    }

    [Fact]
    public async Task Overview_Should_Not_Become_Stale_When_Only_Resource_Chart_Is_Stale()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);
        var snapshotStore = new FakeDashboardSnapshotStore();
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardStatusCoreSnapshotPayload>(
            DashboardSnapshotKeys.StatusCore,
            now.AddSeconds(-5),
            30,
            now.AddSeconds(25),
            string.Empty,
            new DashboardStatusCoreSnapshotPayload(
                "mcp-server",
                "ContextHub",
                "1.2.3",
                now.AddMinutes(-2),
                "Http",
                "CPUExecutionProvider",
                "compact",
                "intfloat/multilingual-e5-small",
                384,
                512,
                6,
                8,
                true,
                42)));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardDependenciesHealthSnapshotPayload>(
            DashboardSnapshotKeys.DependenciesHealth,
            now.AddSeconds(-4),
            10,
            now.AddSeconds(6),
            string.Empty,
            new DashboardDependenciesHealthSnapshotPayload(
                [new DashboardServiceHealthResult("postgres", "Healthy", "ok")])));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardRecentOperationsSnapshotPayload>(
            DashboardSnapshotKeys.RecentOperations,
            now.AddSeconds(-3),
            5,
            now.AddSeconds(2),
            string.Empty,
            new DashboardRecentOperationsSnapshotPayload(
                [new DashboardOverviewMetricResult("jobs", "Jobs", 2, "count")],
                [],
                [])));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardResourceChartSnapshotPayload>(
            DashboardSnapshotKeys.ResourceChart,
            now.AddSeconds(-3),
            1,
            now.AddSeconds(-2),
            string.Empty,
            new DashboardResourceChartSnapshotPayload(
                [new DashboardResourceSampleResult(
                    now.AddSeconds(-3),
                    15,
                    45,
                    1024,
                    12,
                    8,
                    4,
                    2,
                    6,
                    3)])));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardDependencyResourcesResult>(
            DashboardSnapshotKeys.DependencyResources,
            now.AddSeconds(-4),
            5,
            now.AddSeconds(1),
            string.Empty,
            new DashboardDependencyResourcesResult("Healthy", string.Empty, [], [])));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardDockerHostResult>(
            DashboardSnapshotKeys.DockerHost,
            now.AddSeconds(-8),
            30,
            now.AddSeconds(22),
            string.Empty,
            new DashboardDockerHostResult(
                "Healthy",
                string.Empty,
                new DockerHostSummaryResult(
                    "docker-dev",
                    "28.0",
                    "Linux",
                    "6.8",
                    8,
                    1024,
                    768,
                    5,
                    12,
                    3,
                    now.AddSeconds(-8)))));

        var service = new DashboardQueryService(
            new UnusedApplicationDbContext(),
            new UnusedStorageExplorerStore(),
            snapshotStore,
            new FixedTimeProvider(now));

        var overview = await service.GetOverviewAsync(CancellationToken.None);

        overview.SnapshotStatus.Should().NotBeNull();
        overview.SnapshotStatus!.IsStale.Should().BeFalse();
        overview.SnapshotStatus.Warning.Should().BeEmpty();
        overview.SnapshotStatus.Sections.Single(x => x.Key == DashboardSnapshotKeys.ResourceChart).IsStale.Should().BeTrue();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class FakeDashboardSnapshotStore : IDashboardSnapshotStore
    {
        private readonly Dictionary<string, object> envelopes = new(StringComparer.Ordinal);

        public void Add<TPayload>(DashboardSnapshotEnvelope<TPayload> envelope)
            => envelopes[envelope.Key] = envelope;

        public Task<DashboardSnapshotEnvelope<TPayload>?> GetAsync<TPayload>(string key, CancellationToken cancellationToken)
        {
            if (envelopes.TryGetValue(key, out var envelope) && envelope is DashboardSnapshotEnvelope<TPayload> typed)
            {
                return Task.FromResult<DashboardSnapshotEnvelope<TPayload>?>(typed);
            }

            return Task.FromResult<DashboardSnapshotEnvelope<TPayload>?>(null);
        }

        public Task SetAsync<TPayload>(DashboardSnapshotEnvelope<TPayload> envelope, CancellationToken cancellationToken)
        {
            envelopes[envelope.Key] = envelope;
            return Task.CompletedTask;
        }
    }

    private sealed class UnusedStorageExplorerStore : IStorageExplorerStore
    {
        public Task<IReadOnlyList<StorageTableSummaryResult>> ListTablesAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StorageTableRowsResult> GetRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class UnusedApplicationDbContext : IApplicationDbContext
    {
        public DbSet<InstanceSetting> InstanceSettings => throw new NotSupportedException();
        public DbSet<MemoryItem> MemoryItems => throw new NotSupportedException();
        public DbSet<MemoryItemRevision> MemoryItemRevisions => throw new NotSupportedException();
        public DbSet<MemoryItemChunk> MemoryItemChunks => throw new NotSupportedException();
        public DbSet<MemoryChunkVector> MemoryChunkVectors => throw new NotSupportedException();
        public DbSet<MemoryLink> MemoryLinks => throw new NotSupportedException();
        public DbSet<MemoryJob> MemoryJobs => throw new NotSupportedException();
        public DbSet<RuntimeLogEntry> RuntimeLogEntries => throw new NotSupportedException();
        public DbSet<LogIngestionCheckpoint> LogIngestionCheckpoints => throw new NotSupportedException();
        public DbSet<ConversationSession> ConversationSessions => throw new NotSupportedException();
        public DbSet<ConversationCheckpoint> ConversationCheckpoints => throw new NotSupportedException();
        public DbSet<ConversationInsight> ConversationInsights => throw new NotSupportedException();

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

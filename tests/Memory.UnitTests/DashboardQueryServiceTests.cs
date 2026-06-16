using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.UnitTests;

public sealed class DashboardQueryServiceTests
{
    [Fact]
    public async Task MemoryGraph_Should_Read_Precomputed_Snapshot()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);
        var nodeA = CreateGraphNode(1, "ContextHub", "Graph seed", ["graph", "dashboard"], 0.95m);
        var nodeB = CreateGraphNode(2, "ContextHub", "Graph neighbor", ["graph"], 0.85m);
        var nodeC = CreateGraphNode(3, "OtherProject", "Other memory", ["other"], 0.75m);
        var snapshotStore = new FakeDashboardSnapshotStore();
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>(
            DashboardSnapshotKeys.MemoryGraphIndex,
            now.AddSeconds(-2),
            15,
            now.AddSeconds(13),
            string.Empty,
            new DashboardMemoryGraphIndexSnapshotPayload(
                new MemoryGraphResult(
                    [nodeA, nodeB, nodeC],
                    [
                        new MemoryGraphEdgeResult(nodeA.Id, nodeB.Id, "explicit", "references"),
                        new MemoryGraphEdgeResult(nodeA.Id, nodeC.Id, "explicit", "cross-project")
                    ],
                    new MemoryGraphStatsResult(0, 3, 2, false)))));

        var service = new DashboardQueryService(
            new UnusedApplicationDbContext(),
            new UnusedStorageExplorerStore(),
            snapshotStore,
            new UnusedMemoryService(),
            new FakeCacheVersionStore(),
            new FakeRedisObjectCache(),
            new FixedTimeProvider(now),
            new RequestActorAccessor());

        var graph = await service.GetMemoryGraphAsync(
            new MemoryGraphRequest(ProjectId: "ContextHub", GraphMode: MemoryGraphMode.ProjectFull),
            CancellationToken.None);

        graph.Nodes.Should().HaveCount(2);
        graph.Edges.Should().ContainSingle();
        graph.Nodes.Single(node => node.Id == nodeA.Id).ExplicitLinkCount.Should().Be(1);
        graph.Stats.Truncated.Should().BeFalse();
    }

    [Fact]
    public async Task MemoryGraph_Should_Return_Unavailable_When_Snapshot_Is_Missing()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);
        var service = new DashboardQueryService(
            new UnusedApplicationDbContext(),
            new UnusedStorageExplorerStore(),
            new FakeDashboardSnapshotStore(),
            new UnusedMemoryService(),
            new FakeCacheVersionStore(),
            new FakeRedisObjectCache(),
            new FixedTimeProvider(now),
            new RequestActorAccessor());

        var graph = await service.GetMemoryGraphAsync(new MemoryGraphRequest(ProjectId: "ContextHub"), CancellationToken.None);

        graph.Nodes.Should().BeEmpty();
        graph.Edges.Should().BeEmpty();
        graph.Stats.Truncated.Should().BeTrue();
        graph.Stats.TruncationReason.Should().Contain("Graph index snapshot unavailable");
    }

    [Fact]
    public async Task MemoryGraphIndexRefresh_Should_Write_Background_Snapshot()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 10, 0, TimeSpan.Zero);
        var nodeA = CreateGraphNode(1, "ContextHub", "Graph seed", ["graph"], 0.95m);
        var nodeB = CreateGraphNode(2, "ContextHub", "Graph neighbor", ["graph"], 0.85m);
        var expectedGraph = new MemoryGraphResult(
            [nodeA, nodeB],
            [new MemoryGraphEdgeResult(nodeA.Id, nodeB.Id, "explicit", "references")],
            new MemoryGraphStatsResult(0, 2, 1, false));
        var snapshotStore = new FakeDashboardSnapshotStore();
        var service = new DashboardMemoryGraphIndexRefreshService(
            new FakeDashboardMemoryGraphIndexBuilder(expectedGraph),
            snapshotStore,
            new FixedBehaviorSettingsAccessor(23),
            new FixedTimeProvider(now));

        var result = await service.RefreshAsync("manual", null, CancellationToken.None);
        var snapshot = await snapshotStore.GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(
            DashboardSnapshotKeys.MemoryGraphIndex,
            CancellationToken.None);

        result.Trigger.Should().Be("manual");
        result.RefreshIntervalSeconds.Should().Be(23);
        result.NodeCount.Should().Be(2);
        result.EdgeCount.Should().Be(1);
        snapshot.Should().NotBeNull();
        snapshot!.CapturedAtUtc.Should().Be(now);
        snapshot.RefreshIntervalSeconds.Should().Be(23);
        snapshot.Payload.Graph.Edges.Should().ContainSingle();
    }

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
            new UnusedMemoryService(),
            new FakeCacheVersionStore(),
            new FakeRedisObjectCache(),
            new FixedTimeProvider(now),
            new RequestActorAccessor());

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
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardEvaluationSummarySnapshotPayload>(
            DashboardSnapshotKeys.EvaluationSummary,
            now.AddSeconds(-3),
            5,
            now.AddSeconds(2),
            string.Empty,
            new DashboardEvaluationSummarySnapshotPayload(null)));
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardContextSavingsSnapshotPayload>(
            DashboardSnapshotKeys.ContextSavings,
            now.AddSeconds(-3),
            60,
            now.AddSeconds(57),
            string.Empty,
            new DashboardContextSavingsSnapshotPayload(new DashboardContextSavingsResult(
                false,
                0,
                0,
                0,
                0,
                0d,
                ContextSavingsEstimator.LowConfidence,
                0d,
                0d,
                now.AddHours(-24),
                now,
                []))));
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
            new UnusedMemoryService(),
            new FakeCacheVersionStore(),
            new FakeRedisObjectCache(),
            new FixedTimeProvider(now),
            new RequestActorAccessor());

        var overview = await service.GetOverviewAsync(CancellationToken.None);

        overview.SnapshotStatus.Should().NotBeNull();
        overview.SnapshotStatus!.IsStale.Should().BeFalse();
        overview.SnapshotStatus.Warning.Should().BeEmpty();
        overview.SnapshotStatus.Sections.Single(x => x.Key == DashboardSnapshotKeys.ResourceChart).IsStale.Should().BeTrue();
    }

    [Fact]
    public async Task Overview_Should_Respect_Long_Refresh_Interval_Before_Marking_Context_Savings_Stale()
    {
        var capturedAtUtc = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);
        var snapshotStore = CreateOverviewSnapshotStore(capturedAtUtc, capturedAtUtc.AddSeconds(89));
        var serviceBeforeGraceExpires = CreateDashboardQueryService(
            snapshotStore,
            capturedAtUtc.AddSeconds(89));

        var overviewBeforeGraceExpires = await serviceBeforeGraceExpires.GetOverviewAsync(CancellationToken.None);

        overviewBeforeGraceExpires.SnapshotStatus.Should().NotBeNull();
        overviewBeforeGraceExpires.SnapshotStatus!.Sections
            .Single(x => x.Key == DashboardSnapshotKeys.ContextSavings)
            .IsStale.Should().BeFalse();
        overviewBeforeGraceExpires.SnapshotStatus.IsStale.Should().BeFalse();

        var snapshotStoreAfterGraceExpires = CreateOverviewSnapshotStore(capturedAtUtc, capturedAtUtc.AddSeconds(91));
        var serviceAfterGraceExpires = CreateDashboardQueryService(
            snapshotStoreAfterGraceExpires,
            capturedAtUtc.AddSeconds(91));

        var overviewAfterGraceExpires = await serviceAfterGraceExpires.GetOverviewAsync(CancellationToken.None);

        var contextSavingsStatus = overviewAfterGraceExpires.SnapshotStatus!.Sections
            .Single(x => x.Key == DashboardSnapshotKeys.ContextSavings);
        contextSavingsStatus.IsStale.Should().BeTrue();
        contextSavingsStatus.Warning.Should().Contain("資料已延遲 31 秒");
        overviewAfterGraceExpires.SnapshotStatus.IsStale.Should().BeTrue();
    }

    [Fact]
    public async Task StorageTables_Should_Read_Redis_Snapshot()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);
        var snapshotStore = new FakeDashboardSnapshotStore();
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardStorageTableStatsSnapshotPayload>(
            DashboardSnapshotKeys.StorageTableStats,
            now,
            5,
            now.AddSeconds(15),
            string.Empty,
            new DashboardStorageTableStatsSnapshotPayload(
                [new StorageTableSummaryResult("retrieval_events", "Telemetry", 123, ["id"], true)])));
        var service = new DashboardQueryService(
            new UnusedApplicationDbContext(),
            new UnusedStorageExplorerStore(),
            snapshotStore,
            new UnusedMemoryService(),
            new FakeCacheVersionStore(),
            new FakeRedisObjectCache(),
            new FixedTimeProvider(now),
            new RequestActorAccessor());

        var tables = await service.GetStorageTablesAsync(CancellationToken.None);

        tables.Should().ContainSingle(x => x.Name == "retrieval_events" && x.IsLarge);
    }

    [Fact]
    public async Task LargeTablePreview_Should_Read_Redis_Snapshot()
    {
        var now = new DateTimeOffset(2026, 4, 15, 8, 0, 0, TimeSpan.Zero);
        var snapshotStore = new FakeDashboardSnapshotStore();
        var preview = new StorageTableRowsResult(
            "retrieval_events",
            "Telemetry",
            ["id", "query_text"],
            ["id"],
            null,
            null,
            new PagedResult<StorageRowResult>(
                [new StorageRowResult(new Dictionary<string, string?> { ["id"] = "row-1", ["query_text"] = "[omitted]" })],
                1,
                25,
                123),
            DashboardStoragePolicy.LargeTablePreviewWarning,
            "redis");
        snapshotStore.Add(new DashboardSnapshotEnvelope<DashboardStorageLargeTablePreviewSnapshotPayload>(
            DashboardSnapshotKeys.StorageLargeTablePreview,
            now,
            5,
            now.AddSeconds(15),
            string.Empty,
            new DashboardStorageLargeTablePreviewSnapshotPayload([preview])));
        var service = new DashboardQueryService(
            new UnusedApplicationDbContext(),
            new UnusedStorageExplorerStore(),
            snapshotStore,
            new UnusedMemoryService(),
            new FakeCacheVersionStore(),
            new FakeRedisObjectCache(),
            new FixedTimeProvider(now),
            new RequestActorAccessor());

        var rows = await service.GetStorageRowsAsync(
            new StorageRowsRequest("retrieval_events", Page: 1, PageSize: 25),
            CancellationToken.None);

        rows.DataSource.Should().Be("redis");
        rows.Warning.Should().Contain("Large table preview");
        rows.Rows.Items.Should().ContainSingle();
    }

    [Fact]
    public void LargeTablePolicy_Should_Block_Unfiltered_Deep_Paging()
    {
        var request = new StorageRowsRequest("retrieval_hits", Page: 2, PageSize: 25);

        DashboardStoragePolicy.IsBlockedUnfilteredLargeTablePage(request).Should().BeTrue();
        DashboardStoragePolicy.IsLargeTablePreviewRequest(request).Should().BeFalse();
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static DashboardQueryService CreateDashboardQueryService(
        IDashboardSnapshotStore snapshotStore,
        DateTimeOffset now)
        => new(
            new UnusedApplicationDbContext(),
            new UnusedStorageExplorerStore(),
            snapshotStore,
            new UnusedMemoryService(),
            new FakeCacheVersionStore(),
            new FakeRedisObjectCache(),
            new FixedTimeProvider(now),
            new RequestActorAccessor());

    private static FakeDashboardSnapshotStore CreateOverviewSnapshotStore(
        DateTimeOffset contextSavingsCapturedAtUtc,
        DateTimeOffset otherCapturedAtUtc)
    {
        var snapshotStore = new FakeDashboardSnapshotStore();
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.StatusCore,
            5,
            new DashboardStatusCoreSnapshotPayload(
                "mcp-server",
                "ContextHub",
                "1.2.3",
                otherCapturedAtUtc.AddMinutes(-2),
                "Http",
                "CPUExecutionProvider",
                "compact",
                "intfloat/multilingual-e5-small",
                384,
                512,
                6,
                8,
                true,
                42),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.DependenciesHealth,
            5,
            new DashboardDependenciesHealthSnapshotPayload(
                [new DashboardServiceHealthResult("postgres", "Healthy", "ok")]),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.RecentOperations,
            5,
            new DashboardRecentOperationsSnapshotPayload(
                [new DashboardOverviewMetricResult("jobs", "Jobs", 2, "count")],
                [],
                []),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.DashboardJobs,
            5,
            new DashboardJobsSnapshotPayload(new PagedResult<JobListItemResult>([], 1, 10, 0)),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.DashboardLogs,
            5,
            new DashboardLogsSnapshotPayload([]),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.DashboardProjectSuggestions,
            5,
            new DashboardProjectSuggestionsSnapshotPayload([]),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.StorageTableStats,
            5,
            new DashboardStorageTableStatsSnapshotPayload([]),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.StorageLargeTablePreview,
            5,
            new DashboardStorageLargeTablePreviewSnapshotPayload([]),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.EvaluationSummary,
            5,
            new DashboardEvaluationSummarySnapshotPayload(null),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.ContextSavings,
            60,
            new DashboardContextSavingsSnapshotPayload(new DashboardContextSavingsResult(
                false,
                0,
                0,
                0,
                0,
                0d,
                ContextSavingsEstimator.LowConfidence,
                0d,
                0d,
                contextSavingsCapturedAtUtc.AddHours(-24),
                contextSavingsCapturedAtUtc,
                [])),
            contextSavingsCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.ResourceChart,
            5,
            new DashboardResourceChartSnapshotPayload([]),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.DependencyResources,
            5,
            new DashboardDependencyResourcesResult("Healthy", string.Empty, [], []),
            otherCapturedAtUtc);
        AddSnapshot(
            snapshotStore,
            DashboardSnapshotKeys.DockerHost,
            5,
            new DashboardDockerHostResult(
                "Healthy",
                string.Empty,
                new DockerHostSummaryResult("docker-dev", "28.0", "Linux", "6.8", 8, 1024, 768, 5, 12, 3, otherCapturedAtUtc)),
            otherCapturedAtUtc);

        return snapshotStore;
    }

    private static void AddSnapshot<TPayload>(
        FakeDashboardSnapshotStore snapshotStore,
        string key,
        int refreshIntervalSeconds,
        TPayload payload,
        DateTimeOffset capturedAtUtc)
    {
        snapshotStore.Add(new DashboardSnapshotEnvelope<TPayload>(
            key,
            capturedAtUtc,
            refreshIntervalSeconds,
            DashboardSnapshotStalenessPolicy.ComputeStaleAfter(capturedAtUtc, refreshIntervalSeconds),
            string.Empty,
            payload));
    }

    private static MemoryGraphNodeResult CreateGraphNode(
        int index,
        string projectId,
        string title,
        IReadOnlyList<string> tags,
        decimal importance)
        => new(
            Guid.Parse($"20000000-0000-0000-0000-{index:000000000000}"),
            title,
            $"{title} summary",
            projectId,
            MemoryType.Artifact,
            MemoryScope.Project,
            MemoryStatus.Active,
            tags,
            "document",
            $"test://graph/{index}",
            new DateTimeOffset(2026, 4, 15, 7, index, 0, TimeSpan.Zero),
            importance,
            0.9m,
            false,
            null,
            null,
            "document",
            0,
            0);

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

    private sealed class FakeDashboardMemoryGraphIndexBuilder(MemoryGraphResult graph) : IDashboardMemoryGraphIndexBuilder
    {
        public Task<DashboardMemoryGraphIndexSnapshotPayload> BuildAsync(CancellationToken cancellationToken)
            => Task.FromResult(new DashboardMemoryGraphIndexSnapshotPayload(graph));
    }

    private sealed class FakeCacheVersionStore : ICacheVersionStore
    {
        public Task<long> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult(1L);

        public Task<CacheVersionStamp> GetVersionStampAsync(
            IReadOnlyList<string> projectIds,
            ContextHubRequestActor actor,
            bool includeShared,
            CancellationToken cancellationToken)
            => Task.FromResult(new CacheVersionStamp("test", 1, 1, includeShared ? 1 : 0, 1, new Dictionary<string, long>()));

        public Task<long> IncrementAsync(CancellationToken cancellationToken) => Task.FromResult(2L);
        public Task<long> IncrementProjectAsync(string projectId, CancellationToken cancellationToken) => Task.FromResult(2L);
        public Task<long> IncrementUserAsync(ContextHubRequestActor actor, CancellationToken cancellationToken) => Task.FromResult(2L);
        public Task<long> IncrementSharedAsync(CancellationToken cancellationToken) => Task.FromResult(2L);
        public Task<long> IncrementSecurityAsync(CancellationToken cancellationToken) => Task.FromResult(2L);
        public Task<long> GetJobVersionAsync(CancellationToken cancellationToken) => Task.FromResult(1L);
        public Task<long> IncrementJobsAsync(CancellationToken cancellationToken) => Task.FromResult(2L);
        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) => Task.FromResult<T?>(default);
        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task PublishJobSignalAsync(Guid jobId, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<bool> WaitForJobSignalAsync(TimeSpan timeout, CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class FakeRedisObjectCache : IRedisObjectCache
    {
        public Task<RedisCacheLookup<T>> GetAsync<T>(string key, string kind, CancellationToken cancellationToken)
            => Task.FromResult(new RedisCacheLookup<T>(false, default));

        public Task SetAsync<T>(string key, string kind, T value, TimeSpan ttl, CancellationToken cancellationToken)
            => Task.CompletedTask;
    }

    private sealed class FixedBehaviorSettingsAccessor(int memoryGraphIndexSeconds) : IInstanceBehaviorSettingsAccessor
    {
        public Task<InstanceBehaviorSettingsResult> GetCurrentAsync(CancellationToken cancellationToken)
            => Task.FromResult(new InstanceBehaviorSettingsResult(
                true,
                true,
                true,
                30,
                "auto",
                1200,
                "ContextHub",
                MemoryQueryMode.CurrentOnly,
                false,
                true,
                DashboardSnapshotPollingDefaults.Create() with { MemoryGraphIndexSeconds = memoryGraphIndexSeconds },
                5,
                5,
                5,
                5,
                5));
    }

    private sealed class UnusedStorageExplorerStore : IStorageExplorerStore
    {
        public Task<IReadOnlyList<StorageTableSummaryResult>> ListTablesAsync(CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<StorageTableRowsResult> GetRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class UnusedMemoryService : IMemoryService
    {
        public Task<MemoryDocument> UpsertAsync(MemoryUpsertRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MemoryDocument> UpdateAsync(MemoryUpdateRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MemoryDocument?> GetAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<MemorySearchHit>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<WorkingContextResult> BuildWorkingContextAsync(WorkingContextRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EnqueueReindexResult> EnqueueReindexAsync(EnqueueReindexRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<EnqueueSummaryRefreshResult> EnqueueSummaryRefreshAsync(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<JobResult?> GetJobAsync(Guid id, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<MemoryDocument> PromoteLogSliceAsync(PromoteLogSliceRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<UserPreferenceResult> UpsertUserPreferenceAsync(UserPreferenceUpsertRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<IReadOnlyList<UserPreferenceResult>> ListUserPreferencesAsync(UserPreferenceListRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();

        public Task<UserPreferenceResult> ArchiveUserPreferenceAsync(UserPreferenceArchiveRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }

    private sealed class UnusedApplicationDbContext : IApplicationDbContext
    {
        public DbSet<InstanceSetting> InstanceSettings => throw new NotSupportedException();
        public DbSet<Tenant> Tenants => throw new NotSupportedException();
        public DbSet<TenantUser> TenantUsers => throw new NotSupportedException();
        public DbSet<TenantProjectGrant> TenantProjectGrants => throw new NotSupportedException();
        public DbSet<ApiToken> ApiTokens => throw new NotSupportedException();
        public DbSet<SecurityAuditEvent> SecurityAuditEvents => throw new NotSupportedException();
        public DbSet<MemoryItem> MemoryItems => throw new NotSupportedException();
        public DbSet<MemoryItemRevision> MemoryItemRevisions => throw new NotSupportedException();
        public DbSet<MemoryItemChunk> MemoryItemChunks => throw new NotSupportedException();
        public DbSet<MemoryChunkVector> MemoryChunkVectors => throw new NotSupportedException();
        public DbSet<MemoryLink> MemoryLinks => throw new NotSupportedException();
        public DbSet<MemoryJob> MemoryJobs => throw new NotSupportedException();
        public DbSet<MaintenanceRun> MaintenanceRuns => throw new NotSupportedException();
        public DbSet<RetrievalEvent> RetrievalEvents => throw new NotSupportedException();
        public DbSet<RetrievalHit> RetrievalHits => throw new NotSupportedException();
        public DbSet<RetrievalTelemetryDailySummary> RetrievalTelemetryDailySummaries => throw new NotSupportedException();
        public DbSet<RetrievalTelemetryDailyHitSummary> RetrievalTelemetryDailyHitSummaries => throw new NotSupportedException();
        public DbSet<RuntimeLogEntry> RuntimeLogEntries => throw new NotSupportedException();
        public DbSet<LogIngestionCheckpoint> LogIngestionCheckpoints => throw new NotSupportedException();
        public DbSet<SourceConnection> SourceConnections => throw new NotSupportedException();
        public DbSet<SourceSyncRun> SourceSyncRuns => throw new NotSupportedException();
        public DbSet<GovernanceFinding> GovernanceFindings => throw new NotSupportedException();
        public DbSet<EvaluationSuite> EvaluationSuites => throw new NotSupportedException();
        public DbSet<EvaluationCase> EvaluationCases => throw new NotSupportedException();
        public DbSet<EvaluationRun> EvaluationRuns => throw new NotSupportedException();
        public DbSet<EvaluationRunItem> EvaluationRunItems => throw new NotSupportedException();
        public DbSet<SuggestedAction> SuggestedActions => throw new NotSupportedException();
        public DbSet<ConversationSession> ConversationSessions => throw new NotSupportedException();
        public DbSet<ConversationCheckpoint> ConversationCheckpoints => throw new NotSupportedException();
        public DbSet<ConversationInsight> ConversationInsights => throw new NotSupportedException();

        public void ClearTrackedChanges()
        {
        }

        public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }
}

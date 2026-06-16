using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Memory.Application;

public sealed class DashboardQueryService(
    IApplicationDbContext dbContext,
    IStorageExplorerStore storageExplorerStore,
    IDashboardSnapshotStore snapshotStore,
    IMemoryService memoryService,
    ICacheVersionStore cacheStore,
    IRedisObjectCache objectCache,
    TimeProvider timeProvider,
    IRequestActorAccessor actorAccessor) : IDashboardQueryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<DashboardOverviewResult> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var statusCore = await snapshotStore.GetAsync<DashboardStatusCoreSnapshotPayload>(DashboardSnapshotKeys.StatusCore, cancellationToken);
        var dependencies = await snapshotStore.GetAsync<DashboardDependenciesHealthSnapshotPayload>(DashboardSnapshotKeys.DependenciesHealth, cancellationToken);
        var recentOperations = await snapshotStore.GetAsync<DashboardRecentOperationsSnapshotPayload>(DashboardSnapshotKeys.RecentOperations, cancellationToken);
        var resourceChart = await snapshotStore.GetAsync<DashboardResourceChartSnapshotPayload>(DashboardSnapshotKeys.ResourceChart, cancellationToken);
        var dependencyResources = await snapshotStore.GetAsync<DashboardDependencyResourcesResult>(DashboardSnapshotKeys.DependencyResources, cancellationToken);
        var dockerHost = await snapshotStore.GetAsync<DashboardDockerHostResult>(DashboardSnapshotKeys.DockerHost, cancellationToken);
        var dashboardJobs = await snapshotStore.GetAsync<DashboardJobsSnapshotPayload>(DashboardSnapshotKeys.DashboardJobs, cancellationToken);
        var dashboardLogs = await snapshotStore.GetAsync<DashboardLogsSnapshotPayload>(DashboardSnapshotKeys.DashboardLogs, cancellationToken);
        var projectSuggestions = await snapshotStore.GetAsync<DashboardProjectSuggestionsSnapshotPayload>(DashboardSnapshotKeys.DashboardProjectSuggestions, cancellationToken);
        var storageTableStats = await snapshotStore.GetAsync<DashboardStorageTableStatsSnapshotPayload>(DashboardSnapshotKeys.StorageTableStats, cancellationToken);
        var storageLargeTablePreview = await snapshotStore.GetAsync<DashboardStorageLargeTablePreviewSnapshotPayload>(DashboardSnapshotKeys.StorageLargeTablePreview, cancellationToken);
        var evaluationSummary = await snapshotStore.GetAsync<DashboardEvaluationSummarySnapshotPayload>(DashboardSnapshotKeys.EvaluationSummary, cancellationToken);
        var contextSavings = await snapshotStore.GetAsync<DashboardContextSavingsSnapshotPayload>(DashboardSnapshotKeys.ContextSavings, cancellationToken);

        var sectionStatuses = new[]
        {
            BuildSectionStatus(DashboardSnapshotKeys.StatusCore, "核心狀態", statusCore, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependenciesHealth, "依賴健康", dependencies, now),
            BuildSectionStatus(DashboardSnapshotKeys.RecentOperations, "近期維運摘要", recentOperations, now),
            BuildSectionStatus(DashboardSnapshotKeys.DashboardJobs, "背景工作快照", dashboardJobs, now),
            BuildSectionStatus(DashboardSnapshotKeys.DashboardLogs, "近期日誌快照", dashboardLogs, now),
            BuildSectionStatus(DashboardSnapshotKeys.DashboardProjectSuggestions, "Project 建議快照", projectSuggestions, now),
            BuildSectionStatus(DashboardSnapshotKeys.StorageTableStats, "Storage 表統計", storageTableStats, now),
            BuildSectionStatus(DashboardSnapshotKeys.StorageLargeTablePreview, "Storage 大表預覽", storageLargeTablePreview, now),
            BuildSectionStatus(DashboardSnapshotKeys.EvaluationSummary, "評測摘要", evaluationSummary, now),
            BuildSectionStatus(DashboardSnapshotKeys.ContextSavings, "Context 節省估算", contextSavings, now),
            BuildSectionStatus(DashboardSnapshotKeys.ResourceChart, "圖表與即時資料", resourceChart, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependencyResources, "Compose 服務資源", dependencyResources, now),
            BuildSectionStatus(DashboardSnapshotKeys.DockerHost, "Docker 主機", dockerHost, now)
        };

        var snapshotStatus = BuildPageSnapshotStatus(sectionStatuses, now);
        var core = statusCore?.Payload;
        var operations = recentOperations?.Payload;

        return new DashboardOverviewResult(
            core?.Namespace ?? ProjectContext.DefaultProjectId,
            core?.BuildVersion ?? BuildMetadata.Current.Version,
            core?.BuildTimestampUtc ?? BuildMetadata.Current.TimestampUtc,
            core?.EmbeddingProfile ?? "unavailable",
            core?.ModelKey ?? "unavailable",
            core?.Dimensions ?? 0,
            core?.MaxTokens ?? 0,
            core?.CacheVersion ?? 0L,
            dependencies?.Payload.Services ?? [],
            operations?.Metrics ?? [],
            (resourceChart?.Payload.Samples ?? []).Select(x => new RequestTrafficSampleResult(x.TimestampUtc, x.InboundRequests, x.OutboundRequests)).ToArray(),
            operations?.ActiveJobs ?? [],
            operations?.RecentErrors ?? [],
            snapshotStatus.SnapshotAtUtc,
            snapshotStatus,
            dockerHost?.Payload ?? CreateUnavailableDockerHost(now),
            dependencyResources?.Payload ?? CreateUnavailableDependencyResources(),
            resourceChart?.Payload.Samples ?? [],
            evaluationSummary?.Payload.Summary,
            contextSavings?.Payload.Savings ?? CreateEmptyContextSavings(now));
    }

    public async Task<DashboardRuntimeResult> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var runtime = await snapshotStore.GetAsync<DashboardEmbeddingRuntimeSnapshotPayload>(DashboardSnapshotKeys.EmbeddingRuntime, cancellationToken);
        var dependencies = await snapshotStore.GetAsync<DashboardDependenciesHealthSnapshotPayload>(DashboardSnapshotKeys.DependenciesHealth, cancellationToken);
        var dockerHost = await snapshotStore.GetAsync<DashboardDockerHostResult>(DashboardSnapshotKeys.DockerHost, cancellationToken);
        var dependencyResources = await snapshotStore.GetAsync<DashboardDependencyResourcesResult>(DashboardSnapshotKeys.DependencyResources, cancellationToken);

        var sectionStatuses = new[]
        {
            BuildSectionStatus(DashboardSnapshotKeys.EmbeddingRuntime, "向量執行環境", runtime, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependenciesHealth, "依賴健康", dependencies, now),
            BuildSectionStatus(DashboardSnapshotKeys.DockerHost, "Docker 主機", dockerHost, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependencyResources, "依賴資源概況", dependencyResources, now)
        };
        var snapshotStatus = BuildPageSnapshotStatus(sectionStatuses, now);
        var payload = runtime?.Payload;

        var parameters = new[]
        {
            new DashboardRuntimeParameterResult("General", "Memory Namespace", payload?.Namespace ?? ProjectContext.DefaultProjectId, false),
            new DashboardRuntimeParameterResult("Embeddings", "Provider", payload?.EmbeddingProvider ?? "unavailable", false),
            new DashboardRuntimeParameterResult("Embeddings", "Execution Provider", payload?.ExecutionProvider ?? "unavailable", false),
            new DashboardRuntimeParameterResult("Embeddings", "Profile", payload?.EmbeddingProfile ?? "unavailable", false),
            new DashboardRuntimeParameterResult("Embeddings", "Model Key", payload?.ModelKey ?? "unavailable", false),
            new DashboardRuntimeParameterResult("Embeddings", "Dimensions", (payload?.Dimensions ?? 0).ToString(), false),
            new DashboardRuntimeParameterResult("Embeddings", "Max Tokens", (payload?.MaxTokens ?? 0).ToString(), false),
            new DashboardRuntimeParameterResult("Embeddings", "Inference Threads", (payload?.InferenceThreads ?? 0).ToString(), false),
            new DashboardRuntimeParameterResult("Embeddings", "Batch Size", (payload?.BatchSize ?? 0).ToString(), false),
            new DashboardRuntimeParameterResult("Embeddings", "Batching Enabled", payload?.BatchingEnabled == true ? "true" : "false", false)
        };

        return new DashboardRuntimeResult(
            payload?.Namespace ?? ProjectContext.DefaultProjectId,
            payload?.BuildVersion ?? BuildMetadata.Current.Version,
            payload?.BuildTimestampUtc ?? BuildMetadata.Current.TimestampUtc,
            payload?.EmbeddingProvider ?? "unavailable",
            payload?.ExecutionProvider ?? "unavailable",
            payload?.EmbeddingProfile ?? "unavailable",
            payload?.ModelKey ?? "unavailable",
            payload?.Dimensions ?? 0,
            payload?.MaxTokens ?? 0,
            payload?.InferenceThreads ?? 0,
            payload?.BatchSize ?? 0,
            payload?.BatchingEnabled ?? false,
            dependencies?.Payload.Services ?? [],
            parameters,
            snapshotStatus.SnapshotAtUtc,
            snapshotStatus,
            dockerHost?.Payload ?? CreateUnavailableDockerHost(now),
            dependencyResources?.Payload ?? CreateUnavailableDependencyResources());
    }

    public async Task<DashboardMonitoringResult> GetMonitoringAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var statusCore = await snapshotStore.GetAsync<DashboardStatusCoreSnapshotPayload>(DashboardSnapshotKeys.StatusCore, cancellationToken);
        var dependencies = await snapshotStore.GetAsync<DashboardDependenciesHealthSnapshotPayload>(DashboardSnapshotKeys.DependenciesHealth, cancellationToken);
        var dockerHost = await snapshotStore.GetAsync<DashboardDockerHostResult>(DashboardSnapshotKeys.DockerHost, cancellationToken);
        var dependencyResources = await snapshotStore.GetAsync<DashboardDependencyResourcesResult>(DashboardSnapshotKeys.DependencyResources, cancellationToken);
        var resourceChart = await snapshotStore.GetAsync<DashboardResourceChartSnapshotPayload>(DashboardSnapshotKeys.ResourceChart, cancellationToken);
        var monitoring = await snapshotStore.GetAsync<DashboardMonitoringSnapshotPayload>(DashboardSnapshotKeys.MonitoringStats, cancellationToken);
        var storageTableStats = await snapshotStore.GetAsync<DashboardStorageTableStatsSnapshotPayload>(DashboardSnapshotKeys.StorageTableStats, cancellationToken);
        var storageLargeTablePreview = await snapshotStore.GetAsync<DashboardStorageLargeTablePreviewSnapshotPayload>(DashboardSnapshotKeys.StorageLargeTablePreview, cancellationToken);

        var sectionStatuses = new[]
        {
            BuildSectionStatus(DashboardSnapshotKeys.StatusCore, "核心狀態", statusCore, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependenciesHealth, "依賴健康", dependencies, now),
            BuildSectionStatus(DashboardSnapshotKeys.DockerHost, "Docker 主機", dockerHost, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependencyResources, "Compose 服務資源", dependencyResources, now),
            BuildSectionStatus(DashboardSnapshotKeys.ResourceChart, "資源趨勢", resourceChart, now),
            BuildSectionStatus(DashboardSnapshotKeys.MonitoringStats, "Redis / PostgreSQL 統計", monitoring, now),
            BuildSectionStatus(DashboardSnapshotKeys.StorageTableStats, "Storage 表統計", storageTableStats, now),
            BuildSectionStatus(DashboardSnapshotKeys.StorageLargeTablePreview, "Storage 大表預覽", storageLargeTablePreview, now)
        };
        var snapshotStatus = BuildPageSnapshotStatus(sectionStatuses, now);
        var core = statusCore?.Payload;
        var monitoringPayload = monitoring?.Payload;

        return new DashboardMonitoringResult(
            core?.Namespace ?? ProjectContext.DefaultProjectId,
            core?.BuildVersion ?? BuildMetadata.Current.Version,
            core?.BuildTimestampUtc ?? BuildMetadata.Current.TimestampUtc,
            dependencies?.Payload.Services ?? [],
            snapshotStatus.SnapshotAtUtc,
            monitoringPayload?.Redis ?? CreateUnavailableRedisTelemetry(),
            monitoringPayload?.Postgres ?? CreateUnavailablePostgresTelemetry(),
            snapshotStatus,
            dockerHost?.Payload ?? CreateUnavailableDockerHost(now),
            dependencyResources?.Payload ?? CreateUnavailableDependencyResources(),
            resourceChart?.Payload.Samples ?? []);
    }

    private static DashboardSnapshotSectionStatusResult BuildSectionStatus<TPayload>(
        string key,
        string label,
        DashboardSnapshotEnvelope<TPayload>? envelope,
        DateTimeOffset now)
    {
        if (envelope is null)
        {
            return new DashboardSnapshotSectionStatusResult(
                key,
                label,
                now,
                0,
                true,
                "Snapshot unavailable.",
                "尚未收到背景快照。");
        }

        var expectedRefreshAtUtc = envelope.CapturedAtUtc.AddSeconds(Math.Max(1, envelope.RefreshIntervalSeconds));
        var isStale = envelope.StaleAfterUtc < now;
        var warning = isStale
            ? $"資料已延遲 {Math.Max(1, (int)Math.Round((now - expectedRefreshAtUtc).TotalSeconds))} 秒。"
            : string.Empty;

        return new DashboardSnapshotSectionStatusResult(
            key,
            label,
            envelope.CapturedAtUtc,
            envelope.RefreshIntervalSeconds,
            isStale,
            envelope.LastError,
            warning);
    }

    private static DashboardContextSavingsResult CreateEmptyContextSavings(DateTimeOffset now)
        => new(
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
            []);

    private static DashboardPageSnapshotStatusResult BuildPageSnapshotStatus(
        IReadOnlyList<DashboardSnapshotSectionStatusResult> sections,
        DateTimeOffset now)
    {
        var pageCriticalSections = sections.Where(IsPageCriticalSection).ToArray();
        var sectionsForPageState = pageCriticalSections.Length == 0 ? sections.ToArray() : pageCriticalSections;
        var validSections = sectionsForPageState.Where(x => x.CapturedAtUtc > DateTimeOffset.MinValue).ToArray();
        var snapshotAt = validSections.Length == 0 ? now : validSections.Min(x => x.CapturedAtUtc);
        var isStale = sectionsForPageState.Any(x => x.IsStale);
        var warning = sectionsForPageState.FirstOrDefault(x => x.IsStale)?.Warning
            ?? sectionsForPageState.FirstOrDefault(x => !string.IsNullOrWhiteSpace(x.LastError))?.LastError
            ?? string.Empty;

        return new DashboardPageSnapshotStatusResult(snapshotAt, isStale, warning, sections);
    }

    private static bool IsPageCriticalSection(DashboardSnapshotSectionStatusResult section)
        => section.Key is not DashboardSnapshotKeys.ResourceChart and
            not DashboardSnapshotKeys.DashboardJobs and
            not DashboardSnapshotKeys.DashboardLogs and
            not DashboardSnapshotKeys.DashboardProjectSuggestions and
            not DashboardSnapshotKeys.StorageTableStats and
            not DashboardSnapshotKeys.StorageLargeTablePreview;

    private static DashboardDockerHostResult CreateUnavailableDockerHost(DateTimeOffset capturedAtUtc)
        => new(
            "Unavailable",
            "Docker host snapshot unavailable.",
            new DockerHostSummaryResult(
                "unavailable",
                "unavailable",
                "unavailable",
                "unavailable",
                0,
                0,
                0,
                0,
                0,
                0,
                capturedAtUtc));

    private static DashboardDependencyResourcesResult CreateUnavailableDependencyResources()
        => new("Unavailable", "Dependency resource snapshot unavailable.", [], []);

    private static DashboardRedisTelemetryResult CreateUnavailableRedisTelemetry()
        => new(
            "Unavailable",
            "Redis monitoring snapshot unavailable.",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "未配置");

    private static DashboardPostgresTelemetryResult CreateUnavailablePostgresTelemetry()
        => new(
            "Unavailable",
            "PostgreSQL monitoring snapshot unavailable.",
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "未配置",
            0);

    public async Task<PagedResult<MemoryListItemResult>> GetMemoriesAsync(MemoryListRequest request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request.Page, request.PageSize, 100);
        var normalizedRequest = request with { Page = normalized.Page, PageSize = normalized.PageSize };
        var actor = actorAccessor.Current;
        var version = await cacheStore.GetVersionStampAsync(
            ResolveDashboardSearchProjects(request.ProjectId, request.IncludedProjectIds, request.QueryMode, request.UseSummaryLayer) ?? [],
            actor,
            request.UseSummaryLayer,
            cancellationToken);
        var cacheKey = RedisCacheKeyBuilder.DashboardMemories(version, normalizedRequest, actor);
        var cached = await objectCache.GetAsync<PagedResult<MemoryListItemResult>>(
            cacheKey,
            "dashboard-memories",
            cancellationToken);
        if (cached.Hit && cached.Value is not null)
        {
            return cached.Value;
        }

        var query = BuildMemoryScopeQuery(
            request.ProjectId,
            request.IncludedProjectIds,
            request.QueryMode,
            request.UseSummaryLayer,
            request.ProjectQuery,
            request.Query,
            request.Scope,
            request.MemoryType,
            request.Status,
            request.SourceType,
            request.Tag);

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Skip((normalized.Page - 1) * normalized.PageSize)
            .Take(normalized.PageSize)
            .Select(x => new MemoryListItemResult(
                x.Id,
                x.ProjectId,
                x.ExternalKey,
                x.Scope,
                x.MemoryType,
                x.Title,
                x.Summary,
                x.SourceType,
                x.SourceRef,
                x.Tags,
                x.Importance,
                x.Confidence,
                x.Version,
                x.Status,
                x.UpdatedAt,
                x.IsReadOnly))
            .ToListAsync(cancellationToken);

        var result = new PagedResult<MemoryListItemResult>(items, normalized.Page, normalized.PageSize, totalCount);
        await objectCache.SetAsync(cacheKey, "dashboard-memories", result, TimeSpan.FromSeconds(60), cancellationToken);
        return result;
    }

    public async Task<MemoryGraphResult> GetMemoryGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken)
    {
        var normalizedMaxNodes = NormalizeGraphMaxNodes(request.MaxNodes);
        var snapshot = await snapshotStore.GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(
            DashboardSnapshotKeys.MemoryGraphIndex,
            cancellationToken);

        if (snapshot is null)
        {
            return new MemoryGraphResult(
                [],
                [],
                new MemoryGraphStatsResult(
                    0,
                    0,
                    0,
                    true,
                    "Graph index snapshot unavailable. Wait for the background collector to finish the first refresh."));
        }

        var graph = BuildGraphFromSnapshot(request, normalizedMaxNodes, snapshot.Payload.Graph);
        return await ApplyActorGraphFilterAsync(graph, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectSuggestionResult>> GetProjectSuggestionsAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        var normalizedLimit = limit < 1 ? 8 : Math.Min(limit, 20);
        var snapshot = await snapshotStore.GetAsync<DashboardProjectSuggestionsSnapshotPayload>(
            DashboardSnapshotKeys.DashboardProjectSuggestions,
            cancellationToken);
        if (snapshot is not null)
        {
            var snapshotProjects = FilterProjectSuggestions(snapshot.Payload.Projects, query, normalizedLimit);
            if (snapshotProjects.Count >= normalizedLimit || string.IsNullOrWhiteSpace(query))
            {
                return snapshotProjects;
            }
        }

        var projects = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => !actorAccessor.Current.HasUser || (x.TenantId == actorAccessor.Current.TenantId && x.OwnerUserId == actorAccessor.Current.UserId))
            .Where(x => x.ProjectId != ProjectContext.SharedProjectId && x.ProjectId != ProjectContext.UserProjectId)
            .GroupBy(x => x.ProjectId)
            .Select(group => new ProjectSuggestionResult(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        return FilterProjectSuggestions(projects, query, normalizedLimit);
    }

    public async Task<MemoryDetailsResult?> GetMemoryDetailsAsync(Guid id, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        var version = await cacheStore.GetVersionStampAsync([], actor, includeShared: false, cancellationToken);
        var cacheKey = RedisCacheKeyBuilder.DashboardMemoryDetails(version, id, actor);
        var cached = await objectCache.GetAsync<MemoryDetailsResult>(
            cacheKey,
            "dashboard-memory-details",
            cancellationToken);
        if (cached.Hit)
        {
            return cached.Value;
        }

        var entity = await dbContext.MemoryItems
            .AsNoTracking()
            .Include(x => x.Revisions)
            .Include(x => x.Chunks)
                .ThenInclude(x => x.Vectors)
            .Where(x => !actor.HasUser || (x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId))
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken);

        if (entity is null)
        {
            return null;
        }

        var document = new MemoryDocument(
            entity.Id,
            entity.ExternalKey,
            entity.Scope,
            entity.MemoryType,
            entity.Title,
            entity.Content,
            entity.Summary,
            entity.SourceType,
            entity.SourceRef,
            entity.Tags,
            entity.Importance,
            entity.Confidence,
            entity.Version,
            entity.Status,
            entity.MetadataJson,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.ProjectId,
            entity.IsReadOnly);

        var revisions = entity.Revisions
            .OrderByDescending(x => x.Version)
            .ThenByDescending(x => x.CreatedAt)
            .Select(x => new MemoryRevisionResult(
                x.Id,
                x.Version,
                x.Title,
                x.Summary,
                x.ChangedBy,
                x.CreatedAt))
            .ToArray();

        var chunks = entity.Chunks
            .OrderBy(x => x.ChunkIndex)
            .Select(x => new MemoryChunkResult(
                x.Id,
                x.ChunkKind,
                x.ChunkIndex,
                x.ChunkText,
                x.MetadataJson,
                x.CreatedAt,
                x.Vectors
                    .OrderByDescending(v => v.CreatedAt)
                    .Select(v => new MemoryVectorResult(
                        v.Id,
                        v.ModelKey,
                        v.Dimension,
                        v.Status,
                        v.CreatedAt))
                    .ToArray()))
            .ToArray();
        var links = await dbContext.MemoryLinks
            .AsNoTracking()
            .Where(x => x.FromId == id || x.ToId == id)
            .OrderByDescending(x => x.CreatedAt)
            .Select(x => new MemoryLinkResult(
                x.Id,
                x.FromId,
                x.ToId,
                x.LinkType,
                x.CreatedAt))
            .ToListAsync(cancellationToken);
        var findings = await dbContext.GovernanceFindings
            .AsNoTracking()
            .Where(x => x.PrimaryMemoryId == id || x.SecondaryMemoryId == id)
            .OrderByDescending(x => x.UpdatedAt)
            .Select(x => new MemoryGovernanceFindingSummaryResult(
                x.Id,
                x.Type,
                x.Status,
                x.Title,
                x.Summary,
                x.UpdatedAt))
            .ToListAsync(cancellationToken);
        var sourceContext = BuildSourceContext(entity);

        var result = new MemoryDetailsResult(document, revisions, chunks, links, findings, sourceContext);
        await objectCache.SetAsync(cacheKey, "dashboard-memory-details", result, TimeSpan.FromSeconds(60), cancellationToken);
        return result;
    }

    public async Task<PagedResult<JobListItemResult>> GetJobsAsync(JobListRequest request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request.Page, request.PageSize, 100);
        var normalizedRequest = request with { Page = normalized.Page, PageSize = normalized.PageSize };
        if (!request.Status.HasValue && !request.JobType.HasValue && normalized.Page == 1)
        {
            var snapshot = await snapshotStore.GetAsync<DashboardJobsSnapshotPayload>(
                DashboardSnapshotKeys.DashboardJobs,
                cancellationToken);
            if (snapshot is not null && snapshot.Payload.RecentJobs.TotalCount > 0)
            {
                return new PagedResult<JobListItemResult>(
                    snapshot.Payload.RecentJobs.Items.Take(normalized.PageSize).ToArray(),
                    normalized.Page,
                    normalized.PageSize,
                    snapshot.Payload.RecentJobs.TotalCount);
            }
        }

        var jobVersion = await cacheStore.GetJobVersionAsync(cancellationToken);
        var cacheKey = RedisCacheKeyBuilder.DashboardJobs(jobVersion, normalizedRequest);
        var cached = await objectCache.GetAsync<PagedResult<JobListItemResult>>(
            cacheKey,
            "dashboard-jobs",
            cancellationToken);
        if (cached.Hit && cached.Value is not null)
        {
            return cached.Value;
        }

        var query = dbContext.MemoryJobs.AsNoTracking().AsQueryable();

        if (request.Status.HasValue)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        if (request.JobType.HasValue)
        {
            query = query.Where(x => x.JobType == request.JobType.Value);
        }

        var totalCount = await query.CountAsync(cancellationToken);
        var items = await query
            .OrderByDescending(x => x.CreatedAt)
            .Skip((normalized.Page - 1) * normalized.PageSize)
            .Take(normalized.PageSize)
            .Select(x => new JobListItemResult(
                x.Id,
                x.JobType,
                x.Status,
                x.PayloadJson,
                x.Error,
                x.CreatedAt,
                x.StartedAt,
                x.CompletedAt,
                x.ProjectId))
            .ToListAsync(cancellationToken);

        var result = new PagedResult<JobListItemResult>(items, normalized.Page, normalized.PageSize, totalCount);
        await objectCache.SetAsync(cacheKey, "dashboard-jobs", result, TimeSpan.FromSeconds(15), cancellationToken);
        return result;
    }

    public async Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken)
    {
        var snapshot = await snapshotStore.GetAsync<DashboardStorageTableStatsSnapshotPayload>(
            DashboardSnapshotKeys.StorageTableStats,
            cancellationToken);
        return snapshot?.Payload.Tables ?? [];
    }

    public async Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
    {
        var maxPageSize = DashboardStoragePolicy.IsLargeTable(request.Table)
            ? DashboardStoragePolicy.LargeTableMaxPageSize
            : 200;
        var normalized = Normalize(request.Page, request.PageSize, maxPageSize);
        var normalizedRequest = request with
        {
            Page = normalized.Page,
            PageSize = normalized.PageSize
        };

        if (DashboardStoragePolicy.IsLargeTablePreviewRequest(normalizedRequest))
        {
            var snapshot = await snapshotStore.GetAsync<DashboardStorageLargeTablePreviewSnapshotPayload>(
                DashboardSnapshotKeys.StorageLargeTablePreview,
                cancellationToken);
            var preview = snapshot?.Payload.Tables.FirstOrDefault(x => string.Equals(x.Table, normalizedRequest.Table, StringComparison.OrdinalIgnoreCase));
            if (preview is not null)
            {
                return preview with
                {
                    Rows = preview.Rows with
                    {
                        Items = preview.Rows.Items.Take(normalized.PageSize).ToArray(),
                        Page = normalized.Page,
                        PageSize = normalized.PageSize
                    },
                    DataSource = "redis"
                };
            }

            var tables = await GetStorageTablesAsync(cancellationToken);
            var table = tables.FirstOrDefault(x => string.Equals(x.Name, normalizedRequest.Table, StringComparison.OrdinalIgnoreCase));
            return new StorageTableRowsResult(
                normalizedRequest.Table,
                table?.Description ?? "Large table preview",
                table?.Columns ?? [],
                [],
                null,
                null,
                new PagedResult<StorageRowResult>([], normalized.Page, normalized.PageSize, table?.RowCount ?? 0),
                "Large table preview snapshot unavailable. Wait for the background collector to finish the first refresh.",
                "fallback");
        }

        return await storageExplorerStore.GetRowsAsync(normalizedRequest, cancellationToken);
    }

    private static IReadOnlyList<ProjectSuggestionResult> FilterProjectSuggestions(
        IReadOnlyList<ProjectSuggestionResult> projects,
        string? query,
        int limit)
    {
        var filtered = projects;
        if (!string.IsNullOrWhiteSpace(query))
        {
            filtered = filtered
                .Where(project => project.ProjectId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return filtered
            .OrderByDescending(project => project.ItemCount)
            .ThenBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Take(limit)
            .ToArray();
    }

    private static (int Page, int PageSize) Normalize(int page, int pageSize, int maxPageSize)
    {
        var normalizedPage = page < 1 ? 1 : page;
        var normalizedPageSize = pageSize < 1 ? 25 : Math.Min(pageSize, maxPageSize);
        return (normalizedPage, normalizedPageSize);
    }

    private IQueryable<MemoryItem> BuildMemoryScopeQuery(
        string? currentProjectId,
        IReadOnlyList<string>? includedProjectIds,
        MemoryQueryMode queryMode,
        bool useSummaryLayer,
        string? projectQuery,
        string? query,
        MemoryScope? scope,
        MemoryType? memoryType,
        MemoryStatus? status,
        string? sourceType,
        string? tag)
    {
        var items = dbContext.MemoryItems.AsNoTracking().AsQueryable();
        var actor = actorAccessor.Current;
        if (actor.HasUser)
        {
            items = items.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
        }
        var allowedProjects = ResolveDashboardSearchProjects(currentProjectId, includedProjectIds, queryMode, useSummaryLayer);

        if (allowedProjects is not null)
        {
            items = items.Where(x => allowedProjects.Contains(x.ProjectId));
        }

        if (!string.IsNullOrWhiteSpace(projectQuery))
        {
            var projectTerm = projectQuery.Trim().ToLowerInvariant();
            items = items.Where(x => x.ProjectId.ToLower().Contains(projectTerm));
        }

        if (!string.IsNullOrWhiteSpace(query))
        {
            var term = query.Trim();
            items = items.Where(x =>
                x.ProjectId.Contains(term) ||
                x.Title.Contains(term) ||
                x.Summary.Contains(term) ||
                x.Content.Contains(term) ||
                x.SourceRef.Contains(term) ||
                x.ExternalKey.Contains(term));
        }

        if (scope.HasValue)
        {
            items = items.Where(x => x.Scope == scope.Value);
        }

        if (memoryType.HasValue)
        {
            items = items.Where(x => x.MemoryType == memoryType.Value);
        }

        if (status.HasValue)
        {
            items = items.Where(x => x.Status == status.Value);
        }

        if (!string.IsNullOrWhiteSpace(sourceType))
        {
            items = items.Where(x => x.SourceType == sourceType);
        }

        if (!string.IsNullOrWhiteSpace(tag))
        {
            items = items.Where(x => x.Tags.Contains(tag));
        }

        return items;
    }

    private async Task<MemoryGraphResult> ApplyActorGraphFilterAsync(MemoryGraphResult graph, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        if (!actor.HasUser || graph.Nodes.Count == 0)
        {
            return graph;
        }

        var nodeIds = graph.Nodes.Select(x => x.Id).ToArray();
        var allowedIds = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => nodeIds.Contains(x.Id))
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
            .Select(x => x.Id)
            .ToListAsync(cancellationToken);
        var allowed = allowedIds.ToHashSet();
        var nodes = graph.Nodes.Where(x => allowed.Contains(x.Id)).ToArray();
        var edges = graph.Edges.Where(x => allowed.Contains(x.FromId) && allowed.Contains(x.ToId)).ToArray();

        return new MemoryGraphResult(
            nodes,
            edges,
            new MemoryGraphStatsResult(nodes.Length, nodes.Length, edges.Length, graph.Stats.Truncated, graph.Stats.TruncationReason));
    }

    private static IReadOnlyList<string>? ResolveDashboardSearchProjects(
        string? currentProjectId,
        IReadOnlyList<string>? includedProjectIds,
        MemoryQueryMode queryMode,
        bool useSummaryLayer)
    {
        var normalizedCurrent = string.IsNullOrWhiteSpace(currentProjectId)
            ? null
            : ProjectContext.Normalize(currentProjectId);
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        switch (queryMode)
        {
            case MemoryQueryMode.CurrentOnly:
                if (normalizedCurrent is not null)
                {
                    values.Add(normalizedCurrent);
                }
                break;
            case MemoryQueryMode.CurrentPlusReferencedProjects:
                if (normalizedCurrent is not null)
                {
                    values.Add(normalizedCurrent);
                }

                foreach (var projectId in includedProjectIds ?? [])
                {
                    var normalized = ProjectContext.Normalize(projectId);
                    if (!ProjectContext.IsShared(normalized) && !ProjectContext.IsUser(normalized))
                    {
                        values.Add(normalized);
                    }
                }
                break;
            case MemoryQueryMode.SummaryOnly:
                values.Add(ProjectContext.SharedProjectId);
                break;
        }

        if (useSummaryLayer && queryMode != MemoryQueryMode.SummaryOnly)
        {
            values.Add(ProjectContext.SharedProjectId);
        }

        return values.Count == 0 ? null : values.ToArray();
    }

    private async Task<SourceConnection?> ResolveSourceConnectionAsync(Guid sourceConnectionId, CancellationToken cancellationToken)
        => await dbContext.SourceConnections.AsNoTracking().FirstOrDefaultAsync(x => x.Id == sourceConnectionId, cancellationToken);

    private async Task<MemoryGraphResult> BuildSeededGraphAsync(
        MemoryGraphRequest request,
        int maxNodes,
        IReadOnlyList<MemoryItem> scopedItems,
        IReadOnlyDictionary<Guid, MemoryItem> scopedById,
        IReadOnlyList<MemoryLink> scopedLinks,
        CancellationToken cancellationToken)
    {
        var degreeMap = BuildScopedDegreeMap(scopedLinks, scopedById.Keys.ToHashSet());
        var isIntegratedAllProjects = IsIntegratedAllProjectsGraphRequest(request);
        var scoredSeeds = !string.IsNullOrWhiteSpace(request.Query)
            ? await SearchScopedItemsAsync(request, request.Query.Trim(), 32, scopedById, cancellationToken)
            : isIntegratedAllProjects
                ? BuildIntegratedSeedCandidates(scopedItems, degreeMap, maxNodes)
            : scopedItems
                .OrderByDescending(item => item.Importance)
                .ThenByDescending(item => item.UpdatedAt)
                .Select(item => new ScoredGraphNode(item, null))
                .ToArray();

        var seedNodes = scoredSeeds
            .Select(entry => entry.Item)
            .DistinctBy(item => item.Id)
            .Take(Math.Min(8, maxNodes))
            .ToArray();

        if (seedNodes.Length == 0)
        {
            return new MemoryGraphResult([], [], new MemoryGraphStatsResult(0, 0, 0, false));
        }

        var seedIds = seedNodes.Select(item => item.Id).ToHashSet();
        var explicitEdges = scopedLinks
            .Where(link => seedIds.Contains(link.FromId) || seedIds.Contains(link.ToId))
            .Where(link => scopedById.ContainsKey(link.FromId) && scopedById.ContainsKey(link.ToId))
            .GroupBy(link => new { link.FromId, link.ToId, link.LinkType }, link => link)
            .Select(group => group.OrderByDescending(link => link.CreatedAt).First())
            .Select(link => new MemoryGraphEdgeResult(link.FromId, link.ToId, "explicit", link.LinkType))
            .ToList();
        var explicitEdgeKeys = explicitEdges
            .Select(edge => BuildUndirectedEdgeKey(edge.FromId, edge.ToId, "explicit"))
            .ToHashSet(StringComparer.Ordinal);

        var explicitNeighborOrder = explicitEdges
            .SelectMany(edge => new[] { edge.FromId, edge.ToId })
            .Where(id => !seedIds.Contains(id))
            .GroupBy(id => id)
            .OrderByDescending(group => group.Count())
            .ThenByDescending(group => scopedById[group.Key].Importance)
            .ThenByDescending(group => scopedById[group.Key].UpdatedAt)
            .Select(group => group.Key)
            .ToList();

        var similarityEdges = new List<MemoryGraphEdgeResult>();
        var similarityOrder = new List<Guid>();
        var similarityEdgeKeys = new HashSet<string>(StringComparer.Ordinal);
        var maxSimilarityNeighborsPerSeed = isIntegratedAllProjects ? 1 : 3;

        if (request.IncludeSimilarity)
        {
            foreach (var seed in seedNodes)
            {
                var similarityQuery = BuildSimilarityQuery(seed);
                if (string.IsNullOrWhiteSpace(similarityQuery))
                {
                    continue;
                }

                var neighbors = await SearchScopedItemsAsync(request, similarityQuery, 12, scopedById, cancellationToken);
                var taken = 0;

                foreach (var candidate in neighbors)
                {
                    if (candidate.Item.Id == seed.Id)
                    {
                        continue;
                    }

                    if (isIntegratedAllProjects &&
                        !IsEligibleIntegratedSimilarityNeighbor(seed, candidate))
                    {
                        continue;
                    }

                    if (explicitEdgeKeys.Contains(BuildUndirectedEdgeKey(seed.Id, candidate.Item.Id, "explicit")))
                    {
                        continue;
                    }

                    var edgeKey = BuildUndirectedEdgeKey(seed.Id, candidate.Item.Id, "similar");
                    if (!similarityEdgeKeys.Add(edgeKey))
                    {
                        continue;
                    }

                    similarityEdges.Add(new MemoryGraphEdgeResult(
                        seed.Id,
                        candidate.Item.Id,
                        "similar",
                        "Similarity",
                        candidate.Score));
                    similarityOrder.Add(candidate.Item.Id);
                    taken++;

                    if (taken >= maxSimilarityNeighborsPerSeed)
                    {
                        break;
                    }
                }
            }
        }

        var orderedIds = seedNodes.Select(item => item.Id)
            .Concat(explicitNeighborOrder)
            .Concat(similarityOrder)
            .Distinct()
            .ToList();

        var truncated = orderedIds.Count > maxNodes;
        if (truncated)
        {
            orderedIds = orderedIds.Take(maxNodes).ToList();
        }

        var selectedIds = orderedIds.ToHashSet();
        var edges = explicitEdges
            .Concat(similarityEdges)
            .Where(edge => selectedIds.Contains(edge.FromId) && selectedIds.Contains(edge.ToId))
            .ToArray();
        var graph = BuildGraphResult(
            orderedIds,
            edges,
            scopedById,
            seedNodes.Length,
            truncated,
            truncated ? $"Graph capped at {maxNodes} nodes. Refine filters to inspect more context." : null);

        return graph;
    }

    private MemoryGraphResult BuildGraphFromSnapshot(
        MemoryGraphRequest request,
        int maxNodes,
        MemoryGraphResult index)
    {
        var scopedNodes = FilterSnapshotNodes(request, index.Nodes).ToArray();
        if (scopedNodes.Length == 0)
        {
            return new MemoryGraphResult([], [], new MemoryGraphStatsResult(0, 0, 0, false));
        }

        var scopedById = scopedNodes.ToDictionary(node => node.Id);
        var scopedIds = scopedById.Keys.ToHashSet();
        var scopedEdges = index.Edges
            .Where(edge => scopedIds.Contains(edge.FromId) && scopedIds.Contains(edge.ToId))
            .Where(edge => request.IncludeSimilarity || !string.Equals(edge.EdgeType, "similar", StringComparison.OrdinalIgnoreCase))
            .ToArray();

        return request.GraphMode == MemoryGraphMode.ProjectFull
            ? BuildProjectFullGraphFromSnapshot(request, maxNodes, scopedNodes, scopedById, scopedIds, scopedEdges)
            : BuildSeededGraphFromSnapshot(request, maxNodes, scopedNodes, scopedById, scopedIds, scopedEdges);
    }

    private static IEnumerable<MemoryGraphNodeResult> FilterSnapshotNodes(
        MemoryGraphRequest request,
        IReadOnlyList<MemoryGraphNodeResult> nodes)
    {
        var filtered = nodes.AsEnumerable();
        var allowedProjects = ResolveDashboardSearchProjects(
            request.ProjectId,
            request.IncludedProjectIds,
            request.QueryMode,
            request.UseSummaryLayer);

        if (allowedProjects is not null)
        {
            filtered = filtered.Where(node => allowedProjects.Contains(node.ProjectId, StringComparer.OrdinalIgnoreCase));
        }

        if (IsIntegratedAllProjectsGraphRequest(request))
        {
            filtered = filtered.Where(node => !ProjectContext.IsShared(node.ProjectId) && !ProjectContext.IsUser(node.ProjectId));
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectQuery))
        {
            var projectTerm = request.ProjectQuery.Trim();
            filtered = filtered.Where(node => node.ProjectId.Contains(projectTerm, StringComparison.OrdinalIgnoreCase));
        }

        if (request.Scope.HasValue)
        {
            filtered = filtered.Where(node => node.Scope == request.Scope.Value);
        }

        if (request.MemoryType.HasValue)
        {
            filtered = filtered.Where(node => node.MemoryType == request.MemoryType.Value);
        }

        if (request.Status.HasValue)
        {
            filtered = filtered.Where(node => node.Status == request.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType))
        {
            filtered = filtered.Where(node => string.Equals(node.SourceType, request.SourceType, StringComparison.Ordinal));
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            filtered = filtered.Where(node => node.Tags.Contains(request.Tag, StringComparer.OrdinalIgnoreCase));
        }

        return filtered;
    }

    private MemoryGraphResult BuildSeededGraphFromSnapshot(
        MemoryGraphRequest request,
        int maxNodes,
        IReadOnlyList<MemoryGraphNodeResult> scopedNodes,
        IReadOnlyDictionary<Guid, MemoryGraphNodeResult> scopedById,
        IReadOnlySet<Guid> scopedIds,
        IReadOnlyList<MemoryGraphEdgeResult> scopedEdges)
    {
        var degreeMap = BuildSnapshotDegreeMap(scopedEdges, scopedIds);
        var isIntegratedAllProjects = IsIntegratedAllProjectsGraphRequest(request);
        var seedNodes = !string.IsNullOrWhiteSpace(request.Query)
            ? RankSnapshotNodesByLexicalSimilarity(request.Query, scopedNodes, Math.Min(maxNodes, isIntegratedAllProjects ? 8 : 6))
            : isIntegratedAllProjects
                ? BuildIntegratedSeedCandidatesFromSnapshot(scopedNodes, degreeMap, maxNodes)
                : scopedNodes
                    .OrderByDescending(node => degreeMap.GetValueOrDefault(node.Id))
                    .ThenByDescending(node => node.Importance)
                    .ThenByDescending(node => node.UpdatedAt)
                    .Take(Math.Min(maxNodes, 6))
                    .ToArray();

        if (seedNodes.Count == 0)
        {
            return new MemoryGraphResult([], [], new MemoryGraphStatsResult(0, 0, 0, false));
        }

        var selectedIds = seedNodes.Select(node => node.Id).ToHashSet();
        foreach (var seed in seedNodes)
        {
            AddSnapshotNeighbors(seed.Id, "explicit", scopedEdges, selectedIds, maxNodes);
        }

        if (request.IncludeSimilarity)
        {
            foreach (var seed in seedNodes)
            {
                AddSnapshotNeighbors(seed.Id, "similar", scopedEdges, selectedIds, maxNodes);
            }
        }

        var orderedIds = selectedIds
            .Select(id => scopedById[id])
            .OrderByDescending(node => seedNodes.Any(seed => seed.Id == node.Id))
            .ThenByDescending(node => degreeMap.GetValueOrDefault(node.Id))
            .ThenByDescending(node => node.Importance)
            .ThenByDescending(node => node.UpdatedAt)
            .Select(node => node.Id)
            .Take(maxNodes)
            .ToArray();
        var orderedSet = orderedIds.ToHashSet();
        var edges = scopedEdges
            .Where(edge => orderedSet.Contains(edge.FromId) && orderedSet.Contains(edge.ToId))
            .ToArray();
        var truncated = selectedIds.Count > orderedIds.Length;

        return BuildGraphResultFromSnapshot(
            orderedIds,
            edges,
            scopedById,
            seedNodes.Count,
            truncated,
            truncated ? $"Graph capped at {maxNodes} nodes. Refine filters to inspect more context." : null);
    }

    private MemoryGraphResult BuildProjectFullGraphFromSnapshot(
        MemoryGraphRequest request,
        int maxNodes,
        IReadOnlyList<MemoryGraphNodeResult> scopedNodes,
        IReadOnlyDictionary<Guid, MemoryGraphNodeResult> scopedById,
        IReadOnlySet<Guid> scopedIds,
        IReadOnlyList<MemoryGraphEdgeResult> scopedEdges)
    {
        var degreeMap = BuildSnapshotDegreeMap(scopedEdges, scopedIds);
        var candidates = string.IsNullOrWhiteSpace(request.Query)
            ? scopedNodes
            : RankSnapshotNodesByLexicalSimilarity(request.Query, scopedNodes, scopedNodes.Count);
        var orderedIds = candidates
            .OrderByDescending(node => degreeMap.GetValueOrDefault(node.Id))
            .ThenByDescending(node => node.Importance)
            .ThenByDescending(node => node.UpdatedAt)
            .Select(node => node.Id)
            .Take(maxNodes)
            .ToArray();
        var selectedIds = orderedIds.ToHashSet();
        var edges = scopedEdges
            .Where(edge => selectedIds.Contains(edge.FromId) && selectedIds.Contains(edge.ToId))
            .ToArray();
        var truncated = scopedNodes.Count > orderedIds.Length;

        return BuildGraphResultFromSnapshot(
            orderedIds,
            edges,
            scopedById,
            0,
            truncated,
            truncated ? $"Graph capped at {maxNodes} nodes. Add filters to narrow the project graph." : null);
    }

    private static void AddSnapshotNeighbors(
        Guid sourceId,
        string edgeType,
        IReadOnlyList<MemoryGraphEdgeResult> edges,
        ISet<Guid> selectedIds,
        int maxNodes)
    {
        foreach (var edge in edges
                     .Where(edge => string.Equals(edge.EdgeType, edgeType, StringComparison.OrdinalIgnoreCase))
                     .Where(edge => edge.FromId == sourceId || edge.ToId == sourceId)
                     .OrderByDescending(edge => edge.Score ?? 1m))
        {
            if (selectedIds.Count >= maxNodes)
            {
                return;
            }

            selectedIds.Add(edge.FromId == sourceId ? edge.ToId : edge.FromId);
        }
    }

    private MemoryGraphResult BuildGraphResultFromSnapshot(
        IReadOnlyList<Guid> orderedIds,
        IReadOnlyList<MemoryGraphEdgeResult> edges,
        IReadOnlyDictionary<Guid, MemoryGraphNodeResult> scopedById,
        int seedCount,
        bool truncated,
        string? truncationReason)
    {
        var explicitCounts = BuildNeighborCountLookup(edges, "explicit");
        var similarityCounts = BuildNeighborCountLookup(edges, "similar");
        var nodes = orderedIds
            .Select(id =>
            {
                var node = scopedById[id];
                return node with
                {
                    ExplicitLinkCount = explicitCounts.GetValueOrDefault(id),
                    SimilarityNeighborCount = similarityCounts.GetValueOrDefault(id)
                };
            })
            .ToArray();

        return new MemoryGraphResult(
            nodes,
            edges,
            new MemoryGraphStatsResult(seedCount, nodes.Length, edges.Count, truncated, truncationReason));
    }

    private static Dictionary<Guid, int> BuildSnapshotDegreeMap(
        IReadOnlyList<MemoryGraphEdgeResult> edges,
        IReadOnlySet<Guid> scopedIds)
    {
        var degreeMap = scopedIds.ToDictionary(id => id, _ => 0);
        foreach (var edge in edges)
        {
            if (degreeMap.ContainsKey(edge.FromId))
            {
                degreeMap[edge.FromId]++;
            }

            if (degreeMap.ContainsKey(edge.ToId))
            {
                degreeMap[edge.ToId]++;
            }
        }

        return degreeMap;
    }

    private static IReadOnlyList<MemoryGraphNodeResult> BuildIntegratedSeedCandidatesFromSnapshot(
        IReadOnlyList<MemoryGraphNodeResult> scopedNodes,
        IReadOnlyDictionary<Guid, int> degreeMap,
        int maxNodes)
    {
        var targetSeedCount = Math.Min(8, maxNodes);
        var orderedItems = scopedNodes
            .OrderByDescending(node => degreeMap.GetValueOrDefault(node.Id))
            .ThenByDescending(node => node.Importance)
            .ThenByDescending(node => node.UpdatedAt)
            .ToArray();
        var selected = new List<MemoryGraphNodeResult>(targetSeedCount);
        var perProjectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in orderedItems)
        {
            if (perProjectCounts.ContainsKey(item.ProjectId))
            {
                continue;
            }

            selected.Add(item);
            perProjectCounts[item.ProjectId] = 1;

            if (selected.Count >= targetSeedCount)
            {
                return selected;
            }
        }

        foreach (var item in orderedItems)
        {
            if (selected.Any(entry => entry.Id == item.Id))
            {
                continue;
            }

            var currentCount = perProjectCounts.GetValueOrDefault(item.ProjectId);
            if (currentCount >= 2)
            {
                continue;
            }

            selected.Add(item);
            perProjectCounts[item.ProjectId] = currentCount + 1;

            if (selected.Count >= targetSeedCount)
            {
                return selected;
            }
        }

        foreach (var item in orderedItems)
        {
            if (selected.Any(entry => entry.Id == item.Id))
            {
                continue;
            }

            selected.Add(item);
            if (selected.Count >= targetSeedCount)
            {
                break;
            }
        }

        return selected;
    }

    private static IReadOnlyList<MemoryGraphNodeResult> RankSnapshotNodesByLexicalSimilarity(
        string? query,
        IEnumerable<MemoryGraphNodeResult> nodes,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit < 1)
        {
            return [];
        }

        var normalizedQuery = query.Trim();
        var tokens = Tokenize(normalizedQuery);
        return nodes
            .Select(node => (Node: node, Score: ScoreSnapshotLexicalSimilarity(node, normalizedQuery, tokens)))
            .Where(entry => entry.Score > 0m)
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Node.Importance)
            .ThenByDescending(entry => entry.Node.UpdatedAt)
            .Take(limit)
            .Select(entry => entry.Node)
            .ToArray();
    }

    private static decimal ScoreSnapshotLexicalSimilarity(
        MemoryGraphNodeResult node,
        string rawQuery,
        IReadOnlySet<string> queryTokens)
    {
        var normalizedQuery = rawQuery.Trim();
        var haystack = string.Join(
            ' ',
            [
                node.ProjectId,
                node.Title,
                node.Summary,
                node.SourceRef,
                node.SourceLabel,
                string.Join(' ', node.Tags)
            ]).Trim();

        if (string.IsNullOrWhiteSpace(haystack))
        {
            return decimal.Zero;
        }

        var score = decimal.Zero;
        if (node.Title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.7m;
        }

        if (node.Summary.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.35m;
        }

        if (node.SourceRef.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.15m;
        }

        if (node.ProjectId.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.2m;
        }

        if (queryTokens.Count == 0)
        {
            return score;
        }

        var candidateTokens = Tokenize(haystack);
        if (candidateTokens.Count == 0)
        {
            return score;
        }

        var overlap = queryTokens.Count(candidateTokens.Contains);
        return overlap == 0 ? score : score + decimal.Divide(overlap, queryTokens.Count);
    }

    private async Task<MemoryGraphResult> BuildProjectFullGraphAsync(
        MemoryGraphRequest request,
        int maxNodes,
        IReadOnlyList<MemoryItem> scopedItems,
        IReadOnlyDictionary<Guid, MemoryItem> scopedById,
        IReadOnlyList<MemoryLink> scopedLinks,
        CancellationToken cancellationToken)
    {
        var scopedIds = scopedById.Keys.ToHashSet();
        var degreeMap = BuildScopedDegreeMap(scopedLinks, scopedIds);
        var isIntegratedAllProjects = IsIntegratedAllProjectsGraphRequest(request);
        var maxSimilarityNeighborsPerNode = isIntegratedAllProjects ? 1 : 2;

        var orderedIds = scopedItems
            .OrderByDescending(item => degreeMap.GetValueOrDefault(item.Id))
            .ThenByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .Select(item => item.Id)
            .Take(maxNodes)
            .ToList();

        var truncated = scopedItems.Count > orderedIds.Count;
        var selectedIds = orderedIds.ToHashSet();
        var explicitEdges = scopedLinks
            .Where(link => selectedIds.Contains(link.FromId) && selectedIds.Contains(link.ToId))
            .GroupBy(link => new { link.FromId, link.ToId, link.LinkType }, link => link)
            .Select(group => group.OrderByDescending(link => link.CreatedAt).First())
            .Select(link => new MemoryGraphEdgeResult(link.FromId, link.ToId, "explicit", link.LinkType))
            .ToList();
        var explicitEdgeKeys = explicitEdges
            .Select(edge => BuildUndirectedEdgeKey(edge.FromId, edge.ToId, "explicit"))
            .ToHashSet(StringComparer.Ordinal);

        var similarityEdges = new List<MemoryGraphEdgeResult>();
        var similarityEdgeKeys = new HashSet<string>(StringComparer.Ordinal);

        if (request.IncludeSimilarity)
        {
            foreach (var nodeId in orderedIds)
            {
                var node = scopedById[nodeId];
                var similarityQuery = BuildSimilarityQuery(node);
                if (string.IsNullOrWhiteSpace(similarityQuery))
                {
                    continue;
                }

                var candidates = await SearchScopedItemsAsync(request, similarityQuery, 10, scopedById, cancellationToken);
                var taken = 0;

                foreach (var candidate in candidates)
                {
                    if (candidate.Item.Id == nodeId || !selectedIds.Contains(candidate.Item.Id))
                    {
                        continue;
                    }

                    if (isIntegratedAllProjects &&
                        !IsEligibleIntegratedSimilarityNeighbor(node, candidate))
                    {
                        continue;
                    }

                    if (explicitEdgeKeys.Contains(BuildUndirectedEdgeKey(nodeId, candidate.Item.Id, "explicit")))
                    {
                        continue;
                    }

                    var edgeKey = BuildUndirectedEdgeKey(nodeId, candidate.Item.Id, "similar");
                    if (!similarityEdgeKeys.Add(edgeKey))
                    {
                        continue;
                    }

                    similarityEdges.Add(new MemoryGraphEdgeResult(
                        nodeId,
                        candidate.Item.Id,
                        "similar",
                        "Similarity",
                        candidate.Score));
                    taken++;

                    if (taken >= maxSimilarityNeighborsPerNode)
                    {
                        break;
                    }
                }
            }
        }

        return BuildGraphResult(
            orderedIds,
            explicitEdges.Concat(similarityEdges).ToArray(),
            scopedById,
            0,
            truncated,
            truncated ? $"Graph capped at {maxNodes} nodes. Add filters to narrow the project graph." : null);
    }

    private MemoryGraphResult BuildGraphResult(
        IReadOnlyList<Guid> orderedIds,
        IReadOnlyList<MemoryGraphEdgeResult> edges,
        IReadOnlyDictionary<Guid, MemoryItem> scopedById,
        int seedCount,
        bool truncated,
        string? truncationReason)
    {
        var explicitCounts = BuildNeighborCountLookup(edges, "explicit");
        var similarityCounts = BuildNeighborCountLookup(edges, "similar");
        var nodes = orderedIds
            .Select(id => BuildGraphNode(
                scopedById[id],
                explicitCounts.GetValueOrDefault(id),
                similarityCounts.GetValueOrDefault(id)))
            .ToArray();

        return new MemoryGraphResult(
            nodes,
            edges,
            new MemoryGraphStatsResult(seedCount, nodes.Length, edges.Count, truncated, truncationReason));
    }

    private Dictionary<Guid, int> BuildNeighborCountLookup(IReadOnlyList<MemoryGraphEdgeResult> edges, string edgeType)
    {
        return edges
            .Where(edge => string.Equals(edge.EdgeType, edgeType, StringComparison.OrdinalIgnoreCase))
            .SelectMany(edge => new[] { (NodeId: edge.FromId, NeighborId: edge.ToId), (NodeId: edge.ToId, NeighborId: edge.FromId) })
            .GroupBy(entry => entry.NodeId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.NeighborId).Distinct().Count());
    }

    private async Task<IReadOnlyList<ScoredGraphNode>> SearchScopedItemsAsync(
        MemoryGraphRequest request,
        string query,
        int limit,
        IReadOnlyDictionary<Guid, MemoryItem> scopedById,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var normalizedQuery = query.Trim();
        var hits = await memoryService.SearchAsync(
            new MemorySearchRequest(
                normalizedQuery,
                limit,
                IncludeArchived: false,
                ProjectId: ProjectContext.Normalize(request.ProjectId),
                IncludedProjectIds: request.IncludedProjectIds,
                QueryMode: request.QueryMode,
                UseSummaryLayer: request.UseSummaryLayer,
                Telemetry: new RetrievalTelemetryContext("dashboard.memory_graph", "dashboard", "graph explorer search")),
            cancellationToken);
        var searchResults = hits
            .Where(hit => scopedById.ContainsKey(hit.MemoryId))
            .Select(hit => new ScoredGraphNode(scopedById[hit.MemoryId], hit.Score))
            .GroupBy(entry => entry.Item.Id)
            .Select(group => group.OrderByDescending(entry => entry.Score ?? decimal.Zero).First())
            .ToList();
        var existingIds = searchResults.Select(entry => entry.Item.Id).ToHashSet();
        var fallbackResults = RankScopedItemsByLexicalSimilarity(normalizedQuery, scopedById.Values, limit * 2)
            .Where(entry => !existingIds.Contains(entry.Item.Id));

        return searchResults
            .Concat(fallbackResults)
            .Take(limit)
            .ToArray();
    }

    private MemoryGraphNodeResult BuildGraphNode(MemoryItem entity, int explicitLinkCount, int similarityNeighborCount)
    {
        var sourceContext = BuildSourceContext(entity);
        var thumbnailUrl = ResolveThumbnailUrl(sourceContext?.OriginPathOrUrl);
        var faviconUrl = thumbnailUrl is null ? ResolveFaviconUrl(sourceContext?.OriginPathOrUrl) : null;
        var sourceLabel = sourceContext?.ConnectorName
            ?? (!string.IsNullOrWhiteSpace(sourceContext?.OriginPathOrUrl) ? sourceContext!.OriginPathOrUrl! : entity.SourceType);

        return new MemoryGraphNodeResult(
            entity.Id,
            entity.Title,
            entity.Summary,
            entity.ProjectId,
            entity.MemoryType,
            entity.Scope,
            entity.Status,
            entity.Tags,
            entity.SourceType,
            entity.SourceRef,
            entity.UpdatedAt,
            entity.Importance,
            entity.Confidence,
            entity.IsReadOnly,
            thumbnailUrl,
            faviconUrl,
            sourceLabel,
            explicitLinkCount,
            similarityNeighborCount);
    }

    private static int NormalizeGraphMaxNodes(int maxNodes)
        => maxNodes < 1 ? 120 : Math.Min(maxNodes, 120);

    private static bool IsIntegratedAllProjectsGraphRequest(MemoryGraphRequest request)
        => string.IsNullOrWhiteSpace(request.ProjectId) &&
           string.IsNullOrWhiteSpace(request.ProjectQuery) &&
           (request.IncludedProjectIds is null || request.IncludedProjectIds.Count == 0) &&
           request.QueryMode != MemoryQueryMode.SummaryOnly;

    private static Dictionary<Guid, int> BuildScopedDegreeMap(
        IReadOnlyList<MemoryLink> scopedLinks,
        IReadOnlySet<Guid> scopedIds)
    {
        var degreeMap = scopedLinks
            .Where(link => scopedIds.Contains(link.FromId) && scopedIds.Contains(link.ToId))
            .GroupBy(link => link.FromId)
            .ToDictionary(group => group.Key, group => group.Count());

        foreach (var incoming in scopedLinks
                     .Where(link => scopedIds.Contains(link.FromId) && scopedIds.Contains(link.ToId))
                     .GroupBy(link => link.ToId))
        {
            degreeMap[incoming.Key] = degreeMap.TryGetValue(incoming.Key, out var current)
                ? current + incoming.Count()
                : incoming.Count();
        }

        return degreeMap;
    }

    private static IReadOnlyList<ScoredGraphNode> BuildIntegratedSeedCandidates(
        IReadOnlyList<MemoryItem> scopedItems,
        IReadOnlyDictionary<Guid, int> degreeMap,
        int maxNodes)
    {
        var targetSeedCount = Math.Min(8, maxNodes);
        var orderedItems = scopedItems
            .OrderByDescending(item => degreeMap.GetValueOrDefault(item.Id))
            .ThenByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .ToArray();
        var selected = new List<ScoredGraphNode>(targetSeedCount);
        var perProjectCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in orderedItems)
        {
            if (perProjectCounts.ContainsKey(item.ProjectId))
            {
                continue;
            }

            selected.Add(new ScoredGraphNode(item, null));
            perProjectCounts[item.ProjectId] = 1;

            if (selected.Count >= targetSeedCount)
            {
                return selected;
            }
        }

        foreach (var item in orderedItems)
        {
            if (selected.Any(entry => entry.Item.Id == item.Id))
            {
                continue;
            }

            var currentCount = perProjectCounts.GetValueOrDefault(item.ProjectId);
            if (currentCount >= 2)
            {
                continue;
            }

            selected.Add(new ScoredGraphNode(item, null));
            perProjectCounts[item.ProjectId] = currentCount + 1;

            if (selected.Count >= targetSeedCount)
            {
                return selected;
            }
        }

        foreach (var item in orderedItems)
        {
            if (selected.Any(entry => entry.Item.Id == item.Id))
            {
                continue;
            }

            selected.Add(new ScoredGraphNode(item, null));
            if (selected.Count >= targetSeedCount)
            {
                break;
            }
        }

        return selected;
    }

    private static bool IsEligibleIntegratedSimilarityNeighbor(MemoryItem source, ScoredGraphNode candidate)
    {
        if (string.Equals(source.ProjectId, candidate.Item.ProjectId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (candidate.Score is null || candidate.Score < 0.90m)
        {
            return false;
        }

        return source.Tags.Intersect(candidate.Item.Tags, StringComparer.OrdinalIgnoreCase).Any();
    }

    private static string BuildSimilarityQuery(MemoryItem item)
        => string.Join(' ', new[] { item.Title, item.Summary }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static IReadOnlyList<ScoredGraphNode> RankScopedItemsByLexicalSimilarity(
        string query,
        IEnumerable<MemoryItem> items,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit < 1)
        {
            return [];
        }

        var tokens = Tokenize(query);
        var results = items
            .Select(item => new ScoredGraphNode(item, ScoreLexicalSimilarity(item, query, tokens)))
            .Where(entry => entry.Score is > 0m)
            .OrderByDescending(entry => entry.Score)
            .ThenByDescending(entry => entry.Item.Importance)
            .ThenByDescending(entry => entry.Item.UpdatedAt)
            .Take(limit)
            .ToArray();

        return results;
    }

    private static decimal ScoreLexicalSimilarity(MemoryItem item, string rawQuery, IReadOnlySet<string> queryTokens)
    {
        var title = item.Title ?? string.Empty;
        var summary = item.Summary ?? string.Empty;
        var sourceRef = item.SourceRef ?? string.Empty;
        var normalizedQuery = rawQuery.Trim();
        var haystack = string.Join(' ', [title, summary, sourceRef, string.Join(' ', item.Tags)]).Trim();
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return decimal.Zero;
        }

        var score = decimal.Zero;
        if (title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.7m;
        }

        if (summary.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.35m;
        }

        if (sourceRef.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.15m;
        }

        if (queryTokens.Count == 0)
        {
            return score;
        }

        var candidateTokens = Tokenize(haystack);
        if (candidateTokens.Count == 0)
        {
            return score;
        }

        var overlap = queryTokens.Count(candidateTokens.Contains);
        if (overlap == 0)
        {
            return score;
        }

        score += decimal.Divide(overlap, queryTokens.Count);
        return score;
    }

    private static IReadOnlySet<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        var tokens = text
            .Split(
                [' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        return tokens;
    }

    private static string BuildUndirectedEdgeKey(Guid fromId, Guid toId, string edgeType)
    {
        var ordered = fromId.CompareTo(toId) <= 0 ? $"{fromId}:{toId}" : $"{toId}:{fromId}";
        return $"{edgeType}:{ordered}";
    }

    private static string? ResolveThumbnailUrl(string? originPathOrUrl)
    {
        if (!Uri.TryCreate(originPathOrUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = uri.AbsolutePath;
        var isImage = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase);

        return isImage ? uri.ToString() : null;
    }

    private static string? ResolveFaviconUrl(string? originPathOrUrl)
    {
        if (!Uri.TryCreate(originPathOrUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Host}/favicon.ico";
    }

    private MemorySourceContextResult? BuildSourceContext(MemoryItem entity)
    {
        if (string.IsNullOrWhiteSpace(entity.MetadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(entity.MetadataJson);
            if (!document.RootElement.TryGetProperty("connectorId", out var connectorElement) ||
                connectorElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(connectorElement.GetString(), out var connectorId))
            {
                return null;
            }

            var source = dbContext.SourceConnections
                .AsNoTracking()
                .FirstOrDefault(x => x.Id == connectorId);
            var lineage = document.RootElement.TryGetProperty("lineage", out var lineageElement) && lineageElement.ValueKind == JsonValueKind.Array
                ? lineageElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray()
                : [];
            var syncedAt = document.RootElement.TryGetProperty("syncedAt", out var syncedElement) && syncedElement.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(syncedElement.GetString(), out var syncedAtValue)
                ? syncedAtValue
                : (DateTimeOffset?)null;

            return new MemorySourceContextResult(
                connectorId,
                source?.Name,
                document.RootElement.TryGetProperty("cursor", out var cursorElement) ? cursorElement.GetString() : null,
                document.RootElement.TryGetProperty("sourceVersion", out var sourceVersionElement) ? sourceVersionElement.GetString() : null,
                document.RootElement.TryGetProperty("originPathOrUrl", out var originElement) ? originElement.GetString() : null,
                syncedAt,
                source?.LastSuccessfulSyncAt,
                lineage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private sealed record ScoredGraphNode(MemoryItem Item, decimal? Score);
}

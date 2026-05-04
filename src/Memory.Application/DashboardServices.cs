using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Memory.Application;

public sealed class DashboardQueryService(
    IApplicationDbContext dbContext,
    IStorageExplorerStore storageExplorerStore,
    IDashboardSnapshotStore snapshotStore,
    IMemoryService memoryService,
    TimeProvider timeProvider) : IDashboardQueryService
{
    public async Task<DashboardOverviewResult> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        var statusCore = await snapshotStore.GetAsync<DashboardStatusCoreSnapshotPayload>(DashboardSnapshotKeys.StatusCore, cancellationToken);
        var dependencies = await snapshotStore.GetAsync<DashboardDependenciesHealthSnapshotPayload>(DashboardSnapshotKeys.DependenciesHealth, cancellationToken);
        var recentOperations = await snapshotStore.GetAsync<DashboardRecentOperationsSnapshotPayload>(DashboardSnapshotKeys.RecentOperations, cancellationToken);
        var resourceChart = await snapshotStore.GetAsync<DashboardResourceChartSnapshotPayload>(DashboardSnapshotKeys.ResourceChart, cancellationToken);
        var dependencyResources = await snapshotStore.GetAsync<DashboardDependencyResourcesResult>(DashboardSnapshotKeys.DependencyResources, cancellationToken);
        var dockerHost = await snapshotStore.GetAsync<DashboardDockerHostResult>(DashboardSnapshotKeys.DockerHost, cancellationToken);

        var sectionStatuses = new[]
        {
            BuildSectionStatus(DashboardSnapshotKeys.StatusCore, "核心狀態", statusCore, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependenciesHealth, "依賴健康", dependencies, now),
            BuildSectionStatus(DashboardSnapshotKeys.RecentOperations, "近期維運摘要", recentOperations, now),
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
            await BuildEvaluationSummaryAsync(cancellationToken));
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

        var sectionStatuses = new[]
        {
            BuildSectionStatus(DashboardSnapshotKeys.StatusCore, "核心狀態", statusCore, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependenciesHealth, "依賴健康", dependencies, now),
            BuildSectionStatus(DashboardSnapshotKeys.DockerHost, "Docker 主機", dockerHost, now),
            BuildSectionStatus(DashboardSnapshotKeys.DependencyResources, "Compose 服務資源", dependencyResources, now),
            BuildSectionStatus(DashboardSnapshotKeys.ResourceChart, "資源趨勢", resourceChart, now),
            BuildSectionStatus(DashboardSnapshotKeys.MonitoringStats, "Redis / PostgreSQL 統計", monitoring, now)
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

        var isStale = envelope.StaleAfterUtc < now;
        var warning = isStale
            ? $"資料已延遲 {Math.Max(1, (int)Math.Round((now - envelope.CapturedAtUtc).TotalSeconds))} 秒。"
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
        => !string.Equals(section.Key, DashboardSnapshotKeys.ResourceChart, StringComparison.Ordinal);

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

        return new PagedResult<MemoryListItemResult>(items, normalized.Page, normalized.PageSize, totalCount);
    }

    public async Task<MemoryGraphResult> GetMemoryGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken)
    {
        var normalizedMaxNodes = NormalizeGraphMaxNodes(request.MaxNodes);
        var scopedItems = await BuildMemoryScopeQuery(
                request.ProjectId,
                request.IncludedProjectIds,
                request.QueryMode,
                request.UseSummaryLayer,
                request.ProjectQuery,
                null,
                request.Scope,
                request.MemoryType,
                request.Status,
                request.SourceType,
                request.Tag)
            .ToListAsync(cancellationToken);

        if (IsIntegratedAllProjectsGraphRequest(request))
        {
            scopedItems = scopedItems
                .Where(item => !ProjectContext.IsShared(item.ProjectId) && !ProjectContext.IsUser(item.ProjectId))
                .ToList();
        }

        if (scopedItems.Count == 0)
        {
            return new MemoryGraphResult([], [], new MemoryGraphStatsResult(0, 0, 0, false));
        }

        var scopedById = scopedItems.ToDictionary(item => item.Id);
        var scopedIds = scopedById.Keys.ToHashSet();
        var scopedLinks = await dbContext.MemoryLinks
            .AsNoTracking()
            .Where(x => scopedIds.Contains(x.FromId) || scopedIds.Contains(x.ToId))
            .ToListAsync(cancellationToken);

        return request.GraphMode == MemoryGraphMode.ProjectFull
            ? await BuildProjectFullGraphAsync(request, normalizedMaxNodes, scopedItems, scopedById, scopedLinks, cancellationToken)
            : await BuildSeededGraphAsync(request, normalizedMaxNodes, scopedItems, scopedById, scopedLinks, cancellationToken);
    }

    public async Task<IReadOnlyList<ProjectSuggestionResult>> GetProjectSuggestionsAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        var normalizedLimit = limit < 1 ? 8 : Math.Min(limit, 20);
        var projects = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => x.ProjectId != ProjectContext.SharedProjectId && x.ProjectId != ProjectContext.UserProjectId)
            .GroupBy(x => x.ProjectId)
            .Select(group => new ProjectSuggestionResult(group.Key, group.Count()))
            .ToListAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(query))
        {
            projects = projects
                .Where(project => project.ProjectId.Contains(query, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        return projects
            .OrderByDescending(project => project.ItemCount)
            .ThenBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Take(normalizedLimit)
            .ToArray();
    }

    public async Task<MemoryDetailsResult?> GetMemoryDetailsAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MemoryItems
            .AsNoTracking()
            .Include(x => x.Revisions)
            .Include(x => x.Chunks)
                .ThenInclude(x => x.Vectors)
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

        return new MemoryDetailsResult(document, revisions, chunks, links, findings, sourceContext);
    }

    public async Task<PagedResult<JobListItemResult>> GetJobsAsync(JobListRequest request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request.Page, request.PageSize, 100);
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

        return new PagedResult<JobListItemResult>(items, normalized.Page, normalized.PageSize, totalCount);
    }

    public Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken)
        => storageExplorerStore.ListTablesAsync(cancellationToken);

    public Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
    {
        var normalized = Normalize(request.Page, request.PageSize, 200);
        return storageExplorerStore.GetRowsAsync(
            request with
            {
                Page = normalized.Page,
                PageSize = normalized.PageSize
            },
            cancellationToken);
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

    private async Task<DashboardEvaluationSummaryResult?> BuildEvaluationSummaryAsync(CancellationToken cancellationToken)
    {
        try
        {
            var latestRun = await dbContext.EvaluationRuns
                .AsNoTracking()
                .OrderByDescending(x => x.StartedAt)
                .FirstOrDefaultAsync(cancellationToken);
            if (latestRun is null)
            {
                return null;
            }

            var suiteName = await dbContext.EvaluationSuites
                .AsNoTracking()
                .Where(x => x.Id == latestRun.SuiteId)
                .Select(x => x.Name)
                .FirstOrDefaultAsync(cancellationToken);
            return new DashboardEvaluationSummaryResult(
                latestRun.Id,
                latestRun.SuiteId,
                suiteName ?? "Unnamed suite",
                latestRun.Status,
                latestRun.HitRate,
                latestRun.RecallAtK,
                latestRun.MeanReciprocalRank,
                latestRun.AverageLatencyMs,
                latestRun.StartedAt,
                latestRun.CompletedAt);
        }
        catch (NotSupportedException)
        {
            return null;
        }
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

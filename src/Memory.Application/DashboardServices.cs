using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class DashboardQueryService(
    IApplicationDbContext dbContext,
    IStorageExplorerStore storageExplorerStore,
    IDashboardSnapshotStore snapshotStore,
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
            resourceChart?.Payload.Samples ?? []);
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
        var query = dbContext.MemoryItems.AsNoTracking().AsQueryable();
        var allowedProjects = ResolveDashboardSearchProjects(
            request.ProjectId,
            request.IncludedProjectIds,
            request.QueryMode,
            request.UseSummaryLayer);

        if (allowedProjects is not null)
        {
            query = query.Where(x => allowedProjects.Contains(x.ProjectId));
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectQuery))
        {
            var projectTerm = request.ProjectQuery.Trim().ToLowerInvariant();
            query = query.Where(x => x.ProjectId.ToLower().Contains(projectTerm));
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            var term = request.Query.Trim();
            query = query.Where(x =>
                x.ProjectId.Contains(term) ||
                x.Title.Contains(term) ||
                x.Summary.Contains(term) ||
                x.Content.Contains(term) ||
                x.SourceRef.Contains(term) ||
                x.ExternalKey.Contains(term));
        }

        if (request.Scope.HasValue)
        {
            query = query.Where(x => x.Scope == request.Scope.Value);
        }

        if (request.MemoryType.HasValue)
        {
            query = query.Where(x => x.MemoryType == request.MemoryType.Value);
        }

        if (request.Status.HasValue)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType))
        {
            query = query.Where(x => x.SourceType == request.SourceType);
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            query = query.Where(x => x.Tags.Contains(request.Tag));
        }

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

        return new MemoryDetailsResult(document, revisions, chunks);
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
}

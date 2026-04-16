using Memory.Application;
using Memory.Domain;

namespace Memory.Dashboard.Services.Testing;

internal sealed class BrowserTestContextHubApiClient : IContextHubApiClient
{
    private readonly DashboardBrowserTestProfileAccessor _profileAccessor;

    public BrowserTestContextHubApiClient(DashboardBrowserTestProfileAccessor profileAccessor)
    {
        _profileAccessor = profileAccessor;
    }

    private DashboardBrowserTestProfile Profile => _profileAccessor.Current;

    private static DateTimeOffset BuildTimestampUtc => DateTimeOffset.Parse("2026-04-12T00:30:00+00:00");

    public Task<SystemStatusResult> GetStatusAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new SystemStatusResult(
            "mcp-server",
            Profile == DashboardBrowserTestProfile.Dense ? "dense" : "test",
            "2026.04.12-browser-test",
            BuildTimestampUtc,
            "Http",
            "CPUExecutionProvider",
            Profile == DashboardBrowserTestProfile.Dense ? "dense" : "compact",
            Profile == DashboardBrowserTestProfile.Dense
                ? "intfloat/multilingual-e5-large-with-super-long-model-key-for-layout-validation"
                : "intfloat/multilingual-e5-small",
            Profile == DashboardBrowserTestProfile.Dense ? 768 : 384,
            Profile == DashboardBrowserTestProfile.Dense ? 1024 : 512,
            6,
            Profile == DashboardBrowserTestProfile.Dense ? 16 : 8,
            true,
            Profile == DashboardBrowserTestProfile.Dense ? 24 : 12,
            now,
            now.AddSeconds(-1),
            Profile == DashboardBrowserTestProfile.Dense ? 1 : 3,
            false,
            string.Empty,
            string.Empty));
    }

    public Task<DashboardOverviewResult> GetOverviewAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var trafficSamples = BuildTrafficSamples();
        return Task.FromResult(new DashboardOverviewResult(
            Profile == DashboardBrowserTestProfile.Dense ? "context-hub-dense-browser-suite" : "test",
            "2026.04.12-browser-test",
            BuildTimestampUtc,
            Profile == DashboardBrowserTestProfile.Dense ? "dense" : "compact",
            Profile == DashboardBrowserTestProfile.Dense
                ? "intfloat/multilingual-e5-large-with-super-long-model-key-for-layout-validation"
                : "intfloat/multilingual-e5-small",
            Profile == DashboardBrowserTestProfile.Dense ? 768 : 384,
            Profile == DashboardBrowserTestProfile.Dense ? 1024 : 512,
            Profile == DashboardBrowserTestProfile.Empty ? 0 : Profile == DashboardBrowserTestProfile.Dense ? 37 : 12,
            BuildServices(),
            BuildMetrics(),
            trafficSamples,
            BuildJobs(now),
            BuildLogs(now),
            now,
            BuildPageSnapshotStatus(now, Profile == DashboardBrowserTestProfile.Empty),
            BuildDockerHost(now),
            BuildDependencyResources(),
            BuildResourceSamples(trafficSamples)));
    }

    public Task<DashboardRuntimeResult> GetRuntimeAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return Task.FromResult(new DashboardRuntimeResult(
            Profile == DashboardBrowserTestProfile.Dense ? "context-hub-dense-browser-suite" : "test",
            "2026.04.12-browser-test",
            BuildTimestampUtc,
            "Http",
            "CPUExecutionProvider",
            Profile == DashboardBrowserTestProfile.Dense ? "dense" : "compact",
            Profile == DashboardBrowserTestProfile.Dense
                ? "intfloat/multilingual-e5-large-with-super-long-model-key-for-layout-validation"
                : "intfloat/multilingual-e5-small",
            Profile == DashboardBrowserTestProfile.Dense ? 768 : 384,
            Profile == DashboardBrowserTestProfile.Dense ? 1024 : 512,
            6,
            Profile == DashboardBrowserTestProfile.Dense ? 16 : 8,
            true,
            BuildServices(),
            BuildRuntimeParameters(),
            now,
            BuildPageSnapshotStatus(now, Profile == DashboardBrowserTestProfile.Empty),
            BuildDockerHost(now),
            BuildDependencyResources()));
    }

    public Task<DashboardMonitoringResult> GetMonitoringAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var trafficSamples = BuildTrafficSamples();
        return Task.FromResult(new DashboardMonitoringResult(
            Profile == DashboardBrowserTestProfile.Dense ? "context-hub-dense-browser-suite" : "test",
            "2026.04.12-browser-test",
            BuildTimestampUtc,
            BuildServices(),
            now,
            BuildRedisTelemetry(),
            BuildPostgresTelemetry(),
            BuildPageSnapshotStatus(now, Profile == DashboardBrowserTestProfile.Empty),
            BuildDockerHost(now),
            BuildDependencyResources(),
            BuildResourceSamples(trafficSamples)));
    }

    public Task<PagedResult<MemoryListItemResult>> GetMemoriesAsync(MemoryListRequest request, CancellationToken cancellationToken)
    {
        var memories = BuildMemories().AsEnumerable();

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            memories = memories.Where(document => string.Equals(document.ProjectId, request.ProjectId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectQuery))
        {
            memories = memories.Where(document => document.ProjectId.Contains(request.ProjectQuery, StringComparison.OrdinalIgnoreCase));
        }

        var items = memories.Select(document => new MemoryListItemResult(
            document.Id,
            document.ProjectId,
            document.ExternalKey,
            document.Scope,
            document.MemoryType,
            document.Title,
            document.Summary,
            document.SourceType,
            document.SourceRef,
            document.Tags,
            document.Importance,
            document.Confidence,
            document.Version,
            document.Status,
            document.UpdatedAt,
            document.IsReadOnly)).ToArray();

        return Task.FromResult(new PagedResult<MemoryListItemResult>(
            items,
            request.Page,
            request.PageSize,
            items.Length));
    }

    public Task<IReadOnlyList<ProjectSuggestionResult>> GetMemoryProjectsAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        var projects = BuildMemories()
            .Where(memory => !string.Equals(memory.ProjectId, "shared", StringComparison.OrdinalIgnoreCase) &&
                             !string.Equals(memory.ProjectId, "user", StringComparison.OrdinalIgnoreCase))
            .GroupBy(memory => memory.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new ProjectSuggestionResult(group.Key, group.Count()))
            .AsEnumerable();

        if (!string.IsNullOrWhiteSpace(query))
        {
            projects = projects.Where(project => project.ProjectId.Contains(query, StringComparison.OrdinalIgnoreCase));
        }

        return Task.FromResult<IReadOnlyList<ProjectSuggestionResult>>(projects
            .OrderByDescending(project => project.ItemCount)
            .ThenBy(project => project.ProjectId, StringComparer.OrdinalIgnoreCase)
            .Take(Math.Clamp(limit, 1, 20))
            .ToArray());
    }

    public Task<MemoryDetailsResult?> GetMemoryDetailsAsync(Guid id, CancellationToken cancellationToken)
    {
        var document = BuildMemories().FirstOrDefault(memory => memory.Id == id) ?? BuildMemories().FirstOrDefault();
        if (document is null)
        {
            return Task.FromResult<MemoryDetailsResult?>(null);
        }

        var chunkText = Profile == DashboardBrowserTestProfile.Dense
            ? string.Join(Environment.NewLine, Enumerable.Range(1, 8).Select(index => $"Dense chunk line {index}: shared summary, project isolation, and runtime layout verification payload {index}."))
            : "這是一個示範 chunk。";

        return Task.FromResult<MemoryDetailsResult?>(new MemoryDetailsResult(
            document,
            Enumerable.Range(1, Profile == DashboardBrowserTestProfile.Dense ? 5 : 1)
                .Select(index => new MemoryRevisionResult(
                    Guid.NewGuid(),
                    document.Version - (index - 1),
                    $"{document.Title} v{index}",
                    $"{document.Summary} / revision {index}",
                    index == 1 ? "update" : "refresh-shared-summary-layer",
                    DateTimeOffset.UtcNow.AddHours(-index)))
                .ToArray(),
            Enumerable.Range(0, Profile == DashboardBrowserTestProfile.Dense ? 5 : 1)
                .Select(index => new MemoryChunkResult(
                    Guid.NewGuid(),
                    ChunkKind.Document,
                    index,
                    chunkText,
                    "{\"kind\":\"demo\"}",
                    DateTimeOffset.UtcNow.AddHours(-4),
                    [
                        new MemoryVectorResult(
                            Guid.NewGuid(),
                            Profile == DashboardBrowserTestProfile.Dense
                                ? "intfloat/multilingual-e5-large-with-super-long-model-key-for-layout-validation"
                                : "intfloat/multilingual-e5-small",
                            Profile == DashboardBrowserTestProfile.Dense ? 768 : 384,
                            "Active",
                            DateTimeOffset.UtcNow.AddHours(-4))
                    ]))
                .ToArray()));
    }

    public Task<MemoryTransferDownloadResult> ExportMemoriesAsync(MemoryExportRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new MemoryTransferDownloadResult(
            "demo-export.json",
            "application/json",
            Convert.ToBase64String("{}"u8.ToArray()),
            Profile == DashboardBrowserTestProfile.Empty ? 0 : Profile == DashboardBrowserTestProfile.Dense ? 12 : 4,
            !string.IsNullOrWhiteSpace(request.Passphrase)));

    public Task<MemoryImportPreviewResult> PreviewMemoryImportAsync(MemoryImportRequest request, CancellationToken cancellationToken)
    {
        var sample = BuildMemories().FirstOrDefault() ?? CreateMemory(0, "demo-memory", "示範記憶", "示範記憶摘要", false);
        var conflicts = Profile == DashboardBrowserTestProfile.Empty
            ? []
            : Enumerable.Range(1, Profile == DashboardBrowserTestProfile.Dense ? 4 : 1)
                .Select(index => new MemoryImportConflictResult(
                    sample.ProjectId,
                    $"external-key-{index}",
                    sample.Id,
                    $"{sample.Title} existing {index}",
                    $"{sample.Title} incoming {index}",
                    sample.UpdatedAt))
                .ToArray();

        return Task.FromResult(new MemoryImportPreviewResult(
            Profile == DashboardBrowserTestProfile.Dense ? "context-hub-dense-browser-suite" : "test",
            Profile == DashboardBrowserTestProfile.Empty ? 0 : Profile == DashboardBrowserTestProfile.Dense ? 8 : 1,
            Profile == DashboardBrowserTestProfile.Empty ? 0 : 1,
            conflicts.Length,
            false,
            false,
            conflicts));
    }

    public Task<MemoryImportApplyResult> ApplyMemoryImportAsync(MemoryImportRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new MemoryImportApplyResult(
            Profile == DashboardBrowserTestProfile.Empty ? 0 : Profile == DashboardBrowserTestProfile.Dense ? 8 : 1,
            request.ForceOverwrite ? (Profile == DashboardBrowserTestProfile.Empty ? 0 : 2) : 0,
            BuildMemories().Take(Profile == DashboardBrowserTestProfile.Empty ? 0 : 2).Select(memory => memory.Id).ToArray()));

    public Task<IReadOnlyList<UserPreferenceResult>> GetPreferencesAsync(UserPreferenceKind? kind, bool includeArchived, int limit, CancellationToken cancellationToken)
    {
        var items = BuildPreferences();
        if (kind.HasValue)
        {
            items = items.Where(item => item.Kind == kind.Value).ToArray();
        }

        return Task.FromResult<IReadOnlyList<UserPreferenceResult>>(items.Take(limit).ToArray());
    }

    public Task<UserPreferenceResult> UpsertPreferenceAsync(UserPreferenceUpsertRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new UserPreferenceResult(
            Guid.NewGuid(),
            request.Key,
            request.Kind,
            request.Title,
            request.Content,
            request.Rationale,
            request.Tags ?? [],
            0.95m,
            0.95m,
            MemoryStatus.Active,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow));

    public Task<UserPreferenceResult> ArchivePreferenceAsync(Guid id, bool archived, CancellationToken cancellationToken)
        => Task.FromResult(new UserPreferenceResult(
            id,
            "archived-preference",
            UserPreferenceKind.CommunicationStyle,
            "封存測試",
            "封存測試內容",
            "browser tests",
            ["archive"],
            0.8m,
            0.8m,
            archived ? MemoryStatus.Archived : MemoryStatus.Active,
            DateTimeOffset.UtcNow.AddDays(-2),
            DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<LogEntryResult>> SearchLogsAsync(LogQueryRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<LogEntryResult>>(BuildLogs(DateTimeOffset.UtcNow));

    public Task<LogEntryResult?> GetLogAsync(long id, CancellationToken cancellationToken)
        => Task.FromResult<LogEntryResult?>(BuildLogs(DateTimeOffset.UtcNow).FirstOrDefault(log => log.Id == id) ?? BuildLogs(DateTimeOffset.UtcNow).FirstOrDefault());

    public Task<PagedResult<JobListItemResult>> GetJobsAsync(JobListRequest request, CancellationToken cancellationToken)
    {
        var jobs = BuildJobs(DateTimeOffset.UtcNow);
        return Task.FromResult(new PagedResult<JobListItemResult>(jobs, request.Page, request.PageSize, jobs.Count));
    }

    public Task<EnqueueReindexResult> EnqueueReindexAsync(EnqueueReindexRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EnqueueReindexResult(Guid.NewGuid(), MemoryJobStatus.Pending));

    public Task<EnqueueSummaryRefreshResult> EnqueueSummaryRefreshAsync(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EnqueueSummaryRefreshResult(Guid.NewGuid(), MemoryJobStatus.Pending));

    public Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<StorageTableSummaryResult> tables = Profile switch
        {
            DashboardBrowserTestProfile.Empty =>
            [
                new StorageTableSummaryResult("memory_items", "記憶主體與 metadata", 0, ["id", "title", "content", "summary"])
            ],
            DashboardBrowserTestProfile.Dense =>
            [
                new StorageTableSummaryResult("memory_items", "記憶主體與 metadata", 248, ["id", "title", "content", "summary", "project_id", "external_key"]),
                new StorageTableSummaryResult("memory_item_chunks", "向量切塊資料", 998, ["id", "memory_item_id", "chunk_text", "chunk_kind"]),
                new StorageTableSummaryResult("runtime_log_entries", "DB-first runtime logs", 124, ["id", "service_name", "message", "trace_id"]),
                new StorageTableSummaryResult("memory_jobs", "背景工作", 42, ["id", "job_type", "status", "payload_json"]),
                new StorageTableSummaryResult("user_preferences", "偏好設定", 18, ["id", "kind", "title", "content"])
            ],
            _ =>
            [
                new StorageTableSummaryResult("memory_items", "記憶主體與 metadata", 24, ["id", "title", "content", "summary"]),
                new StorageTableSummaryResult("runtime_log_entries", "DB-first runtime logs", 4, ["id", "service_name", "message"])
            ]
        };

        return Task.FromResult(tables);
    }

    public Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
    {
        var rows = Profile switch
        {
            DashboardBrowserTestProfile.Empty => Array.Empty<StorageRowResult>(),
            DashboardBrowserTestProfile.Dense => Enumerable.Range(1, Math.Min(request.PageSize, 18))
                .Select(index => new StorageRowResult(new Dictionary<string, string?>
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["title"] = $"Dense storage row title {index} with a deliberately long payload for responsive verification",
                    ["content"] = $"Dense storage row content {index} | project=context-hub-shared-knowledge-layer | request-id=req-{index:0000} | trace-id=trace-{index:0000}",
                    ["summary"] = $"Summary {index}: validates table shell, sticky header, and row inspector layout under high density."
                }))
                .ToArray(),
            _ => new[]
            {
                new StorageRowResult(new Dictionary<string, string?>
                {
                    ["id"] = Guid.NewGuid().ToString(),
                    ["title"] = "示範記憶",
                    ["content"] = "這是一筆提供給 browser UI 測試的 storage row 內容。",
                    ["summary"] = "示範記憶摘要"
                })
            }
        };

        return Task.FromResult(new StorageTableRowsResult(
            request.Table,
            request.Table == "memory_item_chunks" ? "向量切塊資料" : "記憶主體與 metadata",
            ["id", "title", "content", "summary"],
            ["title", "content", "summary"],
            request.Query,
            request.Column,
            new PagedResult<StorageRowResult>(rows, request.Page, request.PageSize, rows.Length)));
    }

    public Task<PerformanceMeasureResult> MeasurePerformanceAsync(PerformanceMeasureRequest request, CancellationToken cancellationToken)
    {
        var multiplier = Profile == DashboardBrowserTestProfile.Dense ? 4 : 1;
        return Task.FromResult(new PerformanceMeasureResult(
            "Http",
            Profile == DashboardBrowserTestProfile.Dense ? "dense" : "compact",
            Profile == DashboardBrowserTestProfile.Dense
                ? "intfloat/multilingual-e5-large-with-super-long-model-key-for-layout-validation"
                : "intfloat/multilingual-e5-small",
            Profile == DashboardBrowserTestProfile.Dense ? 768 : 384,
            request.SearchLimit,
            request.IncludeArchived,
            request.WarmupIterations,
            request.MeasurementIterations,
            2 * multiplier,
            42 * multiplier,
            1 * multiplier,
            1 * multiplier,
            1 * multiplier,
            request.MeasurementMode,
            request.MeasurementDurationSeconds,
            request.MaxMeasurementIterations,
            request.MeasurementMode == PerformanceMeasurementMode.Duration
                ? request.MeasurementDurationSeconds * 1000
                : request.MeasurementIterations * 6 * multiplier,
            new PerformanceMetricResult("ms", request.MeasurementIterations, 1 * multiplier, 1, 1, 1, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 2 * multiplier, 2, 2, 2, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 3 * multiplier, 3, 3, 3, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 4 * multiplier, 4, 4, 4, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 5 * multiplier, 5, 5, 5, 1),
            new PerformanceMetricResult("ms", request.MeasurementIterations, 6 * multiplier, 6, 6, 6, 1),
            DateTimeOffset.UtcNow));
    }

    private IReadOnlyList<DashboardServiceHealthResult> BuildServices()
        => Profile switch
        {
            DashboardBrowserTestProfile.Dense =>
            [
                new DashboardServiceHealthResult("postgres", "Healthy", ""),
                new DashboardServiceHealthResult("redis", "Healthy", ""),
                new DashboardServiceHealthResult("embeddings", "Healthy", ""),
                new DashboardServiceHealthResult("dashboard", "Healthy", "")
            ],
            _ =>
            [
                new DashboardServiceHealthResult("postgres", "Healthy", ""),
                new DashboardServiceHealthResult("redis", "Healthy", ""),
                new DashboardServiceHealthResult("embeddings", "Healthy", "")
            ]
        };

    private IReadOnlyList<DashboardOverviewMetricResult> BuildMetrics()
        => Profile switch
        {
            DashboardBrowserTestProfile.Empty =>
            [
                new DashboardOverviewMetricResult("memoryItems", "全部記憶條目", 0, "items"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", 0, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", 0, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", 0, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "錯誤日誌", 0, "logs")
            ],
            DashboardBrowserTestProfile.Dense =>
            [
                new DashboardOverviewMetricResult("memoryItems", "全部記憶條目", 1248, "items"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", 314, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", 18, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", 7, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "錯誤日誌", 24, "logs")
            ],
            _ =>
            [
                new DashboardOverviewMetricResult("memoryItems", "全部記憶條目", 24, "items"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", 4, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", 3, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", 1, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "錯誤日誌", 2, "logs")
            ]
        };

    private IReadOnlyList<RequestTrafficSampleResult> BuildTrafficSamples()
    {
        var count = Profile == DashboardBrowserTestProfile.Dense ? 24 : 15;
        return Enumerable.Range(0, count)
            .Select(index => new RequestTrafficSampleResult(
                DateTimeOffset.UtcNow.AddSeconds(index - count),
                Profile == DashboardBrowserTestProfile.Empty ? 0 : (index % 5) + 1,
                Profile == DashboardBrowserTestProfile.Empty ? 0 : (index % 4) + 1))
            .ToArray();
    }

    private IReadOnlyList<JobListItemResult> BuildJobs(DateTimeOffset now)
    {
        if (Profile == DashboardBrowserTestProfile.Empty)
        {
            return [];
        }

        var count = Profile == DashboardBrowserTestProfile.Dense ? 8 : 1;
        return Enumerable.Range(1, count)
            .Select(index => new JobListItemResult(
                Guid.NewGuid(),
                index % 2 == 0 ? MemoryJobType.RefreshSummary : MemoryJobType.Reindex,
                index == 1 ? MemoryJobStatus.Running : MemoryJobStatus.Pending,
                Profile == DashboardBrowserTestProfile.Dense
                    ? $"{{\"modelKey\":\"intfloat/multilingual-e5-large\",\"projectId\":\"proj-{index:000}\",\"notes\":\"dense-job-payload-for-browser-layout-validation-{index}\"}}"
                    : "{\"modelKey\":\"intfloat/multilingual-e5-small\"}",
                index % 3 == 0 ? "Transient warning" : string.Empty,
                now.AddMinutes(-5 - index),
                now.AddMinutes(-4 - index),
                null))
            .ToArray();
    }

    private IReadOnlyList<LogEntryResult> BuildLogs(DateTimeOffset now)
    {
        if (Profile == DashboardBrowserTestProfile.Empty)
        {
            return [];
        }

        var count = Profile == DashboardBrowserTestProfile.Dense ? 10 : 1;
        return Enumerable.Range(1, count)
            .Select(index => new LogEntryResult(
                index,
                "mcp-server",
                "BrowserTests.Dashboard",
                index % 2 == 0 ? "Warning" : "Error",
                Profile == DashboardBrowserTestProfile.Dense
                    ? $"示範 log {index}: shared summary layer refresh, project isolation, and RWD validation payload with long trace labels."
                    : "示範 log",
                Profile == DashboardBrowserTestProfile.Dense
                    ? $"System.InvalidOperationException: Dense browser validation exception #{index}{Environment.NewLine}at Dashboard.ValidateLayout(){Environment.NewLine}at Dashboard.RenderAsync()"
                    : "System.Exception: demo",
                $"trace-{index:0000}-with-a-very-long-correlation-id",
                $"request-{index:0000}-with-a-very-long-request-id",
                $"{{\"kind\":\"demo\",\"index\":{index},\"component\":\"dashboard-browser-tests\",\"notes\":\"layout-validation-{index}\"}}",
                now.AddMinutes(-2 - index)))
            .ToArray();
    }

    private IReadOnlyList<DashboardRuntimeParameterResult> BuildRuntimeParameters()
        => Profile == DashboardBrowserTestProfile.Dense
            ? [
                new DashboardRuntimeParameterResult("Embeddings", "Profile", "dense", false),
                new DashboardRuntimeParameterResult("Embeddings", "Dimensions", "768", false),
                new DashboardRuntimeParameterResult("Embeddings", "Execution Provider", "CPUExecutionProvider", false),
                new DashboardRuntimeParameterResult("Embeddings", "Batch Size", "16", false),
                new DashboardRuntimeParameterResult("Embeddings", "Batching Enabled", "true", false),
                new DashboardRuntimeParameterResult("Dashboard", "Polling Overview Seconds", "10", false),
                new DashboardRuntimeParameterResult("Dashboard", "Compose Project", "contexthub-dense-browser-suite", false)
            ]
            : [
                new DashboardRuntimeParameterResult("Embeddings", "Profile", "compact", false),
                new DashboardRuntimeParameterResult("Embeddings", "Dimensions", "384", false),
                new DashboardRuntimeParameterResult("Embeddings", "Execution Provider", "CPUExecutionProvider", false),
                new DashboardRuntimeParameterResult("Embeddings", "Batch Size", "8", false),
                new DashboardRuntimeParameterResult("Embeddings", "Batching Enabled", "true", false)
            ];

    private DashboardPageSnapshotStatusResult BuildPageSnapshotStatus(DateTimeOffset snapshotAtUtc, bool isStale)
    {
        var warning = isStale ? "Snapshot is stale in browser test profile." : string.Empty;
        return new DashboardPageSnapshotStatusResult(
            snapshotAtUtc,
            isStale,
            warning,
            [
                new DashboardSnapshotSectionStatusResult(
                    "statusCore",
                    "核心狀態",
                    snapshotAtUtc,
                    Profile == DashboardBrowserTestProfile.Dense ? 1 : 3,
                    isStale,
                    string.Empty,
                    warning),
                new DashboardSnapshotSectionStatusResult(
                    "dependencyResources",
                    "Compose 服務資源",
                    snapshotAtUtc,
                    5,
                    isStale,
                    string.Empty,
                    warning)
            ]);
    }

    private DashboardDockerHostResult BuildDockerHost(DateTimeOffset capturedAtUtc)
        => new(
            "Healthy",
            string.Empty,
            new Memory.Application.DockerHostSummaryResult(
                Profile == DashboardBrowserTestProfile.Dense ? "dense-browser-host" : "browser-host",
                "28.1.1",
                "Docker Desktop",
                "linux",
                Profile == DashboardBrowserTestProfile.Dense ? 12 : 8,
                (Profile == DashboardBrowserTestProfile.Dense ? 16L : 8L) * 1024 * 1024 * 1024,
                (Profile == DashboardBrowserTestProfile.Dense ? 9L : 5L) * 1024 * 1024 * 1024,
                4,
                6,
                3,
                capturedAtUtc));

    private DashboardDependencyResourcesResult BuildDependencyResources()
        => new(
            "Healthy",
            string.Empty,
            [
                new Memory.Application.DockerContainerMetricResult("contexthub-postgres-1", "postgres", "pgvector/pgvector:pg17", "running", "healthy", 0, 0.8, 1536L * 1024 * 1024, 4096L * 1024 * 1024, 24_000, 22_000, 18_000, 12_000),
                new Memory.Application.DockerContainerMetricResult("contexthub-redis-1", "redis", "redis:7.4-alpine", "running", "healthy", 1, 0.3, 192L * 1024 * 1024, 1024L * 1024 * 1024, 9_000, 8_500, 1_200, 900),
                new Memory.Application.DockerContainerMetricResult("contexthub-embedding-service-1", "embedding-service", "context-hub/embedding-service:local", "running", "healthy", 0, 3.2, 1024L * 1024 * 1024, 4096L * 1024 * 1024, 15_000, 13_500, 6_000, 4_800),
                new Memory.Application.DockerContainerMetricResult("contexthub-mcp-server-1", "mcp-server", "context-hub/mcp", "running", "healthy", 0, 1.2, 512L * 1024 * 1024, 1024L * 1024 * 1024, 12_000, 16_000, 4_000, 3_500)
            ],
            [
                new Memory.Application.DockerVolumeSummaryResult("contexthub_postgres-data", "local", 1024L * 1024 * 1024, "/var/lib/docker/volumes/contexthub_postgres-data"),
                new Memory.Application.DockerVolumeSummaryResult("contexthub_redis-data", "local", 256L * 1024 * 1024, "/var/lib/docker/volumes/contexthub_redis-data"),
                new Memory.Application.DockerVolumeSummaryResult("contexthub_embedding-model", "local", 768L * 1024 * 1024, "/var/lib/docker/volumes/contexthub_embedding-model")
            ]);

    private DashboardRedisTelemetryResult BuildRedisTelemetry()
        => new(
            "Healthy",
            string.Empty,
            196L * 1024 * 1024,
            256L * 1024 * 1024,
            Profile == DashboardBrowserTestProfile.Dense ? 420 : 96,
            Profile == DashboardBrowserTestProfile.Dense ? 320_000 : 42_000,
            Profile == DashboardBrowserTestProfile.Dense ? 128L * 1024 * 1024 : 16L * 1024 * 1024,
            Profile == DashboardBrowserTestProfile.Dense ? 118L * 1024 * 1024 : 14L * 1024 * 1024,
            Profile == DashboardBrowserTestProfile.Dense ? 32.4 : 8.6,
            Profile == DashboardBrowserTestProfile.Dense ? 28.1 : 7.4,
            24,
            0,
            9_000,
            8_500,
            1_200,
            900,
            256L * 1024 * 1024,
            "contexthub_redis-data");

    private DashboardPostgresTelemetryResult BuildPostgresTelemetry()
        => new(
            "Healthy",
            string.Empty,
            Profile == DashboardBrowserTestProfile.Dense ? 14 : 4,
            Profile == DashboardBrowserTestProfile.Dense ? 1_240_000 : 42_000,
            Profile == DashboardBrowserTestProfile.Dense ? 48 : 2,
            Profile == DashboardBrowserTestProfile.Dense ? 320_000 : 24_000,
            Profile == DashboardBrowserTestProfile.Dense ? 8_200_000 : 420_000,
            Profile == DashboardBrowserTestProfile.Dense ? 4_800_000 : 180_000,
            Profile == DashboardBrowserTestProfile.Dense ? 240_000 : 24_000,
            Profile == DashboardBrowserTestProfile.Dense ? 18_000 : 640,
            Profile == DashboardBrowserTestProfile.Dense ? 12_000 : 320,
            Profile == DashboardBrowserTestProfile.Dense ? 1_200 : 42,
            Profile == DashboardBrowserTestProfile.Dense ? 768L * 1024 * 1024 : 42L * 1024 * 1024,
            0,
            24_000,
            22_000,
            18_000,
            12_000,
            0,
            1024L * 1024 * 1024,
            "contexthub_postgres-data",
            Profile == DashboardBrowserTestProfile.Dense ? 640L * 1024 * 1024 : 96L * 1024 * 1024);

    private IReadOnlyList<DashboardResourceSampleResult> BuildResourceSamples(IReadOnlyList<RequestTrafficSampleResult> trafficSamples)
        => trafficSamples
            .Select((sample, index) => new DashboardResourceSampleResult(
                sample.TimestampUtc,
                20 + (index % 5 * 7),
                30 + (index % 4 * 8),
                (512L + (index * 48L)) * 1024 * 1024,
                30_000 + (index * 1_200),
                26_000 + (index * 1_000),
                8_000 + (index * 350),
                7_000 + (index * 320),
                sample.InboundRequests,
                sample.OutboundRequests))
            .ToArray();

    private IReadOnlyList<MemoryDocument> BuildMemories()
    {
        if (Profile == DashboardBrowserTestProfile.Empty)
        {
            return [];
        }

        var count = Profile == DashboardBrowserTestProfile.Dense ? 12 : 1;
        return Enumerable.Range(0, count)
            .Select(index => CreateMemory(
                index,
                index == 0 ? "demo-memory" : $"dense-memory-{index:00}",
                index == 0 ? "示範記憶" : $"Dense Memory Item {index:00} With Long Title For Responsive Layout Validation",
                index == 0 ? "示範記憶摘要" : $"Dense summary {index:00} validating project isolation, cross-project reads, shared summary layer, and long metadata wrapping.",
                Profile == DashboardBrowserTestProfile.Dense && index % 4 == 0))
            .ToArray();
    }

    private MemoryDocument CreateMemory(int index, string externalKey, string title, string summary, bool readOnly)
    {
        var content = Profile == DashboardBrowserTestProfile.Dense
            ? string.Join(Environment.NewLine, Enumerable.Range(1, 10).Select(line => $"Dense memory {index:00} line {line}: validates long body content, scroll containers, and non-overlapping detail panels."))
            : "這是一筆提供給 dashboard browser 測試的示範記憶內容。";

        return new MemoryDocument(
            Guid.NewGuid(),
            externalKey,
            MemoryScope.Project,
            index % 3 == 0 ? MemoryType.Decision : index % 2 == 0 ? MemoryType.Fact : MemoryType.Artifact,
            title,
            content,
            summary,
            "document",
            Profile == DashboardBrowserTestProfile.Dense ? $"repo://context-hub/dense/layout-validation/{index:00}" : "tests",
            Profile == DashboardBrowserTestProfile.Dense ? ["demo", "dashboard", "layout", $"project-{index:00}", "shared-summary-layer"] : ["demo", "dashboard"],
            0.8m,
            0.9m,
            Profile == DashboardBrowserTestProfile.Dense ? 4 : 2,
            MemoryStatus.Active,
            "{\"kind\":\"demo\"}",
            DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-index),
            DateTimeOffset.UtcNow.AddMinutes(-index),
            readOnly ? "shared" : index % 2 == 0 ? ProjectContext.DefaultProjectId : $"project-{index:00}",
            readOnly);
    }

    private IReadOnlyList<UserPreferenceResult> BuildPreferences()
    {
        if (Profile == DashboardBrowserTestProfile.Empty)
        {
            return [];
        }

        if (Profile == DashboardBrowserTestProfile.Dense)
        {
            return Enumerable.Range(1, 8)
                .Select(index => new UserPreferenceResult(
                    Guid.NewGuid(),
                    $"preference-key-{index:00}",
                    (UserPreferenceKind)(index % Enum.GetValues<UserPreferenceKind>().Length),
                    $"Dense preference title {index:00}",
                    $"Dense preference content {index:00}: 使用繁體中文、保持 production-ready、關注長期維護與跨專案知識整理。",
                    $"Dense rationale {index:00}: validates layout wrapping, tag rendering, and actions alignment.",
                    ["language", "style", $"tag-{index:00}", "long-layout-validation"],
                    0.95m,
                    0.92m,
                    MemoryStatus.Active,
                    DateTimeOffset.UtcNow.AddDays(-index),
                    DateTimeOffset.UtcNow.AddHours(-index)))
                .ToArray();
        }

        return
        [
            new UserPreferenceResult(
                Guid.Parse("7f930e28-5bf3-4e1d-b851-ae9d28c3cc2f"),
                "preferred-language",
                UserPreferenceKind.CommunicationStyle,
                "偏好繁體中文",
                "回覆預設使用繁體中文。",
                "長期偏好",
                ["language", "style"],
                0.95m,
                0.95m,
                MemoryStatus.Active,
                DateTimeOffset.UtcNow.AddDays(-3),
                DateTimeOffset.UtcNow.AddHours(-5))
        ];
    }
}

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

    private static IReadOnlyList<MemoryGraphEdgeResult> GraphDemoPrecomputedEdges { get; } = BuildGraphDemoPrecomputedEdges();

    private DashboardContextSavingsResult BuildContextSavings(DateTimeOffset now)
    {
        if (Profile == DashboardBrowserTestProfile.Empty)
        {
            return new DashboardContextSavingsResult(
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
        }

        var baseline = Profile == DashboardBrowserTestProfile.Dense ? 186_420 : 48_250;
        var returned = Profile == DashboardBrowserTestProfile.Dense ? 34_780 : 9_640;
        var saved = baseline - returned;
        var sampleCount = Profile == DashboardBrowserTestProfile.Dense ? 42 : 16;
        var trend = Enumerable.Range(0, 18)
            .Select(index =>
            {
                var pointBaseline = (baseline / 18) + (index * 64);
                var pointReturned = Math.Max(900, (returned / 18) - (index * 8));
                var pointSaved = Math.Max(0, pointBaseline - pointReturned);
                var savingPercent = pointBaseline > 0 ? pointSaved / (double)pointBaseline * 100d : 0d;
                return new DashboardContextSavingsTrendPointResult(
                    now.AddMinutes(-85 + (index * 5)),
                    pointBaseline,
                    pointReturned,
                    pointSaved,
                    Math.Round(savingPercent, 2));
            })
            .ToArray();

        return new DashboardContextSavingsResult(
            true,
            sampleCount,
            baseline,
            returned,
            saved,
            Math.Round(saved / (double)baseline * 100d, 2),
            ContextSavingsEstimator.HighConfidence,
            Profile == DashboardBrowserTestProfile.Dense ? 92.3d : 86.8d,
            Profile == DashboardBrowserTestProfile.Dense ? 61.9d : 43.8d,
            now.AddHours(-24),
            now,
            trend);
    }

    private static DashboardEvaluationSummaryResult BuildEvaluationSummary()
        => new(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001"),
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001"),
            "browser-test-context",
            EvaluationRunStatus.Completed,
            0.833m,
            0.786m,
            0.72m,
            41.8d,
            DateTimeOffset.UtcNow.AddMinutes(-20),
            DateTimeOffset.UtcNow.AddMinutes(-18));

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
            BuildResourceSamples(trafficSamples),
            BuildEvaluationSummary(),
            BuildContextSavings(now)));
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

    public Task<IReadOnlyList<ConversationCheckpointSearchResult>> SearchConversationCheckpointsAsync(ConversationCheckpointSearchRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ConversationCheckpointSearchResult>>([]);

    public Task<MemoryGraphResult> GetMemoryGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken)
    {
        var candidates = FilterGraphMemories(request, BuildMemories()).ToArray();
        if (candidates.Length == 0)
        {
            return Task.FromResult(new MemoryGraphResult([], [], new MemoryGraphStatsResult(0, 0, 0, false)));
        }

        var maxNodes = Math.Clamp(request.MaxNodes, 1, 120);
        var ordered = !string.IsNullOrWhiteSpace(request.Query)
            ? candidates
                .OrderByDescending(memory => memory.Title.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ? 3 : 0)
                .ThenByDescending(memory => memory.Summary.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ? 2 : 0)
                .ThenByDescending(memory => memory.Importance)
                .ThenByDescending(memory => memory.UpdatedAt)
                .ToArray()
            : candidates
                .OrderByDescending(memory => memory.Importance)
                .ThenByDescending(memory => memory.UpdatedAt)
                .ToArray();
        var nodes = ordered.Take(maxNodes).ToArray();
        var seedCount = request.GraphMode == MemoryGraphMode.ProjectFull ? 0 : Math.Min(nodes.Length, Profile == DashboardBrowserTestProfile.Dense ? 2 : 1);
        var seedIds = nodes.Take(seedCount).Select(node => node.Id).ToArray();
        var edges = BuildGraphEdges(nodes, seedIds, request.IncludeSimilarity).ToArray();
        var explicitCounts = BuildEdgeCounts(edges, "explicit");
        var similarityCounts = BuildEdgeCounts(edges, "similar");

        return Task.FromResult(new MemoryGraphResult(
            nodes.Select(node => new MemoryGraphNodeResult(
                    node.Id,
                    node.Title,
                    node.Summary,
                    node.ProjectId,
                    node.MemoryType,
                    node.Scope,
                    node.Status,
                    node.Tags,
                    node.SourceType,
                    node.SourceRef,
                    node.UpdatedAt,
                    node.Importance,
                    node.Confidence,
                    node.IsReadOnly,
                    null,
                    "https://example.com/favicon.ico",
                    node.SourceType,
                    explicitCounts.GetValueOrDefault(node.Id),
                    similarityCounts.GetValueOrDefault(node.Id)))
                .ToArray(),
            edges,
            new MemoryGraphStatsResult(
                seedCount,
                nodes.Length,
                edges.Length,
                candidates.Length > nodes.Length,
                candidates.Length > nodes.Length ? $"Graph capped at {maxNodes} nodes. Add filters to reduce the browser-test dataset." : null)));
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
                .ToArray(),
            [
                new MemoryLinkResult(
                    Guid.Parse("b1000000-0000-0000-0000-000000000001"),
                    document.Id,
                    BuildMemories().FirstOrDefault(memory => memory.Id != document.Id)?.Id ?? document.Id,
                    "related",
                    DateTimeOffset.UtcNow.AddHours(-6))
            ],
            null,
            new MemorySourceContextResult(
                Guid.Parse("a1000000-0000-0000-0000-000000000001"),
                "Browser Test Source",
                "cursor-demo",
                "v1",
                "https://example.com/docs/context-hub",
                DateTimeOffset.UtcNow.AddMinutes(-45),
                DateTimeOffset.UtcNow.AddMinutes(-30),
                ["browser-tests", "graph-page"])));
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

    public Task<IReadOnlyList<SourceConnectionResult>> GetSourcesAsync(SourceListRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SourceConnectionResult>>(
            Profile == DashboardBrowserTestProfile.Empty
                ? []
                :
                [
                    new SourceConnectionResult(Guid.NewGuid(), request.ProjectId, "Local Repo", SourceKind.LocalRepo, true, """{"rootPath":"W:/Repositories/WJCY/ContextHub"}""", false, string.Empty, DateTimeOffset.UtcNow.AddMinutes(-10), DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow)
                ]);

    public Task<SourceConnectionResult> CreateSourceAsync(SourceConnectionCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SourceConnectionResult(Guid.NewGuid(), request.ProjectId, request.Name, request.SourceKind, request.Enabled, request.ConfigJson, !string.IsNullOrWhiteSpace(request.SecretJson), string.Empty, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<SourceConnectionResult> UpdateSourceAsync(SourceConnectionUpdateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new SourceConnectionResult(request.Id, request.ProjectId ?? ProjectContext.DefaultProjectId, request.Name ?? "Updated Source", SourceKind.LocalRepo, request.Enabled ?? true, request.ConfigJson ?? "{}", !string.IsNullOrWhiteSpace(request.SecretJson), string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow));

    public Task<EnqueueSourceSyncResult> SyncSourceAsync(Guid id, SourceSyncRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EnqueueSourceSyncResult(Guid.NewGuid(), MemoryJobStatus.Pending));

    public Task<IReadOnlyList<SourceSyncRunResult>> GetSourceRunsAsync(Guid id, string? projectId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SourceSyncRunResult>>(
        [
            new SourceSyncRunResult(Guid.NewGuid(), id, projectId ?? ProjectContext.DefaultProjectId, SourceSyncTrigger.Manual, SourceSyncStatus.Completed, 8, 4, 1, 0, "before", "after", string.Empty, DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddMinutes(-4))
        ]);

    public Task<IReadOnlyList<GovernanceFindingResult>> GetGovernanceFindingsAsync(GovernanceFindingListRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<GovernanceFindingResult>>(
            Profile == DashboardBrowserTestProfile.Empty
                ? []
                :
                [
                    new GovernanceFindingResult(Guid.NewGuid(), request.ProjectId, null, Guid.NewGuid(), null, GovernanceFindingType.ReindexRequired, GovernanceFindingStatus.Open, "需要重新索引：示範記憶", "目前向量資料未對齊。", "{}", "demo", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)
                ]);

    public Task<GovernanceAnalyzeResult> AnalyzeGovernanceAsync(GovernanceAnalyzeRequest request, CancellationToken cancellationToken)
        => Task.FromResult(
            Profile == DashboardBrowserTestProfile.Empty
                ? new GovernanceAnalyzeResult(request.ProjectId, 0, 0, DateTimeOffset.UtcNow)
                : new GovernanceAnalyzeResult(request.ProjectId, 1, 1, DateTimeOffset.UtcNow));

    public Task<GovernanceFindingResult> AcceptGovernanceFindingAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(new GovernanceFindingResult(id, ProjectContext.DefaultProjectId, null, Guid.NewGuid(), null, GovernanceFindingType.ReindexRequired, GovernanceFindingStatus.Accepted, "接受 finding", "accepted", "{}", "demo", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    public Task<GovernanceFindingResult> DismissGovernanceFindingAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(new GovernanceFindingResult(id, ProjectContext.DefaultProjectId, null, Guid.NewGuid(), null, GovernanceFindingType.ReindexRequired, GovernanceFindingStatus.Dismissed, "忽略 finding", "dismissed", "{}", "demo", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<EvaluationSuiteResult>> GetEvaluationSuitesAsync(string projectId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<EvaluationSuiteResult>>(
            Profile == DashboardBrowserTestProfile.Empty
                ? []
                :
                [
                    new EvaluationSuiteResult(Guid.NewGuid(), projectId, "Browser Test Suite", "Demo suite", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow, [new EvaluationCaseResult(Guid.NewGuid(), Guid.NewGuid(), projectId, "Scenario", "demo query", [], ["demo-memory"], DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow)])
                ]);

    public Task<EvaluationSuiteResult> CreateEvaluationSuiteAsync(EvaluationSuiteCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EvaluationSuiteResult(Guid.NewGuid(), request.ProjectId, request.Name, request.Description, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

    public Task<EvaluationRunResult> RunEvaluationAsync(EvaluationRunRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new EvaluationRunResult(Guid.NewGuid(), request.SuiteId, ProjectContext.DefaultProjectId, EvaluationRunStatus.Completed, "compact", request.QueryMode, request.UseSummaryLayer, request.TopK, 1m, 1m, 1m, 12.5d, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

    public Task<EvaluationRunResult?> GetEvaluationRunAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult<EvaluationRunResult?>(new EvaluationRunResult(id, Guid.NewGuid(), ProjectContext.DefaultProjectId, EvaluationRunStatus.Completed, "compact", MemoryQueryMode.CurrentOnly, false, 5, 1m, 1m, 1m, 10d, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

    public Task<IReadOnlyList<SuggestedActionResult>> GetSuggestedActionsAsync(SuggestedActionListRequest request, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<SuggestedActionResult>>(
            Profile == DashboardBrowserTestProfile.Empty
                ? []
                :
                [
                    new SuggestedActionResult(Guid.NewGuid(), request.ProjectId, SuggestedActionType.ReindexProject, SuggestedActionStatus.Pending, "重新索引專案", "評測品質回退。", "{}", string.Empty, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null)
                ]);

    public Task<SuggestedActionMutationResult> AcceptSuggestedActionAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(new SuggestedActionMutationResult(new SuggestedActionResult(id, ProjectContext.DefaultProjectId, SuggestedActionType.ReindexProject, SuggestedActionStatus.Executed, "重新索引專案", "已執行。", "{}", string.Empty, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow), Guid.NewGuid()));

    public Task<SuggestedActionResult> DismissSuggestedActionAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult(new SuggestedActionResult(id, ProjectContext.DefaultProjectId, SuggestedActionType.ReindexProject, SuggestedActionStatus.Dismissed, "重新索引專案", "已忽略。", "{}", string.Empty, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow, null));

    public Task<IReadOnlyList<TenantResult>> GetTenantsAsync(bool includeArchived, int limit, CancellationToken cancellationToken)
    {
        TenantResult[] tenants = Profile == DashboardBrowserTestProfile.Empty
            ? []
            : [DemoTenant()];

        return Task.FromResult<IReadOnlyList<TenantResult>>(tenants.Take(limit).ToArray());
    }

    public Task<TenantResult> CreateTenantAsync(TenantCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new TenantResult(Guid.NewGuid(), request.Slug, request.DisplayName, TenantStatus.Active, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<TenantUserResult>> GetTenantUsersAsync(Guid tenantId, bool includeArchived, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TenantUserResult>>(
            Profile == DashboardBrowserTestProfile.Empty
                ? []
                :
                [
                    DemoUser(tenantId),
                    new TenantUserResult(Guid.Parse("73000000-0000-0000-0000-000000000002"), tenantId, "automation", "Automation Runner", "automation@example.com", TenantUserRole.Member, TenantUserStatus.Active, DateTimeOffset.UtcNow.AddHours(-6), DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddHours(-6))
                ]);

    public Task<TenantUserResult> CreateTenantUserAsync(TenantUserCreateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new TenantUserResult(Guid.NewGuid(), request.TenantId, request.Username, request.DisplayName, request.Email, request.Role, TenantUserStatus.Active, null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<TenantProjectGrantResult>> GetTenantProjectGrantsAsync(Guid tenantId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<TenantProjectGrantResult>>(
        [
            new TenantProjectGrantResult(Guid.Parse("74000000-0000-0000-0000-000000000001"), tenantId, "ContextHub", true, true, true, DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddHours(-1))
        ]);

    public Task<TenantProjectGrantResult> UpsertTenantProjectGrantAsync(TenantProjectGrantUpsertRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new TenantProjectGrantResult(Guid.NewGuid(), request.TenantId, request.ProjectId, request.CanRead, request.CanWrite, request.CanManageTokens, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));

    public Task<IReadOnlyList<ApiTokenResult>> GetApiTokensAsync(Guid tenantId, bool includeRevoked, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ApiTokenResult>>(
            Profile == DashboardBrowserTestProfile.Empty
                ? []
                :
                [
                    new ApiTokenResult(Guid.Parse("75000000-0000-0000-0000-000000000001"), tenantId, DemoUser(tenantId).Id, "Codex MCP", "外部 Codex 連線使用", "ctxh_9f7a1c", "4A2F", ["memory:read", "memory:write"], ["ContextHub"], null, null, DateTimeOffset.UtcNow.AddMinutes(-12), "203.0.113.42", "codex-mcp-client/1.0", DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddMinutes(-12))
                ]);

    public Task<ApiTokenCreatedResult> CreateApiTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken)
    {
        var token = new ApiTokenResult(Guid.NewGuid(), request.TenantId, request.OwnerUserId, request.Name, request.Notes ?? string.Empty, "ctxh_demo", "9F0A", request.Scopes ?? ["memory:read"], request.AllowedProjectIds ?? [], request.ExpiresAt, null, null, string.Empty, string.Empty, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
        return Task.FromResult(new ApiTokenCreatedResult(token, "ctxh_demo_plain_token_9F0A"));
    }

    public Task<ApiTokenResult> UpdateApiTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken)
        => Task.FromResult(new ApiTokenResult(tokenId, DemoTenant().Id, DemoUser(DemoTenant().Id).Id, request.Name ?? "Updated Token", request.Notes ?? string.Empty, "ctxh_demo", "9F0A", request.Scopes ?? ["memory:read"], request.AllowedProjectIds ?? [], request.ExpiresAt, null, null, string.Empty, string.Empty, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    public Task<ApiTokenResult> RevokeApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
        => Task.FromResult(new ApiTokenResult(tokenId, DemoTenant().Id, DemoUser(DemoTenant().Id).Id, "Revoked Token", "已撤銷", "ctxh_demo", "9F0A", ["memory:read"], ["ContextHub"], null, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddMinutes(-12), "203.0.113.42", "codex-mcp-client/1.0", DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow));

    public Task<ApiTokenCreatedResult> RegenerateApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        var token = new ApiTokenResult(tokenId, DemoTenant().Id, DemoUser(DemoTenant().Id).Id, "Regenerated Token", string.Empty, "ctxh_new", "1A2B", ["memory:read"], ["ContextHub"], null, null, null, string.Empty, string.Empty, DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow);
        return Task.FromResult(new ApiTokenCreatedResult(token, "ctxh_new_plain_token_1A2B"));
    }

    public Task<IReadOnlyList<SecurityAuditEventResult>> GetSecurityAuditEventsAsync(Guid? tenantId, int limit, CancellationToken cancellationToken)
    {
        SecurityAuditEventResult[] events = Profile == DashboardBrowserTestProfile.Empty
            ? []
            :
            [
                new SecurityAuditEventResult(Guid.NewGuid(), tenantId ?? DemoTenant().Id, DemoUser(tenantId ?? DemoTenant().Id).Id, Guid.Parse("75000000-0000-0000-0000-000000000001"), SecurityAuditEventType.ApiTokenAuthenticated, "Succeeded", "203.0.113.42", "codex-mcp-client/1.0", """{"name":"Codex MCP"}""", DateTimeOffset.UtcNow.AddMinutes(-12)),
                new SecurityAuditEventResult(Guid.NewGuid(), tenantId ?? DemoTenant().Id, DemoUser(tenantId ?? DemoTenant().Id).Id, null, SecurityAuditEventType.ProjectGrantUpserted, "Succeeded", string.Empty, string.Empty, """{"projectId":"ContextHub"}""", DateTimeOffset.UtcNow.AddHours(-2))
            ];

        return Task.FromResult<IReadOnlyList<SecurityAuditEventResult>>(events.Take(limit).ToArray());
    }

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

    private static TenantResult DemoTenant()
        => new(Guid.Parse("72000000-0000-0000-0000-000000000001"), "context-team", "Context Team", TenantStatus.Active, DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddHours(-1));

    private static TenantUserResult DemoUser(Guid tenantId)
        => new(Guid.Parse("73000000-0000-0000-0000-000000000001"), tenantId, "admin", "Admin User", "admin@example.com", TenantUserRole.Owner, TenantUserStatus.Active, DateTimeOffset.UtcNow.AddMinutes(-12), DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddDays(-8), DateTimeOffset.UtcNow.AddHours(-1));

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
                new DashboardOverviewMetricResult("memoryItems", "記憶條目", 0, "items"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", 0, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", 0, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", 0, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "錯誤日誌", 0, "logs")
            ],
            DashboardBrowserTestProfile.Dense =>
            [
                new DashboardOverviewMetricResult("memoryItems", "記憶條目", 1248, "items"),
                new DashboardOverviewMetricResult("defaultProjectMemoryItems", "預設專案記憶", 314, "items"),
                new DashboardOverviewMetricResult("userPreferences", "使用者偏好", 18, "items"),
                new DashboardOverviewMetricResult("activeJobs", "背景工作", 7, "jobs"),
                new DashboardOverviewMetricResult("errorLogs", "錯誤日誌", 24, "logs")
            ],
            _ =>
            [
                new DashboardOverviewMetricResult("memoryItems", "記憶條目", 24, "items"),
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
        var effectiveSnapshotAtUtc = isStale ? snapshotAtUtc.AddSeconds(-12) : snapshotAtUtc;
        var warning = isStale ? "Snapshot is stale in browser test profile." : string.Empty;
        return new DashboardPageSnapshotStatusResult(
            effectiveSnapshotAtUtc,
            isStale,
            warning,
            [
                new DashboardSnapshotSectionStatusResult(
                    "statusCore",
                    "核心狀態",
                    effectiveSnapshotAtUtc,
                    Profile == DashboardBrowserTestProfile.Dense ? 1 : 3,
                    isStale,
                    string.Empty,
                    warning),
                new DashboardSnapshotSectionStatusResult(
                    "dependencyResources",
                    "Compose 服務資源",
                    effectiveSnapshotAtUtc,
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
            "contexthub_redis-data",
            Profile == DashboardBrowserTestProfile.Dense ? 86_400 : 7_200,
            Profile == DashboardBrowserTestProfile.Dense ? 3_600 : 800,
            Profile == DashboardBrowserTestProfile.Dense ? 18_000 : 1_200,
            42,
            3,
            Profile == DashboardBrowserTestProfile.Dense ? 1_640_000 : 96_000,
            Profile == DashboardBrowserTestProfile.Dense ? 60_000 : 4_000,
            Profile == DashboardBrowserTestProfile.Dense ? 1_700_000 : 100_000,
            96.47,
            Profile == DashboardBrowserTestProfile.Dense ? 90_000 : 8_000,
            Profile == DashboardBrowserTestProfile.Dense ? 96.0 : 90.0);

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
            Profile == DashboardBrowserTestProfile.Dense ? 640L * 1024 * 1024 : 96L * 1024 * 1024,
            Profile == DashboardBrowserTestProfile.Dense ? 8_520_000 : 444_000,
            Profile == DashboardBrowserTestProfile.Dense ? 96.24 : 94.59);

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

        if (Profile == DashboardBrowserTestProfile.GraphDemo)
        {
            return BuildGraphDemoMemories();
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

    private static IReadOnlyList<MemoryDocument> BuildGraphDemoMemories()
        => [
            CreateGraphDemoMemory(
                0,
                "demo-memory",
                MemoryType.Fact,
                "示範記憶",
                "Graph seed：ContextHub Dashboard 的入口節點，用來展示 hub、bridge、explicit 與 similarity 關聯。",
                "demo://graph/seed",
                "ContextHub",
                ["demo", "dashboard", "graph", "hub"],
                0.98m),
            CreateGraphDemoMemory(
                1,
                "graph-design-decision",
                MemoryType.Decision,
                "Graph Explorer 採用 JS viewport 互動",
                "決策節點：圖譜縮放與拖曳平移交由前端 JS 管理，避免 Blazor round trip 造成互動延遲。",
                "decision://graph/js-viewport",
                "ContextHub",
                ["graph", "viewport", "interaction"],
                0.94m),
            CreateGraphDemoMemory(
                2,
                "graph-api-contract",
                MemoryType.Artifact,
                "Memory Graph API contract v1",
                "Artifact 節點：/api/memories/graph 回傳 nodes、edges 與 stats，Dashboard 只負責視覺化。",
                "artifact://api/memory-graph-v1",
                "ContextHub.Dashboard",
                ["graph", "api", "contract"],
                0.91m),
            CreateGraphDemoMemory(
                3,
                "graph-layout-validation",
                MemoryType.Fact,
                "Graph layout validation passed",
                "Fact 節點：桌面、平板與手機 viewport 已驗證節點卡片不重疊，canvas 可以 fit、zoom、pan。",
                "test://dashboard/graph-layout",
                "ContextHub.Dashboard",
                ["graph", "browser-test", "layout"],
                0.89m),
            CreateGraphDemoMemory(
                4,
                "graph-stitch-baseline",
                MemoryType.Artifact,
                "Stitch Memory Graph baseline",
                "Artifact 節點：Stitch baseline 定義 graph explorer 的管理介面密度、legend 與 node detail 層級。",
                "stitch://projects/6837023420245161450",
                "ContextHub.Design",
                ["graph", "stitch", "ux"],
                0.86m),
            CreateGraphDemoMemory(
                5,
                "graph-selection-behavior",
                MemoryType.Decision,
                "選取節點不等於初始聚焦",
                "二階節點：全專案整合視圖不自動 focus，避免初始畫面被單一節點裁切。",
                "decision://graph/selection-focus",
                "ContextHub",
                ["graph", "focus", "selection"],
                0.82m),
            CreateGraphDemoMemory(
                6,
                "graph-similarity-fallback",
                MemoryType.Decision,
                "Similarity fallback 避免空關聯",
                "二階節點：搜尋結果不足時以 lexical fallback 補足 similarity 邊，讓探索圖不會只有孤立節點。",
                "decision://graph/similarity-fallback",
                "ContextHub.Application",
                ["graph", "similarity", "retrieval"],
                0.8m),
            CreateGraphDemoMemory(
                7,
                "graph-css-isolation",
                MemoryType.Decision,
                "Graph CSS isolation",
                "Bridge 節點：graph 專屬樣式放在 CSS isolation，降低全域 dashboard CSS 互相污染的風險。",
                "decision://graph/css-isolation",
                "ContextHub.Dashboard",
                ["graph", "css", "maintainability"],
                0.78m)
            ,
            CreateGraphDemoMemory(
                8,
                "graph-index-snapshot",
                MemoryType.Artifact,
                "MemoryGraphIndex 背景快照",
                "Artifact 節點：關聯索引由背景 collector 預先建立，Dashboard 只讀取已完成的 snapshot。",
                "snapshot://dashboard/memory-graph-index",
                "ContextHub.Application",
                ["graph", "snapshot", "background"],
                0.88m),
            CreateGraphDemoMemory(
                9,
                "graph-scheduled-refresh",
                MemoryType.Decision,
                "排程刷新關聯索引",
                "Decision 節點：memoryGraphIndex 依 Dashboard polling cadence 排程更新，避免互動查詢時重新計算全圖。",
                "schedule://dashboard/memory-graph-index",
                "ContextHub.Worker",
                ["graph", "schedule", "snapshot"],
                0.84m),
            CreateGraphDemoMemory(
                10,
                "graph-event-refresh-entry",
                MemoryType.Fact,
                "事件驅動刷新入口",
                "Fact 節點：memory upsert、source sync 與 reindex 完成後可導向同一個 graph index refresh flow。",
                "event://memory/graph-index-refresh",
                "ContextHub.Runtime",
                ["graph", "event", "refresh"],
                0.8m),
            CreateGraphDemoMemory(
                11,
                "graph-manual-refresh",
                MemoryType.Artifact,
                "手動觸發關聯重建",
                "Artifact 節點：管理者可以手動觸發背景索引重建，再由圖譜頁重新讀取最新 snapshot。",
                "manual://dashboard/memory-graph-index",
                "ContextHub.Dashboard",
                ["graph", "manual", "snapshot"],
                0.79m)
        ];

    private static MemoryDocument CreateGraphDemoMemory(
        int index,
        string externalKey,
        MemoryType memoryType,
        string title,
        string summary,
        string sourceRef,
        string projectId,
        IReadOnlyList<string> tags,
        decimal importance)
    {
        return new MemoryDocument(
            CreateGraphDemoId(index),
            externalKey,
            MemoryScope.Project,
            memoryType,
            title,
            $"這是一筆 graph preview 測試資料。{summary}",
            summary,
            "document",
            sourceRef,
            tags,
            importance,
            0.92m,
            3,
            MemoryStatus.Active,
            "{\"kind\":\"graph-preview\"}",
            DateTimeOffset.UtcNow.AddDays(-1).AddMinutes(-index),
            DateTimeOffset.UtcNow.AddMinutes(-index),
            projectId,
            false);
    }

    private MemoryDocument CreateMemory(int index, string externalKey, string title, string summary, bool readOnly)
    {
        var content = Profile == DashboardBrowserTestProfile.Dense
            ? string.Join(Environment.NewLine, Enumerable.Range(1, 10).Select(line => $"Dense memory {index:00} line {line}: validates long body content, scroll containers, and non-overlapping detail panels."))
            : "這是一筆提供給 dashboard browser 測試的示範記憶內容。";

        return new MemoryDocument(
            Guid.Parse($"10000000-0000-0000-0000-{index + 1:000000000000}"),
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
            readOnly
                ? "shared"
                : Profile == DashboardBrowserTestProfile.Dense
                    ? index % 2 == 0
                        ? "context-hub-dev-project-with-long-name"
                        : "ContextHub"
                    : "ContextHub",
            readOnly);
    }

    private static IReadOnlyList<MemoryDocument> FilterGraphMemories(MemoryGraphRequest request, IReadOnlyList<MemoryDocument> memories)
    {
        var query = memories.AsEnumerable();

        if (IsIntegratedAllProjectsGraphRequest(request))
        {
            query = query.Where(memory =>
                !string.Equals(memory.ProjectId, ProjectContext.SharedProjectId, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(memory.ProjectId, ProjectContext.UserProjectId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectId))
        {
            query = query.Where(memory => string.Equals(memory.ProjectId, request.ProjectId, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.ProjectQuery))
        {
            query = query.Where(memory => memory.ProjectId.Contains(request.ProjectQuery, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            query = query.Where(memory =>
                memory.Title.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ||
                memory.Summary.Contains(request.Query, StringComparison.OrdinalIgnoreCase) ||
                memory.SourceRef.Contains(request.Query, StringComparison.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.Tag))
        {
            query = query.Where(memory => memory.Tags.Contains(request.Tag, StringComparer.OrdinalIgnoreCase));
        }

        if (!string.IsNullOrWhiteSpace(request.SourceType))
        {
            query = query.Where(memory => string.Equals(memory.SourceType, request.SourceType, StringComparison.OrdinalIgnoreCase));
        }

        if (request.Scope.HasValue)
        {
            query = query.Where(memory => memory.Scope == request.Scope.Value);
        }

        if (request.MemoryType.HasValue)
        {
            query = query.Where(memory => memory.MemoryType == request.MemoryType.Value);
        }

        if (request.Status.HasValue)
        {
            query = query.Where(memory => memory.Status == request.Status.Value);
        }

        return query.ToArray();
    }

    private static bool IsIntegratedAllProjectsGraphRequest(MemoryGraphRequest request)
        => string.IsNullOrWhiteSpace(request.ProjectId) &&
           string.IsNullOrWhiteSpace(request.ProjectQuery) &&
           (request.IncludedProjectIds is null || request.IncludedProjectIds.Count == 0) &&
           request.QueryMode != MemoryQueryMode.SummaryOnly;

    private IReadOnlyList<MemoryGraphEdgeResult> BuildGraphEdges(IReadOnlyList<MemoryDocument> nodes, IReadOnlyList<Guid> seedIds, bool includeSimilarity)
    {
        if (Profile == DashboardBrowserTestProfile.GraphDemo)
        {
            return SelectPrecomputedGraphDemoEdges(nodes, includeSimilarity);
        }

        var edges = new List<MemoryGraphEdgeResult>();
        if (nodes.Count == 0)
        {
            return edges;
        }

        for (var index = 1; index < Math.Min(nodes.Count, 5); index++)
        {
            var fromId = seedIds.Count > 0 ? seedIds[0] : nodes[0].Id;
            var relation = index switch
            {
                1 => "decides",
                2 => "implements",
                3 => "validates",
                4 => "references",
                _ => "related"
            };
            edges.Add(new MemoryGraphEdgeResult(fromId, nodes[index].Id, "explicit", relation));
        }

        if (seedIds.Count > 1 && nodes.Count > 3)
        {
            edges.Add(new MemoryGraphEdgeResult(seedIds[1], nodes[3].Id, "explicit", "depends-on"));
        }

        if (nodes.Count > 5)
        {
            edges.Add(new MemoryGraphEdgeResult(nodes[1].Id, nodes[5].Id, "explicit", "refines"));
        }

        if (nodes.Count > 6)
        {
            edges.Add(new MemoryGraphEdgeResult(nodes[2].Id, nodes[6].Id, "explicit", "backs"));
        }

        if (nodes.Count > 7)
        {
            edges.Add(new MemoryGraphEdgeResult(nodes[3].Id, nodes[7].Id, "explicit", "isolates-style"));
        }

        if (includeSimilarity)
        {
            var similarityRoots = seedIds.Count > 0
                ? seedIds
                : nodes.Take(Math.Min(nodes.Count, 2)).Select(node => node.Id).ToArray();

            foreach (var seedId in similarityRoots)
            {
                foreach (var nodeId in nodes.Where(node => node.Id != seedId).Skip(2).Take(nodes.Count >= 8 ? 3 : 2).Select(node => node.Id))
                {
                    edges.Add(new MemoryGraphEdgeResult(seedId, nodeId, "similar", "Similarity", 0.82m));
                }
            }
        }

        return edges
            .GroupBy(edge => $"{edge.EdgeType}:{edge.FromId}:{edge.ToId}")
            .Select(group => group.First())
            .ToArray();
    }

    private static Guid CreateGraphDemoId(int index)
        => Guid.Parse($"10000000-0000-0000-0000-{index + 1:000000000000}");

    private static IReadOnlyList<MemoryGraphEdgeResult> SelectPrecomputedGraphDemoEdges(
        IReadOnlyList<MemoryDocument> nodes,
        bool includeSimilarity)
    {
        var nodeIds = nodes.Select(node => node.Id).ToHashSet();
        return GraphDemoPrecomputedEdges
            .Where(edge => nodeIds.Contains(edge.FromId) && nodeIds.Contains(edge.ToId))
            .Where(edge => includeSimilarity || !string.Equals(edge.EdgeType, "similar", StringComparison.OrdinalIgnoreCase))
            .ToArray();
    }

    private static IReadOnlyList<MemoryGraphEdgeResult> BuildGraphDemoPrecomputedEdges()
    {
        static Guid Node(int index) => CreateGraphDemoId(index);
        static MemoryGraphEdgeResult Explicit(int from, int to, string relation)
            => new(Node(from), Node(to), "explicit", relation);
        static MemoryGraphEdgeResult Similar(int from, int to, decimal score)
            => new(Node(from), Node(to), "similar", "Similarity", score);

        return
        [
            Explicit(0, 1, "decides"),
            Explicit(0, 2, "documents"),
            Explicit(0, 3, "validates"),
            Explicit(0, 4, "references"),
            Explicit(0, 8, "indexes"),
            Explicit(1, 5, "refines"),
            Explicit(1, 7, "implements"),
            Explicit(1, 3, "verifies"),
            Explicit(2, 6, "backs"),
            Explicit(2, 8, "served-by"),
            Explicit(2, 10, "accepts-event"),
            Explicit(3, 7, "guards"),
            Explicit(3, 11, "tests-manual"),
            Explicit(4, 7, "styles"),
            Explicit(4, 5, "baseline-for"),
            Explicit(5, 3, "validates"),
            Explicit(6, 2, "derives"),
            Explicit(6, 8, "precomputes"),
            Explicit(8, 9, "scheduled-by"),
            Explicit(8, 10, "refreshed-by"),
            Explicit(8, 11, "rebuilt-by"),
            Explicit(9, 3, "observed-by"),
            Explicit(9, 10, "coordinates"),
            Explicit(10, 11, "shares-flow"),
            Explicit(11, 2, "publishes"),
            Similar(0, 8, 0.92m),
            Similar(4, 7, 0.88m),
            Similar(6, 10, 0.84m),
            Similar(9, 11, 0.82m)
        ];
    }

    private static Dictionary<Guid, int> BuildEdgeCounts(IReadOnlyList<MemoryGraphEdgeResult> edges, string edgeType)
        => edges
            .Where(edge => string.Equals(edge.EdgeType, edgeType, StringComparison.OrdinalIgnoreCase))
            .SelectMany(edge => new[] { (edge.FromId, edge.ToId), (edge.ToId, edge.FromId) })
            .GroupBy(pair => pair.Item1)
            .ToDictionary(group => group.Key, group => group.Select(pair => pair.Item2).Distinct().Count());

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

    public Task<CurrentUserResult> GetCurrentUserAsync(CancellationToken cancellationToken)
        => Task.FromResult(new CurrentUserResult(
            Guid.Parse("72000000-0000-0000-0000-000000000001"),
            Guid.Parse("73000000-0000-0000-0000-000000000001"),
            "admin",
            "Admin User",
            "admin@example.com",
            TenantUserRole.Owner));

    public Task<IReadOnlyList<ApiTokenResult>> GetMyApiTokensAsync(bool includeRevoked, CancellationToken cancellationToken)
        => GetApiTokensAsync(Guid.Parse("72000000-0000-0000-0000-000000000001"), includeRevoked, cancellationToken);

    public Task<ApiTokenCreatedResult> CreateMyApiTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken)
        => CreateApiTokenAsync(request, cancellationToken);

    public Task<ApiTokenResult> UpdateMyApiTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken)
        => UpdateApiTokenAsync(tokenId, request, cancellationToken);

    public Task<ApiTokenCreatedResult> RegenerateMyApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
        => RegenerateApiTokenAsync(tokenId, cancellationToken);

    public Task<ApiTokenResult> RevokeMyApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
        => RevokeApiTokenAsync(tokenId, cancellationToken);
}

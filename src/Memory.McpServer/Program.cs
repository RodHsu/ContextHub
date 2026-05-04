using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.McpServer;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddProblemDetails();
builder.Services.AddMemoryApplication();
builder.Services.AddMemoryInfrastructure(builder.Configuration, "mcp-server");
builder.Services.AddHostedService<DashboardSnapshotCollectorHostedService>();
builder.Services.AddScoped<MemoryMcpTools>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<MemoryMcpTools>()
    .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult
    {
        Resources = []
    }))
    .WithListResourceTemplatesHandler(WorkingContextMcpResources.ListTemplatesAsync)
    .WithReadResourceHandler(WorkingContextMcpResources.ReadAsync);

var app = builder.Build();

app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var shouldCount = path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
                      path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);
    var suppressTrafficMetrics =
        context.Request.Headers.TryGetValue(RequestTrafficConstants.DashboardRequestHeader, out var headerValues) &&
        headerValues.Any(value => string.Equals(value, RequestTrafficConstants.DashboardRequestHeaderValue, StringComparison.Ordinal));

    using var suppression = suppressTrafficMetrics ? RequestTrafficSuppressionScope.Suppress() : null;

    try
    {
        await next();
    }
    finally
    {
        if (shouldCount && !RequestTrafficSuppressionScope.IsSuppressed)
        {
            context.RequestServices.GetRequiredService<RequestTrafficMetricsCollector>().RecordInbound();
        }
    }
});

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.MapGet("/api/status", async (
    IDashboardSnapshotStore snapshotStore,
    TimeProvider timeProvider,
    CancellationToken cancellationToken) =>
{
    var snapshot = await snapshotStore.GetAsync<DashboardStatusCoreSnapshotPayload>(DashboardSnapshotKeys.StatusCore, cancellationToken);
    var now = timeProvider.GetUtcNow();
    var payload = snapshot?.Payload;
    return Results.Ok(new SystemStatusResult(
        payload?.Service ?? "mcp-server",
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
        payload?.CacheVersion ?? 0L,
        now,
        snapshot?.CapturedAtUtc ?? now,
        snapshot?.RefreshIntervalSeconds ?? 0,
        snapshot is null || snapshot.StaleAfterUtc < now,
        snapshot?.LastError ?? (snapshot is null ? "Status snapshot unavailable." : string.Empty),
        snapshot is null
            ? "尚未收到背景快照。"
            : snapshot.StaleAfterUtc < now
                ? "狀態資料已過期。"
                : string.Empty));
});

var dashboard = app.MapGroup("/api/dashboard");
dashboard.MapGet("/overview", async (IDashboardQueryService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetOverviewAsync(cancellationToken);
    return Results.Ok(result);
});

dashboard.MapGet("/runtime", async (IDashboardQueryService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetRuntimeAsync(cancellationToken);
    return Results.Ok(result);
});

dashboard.MapGet("/monitoring", async (IDashboardQueryService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetMonitoringAsync(cancellationToken);
    return Results.Ok(result);
});

var memories = app.MapGroup("/api/memories");
memories.MapGet(string.Empty, async (
    string? query,
    string? scope,
    string? memoryType,
    string? status,
    string? sourceType,
    string? tag,
    string? projectId,
    string? projectQuery,
    string? includedProjectIds,
    string? queryMode,
    bool? useSummaryLayer,
    int? page,
    int? pageSize,
    IDashboardQueryService service,
    CancellationToken cancellationToken) =>
{
    string? scopeError = null;
    string? memoryTypeError = null;
    string? statusError = null;
    string? queryModeError = null;
    if (!EnumParser.TryParse(scope, out MemoryScope? parsedScope, out scopeError) ||
        !EnumParser.TryParse(memoryType, out MemoryType? parsedMemoryType, out memoryTypeError) ||
        !EnumParser.TryParse(status, out MemoryStatus? parsedStatus, out statusError) ||
        !EnumParser.TryParse(queryMode, out MemoryQueryMode? parsedQueryMode, out queryModeError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["scope"] = scopeError is null ? [] : [scopeError],
            ["memoryType"] = memoryTypeError is null ? [] : [memoryTypeError],
            ["status"] = statusError is null ? [] : [statusError],
            ["queryMode"] = queryModeError is null ? [] : [queryModeError]
        }.Where(x => x.Value.Length > 0).ToDictionary());
    }

    var result = await service.GetMemoriesAsync(
        new MemoryListRequest(
            query,
            parsedScope,
            parsedMemoryType,
            parsedStatus,
            sourceType,
            tag,
            string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim(),
            string.IsNullOrWhiteSpace(projectQuery) ? null : projectQuery.Trim(),
            QueryParser.ParseProjectIds(includedProjectIds),
            parsedQueryMode ?? MemoryQueryMode.CurrentOnly,
            useSummaryLayer ?? false,
            page ?? 1,
            pageSize ?? 25),
        cancellationToken);
    return Results.Ok(result);
});

memories.MapGet("/projects", async (
    string? query,
    int? limit,
    IDashboardQueryService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.GetProjectSuggestionsAsync(query, limit ?? 8, cancellationToken);
    return Results.Ok(result);
});

memories.MapGet("/graph", async (
    string? query,
    string? tag,
    string? projectId,
    string? projectQuery,
    string? includedProjectIds,
    string? queryMode,
    bool? useSummaryLayer,
    string? graphMode,
    int? maxNodes,
    bool? includeSimilarity,
    string? scope,
    string? memoryType,
    string? status,
    string? sourceType,
    IDashboardQueryService service,
    CancellationToken cancellationToken) =>
{
    string? queryModeError = null;
    string? graphModeError = null;
    string? scopeError = null;
    string? memoryTypeError = null;
    string? statusError = null;

    if (!EnumParser.TryParse(queryMode, out MemoryQueryMode? parsedQueryMode, out queryModeError) ||
        !EnumParser.TryParse(graphMode, out MemoryGraphMode? parsedGraphMode, out graphModeError) ||
        !EnumParser.TryParse(scope, out MemoryScope? parsedScope, out scopeError) ||
        !EnumParser.TryParse(memoryType, out MemoryType? parsedMemoryType, out memoryTypeError) ||
        !EnumParser.TryParse(status, out MemoryStatus? parsedStatus, out statusError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["queryMode"] = queryModeError is null ? [] : [queryModeError],
            ["graphMode"] = graphModeError is null ? [] : [graphModeError],
            ["scope"] = scopeError is null ? [] : [scopeError],
            ["memoryType"] = memoryTypeError is null ? [] : [memoryTypeError],
            ["status"] = statusError is null ? [] : [statusError]
        }.Where(x => x.Value.Length > 0).ToDictionary());
    }

    var result = await service.GetMemoryGraphAsync(
        new MemoryGraphRequest(
            query,
            tag,
            string.IsNullOrWhiteSpace(projectId) ? null : projectId.Trim(),
            string.IsNullOrWhiteSpace(projectQuery) ? null : projectQuery.Trim(),
            QueryParser.ParseProjectIds(includedProjectIds),
            parsedQueryMode ?? MemoryQueryMode.CurrentOnly,
            useSummaryLayer ?? false,
            parsedGraphMode ?? MemoryGraphMode.Seeded,
            maxNodes ?? 120,
            includeSimilarity ?? true,
            parsedScope,
            parsedMemoryType,
            parsedStatus,
            sourceType),
        cancellationToken);
    return Results.Ok(result);
});

memories.MapGet("/search", async (
    string query,
    int? limit,
    bool? includeArchived,
    string? projectId,
    string? includedProjectIds,
    string? queryMode,
    bool? useSummaryLayer,
    IMemoryService service,
    CancellationToken cancellationToken) =>
{
    if (!EnumParser.TryParse(queryMode, out MemoryQueryMode? parsedQueryMode, out var queryModeError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["queryMode"] = [queryModeError ?? "Unsupported MemoryQueryMode value."]
        });
    }

    var result = await service.SearchAsync(
        new MemorySearchRequest(
            query,
            limit ?? 10,
            includeArchived ?? false,
            ProjectContext.Normalize(projectId),
            QueryParser.ParseProjectIds(includedProjectIds),
            parsedQueryMode ?? MemoryQueryMode.CurrentOnly,
            useSummaryLayer ?? false,
            new RetrievalTelemetryContext("/api/memories/search", "rest", "dashboard memory search")),
        cancellationToken);
    return Results.Ok(result);
});

memories.MapGet("/{id:guid}", async (Guid id, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetAsync(id, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

memories.MapGet("/{id:guid}/details", async (Guid id, IDashboardQueryService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetMemoryDetailsAsync(id, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

memories.MapPost("/export", async (MemoryExportRequest request, IMemoryTransferService service, CancellationToken cancellationToken) =>
{
    var result = await service.ExportAsync(request, cancellationToken);
    return Results.Ok(result);
});

memories.MapPost("/import/preview", async (MemoryImportRequest request, IMemoryTransferService service, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.PreviewImportAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["package"] = [ex.Message]
        });
    }
});

memories.MapPost("/import/apply", async (MemoryImportRequest request, IMemoryTransferService service, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.ApplyImportAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["package"] = [ex.Message]
        });
    }
});

var logs = app.MapGroup("/api/logs");
logs.MapGet("/search", async (
    string? query,
    [FromQuery(Name = "serviceName")] string[]? serviceNames,
    [FromQuery(Name = "level")] string[]? levels,
    string? traceId,
    string? requestId,
    DateTimeOffset? from,
    DateTimeOffset? to,
    int? limit,
    string? projectId,
    ILogQueryService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.SearchAsync(
        new LogQueryRequest(
            query,
            JoinQueryFilter(serviceNames),
            JoinQueryFilter(levels),
            traceId,
            requestId,
            from,
            to,
            limit ?? 50,
            ProjectContext.Normalize(projectId)),
        cancellationToken);
    return Results.Ok(result);
});

logs.MapGet("/{id:long}", async (long id, ILogQueryService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetAsync(id, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

static string? JoinQueryFilter(string[]? values)
    => values is null || values.Length == 0
        ? null
        : string.Join(',', values.Where(value => !string.IsNullOrWhiteSpace(value)));

var userPreferences = app.MapGroup("/api/user/preferences");
userPreferences.MapGet(string.Empty, async (
    string? kind,
    bool? includeArchived,
    int? limit,
    IMemoryService service,
    CancellationToken cancellationToken) =>
{
    UserPreferenceKind? parsedKind = null;
    if (!string.IsNullOrWhiteSpace(kind))
    {
        if (!Enum.TryParse<UserPreferenceKind>(kind, ignoreCase: true, out var value))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["kind"] = ["Unsupported user preference kind."]
            });
        }

        parsedKind = value;
    }

    var result = await service.ListUserPreferencesAsync(new UserPreferenceListRequest(parsedKind, includeArchived ?? false, limit ?? 50), cancellationToken);
    return Results.Ok(result);
});

userPreferences.MapPost(string.Empty, async (UserPreferenceUpsertRequest request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.UpsertUserPreferenceAsync(request, cancellationToken);
    return Results.Ok(result);
});

userPreferences.MapPatch("/{id:guid}", async (Guid id, UserPreferenceArchiveBody request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.ArchiveUserPreferenceAsync(new UserPreferenceArchiveRequest(id, request.Archived), cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/context/build", async (WorkingContextRequest request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.BuildWorkingContextAsync(
        request with
        {
            Telemetry = new RetrievalTelemetryContext("/api/context/build", "rest", "task context bootstrap")
        },
        cancellationToken);
    return Results.Ok(result);
});

var sources = app.MapGroup("/api/sources");
sources.MapGet(string.Empty, async (
    string? projectId,
    string? enabled,
    string? sourceKind,
    ISourceConnectionService service,
    CancellationToken cancellationToken) =>
{
    if (!bool.TryParse(enabled, out var parsedEnabled) && !string.IsNullOrWhiteSpace(enabled))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["enabled"] = ["Enabled must be true or false."]
        });
    }

    if (!EnumParser.TryParse(sourceKind, out SourceKind? parsedSourceKind, out var sourceKindError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["sourceKind"] = [sourceKindError ?? "Unsupported SourceKind value."]
        });
    }

    var result = await service.ListAsync(
        new SourceListRequest(
            ProjectContext.Normalize(projectId),
            string.IsNullOrWhiteSpace(enabled) ? null : parsedEnabled,
            parsedSourceKind),
        cancellationToken);
    return Results.Ok(result);
});

sources.MapPost(string.Empty, async (
    SourceConnectionCreateRequest request,
    ISourceConnectionService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.CreateAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["source"] = [ex.Message]
        });
    }
});

sources.MapPatch("/{id:guid}", async (
    Guid id,
    SourceConnectionPatchBody request,
    ISourceConnectionService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.UpdateAsync(
            new SourceConnectionUpdateRequest(
                id,
                request.Name,
                request.ConfigJson,
                request.SecretJson,
                request.Enabled,
                request.ProjectId),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["source"] = [ex.Message]
        });
    }
});

sources.MapPost("/{id:guid}/sync", async (
    Guid id,
    SourceSyncBody request,
    ISourceConnectionService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.EnqueueSyncAsync(
        new SourceSyncRequest(
            id,
            request.Trigger,
            request.Force,
            request.ProjectId),
        cancellationToken);
    return Results.Ok(result);
});

sources.MapGet("/{id:guid}/runs", async (
    Guid id,
    string? projectId,
    ISourceConnectionService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListRunsAsync(id, projectId, cancellationToken);
    return Results.Ok(result);
});

var governance = app.MapGroup("/api/governance");
governance.MapGet("/findings", async (
    string? projectId,
    string? type,
    string? status,
    int? limit,
    IGovernanceService service,
    CancellationToken cancellationToken) =>
{
    string? typeError = null;
    string? statusError = null;
    if (!EnumParser.TryParse(type, out GovernanceFindingType? parsedType, out typeError) ||
        !EnumParser.TryParse(status, out GovernanceFindingStatus? parsedStatus, out statusError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["type"] = typeError is null ? [] : [typeError],
            ["status"] = statusError is null ? [] : [statusError]
        }.Where(x => x.Value.Length > 0).ToDictionary());
    }

    var result = await service.ListAsync(
        new GovernanceFindingListRequest(ProjectContext.Normalize(projectId), parsedType, parsedStatus, limit ?? 100),
        cancellationToken);
    return Results.Ok(result);
});

governance.MapPost("/analyze", async (
    GovernanceAnalyzeRequest request,
    IGovernanceService governanceService,
    ISuggestedActionService actionService,
    CancellationToken cancellationToken) =>
{
    var projectId = ProjectContext.Normalize(request.ProjectId);
    await governanceService.AnalyzeAsync(projectId, cancellationToken);

    var findings = await governanceService.ListAsync(
        new GovernanceFindingListRequest(projectId, Status: GovernanceFindingStatus.Open),
        cancellationToken);
    var actions = await actionService.ListAsync(
        new SuggestedActionListRequest(projectId),
        cancellationToken);

    return Results.Ok(new GovernanceAnalyzeResult(
        projectId,
        findings.Count,
        actions.Count,
        DateTimeOffset.UtcNow));
});

governance.MapPost("/findings/{id:guid}/accept", async (Guid id, IGovernanceService service, CancellationToken cancellationToken) =>
{
    var result = await service.AcceptAsync(id, cancellationToken);
    return Results.Ok(result);
});

governance.MapPost("/findings/{id:guid}/dismiss", async (Guid id, IGovernanceService service, CancellationToken cancellationToken) =>
{
    var result = await service.DismissAsync(id, cancellationToken);
    return Results.Ok(result);
});

var evaluation = app.MapGroup("/api/evaluation");
evaluation.MapGet("/suites", async (
    string? projectId,
    IEvaluationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListSuitesAsync(ProjectContext.Normalize(projectId), cancellationToken);
    return Results.Ok(result);
});

evaluation.MapPost("/suites", async (
    EvaluationSuiteCreateRequest request,
    IEvaluationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.CreateSuiteAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["suite"] = [ex.Message]
        });
    }
});

evaluation.MapPost("/runs", async (
    EvaluationRunRequest request,
    IEvaluationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.RunAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["run"] = [ex.Message]
        });
    }
});

evaluation.MapGet("/runs/{id:guid}", async (Guid id, IEvaluationService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetRunAsync(id, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

var actions = app.MapGroup("/api/actions");
actions.MapGet(string.Empty, async (
    string? projectId,
    string? status,
    string? type,
    int? limit,
    ISuggestedActionService service,
    CancellationToken cancellationToken) =>
{
    string? statusError = null;
    string? typeError = null;
    if (!EnumParser.TryParse(status, out SuggestedActionStatus? parsedStatus, out statusError) ||
        !EnumParser.TryParse(type, out SuggestedActionType? parsedType, out typeError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = statusError is null ? [] : [statusError],
            ["type"] = typeError is null ? [] : [typeError]
        }.Where(x => x.Value.Length > 0).ToDictionary());
    }

    var result = await service.ListAsync(
        new SuggestedActionListRequest(ProjectContext.Normalize(projectId), parsedStatus, parsedType, limit ?? 100),
        cancellationToken);
    return Results.Ok(result);
});

actions.MapPost("/{id:guid}/accept", async (Guid id, ISuggestedActionService service, CancellationToken cancellationToken) =>
{
    var result = await service.AcceptAsync(id, cancellationToken);
    return Results.Ok(result);
});

actions.MapPost("/{id:guid}/dismiss", async (Guid id, ISuggestedActionService service, CancellationToken cancellationToken) =>
{
    var result = await service.DismissAsync(id, cancellationToken);
    return Results.Ok(result);
});

var conversations = app.MapGroup("/api/conversations");
conversations.MapPost("/ingest", async (
    ConversationIngestRequest request,
    IConversationAutomationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.IngestAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["conversation"] = [ex.Message]
        });
    }
});

conversations.MapGet("/sessions", async (
    string? projectId,
    string? sourceSystem,
    string? conversationId,
    int? limit,
    IConversationAutomationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListSessionsAsync(
        new ConversationSessionListRequest(projectId, sourceSystem, conversationId, limit ?? 50),
        cancellationToken);
    return Results.Ok(result);
});

conversations.MapGet("/insights", async (
    string? projectId,
    string? conversationId,
    string? promotionStatus,
    string? insightType,
    int? limit,
    IConversationAutomationService service,
    CancellationToken cancellationToken) =>
{
    string? promotionStatusError = null;
    string? insightTypeError = null;
    if (!EnumParser.TryParse(promotionStatus, out ConversationPromotionStatus? parsedPromotionStatus, out promotionStatusError) ||
        !EnumParser.TryParse(insightType, out ConversationInsightType? parsedInsightType, out insightTypeError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["promotionStatus"] = promotionStatusError is null ? [] : [promotionStatusError],
            ["insightType"] = insightTypeError is null ? [] : [insightTypeError]
        }.Where(x => x.Value.Length > 0).ToDictionary());
    }

    var result = await service.ListInsightsAsync(
        new ConversationInsightListRequest(projectId, conversationId, parsedPromotionStatus, parsedInsightType, limit ?? 100),
        cancellationToken);
    return Results.Ok(result);
});

var jobs = app.MapGroup("/api/jobs");
jobs.MapGet(string.Empty, async (
    string? status,
    string? jobType,
    int? page,
    int? pageSize,
    IDashboardQueryService service,
    CancellationToken cancellationToken) =>
{
    string? statusError = null;
    string? jobTypeError = null;
    if (!EnumParser.TryParse(status, out MemoryJobStatus? parsedStatus, out statusError) ||
        !EnumParser.TryParse(jobType, out MemoryJobType? parsedJobType, out jobTypeError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = statusError is null ? [] : [statusError],
            ["jobType"] = jobTypeError is null ? [] : [jobTypeError]
        }.Where(x => x.Value.Length > 0).ToDictionary());
    }

    var result = await service.GetJobsAsync(
        new JobListRequest(parsedStatus, parsedJobType, page ?? 1, pageSize ?? 25),
        cancellationToken);
    return Results.Ok(result);
});

jobs.MapGet("/{id:guid}", async (Guid id, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetJobAsync(id, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});

jobs.MapPost("/reindex", async (EnqueueReindexRequest request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.EnqueueReindexAsync(request, cancellationToken);
    return Results.Ok(result);
});

jobs.MapPost("/summary-refresh", async (EnqueueSummaryRefreshRequest request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.EnqueueSummaryRefreshAsync(request, cancellationToken);
    return Results.Ok(result);
});

var storage = app.MapGroup("/api/storage");
storage.MapGet("/tables", async (IDashboardQueryService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetStorageTablesAsync(cancellationToken);
    return Results.Ok(result);
});

storage.MapGet("/{table}", async (
    string table,
    string? query,
    string? column,
    int? page,
    int? pageSize,
    IDashboardQueryService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.GetStorageRowsAsync(
            new StorageRowsRequest(
                table,
                query,
                column,
                page ?? 1,
                pageSize ?? 50),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (ArgumentException ex) when (string.Equals(ex.ParamName, "column", StringComparison.Ordinal))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["column"] = [ex.Message]
        });
    }
    catch (InvalidOperationException ex)
    {
        return Results.NotFound(new ProblemDetails
        {
            Title = "Storage table not found.",
            Detail = ex.Message,
            Status = StatusCodes.Status404NotFound
        });
    }
});

app.MapPost("/api/performance/measure", async (PerformanceMeasureRequest request, IPerformanceProbeService service, CancellationToken cancellationToken) =>
{
    var errors = ApiValidation.ValidatePerformanceRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var result = await service.MeasureAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.MapMcp("/mcp");

app.Run();

public partial class Program;

internal static class ApiValidation
{
    public static Dictionary<string, string[]> ValidatePerformanceRequest(PerformanceMeasureRequest request)
    {
        var errors = new Dictionary<string, string[]>();

        if (string.IsNullOrWhiteSpace(request.Query))
        {
            errors["query"] = ["Query is required."];
        }

        if (request.SearchLimit is < 1 or > 50)
        {
            errors["searchLimit"] = ["SearchLimit must be between 1 and 50."];
        }

        if (request.WarmupIterations is < 0 or > 10)
        {
            errors["warmupIterations"] = ["WarmupIterations must be between 0 and 10."];
        }

        if (request.MeasurementIterations is < 1 or > 20)
        {
            errors["measurementIterations"] = ["MeasurementIterations must be between 1 and 20."];
        }

        return errors;
    }
}

internal sealed record UserPreferenceArchiveBody(bool Archived = true);
internal sealed record SourceConnectionPatchBody(
    string? Name = null,
    string? ConfigJson = null,
    string? SecretJson = null,
    bool? Enabled = null,
    string? ProjectId = null);
internal sealed record SourceSyncBody(
    SourceSyncTrigger Trigger = SourceSyncTrigger.Manual,
    bool Force = false,
    string? ProjectId = null);

internal static class EnumParser
{
    public static bool TryParse<TEnum>(string? value, out TEnum? parsed, out string? error)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            parsed = null;
            error = null;
            return true;
        }

        if (Enum.TryParse<TEnum>(value, ignoreCase: true, out var typed))
        {
            parsed = typed;
            error = null;
            return true;
        }

        parsed = null;
        error = $"Unsupported {typeof(TEnum).Name} value.";
        return false;
    }
}

internal static class QueryParser
{
    public static IReadOnlyList<string>? ParseProjectIds(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? null
            : value
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
}

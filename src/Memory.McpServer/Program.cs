using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.McpServer;
using Memory.McpTransport;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;
using System.Diagnostics;
using System.Security.Cryptography;

var builder = WebApplication.CreateBuilder(args);
LocalDotEnvConfiguration.AddFallbacks(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    new Dictionary<string, string>
    {
        ["CONTEXTHUB_SECURITY_BOOTSTRAP_TOKEN"] = "ContextHub:Security:BootstrapToken",
        ["CONTEXTHUB_SECURITY_BOOTSTRAP_TENANT_SLUG"] = "ContextHub:Security:BootstrapTenantSlug",
        ["CONTEXTHUB_SECURITY_BOOTSTRAP_USERNAME"] = "ContextHub:Security:BootstrapUsername",
        ["CONTEXTHUB_SECURITY_BOOTSTRAP_ALLOWED_PROJECT_IDS"] = "ContextHub:Security:BootstrapAllowedProjectIds"
    });

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                               ForwardedHeaders.XForwardedHost |
                               ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.RequireHeaderSymmetry = true;
    AddTrustedForwarders(options, builder.Configuration);
});

builder.Services.AddProblemDetails();
builder.Services.AddAuthentication(ContextHubAuthentication.Scheme)
    .AddScheme<AuthenticationSchemeOptions, ApiTokenAuthenticationHandler>(ContextHubAuthentication.Scheme, _ => { });
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<IPasswordHasher<object>, PasswordHasher<object>>();
builder.Services.AddMemoryApplication();
builder.Services.AddMemoryInfrastructure(builder.Configuration, "mcp-server");
builder.Services.AddHostedService<InProcessMaintenanceRunRecoveryHostedService>();
builder.Services.AddHostedService<DashboardSnapshotCollectorHostedService>();
builder.Services.AddScoped<MemoryMcpTools>();
builder.Services.AddMcpServer(options => options.ServerInfo = new Implementation
{
    Name = "Memory.McpServer",
    Version = $"{BuildMetadata.Current.Version}+catalog.{GovernanceToolContract.PublishedCatalogVersion}"
})
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<MemoryMcpTools>()
    .WithRequestFilters(filters => filters.AddCallToolFilter(next => async (context, cancellationToken) =>
    {
        var startedAt = Stopwatch.GetTimestamp();
        var success = false;
        try
        {
            var result = await next(context, cancellationToken);
            success = result.IsError is not true;
            return result;
        }
        finally
        {
            await McpToolCallTelemetry.TryRecordAsync(
                context.Services,
                "mcp-server",
                context.Params?.Name ?? string.Empty,
                context.Params?.Arguments,
                success,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        }
    }))
    .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult
    {
        Resources = []
    }))
    .WithListResourceTemplatesHandler(WorkingContextMcpResources.ListTemplatesAsync)
    .WithReadResourceHandler(WorkingContextMcpResources.ReadAsync);

var app = builder.Build();
const bool requireAuthentication = true;
var allowedMcpOrigins = ResolveAllowedOrigins(
    builder.Configuration.GetSection("ContextHub:Security:AllowedMcpOrigins").Get<string[]>());

app.UseForwardedHeaders();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase) &&
        !IsAllowedMcpOrigin(context.Request, allowedMcpOrigins))
    {
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await context.Response.WriteAsync("Forbidden MCP Origin.", context.RequestAborted);
        return;
    }

    await next();
});
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (MaintenanceUnavailableException ex) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        var retryAfterSeconds = MaintenanceApiHelpers.ComputeRetryAfterSeconds(ex.Status.EstimatedEndsAtUtc);
        context.Response.Headers["Retry-After"] = retryAfterSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
        context.Response.Headers["X-ContextHub-Maintenance"] = ex.Status.Phase.ToString().ToLowerInvariant();
        context.Response.Headers["X-ContextHub-Maintenance-Phase"] = ex.Status.Phase.ToString();
        await Results.Problem(
            title: "ContextHub maintenance is in progress.",
            detail: ex.Message,
            statusCode: StatusCodes.Status503ServiceUnavailable,
            extensions: new Dictionary<string, object?>
            {
                ["phase"] = ex.Status.Phase.ToString(),
                ["runId"] = ex.Status.RunId,
                ["estimatedEndsAtUtc"] = ex.Status.EstimatedEndsAtUtc,
                ["activeLeaseCount"] = ex.Status.ActiveLeaseCount
            })
            .ExecuteAsync(context);
    }
    catch (UnauthorizedAccessException ex) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await Results.Problem(
            title: "Forbidden",
            detail: ex.Message,
            statusCode: StatusCodes.Status403Forbidden)
            .ExecuteAsync(context);
    }
});
app.Use(CloudflareCacheHeaders.ApplyNoStorePolicyAsync);
app.UseMiddleware<MaintenanceModeMiddleware>();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (!RequiresToken(context.Request.Path) ||
        context.User.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    await context.ChallengeAsync(ContextHubAuthentication.Scheme);
});
app.UseAuthorization();
app.UseMiddleware<RequestActorMiddleware>();
app.UseMcpProtocolCompatibility();
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
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).AllowAnonymous();

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
}).RequireAuthIfEnabled(requireAuthentication);

var dashboard = app.MapGroup("/api/dashboard");
dashboard.RequireAuthIfEnabled(requireAuthentication);
dashboard.RequireAdminIfEnabled(requireAuthentication);
dashboard.MapGet("/overview", async (IDashboardQueryService service, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    var result = await service.GetOverviewAsync(cancellationToken);
    SetDataSource(httpContext, "redis");
    return Results.Ok(result);
});

dashboard.MapGet("/runtime", async (IDashboardQueryService service, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    var result = await service.GetRuntimeAsync(cancellationToken);
    SetDataSource(httpContext, "redis");
    return Results.Ok(result);
});

dashboard.MapGet("/monitoring", async (IDashboardQueryService service, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    var result = await service.GetMonitoringAsync(cancellationToken);
    SetDataSource(httpContext, "redis");
    return Results.Ok(result);
});

var memories = app.MapGroup("/api/memories");
memories.RequireAuthIfEnabled(requireAuthentication);
memories.RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
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
    HttpContext httpContext,
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
    SetDataSource(httpContext, "cache");
    return Results.Ok(result);
});

memories.MapGet("/projects", async (
    string? query,
    int? limit,
    IDashboardQueryService service,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var result = await service.GetProjectSuggestionsAsync(query, limit ?? 8, cancellationToken);
    SetDataSource(httpContext, "redis");
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
    HttpContext httpContext,
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
    SetDataSource(httpContext, "redis");
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);

memories.MapPost("/graph/index/refresh", async (
    IDashboardMemoryGraphIndexRefreshService refreshService,
    CancellationToken cancellationToken) =>
{
    var result = await refreshService.RefreshAsync("manual", null, cancellationToken);
    return Results.Ok(result);
}).RequireAdminIfEnabled(requireAuthentication);

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

memories.MapGet("/{id:guid}/details", async (Guid id, IDashboardQueryService service, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    var result = await service.GetMemoryDetailsAsync(id, cancellationToken);
    SetDataSource(httpContext, "cache");
    return result is null ? Results.NotFound() : Results.Ok(result);
});

var projectInformation = app.MapGroup("/api/projects/information");
projectInformation.RequireAuthIfEnabled(requireAuthentication);
projectInformation.MapGet("/", async (bool? includeInactive, IProjectInformationService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ListAsync(includeInactive ?? false, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
projectInformation.MapGet("/{projectId}", async (string projectId, IProjectInformationService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetAsync(projectId, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
projectInformation.MapPut("/{projectId}", async (string projectId, ProjectInformationUpdateRequest request, IProjectInformationService service, CancellationToken cancellationToken) =>
{
    if (!string.Equals(ProjectContext.Normalize(projectId), ProjectContext.Normalize(request.ProjectId), StringComparison.OrdinalIgnoreCase))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["projectId"] = ["Route and request ProjectId must match."] });
    }

    return Results.Ok(await service.UpsertAsync(request, cancellationToken));
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
projectInformation.MapPost("/{projectId}/lifecycle", async (string projectId, ProjectLifecycleUpdateRequest request, IProjectInformationService service, CancellationToken cancellationToken) =>
{
    if (!string.Equals(ProjectContext.Normalize(projectId), ProjectContext.Normalize(request.ProjectId), StringComparison.OrdinalIgnoreCase))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["projectId"] = ["Route and request ProjectId must match."] });
    }

    return Results.Ok(await service.UpdateLifecycleAsync(request, cancellationToken));
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

memories.MapPost("/{id:guid}/archive", async (Guid id, MemoryArchiveBody request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.ArchiveAsync(new MemoryArchiveRequest(id, request.ProjectId, request.Archived, request.Reason), cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

memories.MapPost("/{id:guid}/restore", async (Guid id, MemoryArchiveBody request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.ArchiveAsync(new MemoryArchiveRequest(id, request.ProjectId, Archived: false, request.Reason), cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

memories.MapPost("/{id:guid}/move", async (Guid id, MemoryMoveBody request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.MoveAsync(new MemoryMoveRequest(id, request.TargetProjectId, request.SourceProjectId, request.Reason), cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

memories.MapPost("/{id:guid}/delete", async (Guid id, MemoryDeleteBody request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.DeleteAsync(new MemoryDeleteRequest(id, request.ProjectId, request.Reason), cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

memories.MapPost("/project-cleanup/preview", async (ProjectCleanupPreviewRequest request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.PreviewProjectCleanupAsync(request, cancellationToken);
    return Results.Ok(result);
});

memories.MapPost("/project-cleanup/apply", async (ProjectCleanupApplyRequest request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.ApplyProjectCleanupAsync(request, cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

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
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

var logs = app.MapGroup("/api/logs");
logs.RequireAuthIfEnabled(requireAuthentication);
logs.RequireAdminIfEnabled(requireAuthentication);
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
    IRedisObjectCache objectCache,
    IRequestActorAccessor actorAccessor,
    HttpContext httpContext,
    CancellationToken cancellationToken) =>
{
    var request = new LogQueryRequest(
        query,
        JoinQueryFilter(serviceNames),
        JoinQueryFilter(levels),
        traceId,
        requestId,
        from,
        to,
        limit ?? 50,
        ProjectContext.Normalize(projectId));
    var cacheKey = RedisCacheKeyBuilder.DashboardLogs(request, actorAccessor.Current);
    var cached = await objectCache.GetAsync<IReadOnlyList<LogEntryResult>>(cacheKey, "dashboard-logs-search", cancellationToken);
    if (cached.Hit && cached.Value is not null)
    {
        SetDataSource(httpContext, "cache");
        return Results.Ok(cached.Value);
    }

    var result = await service.SearchAsync(request, cancellationToken);
    await objectCache.SetAsync(cacheKey, "dashboard-logs-search", result, TimeSpan.FromSeconds(15), cancellationToken);
    SetDataSource(httpContext, "origin");
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
userPreferences.RequireAuthIfEnabled(requireAuthentication);
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
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.PreferencesRead);

userPreferences.MapPost(string.Empty, async (UserPreferenceUpsertToolRequest request, IMemoryService service, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.UpsertUserPreferenceAsync(request.ToApplicationRequest(), cancellationToken);
        return Results.Ok(result);
    }
    catch (MemoryScoreValidationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [ex.Field] = [ex.Message],
            ["code"] = [ex.Code],
            ["reasonClass"] = [ex.ReasonClass]
        });
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.PreferencesWrite);

userPreferences.MapPatch("/{id:guid}", async (Guid id, UserPreferenceArchiveBody request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.ArchiveUserPreferenceAsync(new UserPreferenceArchiveRequest(id, request.Archived), cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.PreferencesWrite);

var me = app.MapGroup("/api/me");
me.RequireAuthIfEnabled(requireAuthentication);
me.MapGet(string.Empty, (IRequestActorAccessor actorAccessor) =>
{
    var actor = actorAccessor.Current;
    return actor.HasUser
        ? Results.Ok(new CurrentUserResult(
            actor.TenantId!.Value,
            actor.UserId!.Value,
            actor.Username,
            actor.Username,
            string.Empty,
            actor.Role ?? TenantUserRole.Member))
        : Results.Unauthorized();
});

me.MapGet("/tokens", async (
    bool? includeRevoked,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListMyTokensAsync(includeRevoked ?? false, cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.TokenManage);

me.MapPost("/tokens", async (
    ApiTokenCreateBody request,
    ITenantSecurityService service,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    try
    {
        var actor = actorAccessor.Current;
        if (!actor.HasUser)
        {
            return Results.Unauthorized();
        }

        var result = await service.CreateMyTokenAsync(
            new ApiTokenCreateRequest(
                actor.TenantId!.Value,
                actor.UserId!.Value,
                request.Name,
                request.Notes,
                request.Scopes,
                request.AllowedProjectIds,
                request.ExpiresAt),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] });
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.TokenManage);

me.MapPatch("/tokens/{tokenId:guid}", async (
    Guid tokenId,
    ApiTokenUpdateRequest request,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.UpdateMyTokenAsync(tokenId, request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] });
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.TokenManage);

me.MapPost("/tokens/{tokenId:guid}/revoke", async (
    Guid tokenId,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.RevokeMyTokenAsync(tokenId, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] });
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.TokenManage);

me.MapPost("/tokens/{tokenId:guid}/regenerate", async (
    Guid tokenId,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.RegenerateMyTokenAsync(tokenId, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] });
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.TokenManage);

var security = app.MapGroup("/api/security");
security.RequireAuthIfEnabled(requireAuthentication);
if (requireAuthentication)
{
    security.AddEndpointFilter(async (context, next) =>
    {
        var actor = context.HttpContext.RequestServices.GetRequiredService<IRequestActorAccessor>().Current;
        return actor.IsAdmin ? await next(context) : Results.Forbid();
    });
}
security.MapGet("/tenants", async (
    bool? includeArchived,
    int? limit,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListTenantsAsync(includeArchived ?? false, limit ?? 100, cancellationToken);
    return Results.Ok(result);
});

security.MapPost("/tenants", async (
    TenantCreateRequest request,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.CreateTenantAsync(request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["tenant"] = [ex.Message] });
    }
});

security.MapGet("/tenants/{tenantId:guid}/users", async (
    Guid tenantId,
    bool? includeArchived,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListUsersAsync(tenantId, includeArchived ?? false, cancellationToken);
    return Results.Ok(result);
});

security.MapPost("/tenants/{tenantId:guid}/users", async (
    Guid tenantId,
    TenantUserCreateBody request,
    ITenantSecurityService service,
    IPasswordHasher<object> passwordHasher,
    CancellationToken cancellationToken) =>
{
    try
    {
        var passwordHash = string.IsNullOrWhiteSpace(request.Password)
            ? string.Empty
            : passwordHasher.HashPassword(new object(), request.Password);
        var result = await service.CreateUserAsync(
            new TenantUserCreateRequest(
                tenantId,
                request.Username,
                request.DisplayName,
                request.Email,
                request.Role,
                passwordHash),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["user"] = [ex.Message] });
    }
});

security.MapPatch("/users/{userId:guid}", async (
    Guid userId,
    TenantUserUpdateBody request,
    ITenantSecurityService service,
    IPasswordHasher<object> passwordHasher,
    CancellationToken cancellationToken) =>
{
    try
    {
        var passwordHash = request.Password is null
            ? null
            : string.IsNullOrWhiteSpace(request.Password)
                ? string.Empty
                : passwordHasher.HashPassword(new object(), request.Password);
        var result = await service.UpdateUserAsync(
            userId,
            new TenantUserUpdateRequest(
                request.DisplayName,
                request.Email,
                request.Role,
                request.Status,
                passwordHash),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["user"] = [ex.Message] });
    }
});

security.MapGet("/tenants/{tenantId:guid}/project-grants", async (
    Guid tenantId,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListProjectGrantsAsync(tenantId, cancellationToken);
    return Results.Ok(result);
});

security.MapPut("/tenants/{tenantId:guid}/project-grants/{projectId}", async (
    Guid tenantId,
    string projectId,
    TenantProjectGrantUpsertBody request,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.UpsertProjectGrantAsync(
            new TenantProjectGrantUpsertRequest(
                tenantId,
                projectId,
                request.CanRead,
                request.CanWrite,
                request.CanManageTokens),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["projectGrant"] = [ex.Message] });
    }
});

security.MapGet("/tenants/{tenantId:guid}/tokens", async (
    Guid tenantId,
    bool? includeRevoked,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListTokensAsync(tenantId, includeRevoked ?? false, cancellationToken);
    return Results.Ok(result);
});

security.MapPost("/tenants/{tenantId:guid}/tokens", async (
    Guid tenantId,
    ApiTokenCreateBody request,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.CreateTokenAsync(
            new ApiTokenCreateRequest(
                tenantId,
                request.OwnerUserId,
                request.Name,
                request.Notes,
                request.Scopes,
                request.AllowedProjectIds,
                request.ExpiresAt),
            cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] });
    }
});

security.MapPatch("/tokens/{tokenId:guid}", async (
    Guid tokenId,
    ApiTokenUpdateRequest request,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.UpdateTokenAsync(tokenId, request, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] });
    }
});

security.MapPost("/tokens/{tokenId:guid}/revoke", async (
    Guid tokenId,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.RevokeTokenAsync(tokenId, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] });
    }
});

security.MapPost("/tokens/{tokenId:guid}/regenerate", async (
    Guid tokenId,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.RegenerateTokenAsync(tokenId, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["token"] = [ex.Message] });
    }
});

security.MapGet("/audit-events", async (
    Guid? tenantId,
    int? limit,
    ITenantSecurityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListAuditEventsAsync(tenantId, limit ?? 100, cancellationToken);
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
}).RequireAuthIfEnabled(requireAuthentication).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);

app.MapGet("/api/context/bootstrap", (
    string? projectId,
    IContextHubBootstrapService service) =>
{
    var result = service.Describe(new ContextHubBootstrapRequest(projectId));
    return Results.Ok(result);
}).RequireAuthIfEnabled(requireAuthentication);

var sources = app.MapGroup("/api/sources");
sources.RequireAuthIfEnabled(requireAuthentication);
sources.RequireAdminIfEnabled(requireAuthentication);
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
governance.RequireAuthIfEnabled(requireAuthentication);
governance.RequireAdminIfEnabled(requireAuthentication);
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

governance.MapPost("/findings/disposition", async (GovernanceFindingDispositionRequest request, IGovernanceService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.SetDispositionAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["finding"] = [ex.Message] });
    }
});

governance.MapPost("/findings/reopen", async (GovernanceFindingReopenRequest request, IGovernanceService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.ReopenAsync(request, cancellationToken));
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["finding"] = [ex.Message] });
    }
});

var evaluation = app.MapGroup("/api/evaluation");
evaluation.RequireAuthIfEnabled(requireAuthentication);
evaluation.RequireAdminIfEnabled(requireAuthentication);
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
actions.RequireAuthIfEnabled(requireAuthentication);
actions.RequireAdminIfEnabled(requireAuthentication);
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
conversations.RequireAuthIfEnabled(requireAuthentication);
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
    int? offset,
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
        new ConversationInsightListRequest(projectId, conversationId, parsedPromotionStatus, parsedInsightType, limit ?? 100, offset ?? 0),
        cancellationToken);
    return Results.Ok(result);
});

conversations.MapGet("/insights/{insightId:guid}", async (Guid insightId, IConversationAutomationService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetInsightAsync(insightId, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);

conversations.MapPost("/insights/{insightId:guid}/retry", async (Guid insightId, ConversationInsightGovernanceBody body, IConversationAutomationService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.RetryInsightAsync(new ConversationInsightGovernanceRequest(insightId, body.GovernanceRunId, body.Reason), cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["insight"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

conversations.MapPost("/insights/{insightId:guid}/skip", async (Guid insightId, ConversationInsightGovernanceBody body, IConversationAutomationService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.SkipInsightAsync(new ConversationInsightGovernanceRequest(insightId, body.GovernanceRunId, body.Reason), cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["insight"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

conversations.MapPost("/insights/{insightId:guid}/disposition", async (Guid insightId, ConversationInsightDispositionBody body, IConversationAutomationService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.SetInsightDispositionAsync(new ConversationInsightDispositionRequest(insightId, body.Disposition, body.Reason, body.GovernanceRunId), cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["insight"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

conversations.MapGet("/checkpoints/search", async (
    string? query,
    string? projectId,
    string? conversationId,
    int? limit,
    IConversationAutomationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.SearchCheckpointsAsync(
        new ConversationCheckpointSearchRequest(query, projectId, conversationId, limit ?? 20),
        cancellationToken);
    return Results.Ok(result);
}).RequireAdminIfEnabled(requireAuthentication);

var discussions = app.MapGroup("/api/discussions");
discussions.RequireAuthIfEnabled(requireAuthentication);
discussions.MapPost("/threads", async (DiscussionThreadCreateRequest request, IProjectDiscussionService service, CancellationToken cancellationToken) =>
{
    try { return Results.Created($"/api/discussions/threads", await service.CreateThreadAsync(request, cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["discussion"] = [ex.Message] }); }
});
discussions.MapGet("/threads", async (string? projectId, string? hostProjectId, string? status, int? limit, bool? includeArchived, IProjectDiscussionService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ListThreadsAsync(new DiscussionThreadListRequest(projectId, hostProjectId, status, limit ?? 50, includeArchived ?? false), cancellationToken)));
discussions.MapGet("/threads/{threadId:guid}", async (Guid threadId, string? readerProjectId, IProjectDiscussionService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetThreadAsync(threadId, readerProjectId, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
discussions.MapPost("/threads/{threadId:guid}/close", async (Guid threadId, IProjectDiscussionService service, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.CloseThreadAsync(threadId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["discussion"] = [ex.Message] }); }
});
discussions.MapPost("/threads/{threadId:guid}/archive", async (Guid threadId, IProjectDiscussionService service, CancellationToken cancellationToken) =>
{
    var result = await service.SetThreadArchivedAsync(threadId, archived: true, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
discussions.MapPost("/threads/{threadId:guid}/restore", async (Guid threadId, IProjectDiscussionService service, CancellationToken cancellationToken) =>
{
    var result = await service.SetThreadArchivedAsync(threadId, archived: false, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
});
discussions.MapPost("/threads/{threadId:guid}/read", async (Guid threadId, DiscussionThreadReadBody body, IProjectDiscussionService service, CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.AdvanceThreadReadCursorAsync(threadId, body.ReaderProjectId, body.LastReadMessageId, cancellationToken);
        return result is null ? Results.NotFound() : Results.Ok(result);
    }
    catch (ArgumentException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["lastReadMessageId"] = [exception.Message] });
    }
    catch (InvalidOperationException exception)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["discussion"] = [exception.Message] });
    }
});
discussions.MapPost("/threads/{threadId:guid}/messages", async (Guid threadId, DiscussionMessageCreateBody body, IProjectDiscussionService service, CancellationToken cancellationToken) =>
{
    try { return Results.Created($"/api/discussions/threads/{threadId:D}/messages", await service.AddMessageAsync(new DiscussionMessageCreateRequest(threadId, body.SenderProjectId, body.Content), cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["discussion"] = [ex.Message] }); }
});

var workItems = app.MapGroup("/api/work-items");
workItems.RequireAuthIfEnabled(requireAuthentication);
workItems.MapGet(string.Empty, async (string projectId, string? status, string? definitionState, int? limit, bool? includeArchived, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    if (!EnumParser.TryParse(status, out ProjectWorkItemStatus? parsedStatus, out var error))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [error ?? "Unsupported ProjectWorkItemStatus value."] });
    }
    if (!EnumParser.TryParse(definitionState, out ProjectWorkItemDefinitionState? parsedDefinitionState, out var definitionError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["definitionState"] = [definitionError ?? "Unsupported ProjectWorkItemDefinitionState value."] });
    }
    return Results.Ok(await service.ListAsync(new ProjectWorkItemListRequest(ProjectContext.Normalize(projectId), parsedStatus, limit ?? 100, includeArchived ?? false, DefinitionState: parsedDefinitionState), cancellationToken));
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
workItems.MapPost(string.Empty, async (ProjectWorkItemCreateRequest request, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    try { return Results.Created("/api/work-items", await service.CreateAsync(request, cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["workItem"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
workItems.MapPut("/{id:guid}", async (Guid id, ProjectWorkItemUpdateBody body, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.UpdateAsync(new ProjectWorkItemUpdateRequest(id, body.Title, body.Description, body.Tags, body.Status, body.Priority, body.DueAt, body.DefinitionState), cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["workItem"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
workItems.MapPost("/{id:guid}/archive", async (Guid id, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.SetArchivedAsync(id, archived: true, cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["workItem"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
workItems.MapPost("/{id:guid}/restore", async (Guid id, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.SetArchivedAsync(id, archived: false, cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["workItem"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
workItems.MapPut("/{workItemId:guid}/checklist/{checklistItemId:guid}", async (Guid workItemId, Guid checklistItemId, ProjectWorkItemChecklistCompletionBody body, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.SetChecklistItemCompletionAsync(workItemId, checklistItemId, body.IsCompleted, cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["checklistItem"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
workItems.MapPut("/{workItemId:guid}/governance-exclusion", async (Guid workItemId, ProjectWorkItemGovernanceExclusionBody body, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.SetGovernanceExclusionAsync(new ProjectWorkItemGovernanceExclusionRequest(workItemId, body.ProjectId, body.GovernanceRunId, body.Reason, body.Excluded), cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["governanceExclusion"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

var agentExecutions = app.MapGroup("/api/agent-executions");
agentExecutions.RequireAuthIfEnabled(requireAuthentication);
agentExecutions.MapPost("/prepare", async (AgentExecutionPrepareRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => Results.Created("/api/agent-executions", await service.PrepareAsync(request, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsManage);
agentExecutions.MapPost("/claim-next", async (AgentExecutionClaimRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ClaimNextAsync(request, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsClaim);
agentExecutions.MapGet("/{executionId:guid}", async (Guid executionId, IAgentExecutionService service, CancellationToken cancellationToken)
    => await service.GetAsync(executionId, cancellationToken) is { } execution ? Results.Ok(execution) : Results.NotFound())
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsRead);
agentExecutions.MapGet(string.Empty, async (string projectId, string? status, int? limit, int? offset, IAgentExecutionService service, CancellationToken cancellationToken) =>
{
    if (!EnumParser.TryParse(status, out AgentExecutionStatus? parsedStatus, out var error))
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [error ?? "Unsupported AgentExecutionStatus value."] });
    return Results.Ok(await service.ListAsync(new AgentExecutionListRequest(projectId, parsedStatus, limit ?? 100, offset ?? 0), cancellationToken));
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsRead);
agentExecutions.MapGet("/dashboard/{projectId}", async (string projectId, IAgentExecutionService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetDashboardAsync(projectId, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsRead);
agentExecutions.MapPost("/{executionId:guid}/heartbeat", async (Guid executionId, AgentExecutionLeaseRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => executionId == request.ExecutionId ? Results.Ok(await service.HeartbeatAsync(request, cancellationToken)) : Results.BadRequest("Route and body ExecutionId must match."))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsWrite);
agentExecutions.MapPost("/{executionId:guid}/checkpoints", async (Guid executionId, AgentExecutionCheckpointRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => executionId == request.ExecutionId ? Results.Ok(await service.CheckpointAsync(request, cancellationToken)) : Results.BadRequest("Route and body ExecutionId must match."))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsWrite);
agentExecutions.MapPost("/{executionId:guid}/block", async (Guid executionId, AgentExecutionTerminalRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => executionId == request.ExecutionId ? Results.Ok(await service.BlockAsync(request, cancellationToken)) : Results.BadRequest("Route and body ExecutionId must match."))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsWrite);
agentExecutions.MapPost("/{executionId:guid}/complete", async (Guid executionId, AgentExecutionTerminalRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => executionId == request.ExecutionId ? Results.Ok(await service.CompleteAsync(request, cancellationToken)) : Results.BadRequest("Route and body ExecutionId must match."))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsWrite);
agentExecutions.MapPost("/{executionId:guid}/fail", async (Guid executionId, AgentExecutionTerminalRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => executionId == request.ExecutionId ? Results.Ok(await service.FailAsync(request, cancellationToken)) : Results.BadRequest("Route and body ExecutionId must match."))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsWrite);
agentExecutions.MapPost("/{executionId:guid}/abandon", async (Guid executionId, AgentExecutionTerminalRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => executionId == request.ExecutionId ? Results.Ok(await service.AbandonAsync(request, cancellationToken)) : Results.BadRequest("Route and body ExecutionId must match."))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsWrite);
agentExecutions.MapPost("/{executionId:guid}/cancel", async (Guid executionId, AgentExecutionCancelRequest request, IAgentExecutionService service, CancellationToken cancellationToken)
    => executionId == request.ExecutionId ? Results.Ok(await service.CancelAsync(request, cancellationToken)) : Results.BadRequest("Route and body ExecutionId must match."))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentExecutionsManage);

var skills = app.MapGroup("/api/skills");
skills.RequireAuthIfEnabled(requireAuthentication);
skills.MapGet(string.Empty, async (string? projectId, bool? includeArchived, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ListAsync(projectId, includeArchived ?? false, cancellationToken)));
skills.MapGet("/{skillId:guid}", async (Guid skillId, ISkillService service, CancellationToken cancellationToken)
    => await service.GetAsync(skillId, cancellationToken) is { } skill ? Results.Ok(skill) : Results.NotFound());
skills.MapPost("/import/preview", async (SkillImportPreviewRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.PreviewImportAsync(request, cancellationToken)));
skills.MapPost("/import", async (SkillImportRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Created("/api/skills", await service.ImportAsync(request, cancellationToken)));
skills.MapPost("/versions/{skillVersionId:guid}/publish", async (Guid skillVersionId, SkillPublishRequest request, ISkillService service, CancellationToken cancellationToken) =>
{
    if (skillVersionId != request.SkillVersionId) return Results.BadRequest("Route and body SkillVersionId must match.");
    return Results.Ok(await service.PublishAsync(request, cancellationToken));
});
skills.MapPost("/versions/{skillVersionId:guid}/lifecycle", async (Guid skillVersionId, SkillLifecycleRequest request, ISkillService service, CancellationToken cancellationToken) =>
{
    if (skillVersionId != request.SkillVersionId) return Results.BadRequest("Route and body SkillVersionId must match.");
    return Results.Ok(await service.ChangeLifecycleAsync(request, cancellationToken));
});
skills.MapPut("/{skillId:guid}/default", async (Guid skillId, SkillDefaultVersionRequest request, ISkillService service, CancellationToken cancellationToken) =>
{
    if (skillId != request.SkillId) return Results.BadRequest("Route and body SkillId must match.");
    return Results.Ok(await service.SetDefaultVersionAsync(request, cancellationToken));
});
skills.MapPut("/{skillId:guid}/bindings", async (Guid skillId, SkillBindingUpsertRequest request, ISkillService service, CancellationToken cancellationToken) =>
{
    if (skillId != request.SkillId) return Results.BadRequest("Route and body SkillId must match.");
    return Results.Ok(await service.UpsertBindingAsync(request, cancellationToken));
});
skills.MapGet("/versions/{skillVersionId:guid}/export", async (Guid skillVersionId, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ExportAsync(skillVersionId, cancellationToken)));
skills.MapGet("/{skillId:guid}/versions/diff", async (Guid skillId, Guid leftVersionId, Guid rightVersionId, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.DiffVersionsAsync(skillId, leftVersionId, rightVersionId, cancellationToken)));
skills.MapGet("/{skillId:guid}/source-observations", async (Guid skillId, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ListSourceObservationsAsync(skillId, cancellationToken)));
skills.MapPost("/{skillId:guid}/source-observations", async (Guid skillId, SkillSourceObservationRequest request, ISkillService service, CancellationToken cancellationToken) =>
{
    if (skillId != request.SkillId) return Results.BadRequest("Route and body SkillId must match.");
    return Results.Ok(await service.RecordSourceObservationAsync(request, cancellationToken));
});
skills.MapPost("/reindex", async (SkillReindexRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ReindexAsync(request, cancellationToken)));
skills.MapGet("/search-generations", async (ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ListSearchGenerationsAsync(cancellationToken)));
skills.MapPost("/search-generations/{generationId:guid}/activate", async (Guid generationId, SkillSearchGenerationActivateRequest request, ISkillService service, CancellationToken cancellationToken) =>
{
    if (generationId != request.GenerationId) return Results.BadRequest("Route and body GenerationId must match.");
    return Results.Ok(await service.ActivateSearchGenerationAsync(request, cancellationToken));
});
skills.MapPost("/analytics", async (SkillAnalyticsRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetAnalyticsAsync(request, cancellationToken)));
skills.MapPost("/analytics/trend", async (SkillAnalyticsRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetAnalyticsTrendAsync(request, cancellationToken)));
skills.MapPost("/analytics/reconcile", async (SkillTelemetryReconciliationRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ReconcileTelemetryAsync(request, cancellationToken)));
skills.MapPost("/governance/review", async (SkillMetadataGovernancePolicy request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ReviewMetadataGovernanceAsync(request, cancellationToken)));
skills.MapGet("/governance/proposals", async (string? status, ISkillService service, CancellationToken cancellationToken) =>
{
    SkillMetadataProposalStatus? parsed = null;
    if (!string.IsNullOrWhiteSpace(status))
    {
        if (!Enum.TryParse<SkillMetadataProposalStatus>(status, true, out var value)) return Results.BadRequest("Unsupported proposal status.");
        parsed = value;
    }
    return Results.Ok(await service.ListMetadataProposalsAsync(parsed, cancellationToken));
});
skills.MapPost("/governance/proposals/{proposalId:guid}/decision", async (Guid proposalId, SkillMetadataProposalDecisionRequest request, ISkillService service, CancellationToken cancellationToken) =>
{
    if (proposalId != request.ProposalId) return Results.BadRequest("Route and body ProposalId must match.");
    return Results.Ok(await service.DecideMetadataProposalAsync(request, cancellationToken));
});
skills.MapPost("/search-for-execution", async (SkillSearchForExecutionRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.SearchForExecutionAsync(request, cancellationToken)));
skills.MapGet("/resolutions", async (string? projectId, int? limit, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ListResolutionsAsync(projectId, limit ?? 50, cancellationToken)));
skills.MapPost("/resolution-feedback", async (SkillResolutionFeedbackRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.RecordFeedbackAsync(request, cancellationToken)));
skills.MapPost("/select-for-execution", async (SkillSelectForExecutionRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.SelectAsync(request, cancellationToken)));
skills.MapPost("/version-get", async (SkillVersionGetRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetPinnedVersionAsync(request, cancellationToken)));
skills.MapPost("/materialize", async (SkillMaterializeRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.MaterializeAsync(request, cancellationToken)));
skills.MapPost("/materializations/cleanup", async (SkillMaterializationCleanupRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.CleanupMaterializationsAsync(request, cancellationToken)));
skills.MapPost("/invocations", async (SkillInvocationRecordRequest request, ISkillService service, CancellationToken cancellationToken)
    => Results.Ok(await service.RecordInvocationAsync(request, cancellationToken)));

var knowledgeReviews = app.MapGroup("/api/knowledge-reviews");
knowledgeReviews.RequireAuthIfEnabled(requireAuthentication);
knowledgeReviews.MapPost(string.Empty, async (KnowledgeReviewRequest request, IKnowledgeReviewService service, CancellationToken cancellationToken)
    => Results.Ok(await service.ReviewAsync(request, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
knowledgeReviews.MapPost("/execute", async (GovernanceBatchExecuteRequest request, IGovernanceBatchExecutor service, CancellationToken cancellationToken)
    =>
    {
        try
        {
            var result = await service.ExecuteAsync(request, cancellationToken);
            return result.Succeeded
                ? Results.Ok(result)
                : Results.Problem(
                    statusCode: StatusCodes.Status409Conflict,
                    title: "Governance batch continuation rejected",
                    detail: result.StoppedReason,
                    extensions: new Dictionary<string, object?> { ["code"] = result.ErrorCode.ToString() });
        }
        catch (GovernanceBatchException ex)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "Governance batch continuation rejected",
                detail: ex.Message,
                extensions: new Dictionary<string, object?> { ["code"] = ex.Code.ToString() });
        }
        catch (InvalidOperationException ex)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]> { ["governanceBatch"] = [ex.Message] });
        }
    })
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite)
    .RequireAdminIfEnabled(requireAuthentication);
knowledgeReviews.MapGet("/tombstones/{resourceId:guid}", async (Guid resourceId, string? projectId, IAutonomousRetentionService service, CancellationToken cancellationToken) =>
    {
        var tombstone = await service.GetTombstoneAsync(resourceId, projectId, cancellationToken);
        return tombstone is null ? Results.NotFound() : Results.Ok(tombstone);
    })
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);

var projectHierarchy = app.MapGroup("/api/projects/hierarchy");
projectHierarchy.RequireAuthIfEnabled(requireAuthentication);
projectHierarchy.MapGet("/{parentProjectId}", async (string parentProjectId, IProjectDiscussionService service, CancellationToken cancellationToken)
    => Results.Ok(await service.GetChildrenAsync(parentProjectId, cancellationToken)));
projectHierarchy.MapPut("/{parentProjectId}", async (string parentProjectId, ProjectHierarchySetChildrenBody body, IProjectDiscussionService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.SetChildrenAsync(new ProjectHierarchySetChildrenRequest(parentProjectId, body.ChildProjectIds ?? []), cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["projectHierarchy"] = [ex.Message] }); }
});

conversations.MapGet("/checkpoints/{checkpointId:guid}/pipeline", async (
    Guid checkpointId,
    IConversationAutomationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.GetPipelineStatusAsync(checkpointId, cancellationToken);
    return result is null ? Results.NotFound() : Results.Ok(result);
}).RequireAdminIfEnabled(requireAuthentication);

conversations.MapPost("/checkpoints/{checkpointId:guid}/process", async (
    Guid checkpointId,
    IConversationAutomationService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.ProcessCheckpointNowAsync(checkpointId, cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["checkpoint"] = [ex.Message]
        });
    }
}).RequireAdminIfEnabled(requireAuthentication);

conversations.MapPost("/insights/promote", async (
    ConversationPromotionRetryRequest request,
    IConversationAutomationService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.RetryPromotionAsync(request, cancellationToken);
    return Results.Ok(result);
}).RequireAdminIfEnabled(requireAuthentication);

var chatGpt = app.MapGroup("/api/chatgpt");
chatGpt.RequireAuthIfEnabled(requireAuthentication);
chatGpt.MapGet("/proposals", async (
    string? projectId,
    string? status,
    int? limit,
    IChatGptProposalService service,
    CancellationToken cancellationToken) =>
{
    if (!EnumParser.TryParse(status, out ChatGptProposalStatus? parsedStatus, out var statusError))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["status"] = [statusError ?? "Unsupported ChatGptProposalStatus value."]
        });
    }

    var result = await service.ListAsync(new ChatGptProposalListRequest(projectId, parsedStatus, limit ?? 50), cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);

chatGpt.MapPost("/proposals/{proposalId:guid}/approve", async (
    Guid proposalId,
    ChatGptProposalDecisionBody body,
    IChatGptProposalService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ApproveAsync(new ChatGptProposalDecisionRequest(proposalId, body.Note ?? string.Empty), cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

chatGpt.MapPost("/proposals/{proposalId:guid}/reject", async (
    Guid proposalId,
    ChatGptProposalDecisionBody body,
    IChatGptProposalService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.RejectAsync(new ChatGptProposalDecisionRequest(proposalId, body.Note ?? string.Empty), cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

var jobs = app.MapGroup("/api/jobs");
jobs.RequireAuthIfEnabled(requireAuthentication);
jobs.RequireAdminIfEnabled(requireAuthentication);
jobs.MapGet(string.Empty, async (
    string? status,
    string? jobType,
    int? page,
    int? pageSize,
    IDashboardQueryService service,
    HttpContext httpContext,
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
    SetDataSource(httpContext, parsedStatus is null && parsedJobType is null && (page ?? 1) <= 1 ? "redis" : "cache");
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

var maintenance = app.MapGroup("/api/maintenance");
maintenance.RequireAuthIfEnabled(requireAuthentication);
maintenance.RequireAdminIfEnabled(requireAuthentication);
maintenance.MapGet("/status", async (IMaintenanceCoordinator coordinator, CancellationToken cancellationToken) =>
{
    var result = await coordinator.GetStatusAsync(cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/mode", async (
    MaintenanceModeRequest request,
    IMaintenanceModeStore store,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await store.EnableAsync(request, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapDelete("/mode", async (
    IMaintenanceModeStore store,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await store.DisableAsync(MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows", async (
    MaintenanceWindowRequest request,
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.ScheduleAsync(request, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows/{runId:guid}/drain", async (
    Guid runId,
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.StartDrainAsync(runId, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows/current/drain", async (
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.StartDrainAsync(null, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows/{runId:guid}/start", async (
    Guid runId,
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.StartRunningAsync(runId, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows/current/start", async (
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.StartRunningAsync(null, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows/{runId:guid}/complete", async (
    Guid runId,
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.CompleteAsync(runId, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows/current/complete", async (
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.CompleteAsync(null, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows/{runId:guid}/cancel", async (
    Guid runId,
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.CancelAsync(runId, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/windows/current/cancel", async (
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.CancelAsync(null, MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor), cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/leases/heartbeat", async (
    MaintenanceLeaseHeartbeatRequest request,
    IMaintenanceCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.HeartbeatLeaseAsync(request, cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/leases/complete", async (
    MaintenanceLeaseCompleteRequest request,
    IMaintenanceCoordinator coordinator,
    CancellationToken cancellationToken) =>
{
    var result = await coordinator.CompleteLeaseAsync(request, cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/retrieval-telemetry-retention/run", async (
    RetrievalTelemetryRetentionRunRequest request,
    IRetrievalTelemetryRetentionService service,
    IRequestActorAccessor actorAccessor,
    IHostApplicationLifetime applicationLifetime) =>
{
    var result = await service.RunAsync(
        request,
        MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor),
        applicationLifetime.ApplicationStopping);
    return Results.Ok(result);
});

maintenance.MapPost("/vacuum-full-reclaim/run", async (
    VacuumFullReclaimRunRequest request,
    IVacuumFullReclaimService service,
    IMaintenanceCoordinator coordinator,
    IRequestActorAccessor actorAccessor,
    IHostApplicationLifetime applicationLifetime) =>
{
    var maintenanceStatus = await coordinator.GetStatusAsync(applicationLifetime.ApplicationStopping);
    if (maintenanceStatus.Phase != MaintenancePhase.Running)
    {
        return Results.Problem(
            title: "ContextHub maintenance mode is required.",
            detail: "Run /api/maintenance/windows/current/start or /api/maintenance/mode before starting VACUUM FULL reclaim.",
            statusCode: StatusCodes.Status409Conflict,
            extensions: new Dictionary<string, object?>
            {
                ["phase"] = maintenanceStatus.Phase.ToString(),
                ["runId"] = maintenanceStatus.RunId
            });
    }

    var result = await service.RunAsync(
        string.IsNullOrWhiteSpace(request.TriggeredBy) ? MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor) : request.TriggeredBy,
        applicationLifetime.ApplicationStopping);
    return Results.Ok(result);
});

maintenance.MapPost("/memory-data-retention/run", async (
    MemoryDataRetentionRunRequest request,
    IMemoryDataRetentionService service,
    IRequestActorAccessor actorAccessor,
    IHostApplicationLifetime applicationLifetime) =>
{
    try
    {
        var result = await service.RunAsync(
            request,
            MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor),
            applicationLifetime.ApplicationStopping);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["memoryDataRetention"] = [ex.Message] });
    }
});

maintenance.MapPost("/domain-owner-repair/preview", async (
    DomainOwnerRepairRequest request,
    IDomainOwnerRepairService service,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await service.RunAsync(
        request with { Apply = false },
        MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor),
        cancellationToken);
    return Results.Ok(result);
});

maintenance.MapPost("/domain-owner-repair/run", async (
    DomainOwnerRepairRequest request,
    IDomainOwnerRepairService service,
    IRequestActorAccessor actorAccessor,
    CancellationToken cancellationToken) =>
{
    var result = await service.RunAsync(
        request with { Apply = true },
        MaintenanceApiHelpers.ResolveTriggeredBy(actorAccessor),
        cancellationToken);
    return result.Conflicts.Count > 0
        ? Results.Conflict(result)
        : Results.Ok(result);
});

maintenance.MapGet("/runs", async (
    int? limit,
    IMaintenanceRunQueryService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ListRunsAsync(limit ?? 100, cancellationToken);
    return Results.Ok(result);
});

var transfers = app.MapGroup("/api/transfers");
transfers.RequireAuthIfEnabled(requireAuthentication);

transfers.MapPost("/uploads", async (CreateManagedUploadRequest request, IManagedTransferService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.CreateUploadAsync(request, cancellationToken)));

transfers.MapPost("/downloads", async (CreateManagedDownloadRequest request, IManagedTransferService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.CreateDownloadAsync(request, cancellationToken)));

transfers.MapPut("/{sessionId:guid}/chunks/{chunkIndex:int}", async (
    Guid sessionId,
    int chunkIndex,
    HttpRequest httpRequest,
    IManagedTransferService service,
    IOptions<ManagedTransferOptions> transferOptions,
    CancellationToken cancellationToken) =>
{
    var contentLength = httpRequest.ContentLength;
    if (!contentLength.HasValue || contentLength <= 0 || contentLength > transferOptions.Value.NormalizedChunkBytes)
    {
        return Results.BadRequest(new { error = "Chunk Content-Length is required and exceeds no configured transfer boundary." });
    }
    var buffer = new byte[checked((int)contentLength.Value)];
    try
    {
        await httpRequest.Body.ReadExactlyAsync(buffer, cancellationToken);
        if (await httpRequest.Body.ReadAsync(new byte[1], cancellationToken) != 0)
        {
            return Results.BadRequest(new { error = "Chunk body exceeds its declared boundary." });
        }
        var result = await service.UploadChunkAsync(new ManagedChunkWriteRequest(
            sessionId,
            RequireTransferHeader(httpRequest, "X-ContextHub-Capability"),
            RequireTransferRevision(httpRequest),
            RequireTransferHeader(httpRequest, "X-Request-Id"),
            chunkIndex,
            buffer,
            RequireTransferHeader(httpRequest, "X-Content-SHA256")), cancellationToken);
        buffer = [];
        return Results.Ok(result);
    }
    finally
    {
        if (buffer.Length > 0) CryptographicOperations.ZeroMemory(buffer);
    }
});

transfers.MapPost("/{sessionId:guid}/complete", async (
    Guid sessionId,
    HttpRequest request,
    IManagedTransferService service,
    CancellationToken cancellationToken) => Results.Ok(await service.CompleteUploadAsync(
        sessionId,
        RequireTransferHeader(request, "X-ContextHub-Capability"),
        RequireTransferRevision(request),
        RequireTransferHeader(request, "X-Request-Id"),
        cancellationToken)));

transfers.MapGet("/{sessionId:guid}/content", async (
    Guid sessionId,
    long offset,
    int length,
    HttpContext context,
    IManagedTransferService service,
    CancellationToken cancellationToken) =>
{
    context.Response.ContentType = "application/octet-stream";
    context.Response.ContentLength = length;
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    await service.CopyRangeAsync(new ManagedRangeReadRequest(
        sessionId,
        RequireTransferHeader(context.Request, "X-ContextHub-Capability"),
        RequireTransferRevision(context.Request),
        RequireTransferHeader(context.Request, "X-Request-Id"),
        offset,
        length), context.Response.Body, cancellationToken);
});

transfers.MapDelete("/{sessionId:guid}", async (Guid sessionId, IManagedTransferService service, CancellationToken cancellationToken) =>
{
    await service.RevokeAsync(sessionId, cancellationToken);
    return Results.NoContent();
});

var files = app.MapGroup("/api/files");
files.RequireAuthIfEnabled(requireAuthentication);
files.MapPost(string.Empty, async (CreateManagedFileRequest request, IManagedFileService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.CreateAsync(request, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
files.MapGet("/search", async (string projectId, string query, int? limit, IManagedFileService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.SearchAsync(projectId, query, limit ?? 20, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
files.MapPost("/{fileVersionId:guid}/security-assessments", async (Guid fileVersionId, FileSecurityAssessment request, IManagedFileService service, CancellationToken cancellationToken) =>
{
    var result = await service.ApplySecurityAssessmentAsync(fileVersionId, request, cancellationToken);
    return Results.Ok(ToFileVersionContract(result));
})
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecurityManage);
files.MapPost("/{fileVersionId:guid}/authorize", async (Guid fileVersionId, FileOperationBody request, IManagedFileService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.AuthorizeOperationAsync(fileVersionId, request.Operation, request.Purpose, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
files.MapPost("/{fileVersionId:guid}/representations", async (Guid fileVersionId, CreateFileRepresentationBody request, IManagedFileService service, CancellationToken cancellationToken) =>
{
    var result = await service.AddRepresentationAsync(new CreateFileRepresentationRequest(fileVersionId, request.Kind, request.ContentHash, request.Classification), cancellationToken);
    return Results.Ok(new { representationId = result.Id, result.FileVersionId, result.Kind, result.Classification, result.SourceClassificationRevision, result.CreatedAt, result.InvalidatedAt });
})
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
files.MapPost("/{fileVersionId:guid}/quarantine-release", async (Guid fileVersionId, QuarantineReleaseBody request, IManagedFileService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RequestQuarantineReleaseAsync(new QuarantineReleaseRequest(fileVersionId, request.Reason, request.ExternalApprovalReference, request.ExpectedClassificationRevision), cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecurityManage);
files.MapPost("/{fileId:guid}/delete", async (Guid fileId, FileDeleteBody request, IManagedFileService service, CancellationToken cancellationToken) =>
{
    var result = await service.RequestDeleteAsync(new FileDeleteRequest(fileId, request.Reason, request.RequestId), cancellationToken);
    return Results.Ok(new
    {
        result.RequiresUserDecision,
        result.State,
        records = result.Records.Select(record => new { deletionId = record.Id, record.FileAssetId, record.State, record.LegalHold, record.ReferenceBlocked, record.RetainUntil, record.AttemptCount, record.CreatedAt, record.UpdatedAt, record.PrimaryDeletedAt, record.FullyExpiredAt, record.VerifiedAt })
    });
})
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);

var stepUpAuthentication = app.MapGroup("/api/step-up");
stepUpAuthentication.RequireAuthIfEnabled(requireAuthentication);
stepUpAuthentication.MapPost("/password", async (PasswordStepUpBody request, IStepUpAuthenticationService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.CreatePasswordAssertionAsync(new PasswordStepUpRequest(
            request.Password, request.Purpose, request.ResourceType, request.ResourceId, request.MaxUses), cancellationToken));
    }
    catch (UnauthorizedAccessException)
    {
        return Results.Json(new { outcome = StepUpRequirementOutcome.RequiresStepUp, requiredAssurance = AssuranceLevel.Aal1, reasonCode = "PasswordVerificationFailed" }, statusCode: StatusCodes.Status401Unauthorized);
    }
});

var secrets = app.MapGroup("/api/secrets");
secrets.RequireAuthIfEnabled(requireAuthentication);
secrets.MapGet(string.Empty, async (string projectId, ISecretManagementService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.ListAsync(projectId, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsRead);
secrets.MapPost(string.Empty, async (SecretCreateBody request, ISecretManagementService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.CreateAsync(new CreateSecretRequest(request.ProjectId, request.Name, request.Kind, request.StepUp), cancellationToken));
    }
    catch (StepUpRequiredException exception)
    {
        return ToStepUpResult(exception.Decision);
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsManage);
secrets.MapPost("/{secretId:guid}/versions", async (
    Guid secretId,
    HttpRequest request,
    ISecretManagementService service,
    IOptions<SecretManagementOptions> secretOptions,
    CancellationToken cancellationToken) =>
{
    if (!request.ContentLength.HasValue || request.ContentLength <= 0 || request.ContentLength > secretOptions.Value.NormalizedMaxSecretBytes)
        return Results.BadRequest(new { error = "Secret Content-Length is required and exceeds no configured boundary." });
    var material = new byte[checked((int)request.ContentLength.Value)];
    try
    {
        await request.Body.ReadExactlyAsync(material, cancellationToken);
        if (await request.Body.ReadAsync(new byte[1], cancellationToken) != 0) return Results.BadRequest(new { error = "Secret body exceeds its declared boundary." });
        var result = await service.AddVersionAsync(new AddSecretVersionRequest(
            secretId,
            material,
            RequireLongHeader(request, "X-Expected-Secret-Revision"),
            RequireTransferHeader(request, "X-Request-Id"),
            TryReadStepUpProof(request),
            request.Headers["X-External-Approval"].FirstOrDefault(),
            TryReadDateTimeHeader(request, "X-Secret-Expires-At")), cancellationToken);
        CryptographicOperations.ZeroMemory(material);
        material = [];
        return Results.Ok(result);
    }
    catch (StepUpRequiredException exception)
    {
        return ToStepUpResult(exception.Decision);
    }
    finally
    {
        if (material.Length > 0) CryptographicOperations.ZeroMemory(material);
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsManage);
secrets.MapPost("/{secretId:guid}/leases", async (Guid secretId, SecretLeaseCreateBody request, ISecretManagementService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.CreateLeaseAsync(new CreateSecretLeaseRequest(secretId, request.Kind, request.Purpose, request.Target,
            request.RequestId, request.ExpectedSecretRevision, request.MaxUses, request.MaxConcurrency,
            request.TtlSeconds.HasValue ? TimeSpan.FromSeconds(request.TtlSeconds.Value) : null, request.ExecutionId, request.StepUp), cancellationToken));
    }
    catch (StepUpRequiredException exception)
    {
        return ToStepUpResult(exception.Decision);
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsUse);
secrets.MapPost("/{secretId:guid}/revoke", async (Guid secretId, SecretRevokeBody request, ISecretManagementService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RevokeAsync(secretId, request.ExpectedRevision, request.ExternalApprovalReference, request.RequestId, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsManage);
secrets.MapPost("/ssh/sign-authentication", async (SshSignAuthenticationBody request, ISecretManagementService service, CancellationToken cancellationToken) =>
{
    byte[] challenge;
    try { challenge = Convert.FromBase64String(request.ChallengeBase64); }
    catch (FormatException) { return Results.BadRequest(new { error = "ChallengeBase64 is invalid." }); }
    try
    {
        var result = await service.SignSshAuthenticationAsync(new SshSignerRequest(request.LeaseId, request.Capability,
            request.ExpectedRevision, request.RequestId, request.SessionId, request.Host, request.Port, request.User, challenge), cancellationToken);
        return Results.Ok(new { signatureBase64 = Convert.ToBase64String(result.Signature), result.Algorithm, result.Lease });
    }
    finally
    {
        CryptographicOperations.ZeroMemory(challenge);
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsUse);

var sshCertificates = app.MapGroup("/api/ssh-certificates");
sshCertificates.RequireAuthIfEnabled(requireAuthentication);
sshCertificates.MapPost(string.Empty, async (SshCertificateIssueBody request, ISshCertificateService service, CancellationToken cancellationToken) =>
{
    try
    {
        return Results.Ok(await service.IssueAsync(new IssueSshCertificateRequest(request.CaSecretId, request.PublicKey, request.TargetHost,
            request.TargetPort, request.TargetUser, request.Purpose, request.RequestId, request.ExpectedSecretRevision, request.ExecutionId, request.StepUp), cancellationToken));
    }
    catch (StepUpRequiredException exception)
    {
        return ToStepUpResult(exception.Decision);
    }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsUse);
sshCertificates.MapPost("/{certificateLeaseId:guid}/renew", async (Guid certificateLeaseId, SshCertificateRenewBody request, ISshCertificateService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RenewAsync(new RenewSshCertificateRequest(certificateLeaseId, request.RenewalCapability, request.ExpectedRevision, request.RequestId), cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsUse);
sshCertificates.MapPost("/{certificateLeaseId:guid}/revoke", async (Guid certificateLeaseId, SshCertificateRevokeBody request, ISshCertificateService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.RevokeAsync(certificateLeaseId, request.ExpectedRevision, request.ExternalApprovalReference, request.Reason, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecretsManage);

var canonicalTags = app.MapGroup("/api/canonical-tags");
canonicalTags.RequireAuthIfEnabled(requireAuthentication);
canonicalTags.MapPost("/telemetry", async (RecordTagTelemetryRequest request, ICanonicalTagGovernanceService service, CancellationToken cancellationToken) =>
{
    await service.RecordAsync(request, cancellationToken);
    return Results.NoContent();
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
canonicalTags.MapPost("/{projectId}/reconcile", async (string projectId, DateOnly? day, ICanonicalTagGovernanceService service, CancellationToken cancellationToken) =>
    Results.Ok(new { processed = await service.ReconcileAsync(projectId, day, cancellationToken) }))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecurityManage);
canonicalTags.MapGet("/{projectId}/quality", async (string projectId, int? days, int? minimumSamples, ICanonicalTagGovernanceService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.GetQualityAsync(projectId, days ?? 30, Math.Clamp(minimumSamples ?? 20, 5, 10000), cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
canonicalTags.MapGet("/{projectId}/merge-preview", async (string projectId, Guid sourceId, Guid targetId, ICanonicalTagGovernanceService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.PreviewMergeAsync(projectId, sourceId, targetId, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
canonicalTags.MapPost("/{projectId}/merge", async (string projectId, CanonicalTagMergeBody request, ICanonicalTagGovernanceService service, CancellationToken cancellationToken) =>
{
    await service.ApplyMergeAsync(projectId, request.SourceId, request.TargetId, request.ExpectedSourceRevision, cancellationToken);
    return Results.NoContent();
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecurityManage);
canonicalTags.MapPost("/{projectId}/split-preview", async (string projectId, CanonicalTagSplitBody request, ICanonicalTagGovernanceService service, CancellationToken cancellationToken) =>
    Results.Ok(await service.PreviewSplitAsync(projectId, request.SourceId, request.CandidateResourceIds, cancellationToken)))
    .RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecurityManage);
canonicalTags.MapPost("/{projectId}/lifecycle", async (string projectId, CanonicalTagLifecycleRequest request, ICanonicalTagGovernanceService service, CancellationToken cancellationToken) =>
{
    await service.ApplyLifecycleAsync(projectId, request, cancellationToken);
    return Results.NoContent();
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.SecurityManage);

var storage = app.MapGroup("/api/storage");
storage.RequireAuthIfEnabled(requireAuthentication);
storage.RequireAdminIfEnabled(requireAuthentication);
storage.MapGet("/tables", async (IDashboardQueryService service, HttpContext httpContext, CancellationToken cancellationToken) =>
{
    var result = await service.GetStorageTablesAsync(cancellationToken);
    SetDataSource(httpContext, "redis");
    return Results.Ok(result);
});

storage.MapGet("/{table}", async (
    string table,
    string? query,
    string? column,
    int? page,
    int? pageSize,
    IDashboardQueryService service,
    HttpContext httpContext,
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
        SetDataSource(httpContext, result.DataSource);
        return Results.Ok(result);
    }
    catch (StorageExplorerQueryRejectedException ex)
    {
        return Results.BadRequest(new ProblemDetails
        {
            Title = "Storage query rejected.",
            Detail = ex.Message,
            Status = StatusCodes.Status400BadRequest
        });
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

static void SetDataSource(HttpContext httpContext, string source)
{
    httpContext.Response.Headers["X-ContextHub-Data-Source"] = source;
}

static bool RequiresToken(PathString path)
    => path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) ||
       path.StartsWithSegments("/mcp", StringComparison.OrdinalIgnoreCase);

static string RequireTransferHeader(HttpRequest request, string name)
{
    var value = request.Headers[name].ToString().Trim();
    if (string.IsNullOrWhiteSpace(value)) throw new BadHttpRequestException($"{name} is required.");
    return value;
}

static long RequireTransferRevision(HttpRequest request)
{
    var value = RequireTransferHeader(request, "X-Transfer-Revision");
    return long.TryParse(value, out var revision) && revision > 0
        ? revision
        : throw new BadHttpRequestException("X-Transfer-Revision must be a positive integer.");
}

static object ToFileVersionContract(FileVersion result) => new
{
    fileVersionId = result.Id,
    fileId = result.FileAssetId,
    result.VersionNumber,
    result.ContentSha256,
    result.ContentType,
    result.Lifecycle,
    result.Classification,
    result.ClassificationRevision,
    result.SearchProjectionAllowed,
    result.EmbeddingAllowed,
    result.NeedsRescan,
    result.LastScanAt,
    result.ScannerSetVersion,
    result.SecurityPolicyVersion,
    result.CreatedAt,
    result.UpdatedAt
};

app.MapPost("/api/performance/measure", async (PerformanceMeasureRequest request, IPerformanceProbeService service, CancellationToken cancellationToken) =>
{
    var errors = ApiValidation.ValidatePerformanceRequest(request);
    if (errors.Count > 0)
    {
        return Results.ValidationProblem(errors);
    }

    var result = await service.MeasureAsync(request, cancellationToken);
    return Results.Ok(result);
}).RequireAuthIfEnabled(requireAuthentication);

var agentConnectivity = app.MapGroup("/api/agent-connectivity");
agentConnectivity.RequireAuthIfEnabled(requireAuthentication);

agentConnectivity.MapGet("/settings", (IAgentConnectivityService service) =>
{
    return Results.Ok(service.GetSettings());
}).RequireAdminIfEnabled(requireAuthentication);

agentConnectivity.MapGet("/status", async (
    string? projectId,
    IAgentConnectivityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.GetStatusAsync(projectId, cancellationToken);
    return Results.Ok(result);
}).RequireAdminIfEnabled(requireAuthentication);

agentConnectivity.MapGet("/summaries", async (
    string? projectId,
    string? agentId,
    string? mcpMethod,
    DateTimeOffset? fromUtc,
    DateTimeOffset? toUtc,
    int? limit,
    IAgentConnectivityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.GetSummariesAsync(
        new AgentConnectivitySummaryQuery(projectId, agentId, mcpMethod, fromUtc, toUtc, limit ?? 200),
        cancellationToken);
    return Results.Ok(result);
}).RequireAdminIfEnabled(requireAuthentication);

agentConnectivity.MapGet("/recent", async (
    string? projectId,
    string? agentId,
    int? limit,
    IAgentConnectivityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.GetRecentAsync(projectId, agentId, limit ?? 100, cancellationToken);
    return Results.Ok(result);
}).RequireAdminIfEnabled(requireAuthentication);

agentConnectivity.MapPost("/observations", async (
    AgentConnectivityObservationBatchRequest request,
    IAgentConnectivityService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.IngestAsync(request, cancellationToken);
    return Results.Ok(result);
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.AgentConnectivityWrite);

app.MapMcp("/mcp").RequireAuthIfEnabled(requireAuthentication);

app.Run();

static void AddTrustedForwarders(ForwardedHeadersOptions options, IConfiguration configuration)
{
    foreach (var value in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        if (!System.Net.IPAddress.TryParse(value, out var address))
        {
            throw new InvalidOperationException($"ForwardedHeaders:KnownProxies contains invalid IP address '{value}'.");
        }

        options.KnownProxies.Add(address);
    }

    foreach (var value in configuration.GetSection("ForwardedHeaders:KnownNetworks").Get<string[]>() ?? [])
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        if (!System.Net.IPNetwork.TryParse(value, out var network))
        {
            throw new InvalidOperationException($"ForwardedHeaders:KnownNetworks contains invalid CIDR '{value}'.");
        }

        options.KnownIPNetworks.Add(network);
    }
}

static IResult ToStepUpResult(StepUpAuthorizationResult decision)
    => Results.Json(decision, statusCode: decision.Outcome switch
    {
        StepUpRequirementOutcome.RequiresStepUp => StatusCodes.Status428PreconditionRequired,
        StepUpRequirementOutcome.RequiresExternalApproval => StatusCodes.Status409Conflict,
        StepUpRequirementOutcome.Disabled => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status200OK
    });

static long RequireLongHeader(HttpRequest request, string name)
    => long.TryParse(request.Headers[name].FirstOrDefault(), out var value) && value >= 0
        ? value
        : throw new BadHttpRequestException($"{name} is required and must be a non-negative integer.");

static DateTimeOffset? TryReadDateTimeHeader(HttpRequest request, string name)
    => string.IsNullOrWhiteSpace(request.Headers[name].FirstOrDefault())
        ? null
        : DateTimeOffset.TryParse(request.Headers[name].FirstOrDefault(), out var value)
            ? value
            : throw new BadHttpRequestException($"{name} must be an ISO 8601 timestamp.");

static StepUpProof? TryReadStepUpProof(HttpRequest request)
{
    var assertion = request.Headers["X-Step-Up-Assertion-Id"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(assertion)) return null;
    if (!Guid.TryParse(assertion, out var assertionId)) throw new BadHttpRequestException("X-Step-Up-Assertion-Id is invalid.");
    return new StepUpProof(
        assertionId,
        RequireTransferHeader(request, "X-Step-Up-Nonce"),
        RequireLongHeader(request, "X-Step-Up-Revision"));
}

static HashSet<string> ResolveAllowedOrigins(IEnumerable<string>? configuredValues)
{
    var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var value in configuredValues ?? [])
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException($"ContextHub:Security:AllowedMcpOrigins contains invalid origin '{value}'.");
        }

        origins.Add(uri.GetLeftPart(UriPartial.Authority));
    }

    return origins;
}

static bool IsAllowedMcpOrigin(HttpRequest request, IReadOnlySet<string> allowedOrigins)
{
    if (!request.Headers.TryGetValue("Origin", out var values))
    {
        return true;
    }

    if (values.Count != 1 ||
        !Uri.TryCreate(values[0], UriKind.Absolute, out var origin) ||
        (origin.Scheme != Uri.UriSchemeHttps && origin.Scheme != Uri.UriSchemeHttp) ||
        !string.IsNullOrEmpty(origin.UserInfo) ||
        origin.AbsolutePath != "/" ||
        !string.IsNullOrEmpty(origin.Query) ||
        !string.IsNullOrEmpty(origin.Fragment))
    {
        return false;
    }

    return allowedOrigins.Contains(origin.GetLeftPart(UriPartial.Authority));
}

public partial class Program;

internal static class CloudflareCacheHeaders
{
    public static async Task ApplyNoStorePolicyAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(static state =>
        {
            var httpContext = (HttpContext)state;
            httpContext.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate, no-transform";
            httpContext.Response.Headers["Cloudflare-CDN-Cache-Control"] = "no-store";
            httpContext.Response.Headers["CDN-Cache-Control"] = "no-store";
            httpContext.Response.Headers["X-Accel-Buffering"] = "no";
            httpContext.Response.Headers.Pragma = "no-cache";
            httpContext.Response.Headers.Expires = "0";
            return Task.CompletedTask;
        }, context);

        await next(context);
    }
}

internal static class MaintenanceApiHelpers
{
    public static string ResolveTriggeredBy(IRequestActorAccessor actorAccessor)
    {
        var actor = actorAccessor.Current;
        if (!string.IsNullOrWhiteSpace(actor.Username))
        {
            return actor.Username;
        }

        if (actor.UserId.HasValue)
        {
            return actor.UserId.Value.ToString("D");
        }

        return actor.IsAuthenticated ? "authenticated-api-token" : "system";
    }

    public static int ComputeRetryAfterSeconds(DateTimeOffset? estimatedEndsAtUtc)
    {
        if (!estimatedEndsAtUtc.HasValue)
        {
            return 300;
        }

        var seconds = (int)Math.Ceiling((estimatedEndsAtUtc.Value - DateTimeOffset.UtcNow).TotalSeconds);
        return Math.Clamp(seconds, 1, 24 * 60 * 60);
    }
}

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
internal sealed record MemoryArchiveBody(bool Archived = true, string? ProjectId = null, string? Reason = null);
internal sealed record MemoryMoveBody(string TargetProjectId, string? SourceProjectId = null, string? Reason = null);
internal sealed record MemoryDeleteBody(string? ProjectId = null, string? Reason = null);
internal sealed record ChatGptProposalDecisionBody(string? Note = null);
internal sealed record TenantUserCreateBody(
    string Username,
    string DisplayName,
    string Email = "",
    TenantUserRole Role = TenantUserRole.Member,
    string Password = "");
internal sealed record TenantUserUpdateBody(
    string? DisplayName = null,
    string? Email = null,
    TenantUserRole? Role = null,
    TenantUserStatus? Status = null,
    string? Password = null);
internal sealed record DiscussionMessageCreateBody(string SenderProjectId, string Content);
internal sealed record DiscussionThreadReadBody(string ReaderProjectId, Guid LastReadMessageId);
internal sealed record ProjectWorkItemUpdateBody(string? Title = null, string? Description = null, IReadOnlyList<string>? Tags = null, ProjectWorkItemStatus? Status = null, int? Priority = null, DateTimeOffset? DueAt = null, ProjectWorkItemDefinitionState? DefinitionState = null);
internal sealed record ProjectWorkItemChecklistCompletionBody(bool IsCompleted);
internal sealed record ProjectWorkItemGovernanceExclusionBody(string ProjectId, string GovernanceRunId, string Reason, bool Excluded = true);
internal sealed record ConversationInsightGovernanceBody(string? GovernanceRunId = null, string? Reason = null);
internal sealed record ConversationInsightDispositionBody(ConversationInsightDisposition Disposition, string Reason, string? GovernanceRunId = null);
internal sealed record ProjectHierarchySetChildrenBody(IReadOnlyList<string>? ChildProjectIds);
internal sealed record FileOperationBody(FileOperation Operation, string Purpose);
internal sealed record CreateFileRepresentationBody(FileRepresentationKind Kind, string ContentHash, FileClassification Classification);
internal sealed record QuarantineReleaseBody(string Reason, string ExternalApprovalReference, long ExpectedClassificationRevision);
internal sealed record FileDeleteBody(string Reason, string RequestId);
internal sealed record CanonicalTagMergeBody(Guid SourceId, Guid TargetId, long ExpectedSourceRevision);
internal sealed record CanonicalTagSplitBody(Guid SourceId, IReadOnlyList<string> CandidateResourceIds);
internal sealed record PasswordStepUpBody(string Password, string Purpose, string? ResourceType = null, string? ResourceId = null, int MaxUses = 1);
internal sealed record SecretCreateBody(string ProjectId, string Name, SecretKind Kind, StepUpProof StepUp);
internal sealed record SecretLeaseCreateBody(SecretLeaseKind Kind, string Purpose, string Target, string RequestId, long ExpectedSecretRevision, int MaxUses, int MaxConcurrency, int? TtlSeconds, Guid? ExecutionId, StepUpProof StepUp);
internal sealed record SecretRevokeBody(long ExpectedRevision, string ExternalApprovalReference, string RequestId);
internal sealed record SshSignAuthenticationBody(Guid LeaseId, string Capability, long ExpectedRevision, string RequestId, string SessionId, string Host, int Port, string User, string ChallengeBase64);
internal sealed record SshCertificateIssueBody(Guid CaSecretId, string PublicKey, string TargetHost, int TargetPort, string TargetUser, string Purpose, string RequestId, long ExpectedSecretRevision, Guid? ExecutionId, StepUpProof StepUp);
internal sealed record SshCertificateRenewBody(string RenewalCapability, long ExpectedRevision, string RequestId);
internal sealed record SshCertificateRevokeBody(long ExpectedRevision, string ExternalApprovalReference, string Reason);

internal sealed record TenantProjectGrantUpsertBody(
    bool CanRead = true,
    bool CanWrite = false,
    bool CanManageTokens = false);
internal sealed record ApiTokenCreateBody(
    Guid OwnerUserId,
    string Name,
    string? Notes = null,
    IReadOnlyList<string>? Scopes = null,
    IReadOnlyList<string>? AllowedProjectIds = null,
    DateTimeOffset? ExpiresAt = null);
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

internal static class EndpointAuthorizationExtensions
{
    public static TBuilder RequireAuthIfEnabled<TBuilder>(this TBuilder builder, bool enabled)
        where TBuilder : IEndpointConventionBuilder
    {
        _ = enabled;
        builder.RequireAuthorization();
        return builder;
    }

    public static TBuilder RequireAdminIfEnabled<TBuilder>(this TBuilder builder, bool enabled)
        where TBuilder : IEndpointConventionBuilder
    {
        _ = enabled;
        builder.AddEndpointFilter(async (context, next) =>
        {
            var actor = context.HttpContext.RequestServices.GetRequiredService<IRequestActorAccessor>().Current;
            return actor.IsAdmin ? await next(context) : Results.Forbid();
        });
        return builder;
    }

    public static TBuilder RequireScopeIfEnabled<TBuilder>(this TBuilder builder, bool enabled, string scope)
        where TBuilder : IEndpointConventionBuilder
    {
        _ = enabled;
        builder.AddEndpointFilter(async (context, next) =>
        {
            var actor = context.HttpContext.RequestServices.GetRequiredService<IRequestActorAccessor>().Current;
            return actor.HasUser && actor.HasScope(scope)
                ? await next(context)
                : Results.Forbid();
        });
        return builder;
    }
}

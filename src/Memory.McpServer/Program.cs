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
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
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
builder.Services.AddMcpServer()
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<MemoryMcpTools>()
    .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult
    {
        Resources = []
    }))
    .WithListResourceTemplatesHandler(WorkingContextMcpResources.ListTemplatesAsync)
    .WithReadResourceHandler(WorkingContextMcpResources.ReadAsync);

var app = builder.Build();
const bool requireAuthentication = true;

app.UseForwardedHeaders();
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

userPreferences.MapPost(string.Empty, async (UserPreferenceUpsertRequest request, IMemoryService service, CancellationToken cancellationToken) =>
{
    var result = await service.UpsertUserPreferenceAsync(request, cancellationToken);
    return Results.Ok(result);
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
workItems.MapGet(string.Empty, async (string projectId, string? status, int? limit, bool? includeArchived, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    if (!EnumParser.TryParse(status, out ProjectWorkItemStatus? parsedStatus, out var error))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]> { ["status"] = [error ?? "Unsupported ProjectWorkItemStatus value."] });
    }
    return Results.Ok(await service.ListAsync(new ProjectWorkItemListRequest(ProjectContext.Normalize(projectId), parsedStatus, limit ?? 100, includeArchived ?? false), cancellationToken));
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryRead);
workItems.MapPost(string.Empty, async (ProjectWorkItemCreateRequest request, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    try { return Results.Created("/api/work-items", await service.CreateAsync(request, cancellationToken)); }
    catch (InvalidOperationException ex) { return Results.ValidationProblem(new Dictionary<string, string[]> { ["workItem"] = [ex.Message] }); }
}).RequireScopeIfEnabled(requireAuthentication, SecurityScopes.MemoryWrite);
workItems.MapPut("/{id:guid}", async (Guid id, ProjectWorkItemUpdateBody body, IProjectWorkItemService service, CancellationToken cancellationToken) =>
{
    try { return Results.Ok(await service.UpdateAsync(new ProjectWorkItemUpdateRequest(id, body.Title, body.Description, body.Tags, body.Status, body.Priority, body.DueAt), cancellationToken)); }
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
internal sealed record ProjectWorkItemUpdateBody(string? Title = null, string? Description = null, IReadOnlyList<string>? Tags = null, ProjectWorkItemStatus? Status = null, int? Priority = null, DateTimeOffset? DueAt = null);
internal sealed record ProjectWorkItemChecklistCompletionBody(bool IsCompleted);
internal sealed record ProjectWorkItemGovernanceExclusionBody(string ProjectId, string GovernanceRunId, string Reason, bool Excluded = true);
internal sealed record ConversationInsightGovernanceBody(string? GovernanceRunId = null, string? Reason = null);
internal sealed record ConversationInsightDispositionBody(ConversationInsightDisposition Disposition, string Reason, string? GovernanceRunId = null);
internal sealed record ProjectHierarchySetChildrenBody(IReadOnlyList<string>? ChildProjectIds);

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

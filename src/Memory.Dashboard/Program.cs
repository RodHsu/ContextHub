using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Memory.Application;
using Memory.Dashboard.Components;
using Memory.Dashboard.Services;
using Memory.Dashboard.Services.Testing;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);
LocalDotEnvConfiguration.AddFallbacks(
    builder.Configuration,
    builder.Environment.ContentRootPath,
    new Dictionary<string, string>
    {
        ["DASHBOARD_API_TOKEN"] = $"{DashboardOptions.SectionName}:ApiToken"
    });
var useBrowserTestDoubles = builder.Configuration.GetValue<bool>($"{DashboardOptions.SectionName}:UseBrowserTestDoubles");

if (useBrowserTestDoubles || builder.Environment.IsEnvironment("Testing"))
{
    builder.WebHost.UseStaticWebAssets();
}

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
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    var loginPermitLimit = useBrowserTestDoubles || builder.Environment.IsEnvironment("Testing") ? 10_000 : 10;
    options.AddPolicy("dashboard-login", context => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = loginPermitLimit,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0,
            AutoReplenishment = true
        }));
});
builder.Services.AddOptions<DashboardOptions>()
    .Bind(builder.Configuration.GetSection(DashboardOptions.SectionName))
    .PostConfigure(options =>
    {
        if (string.IsNullOrWhiteSpace(options.InstanceId))
        {
            options.InstanceId =
                builder.Configuration["ContextHub:InstanceId"]
                ?? builder.Configuration[$"{DashboardOptions.SectionName}:InstanceId"]
                ?? options.ComposeProject
                ?? "default";
        }

        options.InstanceId = options.InstanceId.Trim();
    })
    .Validate(options => !string.IsNullOrWhiteSpace(options.BaseUrl), "Dashboard:BaseUrl is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.AdminUsername), "Dashboard:AdminUsername is required.")
    .Validate(options => !string.IsNullOrWhiteSpace(options.AdminPasswordHash), "Dashboard:AdminPasswordHash is required.")
    .Validate(
        options => !builder.Environment.IsProduction() || !DashboardOptions.IsDefaultAdminPasswordHash(options.AdminPasswordHash),
        "Dashboard:AdminPasswordHash must not use the built-in default in Production.")
    .ValidateOnStart();
builder.Services.Configure<MemoryOptions>(builder.Configuration.GetSection(MemoryOptions.SectionName));
builder.Services.AddSingleton<IPasswordHasher<object>, PasswordHasher<object>>();
builder.Services.AddHttpContextAccessor();
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents(options =>
    {
        options.DetailedErrors = builder.Environment.IsDevelopment();
        options.DisconnectedCircuitRetentionPeriod = TimeSpan.FromMinutes(3);
        options.JSInteropDefaultCallTimeout = TimeSpan.FromSeconds(20);
        options.MaxBufferedUnacknowledgedRenderBatches = 32;
    })
    .AddHubOptions(options =>
    {
        options.ClientTimeoutInterval = TimeSpan.FromSeconds(60);
        options.HandshakeTimeout = TimeSpan.FromSeconds(15);
        options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        options.MaximumReceiveMessageSize = 256 * 1024;
    });
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddAuthorization();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.AccessDeniedPath = "/login";
        options.Cookie.Name = "contexthub.dashboard";
        options.SlidingExpiration = true;
    });
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(builder.Configuration[$"{DashboardOptions.SectionName}:DataProtectionPath"] ?? "/var/lib/contexthub-dashboard/keys"))
    .SetApplicationName("ContextHub.Dashboard");

if (useBrowserTestDoubles)
{
    builder.Services.AddScoped<DashboardBrowserTestProfileAccessor>();
    builder.Services.AddScoped<IContextHubApiClient, BrowserTestContextHubApiClient>();
    builder.Services.AddScoped<IDockerMetricsService, BrowserTestDockerMetricsService>();
    builder.Services.AddScoped<IInstanceSettingsService, BrowserTestInstanceSettingsService>();
}
else
{
    var postgresConnectionString = builder.Configuration.GetConnectionString("Postgres");
    if (!string.IsNullOrWhiteSpace(postgresConnectionString))
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(postgresConnectionString);
        dataSourceBuilder.UseVector();
        var dataSource = dataSourceBuilder.Build();

        builder.Services.AddSingleton(dataSource);
        builder.Services.AddDbContextFactory<MemoryDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseVector());
        });
        builder.Services.AddDbContext<MemoryDbContext>((sp, options) =>
        {
            options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>(), npgsql => npgsql.UseVector());
        });
        builder.Services.AddScoped<IApplicationDbContext>(sp => sp.GetRequiredService<MemoryDbContext>());
        builder.Services.AddScoped<IInstanceSettingsService, DashboardInstanceSettingsService>();
    }
    else
    {
        builder.Services.AddScoped<IInstanceSettingsService, LocalOnlyInstanceSettingsService>();
    }

    builder.Services.AddHttpClient<IContextHubApiClient, ContextHubApiClient>((sp, client) =>
    {
        var options = sp.GetRequiredService<IOptions<DashboardOptions>>().Value;
        DashboardApiClientHttpClient.Configure(client, options);
    })
    .AddHttpMessageHandler<DashboardActAsDelegatingHandler>();
    builder.Services.AddTransient<DashboardActAsDelegatingHandler>();
    builder.Services.AddSingleton<IDockerMetricsService, DockerMetricsService>();
}
builder.Services.AddScoped<IDashboardRuntimeSettingsAccessor, DashboardRuntimeSettingsAccessor>();
builder.Services.AddScoped<IInstanceTransferService, InstanceTransferService>();

var app = builder.Build();

await ValidateProductionDashboardCredentialsAsync(app.Services, app.Environment, CancellationToken.None);

app.UseForwardedHeaders();
app.UseRouting();
app.UseRateLimiter();
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.Use(CloudflareCacheHeaders.ApplyDashboardPolicyAsync);
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (AnonymousPaths.IsAllowed(context.Request.Path))
    {
        await next();
        return;
    }

    if (context.User.Identity?.IsAuthenticated != true)
    {
        var returnUrl = Uri.EscapeDataString($"{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}");
        context.Response.Redirect($"/login?returnUrl={returnUrl}");
        return;
    }

    if (!DashboardRouteAuthorization.CanAccess(context.User, context.Request.Path))
    {
        if (context.Request.Path == "/")
        {
            context.Response.Redirect("/memories");
            return;
        }

        context.Response.Redirect("/forbidden");
        return;
    }

    await next();
});
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/health/live", (HttpContext context, IOptions<DashboardOptions> options) =>
{
    if (!DashboardHealthTokenAuthorization.IsAuthorized(context, options.Value))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(new { status = "live" });
});
app.MapGet("/health/ready", async (HttpContext context, IOptions<DashboardOptions> options, IContextHubApiClient apiClient, CancellationToken cancellationToken) =>
{
    if (!DashboardHealthTokenAuthorization.IsAuthorized(context, options.Value))
    {
        return Results.Unauthorized();
    }

    try
    {
        await apiClient.GetStatusAsync(cancellationToken);
        return Results.Ok(new { status = "ready" });
    }
    catch (Exception ex)
    {
        return Results.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            title: "Dashboard dependencies are not ready.",
            detail: ex.Message);
    }
});

var loginEndpoint = app.MapPost("/account/login", async (
    [FromForm] DashboardLoginForm form,
    HttpContext context,
    IInstanceSettingsService instanceSettingsService,
    IPasswordHasher<object> passwordHasher) =>
{
    var dbContext = context.RequestServices.GetService<MemoryDbContext>();
    if (dbContext is not null)
    {
        var username = (form.Username ?? string.Empty).Trim();
        var user = await dbContext.TenantUsers
            .Include(x => x.Tenant)
            .FirstOrDefaultAsync(
                x => x.Username == username &&
                     x.Status == TenantUserStatus.Active &&
                     x.Tenant!.Status == TenantStatus.Active,
                context.RequestAborted);

        if (user is not null && !string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            if (app.Environment.IsProduction() && DashboardOptions.IsDefaultAdminPasswordHash(user.PasswordHash))
            {
                return Results.Redirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(DashboardRouting.NormalizeReturnUrl(form.ReturnUrl))}");
            }

            var userVerification = passwordHasher.VerifyHashedPassword(new object(), user.PasswordHash, form.Password ?? string.Empty);
            if (userVerification != PasswordVerificationResult.Failed)
            {
                user.LastLoginAt = DateTimeOffset.UtcNow;
                user.UpdatedAt = DateTimeOffset.UtcNow;
                await dbContext.SaveChangesAsync(context.RequestAborted);
                var authenticationSettings = await instanceSettingsService.GetDashboardAuthenticationSettingsAsync(context.RequestAborted);
                return await SignInDashboardUserAsync(
                    context,
                    user,
                    form.ReturnUrl,
                    authenticationSettings.SessionTimeoutMinutes);
            }
        }
    }

    var settings = await instanceSettingsService.GetDashboardAuthenticationSettingsAsync(context.RequestAborted);
    if (app.Environment.IsProduction() && DashboardOptions.IsDefaultAdminPasswordHash(settings.AdminPasswordHash))
    {
        throw new InvalidOperationException("Dashboard default admin password hash is not allowed in Production.");
    }

    var verification = passwordHasher.VerifyHashedPassword(new object(), settings.AdminPasswordHash, form.Password ?? string.Empty);
    if (!string.Equals(form.Username, settings.AdminUsername, StringComparison.Ordinal) ||
        verification == PasswordVerificationResult.Failed)
    {
        return Results.Redirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(DashboardRouting.NormalizeReturnUrl(form.ReturnUrl))}");
    }

    if (dbContext is not null)
    {
        var admin = await EnsureDashboardAdminUserAsync(dbContext, settings, context.RequestAborted);
        return await SignInDashboardUserAsync(context, admin, form.ReturnUrl, settings.SessionTimeoutMinutes);
    }

    return await SignInLegacyAdminAsync(context, settings, form.ReturnUrl);
});
if (!useBrowserTestDoubles && !builder.Environment.IsEnvironment("Testing"))
{
    loginEndpoint.RequireRateLimiting("dashboard-login");
}

app.MapPost("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/account/session/refresh", () => Results.NoContent());

var settingsApi = app.MapGroup("/api/settings");
settingsApi.AddEndpointFilter(async (context, next) =>
{
    var user = context.HttpContext.User;
    if (user.Identity?.IsAuthenticated == true && DashboardRouteAuthorization.IsAdmin(user))
    {
        return await next(context);
    }

    return Results.Forbid();
});

settingsApi.MapGet("/instance", async (IInstanceSettingsService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetSnapshotAsync(cancellationToken);
    return Results.Ok(result);
});

settingsApi.MapPut("/instance", async (
    InstanceSettingsUpdateRequest request,
    HttpContext context,
    IInstanceSettingsService service,
    CancellationToken cancellationToken) =>
{
    try
    {
        var result = await service.UpdateAsync(request, context.User.Identity?.Name ?? "dashboard", cancellationToken);
        return Results.Ok(result);
    }
    catch (InvalidOperationException ex)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["settings"] = [ex.Message]
        });
    }
});

settingsApi.MapDelete("/instance", async (
    HttpContext context,
    IInstanceSettingsService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ResetAsync(context.User.Identity?.Name ?? "dashboard", cancellationToken);
    return Results.Ok(result);
});

settingsApi.MapPost("/restart-app", async (
    RestartAppContainersRequest request,
    IDockerMetricsService dockerMetricsService,
    CancellationToken cancellationToken) =>
{
    var result = await dockerMetricsService.RestartAppContainersAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

async Task<IResult> SignInDashboardUserAsync(
    HttpContext context,
    TenantUser user,
    string? returnUrl,
    int sessionTimeoutMinutes = 60)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.Name, user.Username),
        new Claim(ClaimTypes.Role, user.Role.ToString()),
        new Claim("contexthub:tenant_id", user.TenantId.ToString()),
        new Claim("contexthub:user_id", user.Id.ToString())
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = true,
        AllowRefresh = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(sessionTimeoutMinutes)
    });

    var normalizedReturnUrl = DashboardRouting.NormalizeReturnUrl(returnUrl);
    return Results.Redirect(IsDashboardAdmin(user.Role) ? normalizedReturnUrl : NormalizeUserReturnUrl(normalizedReturnUrl));
}

async Task<IResult> SignInLegacyAdminAsync(HttpContext context, DashboardAuthenticationSettings settings, string? returnUrl)
{
    var claims = new[]
    {
        new Claim(ClaimTypes.Name, settings.AdminUsername),
        new Claim(ClaimTypes.Role, TenantUserRole.Owner.ToString())
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = true,
        AllowRefresh = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(settings.SessionTimeoutMinutes)
    });

    return Results.Redirect(DashboardRouting.NormalizeReturnUrl(returnUrl));
}

static async Task ValidateProductionDashboardCredentialsAsync(
    IServiceProvider services,
    IWebHostEnvironment environment,
    CancellationToken cancellationToken)
{
    if (!environment.IsProduction())
    {
        return;
    }

    await using var scope = services.CreateAsyncScope();
    var dbContext = scope.ServiceProvider.GetService<MemoryDbContext>();
    if (dbContext is null)
    {
        return;
    }

    var defaultAdminExists = await dbContext.TenantUsers
        .AsNoTracking()
        .AnyAsync(
            x => x.Status == TenantUserStatus.Active &&
                 (x.Role == TenantUserRole.Owner || x.Role == TenantUserRole.Admin) &&
                 x.PasswordHash == DashboardOptions.DefaultAdminPasswordHash,
            cancellationToken);
    if (defaultAdminExists)
    {
        throw new InvalidOperationException("Production contains an active dashboard owner/admin with the built-in default password hash.");
    }
}

async Task<TenantUser> EnsureDashboardAdminUserAsync(
    MemoryDbContext dbContext,
    DashboardAuthenticationSettings settings,
    CancellationToken cancellationToken)
{
    var tenant = await dbContext.Tenants.FirstOrDefaultAsync(x => x.Slug == "context-team", cancellationToken);
    if (tenant is null)
    {
        tenant = new Tenant
        {
            Slug = "context-team",
            DisplayName = "Context Team",
            Status = TenantStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await dbContext.Tenants.AddAsync(tenant, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    var username = settings.AdminUsername.Trim();
    var admin = await dbContext.TenantUsers.FirstOrDefaultAsync(
        x => x.TenantId == tenant.Id && x.Username == username,
        cancellationToken);
    if (admin is null)
    {
        admin = new TenantUser
        {
            TenantId = tenant.Id,
            Username = username,
            DisplayName = username,
            Email = string.Empty,
            PasswordHash = settings.AdminPasswordHash,
            Role = TenantUserRole.Owner,
            Status = TenantUserStatus.Active,
            LastLoginAt = DateTimeOffset.UtcNow,
            PasswordUpdatedAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await dbContext.TenantUsers.AddAsync(admin, cancellationToken);
    }
    else
    {
        admin.PasswordHash = string.IsNullOrWhiteSpace(admin.PasswordHash) ? settings.AdminPasswordHash : admin.PasswordHash;
        admin.Role = admin.Role == TenantUserRole.Member ? TenantUserRole.Owner : admin.Role;
        admin.Status = TenantUserStatus.Active;
        admin.LastLoginAt = DateTimeOffset.UtcNow;
        admin.UpdatedAt = DateTimeOffset.UtcNow;
    }

    await dbContext.SaveChangesAsync(cancellationToken);
    return admin;
}

bool IsDashboardAdmin(TenantUserRole role)
    => role is TenantUserRole.Owner or TenantUserRole.Admin;

string NormalizeUserReturnUrl(string returnUrl)
    => returnUrl is "/memories" or "/graph" or "/preferences" or "/account/tokens"
        ? returnUrl
        : "/memories";

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

public partial class Program;

internal static class CloudflareCacheHeaders
{
    private const string BrowserAndSharedNoStore = "no-store, no-cache, max-age=0, must-revalidate";
    private const string SharedNoStore = "no-store";
    private const string StaticAssetBrowserCache = "public, max-age=31536000, immutable";
    private const string StaticAssetSharedCache = "public, max-age=31536000";

    public static async Task ApplyDashboardPolicyAsync(HttpContext context, RequestDelegate next)
    {
        context.Response.OnStarting(static state =>
        {
            var httpContext = (HttpContext)state;
            ApplyDashboardPolicy(httpContext);
            return Task.CompletedTask;
        }, context);

        await next(context);
    }

    private static void ApplyDashboardPolicy(HttpContext context)
    {
        if (IsCacheableStaticAsset(context))
        {
            SetPublicStaticAssetHeaders(context);
            return;
        }

        SetNoStoreHeaders(context);
    }

    private static bool IsCacheableStaticAsset(HttpContext context)
    {
        if (!HttpMethods.IsGet(context.Request.Method) &&
            !HttpMethods.IsHead(context.Request.Method))
        {
            return false;
        }

        if (context.Response.StatusCode is not StatusCodes.Status200OK and not StatusCodes.Status304NotModified)
        {
            return false;
        }

        if (context.Request.Headers.ContainsKey("Authorization") ||
            context.Response.Headers.ContainsKey("Set-Cookie"))
        {
            return false;
        }

        return IsStaticAssetPath(context.Request.Path) ||
               IsStaticAssetContentType(context.Response.ContentType);
    }

    private static bool IsStaticAssetPath(PathString path)
        => path.StartsWithSegments("/_framework", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/_content", StringComparison.OrdinalIgnoreCase) ||
           (path.HasValue && Path.HasExtension(path.Value));

    private static bool IsStaticAssetContentType(string? contentType)
    {
        var mediaType = contentType?.Split(';', 2)[0].Trim();
        return mediaType is "text/css" or "application/javascript" or "text/javascript" or "image/svg+xml" or "image/png" or "image/x-icon" or "font/woff2";
    }

    private static void SetPublicStaticAssetHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = StaticAssetBrowserCache;
        context.Response.Headers["Cloudflare-CDN-Cache-Control"] = StaticAssetSharedCache;
        context.Response.Headers["CDN-Cache-Control"] = StaticAssetSharedCache;
        context.Response.Headers.Remove("Pragma");
        context.Response.Headers.Remove("Expires");
    }

    private static void SetNoStoreHeaders(HttpContext context)
    {
        context.Response.Headers.CacheControl = BrowserAndSharedNoStore;
        context.Response.Headers["Cloudflare-CDN-Cache-Control"] = SharedNoStore;
        context.Response.Headers["CDN-Cache-Control"] = SharedNoStore;
        context.Response.Headers.Pragma = "no-cache";
        context.Response.Headers.Expires = "0";
    }
}

internal static class AnonymousPaths
{
    public static bool IsAllowed(PathString path)
    {
        if (!path.HasValue)
        {
            return true;
        }

        if (path.StartsWithSegments("/login") ||
            path.StartsWithSegments("/account/login") ||
            path.StartsWithSegments("/health") ||
            IsInfrastructureRequest(path) ||
            path.StartsWithSegments("/not-found"))
        {
            return true;
        }

        var value = path.Value!;
        return Path.HasExtension(value);
    }

    public static bool IsInfrastructureRequest(PathString path)
        => path.StartsWithSegments("/_blazor") ||
           path.StartsWithSegments("/_framework") ||
           path.StartsWithSegments("/_content");
}

internal static class DashboardHealthTokenAuthorization
{
    private const string BearerPrefix = "Bearer ";

    public static bool IsAuthorized(HttpContext context, DashboardOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ApiToken))
        {
            return false;
        }

        var authorization = context.Request.Headers.Authorization.ToString();
        if (!authorization.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authorization[BearerPrefix.Length..].Trim();
        return FixedTimeEquals(token, options.ApiToken.Trim());
    }

    private static bool FixedTimeEquals(string candidate, string expected)
    {
        var candidateBytes = Encoding.UTF8.GetBytes(candidate);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return candidateBytes.Length == expectedBytes.Length &&
               CryptographicOperations.FixedTimeEquals(candidateBytes, expectedBytes);
    }
}

internal static class DashboardRouteAuthorization
{
    private static readonly string[] MemberAllowedPrefixes =
    [
        "/memories",
        "/graph",
        "/preferences",
        "/account/tokens",
        "/account/session/refresh",
        "/forbidden",
        "/account/logout"
    ];

    public static bool CanAccess(ClaimsPrincipal user, PathString path)
    {
        if (IsAdmin(user))
        {
            return true;
        }

        if (!path.HasValue || path == "/")
        {
            return false;
        }

        return MemberAllowedPrefixes.Any(prefix => path.StartsWithSegments(prefix));
    }

    public static bool IsAdmin(ClaimsPrincipal user)
        => user.IsInRole(TenantUserRole.Owner.ToString()) ||
           user.IsInRole(TenantUserRole.Admin.ToString()) ||
           user.IsInRole("DashboardAdmin");
}

internal static class DashboardRouting
{
    public static string NormalizeReturnUrl(string? returnUrl)
    {
        if (string.IsNullOrWhiteSpace(returnUrl))
        {
            return "/";
        }

        return returnUrl.StartsWith('/') && !returnUrl.StartsWith("//", StringComparison.Ordinal)
            ? returnUrl
            : "/";
    }
}

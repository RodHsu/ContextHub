using System.Security.Claims;
using Memory.Application;
using Memory.Dashboard.Components;
using Memory.Dashboard.Services;
using Memory.Dashboard.Services.Testing;
using Memory.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);
var useBrowserTestDoubles = builder.Configuration.GetValue<bool>($"{DashboardOptions.SectionName}:UseBrowserTestDoubles");

if (useBrowserTestDoubles || builder.Environment.IsEnvironment("Testing"))
{
    builder.WebHost.UseStaticWebAssets();
}

builder.Services.Configure<Microsoft.AspNetCore.Http.Json.JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
});

builder.Services.AddProblemDetails();
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
        client.BaseAddress = new Uri(options.BaseUrl, UriKind.Absolute);
        client.Timeout = TimeSpan.FromSeconds(15);
        client.DefaultRequestHeaders.Add(RequestTrafficConstants.DashboardRequestHeader, RequestTrafficConstants.DashboardRequestHeaderValue);
    });
    builder.Services.AddSingleton<IDockerMetricsService, DockerMetricsService>();
}
builder.Services.AddScoped<IDashboardRuntimeSettingsAccessor, DashboardRuntimeSettingsAccessor>();
builder.Services.AddScoped<IInstanceTransferService, InstanceTransferService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}

app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (!HttpMethods.IsGet(context.Request.Method))
    {
        await next();
        return;
    }

    if (AnonymousPaths.IsInfrastructureRequest(context.Request.Path) ||
        context.Request.Path.StartsWithSegments("/api") ||
        context.Request.Path.StartsWithSegments("/health"))
    {
        await next();
        return;
    }

    context.Response.OnStarting(static state =>
    {
        var httpContext = (HttpContext)state;
        if (!string.Equals(httpContext.Response.ContentType?.Split(';', 2)[0], "text/html", StringComparison.OrdinalIgnoreCase))
        {
            return Task.CompletedTask;
        }

        httpContext.Response.Headers.CacheControl = "no-store, no-cache, max-age=0, must-revalidate";
        httpContext.Response.Headers.Pragma = "no-cache";
        httpContext.Response.Headers.Expires = "0";
        return Task.CompletedTask;
    }, context);

    await next();
});
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

    await next();
});
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", async (IContextHubApiClient apiClient, CancellationToken cancellationToken) =>
{
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

app.MapPost("/account/login", async (
    [FromForm] DashboardLoginForm form,
    HttpContext context,
    IInstanceSettingsService instanceSettingsService,
    IPasswordHasher<object> passwordHasher) =>
{
    var settings = await instanceSettingsService.GetDashboardAuthenticationSettingsAsync(context.RequestAborted);
    var verification = passwordHasher.VerifyHashedPassword(new object(), settings.AdminPasswordHash, form.Password ?? string.Empty);
    if (!string.Equals(form.Username, settings.AdminUsername, StringComparison.Ordinal) ||
        verification == PasswordVerificationResult.Failed)
    {
        return Results.Redirect($"/login?error=invalid&returnUrl={Uri.EscapeDataString(DashboardRouting.NormalizeReturnUrl(form.ReturnUrl))}");
    }

    var claims = new[]
    {
        new Claim(ClaimTypes.Name, settings.AdminUsername),
        new Claim(ClaimTypes.Role, "DashboardAdmin")
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    var principal = new ClaimsPrincipal(identity);
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, principal, new AuthenticationProperties
    {
        IsPersistent = true,
        AllowRefresh = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(settings.SessionTimeoutMinutes)
    });

    return Results.Redirect(DashboardRouting.NormalizeReturnUrl(form.ReturnUrl));
});

app.MapPost("/account/logout", async (HttpContext context) =>
{
    await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
});

app.MapGet("/api/settings/instance", async (IInstanceSettingsService service, CancellationToken cancellationToken) =>
{
    var result = await service.GetSnapshotAsync(cancellationToken);
    return Results.Ok(result);
});

app.MapPut("/api/settings/instance", async (
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

app.MapDelete("/api/settings/instance", async (
    HttpContext context,
    IInstanceSettingsService service,
    CancellationToken cancellationToken) =>
{
    var result = await service.ResetAsync(context.User.Identity?.Name ?? "dashboard", cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/api/settings/restart-app", async (
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

public partial class Program;

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

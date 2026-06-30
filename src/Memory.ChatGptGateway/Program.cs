using Memory.Application;
using Memory.ChatGptGateway;
using Memory.Infrastructure;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ChatGptGatewayOptions>(builder.Configuration.GetSection("ChatGptGateway"));
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                               ForwardedHeaders.XForwardedHost |
                               ForwardedHeaders.XForwardedProto;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddProblemDetails();
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryApplication();
builder.Services.AddMemoryInfrastructure(builder.Configuration, "chatgpt-gateway");

var gatewayOptions = builder.Configuration.GetSection("ChatGptGateway").Get<ChatGptGatewayOptions>() ?? new ChatGptGatewayOptions();
if (gatewayOptions.OAuth.TestMode)
{
    builder.Services.AddAuthentication(GatewayAuthentication.TestScheme)
        .AddScheme<AuthenticationSchemeOptions, ChatGptTestAuthenticationHandler>(GatewayAuthentication.TestScheme, _ => { });
}
else
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = Required(gatewayOptions.OAuth.Authority, "ChatGptGateway:OAuth:Authority");
            options.Audience = Required(gatewayOptions.OAuth.ClientId, "ChatGptGateway:OAuth:ClientId");
            options.RequireHttpsMetadata = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                NameClaimType = gatewayOptions.OAuth.NameClaim
            };
            options.Events = new JwtBearerEvents
            {
                OnChallenge = context =>
                {
                    AppendOAuthResourceChallenge(context.HttpContext);
                    return Task.CompletedTask;
                }
            };
        });
}

builder.Services.AddAuthorization();
builder.Services.AddScoped<ChatGptGatewayTools>();
builder.Services.AddMcpServer()
    .WithHttpTransport()
    .WithTools<ChatGptGatewayTools>()
    .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult
    {
        Resources = []
    }));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseExceptionHandler();
app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (UnauthorizedAccessException ex) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        await Results.Problem("Forbidden", ex.Message, StatusCodes.Status403Forbidden).ExecuteAsync(context);
    }
    catch (InvalidOperationException ex) when (!context.Response.HasStarted)
    {
        context.Response.Clear();
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        await Results.Problem("Invalid request", ex.Message, StatusCodes.Status400BadRequest).ExecuteAsync(context);
    }
});
app.Use(CloudflareCacheHeaders.ApplyNoStorePolicyAsync);
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (IsPublicPath(context.Request.Path) ||
        context.User.Identity?.IsAuthenticated == true)
    {
        await next();
        return;
    }

    AppendOAuthResourceChallenge(context);
    await context.ChallengeAsync();
});
app.UseAuthorization();
app.UseMiddleware<ChatGptGatewayActorMiddleware>();

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("live")
}).AllowAnonymous();

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
}).AllowAnonymous();

app.MapGet("/api/status", (IOptions<ChatGptGatewayOptions> options) => Results.Ok(new
{
    service = "chatgpt-gateway",
    oauthTestMode = options.Value.OAuth.TestMode,
    publicMcpUrl = options.Value.PublicMcpUrl,
    publicResourceMetadataUrl = options.Value.PublicResourceMetadataUrl,
    allowedProjectIds = options.Value.AllowedProjectIds,
    readTools = options.Value.ReadTools,
    directWriteTools = options.Value.DirectWriteTools,
    proposalWriteTools = options.Value.ProposalWriteTools
})).RequireAuthorization();

app.MapGet("/.well-known/oauth-protected-resource/mcp-chat", (
    HttpContext context,
    IOptions<ChatGptGatewayOptions> options) =>
{
    var value = options.Value;
    var publicMcpUrl = ResolvePublicMcpUrl(context, value);
    var metadata = new OAuthProtectedResourceMetadata(
        publicMcpUrl,
        [Required(value.OAuth.Authority, "ChatGptGateway:OAuth:Authority")],
        value.OAuth.Scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray(),
        ["header"],
        "ContextHub MCP Chat Gateway");

    return Results.Json(metadata);
}).AllowAnonymous();

app.MapMcp("/mcp").RequireAuthorization();

app.Run();

static bool IsPublicPath(PathString path)
    => path.StartsWithSegments("/health/live", StringComparison.OrdinalIgnoreCase) ||
       path.StartsWithSegments("/health/ready", StringComparison.OrdinalIgnoreCase) ||
       path.StartsWithSegments("/.well-known/oauth-protected-resource/mcp-chat", StringComparison.OrdinalIgnoreCase);

static string Required(string value, string key)
    => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{key} is required when ChatGptGateway:OAuth:TestMode is false.")
        : value.Trim();

static void AppendOAuthResourceChallenge(HttpContext context)
{
    var options = context.RequestServices.GetRequiredService<IOptions<ChatGptGatewayOptions>>().Value;
    var metadataUrl = ResolvePublicResourceMetadataUrl(context, options);
    var challenge = $"Bearer resource_metadata=\"{metadataUrl}\"";
    context.Response.Headers.Append("WWW-Authenticate", challenge);
}

static string ResolvePublicMcpUrl(HttpContext context, ChatGptGatewayOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.PublicMcpUrl))
    {
        return options.PublicMcpUrl.Trim();
    }

    return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/mcp";
}

static string ResolvePublicResourceMetadataUrl(HttpContext context, ChatGptGatewayOptions options)
{
    if (!string.IsNullOrWhiteSpace(options.PublicResourceMetadataUrl))
    {
        return options.PublicResourceMetadataUrl.Trim();
    }

    return $"{context.Request.Scheme}://{context.Request.Host}{context.Request.PathBase}/.well-known/oauth-protected-resource/mcp-chat";
}

internal sealed record OAuthProtectedResourceMetadata(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("authorization_servers")] IReadOnlyList<string> AuthorizationServers,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported,
    [property: JsonPropertyName("bearer_methods_supported")] IReadOnlyList<string> BearerMethodsSupported,
    [property: JsonPropertyName("resource_name")] string ResourceName);

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

public partial class Program;

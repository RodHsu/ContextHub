using Memory.Application;
using Memory.ChatGptGateway;
using Memory.Infrastructure;
using ModelContextProtocol.Protocol;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using System.Text.Json.Serialization;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<ChatGptGatewayOptions>(builder.Configuration.GetSection("ChatGptGateway"));
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
    options.AddPolicy("oauth-login", context => FixedWindowByRemoteIp(context, 10, TimeSpan.FromMinutes(1)));
    options.AddPolicy("oauth-register", context => FixedWindowByRemoteIp(context, 10, TimeSpan.FromHours(1)));
    options.AddPolicy("oauth-token", context => FixedWindowByRemoteIp(context, 60, TimeSpan.FromMinutes(1)));
});
builder.Services.AddHttpContextAccessor();
builder.Services.AddHttpClient();
builder.Services.AddSingleton<IPasswordHasher<object>, PasswordHasher<object>>();
builder.Services.AddSingleton<IChatGptOAuthClientMetadataFetcher, HttpChatGptOAuthClientMetadataFetcher>();
builder.Services.AddMemoryApplication();
builder.Services.AddMemoryInfrastructure(builder.Configuration, "chatgpt-gateway");
builder.Services.AddScoped<SelfHostedOAuthService>();
builder.Services.AddSingleton<RedisOAuthStateStore>();
builder.Services.AddSingleton<PostgresOAuthClientStore>();
builder.Services.AddSingleton<PostgresOAuthTokenStateStore>();

var gatewayOptions = builder.Configuration.GetSection("ChatGptGateway").Get<ChatGptGatewayOptions>() ?? new ChatGptGatewayOptions();
if (builder.Environment.IsProduction())
{
    RequireAbsoluteHttpsUrl(gatewayOptions.PublicMcpUrl, "ChatGptGateway:PublicMcpUrl");
    RequireAbsoluteHttpsUrl(gatewayOptions.PublicResourceMetadataUrl, "ChatGptGateway:PublicResourceMetadataUrl");
}

var rsaSigningCredentials = gatewayOptions.OAuth.SelfHosted && !string.IsNullOrWhiteSpace(gatewayOptions.OAuth.SelfHostedRsaPrivateKey)
    ? new SelfHostedOAuthSigningCredentials(builder.Configuration)
    : null;
if (rsaSigningCredentials is not null) builder.Services.AddSingleton(rsaSigningCredentials);
if (gatewayOptions.OAuth.TestMode)
{
    builder.Services.AddAuthentication(GatewayAuthentication.TestScheme)
        .AddScheme<AuthenticationSchemeOptions, ChatGptTestAuthenticationHandler>(GatewayAuthentication.TestScheme, _ => { });
}
else if (gatewayOptions.OAuth.SelfHosted)
{
    builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            var issuer = Required(gatewayOptions.OAuth.SelfHostedIssuer, "ChatGptGateway:OAuth:SelfHostedIssuer").TrimEnd('/');
            options.RequireHttpsMetadata = true;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuer = issuer,
                ValidateAudience = true,
                ValidAudiences = ResolveSelfHostedAudiences(gatewayOptions),
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKey = (SecurityKey?)rsaSigningCredentials?.Key ?? SelfHostedOAuthService.BuildSigningKey(
                    Required(gatewayOptions.OAuth.SelfHostedSigningKey, "ChatGptGateway:OAuth:SelfHostedSigningKey")),
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
    .WithHttpTransport(options => options.Stateless = true)
    .WithTools<ChatGptGatewayTools>()
    .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult
    {
        Resources = []
    }));

var app = builder.Build();
var allowedMcpOrigins = ResolveAllowedOrigins(
    builder.Configuration.GetSection("ChatGptGateway:AllowedMcpOrigins").Get<string[]>(),
    gatewayOptions.PublicMcpUrl);

app.UseForwardedHeaders();
app.UseRouting();
app.UseRateLimiter();
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
app.Use(async (context, next) =>
{
    if (IsChatGptOAuthCorsPath(context.Request.Path))
    {
        ApplyChatGptOAuthCorsHeaders(context);
        if (HttpMethods.IsOptions(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status204NoContent;
            return;
        }
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
app.Use(async (context, next) =>
{
    if (IsProtectedResourceMetadataPath(context.Request.Path))
    {
        await CreateProtectedResourceMetadata(
            context,
            context.RequestServices.GetRequiredService<IOptions<ChatGptGatewayOptions>>()).ExecuteAsync(context);
        return;
    }

    if (IsAuthorizationServerMetadataPath(context.Request.Path))
    {
        await CreateAuthorizationServerMetadata(
            context,
            context.RequestServices.GetRequiredService<IOptions<ChatGptGatewayOptions>>()).ExecuteAsync(context);
        return;
    }

    if (IsOpenIdConfigurationPath(context.Request.Path))
    {
        await CreateOpenIdConfiguration(
            context,
            context.RequestServices.GetRequiredService<IOptions<ChatGptGatewayOptions>>()).ExecuteAsync(context);
        return;
    }

    if (string.Equals(context.Request.Path.Value, "/.well-known/jwks.json", StringComparison.OrdinalIgnoreCase))
    {
        var signing = context.RequestServices.GetService<SelfHostedOAuthSigningCredentials>();
        await (signing is null ? Results.NotFound() : Results.Json(signing.Jwks)).ExecuteAsync(context);
        return;
    }

    await next();
});
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
    oauthSelfHosted = options.Value.OAuth.SelfHosted,
    publicMcpUrl = options.Value.PublicMcpUrl,
    publicResourceMetadataUrl = options.Value.PublicResourceMetadataUrl,
    readTools = options.Value.ReadTools,
    directWriteTools = options.Value.DirectWriteTools,
    proposalWriteTools = options.Value.ProposalWriteTools,
    publishedTools = ChatGptGatewayToolCatalog.PublishedToolNames.Order(StringComparer.Ordinal),
    backendOnlyTools = ChatGptGatewayToolCatalog.BackendOnlyToolNames.Order(StringComparer.Ordinal),
    gatewayOnlyTools = ChatGptGatewayToolCatalog.GatewayOnlyToolNames.Order(StringComparer.Ordinal)
})).RequireAuthorization();

app.MapGet("/.well-known/oauth-protected-resource/{resource?}", CreateProtectedResourceMetadata).AllowAnonymous();

app.MapGet("/.well-known/oauth-authorization-server/{resource?}", CreateAuthorizationServerMetadata).AllowAnonymous();

app.MapGet("/.well-known/openid-configuration/{resource?}", CreateOpenIdConfiguration).AllowAnonymous();
app.MapGet("/.well-known/jwks.json", (IServiceProvider services) =>
{
    var signing = services.GetService<SelfHostedOAuthSigningCredentials>();
    return signing is null ? Results.NotFound() : Results.Json(signing.Jwks);
}).AllowAnonymous();

app.MapGet("/oauth/chat/authorize", async (
    HttpContext context,
    SelfHostedOAuthService oauth) =>
{
    var validation = await oauth.ValidateAuthorizeRequestAsync(context.Request.Query, context.RequestAborted);
    if (!validation.Success || validation.Request is null)
    {
        return Results.Content(RenderOAuthError(validation.Error), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
    }

    return Results.Content(RenderOAuthLogin(validation.Request, string.Empty), "text/html", Encoding.UTF8);
}).AllowAnonymous().RequireRateLimiting("oauth-login");

app.MapPost("/oauth/chat/authorize", async (
    HttpContext context,
    SelfHostedOAuthService oauth) =>
{
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var validation = await oauth.ValidateAuthorizeRequestAsync(context.Request.Query, context.RequestAborted);
    if (!validation.Success || validation.Request is null)
    {
        return Results.Content(RenderOAuthError(validation.Error), "text/html", Encoding.UTF8, StatusCodes.Status400BadRequest);
    }

    var result = await oauth.AuthorizeAsync(
        validation.Request,
        form["username"].ToString(),
        form["password"].ToString(),
        context.RequestAborted);
    if (!result.Success)
    {
        return Results.Content(RenderOAuthLogin(validation.Request, result.Error), "text/html", Encoding.UTF8, StatusCodes.Status401Unauthorized);
    }

    return Results.Redirect(result.RedirectUri, permanent: false, preserveMethod: false);
}).AllowAnonymous().RequireRateLimiting("oauth-login");

app.MapPost("/oauth/chat/register", async (
    HttpContext context,
    SelfHostedOAuthService oauth) =>
{
    OAuthClientRegistrationRequest? request;
    try
    {
        request = await context.Request.ReadFromJsonAsync<OAuthClientRegistrationRequest>(context.RequestAborted);
    }
    catch (Exception ex) when (ex is BadHttpRequestException or System.Text.Json.JsonException)
    {
        return Results.Json(
            new OAuthError("invalid_client_metadata", "Registration payload must be valid JSON."),
            statusCode: StatusCodes.Status400BadRequest);
    }

    if (request is null)
    {
        return Results.Json(new OAuthError("invalid_client_metadata", "Registration payload is required."), statusCode: StatusCodes.Status400BadRequest);
    }

    var result = await oauth.RegisterClientAsync(request, context.RequestAborted);
    if (!result.Success || result.Registration is null)
    {
        return Results.Json(new OAuthError("invalid_client_metadata", result.Error), statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Json(result.Registration, statusCode: StatusCodes.Status201Created);
}).AllowAnonymous().RequireRateLimiting("oauth-register");

app.MapPost("/oauth/chat/token", async (
    HttpContext context,
    SelfHostedOAuthService oauth) =>
{
    var form = await context.Request.ReadFormAsync(context.RequestAborted);
    var (basicClientId, basicClientSecret) = ReadBasicClientCredentials(context.Request.Headers.Authorization.ToString());
    var clientId = string.IsNullOrWhiteSpace(basicClientId) ? form["client_id"].ToString() : basicClientId;
    var clientSecret = string.IsNullOrWhiteSpace(basicClientSecret) ? form["client_secret"].ToString() : basicClientSecret;
    var grantType = form["grant_type"].ToString();
    var result = grantType switch
    {
        "authorization_code" => await oauth.ExchangeCodeAsync(
            form["code"].ToString(),
            form["redirect_uri"].ToString(),
            clientId,
            clientSecret,
            form["code_verifier"].ToString(),
            form["resource"].ToString()),
        "refresh_token" => await oauth.RefreshAccessTokenAsync(
            form["refresh_token"].ToString(),
            clientId,
            clientSecret),
        _ => OAuthTokenResult.Fail("Only authorization_code and refresh_token are supported.")
    };
    if (!string.Equals(grantType, "authorization_code", StringComparison.Ordinal) &&
        !string.Equals(grantType, "refresh_token", StringComparison.Ordinal))
    {
        return Results.Json(new OAuthError("unsupported_grant_type", result.Error), statusCode: StatusCodes.Status400BadRequest);
    }

    if (!result.Success)
    {
        return Results.Json(new OAuthError("invalid_grant", result.Error), statusCode: StatusCodes.Status400BadRequest);
    }

    return Results.Json(new OAuthTokenResponse(
        result.AccessToken,
        string.IsNullOrWhiteSpace(result.IdToken) ? null : result.IdToken,
        string.IsNullOrWhiteSpace(result.RefreshToken) ? null : result.RefreshToken,
        "Bearer",
        result.ExpiresIn,
        result.Scope));
}).AllowAnonymous().RequireRateLimiting("oauth-token");

app.MapGet("/userinfo", (ClaimsPrincipal user) => Results.Json(CreateUserInfo(user))).RequireAuthorization();

app.MapMcp("/mcp").RequireAuthorization();

app.Run();

static RateLimitPartition<string> FixedWindowByRemoteIp(
    HttpContext context,
    int permitLimit,
    TimeSpan window)
    => RateLimitPartition.GetFixedWindowLimiter(
        context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = permitLimit,
            Window = window,
            QueueLimit = 0,
            AutoReplenishment = true
        });

static void AddTrustedForwarders(ForwardedHeadersOptions options, IConfiguration configuration)
{
    foreach (var value in configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [])
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        if (!IPAddress.TryParse(value, out var address))
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

static HashSet<string> ResolveAllowedOrigins(IEnumerable<string>? configuredValues, params string[] publicUrls)
{
    var origins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var value in (configuredValues ?? []).Concat(publicUrls))
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            continue;
        }

        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp) ||
            !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new InvalidOperationException($"ChatGptGateway MCP origin configuration contains invalid URL '{value}'.");
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

static bool IsPublicPath(PathString path)
{
    var value = path.Value ?? string.Empty;
    return value.StartsWith("/health/live", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("/health/ready", StringComparison.OrdinalIgnoreCase) ||
           IsProtectedResourceMetadataPath(path) ||
           IsAuthorizationServerMetadataPath(path) ||
           IsOpenIdConfigurationPath(path) ||
           string.Equals(path.Value, "/.well-known/jwks.json", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("/oauth/chat/authorize", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("/oauth/chat/register", StringComparison.OrdinalIgnoreCase) ||
           value.StartsWith("/oauth/chat/token", StringComparison.OrdinalIgnoreCase);
}

static bool IsChatGptOAuthCorsPath(PathString path)
    => IsProtectedResourceMetadataPath(path) ||
       IsAuthorizationServerMetadataPath(path) ||
       IsOpenIdConfigurationPath(path) ||
       string.Equals(path.Value, "/.well-known/jwks.json", StringComparison.OrdinalIgnoreCase) ||
       (path.Value ?? string.Empty).StartsWith("/oauth/chat/", StringComparison.OrdinalIgnoreCase);

static void ApplyChatGptOAuthCorsHeaders(HttpContext context)
{
    var origin = context.Request.Headers.Origin.ToString();
    if (IsAllowedChatGptOrigin(origin))
    {
        context.Response.Headers["Access-Control-Allow-Origin"] = origin;
        context.Response.Headers.Append("Vary", "Origin");
    }

    context.Response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
    context.Response.Headers["Access-Control-Allow-Headers"] = "authorization, content-type, accept, mcp-session-id, mcp-protocol-version";
    context.Response.Headers["Access-Control-Max-Age"] = "600";
}

static bool IsAllowedChatGptOrigin(string origin)
{
    if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) ||
        uri.Scheme != Uri.UriSchemeHttps)
    {
        return false;
    }

    return string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(uri.Host, "chat.openai.com", StringComparison.OrdinalIgnoreCase);
}

static bool IsProtectedResourceMetadataPath(PathString path)
{
    var value = path.Value ?? string.Empty;
    return string.Equals(value, "/.well-known/oauth-protected-resource", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "/.well-known/oauth-protected-resource/mcp-chat", StringComparison.OrdinalIgnoreCase);
}

static bool IsAuthorizationServerMetadataPath(PathString path)
{
    var value = path.Value ?? string.Empty;
    return string.Equals(value, "/.well-known/oauth-authorization-server", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "/.well-known/oauth-authorization-server/mcp-chat", StringComparison.OrdinalIgnoreCase);
}

static bool IsOpenIdConfigurationPath(PathString path)
{
    var value = path.Value ?? string.Empty;
    return string.Equals(value, "/.well-known/openid-configuration", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(value, "/.well-known/openid-configuration/mcp-chat", StringComparison.OrdinalIgnoreCase);
}

static string Required(string value, string key)
    => string.IsNullOrWhiteSpace(value)
        ? throw new InvalidOperationException($"{key} is required when ChatGptGateway:OAuth:TestMode is false.")
        : value.Trim();

static void RequireAbsoluteHttpsUrl(string? value, string key)
{
    if (string.IsNullOrWhiteSpace(value) ||
        !Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
        !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(uri.Host))
    {
        throw new InvalidOperationException($"{key} must be an absolute HTTPS URL in Production.");
    }
}

static string[] ResolveSelfHostedAudiences(ChatGptGatewayOptions options)
{
    var audiences = new List<string>
    {
        Required(options.OAuth.ClientId, "ChatGptGateway:OAuth:ClientId")
    };
    if (!string.IsNullOrWhiteSpace(options.PublicMcpUrl))
    {
        audiences.Add(options.PublicMcpUrl.Trim());
    }

    return audiences.Distinct(StringComparer.Ordinal).ToArray();
}

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

static IResult CreateProtectedResourceMetadata(HttpContext context, IOptions<ChatGptGatewayOptions> options)
{
    var value = options.Value;
    var publicMcpUrl = ResolvePublicMcpUrl(context, value);
    var authorizationServer = SelfHostedOAuthService.ResolveIssuer(context, value);
    var metadata = new OAuthProtectedResourceMetadata(
        publicMcpUrl,
        [authorizationServer],
        NormalizeScopes(value.OAuth.Scopes),
        ["header"],
        "ContextHub MCP Chat Gateway");

    return Results.Json(metadata);
}

static IResult CreateAuthorizationServerMetadata(HttpContext context, IOptions<ChatGptGatewayOptions> options)
{
    var value = options.Value;
    var issuer = SelfHostedOAuthService.ResolveIssuer(context, value);
    var metadata = new OAuthAuthorizationServerMetadata(
        issuer,
        $"{issuer}/oauth/chat/authorize",
        $"{issuer}/oauth/chat/token",
        $"{issuer}/oauth/chat/register",
        ["code"],
        ["authorization_code", "refresh_token"],
        ["S256"],
        GetTokenEndpointAuthenticationMethods(value.OAuth),
        true,
        value.OAuth.IncludeIssuerInAuthorizationResponse,
        NormalizeScopes(value.OAuth.Scopes));

    return Results.Json(metadata);
}

static IResult CreateOpenIdConfiguration(HttpContext context, IOptions<ChatGptGatewayOptions> options)
{
    var value = options.Value;
    var issuer = SelfHostedOAuthService.ResolveIssuer(context, value);
    var metadata = new OpenIdConfigurationMetadata(
        issuer,
        $"{issuer}/oauth/chat/authorize",
        $"{issuer}/oauth/chat/token",
        $"{issuer}/oauth/chat/register",
        $"{issuer}/userinfo",
        $"{issuer}/.well-known/jwks.json",
        ["code"],
        ["authorization_code", "refresh_token"],
        ["S256"],
        GetTokenEndpointAuthenticationMethods(value.OAuth),
        true,
        value.OAuth.IncludeIssuerInAuthorizationResponse,
        ["public"],
        [string.IsNullOrWhiteSpace(value.OAuth.SelfHostedRsaPrivateKey) ? "HS256" : "RS256"],
        NormalizeScopes(value.OAuth.Scopes),
        ["sub", "name", "email", "tenant_id", "tenant_user_id"]);

    return Results.Json(metadata);
}

static OpenIdUserInfo CreateUserInfo(ClaimsPrincipal user)
{
    var subject = user.FindFirstValue("sub") ??
                  user.FindFirstValue(ClaimTypes.NameIdentifier) ??
                  user.Identity?.Name ??
                  string.Empty;
    var name = user.FindFirstValue("name") ??
               user.FindFirstValue(ClaimTypes.Name) ??
               subject;
    var email = user.FindFirstValue("email") ??
                user.FindFirstValue(ClaimTypes.Email) ??
                string.Empty;

    return new OpenIdUserInfo(
        subject,
        name,
        email,
        user.FindFirstValue("tenant_id"),
        user.FindFirstValue("tenant_user_id"));
}

static string[] NormalizeScopes(IEnumerable<string> scopes)
    => scopes
        .Where(scope => !string.IsNullOrWhiteSpace(scope))
        .Select(scope => scope.Trim())
        .Distinct(StringComparer.Ordinal)
        .ToArray();

static string[] GetTokenEndpointAuthenticationMethods(OAuthOptions options)
    => string.IsNullOrWhiteSpace(options.ClientSecret)
        ? ["none"]
        : ["none", "client_secret_basic", "client_secret_post"];

static (string ClientId, string ClientSecret) ReadBasicClientCredentials(string authorization)
{
    if (!authorization.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
    {
        return (string.Empty, string.Empty);
    }

    try
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(authorization["Basic ".Length..].Trim()));
        var separator = decoded.IndexOf(':', StringComparison.Ordinal);
        return separator < 0
            ? (decoded, string.Empty)
            : (decoded[..separator], decoded[(separator + 1)..]);
    }
    catch
    {
        return (string.Empty, string.Empty);
    }
}

static string RenderOAuthLogin(AuthorizeRequest request, string error)
{
    var errorHtml = string.IsNullOrWhiteSpace(error)
        ? string.Empty
        : $"""<p class="error">{WebUtility.HtmlEncode(error)}</p>""";
    return $$"""
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>Connect ContextHub</title>
  <style>
    body { font-family: system-ui, sans-serif; margin: 0; min-height: 100vh; display: grid; place-items: center; background: #f7f7f3; color: #1e2722; }
    main { width: min(420px, calc(100vw - 32px)); background: #fff; border: 1px solid #d7dbd4; border-radius: 8px; padding: 24px; box-shadow: 0 12px 40px rgba(20, 30, 24, .08); }
    h1 { font-size: 1.25rem; margin: 0 0 8px; }
    p { color: #556159; }
    label { display: grid; gap: 6px; margin-top: 16px; font-size: .9rem; font-weight: 600; }
    input { font: inherit; padding: 10px 12px; border: 1px solid #b9c0b8; border-radius: 6px; }
    button { margin-top: 20px; width: 100%; border: 0; border-radius: 6px; padding: 11px 14px; background: #1d6b4f; color: #fff; font: inherit; font-weight: 700; }
    .error { color: #a52822; font-weight: 600; }
  </style>
</head>
<body>
  <main>
    <h1>Connect ContextHub</h1>
    <p>Sign in with your ContextHub account to authorize ChatGPT MCP access.</p>
    {{errorHtml}}
    <form method="post">
      <label>Username <input name="username" autocomplete="username" required></label>
      <label>Password <input name="password" type="password" autocomplete="current-password" required></label>
      <button type="submit">Authorize</button>
    </form>
  </main>
</body>
</html>
""";
}

static string RenderOAuthError(string error)
    => $$"""
<!doctype html>
<html lang="en">
<head><meta charset="utf-8"><title>ContextHub OAuth Error</title></head>
<body><h1>ContextHub OAuth Error</h1><p>{{WebUtility.HtmlEncode(error)}}</p></body>
</html>
""";

internal sealed record OAuthProtectedResourceMetadata(
    [property: JsonPropertyName("resource")] string Resource,
    [property: JsonPropertyName("authorization_servers")] IReadOnlyList<string> AuthorizationServers,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported,
    [property: JsonPropertyName("bearer_methods_supported")] IReadOnlyList<string> BearerMethodsSupported,
    [property: JsonPropertyName("resource_name")] string ResourceName);

internal sealed record OAuthAuthorizationServerMetadata(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("registration_endpoint")] string RegistrationEndpoint,
    [property: JsonPropertyName("response_types_supported")] IReadOnlyList<string> ResponseTypesSupported,
    [property: JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypesSupported,
    [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string> CodeChallengeMethodsSupported,
    [property: JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
    [property: JsonPropertyName("client_id_metadata_document_supported")] bool ClientIdMetadataDocumentSupported,
    [property: JsonPropertyName("authorization_response_iss_parameter_supported")] bool AuthorizationResponseIssParameterSupported,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported);

internal sealed record OpenIdConfigurationMetadata(
    [property: JsonPropertyName("issuer")] string Issuer,
    [property: JsonPropertyName("authorization_endpoint")] string AuthorizationEndpoint,
    [property: JsonPropertyName("token_endpoint")] string TokenEndpoint,
    [property: JsonPropertyName("registration_endpoint")] string RegistrationEndpoint,
    [property: JsonPropertyName("userinfo_endpoint")] string UserInfoEndpoint,
    [property: JsonPropertyName("jwks_uri")] string JwksUri,
    [property: JsonPropertyName("response_types_supported")] IReadOnlyList<string> ResponseTypesSupported,
    [property: JsonPropertyName("grant_types_supported")] IReadOnlyList<string> GrantTypesSupported,
    [property: JsonPropertyName("code_challenge_methods_supported")] IReadOnlyList<string> CodeChallengeMethodsSupported,
    [property: JsonPropertyName("token_endpoint_auth_methods_supported")] IReadOnlyList<string> TokenEndpointAuthMethodsSupported,
    [property: JsonPropertyName("client_id_metadata_document_supported")] bool ClientIdMetadataDocumentSupported,
    [property: JsonPropertyName("authorization_response_iss_parameter_supported")] bool AuthorizationResponseIssParameterSupported,
    [property: JsonPropertyName("subject_types_supported")] IReadOnlyList<string> SubjectTypesSupported,
    [property: JsonPropertyName("id_token_signing_alg_values_supported")] IReadOnlyList<string> IdTokenSigningAlgValuesSupported,
    [property: JsonPropertyName("scopes_supported")] IReadOnlyList<string> ScopesSupported,
    [property: JsonPropertyName("claims_supported")] IReadOnlyList<string> ClaimsSupported);

internal sealed record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("id_token")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? IdToken,
    [property: JsonPropertyName("refresh_token")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("scope")] string Scope);

internal sealed record OpenIdUserInfo(
    [property: JsonPropertyName("sub")] string Subject,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("email")] string Email,
    [property: JsonPropertyName("tenant_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TenantId,
    [property: JsonPropertyName("tenant_user_id")]
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    string? TenantUserId);

internal sealed record OAuthError(
    [property: JsonPropertyName("error")] string Error,
    [property: JsonPropertyName("error_description")] string ErrorDescription);

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

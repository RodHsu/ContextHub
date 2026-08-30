using System.Security.Claims;
using System.Text.Encodings.Web;
using Memory.Application;
using Memory.Domain;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.ChatGptGateway;

internal static class GatewayAuthentication
{
    public const string TestScheme = "ChatGptGatewayTest";
    public const string SubjectClaim = "chatgpt:subject";
}

internal sealed class ChatGptTestAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptions<ChatGptGatewayOptions> gatewayOptions)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var configured = gatewayOptions.Value.OAuth.TestBearerToken;
        var authorization = Request.Headers.Authorization.ToString();
        if (string.IsNullOrWhiteSpace(configured) ||
            !authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(authorization["Bearer ".Length..].Trim(), configured, StringComparison.Ordinal))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var oauth = gatewayOptions.Value.OAuth;
        var claims = new List<Claim>
        {
            new(GatewayAuthentication.SubjectClaim, oauth.TestSubject),
            new(ClaimTypes.NameIdentifier, oauth.TestSubject),
            new(ClaimTypes.Name, oauth.TestName),
            new(ClaimTypes.Email, oauth.TestEmail),
            new("scope", string.Join(' ', oauth.Scopes))
        };
        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, GatewayAuthentication.TestScheme));
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, GatewayAuthentication.TestScheme)));
    }
}

internal sealed class ChatGptGatewayActorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(
        HttpContext context,
        IRequestActorAccessor actorAccessor,
        IOptions<ChatGptGatewayOptions> gatewayOptions,
        IOptions<ContextHubOptions> contextHubOptions,
        IApplicationDbContext dbContext)
    {
        if (IsHealthCheck(context.Request.Path))
        {
            await next(context);
            return;
        }

        if (context.User.Identity?.IsAuthenticated != true)
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return;
        }

        var oauth = gatewayOptions.Value.OAuth;
        var surface = ChatGptGatewaySurfaceResolver.Resolve(gatewayOptions.Value.Surface);
        var subject = ReadClaim(context.User, GatewayAuthentication.SubjectClaim, ClaimTypes.NameIdentifier, oauth.SubjectClaim);
        if (string.IsNullOrWhiteSpace(subject))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }

        var tenantId = ReadGuidClaim(context.User, "tenant_id");
        var userId = ReadGuidClaim(context.User, "tenant_user_id");
        var contextHubUser = tenantId.HasValue && userId.HasValue
            ? await dbContext.TenantUsers
                .Include(x => x.Tenant)
                .FirstOrDefaultAsync(
                    x => x.Id == userId.Value &&
                         x.TenantId == tenantId.Value &&
                         x.Status == TenantUserStatus.Active &&
                         x.Tenant != null &&
                         x.Tenant.Status == TenantStatus.Active,
                    context.RequestAborted)
            : null;

        if (contextHubUser is null && gatewayOptions.Value.OAuth.TestMode)
        {
            contextHubUser = await ResolveTestServiceUserAsync(
                dbContext,
                contextHubOptions.Value.Security.BootstrapUsername,
                context.RequestAborted);
        }

        if (contextHubUser is null)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("ChatGPT OAuth identity is not linked to an active ContextHub user.", context.RequestAborted);
            return;
        }

        var grantedOAuthScopes = (context.User.FindFirstValue("scope") ?? string.Empty)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToHashSet(StringComparer.Ordinal);
        if (surface == ChatGptGatewaySurface.Automation &&
            !grantedOAuthScopes.Contains(SecurityScopes.ScheduledGovernance))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsync("Scheduled Governance OAuth scope is required.", context.RequestAborted);
            return;
        }

        var applicationScopes = surface == ChatGptGatewaySurface.Automation
            ? new[] { SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.ScheduledGovernance }
            : new[]
            {
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite,
                SecurityScopes.LogsRead,
                SecurityScopes.GovernanceTrackerManage
            };

        var previous = actorAccessor.Current;
        actorAccessor.Current = new ContextHubRequestActor(
            contextHubUser.TenantId,
            contextHubUser.Id,
            contextHubUser.Username,
            contextHubUser.Role,
            applicationScopes,
            [],
            IsAuthenticated: true,
            IsServiceActor: false);
        try
        {
            await next(context);
        }
        finally
        {
            actorAccessor.Current = previous;
        }
    }

    private static async Task<TenantUser?> ResolveTestServiceUserAsync(
        IApplicationDbContext dbContext,
        string bootstrapUsername,
        CancellationToken cancellationToken)
    {
        var username = NormalizeUsername(bootstrapUsername);
        return await dbContext.TenantUsers
            .Include(x => x.Tenant)
            .FirstOrDefaultAsync(
                x => x.Username == username &&
                     x.Status == TenantUserStatus.Active &&
                     x.Tenant != null &&
                     x.Tenant.Status == TenantStatus.Active,
                cancellationToken);
    }

    private static string NormalizeUsername(string value)
        => value.Trim().ToLowerInvariant();

    private static string? ReadClaim(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirstValue(type);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private static Guid? ReadGuidClaim(ClaimsPrincipal principal, string type)
        => Guid.TryParse(principal.FindFirstValue(type), out var value) ? value : null;

    private static bool IsHealthCheck(PathString path)
        => path.StartsWithSegments("/health/live", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/health/ready", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/.well-known/oauth-protected-resource/mcp-chat", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/.well-known/oauth-authorization-server/mcp-chat", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/.well-known/openid-configuration/mcp-chat", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/oauth/chat/authorize", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/oauth/chat/register", StringComparison.OrdinalIgnoreCase) ||
           path.StartsWithSegments("/oauth/chat/token", StringComparison.OrdinalIgnoreCase);
}

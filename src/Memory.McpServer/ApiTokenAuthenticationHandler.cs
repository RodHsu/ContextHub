using System.Security.Claims;
using System.Text.Encodings.Web;
using Memory.Application;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;

namespace Memory.McpServer;

public static class ContextHubAuthentication
{
    public const string Scheme = "ContextHubToken";
    public const string TenantIdClaim = "contexthub:tenant_id";
    public const string TenantSlugClaim = "contexthub:tenant_slug";
    public const string UserIdClaim = "contexthub:user_id";
    public const string TokenIdClaim = "contexthub:token_id";
    public const string ScopeClaim = "scope";
    public const string ProjectClaim = "contexthub:project_id";
    public const string UsernameClaim = "contexthub:username";
}

public sealed class ApiTokenAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ITenantSecurityService securityService)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var token = ReadToken();
        if (string.IsNullOrWhiteSpace(token))
        {
            return AuthenticateResult.NoResult();
        }

        var remoteIp = Context.Connection.RemoteIpAddress?.ToString() ?? string.Empty;
        var userAgent = Request.Headers.UserAgent.ToString();
        var result = await securityService.AuthenticateTokenAsync(token, remoteIp, userAgent, Context.RequestAborted);
        if (!result.Succeeded)
        {
            return AuthenticateResult.Fail(result.FailureReason);
        }

        var claims = new List<Claim>
        {
            new(ContextHubAuthentication.TenantIdClaim, result.TenantId!.Value.ToString()),
            new(ContextHubAuthentication.UserIdClaim, result.OwnerUserId!.Value.ToString()),
            new(ContextHubAuthentication.TokenIdClaim, result.ApiTokenId!.Value.ToString())
        };

        if (!string.IsNullOrWhiteSpace(result.Username))
        {
            claims.Add(new Claim(ContextHubAuthentication.UsernameClaim, result.Username));
        }

        if (result.Role.HasValue)
        {
            claims.Add(new Claim(ClaimTypes.Role, result.Role.Value.ToString()));
        }

        if (!string.IsNullOrWhiteSpace(result.TenantSlug))
        {
            claims.Add(new Claim(ContextHubAuthentication.TenantSlugClaim, result.TenantSlug));
        }

        foreach (var scope in result.Scopes ?? [])
        {
            claims.Add(new Claim(ContextHubAuthentication.ScopeClaim, scope));
        }

        foreach (var projectId in result.AllowedProjectIds ?? [])
        {
            claims.Add(new Claim(ContextHubAuthentication.ProjectClaim, projectId));
        }

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, ContextHubAuthentication.Scheme));
        return AuthenticateResult.Success(new AuthenticationTicket(principal, ContextHubAuthentication.Scheme));
    }

    private string? ReadToken()
    {
        var authorization = Request.Headers.Authorization.ToString();
        if (authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return authorization["Bearer ".Length..].Trim();
        }

        return Request.Headers.TryGetValue("X-ContextHub-Token", out var headerValues)
            ? headerValues.FirstOrDefault()
            : null;
    }
}

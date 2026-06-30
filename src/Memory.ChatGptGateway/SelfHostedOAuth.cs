using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Memory.Application;
using Memory.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Memory.ChatGptGateway;

internal sealed class SelfHostedOAuthService(
    IApplicationDbContext dbContext,
    IPasswordHasher<object> passwordHasher,
    IMemoryCache cache,
    IOptions<ChatGptGatewayOptions> gatewayOptions)
{
    private const string CodeCachePrefix = "chatgpt-oauth-code:";
    private static readonly JwtSecurityTokenHandler TokenHandler = new();

    public async Task<AuthorizeValidationResult> ValidateAuthorizeRequestAsync(
        IQueryCollection query,
        CancellationToken cancellationToken)
    {
        var options = gatewayOptions.Value.OAuth;
        var clientId = query["client_id"].ToString();
        var redirectUri = query["redirect_uri"].ToString();
        var responseType = query["response_type"].ToString();
        var codeChallenge = query["code_challenge"].ToString();
        var codeChallengeMethod = query["code_challenge_method"].ToString();
        var scope = NormalizeScopes(query["scope"].ToString(), options.Scopes);
        var state = query["state"].ToString();

        if (!options.SelfHosted)
        {
            return AuthorizeValidationResult.Fail("Self-hosted OAuth is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(clientId) ||
            !string.Equals(clientId, options.ClientId, StringComparison.Ordinal))
        {
            return AuthorizeValidationResult.Fail("Invalid OAuth client.");
        }

        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
        {
            return AuthorizeValidationResult.Fail("Unsupported OAuth response_type.");
        }

        if (!IsRedirectUriAllowed(redirectUri, options))
        {
            return AuthorizeValidationResult.Fail("Redirect URI is not allowed.");
        }

        if (string.IsNullOrWhiteSpace(codeChallenge) ||
            !string.Equals(codeChallengeMethod, "S256", StringComparison.OrdinalIgnoreCase))
        {
            return AuthorizeValidationResult.Fail("PKCE S256 is required.");
        }

        await Task.CompletedTask;
        return AuthorizeValidationResult.Ok(new AuthorizeRequest(
            clientId,
            redirectUri,
            scope,
            state,
            codeChallenge,
            "S256"));
    }

    public async Task<AuthorizeResult> AuthorizeAsync(
        AuthorizeRequest request,
        string username,
        string password,
        CancellationToken cancellationToken)
    {
        var normalizedUsername = username.Trim().ToLowerInvariant();
        var user = await dbContext.TenantUsers
            .Include(x => x.Tenant)
            .FirstOrDefaultAsync(
                x => x.Username == normalizedUsername &&
                     x.Status == TenantUserStatus.Active &&
                     x.Tenant != null &&
                     x.Tenant.Status == TenantStatus.Active,
                cancellationToken);
        if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash))
        {
            return AuthorizeResult.Fail("Invalid username or password.");
        }

        var verification = passwordHasher.VerifyHashedPassword(new object(), user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            return AuthorizeResult.Fail("Invalid username or password.");
        }

        user.LastLoginAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        var code = CreateTokenValue(32);
        var options = gatewayOptions.Value.OAuth;
        var payload = new AuthorizationCodePayload(
            request.ClientId,
            request.RedirectUri,
            request.Scope,
            request.CodeChallenge,
            user.Id,
            user.TenantId,
            user.Username,
            user.Email,
            user.DisplayName);
        cache.Set(
            CodeCachePrefix + code,
            payload,
            TimeSpan.FromMinutes(Math.Max(1, options.AuthorizationCodeLifetimeMinutes)));

        var redirect = QueryString.Create(new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = request.State
        });
        return AuthorizeResult.Ok(request.RedirectUri + redirect);
    }

    public OAuthTokenResult ExchangeCode(
        string code,
        string redirectUri,
        string clientId,
        string? clientSecret,
        string codeVerifier)
    {
        var options = gatewayOptions.Value.OAuth;
        if (!options.SelfHosted)
        {
            return OAuthTokenResult.Fail("Self-hosted OAuth is not enabled.");
        }

        if (!string.Equals(clientId, options.ClientId, StringComparison.Ordinal) ||
            (!string.IsNullOrWhiteSpace(options.ClientSecret) &&
             !string.Equals(clientSecret, options.ClientSecret, StringComparison.Ordinal)))
        {
            return OAuthTokenResult.Fail("Invalid OAuth client.");
        }

        if (string.IsNullOrWhiteSpace(code) ||
            !cache.TryGetValue<AuthorizationCodePayload>(CodeCachePrefix + code, out var payload) ||
            payload is null)
        {
            return OAuthTokenResult.Fail("Invalid authorization code.");
        }

        cache.Remove(CodeCachePrefix + code);

        if (!string.Equals(payload.RedirectUri, redirectUri, StringComparison.Ordinal) ||
            !string.Equals(payload.ClientId, clientId, StringComparison.Ordinal))
        {
            return OAuthTokenResult.Fail("Authorization code binding mismatch.");
        }

        if (!ValidatePkce(payload.CodeChallenge, codeVerifier))
        {
            return OAuthTokenResult.Fail("Invalid PKCE verifier.");
        }

        var token = CreateAccessToken(payload, options);
        return OAuthTokenResult.Ok(token, Math.Max(1, options.AccessTokenLifetimeMinutes) * 60, payload.Scope);
    }

    public static string ResolveIssuer(HttpContext context, ChatGptGatewayOptions options)
    {
        if (options.OAuth.SelfHosted && !string.IsNullOrWhiteSpace(options.OAuth.SelfHostedIssuer))
        {
            return options.OAuth.SelfHostedIssuer.Trim().TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(options.OAuth.Authority))
        {
            return options.OAuth.Authority.Trim().TrimEnd('/');
        }

        return $"{context.Request.Scheme}://{context.Request.Host}".TrimEnd('/');
    }

    public static SymmetricSecurityKey BuildSigningKey(string signingKey)
    {
        var trimmed = signingKey.Trim();
        var utf8Bytes = Encoding.UTF8.GetBytes(trimmed);
        var base64Buffer = new byte[Math.Max(32, utf8Bytes.Length)];
        var bytes = Convert.TryFromBase64String(trimmed, base64Buffer, out var bytesWritten) &&
                    bytesWritten >= 32
            ? base64Buffer[..bytesWritten]
            : utf8Bytes;
        if (bytes.Length < 32)
        {
            throw new InvalidOperationException("ChatGptGateway:OAuth:SelfHostedSigningKey must be at least 32 bytes.");
        }

        return new SymmetricSecurityKey(bytes);
    }

    private string CreateAccessToken(AuthorizationCodePayload payload, OAuthOptions options)
    {
        var issuer = string.IsNullOrWhiteSpace(options.SelfHostedIssuer)
            ? throw new InvalidOperationException("ChatGptGateway:OAuth:SelfHostedIssuer is required when SelfHosted is true.")
            : options.SelfHostedIssuer.Trim().TrimEnd('/');
        var now = DateTime.UtcNow;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, payload.Username),
            new(ClaimTypes.NameIdentifier, payload.Username),
            new(ClaimTypes.Name, string.IsNullOrWhiteSpace(payload.DisplayName) ? payload.Username : payload.DisplayName),
            new(ClaimTypes.Email, payload.Email),
            new("scope", payload.Scope),
            new("tenant_id", payload.TenantId.ToString("D")),
            new("tenant_user_id", payload.UserId.ToString("D"))
        };
        var token = new JwtSecurityToken(
            issuer,
            options.ClientId,
            claims,
            now,
            now.AddMinutes(Math.Max(1, options.AccessTokenLifetimeMinutes)),
            new SigningCredentials(BuildSigningKey(options.SelfHostedSigningKey), SecurityAlgorithms.HmacSha256));
        return TokenHandler.WriteToken(token);
    }

    private static bool IsRedirectUriAllowed(string redirectUri, OAuthOptions options)
    {
        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttps)
        {
            return false;
        }

        if (options.AllowedRedirectUris.Any(x => string.Equals(x.Trim(), redirectUri, StringComparison.Ordinal)))
        {
            return true;
        }

        return options.AllowedRedirectUriPrefixes
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Any(prefix => redirectUri.StartsWith(prefix.Trim(), StringComparison.Ordinal));
    }

    private static string NormalizeScopes(string requested, IReadOnlyList<string> supported)
    {
        var supportedSet = supported
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .ToHashSet(StringComparer.Ordinal);
        var requestedScopes = requested
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var accepted = requestedScopes.Length == 0
            ? supportedSet
            : requestedScopes.Where(supportedSet.Contains);
        return string.Join(' ', accepted);
    }

    private static bool ValidatePkce(string expectedChallenge, string codeVerifier)
        => string.Equals(expectedChallenge, Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(codeVerifier))), StringComparison.Ordinal);

    private static string CreateTokenValue(int bytes)
    {
        var buffer = RandomNumberGenerator.GetBytes(bytes);
        return Base64UrlEncode(buffer);
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

internal sealed record AuthorizeRequest(
    string ClientId,
    string RedirectUri,
    string Scope,
    string State,
    string CodeChallenge,
    string CodeChallengeMethod);

internal sealed record AuthorizeValidationResult(bool Success, AuthorizeRequest? Request, string Error)
{
    public static AuthorizeValidationResult Ok(AuthorizeRequest request) => new(true, request, string.Empty);
    public static AuthorizeValidationResult Fail(string error) => new(false, null, error);
}

internal sealed record AuthorizeResult(bool Success, string RedirectUri, string Error)
{
    public static AuthorizeResult Ok(string redirectUri) => new(true, redirectUri, string.Empty);
    public static AuthorizeResult Fail(string error) => new(false, string.Empty, error);
}

internal sealed record OAuthTokenResult(bool Success, string AccessToken, int ExpiresIn, string Scope, string Error)
{
    public static OAuthTokenResult Ok(string accessToken, int expiresIn, string scope) => new(true, accessToken, expiresIn, scope, string.Empty);
    public static OAuthTokenResult Fail(string error) => new(false, string.Empty, 0, string.Empty, error);
}

internal sealed record AuthorizationCodePayload(
    string ClientId,
    string RedirectUri,
    string Scope,
    string CodeChallenge,
    Guid UserId,
    Guid TenantId,
    string Username,
    string Email,
    string DisplayName);

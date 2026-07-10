using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Memory.Application;
using Memory.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Memory.ChatGptGateway;

internal sealed class SelfHostedOAuthService(
    IApplicationDbContext dbContext,
    IPasswordHasher<object> passwordHasher,
    RedisOAuthStateStore stateStore,
    PostgresOAuthClientStore clientStore,
    IOptions<ChatGptGatewayOptions> gatewayOptions,
    IChatGptOAuthClientMetadataFetcher clientMetadataFetcher,
    ILogger<SelfHostedOAuthService> logger,
    SelfHostedOAuthSigningCredentials? rsaSigningCredentials = null)
{
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
        var resource = query["resource"].ToString();

        if (!options.SelfHosted)
        {
            LogOAuthAuthorizeValidation(clientId, redirectUri, scope, resource, "failed", "self_hosted_disabled");
            return AuthorizeValidationResult.Fail("Self-hosted OAuth is not enabled.");
        }

        var clientValidation = await ValidateAuthorizeClientAsync(clientId, redirectUri, options, cancellationToken);
        if (!clientValidation.Success)
        {
            LogOAuthAuthorizeValidation(clientId, redirectUri, scope, resource, "failed", clientValidation.FailureReason);
            return AuthorizeValidationResult.Fail("Invalid OAuth client.");
        }

        if (!string.Equals(responseType, "code", StringComparison.Ordinal))
        {
            LogOAuthAuthorizeValidation(clientId, redirectUri, scope, resource, "failed", "unsupported_response_type");
            return AuthorizeValidationResult.Fail("Unsupported OAuth response_type.");
        }

        if (!IsRedirectUriAllowed(redirectUri, options))
        {
            LogOAuthAuthorizeValidation(clientId, redirectUri, scope, resource, "failed", "redirect_not_allowed");
            return AuthorizeValidationResult.Fail("Redirect URI is not allowed.");
        }

        if (string.IsNullOrWhiteSpace(codeChallenge) ||
            !string.Equals(codeChallengeMethod, "S256", StringComparison.OrdinalIgnoreCase))
        {
            LogOAuthAuthorizeValidation(clientId, redirectUri, scope, resource, "failed", "pkce_s256_required");
            return AuthorizeValidationResult.Fail("PKCE S256 is required.");
        }

        LogOAuthAuthorizeValidation(clientId, redirectUri, scope, resource, "success", string.Empty);
        return AuthorizeValidationResult.Ok(new AuthorizeRequest(
            clientId,
            redirectUri,
            scope,
            state,
            codeChallenge,
            "S256",
            resource));
    }

    public async Task<OAuthClientRegistrationResult> RegisterClientAsync(
        OAuthClientRegistrationRequest request,
        CancellationToken cancellationToken)
    {
        var options = gatewayOptions.Value.OAuth;
        if (!options.SelfHosted)
        {
            LogOAuthRegister(string.Empty, string.Empty, "failed", "self_hosted_disabled");
            return OAuthClientRegistrationResult.Fail("Self-hosted OAuth is not enabled.");
        }

        var redirectUris = request.RedirectUris?
            .Where(uri => !string.IsNullOrWhiteSpace(uri))
            .Select(uri => uri.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray() ?? [];
        if (redirectUris.Length == 0)
        {
            LogOAuthRegister(string.Empty, string.Empty, "failed", "missing_redirect_uris");
            return OAuthClientRegistrationResult.Fail("At least one redirect URI is required.");
        }

        var invalidRedirectUri = redirectUris.FirstOrDefault(uri => !IsRedirectUriAllowed(uri, options));
        if (invalidRedirectUri is not null)
        {
            LogOAuthRegister(GetHost(invalidRedirectUri), string.Empty, "failed", "redirect_not_allowed");
            return OAuthClientRegistrationResult.Fail("Redirect URI is not allowed.");
        }

        var tokenEndpointAuthMethod = string.IsNullOrWhiteSpace(request.TokenEndpointAuthMethod)
            ? "none"
            : request.TokenEndpointAuthMethod.Trim();
        if (!string.Equals(tokenEndpointAuthMethod, "none", StringComparison.Ordinal))
        {
            LogOAuthRegister(GetHost(redirectUris[0]), string.Empty, "failed", "unsupported_token_auth_method");
            return OAuthClientRegistrationResult.Fail("Only public PKCE clients with token_endpoint_auth_method=none are supported.");
        }

        var grantTypes = NormalizeRegistrationList(request.GrantTypes, ["authorization_code", "refresh_token"]);
        if (!grantTypes.Contains("authorization_code", StringComparer.Ordinal))
        {
            LogOAuthRegister(GetHost(redirectUris[0]), string.Empty, "failed", "authorization_code_required");
            return OAuthClientRegistrationResult.Fail("authorization_code grant is required.");
        }

        var responseTypes = NormalizeRegistrationList(request.ResponseTypes, ["code"]);
        if (!responseTypes.Contains("code", StringComparer.Ordinal))
        {
            LogOAuthRegister(GetHost(redirectUris[0]), string.Empty, "failed", "code_response_required");
            return OAuthClientRegistrationResult.Fail("code response type is required.");
        }

        var clientId = $"contexthub-chatgpt-dcr-{Guid.NewGuid():N}";
        var registered = new RegisteredOAuthClient(
            clientId,
            redirectUris,
            tokenEndpointAuthMethod,
            grantTypes,
            responseTypes,
            DateTimeOffset.UtcNow);
        await clientStore.UpsertAsync(
            registered,
            DateTimeOffset.UtcNow.AddDays(Math.Max(30, options.RegisteredClientLifetimeDays)),
            cancellationToken);

        LogOAuthRegister(GetHost(redirectUris[0]), clientId, "success", string.Empty);
        return OAuthClientRegistrationResult.Ok(new OAuthClientRegistrationResponse(
            clientId,
            new DateTimeOffset(DateTime.UtcNow).ToUnixTimeSeconds(),
            redirectUris,
            tokenEndpointAuthMethod,
            grantTypes,
            responseTypes,
            request.Scope ?? string.Join(' ', options.Scopes)));
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
            LogOAuthAuthorizeLogin(request, "failed", "invalid_credentials");
            return AuthorizeResult.Fail("Invalid username or password.");
        }

        var verification = passwordHasher.VerifyHashedPassword(new object(), user.PasswordHash, password);
        if (verification == PasswordVerificationResult.Failed)
        {
            LogOAuthAuthorizeLogin(request, "failed", "invalid_credentials");
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
            request.Resource,
            user.Id,
            user.TenantId,
            user.Username,
            user.Email,
            user.DisplayName);
        await stateStore.SetAuthorizationCodeAsync(
            code,
            payload,
            TimeSpan.FromMinutes(Math.Max(1, options.AuthorizationCodeLifetimeMinutes)));

        var includeIssuer = options.IncludeIssuerInAuthorizationResponse;
        var redirectValues = new Dictionary<string, string?>
        {
            ["code"] = code,
            ["state"] = request.State
        };
        if (includeIssuer)
        {
            redirectValues["iss"] = ResolveAuthorizationResponseIssuer(options);
        }

        var redirect = QueryString.Create(redirectValues);
        LogOAuthAuthorizeLogin(request, "success", string.Empty, includeIssuer);
        return AuthorizeResult.Ok(request.RedirectUri + redirect);
    }

    public async Task<OAuthTokenResult> ExchangeCodeAsync(
        string code,
        string redirectUri,
        string clientId,
        string? clientSecret,
        string codeVerifier,
        string? resource)
    {
        var options = gatewayOptions.Value.OAuth;
        if (!options.SelfHosted)
        {
            LogOAuthToken(clientId, "authorization_code", resource, "failed", "self_hosted_disabled");
            return OAuthTokenResult.Fail("Self-hosted OAuth is not enabled.");
        }

        var payload = string.IsNullOrWhiteSpace(code)
            ? null
            : await stateStore.TakeAuthorizationCodeAsync(code);
        if (payload is null)
        {
            LogOAuthToken(clientId, "authorization_code", resource, "failed", "invalid_code");
            return OAuthTokenResult.Fail("Invalid authorization code.");
        }

        if (!string.Equals(payload.RedirectUri, redirectUri, StringComparison.Ordinal) ||
            !string.Equals(payload.ClientId, clientId, StringComparison.Ordinal))
        {
            LogOAuthToken(clientId, "authorization_code", resource, "failed", "code_binding_mismatch");
            return OAuthTokenResult.Fail("Authorization code binding mismatch.");
        }

        if (!IsTokenClientAllowed(clientId, clientSecret, options))
        {
            LogOAuthToken(clientId, "authorization_code", resource, "failed", "invalid_client");
            return OAuthTokenResult.Fail("Invalid OAuth client.");
        }

        if (!IsTokenResourceAllowed(payload.Resource, resource))
        {
            LogOAuthToken(clientId, "authorization_code", resource, "failed", "resource_mismatch");
            return OAuthTokenResult.Fail("OAuth resource binding mismatch.");
        }

        if (string.IsNullOrWhiteSpace(resource) && !string.IsNullOrWhiteSpace(payload.Resource))
        {
            logger.LogWarning(
                "ChatGPT OAuth token request omitted resource. event={Event} clientKind={ClientKind} resourcePresent={ResourcePresent} status={Status}",
                "chatgpt_oauth_token",
                GetClientKind(clientId),
                false,
                "legacy_accepted");
        }

        if (!ValidatePkce(payload.CodeChallenge, codeVerifier))
        {
            LogOAuthToken(clientId, "authorization_code", resource, "failed", "invalid_pkce_verifier");
            return OAuthTokenResult.Fail("Invalid PKCE verifier.");
        }

        LogOAuthToken(clientId, "authorization_code", resource, "success", string.Empty);
        return await CreateTokenResultAsync(payload, options);
    }

    public async Task<OAuthTokenResult> RefreshAccessTokenAsync(
        string refreshToken,
        string clientId,
        string? clientSecret)
    {
        var options = gatewayOptions.Value.OAuth;
        if (!options.SelfHosted)
        {
            LogOAuthToken(clientId, "refresh_token", null, "failed", "self_hosted_disabled");
            return OAuthTokenResult.Fail("Self-hosted OAuth is not enabled.");
        }

        if (!IsTokenClientAllowed(clientId, clientSecret, options))
        {
            LogOAuthToken(clientId, "refresh_token", null, "failed", "invalid_client");
            return OAuthTokenResult.Fail("Invalid OAuth client.");
        }

        var payload = string.IsNullOrWhiteSpace(refreshToken)
            ? null
            : await stateStore.TakeRefreshTokenAsync(refreshToken);
        if (payload is null)
        {
            LogOAuthToken(clientId, "refresh_token", null, "failed", "invalid_refresh_token");
            return OAuthTokenResult.Fail("Invalid refresh token.");
        }

        if (!string.Equals(payload.ClientId, clientId, StringComparison.Ordinal))
        {
            LogOAuthToken(clientId, "refresh_token", null, "failed", "refresh_binding_mismatch");
            return OAuthTokenResult.Fail("Refresh token binding mismatch.");
        }

        LogOAuthToken(clientId, "refresh_token", payload.Resource, "success", string.Empty);
        return await CreateTokenResultAsync(payload, options);
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

    private async Task<OAuthTokenResult> CreateTokenResultAsync(AuthorizationCodePayload payload, OAuthOptions options)
    {
        var token = CreateAccessToken(payload, options);
        var requestedScopes = payload.Scope
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var idToken = requestedScopes.Contains("openid", StringComparer.Ordinal)
            ? CreateIdToken(payload, options)
            : string.Empty;
        var refreshToken = requestedScopes.Contains("offline_access", StringComparer.Ordinal)
            ? await CreateRefreshTokenAsync(payload, options)
            : string.Empty;
        return OAuthTokenResult.Ok(
            token,
            idToken,
            string.IsNullOrWhiteSpace(refreshToken) ? null : refreshToken,
            Math.Max(1, options.AccessTokenLifetimeMinutes) * 60,
            payload.Scope);
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
            string.IsNullOrWhiteSpace(payload.Resource) ? options.ClientId : payload.Resource,
            claims,
            now,
            now.AddMinutes(Math.Max(1, options.AccessTokenLifetimeMinutes)),
            rsaSigningCredentials?.Credentials ?? new SigningCredentials(BuildSigningKey(options.SelfHostedSigningKey), SecurityAlgorithms.HmacSha256));
        return TokenHandler.WriteToken(token);
    }

    private string CreateIdToken(AuthorizationCodePayload payload, OAuthOptions options)
    {
        var issuer = string.IsNullOrWhiteSpace(options.SelfHostedIssuer)
            ? throw new InvalidOperationException("ChatGptGateway:OAuth:SelfHostedIssuer is required when SelfHosted is true.")
            : options.SelfHostedIssuer.Trim().TrimEnd('/');
        var now = DateTime.UtcNow;
        var displayName = string.IsNullOrWhiteSpace(payload.DisplayName) ? payload.Username : payload.DisplayName;
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, payload.Username),
            new(JwtRegisteredClaimNames.Email, payload.Email),
            new(JwtRegisteredClaimNames.Name, displayName),
            new("tenant_id", payload.TenantId.ToString("D")),
            new("tenant_user_id", payload.UserId.ToString("D"))
        };
        var token = new JwtSecurityToken(
            issuer,
            payload.ClientId,
            claims,
            now,
            now.AddMinutes(Math.Max(1, options.AccessTokenLifetimeMinutes)),
            rsaSigningCredentials?.Credentials ?? new SigningCredentials(BuildSigningKey(options.SelfHostedSigningKey), SecurityAlgorithms.HmacSha256));
        return TokenHandler.WriteToken(token);
    }

    private async Task<string> CreateRefreshTokenAsync(AuthorizationCodePayload payload, OAuthOptions options)
    {
        var refreshToken = CreateTokenValue(32);
        await stateStore.SetRefreshTokenAsync(
            refreshToken,
            payload,
            TimeSpan.FromDays(Math.Max(1, options.RefreshTokenLifetimeDays)));
        return refreshToken;
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

    private async Task<ClientValidationResult> ValidateAuthorizeClientAsync(
        string clientId,
        string redirectUri,
        OAuthOptions options,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return ClientValidationResult.Fail("missing_client_id");
        }

        if (string.Equals(clientId, options.ClientId, StringComparison.Ordinal))
        {
            return ClientValidationResult.Ok();
        }

        var registered = await clientStore.GetAsync(clientId, cancellationToken);
        if (registered is not null)
        {
            return registered.RedirectUris.Contains(redirectUri, StringComparer.Ordinal)
                ? ClientValidationResult.Ok()
                : ClientValidationResult.Fail("registered_redirect_mismatch");
        }

        if (!IsChatGptClientMetadataDocumentId(clientId))
        {
            return ClientValidationResult.Fail("unknown_client");
        }

        try
        {
            var metadata = await clientMetadataFetcher.FetchAsync(clientId, cancellationToken);
            if (metadata?.RedirectUris is null ||
                !metadata.RedirectUris.Contains(redirectUri, StringComparer.Ordinal))
            {
                return ClientValidationResult.Fail("cimd_redirect_mismatch");
            }

            var tokenMethod = string.IsNullOrWhiteSpace(metadata.TokenEndpointAuthMethod)
                ? "none"
                : metadata.TokenEndpointAuthMethod.Trim();
            if (!string.Equals(tokenMethod, "none", StringComparison.Ordinal))
            {
                return ClientValidationResult.Fail("cimd_token_auth_method_unsupported");
            }

            return ClientValidationResult.Ok();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(
                ex,
                "Failed to fetch ChatGPT OAuth client metadata. event={Event} clientKind={ClientKind} redirectHost={RedirectHost} status={Status} failureReason={FailureReason}",
                "chatgpt_oauth_authorize_validate",
                "cimd",
                GetHost(redirectUri),
                "failed",
                "cimd_fetch_failed");
            return ClientValidationResult.Fail("cimd_fetch_failed");
        }
    }

    private static bool IsTokenClientAllowed(string clientId, string? clientSecret, OAuthOptions options)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        if (string.Equals(clientId, options.ClientId, StringComparison.Ordinal))
        {
            return string.IsNullOrWhiteSpace(options.ClientSecret)
                ? string.IsNullOrWhiteSpace(clientSecret)
                : string.Equals(clientSecret, options.ClientSecret, StringComparison.Ordinal);
        }

        if (!string.IsNullOrWhiteSpace(clientSecret))
        {
            return false;
        }

        return clientId.StartsWith("contexthub-chatgpt-dcr-", StringComparison.Ordinal) ||
               IsChatGptClientMetadataDocumentId(clientId);
    }

    private static bool IsTokenResourceAllowed(string codeResource, string? tokenResource)
    {
        if (string.IsNullOrWhiteSpace(tokenResource))
        {
            return true;
        }

        if (string.IsNullOrWhiteSpace(codeResource))
        {
            return false;
        }

        return string.Equals(codeResource, tokenResource.Trim(), StringComparison.Ordinal);
    }

    private static bool IsChatGptClientMetadataDocumentId(string clientId)
    {
        return Uri.TryCreate(clientId, UriKind.Absolute, out var uri) &&
               uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, "chatgpt.com", StringComparison.OrdinalIgnoreCase);
    }

    private static string[] NormalizeRegistrationList(IReadOnlyList<string>? values, string[] defaults)
    {
        var normalized = values?
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        return normalized is { Length: > 0 } ? normalized : defaults;
    }

    private static string? ResolveAuthorizationResponseIssuer(OAuthOptions options)
    {
        if (options.SelfHosted && !string.IsNullOrWhiteSpace(options.SelfHostedIssuer))
        {
            return options.SelfHostedIssuer.Trim().TrimEnd('/');
        }

        if (!string.IsNullOrWhiteSpace(options.Authority))
        {
            return options.Authority.Trim().TrimEnd('/');
        }

        return null;
    }

    private void LogOAuthAuthorizeValidation(
        string clientId,
        string redirectUri,
        string scope,
        string resource,
        string status,
        string failureReason)
    {
        logger.LogInformation(
            "ChatGPT OAuth authorize validation. event={Event} clientKind={ClientKind} redirectHost={RedirectHost} scope={Scope} resourcePresent={ResourcePresent} status={Status} failureReason={FailureReason}",
            "chatgpt_oauth_authorize_validate",
            GetClientKind(clientId),
            GetHost(redirectUri),
            scope,
            !string.IsNullOrWhiteSpace(resource),
            status,
            failureReason);
    }

    private void LogOAuthAuthorizeLogin(
        AuthorizeRequest request,
        string status,
        string failureReason,
        bool includeIssuerInResponse = false)
    {
        logger.LogInformation(
            "ChatGPT OAuth authorize login. event={Event} clientKind={ClientKind} redirectHost={RedirectHost} scope={Scope} resourcePresent={ResourcePresent} includeIssuerInResponse={IncludeIssuerInResponse} status={Status} failureReason={FailureReason}",
            "chatgpt_oauth_authorize_login",
            GetClientKind(request.ClientId),
            GetHost(request.RedirectUri),
            request.Scope,
            !string.IsNullOrWhiteSpace(request.Resource),
            includeIssuerInResponse,
            status,
            failureReason);
    }

    private void LogOAuthToken(string clientId, string grantType, string? resource, string status, string failureReason)
    {
        logger.LogInformation(
            "ChatGPT OAuth token exchange. event={Event} clientKind={ClientKind} grantType={GrantType} resourcePresent={ResourcePresent} status={Status} failureReason={FailureReason}",
            "chatgpt_oauth_token",
            GetClientKind(clientId),
            grantType,
            !string.IsNullOrWhiteSpace(resource),
            status,
            failureReason);
    }

    private void LogOAuthRegister(string redirectHost, string clientId, string status, string failureReason)
    {
        logger.LogInformation(
            "ChatGPT OAuth dynamic client registration. event={Event} clientKind={ClientKind} redirectHost={RedirectHost} resourcePresent={ResourcePresent} status={Status} failureReason={FailureReason}",
            "chatgpt_oauth_register",
            GetClientKind(clientId),
            redirectHost,
            false,
            status,
            failureReason);
    }

    private static string GetClientKind(string clientId)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return "missing";
        }

        if (clientId.StartsWith("https://chatgpt.com/", StringComparison.OrdinalIgnoreCase))
        {
            return "cimd";
        }

        if (clientId.StartsWith("contexthub-chatgpt-dcr-", StringComparison.Ordinal))
        {
            return "dcr";
        }

        return "configured";
    }

    private static string GetHost(string value)
        => Uri.TryCreate(value, UriKind.Absolute, out var uri) ? uri.Host : string.Empty;

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
    string CodeChallengeMethod,
    string Resource);

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

internal sealed record OAuthTokenResult(bool Success, string AccessToken, string IdToken, string? RefreshToken, int ExpiresIn, string Scope, string Error)
{
    public static OAuthTokenResult Ok(string accessToken, string idToken, string? refreshToken, int expiresIn, string scope) => new(true, accessToken, idToken, refreshToken, expiresIn, scope, string.Empty);
    public static OAuthTokenResult Fail(string error) => new(false, string.Empty, string.Empty, null, 0, string.Empty, error);
}

internal sealed record AuthorizationCodePayload(
    string ClientId,
    string RedirectUri,
    string Scope,
    string CodeChallenge,
    string Resource,
    Guid UserId,
    Guid TenantId,
    string Username,
    string Email,
    string DisplayName);

public sealed record OAuthClientRegistrationRequest(
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string>? GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string>? ResponseTypes,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("client_name")] string? ClientName);

internal sealed record OAuthClientRegistrationResult(
    bool Success,
    OAuthClientRegistrationResponse? Registration,
    string Error)
{
    public static OAuthClientRegistrationResult Ok(OAuthClientRegistrationResponse registration) => new(true, registration, string.Empty);
    public static OAuthClientRegistrationResult Fail(string error) => new(false, null, error);
}

public sealed record OAuthClientRegistrationResponse(
    [property: JsonPropertyName("client_id")] string ClientId,
    [property: JsonPropertyName("client_id_issued_at")] long ClientIdIssuedAt,
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string> RedirectUris,
    [property: JsonPropertyName("token_endpoint_auth_method")] string TokenEndpointAuthMethod,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string> GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string> ResponseTypes,
    [property: JsonPropertyName("scope")] string Scope);

internal sealed record RegisteredOAuthClient(
    string ClientId,
    IReadOnlyList<string> RedirectUris,
    string TokenEndpointAuthMethod,
    IReadOnlyList<string> GrantTypes,
    IReadOnlyList<string> ResponseTypes,
    DateTimeOffset RegisteredAt);

internal sealed record ClientValidationResult(bool Success, string FailureReason)
{
    public static ClientValidationResult Ok() => new(true, string.Empty);
    public static ClientValidationResult Fail(string failureReason) => new(false, failureReason);
}

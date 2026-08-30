namespace Memory.ChatGptGateway;

public sealed class ChatGptGatewayOptions
{
    public string Surface { get; set; } = "General";
    public OAuthOptions OAuth { get; set; } = new();
    public ContextHubGatewayOptions ContextHub { get; set; } = new();
    public string PublicMcpUrl { get; set; } = string.Empty;
    public string PublicResourceMetadataUrl { get; set; } = string.Empty;
    public int MaxResponseCharacters { get; set; } = 120_000;
}

public sealed class OAuthOptions
{
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = ["openid", "profile", "email", "offline_access"];
    public bool SelfHosted { get; set; }
    public string SelfHostedIssuer { get; set; } = string.Empty;
    public string SelfHostedSigningKey { get; set; } = string.Empty;
    public string SelfHostedRsaPrivateKey { get; set; } = string.Empty;
    public string[] AllowedRedirectUris { get; set; } = [];
    public string[] AllowedRedirectUriPrefixes { get; set; } = [];
    public int AuthorizationCodeLifetimeMinutes { get; set; } = 5;
    public int AccessTokenLifetimeMinutes { get; set; } = 60;
    public int RefreshTokenLifetimeDays { get; set; } = 30;
    public int RegisteredClientLifetimeDays { get; set; } = 30;
    public bool IncludeIssuerInAuthorizationResponse { get; set; }
    public string NameClaim { get; set; } = "name";
    public string EmailClaim { get; set; } = "email";
    public string SubjectClaim { get; set; } = "sub";
    public bool TestMode { get; set; }
    public string TestBearerToken { get; set; } = "test-chatgpt-token";
    public string TestSubject { get; set; } = "chatgpt-test-user";
    public string TestEmail { get; set; } = "chatgpt-test@example.invalid";
    public string TestName { get; set; } = "ChatGPT Test User";
}

public sealed class ContextHubGatewayOptions
{
    public string BaseUrl { get; set; } = "http://mcp-server:8080";
    public string ServiceToken { get; set; } = string.Empty;
}

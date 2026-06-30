namespace Memory.ChatGptGateway;

public sealed class ChatGptGatewayOptions
{
    public OAuthOptions OAuth { get; set; } = new();
    public ContextHubGatewayOptions ContextHub { get; set; } = new();
    public string PublicMcpUrl { get; set; } = string.Empty;
    public string PublicResourceMetadataUrl { get; set; } = string.Empty;
    public string[] AllowedProjectIds { get; set; } = [];
    public string[] ReadTools { get; set; } = [];
    public string[] DirectWriteTools { get; set; } = [];
    public string[] ProposalWriteTools { get; set; } = [];
    public int MaxResponseCharacters { get; set; } = 120_000;
}

public sealed class OAuthOptions
{
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public string[] Scopes { get; set; } = ["openid", "profile", "email"];
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

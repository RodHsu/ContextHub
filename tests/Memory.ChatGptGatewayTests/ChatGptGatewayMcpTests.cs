extern alias mcpserver;

using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Memory.Application;
using Memory.ChatGptGateway;
using Memory.Domain;
using Memory.Infrastructure;
using MemoryMcpTools = mcpserver::Memory.McpServer.MemoryMcpTools;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Memory.ChatGptGatewayTests;

public sealed class ChatGptGatewayMcpTests(ChatGptGatewayTestEnvironment environment) : IClassFixture<ChatGptGatewayTestEnvironment>
{
    private const string ProjectId = ChatGptGatewayTestConstants.ProjectId;
    private const string TestToken = ChatGptGatewayTestConstants.TestToken;
    internal const string PublicMcpUrl = "https://context-hub.example.test/mcp-chat";
    internal const string PublicResourceMetadataUrl = "https://context-hub.example.test/.well-known/oauth-protected-resource/mcp-chat";
    internal const string TestAuthority = "https://oidc.example.test/context-hub";
    internal const string SelfHostedIssuer = "https://context-hub.example.test";
    internal const string SelfHostedClientId = "context-hub-chatgpt-self-hosted-test";
    internal const string SelfHostedSigningKey = "0123456789abcdef0123456789abcdef";
    private static readonly Lazy<string> SelfHostedRsaPrivateKey = new(() =>
    {
        using var rsa = RSA.Create(3072);
        return Convert.ToBase64String(rsa.ExportPkcs8PrivateKey());
    });

    [DockerRequiredFact]
    public async Task Raw_Http_Mcp_Should_Reject_Anonymous_Request()
    {
        using var client = environment.GetFactory().CreateClient();
        client.DefaultRequestHeaders.Authorization = null;

        using var response = await client.PostAsync(
            "/mcp",
            new StringContent("""{"jsonrpc":"2.0","id":"anonymous","method":"tools/list"}""", Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.Unauthorized);
        response.Headers.WwwAuthenticate.Select(x => x.ToString())
            .Should().Contain(x => x.Contains($"resource_metadata=\"{PublicResourceMetadataUrl}\"", StringComparison.Ordinal));
    }

    [DockerRequiredFact]
    public async Task Raw_Http_Mcp_Should_Reject_Untrusted_Origin_And_Accept_Public_Mcp_Origin()
    {
        using var client = CreateAuthorizedClient(environment.GetFactory());

        using var rejectedRequest = CreateModernMcpRequest("tools/list");
        rejectedRequest.Headers.Add("Origin", "https://attacker.example.test");
        using var rejectedResponse = await client.SendAsync(rejectedRequest);
        rejectedResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Forbidden);

        using var acceptedRequest = CreateModernMcpRequest("tools/list");
        acceptedRequest.Headers.Add("Origin", "https://context-hub.example.test");
        using var acceptedResponse = await client.SendAsync(acceptedRequest);
        acceptedResponse.EnsureSuccessStatusCode();
    }

    [DockerRequiredFact]
    public async Task Gateway_Mcp_Should_Normalize_ChatGpt_Legacy_Metadata_And_Preserve_Modern_Metadata()
    {
        using var client = CreateAuthorizedClient(environment.GetFactory());

        using var legacyRequest = CreateRawProtocolRequest(
            "2025-11-25",
            """
            {"jsonrpc":"2.0","id":"legacy-chatgpt","method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2025-11-25","io.modelcontextprotocol/clientInfo":{"name":"ChatGPT","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
            """,
            staleSessionId: "stale-session-from-prior-release");
        using var legacyResponse = await client.SendAsync(legacyRequest);
        var legacyPayload = await legacyResponse.Content.ReadAsStringAsync();

        legacyResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, legacyPayload);
        legacyPayload.Should().Contain("governance_batch_execute");
        legacyPayload.Should().NotContain("The reserved per-request metadata key '_meta/io.modelcontextprotocol/clientCapabilities' is not valid with protocol version '2025-11-25'.");

        using var modernRequest = CreateRawProtocolRequest(
            "2026-07-28",
            """
            {"jsonrpc":"2.0","id":"modern","method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"protocol-test","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
            """);
        using var modernResponse = await client.SendAsync(modernRequest);
        var modernPayload = await modernResponse.Content.ReadAsStringAsync();

        modernResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK, modernPayload);
        modernPayload.Should().Contain("governance_batch_execute");
    }

    [DockerRequiredFact]
    public async Task OAuth_Protected_Resource_Metadata_Should_Describe_Public_Chat_Gateway()
    {
        using var client = environment.GetFactory().CreateClient();

        using var response = await client.GetAsync("/.well-known/oauth-protected-resource/mcp-chat");
        using var rootResponse = await client.GetAsync("/.well-known/oauth-protected-resource");

        response.EnsureSuccessStatusCode();
        rootResponse.EnsureSuccessStatusCode();
        var metadata = await response.Content.ReadFromJsonAsync<JsonElement>();
        var rootMetadata = await rootResponse.Content.ReadFromJsonAsync<JsonElement>();
        metadata.GetProperty("resource").GetString().Should().Be(PublicMcpUrl);
        rootMetadata.GetProperty("resource").GetString().Should().Be(PublicMcpUrl);
        metadata.GetProperty("authorization_servers").EnumerateArray()
            .Select(x => x.GetString())
            .Should().ContainSingle(TestAuthority);
        metadata.GetProperty("scopes_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain(["openid", "profile", "email", "offline_access"]);
        metadata.GetProperty("bearer_methods_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("header");

        using var oidcResponse = await client.GetAsync("/.well-known/openid-configuration/mcp-chat");
        using var rootOidcResponse = await client.GetAsync("/.well-known/openid-configuration");

        oidcResponse.EnsureSuccessStatusCode();
        rootOidcResponse.EnsureSuccessStatusCode();
        var oidcMetadata = await oidcResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rootOidcMetadata = await rootOidcResponse.Content.ReadFromJsonAsync<JsonElement>();
        oidcMetadata.GetProperty("issuer").GetString().Should().Be(TestAuthority);
        rootOidcMetadata.GetProperty("issuer").GetString().Should().Be(TestAuthority);
        oidcMetadata.GetProperty("authorization_endpoint").GetString().Should().Be($"{TestAuthority}/oauth/chat/authorize");
        oidcMetadata.GetProperty("token_endpoint").GetString().Should().Be($"{TestAuthority}/oauth/chat/token");
        oidcMetadata.GetProperty("registration_endpoint").GetString().Should().Be($"{TestAuthority}/oauth/chat/register");
        oidcMetadata.GetProperty("userinfo_endpoint").GetString().Should().Be($"{TestAuthority}/userinfo");
        oidcMetadata.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
        oidcMetadata.GetProperty("authorization_response_iss_parameter_supported").GetBoolean().Should().BeFalse();
        oidcMetadata.GetProperty("code_challenge_methods_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("S256");
        oidcMetadata.GetProperty("id_token_signing_alg_values_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("HS256");
        oidcMetadata.GetProperty("claims_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain(["sub", "name", "email"]);
        oidcMetadata.GetProperty("scopes_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("offline_access");
    }

    [DockerRequiredFact]
    public async Task OAuth_Public_Endpoints_Should_Allow_ChatGpt_Cors_Preflight()
    {
        using var client = environment.GetFactory().CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Options, "/oauth/chat/register");
        request.Headers.TryAddWithoutValidation("Origin", "https://chatgpt.com");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Method", "POST");
        request.Headers.TryAddWithoutValidation("Access-Control-Request-Headers", "content-type");

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(System.Net.HttpStatusCode.NoContent);
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain("https://chatgpt.com");
        response.Headers.GetValues("Access-Control-Allow-Methods").Should().ContainSingle(x => x.Contains("POST", StringComparison.Ordinal));
        response.Headers.GetValues("Access-Control-Allow-Headers").Should().ContainSingle(x => x.Contains("content-type", StringComparison.OrdinalIgnoreCase));
    }

    [DockerRequiredFact]
    public async Task SelfHosted_Oidc_Metadata_Should_Publish_Rs256_Jwks()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true,
            selfHostedRsaPrivateKey: SelfHostedRsaPrivateKey.Value);
        using var client = factory.CreateClient();

        using var metadataResponse = await client.GetAsync("/.well-known/openid-configuration");
        using var jwksResponse = await client.GetAsync("/.well-known/jwks.json");

        metadataResponse.EnsureSuccessStatusCode();
        jwksResponse.EnsureSuccessStatusCode();
        var metadata = await metadataResponse.Content.ReadFromJsonAsync<JsonElement>();
        var jwks = await jwksResponse.Content.ReadFromJsonAsync<JsonElement>();

        metadata.GetProperty("jwks_uri").GetString().Should().Be($"{SelfHostedIssuer}/.well-known/jwks.json");
        metadata.GetProperty("id_token_signing_alg_values_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("RS256");
        var key = jwks.GetProperty("keys").EnumerateArray().Should().ContainSingle().Which;
        key.GetProperty("kty").GetString().Should().Be("RSA");
        key.GetProperty("alg").GetString().Should().Be("RS256");
        key.GetProperty("kid").GetString().Should().NotBeNullOrWhiteSpace();
        key.GetProperty("n").GetString().Should().HaveLength(512);
        key.GetProperty("e").GetString().Should().Be("AQAB");
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Registration_Should_Rate_Limit_Repeated_Requests()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true,
            selfHostedRsaPrivateKey: SelfHostedRsaPrivateKey.Value);
        using var client = factory.CreateClient();

        for (var attempt = 0; attempt < 10; attempt++)
        {
            using var response = await client.PostAsync(
                "/oauth/chat/register",
                new StringContent("{}", Encoding.UTF8, "application/json"));
            response.StatusCode.Should().NotBe(System.Net.HttpStatusCode.TooManyRequests);
        }

        using var limited = await client.PostAsync(
            "/oauth/chat/register",
            new StringContent("{}", Encoding.UTF8, "application/json"));
        limited.StatusCode.Should().Be(System.Net.HttpStatusCode.TooManyRequests);
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Authorization_Code_Should_Issue_Mcp_Bearer_Token()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true);
        using var setupScope = factory.Services.CreateScope();
        var dbContext = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var passwordHasher = setupScope.ServiceProvider.GetRequiredService<IPasswordHasher<object>>();
        var user = await dbContext.TenantUsers.SingleAsync(x => x.Username == "gateway-test-admin");
        user.Email = "oauth-user@example.test";
        user.DisplayName = "OAuth Test User";
        user.PasswordHash = passwordHasher.HashPassword(new object(), "oauth-password");
        await dbContext.SaveChangesAsync();

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = "https://chatgpt.com/aip/context-hub/callback";
        var authorizePath = "/oauth/chat/authorize?" + string.Join('&', new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = SelfHostedClientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile email offline_access",
            ["state"] = "state-123",
            ["code_challenge"] = challenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = PublicMcpUrl
        }.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

        using var metadataResponse = await client.GetAsync("/.well-known/oauth-authorization-server/mcp-chat");
        using var rootMetadataResponse = await client.GetAsync("/.well-known/oauth-authorization-server");
        using var oidcMetadataResponse = await client.GetAsync("/.well-known/openid-configuration/mcp-chat");
        using var rootOidcMetadataResponse = await client.GetAsync("/.well-known/openid-configuration");
        var metadataBody = await metadataResponse.Content.ReadAsStringAsync();
        metadataResponse.IsSuccessStatusCode.Should().BeTrue(metadataBody);
        rootMetadataResponse.EnsureSuccessStatusCode();
        oidcMetadataResponse.EnsureSuccessStatusCode();
        rootOidcMetadataResponse.EnsureSuccessStatusCode();
        var metadata = await metadataResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rootMetadata = await rootMetadataResponse.Content.ReadFromJsonAsync<JsonElement>();
        var oidcMetadata = await oidcMetadataResponse.Content.ReadFromJsonAsync<JsonElement>();
        var rootOidcMetadata = await rootOidcMetadataResponse.Content.ReadFromJsonAsync<JsonElement>();
        metadata.GetProperty("issuer").GetString().Should().Be(SelfHostedIssuer);
        rootMetadata.GetProperty("issuer").GetString().Should().Be(SelfHostedIssuer);
        oidcMetadata.GetProperty("issuer").GetString().Should().Be(SelfHostedIssuer);
        rootOidcMetadata.GetProperty("issuer").GetString().Should().Be(SelfHostedIssuer);
        metadata.GetProperty("authorization_endpoint").GetString().Should().Be($"{SelfHostedIssuer}/oauth/chat/authorize");
        metadata.GetProperty("token_endpoint").GetString().Should().Be($"{SelfHostedIssuer}/oauth/chat/token");
        metadata.GetProperty("registration_endpoint").GetString().Should().Be($"{SelfHostedIssuer}/oauth/chat/register");
        metadata.GetProperty("client_id_metadata_document_supported").GetBoolean().Should().BeTrue();
        metadata.GetProperty("authorization_response_iss_parameter_supported").GetBoolean().Should().BeFalse();
        oidcMetadata.GetProperty("authorization_response_iss_parameter_supported").GetBoolean().Should().BeFalse();
        metadata.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("none");
        oidcMetadata.GetProperty("token_endpoint_auth_methods_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("none");
        oidcMetadata.GetProperty("userinfo_endpoint").GetString().Should().Be($"{SelfHostedIssuer}/userinfo");
        metadata.GetProperty("grant_types_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("refresh_token");
        oidcMetadata.GetProperty("grant_types_supported").EnumerateArray()
            .Select(x => x.GetString())
            .Should().Contain("refresh_token");

        using var authorizePageResponse = await client.GetAsync(authorizePath);
        authorizePageResponse.EnsureSuccessStatusCode();
        (await authorizePageResponse.Content.ReadAsStringAsync()).Should().Contain("Connect ContextHub");

        using var authorizeResponse = await client.PostAsync(
            authorizePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "gateway-test-admin",
                ["password"] = "oauth-password"
            }));
        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
        var location = authorizeResponse.Headers.Location;
        location.Should().NotBeNull();
        location!.ToString().Should().StartWith(redirectUri);
        var callbackQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(location.Query);
        var code = callbackQuery["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace();
        callbackQuery["state"].ToString().Should().Be("state-123");
        callbackQuery.Should().NotContainKey("iss");

        using var tokenResponse = await client.PostAsync(
            "/oauth/chat/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = SelfHostedClientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
                ["resource"] = PublicMcpUrl
            }));
        tokenResponse.EnsureSuccessStatusCode();
        var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        var accessToken = tokenJson.GetProperty("access_token").GetString();
        var idToken = tokenJson.GetProperty("id_token").GetString();
        var refreshToken = tokenJson.GetProperty("refresh_token").GetString();
        accessToken.Should().NotBeNullOrWhiteSpace();
        idToken.Should().NotBeNullOrWhiteSpace();
        refreshToken.Should().NotBeNullOrWhiteSpace();
        tokenJson.GetProperty("token_type").GetString().Should().Be("Bearer");
        tokenJson.GetProperty("scope").GetString().Should().Contain("offline_access");
        new JwtSecurityTokenHandler().ReadJwtToken(accessToken)
            .Audiences.Should().Contain(PublicMcpUrl);

        using var refreshResponse = await client.PostAsync(
            "/oauth/chat/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = SelfHostedClientId,
                ["refresh_token"] = refreshToken!
            }));
        refreshResponse.EnsureSuccessStatusCode();
        var refreshedJson = await refreshResponse.Content.ReadFromJsonAsync<JsonElement>();
        refreshedJson.GetProperty("access_token").GetString().Should().NotBeNullOrWhiteSpace();
        refreshedJson.GetProperty("refresh_token").GetString().Should().NotBeNullOrWhiteSpace();

        using var reusedRefreshResponse = await client.PostAsync(
            "/oauth/chat/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["client_id"] = SelfHostedClientId,
                ["refresh_token"] = refreshToken!
            }));
        reusedRefreshResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);

        using var userInfoClient = factory.CreateClient();
        userInfoClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var userInfoResponse = await userInfoClient.GetAsync("/userinfo");
        userInfoResponse.EnsureSuccessStatusCode();
        var userInfo = await userInfoResponse.Content.ReadFromJsonAsync<JsonElement>();
        userInfo.GetProperty("sub").GetString().Should().Be("gateway-test-admin");
        userInfo.GetProperty("name").GetString().Should().Be("OAuth Test User");
        userInfo.GetProperty("email").GetString().Should().Be("oauth-user@example.test");
        userInfo.GetProperty("tenant_id").GetString().Should().NotBeNullOrWhiteSpace();
        userInfo.GetProperty("tenant_user_id").GetString().Should().NotBeNullOrWhiteSpace();

        using var mcpClient = factory.CreateClient();
        mcpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var initializeRequest = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = 1,
                method = "initialize",
                @params = new
                {
                    protocolVersion = "2025-06-18",
                    capabilities = new { },
                    clientInfo = new { name = "self-hosted-oauth-test", version = "1.0" }
                }
            })
        };
        initializeRequest.Headers.Accept.ParseAdd("application/json");
        initializeRequest.Headers.Accept.ParseAdd("text/event-stream");
        using var initializeResponse = await mcpClient.SendAsync(initializeRequest);
        initializeResponse.EnsureSuccessStatusCode();
        initializeResponse.Headers.TryGetValues("Mcp-Session-Id", out _).Should().BeFalse(
            "the upgraded MCP transport is stateless while OAuth authorization remains enforced");
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Authorization_Response_Should_Include_Issuer_When_Configured()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true,
            includeIssuerInAuthorizationResponse: true);
        await ConfigureSelfHostedUserAsync(factory);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var metadata = await (await client.GetAsync("/.well-known/oauth-authorization-server/mcp-chat"))
            .Content.ReadFromJsonAsync<JsonElement>();
        metadata.GetProperty("authorization_response_iss_parameter_supported").GetBoolean().Should().BeTrue();
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = "https://chatgpt.com/connector/oauth/issuer-opt-in";
        var authorizePath = BuildAuthorizePath(SelfHostedClientId, redirectUri, challenge, PublicMcpUrl);

        using var authorizeResponse = await client.PostAsync(
            authorizePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "gateway-test-admin",
                ["password"] = "oauth-password"
            }));

        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
        var callbackQuery = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query);
        callbackQuery["code"].ToString().Should().NotBeNullOrWhiteSpace();
        callbackQuery["state"].ToString().Should().Be("state-123");
        callbackQuery["iss"].ToString().Should().Be(SelfHostedIssuer);
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Public_Client_Should_Reject_NonEmpty_Client_Secret()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true);
        await ConfigureSelfHostedUserAsync(factory);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = "https://chatgpt.com/connector/oauth/public-client-secret";
        var authorizePath = BuildAuthorizePath(SelfHostedClientId, redirectUri, challenge, PublicMcpUrl);

        using var authorizeResponse = await client.PostAsync(
            authorizePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "gateway-test-admin",
                ["password"] = "oauth-password"
            }));
        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query)["code"].ToString();

        using var tokenResponse = await client.PostAsync(
            "/oauth/chat/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = SelfHostedClientId,
                ["client_secret"] = "unexpected-secret",
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
                ["resource"] = PublicMcpUrl
            }));

        tokenResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Dynamic_Client_Registration_Should_Issue_Token()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true);
        await ConfigureSelfHostedUserAsync(factory);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var redirectUri = "https://chatgpt.com/connector/oauth/dcr-test-callback";
        using var registerResponse = await client.PostAsJsonAsync(
            "/oauth/chat/register",
            new
            {
                redirect_uris = new[] { redirectUri },
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code", "refresh_token" },
                response_types = new[] { "code" },
                scope = "openid profile email offline_access"
            });

        registerResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
        var registration = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        var clientId = registration.GetProperty("client_id").GetString();
        clientId.Should().StartWith("contexthub-chatgpt-dcr-");
        registration.GetProperty("token_endpoint_auth_method").GetString().Should().Be("none");

        var token = await CompleteSelfHostedOAuthCodeFlowAsync(client, clientId!, redirectUri, PublicMcpUrl);
        new JwtSecurityTokenHandler().ReadJwtToken(token)
            .Audiences.Should().Contain(PublicMcpUrl);
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Dynamic_Client_State_Should_Survive_Gateway_Restarts()
    {
        const string redirectUri = "https://chatgpt.com/connector/oauth/dcr-restart-callback";
        string clientId;
        string authorizationCode;
        string verifier;

        await using (var firstFactory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true))
        {
            await ConfigureSelfHostedUserAsync(firstFactory);
            using var client = firstFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            using var registerResponse = await client.PostAsJsonAsync(
                "/oauth/chat/register",
                new
                {
                    redirect_uris = new[] { redirectUri },
                    token_endpoint_auth_method = "none",
                    grant_types = new[] { "authorization_code", "refresh_token" },
                    response_types = new[] { "code" },
                    scope = "openid profile email offline_access"
                });
            registerResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Created);
            clientId = (await registerResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("client_id").GetString()!;

            verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
            var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
            var authorizePath = BuildAuthorizePath(clientId, redirectUri, challenge, PublicMcpUrl);
            using var authorizeResponse = await client.PostAsync(
                authorizePath,
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["username"] = "gateway-test-admin",
                    ["password"] = "oauth-password"
                }));
            authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
            authorizationCode = Microsoft.AspNetCore.WebUtilities.QueryHelpers
                .ParseQuery(authorizeResponse.Headers.Location!.Query)["code"].ToString();
            authorizationCode.Should().NotBeNullOrWhiteSpace();

        }

        string refreshToken;
        await using (var secondFactory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true))
        {
            using var client = secondFactory.CreateClient();
            using var tokenResponse = await client.PostAsync(
                "/oauth/chat/token",
                new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["grant_type"] = "authorization_code",
                    ["code"] = authorizationCode,
                    ["redirect_uri"] = redirectUri,
                    ["client_id"] = clientId,
                    ["code_verifier"] = verifier,
                    ["resource"] = PublicMcpUrl
                }));
            tokenResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
            refreshToken = (await tokenResponse.Content.ReadFromJsonAsync<JsonElement>())
                .GetProperty("refresh_token").GetString()!;
        }

        await using (var redis = await ConnectionMultiplexer.ConnectAsync(environment.RedisConnectionString))
        {
            var legacyRedisKey = $"memory:chatgpt-gateway-tests:chatgpt-oauth:refresh:{refreshToken}";
            (await redis.GetDatabase().StringGetAsync(legacyRedisKey)).IsNull.Should().BeTrue(
                "refresh tokens must remain usable after Redis loses transient OAuth state");
        }

        await using var thirdFactory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true);
        using var refreshClient = thirdFactory.CreateClient();
        using var refreshResponse = await refreshClient.PostAsync(
            "/oauth/chat/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId
            }));
        refreshResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.OK);
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Dynamic_Client_Registration_Should_Reject_Invalid_Redirect()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true);
        using var client = factory.CreateClient();

        using var registerResponse = await client.PostAsJsonAsync(
            "/oauth/chat/register",
            new
            {
                redirect_uris = new[] { "https://evil.example.test/callback" },
                token_endpoint_auth_method = "none",
                grant_types = new[] { "authorization_code" },
                response_types = new[] { "code" }
            });

        registerResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Dynamic_Client_Registration_Should_Reject_Invalid_Json_As_Client_Metadata_Error()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true);
        using var client = factory.CreateClient();

        using var registerResponse = await client.PostAsync(
            "/oauth/chat/register",
            new StringContent("{", Encoding.UTF8, "application/json"));

        registerResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
        var error = await registerResponse.Content.ReadFromJsonAsync<JsonElement>();
        error.GetProperty("error").GetString().Should().Be("invalid_client_metadata");
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Client_Id_Metadata_Document_Should_Issue_Token()
    {
        var redirectUri = "https://chatgpt.com/connector/oauth/cimd-test-callback";
        var clientId = "https://chatgpt.com/connector/context-hub/client.json";
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true,
            clientMetadataFetcher: new FakeClientMetadataFetcher(new ChatGptOAuthClientMetadata(
                [redirectUri],
                null,
                ["authorization_code", "refresh_token"],
                ["code"],
                "openid profile email offline_access",
                ["none", "private_key_jwt"])));
        await ConfigureSelfHostedUserAsync(factory);

        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var token = await CompleteSelfHostedOAuthCodeFlowAsync(client, clientId, redirectUri, PublicMcpUrl);
        new JwtSecurityTokenHandler().ReadJwtToken(token)
            .Audiences.Should().Contain(PublicMcpUrl);
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Client_Id_Metadata_Document_Should_Reject_Redirect_Mismatch()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true,
            clientMetadataFetcher: new FakeClientMetadataFetcher(new ChatGptOAuthClientMetadata(
                ["https://chatgpt.com/connector/oauth/other-callback"],
                "none",
                ["authorization_code"],
                ["code"],
                "openid profile email")));
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizePath = BuildAuthorizePath(
            "https://chatgpt.com/connector/context-hub/client.json",
            "https://chatgpt.com/connector/oauth/mismatch-callback",
            challenge,
            PublicMcpUrl);

        using var authorizePageResponse = await client.GetAsync(authorizePath);

        authorizePageResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [DockerRequiredFact]
    public async Task SelfHosted_OAuth_Token_Should_Reject_Resource_Mismatch()
    {
        await using var factory = new ChatGptGatewayApplicationFactory(
            environment.PostgresConnectionString,
            environment.RedisConnectionString,
            selfHostedOAuth: true);
        await ConfigureSelfHostedUserAsync(factory);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = "https://chatgpt.com/connector/oauth/resource-mismatch";
        var authorizePath = BuildAuthorizePath(SelfHostedClientId, redirectUri, challenge, PublicMcpUrl);
        using var authorizeResponse = await client.PostAsync(
            authorizePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "gateway-test-admin",
                ["password"] = "oauth-password"
            }));
        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query)["code"].ToString();

        using var tokenResponse = await client.PostAsync(
            "/oauth/chat/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = SelfHostedClientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
                ["resource"] = "https://context-hub.example.test/other-resource"
            }));

        tokenResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.BadRequest);
    }

    [DockerRequiredFact]
    public async Task Tool_Discovery_Should_Expose_Only_ChatGpt_Allowed_Tools()
    {
        using var httpClient = CreateAuthorizedClient(environment.GetFactory());
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, httpClient);

        await using var client = await McpClient.CreateAsync(transport);
        var toolNames = (await client.ListToolsAsync())
            .Select(x => x.ProtocolTool.Name)
            .ToArray();

        toolNames.Should().Contain([
            "describe_context_hub",
            "build_working_context",
            "memory_search",
            "memory_get",
            "project_artifacts_list",
            "project_artifacts_search",
            "project_artifact_get",
            "log_search",
            "log_read",
            "conversation_ingest",
            "projects_list",
            "daily_memory_review",
            "user_preferences_list",
            "conversation_insights_list",
            "suggested_actions_list",
            "memory_retention_preview",
            "project_cleanup_preview",
            "discussion_threads_list",
            "discussion_thread_get",
            "discussion_thread_create",
            "discussion_thread_close",
            "discussion_thread_archive",
            "discussion_thread_restore",
            "discussion_message_create",
            "project_hierarchy_get_children",
            "project_hierarchy_set_children",
            "memory_upsert",
            "memory_update",
            "memory_archive",
            "memory_move",
            "memory_delete",
            "project_cleanup_apply",
            "user_preference_upsert",
            "user_preference_archive",
            "suggested_action_accept",
            "suggested_action_dismiss",
            "promote_log_slice_to_memory",
            "project_artifact_publish",
            "project_artifact_upload_object",
            "chatgpt_proposals_list",
            "chatgpt_proposal_approve",
            "chatgpt_proposal_reject"
        ]);
        toolNames.Should().NotContain([
            "enqueue_reindex",
            "conversation_insights_promote",
            "system_status",
            "sessions_list"
        ]);
    }

    [DockerRequiredFact]
    public async Task Discussion_Tools_Should_Interoperate_Between_Direct_Mcp_And_Chat_Gateway()
    {
        var peerProjectId = $"discussion-peer-{Guid.NewGuid():N}";
        async Task<T> UseDirectMcpAsync<T>(Func<MemoryMcpTools, Task<T>> action)
        {
            using var scope = environment.GetFactory().Services.CreateScope();
            UseGatewayActor(scope.ServiceProvider);
            return await action(ActivatorUtilities.CreateInstance<MemoryMcpTools>(scope.ServiceProvider));
        }

        var captureHandler = new SessionCaptureHandler(environment.GetFactory().Server.CreateHandler());
        using var chatClient = CreateAuthorizedClient(environment.GetFactory(), captureHandler);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(chatClient.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, chatClient);
        await using var chatMcp = await McpClient.CreateAsync(transport);
        _ = await chatMcp.ListToolsAsync();
        chatMcp.NegotiatedProtocolVersion.Should().Be("2026-07-28");
        var sessionId = captureHandler.SessionId;
        sessionId.Should().BeNull("MCP 2026-07-28 uses stateless Streamable HTTP");

        // MCP to MCP: direct MCP creates and replies to the same thread.
        var mcpToMcp = await UseDirectMcpAsync(mcp => mcp.discussion_thread_create(new(
            ProjectId, ProjectId, "MCP to MCP", [ProjectId, peerProjectId], "Created by direct MCP.")));
        await UseDirectMcpAsync(mcp => mcp.discussion_message_create(new(mcpToMcp.Id, peerProjectId, "Replied by direct MCP.")));
        (await UseDirectMcpAsync(mcp => mcp.discussion_thread_get(mcpToMcp.Id, ProjectId)))!.Messages.Should().HaveCount(2);

        // Chat to chat: both writes use the OAuth-protected mcp-chat transport.
        var chatToChatPayload = await SendMcpAsync(chatClient, sessionId!, 101, "tools/call", new
        {
            name = "discussion_thread_create",
            arguments = new { request = new { hostProjectId = ProjectId, senderProjectId = ProjectId, title = "Chat to chat", participantProjectIds = new[] { ProjectId, peerProjectId }, initialMessage = "Created by mcp-chat." } }
        });
        var chatToChatId = ExtractToolJson(chatToChatPayload).GetProperty("id").GetGuid();
        _ = await SendMcpAsync(chatClient, sessionId!, 102, "tools/call", new
        {
            name = "discussion_message_create",
            arguments = new { request = new { threadId = chatToChatId, senderProjectId = peerProjectId, content = "Replied by mcp-chat." } }
        });
        var chatToChatReadPayload = await SendMcpAsync(chatClient, sessionId!, 103, "tools/call", new
        {
            name = "discussion_thread_get",
            arguments = new { threadId = chatToChatId, readerProjectId = ProjectId }
        });
        ExtractToolJson(chatToChatReadPayload).GetProperty("messages").GetArrayLength().Should().Be(2);

        // MCP to chat: direct MCP creates, then mcp-chat replies and reads it.
        var mcpToChat = await UseDirectMcpAsync(mcp => mcp.discussion_thread_create(new(
            ProjectId, ProjectId, "MCP to chat", [ProjectId, peerProjectId], "Created by direct MCP.")));
        _ = await SendMcpAsync(chatClient, sessionId!, 104, "tools/call", new
        {
            name = "discussion_message_create",
            arguments = new { request = new { threadId = mcpToChat.Id, senderProjectId = peerProjectId, content = "Replied by mcp-chat." } }
        });
        var mcpToChatReadPayload = await SendMcpAsync(chatClient, sessionId!, 105, "tools/call", new
        {
            name = "discussion_thread_get",
            arguments = new { threadId = mcpToChat.Id, readerProjectId = ProjectId }
        });
        ExtractToolJson(mcpToChatReadPayload).GetProperty("messages").GetArrayLength().Should().Be(2);

        // Chat to MCP: mcp-chat creates, then direct MCP replies and reads it.
        var chatToMcpPayload = await SendMcpAsync(chatClient, sessionId!, 106, "tools/call", new
        {
            name = "discussion_thread_create",
            arguments = new { request = new { hostProjectId = ProjectId, senderProjectId = ProjectId, title = "Chat to MCP", participantProjectIds = new[] { ProjectId, peerProjectId }, initialMessage = "Created by mcp-chat." } }
        });
        var chatToMcpId = ExtractToolJson(chatToMcpPayload).GetProperty("id").GetGuid();
        await UseDirectMcpAsync(mcp => mcp.discussion_message_create(new(chatToMcpId, peerProjectId, "Replied by direct MCP.")));
        (await UseDirectMcpAsync(mcp => mcp.discussion_thread_get(chatToMcpId, ProjectId)))!.Messages.Should().HaveCount(2);

        // Both MCP surfaces can close a thread hosted by an authorized ProjectId.
        (await UseDirectMcpAsync(mcp => mcp.discussion_thread_close(mcpToMcp.Id)))!.Status.Should().Be("Closed");
        await Assert.ThrowsAsync<InvalidOperationException>(() => UseDirectMcpAsync(mcp => mcp.discussion_message_create(new(mcpToMcp.Id, peerProjectId, "This reply must be rejected."))));
        var closePayload = await SendMcpAsync(chatClient, sessionId!, 107, "tools/call", new
        {
            name = "discussion_thread_close",
            arguments = new { threadId = chatToChatId }
        });
        ExtractToolJson(closePayload).GetProperty("status").GetString().Should().Be("Closed");

        var directArchived = await UseDirectMcpAsync(mcp => mcp.discussion_thread_archive(mcpToMcp.Id));
        directArchived!.IsArchived.Should().BeTrue();
        (await UseDirectMcpAsync(mcp => mcp.discussion_threads_list(new(ProjectId))))
            .Should().NotContain(x => x.Id == mcpToMcp.Id);
        (await UseDirectMcpAsync(mcp => mcp.discussion_threads_list(new(ProjectId, IncludeArchived: true))))
            .Should().ContainSingle(x => x.Id == mcpToMcp.Id && x.IsArchived);
        (await UseDirectMcpAsync(mcp => mcp.discussion_thread_restore(mcpToMcp.Id)))!.Status.Should().Be("Closed");

        var archivePayload = await SendMcpAsync(chatClient, sessionId!, 108, "tools/call", new
        {
            name = "discussion_thread_archive",
            arguments = new { threadId = chatToChatId }
        });
        ExtractToolJson(archivePayload).GetProperty("isArchived").GetBoolean().Should().BeTrue();
        var restorePayload = await SendMcpAsync(chatClient, sessionId!, 109, "tools/call", new
        {
            name = "discussion_thread_restore",
            arguments = new { threadId = chatToChatId }
        });
        ExtractToolJson(restorePayload).GetProperty("status").GetString().Should().Be("Closed");
        ExtractToolJson(restorePayload).GetProperty("isArchived").GetBoolean().Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Governance_Proposal_Payload_Should_Reject_Request_Wrapper_And_Unknown_Fields()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var proposals = scope.ServiceProvider.GetRequiredService<IChatGptProposalService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"strict-proposal-{Guid.NewGuid():N}";
        var memoryId = Guid.NewGuid();
        var before = await dbContext.ConversationInsights.CountAsync();

        var wrapped = () => proposals.CreateAsync(new ChatGptProposalCreateRequest(
            "memory_archive",
            projectId,
            JsonSerializer.Serialize(new { request = new { id = memoryId, projectId, archived = true } }),
            "Invalid wrapped payload",
            "The payload must be direct target-tool arguments.",
            GovernanceRunId: $"strict-{Guid.NewGuid():N}"), CancellationToken.None);
        var unknown = () => proposals.CreateAsync(new ChatGptProposalCreateRequest(
            "memory_archive",
            projectId,
            JsonSerializer.Serialize(new { id = memoryId, projectId, archived = true, unknownField = "blocked" }),
            "Invalid unknown field",
            "Unknown fields must fail closed.",
            GovernanceRunId: $"strict-{Guid.NewGuid():N}"), CancellationToken.None);
        var missingRequired = () => proposals.CreateAsync(new ChatGptProposalCreateRequest(
            "memory_move",
            projectId,
            JsonSerializer.Serialize(new { id = memoryId }),
            "Invalid missing target",
            "Required targetProjectId must fail closed.",
            GovernanceRunId: $"strict-{Guid.NewGuid():N}"), CancellationToken.None);

        await wrapped.Should().ThrowAsync<InvalidOperationException>().WithMessage("*schema*");
        await unknown.Should().ThrowAsync<InvalidOperationException>().WithMessage("*schema*");
        await missingRequired.Should().ThrowAsync<InvalidOperationException>().WithMessage("*targetProjectId*");
        (await dbContext.ConversationInsights.CountAsync()).Should().Be(before);
    }

    [DockerRequiredFact]
    public async Task Governance_Batch_Should_Fail_Closed_For_Scheduled_Delete_Invalid_Cursor_Expired_Snapshot_And_NonAdmin()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var admin = actorAccessor.Current;
        var executor = scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>();
        var reviewService = scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"batch-closed-{Guid.NewGuid():N}";
        var runId = $"batch-closed-run-{Guid.NewGuid():N}";
        var review = await reviewService.ReviewAsync(new KnowledgeReviewRequest([projectId], GovernanceRunId: runId), CancellationToken.None);
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 10,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.Reindex],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);

        var hardDelete = () => executor.ExecuteAsync(request with { AllowHardDelete = true }, CancellationToken.None);
        await hardDelete.Should().ThrowAsync<InvalidOperationException>().WithMessage("*AllowHardDelete=false*");
        var missingSnapshot = () => executor.ExecuteAsync(request with { SnapshotToken = null }, CancellationToken.None);
        await missingSnapshot.Should().ThrowAsync<InvalidOperationException>().WithMessage("*snapshotToken*");
        var invalidMutations = () => executor.ExecuteAsync(request with { MaxMutations = 0 }, CancellationToken.None);
        await invalidMutations.Should().ThrowAsync<InvalidOperationException>().WithMessage("*MaxMutations*");
        var invalidDuration = () => executor.ExecuteAsync(request with { MaxDurationSeconds = 0 }, CancellationToken.None);
        await invalidDuration.Should().ThrowAsync<InvalidOperationException>().WithMessage("*MaxDurationSeconds*");

        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel();
            var cancelledExecution = () => executor.ExecuteAsync(
                request with { GovernanceRunId = $"cancelled-{Guid.NewGuid():N}" }, cancelled.Token);
            await cancelledExecution.Should().ThrowAsync<OperationCanceledException>();
        }

        var first = await executor.ExecuteAsync(request, CancellationToken.None);
        var payloadMismatch = await executor.ExecuteAsync(request with { MaxDurationSeconds = 31 }, CancellationToken.None);
        payloadMismatch.ErrorCode.Should().Be(GovernanceBatchErrorCode.ReplayPayloadMismatch);
        var invalidCursor = await executor.ExecuteAsync(request with { Cursor = "invalid-cursor", MaxMutations = 11 }, CancellationToken.None);
        invalidCursor.ErrorCode.Should().Be(GovernanceBatchErrorCode.InvalidCursor);
        var wrongProject = () => executor.ExecuteAsync(request with
        {
            ProjectIds = [projectId, $"wrong-project-{Guid.NewGuid():N}"],
            SnapshotToken = first.SnapshotToken
        }, CancellationToken.None);
        await wrongProject.Should().ThrowAsync<InvalidOperationException>().WithMessage("*ProjectId*");

        var run = await dbContext.GovernanceBatchRuns.SingleAsync(x => x.GovernanceRunId == runId);
        run.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();
        var expired = await executor.ExecuteAsync(request with { SnapshotToken = first.SnapshotToken, MaxDurationSeconds = 31 }, CancellationToken.None);
        expired.ErrorCode.Should().Be(GovernanceBatchErrorCode.CursorExpired);

        actorAccessor.Current = admin with { Role = TenantUserRole.Member };
        var nonAdmin = () => executor.ExecuteAsync(request with { GovernanceRunId = $"member-{Guid.NewGuid():N}" }, CancellationToken.None);
        await nonAdmin.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*administrator*");

        actorAccessor.Current = admin with { Scopes = [SecurityScopes.MemoryRead] };
        var missingScope = () => executor.ExecuteAsync(request with { GovernanceRunId = $"scope-{Guid.NewGuid():N}" }, CancellationToken.None);
        await missingScope.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*memory:write*");
    }

    [DockerRequiredFact]
    public async Task Governance_Batch_Should_Preserve_Completed_Items_And_Replay_When_A_Later_Item_Fails()
    {
        var projectId = $"batch-isolation-{Guid.NewGuid():N}";
        await SeedInsightOnlyProjectAsync(projectId);
        await SeedInsightOnlyProjectAsync(projectId);
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var executor = scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>();
        var reviewService = scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"batch-isolation-run-{Guid.NewGuid():N}";
        var review = await reviewService.ReviewAsync(new KnowledgeReviewRequest([projectId], GovernanceRunId: runId), CancellationToken.None);
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 10,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.ConversationInsightDisposition],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: true,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
        _ = await executor.ExecuteAsync(request, CancellationToken.None);

        var run = await dbContext.GovernanceBatchRuns.SingleAsync(x => x.GovernanceRunId == request.GovernanceRunId);
        const string marker = "\"kind\":\"ConversationInsight\"";
        var lastMarker = run.PlanJson.LastIndexOf(marker, StringComparison.Ordinal);
        lastMarker.Should().BeGreaterThan(0);
        run.PlanJson = run.PlanJson[..lastMarker] + "\"kind\":\"Unsupported\"" + run.PlanJson[(lastMarker + marker.Length)..];
        await dbContext.SaveChangesAsync();

        var executeRequest = request with { DryRun = false };
        var result = await executor.ExecuteAsync(executeRequest, CancellationToken.None);
        result.StoppedReason.Should().Be("ItemFailed");
        result.RequiresUserDecisionCount.Should().Be(1);
        result.FailedCount.Should().Be(1);
        result.Items.Should().ContainSingle(x => x.CursorDisposition == "Advanced");
        result.Items.Should().ContainSingle(x => x.CursorDisposition == "NotAdvancedRetryable" && x.Retryable);
        (await dbContext.ConversationInsights.CountAsync(x => x.ProjectId == projectId && x.PromotionStatus == ConversationPromotionStatus.RequiresUserDecision)).Should().Be(1);
        (await dbContext.ConversationInsights.CountAsync(x => x.ProjectId == projectId && x.PromotionStatus == ConversationPromotionStatus.Pending)).Should().Be(1);

        var replay = await executor.ExecuteAsync(executeRequest, CancellationToken.None);
        replay.IsReplay.Should().BeTrue();
        replay.Items.Select(x => (x.ItemKey, x.Disposition, x.CursorDisposition, x.Error))
            .Should().Equal(result.Items.Select(x => (x.ItemKey, x.Disposition, x.CursorDisposition, x.Error)));
        replay.AuditIds.Should().Equal(result.AuditIds);
    }

    [DockerRequiredFact]
    public async Task Governance_Batch_Should_Merge_Exact_Duplicate_Read_Back_Replacement_Chain_Then_Archive_Secondary()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var owner = actorAccessor.Current;
        var projectId = $"batch-merge-{Guid.NewGuid():N}";
        var runId = $"batch-merge-run-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var primary = new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ProjectId = projectId,
            ExternalKey = $"merge-primary:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Artifact,
            Title = "Exact governed duplicate",
            Content = "Identical execution evidence for a deterministic governed merge.",
            Summary = "Identical governed merge fixture.",
            SourceType = "batch-fixture",
            SourceRef = "fixture:authoritative",
            Tags = ["authoritative"],
            Importance = .7m,
            Confidence = .8m,
            Status = MemoryStatus.Active,
            MetadataJson = "{\"source\":\"primary\"}",
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now
        };
        var secondary = new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ProjectId = projectId,
            ExternalKey = $"merge-secondary:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Artifact,
            Title = primary.Title,
            Content = primary.Content,
            Summary = primary.Summary,
            SourceType = "batch-fixture",
            SourceRef = "fixture:secondary",
            Tags = ["execution-evidence"],
            Importance = .4m,
            Confidence = .5m,
            Status = MemoryStatus.Active,
            MetadataJson = "{\"source\":\"secondary\"}",
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now
        };
        await dbContext.MemoryItems.AddRangeAsync(primary, secondary);
        await dbContext.SaveChangesAsync();

        var review = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: runId), CancellationToken.None);
        review.ProjectKnowledgeGovernance!.Candidates.Select(x => x.Classification).Intersect([
            GovernanceFindingType.DuplicateCandidate,
            GovernanceFindingType.DuplicateMemoryCandidate,
            GovernanceFindingType.MergeMemoryCandidate
        ]).Should().NotBeEmpty();
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 100,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.Merge, GovernanceBatchActionType.Archive],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
        var result = await gatewayTools.governance_batch_execute(request, CancellationToken.None);
        result.MergedCount.Should().BeGreaterThan(0, JsonSerializer.Serialize(result));
        result.ArchivedCount.Should().BeGreaterThan(0);
        result.DeleteProposalCount.Should().Be(0);

        var primaryReadBack = (await memoryService.GetAsync(primary.Id, CancellationToken.None))!;
        var secondaryReadBack = (await memoryService.GetAsync(secondary.Id, CancellationToken.None))!;
        primaryReadBack.Status.Should().Be(MemoryStatus.Active);
        secondaryReadBack.Status.Should().Be(MemoryStatus.Archived);
        using var primaryMetadata = JsonDocument.Parse(primaryReadBack.MetadataJson);
        primaryMetadata.RootElement.GetProperty("mergedFromMemoryIds").EnumerateArray()
            .Select(x => x.GetString()).Should().Contain(secondary.Id.ToString("D"));
        using var secondaryMetadata = JsonDocument.Parse(secondaryReadBack.MetadataJson);
        secondaryMetadata.RootElement.GetProperty("supersededByMemoryId").GetString().Should().Be(primary.Id.ToString("D"));
        (await dbContext.MemoryLinks.AnyAsync(x => x.LinkType == "replaced_by" && x.FromId == secondary.Id && x.ToId == primary.Id)).Should().BeTrue();

        var replay = await gatewayTools.governance_batch_execute(request, CancellationToken.None);
        replay.IsReplay.Should().BeTrue();
        (await dbContext.MemoryItems.CountAsync(x => x.ProjectId == projectId && x.Status == MemoryStatus.Archived)).Should().Be(1);
        var replayReceipt = await gatewayTools.governance_run_get(runId, CancellationToken.None);
        replayReceipt.Should().NotBeNull();
        replayReceipt!.IsReplay.Should().BeTrue();
        replayReceipt.Applied.Should().Be(result.AppliedCount);
        replayReceipt.AuditIds.Should().BeEquivalentTo(result.AuditIds);
        replayReceipt.RunExists.Should().BeTrue();
        replayReceipt.Status.Should().Be("Completed");
        replayReceipt.LatestBatchReceived.Should().BeTrue();
        replayReceipt.RequestIdentityHash.Should().MatchRegex("^[a-f0-9]{64}$");
        replayReceipt.LatestBatch.Should().NotBeNull();
        replayReceipt.LatestBatch!.Executed.Should().BeTrue();
        replayReceipt.LatestBatch.RequestHash.Should().MatchRegex("^[a-f0-9]{64}$");
        replayReceipt.LatestBatch.Applied.Should().Be(result.AppliedCount);
        replayReceipt.LatestBatch.AuditIds.Should().BeEquivalentTo(result.AuditIds);
        replayReceipt.LatestBatch.NextCursor.Should().Be(result.NextCursor);
        replayReceipt.LatestBatch.RequiresReReview.Should().Be(result.RequiresReReview);
        replayReceipt.LatestBatch.SnapshotGeneration.Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Governance_Run_Receipt_Should_Recover_Unknown_Outcome_And_Enforce_Project_And_Tenant_Isolation()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var originalActor = actorAccessor.Current;
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var projectId = $"receipt-recovery-{Guid.NewGuid():N}";
        var runId = $"receipt-recovery-run-{Guid.NewGuid():N}";
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            $"kg:{Guid.NewGuid():N}:0123456789abcdef:i0",
            Cursor: "opaque-cursor",
            MaxMutations: 10,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.Archive],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
        var startedAt = DateTimeOffset.UtcNow;

        await receipts.RecordExecutionStartedAsync(request, startedAt, CancellationToken.None);

        var running = await receipts.GetAsync(runId, CancellationToken.None);
        running.Should().NotBeNull();
        running!.RunExists.Should().BeTrue();
        running.Status.Should().Be("Running");
        running.LatestBatchReceived.Should().BeTrue();
        running.RequestIdentityHash.Should().MatchRegex("^[a-f0-9]{64}$");
        running.LatestBatch.Should().NotBeNull();
        running.LatestBatch!.Received.Should().BeTrue();
        running.LatestBatch.Executed.Should().BeFalse();
        running.LatestBatch.RequestHash.Should().BeEmpty();
        running.LatestBatch.CursorBefore.Should().BeEmpty();
        running.LatestBatch.RequiresReReview.Should().BeTrue();

        await receipts.RecordExecutionStoppedAsync(
            request, startedAt, "Stopped", "RequestCancelledOutcomeUnknown", CancellationToken.None);
        var stopped = await receipts.GetAsync(runId, CancellationToken.None);
        stopped!.Status.Should().Be("Stopped");
        stopped.StoppedReason.Should().Be("RequestCancelledOutcomeUnknown");
        stopped.LatestBatch!.Executed.Should().BeFalse();
        stopped.LatestBatch.Status.Should().Be("Stopped");

        var failedRunId = $"receipt-failed-run-{Guid.NewGuid():N}";
        var failedRequest = request with { GovernanceRunId = failedRunId };
        await receipts.RecordExecutionStartedAsync(failedRequest, startedAt, CancellationToken.None);
        await receipts.RecordExecutionStoppedAsync(
            failedRequest, startedAt, "Failed", "UnhandledExecutionFailure", CancellationToken.None);
        (await receipts.GetAsync(failedRunId, CancellationToken.None))!.Status.Should().Be("Failed");

        actorAccessor.Current = originalActor with { AllowedProjectIds = ["different-project"] };
        var deniedGet = () => receipts.GetAsync(runId, CancellationToken.None);
        await deniedGet.Should().ThrowAsync<UnauthorizedAccessException>();
        (await receipts.ListAsync(new GovernanceRunReceiptListRequest(Limit: 100), CancellationToken.None))
            .Should().NotContain(x => x.GovernanceRunId == runId);

        actorAccessor.Current = originalActor with { TenantId = Guid.NewGuid(), AllowedProjectIds = [] };
        (await receipts.GetAsync(runId, CancellationToken.None)).Should().BeNull();
        (await receipts.ListAsync(new GovernanceRunReceiptListRequest(Limit: 100), CancellationToken.None))
            .Should().NotContain(x => x.GovernanceRunId == runId);
    }

    [DockerRequiredFact]
    public async Task Governance_Batch_Should_Create_Direct_Payload_Delete_Proposal_Without_Hard_Delete()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var proposals = scope.ServiceProvider.GetRequiredService<IChatGptProposalService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = actorAccessor.Current;
        var projectId = $"batch-delete-proposal-{Guid.NewGuid():N}";
        var runId = $"batch-delete-proposal-run-{Guid.NewGuid():N}";
        var memory = new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ProjectId = projectId,
            ExternalKey = $"obsolete:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Artifact,
            Title = "Obsolete memory retaining audit value",
            Content = "Deletion requires an explicit per-memory proposal and user decision.",
            Summary = "Scheduled governance must not hard-delete this memory.",
            SourceType = "batch-fixture",
            SourceRef = "fixture:delete-proposal",
            Tags = ["obsolete"],
            Importance = .6m,
            Confidence = .7m,
            Status = MemoryStatus.Active,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        await dbContext.MemoryItems.AddAsync(memory);
        await dbContext.SaveChangesAsync();

        var review = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: runId), CancellationToken.None);
        var result = await gatewayTools.governance_batch_execute(new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 100,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.DeleteProposal],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled), CancellationToken.None);
        result.DeleteProposalCount.Should().BeGreaterThan(0);
        result.ArchivedCount.Should().Be(0);
        result.RequiresUserDecisionCount.Should().BeGreaterThan(0);
        (await dbContext.MemoryItems.SingleAsync(x => x.Id == memory.Id)).Status.Should().Be(MemoryStatus.Active);
        var proposal = (await proposals.ListAsync(new ChatGptProposalListRequest(projectId, ChatGptProposalStatus.Pending, 100), CancellationToken.None))
            .Single(x => x.ToolName == "memory_delete");
        using var payload = JsonDocument.Parse(proposal.PayloadJson);
        payload.RootElement.TryGetProperty("request", out _).Should().BeFalse();
        payload.RootElement.GetProperty("id").GetGuid().Should().Be(memory.Id);
    }

    [DockerRequiredFact]
    public async Task Internal_Worker_Should_Delete_Only_Revalidated_Matured_Quarantine_And_External_Call_Should_Fail_Closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = actorAccessor.Current;
        var projectId = $"scheduled-matured-delete-{Guid.NewGuid():N}";
        var runId = $"scheduled-matured-delete-run-{Guid.NewGuid():N}";
        var memory = new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ProjectId = projectId,
            ExternalKey = $"synthetic-disposable:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Episode,
            Title = "Disposable tool execution evidence",
            Content = "Synthetic production-safe governance fixture.",
            Summary = "Disposable execution evidence.",
            SourceType = "tool-execution",
            SourceRef = "synthetic-governance-fixture",
            Tags = ["execution-evidence", "synthetic-disposable", "low-value"],
            Importance = .1m,
            Confidence = .2m,
            Status = MemoryStatus.Active,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        dbContext.MemoryItems.Add(memory);
        await dbContext.SaveChangesAsync();

        var initialReview = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: runId), CancellationToken.None);
        var quarantineRequest = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            initialReview.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 20,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.Quarantine],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
        var quarantined = await gatewayTools.governance_batch_execute(quarantineRequest, CancellationToken.None);
        quarantined.QuarantinedCount.Should().Be(1, JsonSerializer.Serialize(quarantined));
        (await dbContext.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status.Should().Be(MemoryStatus.Archived);

        var state = await dbContext.MemoryRetentionStates.SingleAsync(x => x.ResourceId == memory.Id);
        state.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-8);
        state.DeleteEligibleAt = DateTimeOffset.UtcNow.AddDays(-1);
        await dbContext.SaveChangesAsync();

        var maturedReview = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: runId, IsReReview: true), CancellationToken.None);
        var deleteRequest = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            maturedReview.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 20,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.MaturedDelete],
            MaxRiskLevel: GovernanceBatchRiskLevel.High,
            AllowHardDelete: false,
            AllowMaturedDelete: true,
            IsReReview: true,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
        var deleted = await gatewayTools.governance_batch_execute(deleteRequest, CancellationToken.None);
        deleted.Succeeded.Should().BeFalse();
        deleted.ErrorCode.Should().Be(GovernanceBatchErrorCode.HostBlockedMaturedDelete);
        deleted.AutoDeletedCount.Should().Be(0);
        (await dbContext.MemoryItems.AnyAsync(x => x.Id == memory.Id)).Should().BeTrue();

        var blockedReceipt = await gatewayTools.governance_run_get(runId, CancellationToken.None);
        blockedReceipt.Should().NotBeNull();
        blockedReceipt!.StoppedReason.Should().Be(nameof(GovernanceBatchErrorCode.HostBlockedMaturedDelete));
        blockedReceipt.HostBlocked.Should().BeGreaterThan(0);
        blockedReceipt.Status.Should().Be("Stopped");
        blockedReceipt.LatestBatch.Should().NotBeNull();
        blockedReceipt.LatestBatch!.Executed.Should().BeFalse(
            "the prior quarantine execution must not be attributed to the later host-blocked request");
        blockedReceipt.LatestBatch.RequestHash.Should().BeEmpty();

        var internalExecutor = scope.ServiceProvider.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var workerResult = await internalExecutor.ExecuteNextBatchAsync(CancellationToken.None);
        workerResult.DeletedCount.Should().BeGreaterThan(0, JsonSerializer.Serialize(workerResult));
        (await dbContext.MemoryItems.AnyAsync(x => x.Id == memory.Id)).Should().BeFalse();
        (await dbContext.ResourceTombstones.CountAsync(x => x.ResourceId == memory.Id)).Should().Be(1);

        var replay = await internalExecutor.ExecuteNextBatchAsync(CancellationToken.None);
        replay.DeletedCount.Should().Be(0);
        (await dbContext.ResourceTombstones.CountAsync(x => x.ResourceId == memory.Id)).Should().Be(1);

        var workerReceipt = await gatewayTools.governance_run_get(workerResult.GovernanceRunId, CancellationToken.None);
        workerReceipt.Should().NotBeNull();
        workerReceipt!.ExecutionMode.Should().Be("InternalRetentionWorker");
        workerReceipt.AutoDeleted.Should().Be(workerResult.DeletedCount);
        workerReceipt.Tombstoned.Should().Be(workerResult.TombstoneIds.Count);
        var receipts = await gatewayTools.governance_runs_list(
            new GovernanceRunReceiptListRequest(projectId, Limit: 20), CancellationToken.None);
        receipts.Should().Contain(x => x.GovernanceRunId == runId
            && x.StoppedReason == nameof(GovernanceBatchErrorCode.HostBlockedMaturedDelete));
        receipts.Should().Contain(x => x.GovernanceRunId == workerResult.GovernanceRunId
            && x.ExecutionMode == "InternalRetentionWorker");
        var storedReceipt = await dbContext.GovernanceRunReceipts.SingleAsync(x =>
            x.GovernanceRunId == workerResult.GovernanceRunId);
        storedReceipt.StoppedReason = "tampered";
        var mutateReceipt = () => dbContext.SaveChangesAsync();
        var immutableError = await mutateReceipt.Should().ThrowAsync<DbUpdateException>();
        immutableError.Which.InnerException!.Message.Should().Contain("append-only");
        dbContext.ChangeTracker.Clear();
    }

    [DockerRequiredFact]
    public async Task Scheduled_Governance_Should_AutoResolve_Reversible_Semantic_Candidate_With_Exact_Durable_Evidence()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = actorAccessor.Current;
        var projectId = $"semantic-auto-resolution-{Guid.NewGuid():N}";
        var runId = $"semantic-auto-resolution-run-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var durable = new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ProjectId = projectId,
            ExternalKey = $"semantic-authority:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Artifact,
            Title = "Verified reversible semantic evidence",
            Content = "Durable evidence already contains the candidate meaning.",
            Summary = "Exact durable semantic evidence.",
            SourceType = "verified-fixture",
            SourceRef = "synthetic-governance-fixture",
            Importance = .7m,
            Confidence = .99m,
            Status = MemoryStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        var session = new ConversationSession
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = $"semantic-auto:{Guid.NewGuid():N}",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "semantic-fixture",
            LastTurnId = "turn-1",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = session.SourceSystem,
            SourceRef = "semantic-fixture",
            DedupKey = $"semantic-auto:{Guid.NewGuid():N}",
            CreatedAt = now
        };
        var insight = new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = session.SourceSystem,
            SourceRef = "semantic-fixture",
            SourceKind = ConversationSourceKind.AgentSupplemental,
            InsightType = ConversationInsightType.Episode,
            Title = durable.Title,
            Content = durable.Content,
            Summary = durable.Summary,
            Importance = .6m,
            Confidence = .96m,
            DedupKey = $"semantic-auto-insight:{Guid.NewGuid():N}",
            PromotionStatus = ConversationPromotionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        dbContext.MemoryItems.Add(durable);
        dbContext.ConversationInsights.Add(insight);
        await dbContext.SaveChangesAsync();

        var review = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: runId), CancellationToken.None);
        var result = await gatewayTools.governance_batch_execute(new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 20,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.ConversationInsightDisposition, GovernanceBatchActionType.SemanticReevaluate],
            MaxRiskLevel: GovernanceBatchRiskLevel.Medium,
            SemanticAutoResolutionConfidenceThreshold: .90m,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled), CancellationToken.None);

        result.SemanticAutoResolvedCount.Should().Be(1, JsonSerializer.Serialize(result));
        result.Items.Should().ContainSingle(x => x.ResourceId == insight.Id && x.SemanticAutoResolved);
        (await dbContext.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id))
            .PromotionStatus.Should().Be(ConversationPromotionStatus.Skipped);
    }

    [DockerRequiredFact]
    public async Task Governance_Batch_Cursor_Should_Continue_Across_ReReview_And_Return_Stable_Error_Codes()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"cursor-rereview-{Guid.NewGuid():N}";
        var governanceRunId = $"cursor-rereview-run-{Guid.NewGuid():N}";
        var owner = actorAccessor.Current;
        var now = DateTimeOffset.UtcNow;
        var session = new ConversationSession
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = $"cursor-rereview-{Guid.NewGuid():N}",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "cursor-rereview-fixture",
            LastTurnId = "turn-1",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = session.SourceSystem,
            SourceRef = "fixture:cursor-rereview",
            DedupKey = $"cursor-rereview:{Guid.NewGuid():N}",
            CreatedAt = now
        };
        var insights = Enumerable.Range(0, 3).Select(index => new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = session.SourceSystem,
            SourceRef = $"fixture:cursor-rereview:{index}",
            SourceKind = ConversationSourceKind.AgentSupplemental,
            InsightType = ConversationInsightType.Fact,
            Title = $"Cursor re-review insight {index}",
            Content = $"Protected insight {index} requires a user decision.",
            Summary = "Cross-snapshot continuation fixture.",
            Importance = .9m,
            Confidence = .9m,
            DedupKey = $"cursor-rereview:{projectId}:{index}",
            PromotionStatus = ConversationPromotionStatus.Pending,
            MetadataJson = "{}",
            CreatedAt = now.AddMilliseconds(index),
            UpdatedAt = now.AddMilliseconds(index)
        }).ToArray();
        await dbContext.ConversationInsights.AddRangeAsync(insights);
        await dbContext.SaveChangesAsync();

        var review = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], GovernanceRunId: governanceRunId), CancellationToken.None);
        var requestA = new GovernanceBatchExecuteRequest(
            governanceRunId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 1,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.ConversationInsightDisposition],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
        var batchA = await gatewayTools.governance_batch_execute(requestA, CancellationToken.None);
        batchA.Succeeded.Should().BeTrue();
        batchA.RequiresUserDecisionCount.Should().Be(1);
        batchA.HasMore.Should().BeTrue();
        batchA.NextCursor.Should().NotBeNullOrWhiteSpace();
        var itemA = batchA.Items.Should().ContainSingle().Subject.ResourceId;

        var replay = await gatewayTools.governance_batch_execute(requestA, CancellationToken.None);
        replay.IsReplay.Should().BeTrue();
        replay.Items.Select(x => x.ResourceId).Should().Equal(batchA.Items.Select(x => x.ResourceId));
        replay.AuditIds.Should().Equal(batchA.AuditIds);

        var reReview = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], GovernanceRunId: governanceRunId, IsReReview: true), CancellationToken.None);
        reReview.DurableMemoryCoverage!.SnapshotToken.Should().NotBe(review.DurableMemoryCoverage.SnapshotToken);
        var requestB = requestA with
        {
            SnapshotToken = reReview.DurableMemoryCoverage.SnapshotToken,
            Cursor = batchA.NextCursor,
            IsReReview = true
        };
        var batchB = await gatewayTools.governance_batch_execute(requestB, CancellationToken.None);
        batchB.Succeeded.Should().BeTrue();
        batchB.RequiresUserDecisionCount.Should().Be(1);
        batchB.Items.Should().ContainSingle();
        batchB.Items[0].ResourceId.Should().NotBe(itemA);
        batchB.NextCursor.Should().NotBe(batchA.NextCursor);

        var policyMismatch = await gatewayTools.governance_batch_execute(requestB with
        {
            MaxRiskLevel = GovernanceBatchRiskLevel.Medium
        }, CancellationToken.None);
        policyMismatch.Succeeded.Should().BeFalse();
        policyMismatch.ErrorCode.Should().Be(GovernanceBatchErrorCode.CursorPolicyMismatch);
        policyMismatch.StoppedReason.Should().Be(nameof(GovernanceBatchErrorCode.CursorPolicyMismatch));

        var scopeMismatch = await gatewayTools.governance_batch_execute(requestB with
        {
            ProjectIds = [projectId, $"{projectId}-other"]
        }, CancellationToken.None);
        scopeMismatch.Succeeded.Should().BeFalse();
        scopeMismatch.ErrorCode.Should().Be(GovernanceBatchErrorCode.CursorScopeMismatch);

        var payloadMismatch = await gatewayTools.governance_batch_execute(requestB with
        {
            DryRun = true
        }, CancellationToken.None);
        payloadMismatch.Succeeded.Should().BeFalse();
        payloadMismatch.ErrorCode.Should().Be(GovernanceBatchErrorCode.ReplayPayloadMismatch);

        var invalid = await gatewayTools.governance_batch_execute(requestB with { Cursor = "gb2.invalid" }, CancellationToken.None);
        invalid.Succeeded.Should().BeFalse();
        invalid.ErrorCode.Should().Be(GovernanceBatchErrorCode.InvalidCursor);

        var otherActor = owner with { UserId = Guid.NewGuid() };
        actorAccessor.Current = otherActor;
        var actorMismatch = await gatewayTools.governance_batch_execute(requestB, CancellationToken.None);
        actorMismatch.Succeeded.Should().BeFalse();
        actorMismatch.ErrorCode.Should().Be(GovernanceBatchErrorCode.CursorActorMismatch);

        actorAccessor.Current = owner;
        var sourceRun = await dbContext.GovernanceBatchRuns.SingleAsync(x =>
            x.GovernanceRunId == governanceRunId &&
            x.SnapshotToken == review.DurableMemoryCoverage.SnapshotToken);
        sourceRun.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        await dbContext.SaveChangesAsync();
        var expired = await gatewayTools.governance_batch_execute(requestB, CancellationToken.None);
        expired.Succeeded.Should().BeFalse();
        expired.ErrorCode.Should().Be(GovernanceBatchErrorCode.CursorExpired);
    }

    [DockerRequiredFact]
    public async Task Governance_Batch_Should_Retry_Prior_Generation_NoOp_When_ReReview_Still_Finds_Item_Actionable()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var owner = actorAccessor.Current;
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"noop-rereview-{Guid.NewGuid():N}";
        var governanceRunId = $"noop-rereview-run-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var session = new ConversationSession
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = $"noop-rereview-{Guid.NewGuid():N}",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "noop-rereview-fixture",
            LastTurnId = "turn-1",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = session.SourceSystem,
            SourceRef = "fixture:noop-rereview",
            DedupKey = $"noop-rereview:{Guid.NewGuid():N}",
            CreatedAt = now
        };
        var insights = Enumerable.Range(0, 6).Select(index => new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = session.SourceSystem,
            SourceRef = $"fixture:noop-rereview:{index}",
            SourceKind = ConversationSourceKind.AgentSupplemental,
            InsightType = ConversationInsightType.Episode,
            Title = $"NoOp re-review insight {index}",
            Content = $"Low-risk insight {index} requires a durable disposition.",
            Summary = "A prior-generation NoOp must not suppress a still-actionable item.",
            Importance = .2m,
            Confidence = .5m,
            DedupKey = $"noop-rereview:{projectId}:{index}",
            PromotionStatus = ConversationPromotionStatus.Pending,
            MetadataJson = "{}",
            CreatedAt = now.AddMilliseconds(index),
            UpdatedAt = now.AddMilliseconds(index)
        }).ToArray();
        await dbContext.ConversationInsights.AddRangeAsync(insights);
        await dbContext.SaveChangesAsync();

        var review = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: governanceRunId), CancellationToken.None);
        var orderedInsightKeys = review.GovernancePlan
            .Where(x => x.ItemKind == GovernanceItemKind.ConversationInsight)
            .Select(x => x.ItemKey)
            .ToArray();
        orderedInsightKeys.Should().HaveCount(6);
        var firstInsightId = Guid.ParseExact(orderedInsightKeys[0].Split(':')[1], "N");
        var requestA = new GovernanceBatchExecuteRequest(
            governanceRunId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 1,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.ConversationInsightDisposition],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
        var batchStart = await gatewayTools.governance_batch_execute(requestA, CancellationToken.None);
        batchStart.Items.Should().ContainSingle(x => x.ResourceId == firstInsightId && x.Disposition == GovernanceBatchItemDisposition.Deferred,
            JsonSerializer.Serialize(batchStart));
        batchStart.NextCursor.Should().NotBeNullOrWhiteSpace();

        var retriedInsightId = Guid.ParseExact(orderedInsightKeys[1].Split(':')[1], "N");
        var retriedInsight = await dbContext.ConversationInsights.SingleAsync(x => x.Id == retriedInsightId);
        retriedInsight.PromotionStatus = ConversationPromotionStatus.Deferred;
        retriedInsight.GovernanceReason = "Synthetic state change after immutable execution plan.";
        retriedInsight.UpdatedAt = DateTimeOffset.UtcNow;
        dbContext.ConversationInsights.Update(retriedInsight);
        await dbContext.SaveChangesAsync();

        var batchA = await gatewayTools.governance_batch_execute(requestA with { Cursor = batchStart.NextCursor }, CancellationToken.None);
        batchA.Items.Should().Contain(x => x.ItemKey == orderedInsightKeys[1] && x.Disposition == GovernanceBatchItemDisposition.NoOp);
        batchA.NextCursor.Should().NotBeNullOrWhiteSpace();

        retriedInsight = await dbContext.ConversationInsights.SingleAsync(x => x.Id == retriedInsightId);
        retriedInsight.PromotionStatus = ConversationPromotionStatus.Pending;
        retriedInsight.GovernanceReason = string.Empty;
        retriedInsight.GovernanceRunId = string.Empty;
        retriedInsight.GovernanceUpdatedAt = null;
        retriedInsight.UpdatedAt = DateTimeOffset.UtcNow;
        dbContext.ConversationInsights.Update(retriedInsight);
        await dbContext.SaveChangesAsync();
        var reReview = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: governanceRunId, IsReReview: true), CancellationToken.None);
        var reopenedInsightKey = reReview.GovernancePlan.Single(x =>
            x.ItemKind == GovernanceItemKind.ConversationInsight && x.AuthorityResourceId == retriedInsightId).ItemKey;
        reopenedInsightKey.Should().NotBe(orderedInsightKeys[1], "evidence-versioned keys must make reopened items executable in a new generation");

        var batchB = await gatewayTools.governance_batch_execute(requestA with
        {
            SnapshotToken = reReview.DurableMemoryCoverage!.SnapshotToken,
            Cursor = batchA.NextCursor,
            IsReReview = true
        }, CancellationToken.None);
        batchB.Items.Should().Contain(x => x.ItemKey == reopenedInsightKey && x.Disposition == GovernanceBatchItemDisposition.Deferred);
        (await dbContext.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == retriedInsightId))
            .PromotionStatus.Should().Be(ConversationPromotionStatus.Deferred);
    }

    [DockerRequiredFact]
    public async Task Full_Governance_Should_Cover_Mixed_Surfaces_And_Aggregate_100k_Logs_Server_Side()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var actor = actorAccessor.Current;
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var workItemService = scope.ServiceProvider.GetRequiredService<IProjectWorkItemService>();
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var projectId = $"full-governance-{Guid.NewGuid():N}";
        var childProjectId = $"full-governance-child-{Guid.NewGuid():N}";
        var danglingProjectId = $"missing-{Guid.NewGuid():N}";
        var governanceRunId = $"full-governance-run-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        dbContext.MemoryItems.AddRange(
            new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                ExternalKey = "system:project-information",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                Title = "Protected display name",
                Content = "Primary synthetic governance project.",
                Summary = "Primary project.",
                SourceType = "project-information",
                SourceRef = projectId,
                Importance = 1m,
                Confidence = 1m,
                MetadataJson = "{}",
                CreatedAt = now,
                UpdatedAt = now
            },
            new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = childProjectId,
                ExternalKey = "system:project-information",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                Title = "Child display name",
                Content = "Child synthetic governance project.",
                Summary = "Child project.",
                SourceType = "project-information",
                SourceRef = childProjectId,
                Importance = 1m,
                Confidence = 1m,
                MetadataJson = "{}",
                CreatedAt = now,
                UpdatedAt = now
            },
            new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = ProjectContext.UserProjectId,
                ExternalKey = $"user-preference:fixture-a-{Guid.NewGuid():N}",
                Scope = MemoryScope.User,
                MemoryType = MemoryType.Preference,
                Title = "Duplicate preference fixture",
                Content = "Use deterministic governance evidence.",
                Summary = "Fixture.",
                SourceType = "user-preference",
                SourceRef = "fixture-a",
                Importance = .95m,
                Confidence = .95m,
                MetadataJson = "{\"kind\":1,\"rationale\":\"fixture\"}",
                CreatedAt = now,
                UpdatedAt = now
            },
            new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = ProjectContext.UserProjectId,
                ExternalKey = $"user-preference:fixture-b-{Guid.NewGuid():N}",
                Scope = MemoryScope.User,
                MemoryType = MemoryType.Preference,
                Title = "Duplicate preference fixture",
                Content = "Use deterministic governance evidence.",
                Summary = "Fixture.",
                SourceType = "user-preference",
                SourceRef = "fixture-b",
                Importance = .9m,
                Confidence = .9m,
                MetadataJson = "{\"kind\":1,\"rationale\":\"fixture\"}",
                CreatedAt = now,
                UpdatedAt = now
            },
            new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                ExternalKey = $"artifact-a-{Guid.NewGuid():N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                Title = "Duplicate artifact fixture",
                Content = "Identical release evidence.",
                Summary = "Fixture.",
                SourceType = ProjectArtifactExchangeService.SourceType,
                SourceRef = "fixture-a",
                Importance = .7m,
                Confidence = .8m,
                MetadataJson = "{}",
                CreatedAt = now,
                UpdatedAt = now
            },
            new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                ExternalKey = $"artifact-b-{Guid.NewGuid():N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                Title = "Duplicate artifact fixture",
                Content = "Identical release evidence.",
                Summary = "Fixture.",
                SourceType = ProjectArtifactExchangeService.SourceType,
                SourceRef = "fixture-b",
                Importance = .65m,
                Confidence = .75m,
                MetadataJson = "{}",
                CreatedAt = now,
                UpdatedAt = now
            });
        dbContext.ProjectHierarchies.AddRange(
            new ProjectHierarchy { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ParentProjectId = projectId, ChildProjectId = childProjectId, CreatedAt = now, UpdatedAt = now },
            new ProjectHierarchy { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ParentProjectId = childProjectId, ChildProjectId = projectId, CreatedAt = now, UpdatedAt = now },
            new ProjectHierarchy { TenantId = actor.TenantId, OwnerUserId = actor.UserId, ParentProjectId = projectId, ChildProjectId = danglingProjectId, CreatedAt = now, UpdatedAt = now });
        var completedDiscussion = new DiscussionThread
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            HostProjectId = projectId,
            Title = "Completed discussion retention fixture",
            Status = "Closed",
            CreatedAt = now.AddDays(-60),
            UpdatedAt = now.AddDays(-45)
        };
        completedDiscussion.Participants =
        [
            new DiscussionParticipant { ThreadId = completedDiscussion.Id, ProjectId = projectId, LastReadAt = now.AddDays(-45) },
            new DiscussionParticipant { ThreadId = completedDiscussion.Id, ProjectId = childProjectId, LastReadAt = now.AddDays(-45) }
        ];
        dbContext.DiscussionThreads.Add(completedDiscussion);
        await dbContext.SaveChangesAsync();

        var businessWorkItem = await workItemService.CreateAsync(new ProjectWorkItemCreateRequest(projectId, "Normal business work remains active"), CancellationToken.None);
        businessWorkItem = await workItemService.UpdateAsync(new ProjectWorkItemUpdateRequest(businessWorkItem.Id, Status: ProjectWorkItemStatus.InProgress), CancellationToken.None);
        var historical = await workItemService.CreateAsync(new ProjectWorkItemCreateRequest(projectId, "Completed historical fixture"), CancellationToken.None);
        historical = await workItemService.UpdateAsync(new ProjectWorkItemUpdateRequest(historical.Id, Status: ProjectWorkItemStatus.Completed), CancellationToken.None);
        var historicalEntity = await dbContext.ProjectWorkItems.SingleAsync(x => x.Id == historical.Id);
        historicalEntity.UpdatedAt = now.AddDays(-120);
        await dbContext.SaveChangesAsync();

        await dbContext.Database.ExecuteSqlInterpolatedAsync($$"""
            INSERT INTO runtime_log_entries
                (project_id, service_name, category, level, message, exception, trace_id, request_id, payload_json, created_at)
            SELECT {{projectId}}, 'synthetic-governance', 'fixture', 'Information', 'bounded log fixture', '', '', '', '{}', {{now.AddDays(-45)}}
            FROM generate_series(1, 100001)
            """);
        dbContext.RuntimeLogEntries.Add(new RuntimeLogEntry
        {
            ProjectId = projectId,
            ServiceName = "synthetic-sensitive",
            Category = "fixture",
            Level = "Error",
            Message = "password=[REDACTED]",
            PayloadJson = "{}",
            CreatedAt = now.AddDays(-45)
        });
        await dbContext.SaveChangesAsync();

        var review = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId, childProjectId], GovernanceRunId: governanceRunId), CancellationToken.None);

        review.GovernanceCoverage.Should().NotBeNull();
        review.GovernanceCoverage!.CoverageComplete.Should().BeTrue();
        review.GovernanceCoverage.HierarchyCoverage.CandidateCount.Should().BeGreaterThanOrEqualTo(3);
        review.GovernanceCoverage.PreferenceCoverage.CandidateCount.Should().BeGreaterThan(0);
        review.GovernanceCoverage.ArtifactCoverage.CandidateCount.Should().BeGreaterThan(0);
        review.GovernanceCoverage.DiscussionCoverage.CandidateCount.Should().Be(1);
        review.GovernanceCoverage.WorkItemCoverage.CandidateCount.Should().Be(1);
        review.GovernanceCoverage.LogCoverage.TotalCount.Should().BeGreaterThan(100_000);
        review.GovernanceCoverage.LogCoverage.ScannedCount.Should().BeGreaterThan(100_000);
        review.GovernanceCoverage.LogCoverage.CandidateCount.Should().Be(2);
        review.Convergence.BusinessWorkItemActionableCount.Should().Be(1);
        review.GovernancePlan.Should().Contain(x => x.ItemKind == GovernanceItemKind.ProjectHierarchy && x.ReasonCodes.Contains("HIERARCHY_CYCLE"));
        review.GovernancePlan.Should().Contain(x => x.ItemKind == GovernanceItemKind.ProjectHierarchy && x.ReasonCodes.Contains("HIERARCHY_DANGLING_PROJECT"));
        review.GovernancePlan.Should().Contain(x => x.ItemKind == GovernanceItemKind.LogPartition && x.RecommendedAction == "LogRetentionProposal");
        review.GovernancePlan.Should().Contain(x => x.ItemKind == GovernanceItemKind.LogPartition &&
                                                     x.ReasonCodes.Contains("LOG_PROMOTION_PROHIBITED"));
        var sensitivePromotion = () => memoryService.PromoteLogSliceAsync(new PromoteLogSliceRequest(
            "Must not promote", ServiceName: "synthetic-sensitive", ProjectId: projectId), CancellationToken.None);
        await sensitivePromotion.Should().ThrowAsync<InvalidOperationException>().WithMessage("*security-sensitive*");

        var execute = await gatewayTools.governance_batch_execute(new GovernanceBatchExecuteRequest(
            governanceRunId, [projectId, childProjectId], review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 100, MaxDurationSeconds: 120,
            AllowedActionTypes: [GovernanceBatchActionType.DiscussionReconcile, GovernanceBatchActionType.WorkItemReconcile],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled), CancellationToken.None);
        execute.Succeeded.Should().BeTrue();
        execute.Items.Should().Contain(x => x.ItemKind == nameof(GovernanceItemKind.WorkItem) && x.Disposition == GovernanceBatchItemDisposition.Applied);
        execute.Items.Should().Contain(x => x.ItemKind == nameof(GovernanceItemKind.Discussion) && x.Disposition == GovernanceBatchItemDisposition.Applied);
        execute.Items.Should().Contain(x => x.ItemKind == nameof(GovernanceItemKind.ProjectHierarchy) &&
                                            x.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision && x.ProposalIds.Count > 0);
        (await dbContext.DiscussionThreads.AsNoTracking().SingleAsync(x => x.Id == completedDiscussion.Id)).ArchivedAt.Should().NotBeNull();
        (await dbContext.ProjectWorkItems.AsNoTracking().SingleAsync(x => x.Id == historical.Id)).ArchivedAt.Should().NotBeNull();
        (await dbContext.ProjectWorkItems.AsNoTracking().SingleAsync(x => x.Id == businessWorkItem.Id)).Status.Should().Be(ProjectWorkItemStatus.InProgress);
        (await dbContext.MemoryItems.AsNoTracking().SingleAsync(x => x.ProjectId == projectId && x.ExternalKey == "system:project-information")).Title
            .Should().Be("Protected display name");
        execute.Items.Should().NotContain(x => x.ActionType == GovernanceBatchActionType.DeleteProposal && x.Disposition == GovernanceBatchItemDisposition.Applied);
    }

    [DockerRequiredFact]
    public async Task Governance_Batch_Should_Drain_2000_Plus_Actionables_With_Cursor_Replay_And_Audited_Exceptions()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var workItems = scope.ServiceProvider.GetRequiredService<IProjectWorkItemService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"batch-2000-{Guid.NewGuid():N}";
        var governanceRunId = $"batch-run-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var owner = actorAccessor.Current;
        var memories = new[] { new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ProjectId = projectId,
            ExternalKey = $"reindex:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Artifact,
            Title = "Low-risk reindex fixture",
            Content = "Artifact content requiring deterministic reindex.",
            Summary = "Artifact has no current vector and is safe to reindex.",
            SourceType = "batch-fixture",
            SourceRef = "fixture:reindex",
            Importance = .5m,
            Confidence = .7m,
            Status = MemoryStatus.Active,
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        } };
        var session = new ConversationSession
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = $"batch-2000-{Guid.NewGuid():N}",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "batch-fixture",
            LastTurnId = "turn-2000",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-2000",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "batch-fixture",
            SourceRef = "fixture:checkpoint",
            DedupKey = $"batch-checkpoint:{Guid.NewGuid():N}",
            CreatedAt = now
        };
        var insights = Enumerable.Range(0, 2000).Select(index => new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = owner.TenantId,
            OwnerUserId = owner.UserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-2000",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "batch-fixture",
            SourceRef = $"fixture:insight:{index:D4}",
            SourceKind = ConversationSourceKind.AgentSupplemental,
            InsightType = ConversationInsightType.Fact,
            Title = $"Protected insight {index:D4}",
            Content = $"Insight {index:D4} requires an explicit user decision.",
            Summary = "High-signal Fact must not be auto-promoted by Scheduled governance.",
            Importance = .9m,
            Confidence = .9m,
            DedupKey = $"batch-insight:{projectId}:{index:D4}",
            PromotionStatus = ConversationPromotionStatus.Pending,
            MetadataJson = "{}",
            CreatedAt = now.AddMilliseconds(index),
            UpdatedAt = now.AddMilliseconds(index)
        }).ToArray();
        await dbContext.MemoryItems.AddRangeAsync(memories);
        await dbContext.ConversationInsights.AddRangeAsync(insights);
        await dbContext.SaveChangesAsync();
        var businessWorkItem = await workItems.CreateAsync(new ProjectWorkItemCreateRequest(
            projectId,
            "Business item must remain unchanged"), CancellationToken.None);
        businessWorkItem = await workItems.UpdateAsync(new ProjectWorkItemUpdateRequest(
            businessWorkItem.Id,
            Status: ProjectWorkItemStatus.InProgress), CancellationToken.None);

        var review = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 1, GovernanceRunId: governanceRunId), CancellationToken.None);
        review.DurableMemoryCoverage!.CoverageComplete.Should().BeTrue();
        review.ProjectKnowledgeGovernance!.Pagination.TotalCount.Should().Be(1);
        review.Pagination.HighSignalConversationInsights.TotalCount.Should().Be(2000);
        var initialCandidateCount = review.GovernancePlan.Count;
        initialCandidateCount.Should().BeGreaterThan(2000);
        review.Convergence.GovernedExceptionCount.Should().BeGreaterThanOrEqualTo(2000);

        string? cursor = null;
        var batches = new List<GovernanceBatchExecuteResult>();
        GovernanceBatchExecuteRequest? firstRequest = null;
        for (var batchIndex = 0; batchIndex < 30; batchIndex++)
        {
            var request = new GovernanceBatchExecuteRequest(
                governanceRunId,
                [projectId],
                review.DurableMemoryCoverage.SnapshotToken,
                cursor,
                MaxMutations: 300,
                MaxDurationSeconds: 900,
                AllowedActionTypes: [GovernanceBatchActionType.Reindex, GovernanceBatchActionType.ConversationInsightDisposition, GovernanceBatchActionType.SuggestedActionReconcile],
                MaxRiskLevel: GovernanceBatchRiskLevel.Low,
                DryRun: false,
                AllowHardDelete: false,
                IsReReview: false,
                ExecutionMode: GovernanceBatchExecutionMode.Scheduled);
            firstRequest ??= request;
            var batch = await gatewayTools.governance_batch_execute(request, CancellationToken.None);
            batches.Add(batch);
            batch.FailedCount.Should().Be(0);
            batch.Items.Should().OnlyContain(x => x.CursorDisposition == "Advanced");
            if (batchIndex == 0)
            {
                await dbContext.ConversationInsights.AddAsync(new ConversationInsight
                {
                    Session = session,
                    Checkpoint = checkpoint,
                    TenantId = owner.TenantId,
                    OwnerUserId = owner.UserId,
                    ConversationId = session.ConversationId,
                    TurnId = "turn-after-snapshot",
                    ProjectId = projectId,
                    ProjectName = projectId,
                    SourceSystem = "batch-fixture",
                    SourceRef = "fixture:after-snapshot",
                    SourceKind = ConversationSourceKind.AgentSupplemental,
                    InsightType = ConversationInsightType.Fact,
                    Title = "Insight created after the immutable snapshot",
                    Content = "A same-run re-review must discover this post-snapshot actionable.",
                    Summary = "Post-snapshot mutation coverage fixture.",
                    Importance = .9m,
                    Confidence = .9m,
                    DedupKey = $"batch-insight-after-snapshot:{projectId}",
                    PromotionStatus = ConversationPromotionStatus.Pending,
                    MetadataJson = "{}",
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                });
                await dbContext.SaveChangesAsync();
            }
            if (!batch.HasMore) break;
            batch.NextCursor.Should().NotBeNullOrWhiteSpace().And.NotBe(cursor);
            cursor = batch.NextCursor;
        }

        batches.Should().NotBeEmpty();
        batches[^1].HasMore.Should().BeFalse();
        batches.Sum(x => x.AppliedCount).Should().BeGreaterThan(0);
        batches.Sum(x => x.RequiresUserDecisionCount).Should().BeGreaterThanOrEqualTo(2000);
        batches.Sum(x => x.DeleteProposalCount).Should().Be(0);
        batches.SelectMany(x => x.AuditIds).Should().OnlyHaveUniqueItems();

        var jobsBeforeReplay = await dbContext.MemoryJobs.CountAsync(x => x.ProjectId == projectId && x.JobType == MemoryJobType.Reindex);
        jobsBeforeReplay.Should().BeGreaterThan(0);
        var replay = await gatewayTools.governance_batch_execute(firstRequest!, CancellationToken.None);
        replay.IsReplay.Should().BeTrue();
        replay.AuditIds.Should().Equal(batches[0].AuditIds);
        var appliedJobCount = await dbContext.MemoryJobs.CountAsync(x => x.ProjectId == projectId && x.JobType == MemoryJobType.Reindex);
        appliedJobCount.Should().Be(jobsBeforeReplay);

        var reReview = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: governanceRunId, IsReReview: true), CancellationToken.None);
        reReview.GovernancePlan.Count.Should().BeLessThan(initialCandidateCount);
        reReview.ProjectKnowledgeGovernance!.Pagination.TotalCount.Should().Be(0);
        reReview.Pagination.HighSignalConversationInsights.TotalCount.Should().Be(1);
        var catchUp = await gatewayTools.governance_batch_execute(new GovernanceBatchExecuteRequest(
            governanceRunId,
            [projectId],
            reReview.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 10,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.ConversationInsightDisposition],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            IsReReview: true,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled), CancellationToken.None);
        catchUp.RequiresUserDecisionCount.Should().Be(1);
        var finalReview = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: governanceRunId, IsReReview: true), CancellationToken.None);
        finalReview.DurableMemoryCoverage!.SnapshotToken.Should().NotBe(reReview.DurableMemoryCoverage!.SnapshotToken);
        finalReview.Pagination.HighSignalConversationInsights.TotalCount.Should().Be(0);
        var finalReviewReplay = await gatewayTools.knowledge_review(new KnowledgeReviewRequest(
            [projectId], LimitPerSection: 200, GovernanceRunId: governanceRunId, IsReReview: true), CancellationToken.None);
        finalReviewReplay.DurableMemoryCoverage!.SnapshotToken.Should().Be(finalReview.DurableMemoryCoverage.SnapshotToken);
        reReview.Convergence.RequiresUserDecisionCount.Should().BeGreaterThanOrEqualTo(2000);
        var unchangedWorkItem = (await workItems.ListAsync(new ProjectWorkItemListRequest(projectId, IncludeArchived: true), CancellationToken.None))
            .Single(x => x.Id == businessWorkItem.Id);
        unchangedWorkItem.Status.Should().Be(ProjectWorkItemStatus.InProgress);
        unchangedWorkItem.IsArchived.Should().BeFalse();
        (await dbContext.SecurityAuditEvents.CountAsync(x => x.EventType == SecurityAuditEventType.GovernanceBatchItemProcessed))
            .Should().BeGreaterThanOrEqualTo(2001);
    }

    [DockerRequiredFact]
    public async Task Global_Governance_Should_Include_Default_And_Classify_Empty_Project_Metadata_As_NonRetrieval()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var actor = actorAccessor.Current;
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var metadataProjectId = $"hidden-metadata-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var defaultActive = NewCoverageMemory(ProjectContext.DefaultProjectId, "default-active", MemoryStatus.Active);
        var defaultArchived = NewCoverageMemory(ProjectContext.DefaultProjectId, "default-archived", MemoryStatus.Archived);
        var projectInformation = new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = metadataProjectId,
            ExternalKey = DurableMemoryGovernancePolicy.ProjectInformationExternalKey,
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Artifact,
            Title = metadataProjectId,
            Content = string.Empty,
            Summary = string.Empty,
            SourceType = "project-information",
            SourceRef = metadataProjectId,
            Tags = ["project-information", "project-hidden"],
            Importance = 1m,
            Confidence = 1m,
            MetadataJson = JsonSerializer.Serialize(new { isHidden = true }),
            CreatedAt = now,
            UpdatedAt = now
        };
        await dbContext.MemoryItems.AddRangeAsync(defaultActive, defaultArchived, projectInformation);
        await dbContext.SaveChangesAsync();

        var expectedGlobalCount = await dbContext.MemoryItems.AsNoTracking()
            .CountAsync(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId &&
                             x.ProjectId != ProjectContext.UserProjectId);
        var global = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest(GovernanceRunId: $"global-default-{Guid.NewGuid():N}"),
            CancellationToken.None);

        global.Projects.Should().Contain(x => x.ProjectId == ProjectContext.DefaultProjectId);
        global.Projects.Should().Contain(x => x.ProjectId == metadataProjectId);
        global.DurableMemoryCoverage.Should().NotBeNull();
        global.DurableMemoryCoverage!.TotalCount.Should().Be(expectedGlobalCount);
        global.DurableMemoryCoverage.ScannedCount.Should().Be(expectedGlobalCount);
        global.DurableMemoryCoverage.AuthorizedGovernanceDurableMemoryCount.Should().Be(expectedGlobalCount);
        global.DurableMemoryCoverage.GovernanceCoveredDurableMemoryCount.Should().Be(expectedGlobalCount);
        global.DurableMemoryCoverage.CountInvariantSatisfied.Should().BeTrue();
        global.DurableMemoryCoverage.GovernanceProjectIds.Should().Contain(ProjectContext.DefaultProjectId);
        global.DurableMemoryCoverage.SystemMetadataCount.Should().BeGreaterThan(0);
        global.DurableMemoryCoverage.NonRetrievalSystemMetadataCount.Should().BeGreaterThan(0);
        global.DurableMemoryCoverage.ActiveCount.Should().BeGreaterThan(0);
        global.DurableMemoryCoverage.ArchivedCount.Should().BeGreaterThan(0);
        global.ProtectedRetentionCount.Should().BeGreaterThan(0);
        global.ProjectKnowledgeGovernance!.Candidates.Should().NotContain(x =>
            x.MemoryId == projectInformation.Id &&
            (x.Classification == GovernanceFindingType.InvalidMemoryCandidate ||
             x.Classification == GovernanceFindingType.ReindexRequired));
        global.GovernancePlan.Should().NotContain(x =>
            x.AuthorityResourceId == projectInformation.Id && x.Classification == "InvalidProjectMetadata");

        var explicitReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([metadataProjectId], GovernanceRunId: $"explicit-scope-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var expectedExplicitCount = await dbContext.MemoryItems.AsNoTracking()
            .CountAsync(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId &&
                             (x.ProjectId == metadataProjectId || x.ProjectId == ProjectContext.SharedProjectId));
        explicitReview.DurableMemoryCoverage!.TotalCount.Should().Be(expectedExplicitCount);
        explicitReview.DurableMemoryCoverage.GovernanceProjectIds.Should().BeEquivalentTo(
            [metadataProjectId, ProjectContext.SharedProjectId]);
        explicitReview.Projects.Should().ContainSingle(x => x.ProjectId == metadataProjectId);
        explicitReview.Projects.Should().NotContain(x => x.ProjectId == ProjectContext.DefaultProjectId);

        actorAccessor.Current = actor with { AllowedProjectIds = [metadataProjectId] };
        var restrictedGlobal = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest(GovernanceRunId: $"restricted-global-{Guid.NewGuid():N}"),
            CancellationToken.None);
        restrictedGlobal.Projects.Should().ContainSingle(x => x.ProjectId == metadataProjectId);
        restrictedGlobal.Projects.Should().NotContain(x => x.ProjectId == ProjectContext.DefaultProjectId);
        restrictedGlobal.DurableMemoryCoverage!.GovernanceProjectIds.Should().BeEquivalentTo(
            [metadataProjectId, ProjectContext.SharedProjectId]);

        MemoryItem NewCoverageMemory(string projectId, string externalKey, MemoryStatus status) => new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            ExternalKey = $"{externalKey}:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = externalKey,
            Content = $"Coverage fixture {externalKey}.",
            Summary = $"Coverage fixture {externalKey}.",
            SourceType = "test",
            SourceRef = "governance-scope-regression",
            Importance = .9m,
            Confidence = .95m,
            Status = status,
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    [DockerRequiredFact]
    public async Task Full_Governance_Should_Snapshot_More_Than_One_Page_Without_Gaps_Or_Replay_Drift()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"full-governance-{Guid.NewGuid():N}";
        var runId = $"snapshot-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var memories = Enumerable.Range(0, 205).Select(index => new MemoryItem
        {
            TenantId = actorAccessor.Current.TenantId,
            OwnerUserId = actorAccessor.Current.UserId,
            ProjectId = projectId,
            ExternalKey = $"full-governance:{index:D3}:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = $"Unique governed fact {index:D3}",
            Content = $"Durable full coverage fixture {index:D3}.",
            Summary = $"Unique full coverage summary {index:D3}.",
            SourceType = "test",
            SourceRef = "full-governance-fixture",
            Importance = .9m,
            Confidence = .95m,
            Tags = index == 3 ? ["obsolete"] : [],
            Status = index % 5 == 0 ? MemoryStatus.Archived : MemoryStatus.Active,
            MetadataJson = index < 2 ? JsonSerializer.Serialize(new { expectedProjectId = $"target-{index}" }) : "{}",
            CreatedAt = now.AddMinutes(-index),
            UpdatedAt = now.AddMinutes(-index)
        }).ToArray();
        await dbContext.MemoryItems.AddRangeAsync(memories);
        await dbContext.SaveChangesAsync();

        var first = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 1, GovernanceRunId: runId),
            CancellationToken.None);
        first.DurableMemoryCoverage.Should().NotBeNull();
        first.DurableMemoryCoverage!.CoverageComplete.Should().BeTrue();
        first.DurableMemoryCoverage.ScannedCount.Should().Be(first.DurableMemoryCoverage.TotalCount);
        first.DurableMemoryCoverage.TotalCount.Should().BeGreaterThanOrEqualTo(205);
        first.DurableMemoryCoverage.ArchivedCount.Should().BeGreaterThan(0);
        first.ProjectKnowledgeGovernance!.Pagination.HasMore.Should().BeTrue();
        first.ProjectKnowledgeGovernance.Pagination.Continuation.Should().NotBeNullOrWhiteSpace();
        first.PendingSuggestedActions.Count(x =>
            x.ProjectId == projectId && x.Type == SuggestedActionType.ArchiveStaleMemory).Should().Be(1);

        var second = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 1, Offset: 1, GovernanceRunId: runId),
            CancellationToken.None);
        second.DurableMemoryCoverage!.SnapshotId.Should().Be(first.DurableMemoryCoverage.SnapshotId);
        second.DurableMemoryCoverage.TotalCount.Should().Be(first.DurableMemoryCoverage.TotalCount);
        second.ProjectKnowledgeGovernance!.Candidates.Select(x => x.FindingId)
            .Should().NotIntersectWith(first.ProjectKnowledgeGovernance.Candidates.Select(x => x.FindingId));

        var insertedAfterSnapshot = new MemoryItem
        {
            TenantId = actorAccessor.Current.TenantId,
            OwnerUserId = actorAccessor.Current.UserId,
            ProjectId = projectId,
            ExternalKey = $"full-governance:concurrent:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = "Committed after governance snapshot",
            Content = "This row belongs to the next governance snapshot.",
            Summary = "Concurrent data change fixture.",
            SourceType = "test",
            SourceRef = "concurrent-change",
            Importance = .9m,
            Confidence = .95m,
            CreatedAt = now.AddMinutes(1),
            UpdatedAt = now.AddMinutes(1)
        };
        await dbContext.MemoryItems.AddAsync(insertedAfterSnapshot);
        await dbContext.SaveChangesAsync();

        var replay = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 1, GovernanceRunId: runId),
            CancellationToken.None);
        replay.DurableMemoryCoverage!.SnapshotId.Should().Be(first.DurableMemoryCoverage.SnapshotId);
        replay.DurableMemoryCoverage.TotalCount.Should().Be(first.DurableMemoryCoverage.TotalCount);

        var nextRun = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 1, GovernanceRunId: $"{runId}-next"),
            CancellationToken.None);
        nextRun.DurableMemoryCoverage!.TotalCount.Should().Be(first.DurableMemoryCoverage.TotalCount + 1);
    }

    [DockerRequiredFact]
    public async Task Governance_Terminal_Archive_Merge_And_Finding_Disposition_Should_Converge_Idempotently()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var actions = scope.ServiceProvider.GetRequiredService<ISuggestedActionService>();
        var governance = scope.ServiceProvider.GetRequiredService<IGovernanceService>();
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var now = DateTimeOffset.UtcNow;

        var archiveProjectId = $"archive-terminal-{Guid.NewGuid():N}";
        var archiveMemory = new MemoryItem
        {
            TenantId = actorAccessor.Current.TenantId,
            OwnerUserId = actorAccessor.Current.UserId,
            ProjectId = archiveProjectId,
            ExternalKey = $"archive-terminal:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = "Terminal archive fixture",
            Content = "REMOVED",
            Summary = "Low-value archive candidate.",
            SourceType = "test",
            SourceRef = "terminal-governance",
            Importance = .1m,
            Confidence = .2m,
            Version = 1,
            Status = MemoryStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        await dbContext.MemoryItems.AddAsync(archiveMemory);
        await dbContext.SaveChangesAsync();

        var archiveReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([archiveProjectId], GovernanceRunId: $"archive-review-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var archiveAction = archiveReview.PendingSuggestedActions.Single(x =>
            x.ProjectId == archiveProjectId &&
            x.Type == SuggestedActionType.ArchiveStaleMemory &&
            x.PayloadJson.Contains(archiveMemory.Id.ToString(), StringComparison.OrdinalIgnoreCase));
        var historicalConflictAction = new SuggestedAction
        {
            ProjectId = archiveProjectId,
            Type = SuggestedActionType.ReviewConflictCandidate,
            Status = SuggestedActionStatus.Accepted,
            Title = "Historical authority review",
            Summary = "Direct archive must terminalize this stale accepted action.",
            PayloadJson = JsonSerializer.Serialize(new
            {
                dedupKey = $"legacy-authority:{archiveProjectId}:{archiveMemory.Id:D}",
                findingId = $"authoritative-source:{archiveProjectId}:{archiveMemory.Id:D}",
                projectId = archiveProjectId,
                primaryMemoryId = archiveMemory.Id
            }),
            DedupKey = $"legacy-authority:{archiveProjectId}:{archiveMemory.Id:D}",
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-2)
        };
        var legacyMergeAction = new SuggestedAction
        {
            ProjectId = archiveProjectId,
            Type = SuggestedActionType.MergeDuplicateCandidate,
            Status = SuggestedActionStatus.Pending,
            Title = "Legacy merge without secondary target",
            Summary = "Archived primary target makes this legacy action terminal.",
            PayloadJson = JsonSerializer.Serialize(new
            {
                dedupKey = $"legacy-merge:{archiveProjectId}:{archiveMemory.Id:D}",
                projectId = archiveProjectId,
                primaryMemoryId = archiveMemory.Id
            }),
            DedupKey = $"legacy-merge:{archiveProjectId}:{archiveMemory.Id:D}",
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-2)
        };
        await dbContext.SuggestedActions.AddRangeAsync(historicalConflictAction, legacyMergeAction);
        await dbContext.SaveChangesAsync();

        var archived = await memoryService.ArchiveAsync(
            new MemoryArchiveRequest(archiveMemory.Id, archiveProjectId, Archived: true, "direct governance archive"),
            CancellationToken.None);
        var archivedRevisionCount = await dbContext.MemoryItemRevisions.CountAsync(x => x.MemoryItemId == archiveMemory.Id);
        var replay = await memoryService.ArchiveAsync(
            new MemoryArchiveRequest(archiveMemory.Id, archiveProjectId, Archived: true, "direct governance archive"),
            CancellationToken.None);
        var replayWithDifferentReason = await memoryService.ArchiveAsync(
            new MemoryArchiveRequest(archiveMemory.Id, archiveProjectId, Archived: true, "different direct governance reason"),
            CancellationToken.None);
        replay.Version.Should().Be(archived.Version);
        replay.UpdatedAt.Should().Be(archived.UpdatedAt);
        replayWithDifferentReason.Version.Should().Be(archived.Version);
        replayWithDifferentReason.UpdatedAt.Should().Be(archived.UpdatedAt);
        (await dbContext.MemoryItemRevisions.CountAsync(x => x.MemoryItemId == archiveMemory.Id)).Should().Be(archivedRevisionCount);
        dbContext.ClearTrackedChanges();
        (await dbContext.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == archiveMemory.Id)).Version.Should().Be(2);
        (await dbContext.SuggestedActions.AsNoTracking().SingleAsync(x => x.Id == archiveAction.Id)).Status.Should().Be(SuggestedActionStatus.Superseded);
        (await dbContext.SuggestedActions.AsNoTracking().SingleAsync(x => x.Id == historicalConflictAction.Id)).Status.Should().Be(SuggestedActionStatus.Superseded);
        (await dbContext.SuggestedActions.AsNoTracking().SingleAsync(x => x.Id == legacyMergeAction.Id)).Status.Should().Be(SuggestedActionStatus.Superseded);

        var historicalArchiveAction = new SuggestedAction
        {
            ProjectId = archiveProjectId,
            Type = SuggestedActionType.ArchiveStaleMemory,
            Status = SuggestedActionStatus.Pending,
            Title = "Historical duplicate archive",
            Summary = "Historical pending action must converge.",
            PayloadJson = JsonSerializer.Serialize(new
            {
                dedupKey = $"action:ArchiveStaleMemory:archive-memory:{archiveProjectId}:{archiveMemory.Id:D}:",
                findingId = $"archive-memory:{archiveProjectId}:{archiveMemory.Id:D}:",
                projectId = archiveProjectId,
                primaryMemoryId = archiveMemory.Id,
                sourceConnectionId = (Guid?)null
            }),
            DedupKey = $"action:ArchiveStaleMemory:archive-memory:{archiveProjectId}:{archiveMemory.Id:D}:",
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now.AddMinutes(-1)
        };
        await dbContext.SuggestedActions.AddAsync(historicalArchiveAction);
        await dbContext.SaveChangesAsync();

        var persistedBeforeNoOp = await dbContext.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == archiveMemory.Id);
        var noOpCleanup = await memoryService.ArchiveAsync(
            new MemoryArchiveRequest(archiveMemory.Id, archiveProjectId, Archived: true, "reconcile historical action"),
            CancellationToken.None);
        noOpCleanup.Version.Should().Be(persistedBeforeNoOp.Version);
        noOpCleanup.UpdatedAt.Should().Be(persistedBeforeNoOp.UpdatedAt);
        (await dbContext.SuggestedActions.AsNoTracking().SingleAsync(x => x.Id == historicalArchiveAction.Id)).Status
            .Should().Be(SuggestedActionStatus.Superseded);

        var archiveReReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([archiveProjectId], GovernanceRunId: $"archive-rereview-{Guid.NewGuid():N}", IsReReview: true),
            CancellationToken.None);
        archiveReReview.DurableMemoryCoverage!.TotalCount.Should().BeGreaterThanOrEqualTo(1);
        archiveReReview.DurableMemoryCoverage.ArchivedCount.Should().BeGreaterThanOrEqualTo(1);
        archiveReReview.ProjectKnowledgeGovernance!.Candidates.Should().NotContain(x =>
            x.MemoryId == archiveMemory.Id &&
            (x.Classification == GovernanceFindingType.LowValueMemoryCandidate ||
             x.Classification == GovernanceFindingType.ArchiveMemoryCandidate));
        archiveReReview.PendingSuggestedActions.Should().NotContain(x => x.DedupKey == archiveAction.DedupKey);
        (await dbContext.SuggestedActions.AsNoTracking().SingleAsync(x => x.Id == historicalArchiveAction.Id)).Status.Should().Be(SuggestedActionStatus.Superseded);
        (await dbContext.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == archiveMemory.Id)).Version.Should().Be(2);

        var mergeProjectId = $"merge-terminal-{Guid.NewGuid():N}";
        MemoryItem CreateArtifact(string externalKey, decimal importance, decimal confidence, DateTimeOffset updatedAt)
        {
            var item = new MemoryItem
            {
                TenantId = actorAccessor.Current.TenantId,
                OwnerUserId = actorAccessor.Current.UserId,
                ProjectId = mergeProjectId,
                ExternalKey = externalKey,
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                Title = "Canonical duplicate pair",
                Content = "The same authoritative durable content.",
                Summary = "The same authoritative durable summary.",
                SourceType = "test",
                SourceRef = "terminal-governance",
                Importance = importance,
                Confidence = confidence,
                Version = 1,
                Status = MemoryStatus.Active,
                CreatedAt = updatedAt,
                UpdatedAt = updatedAt
            };
            return item;
        }

        var mergeLeft = CreateArtifact($"merge-left:{Guid.NewGuid():N}", .7m, .8m, now.AddMinutes(-2));
        var mergeRight = CreateArtifact($"merge-right:{Guid.NewGuid():N}", .9m, .95m, now.AddMinutes(-1));
        await dbContext.MemoryItems.AddRangeAsync(mergeLeft, mergeRight);
        await dbContext.SaveChangesAsync();

        var mergeReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([mergeProjectId], GovernanceRunId: $"merge-review-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var mergeAction = mergeReview.PendingSuggestedActions.Single(x =>
            x.ProjectId == mergeProjectId && x.Type == SuggestedActionType.MergeDuplicateCandidate);
        var historicalAuthorityAction = new SuggestedAction
        {
            ProjectId = mergeProjectId,
            Type = SuggestedActionType.ReviewConflictCandidate,
            Status = SuggestedActionStatus.Pending,
            Title = "Historical authority pair",
            Summary = "Replacement completion must terminalize cross-type authority review.",
            PayloadJson = JsonSerializer.Serialize(new
            {
                dedupKey = $"legacy-authority:{mergeProjectId}:{mergeRight.Id:D}:{mergeLeft.Id:D}",
                findingId = $"authoritative-source:{mergeProjectId}:{mergeRight.Id:D}:{mergeLeft.Id:D}",
                projectId = mergeProjectId,
                primaryMemoryId = mergeLeft.Id
            }),
            DedupKey = $"legacy-authority:{mergeProjectId}:{mergeRight.Id:D}:{mergeLeft.Id:D}",
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-2)
        };
        await dbContext.SuggestedActions.AddAsync(historicalAuthorityAction);
        await dbContext.SaveChangesAsync();
        (await actions.AcceptAsync(mergeAction.Id, CancellationToken.None)).Action.Status.Should().Be(SuggestedActionStatus.Executed);
        (await actions.AcceptAsync(mergeAction.Id, CancellationToken.None)).Action.Status.Should().Be(SuggestedActionStatus.Executed);
        (await dbContext.SuggestedActions.AsNoTracking().SingleAsync(x => x.Id == historicalAuthorityAction.Id)).Status.Should().Be(SuggestedActionStatus.Superseded);
        (await dbContext.MemoryLinks.AsNoTracking().CountAsync(x =>
            x.LinkType == "replaced_by" &&
            (x.FromId == mergeLeft.Id || x.FromId == mergeRight.Id) &&
            (x.ToId == mergeLeft.Id || x.ToId == mergeRight.Id))).Should().Be(1);

        var historicalMergeAction = new SuggestedAction
        {
            ProjectId = mergeProjectId,
            Type = SuggestedActionType.MergeDuplicateCandidate,
            Status = SuggestedActionStatus.Pending,
            Title = "Historical duplicate merge",
            Summary = "Reversed pair must share the canonical dedup key.",
            PayloadJson = JsonSerializer.Serialize(new
            {
                dedupKey = $"action:MergeDuplicateCandidate:merge-memory:{mergeProjectId}:{mergeRight.Id:D}:{mergeLeft.Id:D}",
                findingId = $"merge-memory:{mergeProjectId}:{mergeRight.Id:D}:{mergeLeft.Id:D}",
                projectId = mergeProjectId,
                primaryMemoryId = mergeLeft.Id,
                sourceConnectionId = (Guid?)null
            }),
            DedupKey = $"action:MergeDuplicateCandidate:merge-memory:{mergeProjectId}:{mergeRight.Id:D}:{mergeLeft.Id:D}",
            CreatedAt = now.AddMinutes(-1),
            UpdatedAt = now.AddMinutes(-1)
        };
        await dbContext.SuggestedActions.AddAsync(historicalMergeAction);
        await dbContext.SaveChangesAsync();

        var mergeReReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([mergeProjectId], GovernanceRunId: $"merge-rereview-{Guid.NewGuid():N}", IsReReview: true),
            CancellationToken.None);
        mergeReReview.ProjectKnowledgeGovernance!.Candidates.Should().NotContain(x =>
            (x.MemoryId == mergeLeft.Id || x.MemoryId == mergeRight.Id) &&
            (x.Classification == GovernanceFindingType.DuplicateMemoryCandidate ||
             x.Classification == GovernanceFindingType.MergeMemoryCandidate ||
             x.Classification == GovernanceFindingType.AuthoritativeSourceCandidate));
        mergeReReview.PendingSuggestedActions.Should().NotContain(x => x.DedupKey == mergeAction.DedupKey);
        (await dbContext.SuggestedActions.AsNoTracking().SingleAsync(x => x.Id == historicalMergeAction.Id)).Status.Should().Be(SuggestedActionStatus.Superseded);
        (await dbContext.MemoryLinks.AsNoTracking().CountAsync(x =>
            x.LinkType == "replaced_by" &&
            (x.FromId == mergeLeft.Id || x.FromId == mergeRight.Id) &&
            (x.ToId == mergeLeft.Id || x.ToId == mergeRight.Id))).Should().Be(1);
        (await dbContext.MemoryItems.AsNoTracking().Where(x => x.Id == mergeLeft.Id || x.Id == mergeRight.Id).Select(x => x.Version).ToArrayAsync()).Should().OnlyContain(x => x == 1);

        var conflictProjectId = $"conflict-disposition-{Guid.NewGuid():N}";
        MemoryItem CreateConflictArtifact(string externalKey, string summary)
        {
            var item = new MemoryItem
            {
                TenantId = actorAccessor.Current.TenantId,
                OwnerUserId = actorAccessor.Current.UserId,
                ProjectId = conflictProjectId,
                ExternalKey = externalKey,
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                Title = "Durable conflict pair",
                Content = summary,
                Summary = summary,
                SourceType = "test",
                SourceRef = "terminal-governance",
                Importance = .9m,
                Confidence = .95m,
                Version = 1,
                Status = MemoryStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            };
            return item;
        }

        var conflictLeft = CreateConflictArtifact($"conflict-left:{Guid.NewGuid():N}", "Alpha-only source statement.");
        var conflictRight = CreateConflictArtifact($"conflict-right:{Guid.NewGuid():N}", "Completely different beta evidence.");
        await dbContext.MemoryItems.AddRangeAsync(conflictLeft, conflictRight);
        await dbContext.SaveChangesAsync();

        var conflictReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([conflictProjectId], GovernanceRunId: $"conflict-review-{Guid.NewGuid():N}"),
            CancellationToken.None);
        var conflict = conflictReview.ProjectKnowledgeGovernance!.Candidates.Single(x => x.Classification == GovernanceFindingType.ConflictCandidate);
        foreach (var other in conflictReview.ProjectKnowledgeGovernance.Candidates.Where(x => x.FindingId != conflict.FindingId))
        {
            await governance.AcceptAsync(other.FindingId, CancellationToken.None);
        }
        var dispositionRunId = $"conflict-disposition-{Guid.NewGuid():N}";
        var disposition = await gatewayTools.governance_finding_set_disposition(
            new GovernanceFindingDispositionRequest(conflict.FindingId, GovernanceFindingDisposition.RequiresUserDecision, "Owner must choose the authoritative durable memory.", dispositionRunId),
            CancellationToken.None);
        disposition.Status.Should().Be(GovernanceFindingStatus.RequiresUserDecision);
        (await gatewayTools.governance_finding_set_disposition(
            new GovernanceFindingDispositionRequest(conflict.FindingId, GovernanceFindingDisposition.RequiresUserDecision, "Owner must choose the authoritative durable memory.", dispositionRunId),
            CancellationToken.None)).GovernanceUpdatedAt.Should().Be(disposition.GovernanceUpdatedAt);

        var conflictReReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([conflictProjectId], GovernanceRunId: $"conflict-rereview-{Guid.NewGuid():N}", IsReReview: true),
            CancellationToken.None);
        conflictReReview.ProjectKnowledgeGovernance!.Candidates.Should().NotContain(x => x.FindingId == conflict.FindingId);
        conflictReReview.Convergence.RequiresUserDecisionCount.Should().BeGreaterThanOrEqualTo(1);
        conflictReReview.Convergence.ActionableItemCount.Should().Be(0);
        conflictReReview.Convergence.Status.Should().Be("ConvergedWithExceptions");
        conflictReReview.Convergence.IsConverged.Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task Knowledge_Review_Should_Refresh_Lifecycle_Across_Multiple_ReReviews_Without_Changing_Snapshot_Membership()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var proposals = scope.ServiceProvider.GetRequiredService<IChatGptProposalService>();
        var projectId = $"multi-round-{Guid.NewGuid():N}";
        var governanceRunId = $"multi-round-run-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        for (var index = 0; index < 4; index++)
        {
            await dbContext.MemoryItems.AddAsync(new MemoryItem
            {
                TenantId = actorAccessor.Current.TenantId,
                OwnerUserId = actorAccessor.Current.UserId,
                ProjectId = projectId,
                ExternalKey = $"multi-round:{index}:{Guid.NewGuid():N}",
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Artifact,
                Title = "Multi-round governance candidate",
                Content = $"Mutually exclusive authority statement {index}.",
                Summary = $"Distinct governance evidence {index}.",
                SourceType = "test",
                SourceRef = "multi-round-governance",
                Importance = .9m,
                Confidence = .95m,
                Version = 1,
                Status = MemoryStatus.Active,
                CreatedAt = now.AddMinutes(index),
                UpdatedAt = now.AddMinutes(index)
            });
        }
        await dbContext.SaveChangesAsync();

        var initial = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: governanceRunId),
            CancellationToken.None);
        initial.ProjectKnowledgeGovernance!.Candidates.Count.Should().BeGreaterThan(2);
        var findingA = initial.ProjectKnowledgeGovernance.Candidates[0];
        var findingB = initial.ProjectKnowledgeGovernance.Candidates[1];
        await gatewayTools.governance_finding_set_disposition(
            new GovernanceFindingDispositionRequest(findingA.FindingId, GovernanceFindingDisposition.Deferred, "Defer first stable member.", governanceRunId),
            CancellationToken.None);

        var pendingProposal = await proposals.CreateAsync(new ChatGptProposalCreateRequest(
            "memory_upsert",
            projectId,
            JsonSerializer.Serialize(new MemoryUpsertRequest(
                $"live-overlay:{Guid.NewGuid():N}",
                MemoryScope.Project,
                MemoryType.Fact,
                "Live proposal overlay",
                "Valid direct target-tool arguments retained as a pending proposal.",
                "Verify terminal proposal state is read live.",
                "test",
                "fixture:live-overlay",
                ["governance"],
                .7m,
                .8m,
                ProjectId: projectId)),
            "Live proposal overlay",
            "Verify terminal proposal state is read live.",
            actorAccessor.Current.Username,
            GovernanceRunId: governanceRunId), CancellationToken.None);
        var firstReReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 1, GovernanceRunId: governanceRunId, IsReReview: true),
            CancellationToken.None);
        firstReReview.ProjectKnowledgeGovernance!.Candidates.Should().NotContain(x => x.FindingId == findingA.FindingId);
        firstReReview.ProjectKnowledgeGovernance.Candidates.Should().Contain(x => x.FindingId == findingB.FindingId);
        firstReReview.ProjectKnowledgeGovernance.Pagination.HasMore.Should().BeTrue();
        var secondStablePage = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 1, Offset: 1, GovernanceRunId: governanceRunId, IsReReview: true),
            CancellationToken.None);
        secondStablePage.DurableMemoryCoverage!.SnapshotId.Should().Be(firstReReview.DurableMemoryCoverage!.SnapshotId);
        secondStablePage.ProjectKnowledgeGovernance!.Candidates.Should().ContainSingle();
        secondStablePage.ProjectKnowledgeGovernance.Candidates[0].FindingId.Should().NotBe(firstReReview.ProjectKnowledgeGovernance.Candidates[0].FindingId);
        var completeStableReReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: governanceRunId, IsReReview: true),
            CancellationToken.None);
        firstReReview.PendingProposals.Should().Contain(x => x.Id == pendingProposal.Id);
        firstReReview.Convergence.DeferredCount.Should().BeGreaterThanOrEqualTo(1);
        var stableCandidateOrder = completeStableReReview.ProjectKnowledgeGovernance!.Candidates.Select(x => x.FindingId).ToArray();

        await gatewayTools.governance_finding_set_disposition(
            new GovernanceFindingDispositionRequest(findingB.FindingId, GovernanceFindingDisposition.RequiresUserDecision, "Owner must decide the second stable member.", governanceRunId),
            CancellationToken.None);
        await proposals.RejectAsync(new ChatGptProposalDecisionRequest(pendingProposal.Id, "Terminalize proposal for live overlay regression."), CancellationToken.None);

        var secondReReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: governanceRunId, IsReReview: true),
            CancellationToken.None);
        secondReReview.DurableMemoryCoverage!.SnapshotId.Should().Be(firstReReview.DurableMemoryCoverage!.SnapshotId);
        secondReReview.DurableMemoryCoverage.SnapshotToken.Should().Be(firstReReview.DurableMemoryCoverage.SnapshotToken);
        secondReReview.DurableMemoryCoverage.TotalCount.Should().Be(firstReReview.DurableMemoryCoverage.TotalCount);
        secondReReview.DurableMemoryCoverage.ScannedCount.Should().Be(firstReReview.DurableMemoryCoverage.ScannedCount);
        secondReReview.ProjectKnowledgeGovernance!.Candidates.Should().NotContain(x => x.FindingId == findingA.FindingId || x.FindingId == findingB.FindingId);
        secondReReview.ProjectKnowledgeGovernance.Candidates.Select(x => x.FindingId)
            .Should().Equal(stableCandidateOrder.Where(id => id != findingB.FindingId));
        secondReReview.Convergence.DeferredCount.Should().BeGreaterThanOrEqualTo(1);
        secondReReview.Convergence.RequiresUserDecisionCount.Should().BeGreaterThanOrEqualTo(1);
        secondReReview.PendingSuggestedActions.Should().NotContain(x => x.DedupKey.Contains(findingB.FindingId.ToString(), StringComparison.OrdinalIgnoreCase));
        secondReReview.PendingProposals.Should().NotContain(x => x.Id == pendingProposal.Id);
    }

    [DockerRequiredFact]
    public async Task Governance_Tracker_Exclusion_Should_Be_Run_Scoped_Audited_And_Fail_Closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"tracker-{Guid.NewGuid():N}";
        var governanceRunId = $"tracker-run-{Guid.NewGuid():N}";
        var ordinary = await gatewayTools.project_work_item_create(
            new ProjectWorkItemCreateRequest(projectId, "Ordinary in-progress work"), CancellationToken.None);
        var tracker = await gatewayTools.project_work_item_create(
            new ProjectWorkItemCreateRequest(projectId, "Explicit acceptance tracker"), CancellationToken.None);
        var pending = await gatewayTools.project_work_item_create(
            new ProjectWorkItemCreateRequest(projectId, "Pending work"), CancellationToken.None);
        var blocked = await gatewayTools.project_work_item_create(
            new ProjectWorkItemCreateRequest(projectId, "Blocked work"), CancellationToken.None);
        var completed = await gatewayTools.project_work_item_create(
            new ProjectWorkItemCreateRequest(projectId, "Completed work"), CancellationToken.None);
        var cancelled = await gatewayTools.project_work_item_create(
            new ProjectWorkItemCreateRequest(projectId, "Cancelled work"), CancellationToken.None);
        ordinary = await gatewayTools.project_work_item_update(
            new ProjectWorkItemUpdateRequest(ordinary.Id, Status: ProjectWorkItemStatus.InProgress), CancellationToken.None);
        tracker = await gatewayTools.project_work_item_update(
            new ProjectWorkItemUpdateRequest(tracker.Id, Status: ProjectWorkItemStatus.InProgress), CancellationToken.None);
        blocked = await gatewayTools.project_work_item_update(
            new ProjectWorkItemUpdateRequest(blocked.Id, Status: ProjectWorkItemStatus.Blocked), CancellationToken.None);
        completed = await gatewayTools.project_work_item_update(
            new ProjectWorkItemUpdateRequest(completed.Id, Status: ProjectWorkItemStatus.Completed), CancellationToken.None);
        cancelled = await gatewayTools.project_work_item_update(
            new ProjectWorkItemUpdateRequest(cancelled.Id, Status: ProjectWorkItemStatus.Cancelled), CancellationToken.None);

        var initial = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], GovernanceRunId: governanceRunId, IsReReview: true), CancellationToken.None);
        initial.WorkItems.Should().Contain(x => x.Id == pending.Id && x.Status == ProjectWorkItemStatus.Pending);
        initial.WorkItems.Should().Contain(x => x.Id == ordinary.Id && x.Status == ProjectWorkItemStatus.InProgress);
        initial.WorkItems.Should().Contain(x => x.Id == blocked.Id && x.Status == ProjectWorkItemStatus.Blocked);
        initial.WorkItems.Should().Contain(x => x.Id == completed.Id && x.Status == ProjectWorkItemStatus.Completed);
        initial.WorkItems.Should().Contain(x => x.Id == cancelled.Id && x.Status == ProjectWorkItemStatus.Cancelled);
        initial.Convergence.WorkItemActionableCount.Should().Be(4);
        initial.Convergence.BusinessWorkItemActionableCount.Should().Be(4);
        initial.Convergence.ActionableItemCount.Should().Be(0);
        initial.Convergence.ExcludedGovernanceTrackerCount.Should().Be(0);

        var exclusionRequest = new ProjectWorkItemGovernanceExclusionRequest(
            tracker.Id, projectId, governanceRunId, "Tracks acceptance of this exact governance run.");
        var excluded = await gatewayTools.project_work_item_set_governance_exclusion(exclusionRequest, CancellationToken.None);
        excluded.GovernanceExclusions.Should().ContainSingle(x => x.GovernanceRunId == governanceRunId && x.IsActive);
        (await gatewayTools.project_work_item_set_governance_exclusion(exclusionRequest, CancellationToken.None)).UpdatedAt.Should().Be(excluded.UpdatedAt);

        var reReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], GovernanceRunId: governanceRunId, IsReReview: true), CancellationToken.None);
        reReview.Convergence.WorkItemActionableCount.Should().Be(3);
        reReview.Convergence.ExcludedGovernanceTrackerCount.Should().Be(1);
        reReview.Convergence.BusinessWorkItemActionableCount.Should().Be(3);
        reReview.Convergence.ActionableItemCount.Should().Be(0);

        var otherRun = $"tracker-other-{Guid.NewGuid():N}";
        var otherReview = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], GovernanceRunId: otherRun, IsReReview: true), CancellationToken.None);
        otherReview.Convergence.WorkItemActionableCount.Should().Be(4);
        otherReview.Convergence.ExcludedGovernanceTrackerCount.Should().Be(0);

        var adminActor = actorAccessor.Current;
        actorAccessor.Current = adminActor with
        {
            Scopes = adminActor.Scopes.Where(x => !string.Equals(x, SecurityScopes.GovernanceTrackerManage, StringComparison.OrdinalIgnoreCase)).ToArray()
        };
        var missingScope = async () => await gatewayTools.project_work_item_set_governance_exclusion(
            new ProjectWorkItemGovernanceExclusionRequest(ordinary.Id, projectId, governanceRunId, "Missing dedicated scope."), CancellationToken.None);
        await missingScope.Should().ThrowAsync<UnauthorizedAccessException>();
        actorAccessor.Current = adminActor with
        {
            Scopes = adminActor.Scopes.Where(x => !string.Equals(x, SecurityScopes.MemoryWrite, StringComparison.OrdinalIgnoreCase)).ToArray()
        };
        var missingWriteScope = async () => await gatewayTools.project_work_item_set_governance_exclusion(
            new ProjectWorkItemGovernanceExclusionRequest(ordinary.Id, projectId, governanceRunId, "Missing memory write scope."), CancellationToken.None);
        await missingWriteScope.Should().ThrowAsync<UnauthorizedAccessException>();
        actorAccessor.Current = adminActor with { Role = TenantUserRole.Member };
        var unauthorized = async () => await gatewayTools.project_work_item_set_governance_exclusion(
            new ProjectWorkItemGovernanceExclusionRequest(ordinary.Id, projectId, governanceRunId, "Unauthorized escape."), CancellationToken.None);
        await unauthorized.Should().ThrowAsync<UnauthorizedAccessException>();
        actorAccessor.Current = adminActor;

        var wrongProject = async () => await gatewayTools.project_work_item_set_governance_exclusion(
            new ProjectWorkItemGovernanceExclusionRequest(ordinary.Id, $"wrong-{projectId}", governanceRunId, "Wrong project."), CancellationToken.None);
        await wrongProject.Should().ThrowAsync<UnauthorizedAccessException>();
        var invalidRun = async () => await gatewayTools.project_work_item_set_governance_exclusion(
            new ProjectWorkItemGovernanceExclusionRequest(ordinary.Id, projectId, $"missing-{Guid.NewGuid():N}", "Missing relationship."), CancellationToken.None);
        await invalidRun.Should().ThrowAsync<InvalidOperationException>();
        (await dbContext.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == SecurityAuditEventType.ProjectWorkItemGovernanceExclusionUpdated)
            .Select(x => x.DetailsJson)
            .ToListAsync()).Count(x => x.Contains(tracker.Id.ToString(), StringComparison.OrdinalIgnoreCase)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Scheduled_Governance_Should_Be_Paged_Idempotent_And_Respect_DisplayName_Boundary()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [] };
        var gatewayTools = ActivatorUtilities.CreateInstance<ChatGptGatewayTools>(scope.ServiceProvider);
        var projectInformation = scope.ServiceProvider.GetRequiredService<IProjectInformationService>();
        var proposals = scope.ServiceProvider.GetRequiredService<IChatGptProposalService>();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"governance-{Guid.NewGuid():N}";
        var governanceRunId = $"run-{Guid.NewGuid():N}";

        var agentCreated = await projectInformation.UpsertAsync(
            new ProjectInformationUpdateRequest(projectId, "Agent must not set this", "Initial description."),
            CancellationToken.None);
        agentCreated.DisplayName.Should().Be(projectId);

        actorAccessor.Current = actorAccessor.Current with { IsInteractiveUser = true };
        var uiUpdated = await projectInformation.UpsertAsync(
            new ProjectInformationUpdateRequest(projectId, "UI managed name", "UI description."),
            CancellationToken.None);
        uiUpdated.DisplayName.Should().Be("UI managed name");
        (await dbContext.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == SecurityAuditEventType.ProjectDisplayNameUpdated)
            .Select(x => x.DetailsJson)
            .ToListAsync()).Count(x => x.Contains(projectId, StringComparison.Ordinal)).Should().Be(1);

        var interactiveAgentUpdated = await projectInformation.UpdateFromAgentAsync(
            new ProjectInformationAgentUpdateRequest(projectId, "Interactive agent description."),
            CancellationToken.None);
        interactiveAgentUpdated.DisplayName.Should().Be("UI managed name");

        actorAccessor.Current = actorAccessor.Current with { IsInteractiveUser = false };
        var agentUpdated = await projectInformation.UpdateFromAgentAsync(
            new ProjectInformationAgentUpdateRequest(projectId, "Agent-updated description."),
            CancellationToken.None);
        agentUpdated.DisplayName.Should().Be("UI managed name");

        var workItems = new List<ProjectWorkItemResult>();
        for (var index = 0; index < 3; index++)
        {
            workItems.Add(await gatewayTools.project_work_item_create(
                new ProjectWorkItemCreateRequest(projectId, $"Governance item {index}", ChecklistItems: [$"Check {index}"]),
                CancellationToken.None));
        }

        var review = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 1, GovernanceRunId: governanceRunId),
            CancellationToken.None);
        review.GovernanceRunId.Should().Be(governanceRunId);
        review.WorkItems.Should().ContainSingle();
        review.Pagination.WorkItems.TotalCount.Should().Be(3);
        review.Pagination.WorkItems.HasMore.Should().BeTrue();
        review.Convergence.IsConverged.Should().BeFalse();

        var secondPage = await gatewayTools.knowledge_review(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 1, Offset: 1, GovernanceRunId: governanceRunId),
            CancellationToken.None);
        secondPage.WorkItems.Should().ContainSingle();
        secondPage.WorkItems[0].Id.Should().NotBe(review.WorkItems[0].Id);

        var tracked = workItems[0];
        tracked = await gatewayTools.project_work_item_checklist_update(tracked.Id, tracked.ChecklistItems[0].Id, true, CancellationToken.None);
        tracked = await gatewayTools.project_work_item_update(new ProjectWorkItemUpdateRequest(tracked.Id, Status: ProjectWorkItemStatus.Completed), CancellationToken.None);
        tracked.Status.Should().Be(ProjectWorkItemStatus.Completed);
        (await gatewayTools.project_work_item_archive(tracked.Id, CancellationToken.None)).IsArchived.Should().BeTrue();
        (await gatewayTools.project_work_items_list(new ProjectWorkItemListRequest(projectId), CancellationToken.None)).Should().NotContain(x => x.Id == tracked.Id);
        (await gatewayTools.project_work_item_restore(tracked.Id, CancellationToken.None)).Status.Should().Be(ProjectWorkItemStatus.Completed);

        var payload = JsonSerializer.Serialize(new MemoryUpsertRequest(
            $"governance:{Guid.NewGuid():N}",
            MemoryScope.Project,
            MemoryType.Fact,
            "Idempotent governance proposal",
            "Governance proposal content.",
            "Governance proposal summary.",
            "chatgpt",
            "chatgpt-governance-test",
            ["governance", "idempotency"],
            0.8m,
            0.9m,
            ProjectId: projectId));
        var proposalRequest = new ChatGptProposalCreateRequest(
            "memory_upsert",
            projectId,
            payload,
            "Idempotent governance proposal",
            "Create exactly one proposal.",
            "governance-test-user",
            GovernanceRunId: governanceRunId);
        var firstProposal = await proposals.CreateAsync(proposalRequest, CancellationToken.None);
        var retriedProposal = await proposals.CreateAsync(proposalRequest, CancellationToken.None);
        retriedProposal.Id.Should().Be(firstProposal.Id);
        (await proposals.ApproveAsync(new ChatGptProposalDecisionRequest(firstProposal.Id), CancellationToken.None)).Status.Should().Be(ChatGptProposalStatus.Applied);
        (await proposals.ApproveAsync(new ChatGptProposalDecisionRequest(firstProposal.Id), CancellationToken.None)).AppliedResourceId.Should().NotBeNull();

        var now = DateTimeOffset.UtcNow;
        var session = new ConversationSession
        {
            TenantId = actorAccessor.Current.TenantId,
            OwnerUserId = actorAccessor.Current.UserId,
            ConversationId = $"governance-insight-{Guid.NewGuid():N}",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "test",
            LastTurnId = "turn-1",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = session.TenantId,
            OwnerUserId = session.OwnerUserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "test",
            SourceRef = "test",
            DedupKey = $"checkpoint-{Guid.NewGuid():N}",
            CreatedAt = now
        };
        var insight = new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = session.TenantId,
            OwnerUserId = session.OwnerUserId,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "test",
            SourceRef = "test",
            InsightType = ConversationInsightType.Fact,
            Title = "Retry insight",
            Content = "Retry insight content.",
            Summary = "Retry insight summary.",
            DedupKey = $"insight-{Guid.NewGuid():N}",
            PromotionStatus = ConversationPromotionStatus.Failed,
            Error = "Transient failure",
            CreatedAt = now,
            UpdatedAt = now
        };
        await dbContext.ConversationInsights.AddAsync(insight);
        await dbContext.SaveChangesAsync();

        (await gatewayTools.conversation_insight_status(insight.Id, CancellationToken.None))!.PromotionStatus.Should().Be(ConversationPromotionStatus.Failed);
        (await gatewayTools.conversation_insight_retry(new ConversationInsightGovernanceRequest(insight.Id, governanceRunId), CancellationToken.None)).PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        var hostBlockedRequest = new ConversationInsightDispositionRequest(insight.Id, ConversationInsightDisposition.HostBlocked, "ChatGPT host safety gate blocked the mutation.", governanceRunId);
        (await gatewayTools.conversation_insight_set_disposition(hostBlockedRequest, CancellationToken.None)).PromotionStatus.Should().Be(ConversationPromotionStatus.HostBlocked);
        (await gatewayTools.conversation_insight_set_disposition(hostBlockedRequest, CancellationToken.None)).PromotionStatus.Should().Be(ConversationPromotionStatus.HostBlocked);
        (await dbContext.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == SecurityAuditEventType.ConversationInsightGovernanceUpdated)
            .Select(x => x.DetailsJson)
            .ToListAsync()).Count(x => x.Contains(insight.Id.ToString(), StringComparison.Ordinal)).Should().Be(2); // retry + one idempotent HostBlocked transition
        await scope.ServiceProvider.GetRequiredService<IConversationAutomationService>()
            .PromotePendingInsightsAsync(insight.ConversationId, projectId, CancellationToken.None);
        (await gatewayTools.conversation_insight_status(insight.Id, CancellationToken.None))!.PromotionStatus.Should().Be(ConversationPromotionStatus.HostBlocked);
        (await gatewayTools.conversation_insight_retry(new ConversationInsightGovernanceRequest(insight.Id, governanceRunId, "Human approved retry."), CancellationToken.None)).PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
    }

    [DockerRequiredFact]
    public async Task Tool_Call_Should_Record_Durable_Gateway_Invocation_Telemetry()
    {
        var startedAt = DateTimeOffset.UtcNow;
        using var client = CreateAuthorizedClient(environment.GetFactory());

        var payload = await SendMcpAsync(client, string.Empty, 201, "tools/call", new
        {
            name = "memory_search",
            arguments = new
            {
                query = "gateway telemetry verification",
                projectId = ProjectId,
                limit = 1
            }
        });

        ExtractSseJson(payload).TryGetProperty("result", out _).Should().BeTrue();
        using var scope = environment.GetFactory().Services.CreateScope();
        var events = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .McpToolCallEvents
            .AsNoTracking()
            .Where(x => x.CreatedAt >= startedAt &&
                        x.ServiceName == "chatgpt-gateway" &&
                        x.ToolName == "memory_search" &&
                        x.ProjectId == ProjectId)
            .ToListAsync();
        events.Should().ContainSingle();
        events.Single().Success.Should().BeTrue();
    }

    [Fact]
    public void App_Catalog_Projection_Should_Accept_Array_Output_Schema_And_Reject_Scalar_Output_Schema()
    {
        using var validCatalog = JsonDocument.Parse("""
            [{
              "name":"governance_runs_list",
              "description":"List governance run receipts.",
              "inputSchema":{"type":"object"},
              "outputSchema":{"type":"array","items":{"type":"object"}},
              "annotations":{"readOnlyHint":true,"destructiveHint":false,"openWorldHint":false,"idempotentHint":true}
            }]
            """);
        using var invalidCatalog = JsonDocument.Parse("""
            [{
              "name":"governance_runs_list",
              "description":"List governance run receipts.",
              "inputSchema":{"type":"object"},
              "outputSchema":{"type":"string"},
              "annotations":{"readOnlyHint":true,"destructiveHint":false,"openWorldHint":false,"idempotentHint":true}
            }]
            """);

        var validTool = ChatGptAppCatalogProjection.Project(validCatalog.RootElement).Tools.Single();
        validTool.IsAppCallable.Should().BeTrue();
        validTool.InvalidReasons.Should().BeEmpty();

        var invalidTool = ChatGptAppCatalogProjection.Project(invalidCatalog.RootElement).Tools.Single();
        invalidTool.IsAppCallable.Should().BeFalse();
        invalidTool.InvalidReasons.Should().ContainSingle("invalid-output-schema");
    }

    [DockerRequiredFact]
    public async Task Proposal_Approval_Should_Bridge_ChatGpt_And_Codex_Read_Paths()
    {
        var externalKey = $"chatgpt-gateway:{Guid.NewGuid():N}";
        var rejectedExternalKey = $"chatgpt-gateway-rejected:{Guid.NewGuid():N}";

        await SeedCodexReadableMemoryAsync("Gateway read fixture", "ChatGPT gateway can read authorized project memory.");
        var insightOnlyProjectId = $"insight-only-{Guid.NewGuid():N}";
        await SeedInsightOnlyProjectAsync(insightOnlyProjectId);

        var captureHandler = new SessionCaptureHandler(environment.GetFactory().Server.CreateHandler());
        using var client = CreateAuthorizedClient(environment.GetFactory(), captureHandler);
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(client.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, client);

        await using var mcpClient = await McpClient.CreateAsync(transport);
        _ = await mcpClient.ListToolsAsync();
        var sessionId = captureHandler.SessionId;
        sessionId.Should().BeNull("MCP 2026-07-28 uses stateless Streamable HTTP");

        var toolsPayload = await SendMcpAsync(client, sessionId!, 2, "tools/list", new { });
        var listedTools = ExtractSseJson(toolsPayload).GetProperty("result").GetProperty("tools");
        var listedToolNames = listedTools.EnumerateArray()
            .Select(tool => tool.GetProperty("name").GetString()!)
            .ToArray();
        var searchTool = listedTools.EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "memory_search");
        searchTool.TryGetProperty("outputSchema", out var searchOutputSchema).Should().BeTrue();
        searchOutputSchema.ValueKind.Should().Be(JsonValueKind.Object);
        listedToolNames.Should().BeEquivalentTo(ChatGptGatewayToolCatalog.PublishedToolNames);
        listedToolNames.Should().HaveCount(65);
        var appFacingProjection = ChatGptAppCatalogProjection.Project(listedTools);
        appFacingProjection.IsValid.Should().BeTrue();
        appFacingProjection.PublishedToolCount.Should().Be(65);
        appFacingProjection.AppCallableToolCount.Should().Be(65);
        appFacingProjection.MissingPublishedTools.Should().BeEmpty();
        appFacingProjection.UnexpectedPublishedTools.Should().BeEmpty();
        appFacingProjection.MissingAppCallableTools.Should().BeEmpty();
        var receiptAndContractTools = new[]
        {
            "governance_contract_get",
            "governance_run_get",
            "governance_runs_list"
        };
        listedToolNames.Except(receiptAndContractTools, StringComparer.Ordinal).Should().HaveCount(62);
        foreach (var toolName in receiptAndContractTools)
        {
            var projectedTool = appFacingProjection.Tools.Single(tool => tool.Name == toolName);
            projectedTool.IsAppCallable.Should().BeTrue();
            projectedTool.IsRequiredReadOnlyTool.Should().BeTrue();
            projectedTool.EffectiveVisibility.Should().BeEquivalentTo("model", "app");
            projectedTool.InvalidReasons.Should().BeEmpty();

            var annotations = listedTools.EnumerateArray()
                .Single(tool => tool.GetProperty("name").GetString() == toolName)
                .GetProperty("annotations");
            annotations.GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
            annotations.GetProperty("destructiveHint").GetBoolean().Should().BeFalse();
            annotations.GetProperty("openWorldHint").GetBoolean().Should().BeFalse();
            annotations.GetProperty("idempotentHint").GetBoolean().Should().BeTrue();
        }
        McpPublishedToolCatalog.QueryToolNames.Should().BeSubsetOf(ChatGptGatewayToolCatalog.PublishedToolNames);
        McpPublishedToolCatalog.ProposalWriteToolNames.Should().BeSubsetOf(ChatGptGatewayToolCatalog.PublishedToolNames);
        McpPublishedToolCatalog.DirectMutationToolNames.Should().BeSubsetOf(ChatGptGatewayToolCatalog.PublishedToolNames);
        McpPublishedToolCatalog.QueryToolNames
            .Concat(McpPublishedToolCatalog.ProposalWriteToolNames)
            .Concat(McpPublishedToolCatalog.DirectMutationToolNames)
            .Should().BeEquivalentTo(ChatGptGatewayToolCatalog.PublishedToolNames);
        ChatGptGatewayToolCatalog.PublishedCatalogHash.Should().MatchRegex("^[a-f0-9]{64}$");
        ChatGptGatewayToolCatalog.PublicationIdentity.Should().Contain(ChatGptGatewayToolCatalog.PublishedCatalogVersion);
        listedToolNames.Should().Contain([
            "knowledge_review",
            "governance_contract_get",
            "governance_batch_execute",
            "governance_run_get",
            "governance_runs_list",
            "governance_tombstone_get",
            "project_work_items_list",
            "project_work_item_create",
            "project_work_item_update",
            "project_work_item_set_governance_exclusion",
            "project_work_item_checklist_update",
            "project_work_item_archive",
            "project_work_item_restore",
            "conversation_insight_status",
            "conversation_insight_retry",
            "conversation_insight_skip",
            "conversation_insight_set_disposition",
            "governance_finding_set_disposition",
            "governance_finding_reopen",
            "chatgpt_governance_proposal_create"
        ]);
        var batchTool = listedTools.EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "governance_batch_execute");
        var reviewTool = listedTools.EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "knowledge_review");
        reviewTool.GetProperty("outputSchema").GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().Contain([
            "governancePlan", "governanceCoverage", "convergence", "quarantinedCount", "deleteEligibleCount",
            "deleteMaturedCount", "autoDeletedCount", "deleteCancelledCount", "tombstonedCount",
            "semanticAutoResolvedCount", "remainingHumanDecisionCount", "protectedRetentionCount",
            "candidateCount", "executionActionableCount", "governedExceptionCount"
        ]);
        var batchRequestSchema = batchTool.GetProperty("inputSchema").GetProperty("properties").GetProperty("request");
        batchRequestSchema.GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().Contain([
            "governanceRunId", "projectIds", "snapshotToken", "cursor", "maxMutations", "maxDurationSeconds",
            "allowedActionTypes", "maxRiskLevel", "dryRun", "allowHardDelete", "allowMaturedDelete",
            "semanticAutoResolutionConfidenceThreshold", "toolContractVersion", "schemaHash", "isReReview", "executionMode"
        ]);
        batchRequestSchema.GetProperty("properties").GetProperty("allowedActionTypes")
            .GetProperty("items").GetProperty("enum").EnumerateArray().Select(x => x.GetString()).Should().Contain([
                nameof(GovernanceBatchActionType.Quarantine),
                nameof(GovernanceBatchActionType.MaturedDelete),
                nameof(GovernanceBatchActionType.SemanticReevaluate)
            ]);
        batchTool.GetProperty("description").GetString().Should().Contain(GovernanceToolContract.SchemaHash);
        PublishedToolSchemaHash.Compute(batchTool).Should().Be(GovernanceToolContract.SchemaHash);
        batchTool.GetProperty("outputSchema").GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().Contain([
            "scannedCount", "attemptedCount", "appliedCount", "noOpCount", "failedCount", "deferredCount",
            "requiresUserDecisionCount", "mergedCount", "updatedCount", "movedCount", "archivedCount",
            "reindexedCount", "deleteProposalCount", "nextCursor", "hasMore", "requiresReReview", "items",
            "auditIds", "snapshotToken", "stoppedReason", "errorCode", "succeeded", "quarantinedCount",
            "deleteEligibleCount", "deleteMaturedCount", "autoDeletedCount", "deleteCancelledCount",
            "tombstonedCount", "semanticAutoResolvedCount", "remainingHumanDecisionCount", "protectedRetentionCount"
        ]);
        var runGetTool = listedTools.EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "governance_run_get");
        runGetTool.GetProperty("outputSchema").GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().Contain([
            "runExists", "status", "latestBatchReceived", "requestIdentityHash", "latestBatch",
            "auditIds", "finalSnapshotToken", "finalConvergenceStatus", "stoppedReason"
        ]);
        runGetTool.GetProperty("outputSchema").GetProperty("properties").GetProperty("latestBatch")
            .GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().Contain([
                "received", "executed", "requestIdentityHash", "requestHash", "status", "snapshotGeneration",
                "nextCursor", "requiresReReview", "stoppedReason", "auditIds"
            ]);
        var exclusionTool = listedTools.EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "project_work_item_set_governance_exclusion");
        var exclusionRequestSchema = exclusionTool.GetProperty("inputSchema")
            .GetProperty("properties")
            .GetProperty("request");
        exclusionRequestSchema.GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().Contain([
            "workItemId", "projectId", "governanceRunId", "reason", "excluded"
        ]);
        exclusionRequestSchema.GetProperty("required").EnumerateArray().Select(x => x.GetString()).Should().Contain([
            "workItemId", "projectId", "governanceRunId", "reason"
        ]);
        exclusionTool.GetProperty("outputSchema").GetProperty("properties")
            .TryGetProperty("governanceExclusions", out _).Should().BeTrue();

        static string[] DeclaredToolNames(Type toolType) => toolType.GetMethods()
            .Where(method => method.GetCustomAttributesData().Any(attribute => attribute.AttributeType.Name == "McpServerToolAttribute"))
            .Select(method => method.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        var gatewayDeclaredTools = DeclaredToolNames(typeof(ChatGptGatewayTools));
        var backendDeclaredTools = DeclaredToolNames(typeof(MemoryMcpTools));
        gatewayDeclaredTools.Should().BeEquivalentTo(ChatGptGatewayToolCatalog.PublishedToolNames);
        backendDeclaredTools.Except(gatewayDeclaredTools, StringComparer.Ordinal)
            .Should().BeEquivalentTo(ChatGptGatewayToolCatalog.BackendOnlyToolNames);
        gatewayDeclaredTools.Except(backendDeclaredTools, StringComparer.Ordinal)
            .Should().BeEquivalentTo(ChatGptGatewayToolCatalog.GatewayOnlyToolNames);
        typeof(ChatGptGatewayTools).GetMethod(nameof(ChatGptGatewayTools.governance_batch_execute))!.GetParameters()[0].ParameterType
            .Should().Be(typeof(GovernanceBatchExecuteRequest));
        typeof(MemoryMcpTools).GetMethod(nameof(MemoryMcpTools.governance_batch_execute))!.GetParameters()[0].ParameterType
            .Should().Be(typeof(GovernanceBatchExecuteRequest));

        var projectsPayload = await SendMcpAsync(client, sessionId!, 21, "tools/call", new
        {
            name = "projects_list",
            arguments = new { limit = 20 }
        });
        var projects = ExtractToolJson(projectsPayload);
        projects.ValueKind.Should().Be(JsonValueKind.Array);
        projects.EnumerateArray().Should().NotContain(project =>
            string.Equals(project.GetProperty("projectId").GetString(), ProjectContext.DefaultProjectId, StringComparison.OrdinalIgnoreCase));
        projects.EnumerateArray().Should().Contain(project =>
            string.Equals(project.GetProperty("projectId").GetString(), ProjectId, StringComparison.OrdinalIgnoreCase));
        projects.EnumerateArray().Should().Contain(project =>
            string.Equals(project.GetProperty("projectId").GetString(), insightOnlyProjectId, StringComparison.OrdinalIgnoreCase));

        var retentionPreviewPayload = await SendMcpAsync(client, sessionId!, 22, "tools/call", new
        {
            name = "memory_retention_preview",
            arguments = new { }
        });
        var retentionPreview = ExtractToolJson(retentionPreviewPayload);
        retentionPreview.GetProperty("mode").GetString().Should().Be("Classify");
        retentionPreview.GetProperty("deletedMemoryItems").GetInt64().Should().Be(0);

        var dailyReviewPayload = await SendMcpAsync(client, sessionId!, 23, "tools/call", new
        {
            name = "daily_memory_review",
            arguments = new { }
        });
        var dailyReview = ExtractToolJson(dailyReviewPayload);
        dailyReview.GetProperty("projects").EnumerateArray().Should().Contain(project =>
            string.Equals(project.GetProperty("projectId").GetString(), ProjectId, StringComparison.OrdinalIgnoreCase));
        dailyReview.GetProperty("retention").GetProperty("mode").GetString().Should().Be("Classify");
        dailyReview.GetProperty("retention").GetProperty("deletedMemoryItems").GetInt64().Should().Be(0);
        dailyReview.GetProperty("highSignalConversationInsights").EnumerateArray().Should().OnlyContain(insight =>
            projects.EnumerateArray().Any(project => string.Equals(
                project.GetProperty("projectId").GetString(),
                insight.GetProperty("projectId").GetString(),
                StringComparison.OrdinalIgnoreCase)));

        var readPayload = await SendMcpAsync(client, sessionId!, 3, "tools/call", new
        {
            name = "memory_search",
            arguments = new
            {
                query = "authorized project memory",
                projectId = ProjectId,
                limit = 5
            }
        });
        ExtractToolText(readPayload).Should().Contain("Gateway read fixture");
        var readResult = ExtractSseJson(readPayload).GetProperty("result");
        readResult.TryGetProperty("structuredContent", out var readStructuredContent).Should().BeTrue();
        readStructuredContent.GetRawText().Should().Contain("Gateway read fixture");

        var crossProjectPayload = await SendMcpAsync(client, sessionId!, 4, "tools/call", new
        {
            name = "memory_search",
            arguments = new
            {
                query = "authorized project memory",
                projectId = "UnauthorizedProject",
                limit = 5
            }
        });
        ExtractToolText(crossProjectPayload).Should().NotContain("An error occurred invoking 'memory_search'.");

        var proposalPayload = await SendMcpAsync(client, sessionId!, 5, "tools/call", new
        {
            name = "memory_upsert",
            arguments = new
            {
                request = new
                {
                    externalKey,
                    scope = "Project",
                    memoryType = "Fact",
                    title = "Approved ChatGPT gateway proposal",
                    content = "Approved ChatGPT proposal content should be visible to Codex and ChatGPT readers.",
                    summary = "Approved ChatGPT proposal summary",
                    sourceType = "chatgpt",
                    sourceRef = "chatgpt-mcp-gateway-tests",
                    tags = new[] { "chatgpt", "gateway" },
                    importance = 0.8m,
                    confidence = 0.9m,
                    projectId = ProjectId
                }
            }
        });
        var proposal = ExtractToolJson(proposalPayload);
        proposal.GetProperty("status").GetString().Should().Be("Pending");
        var proposalId = proposal.GetProperty("id").GetGuid();

        await DurableMemoryShouldNotExistAsync(externalKey);

        var listPayload = await SendMcpAsync(client, sessionId!, 6, "tools/call", new
        {
            name = "chatgpt_proposals_list",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    status = "Pending",
                    limit = 10
                }
            }
        });
        ExtractToolText(listPayload).Should().Contain(proposalId.ToString("D"));

        var approvePayload = await SendMcpAsync(client, sessionId!, 7, "tools/call", new
        {
            name = "chatgpt_proposal_approve",
            arguments = new
            {
                request = new
                {
                    proposalId,
                    note = "Approved by gateway integration test."
                }
            }
        });
        var approved = ExtractToolJson(approvePayload);
        approved.GetProperty("status").GetString().Should().Be("Applied");
        approved.GetProperty("appliedResourceId").ValueKind.Should().Be(JsonValueKind.String);

        await DurableMemoryShouldExistAsync(externalKey);
        await CodexWorkingContextShouldContainAsync("Approved ChatGPT proposal", "Approved ChatGPT gateway proposal");

        var gatewayReadAfterApproval = await SendMcpAsync(client, sessionId!, 7, "tools/call", new
        {
            name = "memory_search",
            arguments = new
            {
                query = "Approved ChatGPT proposal",
                projectId = ProjectId,
                limit = 5
            }
        });
        ExtractToolText(gatewayReadAfterApproval).Should().Contain("Approved ChatGPT gateway proposal");

        var failingProposalPayload = await SendMcpAsync(client, sessionId!, 80, "tools/call", new
        {
            name = "memory_update",
            arguments = new
            {
                request = new
                {
                    id = Guid.NewGuid(),
                    title = "Missing memory update should fail on approval",
                    projectId = ProjectId
                }
            }
        });
        var failingProposal = ExtractToolJson(failingProposalPayload);
        failingProposal.GetProperty("status").GetString().Should().Be("Pending");

        var failedApprovalPayload = await SendMcpAsync(client, sessionId!, 81, "tools/call", new
        {
            name = "chatgpt_proposal_approve",
            arguments = new
            {
                request = new
                {
                    proposalId = failingProposal.GetProperty("id").GetGuid(),
                    note = "Approval should preserve apply failure details."
                }
            }
        });
        var failedApproval = ExtractToolJson(failedApprovalPayload);
        failedApproval.GetProperty("status").GetString().Should().Be("Failed");
        failedApproval.GetProperty("error").GetString().Should().Contain("was not found");

        var artifactProposalPayload = await SendMcpAsync(client, sessionId!, 8, "tools/call", new
        {
            name = "project_artifact_publish",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    title = "ChatGPT artifact exchange proposal",
                    summary = "Gateway artifact proposal should become shared same-project knowledge after approval.",
                    content = "Artifact snippet shared from ChatGPT simulation for Codex interop.",
                    kind = "Snippet",
                    sourceSystem = "chatgpt-mcp-gateway",
                    sourceRef = "chatgpt-mcp-gateway-tests/artifacts",
                    tags = new[] { "chatgpt", "artifact-exchange" }
                }
            }
        });
        var artifactProposal = ExtractToolJson(artifactProposalPayload);
        artifactProposal.GetProperty("status").GetString().Should().Be("Pending");
        var artifactProposalId = artifactProposal.GetProperty("id").GetGuid();

        await ProjectArtifactShouldNotExistAsync("ChatGPT artifact exchange proposal");

        var approveArtifactPayload = await SendMcpAsync(client, sessionId!, 9, "tools/call", new
        {
            name = "chatgpt_proposal_approve",
            arguments = new
            {
                request = new
                {
                    proposalId = artifactProposalId,
                    note = "Approved artifact exchange proposal by gateway integration test."
                }
            }
        });
        var approvedArtifact = ExtractToolJson(approveArtifactPayload);
        approvedArtifact.GetProperty("status").GetString().Should().Be("Applied");

        var codexArtifact = await CodexProjectArtifactSearchShouldContainAsync("Artifact snippet shared from ChatGPT", "ChatGPT artifact exchange proposal");

        var gatewayArtifactSearch = await SendMcpAsync(client, sessionId!, 10, "tools/call", new
        {
            name = "project_artifacts_search",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    query = "Artifact snippet shared from ChatGPT",
                    limit = 5
                }
            }
        });
        ExtractToolText(gatewayArtifactSearch).Should().Contain("ChatGPT artifact exchange proposal");

        var gatewayArtifactGet = await SendMcpAsync(client, sessionId!, 11, "tools/call", new
        {
            name = "project_artifact_get",
            arguments = new
            {
                memoryId = codexArtifact.MemoryId
            }
        });
        ExtractToolText(gatewayArtifactGet).Should().Contain("Artifact snippet shared from ChatGPT simulation");

        var codexExternalArtifact = await PublishCodexExternalArtifactAsync(
            "Codex R2 pointer artifact",
            "Codex published an R2 object pointer for ChatGPT readers.",
            "wjcy-context-artifacts",
            $"chatgpt-gateway-tests/{Guid.NewGuid():N}.md",
            DateTimeOffset.UtcNow.AddHours(1));

        var gatewayCodexArtifactList = await SendMcpAsync(client, sessionId!, 12, "tools/call", new
        {
            name = "project_artifacts_list",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    query = "Codex R2 pointer",
                    kind = "ExternalObject",
                    sourceSystem = "codex",
                    includeExpired = false,
                    limit = 5
                }
            }
        });
        var codexArtifactListText = ExtractToolText(gatewayCodexArtifactList);
        codexArtifactListText.Should().Contain("Codex R2 pointer artifact");
        codexArtifactListText.Should().Contain("wjcy-context-artifacts");

        var gatewayCodexArtifactGet = await SendMcpAsync(client, sessionId!, 13, "tools/call", new
        {
            name = "project_artifact_get",
            arguments = new
            {
                memoryId = codexExternalArtifact.MemoryId
            }
        });
        var codexArtifactGetText = ExtractToolText(gatewayCodexArtifactGet);
        codexArtifactGetText.Should().Contain("Codex R2 pointer artifact");
        codexArtifactGetText.Should().Contain("ExternalObject");
        codexArtifactGetText.Should().Contain("expiresAt");

        var expiredCodexArtifact = await PublishCodexExternalArtifactAsync(
            "Expired Codex R2 pointer artifact",
            "Expired Codex object pointers should be hidden by default.",
            "fake-bucket",
            $"chatgpt-gateway-tests/expired-{Guid.NewGuid():N}.md",
            DateTimeOffset.UtcNow.AddMinutes(-10));

        var nonExpiredOnlyPayload = await SendMcpAsync(client, sessionId!, 14, "tools/call", new
        {
            name = "project_artifacts_list",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    query = "Expired Codex R2 pointer",
                    includeExpired = false,
                    limit = 5
                }
            }
        });
        ExtractToolText(nonExpiredOnlyPayload).Should().NotContain("Expired Codex R2 pointer artifact");

        var includeExpiredPayload = await SendMcpAsync(client, sessionId!, 15, "tools/call", new
        {
            name = "project_artifacts_list",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    query = "Expired Codex R2 pointer",
                    includeExpired = true,
                    limit = 5
                }
            }
        });
        var includeExpiredText = ExtractToolText(includeExpiredPayload);
        includeExpiredText.Should().Contain("Expired Codex R2 pointer artifact");
        includeExpiredText.Should().Contain("isExpired");

        var dryRunPrune = await PruneExpiredArtifactsAsync(dryRun: true);
        dryRunPrune.ScannedCount.Should().BeGreaterThanOrEqualTo(1);
        dryRunPrune.Items.Should().Contain(x => x.MemoryId == expiredCodexArtifact.MemoryId && x.Key == expiredCodexArtifact.ObjectRef!.Key);
        FakeProjectArtifactObjectStore.Deletes.Should().BeEmpty();

        var actualPrune = await PruneExpiredArtifactsAsync(dryRun: false);
        actualPrune.DeletedObjectCount.Should().BeGreaterThanOrEqualTo(1);
        actualPrune.ArchivedArtifactCount.Should().BeGreaterThanOrEqualTo(1);
        actualPrune.Items.Should().Contain(x => x.MemoryId == expiredCodexArtifact.MemoryId && x.ArchivedArtifact);
        FakeProjectArtifactObjectStore.Deletes.Should().Contain(expiredCodexArtifact.ObjectRef!);
        await ProjectArtifactShouldBeArchivedAsync(expiredCodexArtifact.MemoryId);

        var managedUploadProposalPayload = await SendMcpAsync(client, sessionId!, 16, "tools/call", new
        {
            name = "project_artifact_upload_object",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    title = "ChatGPT managed R2 upload proposal",
                    summary = "Managed upload should write object storage only after approval.",
                    contentBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("Managed artifact content should live in object storage.")),
                    fileName = "managed-artifact.md",
                    contentType = "text/markdown",
                    expiresAt = DateTimeOffset.UtcNow.AddHours(2),
                    sourceSystem = "chatgpt-mcp-gateway",
                    sourceRef = $"chatgpt-managed-upload:{Guid.NewGuid():N}",
                    tags = new[] { "chatgpt", "managed-upload" }
                }
            }
        });
        var managedUploadProposal = ExtractToolJson(managedUploadProposalPayload);
        managedUploadProposal.GetProperty("status").GetString().Should().Be("Pending");
        var managedUploadProposalId = managedUploadProposal.GetProperty("id").GetGuid();
        FakeProjectArtifactObjectStore.Uploads.Should().BeEmpty();
        await ProjectArtifactShouldNotExistAsync("ChatGPT managed R2 upload proposal");

        var approveManagedUploadPayload = await SendMcpAsync(client, sessionId!, 17, "tools/call", new
        {
            name = "chatgpt_proposal_approve",
            arguments = new
            {
                request = new
                {
                    proposalId = managedUploadProposalId,
                    note = "Approved managed object upload by gateway integration test."
                }
            }
        });
        ExtractToolJson(approveManagedUploadPayload).GetProperty("status").GetString().Should().Be("Applied");
        FakeProjectArtifactObjectStore.Uploads.Should().ContainSingle(x => x.FileName == "managed-artifact.md");

        var managedUploadListPayload = await SendMcpAsync(client, sessionId!, 18, "tools/call", new
        {
            name = "project_artifacts_list",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    query = "ChatGPT managed R2 upload",
                    includeExpired = false,
                    limit = 5
                }
            }
        });
        var managedUploadText = ExtractToolText(managedUploadListPayload);
        managedUploadText.Should().Contain("ChatGPT managed R2 upload proposal");
        managedUploadText.Should().Contain("fake-r2");
        managedUploadText.Should().NotContain("Managed artifact content should live in object storage.");

        var rejectProposalPayload = await SendMcpAsync(client, sessionId!, 19, "tools/call", new
        {
            name = "memory_upsert",
            arguments = new
            {
                request = new
                {
                    externalKey = rejectedExternalKey,
                    scope = "Project",
                    memoryType = "Fact",
                    title = "Rejected ChatGPT gateway proposal",
                    content = "Rejected proposal content must not be durable memory.",
                    summary = "Rejected proposal summary",
                    sourceType = "chatgpt",
                    sourceRef = "chatgpt-mcp-gateway-tests",
                    tags = new[] { "chatgpt", "gateway" },
                    importance = 0.8m,
                    confidence = 0.9m,
                    projectId = ProjectId
                }
            }
        });
        var rejectedProposalId = ExtractToolJson(rejectProposalPayload).GetProperty("id").GetGuid();

        var rejectPayload = await SendMcpAsync(client, sessionId!, 20, "tools/call", new
        {
            name = "chatgpt_proposal_reject",
            arguments = new
            {
                request = new
                {
                    proposalId = rejectedProposalId,
                    note = "Rejected by gateway integration test."
                }
            }
        });
        ExtractToolJson(rejectPayload).GetProperty("status").GetString().Should().Be("Rejected");
        await DurableMemoryShouldNotExistAsync(rejectedExternalKey);

        var rejectedArtifactProposalPayload = await SendMcpAsync(client, sessionId!, 21, "tools/call", new
        {
            name = "project_artifact_publish",
            arguments = new
            {
                request = new
                {
                    projectId = ProjectId,
                    title = "Rejected ChatGPT artifact exchange proposal",
                    summary = "Rejected artifact summary",
                    content = "Rejected artifact content must not be shared.",
                    kind = "Snippet",
                    sourceSystem = "chatgpt-mcp-gateway",
                    sourceRef = "chatgpt-mcp-gateway-tests/rejected-artifacts"
                }
            }
        });
        var rejectedArtifactProposalId = ExtractToolJson(rejectedArtifactProposalPayload).GetProperty("id").GetGuid();

        var rejectArtifactPayload = await SendMcpAsync(client, sessionId!, 22, "tools/call", new
        {
            name = "chatgpt_proposal_reject",
            arguments = new
            {
                request = new
                {
                    proposalId = rejectedArtifactProposalId,
                    note = "Rejected artifact exchange proposal by gateway integration test."
                }
            }
        });
        ExtractToolJson(rejectArtifactPayload).GetProperty("status").GetString().Should().Be("Rejected");
        await ProjectArtifactShouldNotExistAsync("Rejected ChatGPT artifact exchange proposal");
    }

    private async Task SeedCodexReadableMemoryAsync(string title, string content)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        await memoryService.UpsertAsync(
            new MemoryUpsertRequest(
                ExternalKey: $"chatgpt-gateway-read:{Guid.NewGuid():N}",
                Scope: MemoryScope.Project,
                MemoryType: MemoryType.Fact,
                Title: title,
                Content: content,
                Summary: content,
                SourceType: "test",
                SourceRef: "chatgpt-gateway-tests",
                Tags: ["chatgpt", "gateway"],
                Importance: 0.8m,
                Confidence: 0.9m,
                ProjectId: ProjectId),
            CancellationToken.None);
    }

    private async Task SeedInsightOnlyProjectAsync(string projectId)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await dbContext.TenantUsers.SingleAsync(x => x.Username == "gateway-test-admin");
        var now = DateTimeOffset.UtcNow;
        var session = new ConversationSession
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ConversationId = $"insight-only-{Guid.NewGuid():N}",
            ProjectId = projectId,
            ProjectName = projectId,
            TaskId = "scope-regression",
            SourceSystem = "test",
            LastTurnId = "turn-1",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            TaskId = session.TaskId,
            SourceSystem = session.SourceSystem,
            EventType = ConversationEventType.SessionCheckpoint,
            SourceKind = ConversationSourceKind.AgentSupplemental,
            SourceRef = "scope-regression",
            DedupKey = $"scope-regression:{Guid.NewGuid():N}",
            CreatedAt = now
        };
        dbContext.ConversationInsights.Add(new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ConversationId = session.ConversationId,
            TurnId = checkpoint.TurnId,
            ProjectId = projectId,
            ProjectName = projectId,
            TaskId = session.TaskId,
            SourceSystem = session.SourceSystem,
            SourceKind = ConversationSourceKind.AgentSupplemental,
            InsightType = ConversationInsightType.Fact,
            Title = "Insight-only project scope regression",
            Content = "The project must be listed before daily review can return its insight.",
            Summary = "Insight-only project belongs to the authoritative project list.",
            SourceRef = checkpoint.SourceRef,
            Tags = ["scope-regression"],
            Importance = 0.9m,
            Confidence = 0.9m,
            DedupKey = $"scope-regression:{Guid.NewGuid():N}",
            CreatedAt = now,
            UpdatedAt = now
        });
        await dbContext.SaveChangesAsync();
    }

    private async Task DurableMemoryShouldExistAsync(string externalKey)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var exists = await dbContext.MemoryItems.AnyAsync(x => x.ExternalKey == externalKey, CancellationToken.None);
        exists.Should().BeTrue();
    }

    private async Task DurableMemoryShouldNotExistAsync(string externalKey)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var exists = await dbContext.MemoryItems.AnyAsync(x => x.ExternalKey == externalKey, CancellationToken.None);
        exists.Should().BeFalse();
    }

    private async Task CodexWorkingContextShouldContainAsync(string query, string expected)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
        var context = await memoryService.BuildWorkingContextAsync(
            new WorkingContextRequest(
                query,
                Limit: 5,
                RecentLogLimit: 0,
                ProjectId: ProjectId),
            CancellationToken.None);

        JsonSerializer.Serialize(context).Should().Contain(expected);
    }

    private async Task<ProjectArtifactResult> CodexProjectArtifactSearchShouldContainAsync(string query, string expectedTitle)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var artifactExchange = scope.ServiceProvider.GetRequiredService<IProjectArtifactExchangeService>();
        var results = await artifactExchange.SearchAsync(
            new ProjectArtifactSearchRequest(ProjectId, query, Limit: 5),
            CancellationToken.None);

        var artifact = results.Should().ContainSingle(x => x.Title == expectedTitle).Subject;
        artifact.ProjectId.Should().Be(ProjectId);
        artifact.Kind.Should().Be(ProjectArtifactKind.Snippet);
        artifact.SourceSystem.Should().Be("chatgpt-mcp-gateway");
        return artifact;
    }

    private async Task<ProjectArtifactResult> PublishCodexExternalArtifactAsync(
        string title,
        string summary,
        string bucket,
        string key,
        DateTimeOffset expiresAt)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var artifactExchange = scope.ServiceProvider.GetRequiredService<IProjectArtifactExchangeService>();
        var artifact = await artifactExchange.PublishAsync(
            new ProjectArtifactPublishRequest(
                ProjectId,
                title,
                summary,
                Content: string.Empty,
                Kind: ProjectArtifactKind.ExternalObject,
                SourceSystem: "codex",
                SourceRef: $"codex-r2-pointer:{Guid.NewGuid():N}",
                Tags: ["codex", "r2-pointer"],
                ObjectRef: new ProjectArtifactObjectRef(
                    Provider: "r2",
                    Bucket: bucket,
                    Key: key,
                    ExpiresAt: expiresAt,
                    ContentType: "text/markdown"),
                ExpiresAt: expiresAt),
            CancellationToken.None);

        artifact.Kind.Should().Be(ProjectArtifactKind.ExternalObject);
        artifact.ObjectRef.Should().NotBeNull();
        artifact.ObjectRef!.Provider.Should().Be("r2");
        artifact.ExpiresAt.Should().BeCloseTo(expiresAt, TimeSpan.FromSeconds(1));
        return artifact;
    }

    private async Task ProjectArtifactShouldNotExistAsync(string title)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var exists = await dbContext.MemoryItems.AnyAsync(
            x => x.ProjectId == ProjectId &&
                 x.Title == title &&
                 x.MemoryType == MemoryType.Artifact &&
                 x.SourceType == ProjectArtifactExchangeService.SourceType,
            CancellationToken.None);
        exists.Should().BeFalse();
    }

    private async Task<ProjectArtifactExpiredObjectPruneResult> PruneExpiredArtifactsAsync(bool dryRun)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseGatewayActor(scope.ServiceProvider);
        var artifactExchange = scope.ServiceProvider.GetRequiredService<IProjectArtifactExchangeService>();
        return await artifactExchange.PruneExpiredObjectsAsync(
            new ProjectArtifactExpiredObjectPruneRequest(ProjectId, Limit: 20, DryRun: dryRun),
            CancellationToken.None);
    }

    private async Task ProjectArtifactShouldBeArchivedAsync(Guid memoryId)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var artifact = await dbContext.MemoryItems.SingleAsync(x => x.Id == memoryId, CancellationToken.None);
        artifact.Status.Should().Be(MemoryStatus.Archived);
        artifact.Tags.Should().Contain("artifact-object-pruned");
    }

    private static async Task ConfigureSelfHostedUserAsync(ChatGptGatewayApplicationFactory factory)
    {
        using var setupScope = factory.Services.CreateScope();
        var dbContext = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var passwordHasher = setupScope.ServiceProvider.GetRequiredService<IPasswordHasher<object>>();
        var user = await dbContext.TenantUsers.SingleAsync(x => x.Username == "gateway-test-admin");
        user.Email = "oauth-user@example.test";
        user.DisplayName = "OAuth Test User";
        user.PasswordHash = passwordHasher.HashPassword(new object(), "oauth-password");
        await dbContext.SaveChangesAsync();
    }

    private static async Task<string> CompleteSelfHostedOAuthCodeFlowAsync(
        HttpClient client,
        string clientId,
        string redirectUri,
        string resource)
    {
        var verifier = Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64UrlEncode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var authorizePath = BuildAuthorizePath(clientId, redirectUri, challenge, resource);

        using var authorizePageResponse = await client.GetAsync(authorizePath);
        authorizePageResponse.EnsureSuccessStatusCode();

        using var authorizeResponse = await client.PostAsync(
            authorizePath,
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["username"] = "gateway-test-admin",
                ["password"] = "oauth-password"
            }));
        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.Found);
        var code = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(authorizeResponse.Headers.Location!.Query)["code"].ToString();
        code.Should().NotBeNullOrWhiteSpace();

        using var tokenResponse = await client.PostAsync(
            "/oauth/chat/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["client_id"] = clientId,
                ["code"] = code,
                ["redirect_uri"] = redirectUri,
                ["code_verifier"] = verifier,
                ["resource"] = resource
            }));
        tokenResponse.EnsureSuccessStatusCode();
        var tokenJson = await tokenResponse.Content.ReadFromJsonAsync<JsonElement>();
        return tokenJson.GetProperty("access_token").GetString()!;
    }

    private static string BuildAuthorizePath(
        string clientId,
        string redirectUri,
        string codeChallenge,
        string resource)
        => "/oauth/chat/authorize?" + string.Join('&', new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = clientId,
            ["redirect_uri"] = redirectUri,
            ["scope"] = "openid profile email offline_access",
            ["state"] = "state-123",
            ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256",
            ["resource"] = resource
        }.Select(x => $"{Uri.EscapeDataString(x.Key)}={Uri.EscapeDataString(x.Value)}"));

    private static HttpClient CreateAuthorizedClient(ChatGptGatewayApplicationFactory factory, HttpMessageHandler? handler = null)
    {
        var client = handler is null
            ? factory.CreateClient()
            : new HttpClient(handler) { BaseAddress = factory.Server.BaseAddress };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        return client;
    }

    private static HttpRequestMessage CreateModernMcpRequest(string method)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id = Guid.NewGuid().ToString("N"),
                method,
                @params = new
                {
                    _meta = new Dictionary<string, object>
                    {
                        ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                        ["io.modelcontextprotocol/clientInfo"] = new { name = "gateway-security-test", version = "1.0" },
                        ["io.modelcontextprotocol/clientCapabilities"] = new { }
                    }
                }
            })
        };
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", method);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    private static async Task<string> SendMcpAsync(HttpClient client, string? sessionId, int id, string method, object @params)
    {
        var parameters = JsonSerializer.SerializeToNode(@params) as JsonObject ?? new JsonObject();
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            parameters["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new JsonObject
                {
                    ["name"] = "ContextHub.ChatGptGatewayTests",
                    ["version"] = "1.0"
                },
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
            };
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = method,
                ["params"] = parameters
            })
        };
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
            request.Headers.Add("Mcp-Method", method);
            if (parameters["name"]?.GetValue<string>() is { Length: > 0 } name)
            {
                request.Headers.Add("Mcp-Name", name);
            }
        }
        else
        {
            request.Headers.Add("Mcp-Session-Id", sessionId);
            request.Headers.Add("MCP-Protocol-Version", "2025-03-26");
        }
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static HttpRequestMessage CreateRawProtocolRequest(string protocolVersion, string json, string? staleSessionId = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("MCP-Protocol-Version", protocolVersion);
        if (string.Equals(protocolVersion, "2026-07-28", StringComparison.Ordinal))
        {
            request.Headers.Add("Mcp-Method", "tools/list");
        }
        if (!string.IsNullOrWhiteSpace(staleSessionId))
        {
            request.Headers.Add("Mcp-Session-Id", staleSessionId);
        }

        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        return request;
    }

    private static void UseGatewayActor(IServiceProvider services)
    {
        var dbContext = services.GetRequiredService<MemoryDbContext>();
        var user = dbContext.TenantUsers
            .Include(x => x.Tenant)
            .Single(x => x.Username == "gateway-test-admin");

        services.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite,
                SecurityScopes.LogsRead,
                SecurityScopes.GovernanceTrackerManage
            ],
            [],
            IsAuthenticated: true);
    }

    private static JsonElement ExtractToolJson(string payload)
    {
        using var document = JsonDocument.Parse(ExtractToolText(payload));
        return document.RootElement.Clone();
    }

    private static string ExtractToolText(string payload)
    {
        return ExtractSseJson(payload)
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            ?? string.Empty;
    }

    private static JsonElement ExtractSseJson(string payload)
    {
        if (payload.TrimStart().StartsWith('{'))
        {
            using var jsonDocument = JsonDocument.Parse(payload);
            return jsonDocument.RootElement.Clone();
        }

        var dataLine = payload
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Expected SSE data line.");

        using var document = JsonDocument.Parse(dataLine["data: ".Length..]);
        return document.RootElement.Clone();
    }

    private static string Base64UrlEncode(byte[] bytes)
        => Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private sealed class SessionCaptureHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        public string? SessionId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(SessionId) &&
                (response.Headers.TryGetValues("Mcp-Session-Id", out var values) ||
                 response.Headers.TryGetValues("mcp-session-id", out values)))
            {
                SessionId = values.SingleOrDefault();
            }

            return response;
        }
    }
}

public sealed class ChatGptGatewayTestEnvironment : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private RedisContainer? _redis;

    public ChatGptGatewayApplicationFactory? Factory { get; private set; }
    public string PostgresConnectionString => _postgres?.GetConnectionString() ?? throw new InvalidOperationException(DockerTestGate.Current.Reason);
    public string RedisConnectionString => _redis?.GetConnectionString() ?? throw new InvalidOperationException(DockerTestGate.Current.Reason);

    public async Task InitializeAsync()
    {
        if (!DockerTestGate.Current.IsAvailable)
        {
            return;
        }

        _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithPortBinding(5432, true)
            .WithDatabase("contexthub")
            .WithUsername("contexthub")
            .WithPassword("contexthub")
            .Build();

        _redis = new RedisBuilder("redis:7.4-alpine")
            .WithPortBinding(6379, true)
            .Build();

        await _postgres.StartAsync();
        await _redis.StartAsync();

        FakeProjectArtifactObjectStore.Reset();
        Factory = new ChatGptGatewayApplicationFactory(_postgres.GetConnectionString(), _redis.GetConnectionString());
        await WaitForReadinessAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    public ChatGptGatewayApplicationFactory GetFactory()
        => Factory ?? throw new InvalidOperationException(DockerTestGate.Current.Reason);

    private async Task WaitForReadinessAsync()
    {
        using var client = GetFactory().CreateClient();
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(30))
        {
            try
            {
                using var response = await client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Timed out waiting for the ChatGPT gateway test server readiness endpoint.");
    }
}

public sealed class ChatGptGatewayApplicationFactory(
    string postgresConnectionString,
    string redisConnectionString,
    bool selfHostedOAuth = false,
    IChatGptOAuthClientMetadataFetcher? clientMetadataFetcher = null,
    bool includeIssuerInAuthorizationResponse = false,
    string? selfHostedRsaPrivateKey = null) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", postgresConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", redisConnectionString);
        builder.UseSetting("Embeddings:Provider", "Deterministic");
        builder.UseSetting("Embeddings:Profile", "compact");
        builder.UseSetting("Embeddings:ModelKey", "deterministic-384");
        builder.UseSetting("Embeddings:Dimensions", "384");
        builder.UseSetting("Embeddings:MaxTokens", "512");
        builder.UseSetting("Memory:Namespace", "chatgpt-gateway-tests");
        builder.UseSetting("DatabaseLogging:MinimumLevel", "Error");
        builder.UseSetting("ContextHub:Security:RequireAuthentication", "true");
        builder.UseSetting("ContextHub:Security:BootstrapToken", "gateway-test-bootstrap-token");
        builder.UseSetting("ContextHub:Security:BootstrapTenantSlug", "chatgpt-gateway-tests");
        builder.UseSetting("ContextHub:Security:BootstrapUsername", "gateway-test-admin");
        builder.UseSetting("ContextHub:Security:BootstrapAllowedProjectIds", ProjectContext.AllProjectIdsSentinel);
        builder.UseSetting("ChatGptGateway:OAuth:TestMode", selfHostedOAuth ? "false" : "true");
        builder.UseSetting("ChatGptGateway:OAuth:Authority", selfHostedOAuth ? string.Empty : ChatGptGatewayMcpTests.TestAuthority);
        builder.UseSetting("ChatGptGateway:OAuth:SelfHosted", selfHostedOAuth ? "true" : "false");
        builder.UseSetting("ChatGptGateway:OAuth:SelfHostedIssuer", ChatGptGatewayMcpTests.SelfHostedIssuer);
        builder.UseSetting("ChatGptGateway:OAuth:SelfHostedSigningKey", ChatGptGatewayMcpTests.SelfHostedSigningKey);
        builder.UseSetting("ChatGptGateway:OAuth:SelfHostedRsaPrivateKey", selfHostedRsaPrivateKey ?? string.Empty);
        builder.UseSetting("ChatGptGateway:OAuth:ClientId", selfHostedOAuth ? ChatGptGatewayMcpTests.SelfHostedClientId : "chatgpt-gateway-test-client");
        builder.UseSetting("ChatGptGateway:OAuth:IncludeIssuerInAuthorizationResponse", includeIssuerInAuthorizationResponse ? "true" : "false");
        builder.UseSetting("ChatGptGateway:OAuth:AllowedRedirectUriPrefixes:0", "https://chatgpt.com/");
        builder.UseSetting("ChatGptGateway:OAuth:AllowedRedirectUriPrefixes:1", "https://chat.openai.com/");
        builder.UseSetting("ChatGptGateway:OAuth:TestBearerToken", ChatGptGatewayTestConstants.TestToken);
        builder.UseSetting("ChatGptGateway:OAuth:TestSubject", "chatgpt-gateway-test-subject");
        builder.UseSetting("ChatGptGateway:OAuth:TestEmail", "chatgpt-gateway@example.test");
        builder.UseSetting("ChatGptGateway:OAuth:TestName", "ChatGPT Gateway Test User");
        builder.UseSetting("ChatGptGateway:PublicMcpUrl", ChatGptGatewayMcpTests.PublicMcpUrl);
        builder.UseSetting("ChatGptGateway:PublicResourceMetadataUrl", ChatGptGatewayMcpTests.PublicResourceMetadataUrl);
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgresConnectionString,
                ["ConnectionStrings:Redis"] = redisConnectionString,
                ["Embeddings:Provider"] = "Deterministic",
                ["Embeddings:Profile"] = "compact",
                ["Embeddings:ModelKey"] = "deterministic-384",
                ["Embeddings:Dimensions"] = "384",
                ["Embeddings:MaxTokens"] = "512",
                ["Memory:Namespace"] = "chatgpt-gateway-tests",
                ["DatabaseLogging:MinimumLevel"] = "Error",
                ["ContextHub:Security:RequireAuthentication"] = "true",
                ["ContextHub:Security:BootstrapToken"] = "gateway-test-bootstrap-token",
                ["ContextHub:Security:BootstrapTenantSlug"] = "chatgpt-gateway-tests",
                ["ContextHub:Security:BootstrapUsername"] = "gateway-test-admin",
                ["ContextHub:Security:BootstrapAllowedProjectIds"] = ProjectContext.AllProjectIdsSentinel,
                ["ChatGptGateway:OAuth:TestMode"] = selfHostedOAuth ? "false" : "true",
                ["ChatGptGateway:OAuth:Authority"] = selfHostedOAuth ? string.Empty : ChatGptGatewayMcpTests.TestAuthority,
                ["ChatGptGateway:OAuth:SelfHosted"] = selfHostedOAuth ? "true" : "false",
                ["ChatGptGateway:OAuth:SelfHostedIssuer"] = ChatGptGatewayMcpTests.SelfHostedIssuer,
                ["ChatGptGateway:OAuth:SelfHostedSigningKey"] = ChatGptGatewayMcpTests.SelfHostedSigningKey,
                ["ChatGptGateway:OAuth:SelfHostedRsaPrivateKey"] = selfHostedRsaPrivateKey ?? string.Empty,
                ["ChatGptGateway:OAuth:ClientId"] = selfHostedOAuth ? ChatGptGatewayMcpTests.SelfHostedClientId : "chatgpt-gateway-test-client",
                ["ChatGptGateway:OAuth:IncludeIssuerInAuthorizationResponse"] = includeIssuerInAuthorizationResponse ? "true" : "false",
                ["ChatGptGateway:OAuth:AllowedRedirectUriPrefixes:0"] = "https://chatgpt.com/",
                ["ChatGptGateway:OAuth:AllowedRedirectUriPrefixes:1"] = "https://chat.openai.com/",
                ["ChatGptGateway:OAuth:TestBearerToken"] = ChatGptGatewayTestConstants.TestToken,
                ["ChatGptGateway:OAuth:TestSubject"] = "chatgpt-gateway-test-subject",
                ["ChatGptGateway:OAuth:TestEmail"] = "chatgpt-gateway@example.test",
                ["ChatGptGateway:OAuth:TestName"] = "ChatGPT Gateway Test User",
                ["ChatGptGateway:PublicMcpUrl"] = ChatGptGatewayMcpTests.PublicMcpUrl,
                ["ChatGptGateway:PublicResourceMetadataUrl"] = ChatGptGatewayMcpTests.PublicResourceMetadataUrl
            });
        });
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IProjectArtifactObjectStore, FakeProjectArtifactObjectStore>();
            if (clientMetadataFetcher is not null)
            {
                services.AddSingleton(clientMetadataFetcher);
            }
        });
    }
}

public sealed class FakeClientMetadataFetcher(ChatGptOAuthClientMetadata? metadata) : IChatGptOAuthClientMetadataFetcher
{
    public Task<ChatGptOAuthClientMetadata?> FetchAsync(string clientId, CancellationToken cancellationToken)
        => Task.FromResult(metadata);
}

internal static class ChatGptGatewayTestConstants
{
    public const string ProjectId = "ContextHubChatGptGatewayTest";
    public const string TestToken = "test-chatgpt-gateway-token";
}

internal sealed class FakeProjectArtifactObjectStore : IProjectArtifactObjectStore
{
    private static readonly List<ProjectArtifactObjectUploadRequest> UploadLog = [];
    private static readonly List<ProjectArtifactObjectRef> DeleteLog = [];

    public static IReadOnlyList<ProjectArtifactObjectUploadRequest> Uploads => UploadLog.ToArray();
    public static IReadOnlyList<ProjectArtifactObjectRef> Deletes => DeleteLog.ToArray();

    public static void Reset()
    {
        UploadLog.Clear();
        DeleteLog.Clear();
    }

    public Task<ProjectArtifactObjectRef> UploadAsync(ProjectArtifactObjectUploadRequest request, CancellationToken cancellationToken)
    {
        UploadLog.Add(request);
        return Task.FromResult(new ProjectArtifactObjectRef(
            "fake-r2",
            "fake-bucket",
            $"managed/{request.ProjectId}/{Guid.NewGuid():N}/{request.FileName}",
            $"https://r2.example.invalid/managed/{Uri.EscapeDataString(request.FileName)}",
            request.ExpiresAt,
            "FAKE-SHA256",
            request.Content.LongLength,
            request.ContentType));
    }

    public Task DeleteAsync(ProjectArtifactObjectRef objectRef, CancellationToken cancellationToken)
    {
        DeleteLog.Add(objectRef);
        return Task.CompletedTask;
    }
}

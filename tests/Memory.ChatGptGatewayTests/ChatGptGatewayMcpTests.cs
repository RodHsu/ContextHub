using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.ChatGptGateway;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
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
        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.SeeOther);
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
        initializeResponse.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues).Should().BeTrue();
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

        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.SeeOther);
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
        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.SeeOther);
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
            authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.SeeOther);
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
                "none",
                ["authorization_code", "refresh_token"],
                ["code"],
                "openid profile email offline_access")));
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
        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.SeeOther);
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
            "memory_upsert",
            "memory_update",
            "user_preference_upsert",
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
    public async Task Proposal_Approval_Should_Bridge_ChatGpt_And_Codex_Read_Paths()
    {
        var externalKey = $"chatgpt-gateway:{Guid.NewGuid():N}";
        var rejectedExternalKey = $"chatgpt-gateway-rejected:{Guid.NewGuid():N}";

        await SeedCodexReadableMemoryAsync("Gateway read fixture", "ChatGPT gateway can read authorized project memory.");

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
        sessionId.Should().NotBeNullOrWhiteSpace();

        var toolsPayload = await SendMcpAsync(client, sessionId!, 2, "tools/list", new { });
        var listedTools = ExtractSseJson(toolsPayload).GetProperty("result").GetProperty("tools");
        var searchTool = listedTools.EnumerateArray()
            .Single(tool => tool.GetProperty("name").GetString() == "memory_search");
        searchTool.TryGetProperty("outputSchema", out var searchOutputSchema).Should().BeTrue();
        searchOutputSchema.ValueKind.Should().Be(JsonValueKind.Object);

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

        var unauthorizedProjectPayload = await SendMcpAsync(client, sessionId!, 4, "tools/call", new
        {
            name = "memory_search",
            arguments = new
            {
                query = "authorized project memory",
                projectId = "UnauthorizedProject",
                limit = 5
            }
        });
        ExtractToolText(unauthorizedProjectPayload).Should().Contain("An error occurred invoking 'memory_search'.");

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
        authorizeResponse.StatusCode.Should().Be(System.Net.HttpStatusCode.SeeOther);
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

    private static async Task<string> SendMcpAsync(HttpClient client, string sessionId, int id, string method, object @params)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params
            })
        };
        request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Headers.Add("MCP-Protocol-Version", "2025-03-26");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
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
                SecurityScopes.LogsRead
            ],
            [ProjectId],
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
        builder.UseSetting("ChatGptGateway:AllowedProjectIds:0", ChatGptGatewayTestConstants.ProjectId);
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
                ["ChatGptGateway:PublicResourceMetadataUrl"] = ChatGptGatewayMcpTests.PublicResourceMetadataUrl,
                ["ChatGptGateway:AllowedProjectIds:0"] = ChatGptGatewayTestConstants.ProjectId
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

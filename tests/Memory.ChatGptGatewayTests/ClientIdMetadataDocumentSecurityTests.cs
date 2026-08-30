using System.Net;
using System.Text;
using FluentAssertions;
using Memory.ChatGptGateway;
using Microsoft.Extensions.Options;

namespace Memory.ChatGptGatewayTests;

public sealed class ClientIdMetadataDocumentSecurityTests
{
    private const string ClientId = "https://client.example.test/oauth/client.json";

    [Theory]
    [InlineData("https://chatgpt.com/oauth/client.json", true)]
    [InlineData("https://client.example.test/path/client.json", true)]
    [InlineData("http://client.example.test/path/client.json", false)]
    [InlineData("https://client.example.test/", false)]
    [InlineData("https://user@client.example.test/client.json", false)]
    [InlineData("https://client.example.test/client.json#fragment", false)]
    [InlineData("https://client.example.test/client.json?version=1", false)]
    [InlineData("https://client.example.test/a/../client.json", false)]
    public void Client_Id_Metadata_Document_Url_Should_Follow_Stable_Https_Policy(string value, bool expected)
    {
        ClientIdMetadataDocumentSecurity.TryValidateDocumentUrl(value, out _).Should().Be(expected);
    }

    [Theory]
    [InlineData("127.0.0.1", true)]
    [InlineData("10.0.0.1", true)]
    [InlineData("172.16.0.1", true)]
    [InlineData("192.168.0.1", true)]
    [InlineData("169.254.169.254", true)]
    [InlineData("100.64.0.1", true)]
    [InlineData("192.0.2.1", true)]
    [InlineData("192.0.0.9", true)]
    [InlineData("192.88.99.1", true)]
    [InlineData("8.8.8.8", false)]
    [InlineData("::1", true)]
    [InlineData("fe80::1", true)]
    [InlineData("fc00::1", true)]
    [InlineData("2001:db8::1", true)]
    [InlineData("::192.0.2.1", true)]
    [InlineData("100::1", true)]
    [InlineData("2606:4700:4700::1111", false)]
    public void Cimd_Address_Policy_Should_Block_Non_Public_And_Reserved_Targets(string value, bool blocked)
    {
        ClientIdMetadataDocumentSecurity.IsNonPublicAddress(IPAddress.Parse(value)).Should().Be(blocked);
    }

    [Fact]
    public void Cimd_Address_Policy_Should_Reject_Mixed_Public_And_Private_Dns_Answers()
    {
        var action = () => ClientIdMetadataDocumentSecurity.ValidateResolvedAddresses(
            [IPAddress.Parse("8.8.8.8"), IPAddress.Parse("127.0.0.1")]);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public async Task Cimd_Fetcher_Should_Accept_Exact_Json_Document()
    {
        var fetcher = CreateFetcher((_, _) => JsonResponse($$"""
            {
              "client_id": "{{ClientId}}",
              "client_name": "Test client",
              "redirect_uris": ["https://chatgpt.com/connector_platform_oauth_redirect"],
              "grant_types": ["authorization_code"],
              "response_types": ["code"],
              "token_endpoint_auth_method": "none"
            }
            """));

        var metadata = await fetcher.FetchAsync(ClientId, CancellationToken.None);

        metadata!.ClientId.Should().Be(ClientId);
        metadata.ClientName.Should().Be("Test client");
    }

    [Fact]
    public async Task Cimd_Fetcher_Should_Accept_ChatGpt_Style_Extension_Fields()
    {
        var fetcher = CreateFetcher((_, _) => JsonResponse($$"""
            {
              "client_id": "{{ClientId}}",
              "client_uri": "https://chatgpt.com/",
              "redirect_uris": ["https://chatgpt.com/connector_platform_oauth_redirect"],
              "token_endpoint_auth_method": "private_key_jwt",
              "token_endpoint_auth_methods_supported": ["none", "private_key_jwt"],
              "grant_types": ["authorization_code", "refresh_token"],
              "response_types": ["code"],
              "client_name": "ChatGPT",
              "logo_uri": "https://persistent.oaistatic.com/logo.png",
              "token_endpoint_auth_signing_alg": "RS256",
              "jwks_uri": "https://chatgpt.com/oauth/jwks.json"
            }
            """));

        var metadata = await fetcher.FetchAsync(ClientId, CancellationToken.None);

        metadata!.AdditionalMetadata.Should().ContainKeys(
            "client_uri",
            "logo_uri",
            "token_endpoint_auth_signing_alg",
            "jwks_uri");
    }

    [Theory]
    [InlineData("text/html", "{}")]
    [InlineData("application/json", "{")]
    [InlineData("application/json", "{\"client_id\":\"https://client.example.test/oauth/client.json\",\"client_id\":\"https://evil.example/client.json\",\"client_name\":\"duplicate\",\"redirect_uris\":[\"https://chatgpt.com/callback\"]}")]
    [InlineData("application/json", "{\"client_id\":\"https://other.example/client.json\",\"client_name\":\"mismatch\",\"redirect_uris\":[\"https://chatgpt.com/callback\"]}")]
    public async Task Cimd_Fetcher_Should_Reject_Wrong_Content_And_Ambiguous_Documents(string contentType, string payload)
    {
        var fetcher = CreateFetcher((_, _) => Response(HttpStatusCode.OK, contentType, payload));

        var action = () => fetcher.FetchAsync(ClientId, CancellationToken.None);

        await action.Should().ThrowAsync<Exception>();
    }

    [Fact]
    public async Task Cimd_Fetcher_Should_Reject_Oversized_Document()
    {
        var options = CreateOptions();
        options.OAuth.ClientIdMetadataMaxResponseBytes = 1024;
        var fetcher = CreateFetcher(
            (_, _) => Response(HttpStatusCode.OK, "application/json", new string('x', 1025)),
            options);

        var action = () => fetcher.FetchAsync(ClientId, CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cimd_Fetcher_Should_Bound_Redirects_And_Reject_Unsafe_Targets()
    {
        var options = CreateOptions();
        options.OAuth.ClientIdMetadataMaxRedirects = 1;
        var loop = CreateFetcher((_, _) => Redirect(ClientId), options);
        var unsafeRedirect = CreateFetcher((_, _) => Redirect("https://127.0.0.1/client.json"));

        Func<Task> loopAction = () => loop.FetchAsync(ClientId, CancellationToken.None);
        Func<Task> unsafeRedirectAction = () => unsafeRedirect.FetchAsync(ClientId, CancellationToken.None);
        await loopAction.Should().ThrowAsync<InvalidOperationException>();
        await unsafeRedirectAction.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Pinned_Cimd_Client_Factory_Should_Reject_Dns_Rebinding_Style_Answer()
    {
        var factory = new PinnedClientIdMetadataHttpClientFactory(new StaticAddressResolver(
            IPAddress.Parse("8.8.8.8"),
            IPAddress.Parse("169.254.169.254")));

        var action = () => factory.CreateAsync(new Uri(ClientId), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Cimd_Fetcher_Should_Enforce_Overall_Timeout()
    {
        var options = CreateOptions();
        options.OAuth.ClientIdMetadataFetchTimeoutSeconds = 1;
        var fetcher = new HttpChatGptOAuthClientMetadataFetcher(
            Options.Create(options),
            new TimeoutHttpClientFactory());

        var action = () => fetcher.FetchAsync(ClientId, CancellationToken.None);

        await action.Should().ThrowAsync<OperationCanceledException>();
    }

    private static HttpChatGptOAuthClientMetadataFetcher CreateFetcher(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response,
        ChatGptGatewayOptions? options = null)
        => new(Options.Create(options ?? CreateOptions()), new StubHttpClientFactory(response));

    private static ChatGptGatewayOptions CreateOptions()
        => new()
        {
            OAuth = new OAuthOptions
            {
                ClientIdMetadataFetchTimeoutSeconds = 2,
                ClientIdMetadataMaxRedirects = 2,
                ClientIdMetadataMaxResponseBytes = 65_536
            }
        };

    private static HttpResponseMessage JsonResponse(string payload)
        => Response(HttpStatusCode.OK, "application/json", payload);

    private static HttpResponseMessage Response(HttpStatusCode statusCode, string contentType, string payload)
        => new(statusCode)
        {
            Content = new StringContent(payload, Encoding.UTF8, contentType)
        };

    private static HttpResponseMessage Redirect(string location)
    {
        var response = new HttpResponseMessage(HttpStatusCode.Found);
        response.Headers.Location = new Uri(location);
        return response;
    }

    private sealed class StubHttpClientFactory(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : IClientIdMetadataHttpClientFactory
    {
        public Task<HttpClient> CreateAsync(Uri requestUri, CancellationToken cancellationToken)
            => Task.FromResult(new HttpClient(new StubHandler(response)));
    }

    private sealed class StubHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> response)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(response(request, cancellationToken));
    }

    private sealed class StaticAddressResolver(params IPAddress[] addresses) : IClientIdMetadataAddressResolver
    {
        public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
            => Task.FromResult(addresses);
    }

    private sealed class TimeoutHttpClientFactory : IClientIdMetadataHttpClientFactory
    {
        public Task<HttpClient> CreateAsync(Uri requestUri, CancellationToken cancellationToken)
            => Task.FromResult(new HttpClient(new TimeoutHandler()));
    }

    private sealed class TimeoutHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }
}

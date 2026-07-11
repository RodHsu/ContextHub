using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Memory.ChatGptGateway;

public interface IChatGptOAuthClientMetadataFetcher
{
    Task<ChatGptOAuthClientMetadata?> FetchAsync(string clientId, CancellationToken cancellationToken);
}

public sealed class HttpChatGptOAuthClientMetadataFetcher(IHttpClientFactory httpClientFactory)
    : IChatGptOAuthClientMetadataFetcher
{
    public async Task<ChatGptOAuthClientMetadata?> FetchAsync(string clientId, CancellationToken cancellationToken)
    {
        using var client = httpClientFactory.CreateClient(nameof(HttpChatGptOAuthClientMetadataFetcher));
        client.Timeout = TimeSpan.FromSeconds(10);
        return await client.GetFromJsonAsync<ChatGptOAuthClientMetadata>(clientId, cancellationToken);
    }
}

public sealed record ChatGptOAuthClientMetadata(
    [property: JsonPropertyName("redirect_uris")] IReadOnlyList<string>? RedirectUris,
    [property: JsonPropertyName("token_endpoint_auth_method")] string? TokenEndpointAuthMethod,
    [property: JsonPropertyName("grant_types")] IReadOnlyList<string>? GrantTypes,
    [property: JsonPropertyName("response_types")] IReadOnlyList<string>? ResponseTypes,
    [property: JsonPropertyName("scope")] string? Scope,
    [property: JsonPropertyName("token_endpoint_auth_methods_supported")]
    IReadOnlyList<string>? TokenEndpointAuthMethodsSupported = null);

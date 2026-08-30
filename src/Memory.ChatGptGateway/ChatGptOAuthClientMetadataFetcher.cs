using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace Memory.ChatGptGateway;

public interface IChatGptOAuthClientMetadataFetcher
{
    Task<ChatGptOAuthClientMetadata?> FetchAsync(string clientId, CancellationToken cancellationToken);
}

public interface IClientIdMetadataAddressResolver
{
    Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken);
}

public interface IClientIdMetadataHttpClientFactory
{
    Task<HttpClient> CreateAsync(Uri requestUri, CancellationToken cancellationToken);
}

public sealed class DnsClientIdMetadataAddressResolver : IClientIdMetadataAddressResolver
{
    public Task<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken)
        => Dns.GetHostAddressesAsync(host, cancellationToken);
}

public sealed class PinnedClientIdMetadataHttpClientFactory(IClientIdMetadataAddressResolver addressResolver)
    : IClientIdMetadataHttpClientFactory
{
    public async Task<HttpClient> CreateAsync(Uri requestUri, CancellationToken cancellationToken)
    {
        var addresses = await addressResolver.ResolveAsync(requestUri.Host, cancellationToken);
        ClientIdMetadataDocumentSecurity.ValidateResolvedAddresses(addresses);
        return new HttpClient(CreatePinnedHandler(requestUri, addresses)) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private static SocketsHttpHandler CreatePinnedHandler(Uri requestUri, IReadOnlyList<IPAddress> addresses)
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            ConnectCallback = async (context, cancellationToken) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, requestUri.Host, StringComparison.OrdinalIgnoreCase))
                {
                    throw new HttpRequestException("CIMD connection host changed after validation.");
                }

                Exception? lastError = null;
                foreach (var address in addresses)
                {
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        socket.Dispose();
                        lastError = ex;
                        if (ex is OperationCanceledException)
                        {
                            throw;
                        }
                    }
                }

                throw new HttpRequestException("Unable to connect to the validated CIMD endpoint.", lastError);
            }
        };
    }
}

public sealed class HttpChatGptOAuthClientMetadataFetcher(
    IOptions<ChatGptGatewayOptions> gatewayOptions,
    IClientIdMetadataHttpClientFactory httpClientFactory)
    : IChatGptOAuthClientMetadataFetcher
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false
    };

    public async Task<ChatGptOAuthClientMetadata?> FetchAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        var options = gatewayOptions.Value.OAuth;
        if (!ClientIdMetadataDocumentSecurity.TryValidateDocumentUrl(clientId, out var currentUri))
        {
            throw new InvalidOperationException("Invalid CIMD client_id URL.");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.ClientIdMetadataFetchTimeoutSeconds, 1, 30)));

        var maxRedirects = Math.Clamp(options.ClientIdMetadataMaxRedirects, 0, 5);
        for (var redirectCount = 0; ; redirectCount++)
        {
            using var client = await httpClientFactory.CreateAsync(currentUri, timeout.Token);
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token);

            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount >= maxRedirects || response.Headers.Location is null)
                {
                    throw new InvalidOperationException("CIMD redirect limit exceeded or Location is missing.");
                }

                var redirected = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                if (!ClientIdMetadataDocumentSecurity.TryValidateDocumentUrl(redirected.AbsoluteUri, out currentUri))
                {
                    throw new InvalidOperationException("CIMD redirect target is invalid.");
                }

                continue;
            }

            response.EnsureSuccessStatusCode();
            ValidateContentType(response.Content.Headers.ContentType);
            var maxBytes = Math.Clamp(options.ClientIdMetadataMaxResponseBytes, 1024, 262_144);
            if (response.Content.Headers.ContentLength is > 0 && response.Content.Headers.ContentLength > maxBytes)
            {
                throw new InvalidOperationException("CIMD response is too large.");
            }

            var bytes = await ReadBoundedAsync(response.Content, maxBytes, timeout.Token);
            return DeserializeAndValidate(bytes, clientId);
        }
    }

    private static bool IsRedirect(HttpStatusCode statusCode)
        => statusCode is HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static void ValidateContentType(MediaTypeHeaderValue? contentType)
    {
        var mediaType = contentType?.MediaType;
        if (string.IsNullOrWhiteSpace(mediaType) ||
            (!string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase) &&
             !mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException("CIMD response must use a JSON content type.");
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        HttpContent content,
        int maxBytes,
        CancellationToken cancellationToken)
    {
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream(Math.Min(maxBytes, 16 * 1024));
        var chunk = new byte[8192];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, cancellationToken);
            if (read == 0)
            {
                return buffer.ToArray();
            }

            if (buffer.Length + read > maxBytes)
            {
                throw new InvalidOperationException("CIMD response is too large.");
            }

            buffer.Write(chunk, 0, read);
        }
    }

    private static ChatGptOAuthClientMetadata DeserializeAndValidate(byte[] bytes, string clientId)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions
        {
            AllowTrailingCommas = false,
            CommentHandling = JsonCommentHandling.Disallow,
            MaxDepth = 16
        });
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("CIMD root must be a JSON object.");
        }

        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (!names.Add(property.Name))
            {
                throw new InvalidOperationException("CIMD contains duplicate properties.");
            }
        }

        var metadata = JsonSerializer.Deserialize<ChatGptOAuthClientMetadata>(bytes, JsonOptions)
            ?? throw new InvalidOperationException("CIMD is empty.");
        if (!string.Equals(metadata.ClientId, clientId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(metadata.ClientName) ||
            metadata.RedirectUris is not { Count: > 0 } ||
            metadata.RedirectUris.Any(string.IsNullOrWhiteSpace))
        {
            throw new InvalidOperationException("CIMD required fields are missing or client_id does not match exactly.");
        }

        return metadata;
    }
}

public static class ClientIdMetadataDocumentSecurity
{
    public static bool TryValidateDocumentUrl(string clientId, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(clientId) ||
            !Uri.TryCreate(clientId, UriKind.Absolute, out var parsed) ||
            !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(parsed.Host) ||
            !string.IsNullOrEmpty(parsed.UserInfo) ||
            !string.IsNullOrEmpty(parsed.Fragment) ||
            !string.IsNullOrEmpty(parsed.Query) ||
            parsed.AbsolutePath is "" or "/" ||
            !string.Equals(parsed.AbsoluteUri, clientId, StringComparison.Ordinal))
        {
            return false;
        }

        if (IPAddress.TryParse(parsed.Host, out var literalAddress) && IsNonPublicAddress(literalAddress))
        {
            return false;
        }

        var pathSegments = parsed.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (pathSegments.Any(segment => Uri.UnescapeDataString(segment) is "." or ".."))
        {
            return false;
        }

        uri = parsed;
        return true;
    }

    public static void ValidateResolvedAddresses(IReadOnlyList<IPAddress> addresses)
    {
        if (addresses.Count == 0 || addresses.Any(IsNonPublicAddress))
        {
            throw new InvalidOperationException("CIMD host resolved to a non-public or reserved address.");
        }
    }

    public static bool IsNonPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
        {
            return true;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] is 0 or 10 or 127 ||
                   bytes[0] >= 224 ||
                   bytes[0] == 169 && bytes[1] == 254 ||
                   bytes[0] == 172 && bytes[1] is >= 16 and <= 31 ||
                   bytes[0] == 192 && bytes[1] == 168 ||
                   bytes[0] == 100 && bytes[1] is >= 64 and <= 127 ||
                   bytes[0] == 198 && bytes[1] is 18 or 19 ||
                   bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 0 ||
                   bytes[0] == 192 && bytes[1] == 88 && bytes[2] == 99 ||
                   bytes[0] == 192 && bytes[1] == 0 && bytes[2] == 2 ||
                   bytes[0] == 198 && bytes[1] == 51 && bytes[2] == 100 ||
                   bytes[0] == 203 && bytes[1] == 0 && bytes[2] == 113;
        }

        return address.IsIPv6LinkLocal ||
               address.IsIPv6Multicast ||
               address.IsIPv6SiteLocal ||
               (bytes[0] & 0xfe) == 0xfc ||
               bytes.Take(12).All(value => value == 0) ||
               bytes is [0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, ..] ||
               bytes is [0x20, 0x01, 0x0d, 0xb8, ..];
    }
}

public sealed class ChatGptOAuthClientMetadata(
    IReadOnlyList<string>? redirectUris,
    string? tokenEndpointAuthMethod,
    IReadOnlyList<string>? grantTypes,
    IReadOnlyList<string>? responseTypes,
    string? scope,
    IReadOnlyList<string>? tokenEndpointAuthMethodsSupported = null,
    string? clientId = null,
    string? clientName = null)
{
    [JsonPropertyName("redirect_uris")]
    public IReadOnlyList<string>? RedirectUris { get; } = redirectUris;

    [JsonPropertyName("token_endpoint_auth_method")]
    public string? TokenEndpointAuthMethod { get; } = tokenEndpointAuthMethod;

    [JsonPropertyName("grant_types")]
    public IReadOnlyList<string>? GrantTypes { get; } = grantTypes;

    [JsonPropertyName("response_types")]
    public IReadOnlyList<string>? ResponseTypes { get; } = responseTypes;

    [JsonPropertyName("scope")]
    public string? Scope { get; } = scope;

    [JsonPropertyName("token_endpoint_auth_methods_supported")]
    public IReadOnlyList<string>? TokenEndpointAuthMethodsSupported { get; } = tokenEndpointAuthMethodsSupported;

    [JsonPropertyName("client_id")]
    public string? ClientId { get; } = clientId;

    [JsonPropertyName("client_name")]
    public string? ClientName { get; } = clientName;

    [JsonExtensionData]
    public Dictionary<string, JsonElement>? AdditionalMetadata { get; init; }
}

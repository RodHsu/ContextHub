using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Memory.Application;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class ProjectArtifactObjectStorageOptions
{
    public const string SectionName = "ProjectArtifacts:ObjectStorage";
    public bool Enabled { get; set; }
    public string Provider { get; set; } = "r2";
    public string Endpoint { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string Bucket { get; set; } = string.Empty;
    public string Region { get; set; } = "auto";
    public string PublicBaseUrl { get; set; } = string.Empty;
    public long MaxObjectBytes { get; set; } = 10 * 1024 * 1024;
}

public sealed class S3CompatibleProjectArtifactObjectStore(
    IHttpClientFactory httpClientFactory,
    IOptions<ProjectArtifactObjectStorageOptions> options) : IProjectArtifactObjectStore
{
    private const string Service = "s3";
    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";
    private static readonly char[] UnsafeFileNameChars = Path.GetInvalidFileNameChars();

    public async Task<ProjectArtifactObjectRef> UploadAsync(ProjectArtifactObjectUploadRequest request, CancellationToken cancellationToken)
    {
        var storage = options.Value;
        ValidateOptions(storage);
        if (request.Content.LongLength == 0)
        {
            throw new InvalidOperationException("Managed object content is required.");
        }

        if (request.Content.LongLength > storage.MaxObjectBytes)
        {
            throw new InvalidOperationException($"Managed object content exceeds the configured limit of {storage.MaxObjectBytes} bytes.");
        }

        var key = BuildObjectKey(request.ProjectId, request.SourceSystem, request.ExpiresAt, request.FileName);
        var endpoint = new Uri(storage.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        var objectUri = new Uri(endpoint, $"{EscapePathSegment(storage.Bucket)}/{EscapeObjectKey(key)}");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Put, objectUri)
        {
            Content = new ByteArrayContent(request.Content)
        };
        httpRequest.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(request.ContentType);

        var payloadHash = Sha256Hex(request.Content);
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        httpRequest.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHash);
        httpRequest.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        httpRequest.Headers.TryAddWithoutValidation("x-amz-meta-expires-at", request.ExpiresAt.ToString("O", CultureInfo.InvariantCulture));
        httpRequest.Headers.TryAddWithoutValidation("x-amz-meta-project-id", request.ProjectId);

        var authorization = BuildAuthorizationHeader(httpRequest, storage, payloadHash, amzDate, dateStamp);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue(Algorithm, authorization);

        using var response = await httpClientFactory.CreateClient(nameof(S3CompatibleProjectArtifactObjectStore))
            .SendAsync(httpRequest, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new InvalidOperationException($"Object storage upload failed with HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");
        }

        return new ProjectArtifactObjectRef(
            storage.Provider,
            storage.Bucket,
            key,
            BuildPublicUri(storage, key),
            request.ExpiresAt,
            Sha256Hex(request.Content),
            request.Content.LongLength,
            request.ContentType);
    }

    public async Task DeleteAsync(ProjectArtifactObjectRef objectRef, CancellationToken cancellationToken)
    {
        var storage = options.Value;
        ValidateOptions(storage);
        if (!string.Equals(objectRef.Bucket, storage.Bucket, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Object storage delete rejected because the object bucket does not match the configured bucket.");
        }

        if (string.IsNullOrWhiteSpace(objectRef.Key))
        {
            throw new InvalidOperationException("Object storage delete requires an object key.");
        }

        var endpoint = new Uri(storage.Endpoint.TrimEnd('/') + "/", UriKind.Absolute);
        var objectUri = new Uri(endpoint, $"{EscapePathSegment(storage.Bucket)}/{EscapeObjectKey(objectRef.Key)}");
        using var httpRequest = new HttpRequestMessage(HttpMethod.Delete, objectUri);

        var now = DateTimeOffset.UtcNow;
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        httpRequest.Headers.TryAddWithoutValidation("x-amz-content-sha256", EmptyPayloadHash);
        httpRequest.Headers.TryAddWithoutValidation("x-amz-date", amzDate);

        var authorization = BuildAuthorizationHeader(httpRequest, storage, EmptyPayloadHash, amzDate, dateStamp);
        httpRequest.Headers.Authorization = new AuthenticationHeaderValue(Algorithm, authorization);

        using var response = await httpClientFactory.CreateClient(nameof(S3CompatibleProjectArtifactObjectStore))
            .SendAsync(httpRequest, cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException($"Object storage delete failed with HTTP {(int)response.StatusCode}: {Truncate(body, 500)}");
    }

    private static string BuildAuthorizationHeader(
        HttpRequestMessage request,
        ProjectArtifactObjectStorageOptions options,
        string payloadHash,
        string amzDate,
        string dateStamp)
    {
        var canonicalHeaders = BuildCanonicalHeaders(request);
        var signedHeaders = string.Join(';', canonicalHeaders.Select(x => x.Key));
        var canonicalRequest = string.Join('\n',
        [
            request.Method.Method,
            request.RequestUri!.AbsolutePath,
            string.Empty,
            string.Concat(canonicalHeaders.Select(x => $"{x.Key}:{x.Value}\n")),
            signedHeaders,
            payloadHash
        ]);
        var credentialScope = $"{dateStamp}/{options.Region}/{Service}/aws4_request";
        var stringToSign = string.Join('\n',
        [
            Algorithm,
            amzDate,
            credentialScope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))
        ]);
        var signingKey = BuildSigningKey(options.SecretAccessKey, dateStamp, options.Region);
        var signature = HmacHex(signingKey, stringToSign);
        return $"Credential={options.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static SortedDictionary<string, string> BuildCanonicalHeaders(HttpRequestMessage request)
    {
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        headers["host"] = request.RequestUri!.Authority;

        foreach (var header in request.Headers)
        {
            headers[header.Key.ToLowerInvariant()] = string.Join(',', header.Value.Select(NormalizeHeaderValue));
        }

        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key.ToLowerInvariant()] = string.Join(',', header.Value.Select(NormalizeHeaderValue));
            }
        }

        return headers;
    }

    private static byte[] BuildSigningKey(string secretAccessKey, string dateStamp, string region)
    {
        var kDate = HmacBytes(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        var kRegion = HmacBytes(kDate, region);
        var kService = HmacBytes(kRegion, Service);
        return HmacBytes(kService, "aws4_request");
    }

    private static string BuildObjectKey(string projectId, string sourceSystem, DateTimeOffset expiresAt, string fileName)
    {
        var safeFileName = SanitizeFileName(fileName);
        return string.Join(
            '/',
            ProjectContext.Normalize(projectId),
            sourceSystem.Trim().ToLowerInvariant(),
            expiresAt.UtcDateTime.ToString("yyyy/MM/dd", CultureInfo.InvariantCulture),
            $"{Guid.NewGuid():N}-{safeFileName}");
    }

    private static string BuildPublicUri(ProjectArtifactObjectStorageOptions options, string key)
        => string.IsNullOrWhiteSpace(options.PublicBaseUrl)
            ? $"{options.Endpoint.TrimEnd('/')}/{EscapePathSegment(options.Bucket)}/{EscapeObjectKey(key)}"
            : $"{options.PublicBaseUrl.TrimEnd('/')}/{EscapeObjectKey(key)}";

    private static string EscapeObjectKey(string key)
        => string.Join('/', key.Split('/').Select(EscapePathSegment));

    private static string EscapePathSegment(string value)
        => Uri.EscapeDataString(value);

    private static string SanitizeFileName(string fileName)
    {
        var leaf = Path.GetFileName(fileName.Trim());
        if (string.IsNullOrWhiteSpace(leaf))
        {
            throw new InvalidOperationException("FileName is required.");
        }

        var builder = new StringBuilder(leaf.Length);
        foreach (var ch in leaf)
        {
            builder.Append(UnsafeFileNameChars.Contains(ch) ? '-' : ch);
        }

        return builder.ToString();
    }

    private static void ValidateOptions(ProjectArtifactObjectStorageOptions options)
    {
        if (!options.Enabled)
        {
            throw new InvalidOperationException("Project artifact object storage is not enabled.");
        }

        if (string.IsNullOrWhiteSpace(options.Endpoint) ||
            string.IsNullOrWhiteSpace(options.AccessKeyId) ||
            string.IsNullOrWhiteSpace(options.SecretAccessKey) ||
            string.IsNullOrWhiteSpace(options.Bucket))
        {
            throw new InvalidOperationException("Project artifact object storage requires Endpoint, AccessKeyId, SecretAccessKey, and Bucket.");
        }
    }

    private static string NormalizeHeaderValue(string value)
        => string.Join(' ', value.Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static string Sha256Hex(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static byte[] HmacBytes(byte[] key, string value)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string HmacHex(byte[] key, string value)
        => Convert.ToHexString(HmacBytes(key, value)).ToLowerInvariant();

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : value[..maxLength].TrimEnd();
}

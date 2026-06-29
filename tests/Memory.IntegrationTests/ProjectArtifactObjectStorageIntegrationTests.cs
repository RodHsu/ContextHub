using System.Text;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DotNet.Testcontainers.Builders;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;
using Memory.Tests.Shared;

namespace Memory.IntegrationTests;

public sealed class ProjectArtifactObjectStorageIntegrationTests
{
    [DockerRequiredFact]
    public async Task S3CompatibleStore_Should_Upload_And_Delete_Object_Against_Isolated_Minio()
    {
        await using var minio = new ContainerBuilder("minio/minio:RELEASE.2025-04-22T22-12-26Z")
            .WithEnvironment("MINIO_ROOT_USER", "minioadmin")
            .WithEnvironment("MINIO_ROOT_PASSWORD", "minioadmin")
            .WithCommand("server", "/data")
            .WithPortBinding(9000, true)
            .WithWaitStrategy(Wait.ForUnixContainer()
                .UntilHttpRequestIsSucceeded(request => request
                    .ForPort(9000)
                    .ForPath("/minio/health/ready")))
            .Build();

        await minio.StartAsync();
        var bucket = "context-artifacts";
        var makeBucket = await minio.ExecAsync(["mkdir", "-p", $"/data/{bucket}"]);
        makeBucket.ExitCode.Should().Be(0);

        var endpoint = $"http://127.0.0.1:{minio.GetMappedPublicPort(9000)}";
        var store = new S3CompatibleProjectArtifactObjectStore(
            new FixedHttpClientFactory(new HttpClient()),
            Options.Create(new ProjectArtifactObjectStorageOptions
            {
                Enabled = true,
                Provider = "r2",
                Endpoint = endpoint,
                AccessKeyId = "minioadmin",
                SecretAccessKey = "minioadmin",
                Bucket = bucket,
                Region = "us-east-1",
                MaxObjectBytes = 1024 * 1024
            }));

        var content = Encoding.UTF8.GetBytes("isolated minio object content");
        var objectRef = await store.UploadAsync(
            new ProjectArtifactObjectUploadRequest(
                "ContextHubChatGptGatewayTest",
                "minio-smoke.txt",
                "text/plain",
                content,
                DateTimeOffset.UtcNow.AddHours(1),
                "codex",
                "minio-smoke",
                new Dictionary<string, string>()),
            CancellationToken.None);

        objectRef.Bucket.Should().Be(bucket);
        objectRef.Key.Should().Contain("ContextHubChatGptGatewayTest/codex/");
        using var getClient = new HttpClient();
        var uploaded = await SendSignedGetAsync(getClient, endpoint, bucket, objectRef.Key);
        uploaded.StatusCode.Should().Be(HttpStatusCode.OK);
        (await uploaded.Content.ReadAsByteArrayAsync()).Should().Equal(content);

        await store.DeleteAsync(objectRef, CancellationToken.None);
        var deleted = await SendSignedGetAsync(getClient, endpoint, bucket, objectRef.Key);
        deleted.StatusCode.Should().Be(HttpStatusCode.NotFound);

        await store.DeleteAsync(objectRef, CancellationToken.None);
    }

    private static async Task<HttpResponseMessage> SendSignedGetAsync(HttpClient client, string endpoint, string bucket, string key)
    {
        var objectUri = new Uri($"{endpoint.TrimEnd('/')}/{Uri.EscapeDataString(bucket)}/{EscapeObjectKey(key)}");
        using var request = new HttpRequestMessage(HttpMethod.Get, objectUri);
        var now = DateTimeOffset.UtcNow;
        var amzDate = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        var dateStamp = now.UtcDateTime.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", EmptyPayloadHash);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.Authorization = new AuthenticationHeaderValue(
            Algorithm,
            BuildAuthorizationHeader(request, "minioadmin", "minioadmin", "us-east-1", EmptyPayloadHash, amzDate, dateStamp));
        return await client.SendAsync(request);
    }

    private static string BuildAuthorizationHeader(
        HttpRequestMessage request,
        string accessKeyId,
        string secretAccessKey,
        string region,
        string payloadHash,
        string amzDate,
        string dateStamp)
    {
        var canonicalHeaders = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["host"] = request.RequestUri!.Authority
        };
        foreach (var header in request.Headers)
        {
            canonicalHeaders[header.Key.ToLowerInvariant()] = string.Join(',', header.Value.Select(NormalizeHeaderValue));
        }

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
        var credentialScope = $"{dateStamp}/{region}/s3/aws4_request";
        var stringToSign = string.Join('\n',
        [
            Algorithm,
            amzDate,
            credentialScope,
            Sha256Hex(Encoding.UTF8.GetBytes(canonicalRequest))
        ]);
        var signingKey = BuildSigningKey(secretAccessKey, dateStamp, region);
        var signature = HmacHex(signingKey, stringToSign);
        return $"Credential={accessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";
    }

    private static byte[] BuildSigningKey(string secretAccessKey, string dateStamp, string region)
    {
        var kDate = HmacBytes(Encoding.UTF8.GetBytes("AWS4" + secretAccessKey), dateStamp);
        var kRegion = HmacBytes(kDate, region);
        var kService = HmacBytes(kRegion, "s3");
        return HmacBytes(kService, "aws4_request");
    }

    private static string EscapeObjectKey(string key)
        => string.Join('/', key.Split('/').Select(Uri.EscapeDataString));

    private static string NormalizeHeaderValue(string value)
        => string.Join(' ', value.Trim().Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));

    private static string Sha256Hex(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private static byte[] HmacBytes(byte[] key, string value)
        => HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(value));

    private static string HmacHex(byte[] key, string value)
        => Convert.ToHexString(HmacBytes(key, value)).ToLowerInvariant();

    private const string Algorithm = "AWS4-HMAC-SHA256";
    private const string EmptyPayloadHash = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855";

    private sealed class FixedHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }
}

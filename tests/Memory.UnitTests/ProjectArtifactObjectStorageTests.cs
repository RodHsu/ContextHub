using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Microsoft.Extensions.Options;

namespace Memory.UnitTests;

public sealed class ProjectArtifactObjectStorageTests
{
    [Fact]
    public async Task UploadAsync_Should_Put_Object_With_SigV4_Headers_And_Return_R2_Reference()
    {
        HttpRequestMessage? capturedRequest = null;
        var handler = new StubHttpMessageHandler(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var store = new S3CompatibleProjectArtifactObjectStore(
            new StubHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ProjectArtifactObjectStorageOptions
            {
                Enabled = true,
                Provider = "r2",
                Endpoint = "https://example-account.r2.cloudflarestorage.com",
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                Bucket = "context-artifacts",
                Region = "auto",
                PublicBaseUrl = "https://cdn.example.test/context-artifacts",
                MaxObjectBytes = 1024
            }));

        var expiresAt = new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero);
        var content = Encoding.UTF8.GetBytes("managed file content");
        var result = await store.UploadAsync(
            new ProjectArtifactObjectUploadRequest(
                "ContextHubChatGptGatewayTest",
                "../unsafe managed file.md",
                "text/markdown",
                content,
                expiresAt,
                "codex",
                "unit-test",
                new Dictionary<string, string>()),
            CancellationToken.None);

        capturedRequest.Should().NotBeNull();
        capturedRequest!.Method.Should().Be(HttpMethod.Put);
        capturedRequest.RequestUri!.Host.Should().Be("example-account.r2.cloudflarestorage.com");
        capturedRequest.RequestUri.AbsolutePath.Should().StartWith("/context-artifacts/ContextHubChatGptGatewayTest/codex/2026/06/30/");
        capturedRequest.RequestUri.AbsolutePath.Should().EndWith("-unsafe%20managed%20file.md");
        capturedRequest.Headers.Authorization.Should().NotBeNull();
        capturedRequest.Headers.Authorization!.Scheme.Should().Be("AWS4-HMAC-SHA256");
        capturedRequest.Headers.Authorization.Parameter.Should().Contain("Credential=test-access-key/");
        capturedRequest.Headers.Authorization.Parameter.Should().Contain("SignedHeaders=content-type;host;x-amz-content-sha256;x-amz-date;x-amz-meta-expires-at;x-amz-meta-project-id");
        capturedRequest.Headers.GetValues("x-amz-content-sha256").Single().Should().Be(Sha256Hex(content));
        capturedRequest.Headers.GetValues("x-amz-meta-expires-at").Single().Should().Be(expiresAt.ToString("O"));
        capturedRequest.Headers.GetValues("x-amz-meta-project-id").Single().Should().Be("ContextHubChatGptGatewayTest");
        capturedRequest.Content!.Headers.ContentType!.MediaType.Should().Be("text/markdown");

        result.Provider.Should().Be("r2");
        result.Bucket.Should().Be("context-artifacts");
        result.Key.Should().StartWith("ContextHubChatGptGatewayTest/codex/2026/06/30/");
        result.Uri.Should().StartWith("https://cdn.example.test/context-artifacts/ContextHubChatGptGatewayTest/codex/2026/06/30/");
        result.ExpiresAt.Should().Be(expiresAt);
        result.Sha256.Should().Be(Sha256Hex(content));
        result.SizeBytes.Should().Be(content.Length);
        result.ContentType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task UploadAsync_Should_Reject_When_Object_Storage_Is_Disabled()
    {
        var store = new S3CompatibleProjectArtifactObjectStore(
            new StubHttpClientFactory(new HttpClient(new StubHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)))),
            Options.Create(new ProjectArtifactObjectStorageOptions
            {
                Enabled = false
            }));

        var act = () => store.UploadAsync(
            new ProjectArtifactObjectUploadRequest(
                "ContextHub",
                "artifact.txt",
                "text/plain",
                Encoding.UTF8.GetBytes("content"),
                DateTimeOffset.UtcNow.AddHours(1),
                "codex",
                "unit-test",
                new Dictionary<string, string>()),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*not enabled*");
    }

    [Fact]
    public async Task DeleteAsync_Should_Delete_Object_With_SigV4_Headers_And_Treat_NotFound_As_Success()
    {
        var requests = new List<HttpRequestMessage>();
        var handler = new StubHttpMessageHandler(request =>
        {
            requests.Add(request);
            return new HttpResponseMessage(requests.Count == 1 ? HttpStatusCode.NoContent : HttpStatusCode.NotFound);
        });
        var store = new S3CompatibleProjectArtifactObjectStore(
            new StubHttpClientFactory(new HttpClient(handler)),
            Options.Create(new ProjectArtifactObjectStorageOptions
            {
                Enabled = true,
                Provider = "r2",
                Endpoint = "https://example-account.r2.cloudflarestorage.com",
                AccessKeyId = "test-access-key",
                SecretAccessKey = "test-secret-key",
                Bucket = "context-artifacts",
                Region = "auto"
            }));
        var objectRef = new ProjectArtifactObjectRef(
            "r2",
            "context-artifacts",
            "ContextHubChatGptGatewayTest/codex/2026/06/30/expired artifact.md",
            ExpiresAt: new DateTimeOffset(2026, 6, 30, 12, 0, 0, TimeSpan.Zero));

        await store.DeleteAsync(objectRef, CancellationToken.None);
        await store.DeleteAsync(objectRef, CancellationToken.None);

        requests.Should().HaveCount(2);
        requests.Should().OnlyContain(x => x.Method == HttpMethod.Delete);
        requests[0].RequestUri!.AbsolutePath.Should().Be("/context-artifacts/ContextHubChatGptGatewayTest/codex/2026/06/30/expired%20artifact.md");
        var authorization = requests[0].Headers.Authorization;
        authorization.Should().NotBeNull();
        authorization!.Scheme.Should().Be("AWS4-HMAC-SHA256");
        authorization.Parameter!.Should().Contain("Credential=test-access-key/");
        authorization.Parameter!.Should().Contain("SignedHeaders=host;x-amz-content-sha256;x-amz-date");
        requests[0].Headers.GetValues("x-amz-content-sha256").Single().Should().Be(Sha256Hex([]));
    }

    private static string Sha256Hex(byte[] value)
        => Convert.ToHexString(SHA256.HashData(value)).ToLowerInvariant();

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }
}

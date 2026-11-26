using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.ApiContractTests;

public sealed class ManagedTransferUploadEndpointTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Chunk_upload_masks_storage_write_failures_and_can_retry_after_recovery()
    {
        var directory = Directory.CreateTempSubdirectory("contexthub-transfer-http-");
        var storageRoot = Path.Combine(directory.FullName, "provider-private-root");
        try
        {
            // An existing file at the configured root makes directory creation fail on any supported host.
            await File.WriteAllTextAsync(storageRoot, "synthetic storage fault");
            await using var factory = environment.GetFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
            {
                services.PostConfigure<ManagedObjectStorageOptions>(options =>
                {
                    options.Enabled = true;
                    options.RootPath = storageRoot;
                });
                services.PostConfigure<ManagedFileKeyAuthorityOptions>(options =>
                {
                    options.CurrentKeyId = "test-kek";
                    options.Keys[options.CurrentKeyId] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                });
            }));
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MemoryApplicationFactory.TestBootstrapToken);
            var payload = "synthetic-transfer-chunk"u8.ToArray();
            using var created = await client.PostAsJsonAsync("/api/transfers/uploads",
                new CreateManagedUploadRequest($"transfer-http-{Guid.NewGuid():N}", payload.Length, "test", "create"));
            created.StatusCode.Should().Be(HttpStatusCode.OK);
            var session = (await created.Content.ReadFromJsonAsync<ManagedTransferSessionResult>())!;

            using var denied = await WriteChunkAsync(client, session, payload, "invalid-capability");
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

            using var failed = await WriteChunkAsync(client, session, payload, session.Capability);
            failed.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            failed.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
            failed.Headers.CacheControl!.NoStore.Should().BeTrue();
            failed.Headers.GetValues("CDN-Cache-Control").Should().Equal("no-store");
            var failureBody = await failed.Content.ReadAsStringAsync();
            failureBody.Should().NotContain("provider-private-root").And.NotContain(session.Capability);
            using var problem = JsonDocument.Parse(failureBody);
            problem.RootElement.GetProperty("status").GetInt32().Should().Be(503);
            problem.RootElement.GetProperty("title").GetString().Should().Be("Managed object storage is unavailable for writing.");
            problem.RootElement.GetProperty("code").GetString().Should().Be("StorageUnavailable");

            File.Delete(storageRoot);
            using var retried = await WriteChunkAsync(client, session, payload, session.Capability);
            retried.StatusCode.Should().Be(HttpStatusCode.OK);
            retried.Headers.CacheControl!.NoStore.Should().BeTrue();
            var result = await retried.Content.ReadFromJsonAsync<ManagedTransferMutationResult>();
            result.Should().Be(new ManagedTransferMutationResult(session.SessionId, session.Revision + 1, payload.Length, false));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task<HttpResponseMessage> WriteChunkAsync(HttpClient client, ManagedTransferSessionResult session,
        byte[] payload, string capability)
    {
        using var request = new HttpRequestMessage(HttpMethod.Put, $"{session.ResourcePath}/chunks/0")
        {
            Content = new ByteArrayContent(payload)
        };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentLength = payload.Length;
        request.Headers.Add("X-ContextHub-Capability", capability);
        request.Headers.Add("X-Transfer-Revision", session.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        request.Headers.Add("X-Request-Id", "chunk-0");
        request.Headers.Add("X-Content-SHA256", Convert.ToHexString(SHA256.HashData(payload)).ToLowerInvariant());
        return await client.SendAsync(request);
    }
}

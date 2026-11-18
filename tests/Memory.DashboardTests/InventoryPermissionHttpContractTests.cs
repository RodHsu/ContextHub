using System.Net;
using Memory.Dashboard.Services;

namespace Memory.DashboardTests;

public sealed class InventoryPermissionHttpContractTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inventory_Forbidden_Should_Preserve_Status_Without_Retry(bool files)
    {
        using var handler = new InventoryResponseHandler(HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://context-hub.example.com") };
        var client = new ContextHubApiClient(http);
        var exception = await Assert.ThrowsAsync<HttpRequestException>(() => ReadAsync(client, files));
        Assert.Equal(HttpStatusCode.Forbidden, exception.StatusCode);
        Assert.Contains("Synthetic response", exception.Message);
        Assert.Equal(files ? "/api/files" : "/api/secrets", handler.Path);
        Assert.Equal(1, handler.RequestCount);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Inventory_Server_Error_Should_Keep_Existing_Error_Contract(bool files)
    {
        using var handler = new InventoryResponseHandler(HttpStatusCode.InternalServerError);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://context-hub.example.com") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ReadAsync(new ContextHubApiClient(http), files));
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task Other_Reader_Forbidden_Should_Keep_Existing_Error_Contract()
    {
        using var handler = new InventoryResponseHandler(HttpStatusCode.Forbidden);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("http://context-hub.example.com") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => new ContextHubApiClient(http).GetStatusAsync(CancellationToken.None));
        Assert.Equal(1, handler.RequestCount);
    }

    private static async Task ReadAsync(ContextHubApiClient client, bool files)
    {
        if (files) await client.GetManagedFilesAsync("synthetic-project", 100, CancellationToken.None);
        else await client.GetSecretsAsync("synthetic-project", CancellationToken.None);
    }

    private sealed class InventoryResponseHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? Path { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent("{\"title\":\"Synthetic response\"}") });
        }
    }
}

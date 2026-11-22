using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.McpServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.ApiContractTests;

public sealed class ManagedFileReadEndpointTests
{
    [Fact]
    public async Task Inventory_binds_explicit_project_and_visible_pagination_with_existing_defaults()
    {
        var reads = new Reads();
        await using var app = await Host(reads);
        using var client = app.GetTestClient();
        (await client.GetAsync("/api/files/inventory?projectId=ContextHub&limit=12&offset=24")).StatusCode.Should().Be(HttpStatusCode.OK);
        reads.PageRequest.Should().Be(("ContextHub", 12, 24));
        var page = await client.GetFromJsonAsync<ManagedFileMetadataPage>("/api/files/inventory?projectId=ContextHub");
        reads.PageRequest.Should().Be(("ContextHub", 50, 0));
        page!.HasMore.Should().BeFalse();
        (await client.GetAsync("/api/files/inventory?projectId=ContextHub&offset=invalid")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/files/inventory")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_detail_history_and_foreign_version_pair_share_404_without_response_reason()
    {
        var reads = new Reads();
        await using var app = await Host(reads);
        using var client = app.GetTestClient();
        var file = Guid.NewGuid(); var version = Guid.NewGuid();
        foreach (var path in new[] { $"/{file}", $"/{file}/versions", $"/{file}/versions/{version}" })
        {
            using var response = await client.GetAsync("/api/files" + path + "?projectId=ContextHub");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        }
        reads.VersionRequest.Should().Be(("ContextHub", file, version));
        reads.Result = Row(file, version);
        using var detail = await client.GetAsync($"/api/files/{file}/versions/{version}?projectId=ContextHub");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await detail.Content.ReadFromJsonAsync<ManagedFileInventoryResult>())!.FileVersionId.Should().Be(version);
        var json = await detail.Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "managedObjectId", "contentSha256", "deduplicationScopeKey", "wrapped", "actorId", "purpose", "targetId" })
            json.Should().NotContain(forbidden);
    }

    [Fact]
    public async Task All_four_reads_require_memory_read_before_invoking_service()
    {
        var reads = new Reads();
        await using var app = await Host(reads, false);
        using var client = app.GetTestClient();
        var file = Guid.NewGuid(); var version = Guid.NewGuid();
        foreach (var path in new[] { "/inventory", $"/{file}", $"/{file}/versions", $"/{file}/versions/{version}" })
            (await client.GetAsync("/api/files" + path + "?projectId=ContextHub")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        reads.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Read_budget_expiry_is_unavailable_not_an_empty_complete_page()
    {
        var reads = new Reads { CancelRead = true };
        await using var app = await Host(reads);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/api/files/inventory?projectId=ContextHub");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("MetadataReadIncomplete").And.NotContain("hasMore").And.NotContain("candidate");
    }

    private static ManagedFileInventoryResult Row(Guid file, Guid version) => new(file, version, "Synthetic", "ContextHub", FileAssetState.Active,
        1, FileVersionLifecycle.Ready, FileClassification.Normal, 1, false, null, 0, null, 0, DateTimeOffset.UnixEpoch);
    private static async Task<WebApplication> Host(Reads reads, bool readScope = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IManagedFileReadService>(reads);
        builder.Services.AddSingleton<IRequestActorAccessor>(new Actor(readScope));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("synthetic", _ => { });
        var app = builder.Build();
        app.MapGroup("/api/files").MapFileMetadataReadEndpoints(true);
        await app.StartAsync();
        return app;
    }
    private sealed class Actor(bool readScope) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member,
            readScope ? [SecurityScopes.MemoryRead] : [], ["ContextHub"], true);
    }
    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("synthetic")), "synthetic")));
    }
    private sealed class Reads : IManagedFileReadService
    {
        public bool CancelRead { get; set; }
        public int Calls { get; private set; }
        public (string, int, int) PageRequest { get; private set; }
        public (string, Guid, Guid) VersionRequest { get; private set; }
        public ManagedFileInventoryResult? Result { get; set; }
        public Task<ManagedFileMetadataPage> ListAsync(string projectId, int limit, int offset, CancellationToken ct)
        { Calls++; if (CancelRead) throw new OperationCanceledException(); PageRequest = (projectId, limit, offset); return Task.FromResult(new ManagedFileMetadataPage([], limit, offset, false)); }
        public Task<ManagedFileInventoryResult?> GetAsync(string projectId, Guid fileId, CancellationToken ct)
        { Calls++; return Task.FromResult(Result); }
        public Task<ManagedFileMetadataPage?> ListVersionsAsync(string projectId, Guid fileId, int limit, int offset, CancellationToken ct)
        { Calls++; return Task.FromResult<ManagedFileMetadataPage?>(null); }
        public Task<ManagedFileInventoryResult?> GetVersionAsync(string projectId, Guid fileId, Guid versionId, CancellationToken ct)
        { Calls++; VersionRequest = (projectId, fileId, versionId); return Task.FromResult(Result); }
    }
}

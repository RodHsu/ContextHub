using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.McpServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.ApiContractTests;

public sealed class ProjectWorkItemReadEndpointTests
{
    [Fact]
    public async Task List_binds_offset_and_existing_filters_without_changing_defaults()
    {
        var service = new Reads();
        await using var app = await Host(service);
        using var client = app.GetTestClient();
        (await client.GetAsync("/api/work-items?projectId=ContextHub&offset=23&limit=4&status=Blocked&definitionState=Frozen&includeArchived=true")).StatusCode.Should().Be(HttpStatusCode.OK);
        service.ListRequest.Should().Be(new ProjectWorkItemListRequest("ContextHub", ProjectWorkItemStatus.Blocked, 4, true, 23, ProjectWorkItemDefinitionState.Frozen));
        (await client.GetAsync("/api/work-items?projectId=ContextHub")).StatusCode.Should().Be(HttpStatusCode.OK);
        service.ListRequest.Should().Be(new ProjectWorkItemListRequest("ContextHub"));
        (await client.GetAsync("/api/work-items?projectId=ContextHub&status=unknown")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/work-items?projectId=ContextHub&offset=not-a-number")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Detail_returns_resource_or_404_and_binds_archive_option()
    {
        var service = new Reads();
        await using var app = await Host(service);
        using var client = app.GetTestClient();
        var id = Guid.NewGuid();
        (await client.GetAsync($"/api/work-items/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        service.GetRequest.Should().Be((id, false));
        service.Result = new(id, "ContextHub", "Synthetic", "", [], [], ProjectWorkItemStatus.Pending, 0, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        var response = await client.GetAsync($"/api/work-items/{id}?includeArchived=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ProjectWorkItemResult>())!.Id.Should().Be(id);
        service.GetRequest.Should().Be((id, true));
    }

    [Fact]
    public async Task Both_reads_require_the_existing_memory_read_scope()
    {
        var service = new Reads();
        await using var app = await Host(service, false);
        using var client = app.GetTestClient();
        (await client.GetAsync("/api/work-items?projectId=ContextHub")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/work-items/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        service.ListRequest.Should().BeNull();
        service.GetRequest.Should().Be(default((Guid, bool)));
    }

    private static async Task<WebApplication> Host(Reads service, bool readScope = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IProjectWorkItemService>(service);
        builder.Services.AddSingleton<IRequestActorAccessor>(new Actor(readScope));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("synthetic", _ => { });
        var app = builder.Build();
        app.MapGroup("/api/work-items").MapWorkItemReadEndpoints(true);
        await app.StartAsync();
        return app;
    }
    private sealed class Actor(bool readScope) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member, readScope ? [SecurityScopes.MemoryRead] : [], ["ContextHub"], true);
    }
    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("synthetic")), "synthetic")));
    }
    private sealed class Reads : IProjectWorkItemService
    {
        public ProjectWorkItemListRequest? ListRequest { get; private set; }
        public (Guid, bool) GetRequest { get; private set; }
        public ProjectWorkItemResult? Result { get; set; }
        public Task<ProjectWorkItemResult?> GetAsync(Guid id, bool includeArchived, CancellationToken cancellationToken) { GetRequest = (id, includeArchived); return Task.FromResult(Result); }
        public Task<IReadOnlyList<ProjectWorkItemResult>> ListAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken) { ListRequest = request; return Task.FromResult<IReadOnlyList<ProjectWorkItemResult>>([]); }
        public Task<ProjectWorkItemResult> CreateAsync(ProjectWorkItemCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> UpdateAsync(ProjectWorkItemUpdateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> SetArchivedAsync(Guid id, bool archived, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid id, Guid checklist, bool completed, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> SetGovernanceExclusionAsync(ProjectWorkItemGovernanceExclusionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}

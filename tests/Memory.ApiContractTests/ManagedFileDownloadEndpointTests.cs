using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
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

public sealed class ManagedFileDownloadEndpointTests
{
    [Fact]
    public async Task Logical_pin_and_request_bind_without_accepting_or_serializing_object_locator()
    {
        var service = new Downloads();
        await using var app = await Host(service);
        using var client = app.GetTestClient();
        var file = Guid.NewGuid(); var version = Guid.NewGuid(); var execution = Guid.NewGuid();
        var request = new
        {
            projectId = "ContextHub",
            purpose = "synthetic-review",
            idempotencyKey = "synthetic-key",
            agentId = "synthetic-agent",
            executionId = execution,
            objectId = Guid.NewGuid()
        };
        using var response = await client.PostAsJsonAsync(Path(file, version), request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        service.Pin.Should().Be((file, version));
        service.Request.Should().Be(new CreateLogicalFileDownloadRequest("ContextHub", "synthetic-review", "synthetic-key", "synthetic-agent", execution));
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        json.RootElement.GetProperty("fileId").GetGuid().Should().Be(file);
        json.RootElement.GetProperty("fileVersionId").GetGuid().Should().Be(version);
        json.RootElement.GetProperty("resourcePath").GetString().Should().StartWith("/api/transfers/");
        foreach (var forbidden in new[] { "objectId", "object", "generation", "provider", "storageId", "contentSha256", "purpose", "actorId", "wrapped" })
            json.RootElement.TryGetProperty(forbidden, out _).Should().BeFalse();
        typeof(CreateLogicalFileDownloadRequest).GetProperties().Select(property => property.Name)
            .Should().Equal("ProjectId", "Purpose", "IdempotencyKey", "AgentId", "ExecutionId");
    }

    [Fact]
    public async Task Hidden_and_missing_pins_return_same_empty_uncacheable_404()
    {
        var service = new Downloads { Missing = true };
        await using var app = await Host(service);
        using var client = app.GetTestClient();
        foreach (var _ in Enumerable.Range(0, 2))
        {
            using var response = await client.PostAsJsonAsync(Path(Guid.NewGuid(), Guid.NewGuid()), new CreateLogicalFileDownloadRequest("ContextHub", "synthetic", "synthetic"));
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
        }
    }

    [Fact]
    public async Task Memory_read_scope_is_required_before_resolving_a_pin()
    {
        var service = new Downloads();
        await using var app = await Host(service, readScope: false);
        using var client = app.GetTestClient();
        (await client.PostAsJsonAsync(Path(Guid.NewGuid(), Guid.NewGuid()), new CreateLogicalFileDownloadRequest("ContextHub", "synthetic", "synthetic")))
            .StatusCode.Should().Be(HttpStatusCode.Forbidden);
        service.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Invalid_input_is_generic_without_echoing_purpose_identity_or_internal_errors()
    {
        var service = new Downloads { Invalid = true };
        await using var app = await Host(service);
        using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(Path(Guid.NewGuid(), Guid.NewGuid()), new CreateLogicalFileDownloadRequest("ContextHub", "synthetic", "synthetic"));
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().NotContain("private-exception");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    private static string Path(Guid file, Guid version) => $"/api/files/{file}/versions/{version}/downloads";
    private static async Task<WebApplication> Host(Downloads service, bool readScope = true)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IManagedFileDownloadService>(service);
        builder.Services.AddSingleton<IRequestActorAccessor>(new Actor(readScope));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("synthetic", _ => { });
        var app = builder.Build();
        app.MapGroup("/api/files").MapLogicalFileDownloadEndpoints(true);
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
    private sealed class Downloads : IManagedFileDownloadService
    {
        public bool Missing { get; set; }
        public bool Invalid { get; set; }
        public int Calls { get; private set; }
        public (Guid, Guid) Pin { get; private set; }
        public CreateLogicalFileDownloadRequest? Request { get; private set; }
        public Task<LogicalFileDownloadSessionResult?> CreateAsync(Guid fileId, Guid versionId, CreateLogicalFileDownloadRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Calls++; Pin = (fileId, versionId); Request = request;
            if (Invalid) throw new ArgumentException("private-exception");
            var session = Guid.NewGuid();
            return Task.FromResult(Missing ? null : new LogicalFileDownloadSessionResult(fileId, versionId, session,
                Guid.NewGuid(), "synthetic-only-capability", ManagedTransferOperation.Download, 1, 65536, 100,
                DateTimeOffset.UnixEpoch, $"/api/transfers/{session:D}"));
        }
    }
}

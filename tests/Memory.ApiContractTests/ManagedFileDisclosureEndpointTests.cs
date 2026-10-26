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

public sealed class ManagedFileDisclosureEndpointTests
{
    [Fact]
    public async Task Content_metadata_binds_exact_pin_and_purpose_body_and_returns_no_locator()
    {
        var service = new Reads(); await using var app = await Host(service); using var client = app.GetTestClient();
        var file = Guid.NewGuid(); var version = Guid.NewGuid();
        using var response = await client.PostAsJsonAsync(Path(file, version) + "/content-metadata", new FileContentMetadataRequest("ContextHub", "synthetic"));
        response.StatusCode.Should().Be(HttpStatusCode.OK); response.Headers.CacheControl!.NoStore.Should().BeTrue();
        service.Request.Should().Be(("ContextHub", file, version, "synthetic"));
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("contentSha256").And.Contain("sizeBytes").And.NotContain("objectId").And.NotContain("capability").And.NotContain("purpose");
    }

    [Fact]
    public async Task Audit_is_bounded_masked_and_does_not_serialize_sensitive_fields()
    {
        var service = new Reads(); await using var app = await Host(service); using var client = app.GetTestClient();
        using var response = await client.GetAsync(Path(Guid.NewGuid(), Guid.NewGuid()) + "/audit?projectId=ContextHub&limit=12&offset=24");
        response.StatusCode.Should().Be(HttpStatusCode.OK); service.Paging.Should().Be((12, 24));
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("\"actorMasked\":true").And.Contain("\"purposeMasked\":true").And.Contain("\"deniedDetailsMasked\":true");
        json.Should().NotContain("actorId").And.NotContain("reasonCode").And.NotContain("total");
    }

    [Theory]
    [InlineData("hidden", HttpStatusCode.NotFound)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("incomplete", HttpStatusCode.ServiceUnavailable)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    public async Task Unavailable_denied_incomplete_and_invalid_reads_never_return_sensitive_success_payload(string failure, HttpStatusCode expected)
    {
        var service = new Reads { Failure = failure }; await using var app = await Host(service); using var client = app.GetTestClient();
        var root = Path(Guid.NewGuid(), Guid.NewGuid());
        using var content = await client.PostAsJsonAsync(root + "/content-metadata", new FileContentMetadataRequest("ContextHub", "synthetic"));
        using var audit = await client.GetAsync(root + "/audit?projectId=ContextHub");
        content.StatusCode.Should().Be(expected); audit.StatusCode.Should().Be(expected);
        foreach (var r in new[] { content, audit }) (await r.Content.ReadAsStringAsync()).Should().NotContain("private").And.NotContain("contentSha256");
    }

    [Fact]
    public async Task Both_surfaces_require_memory_read_before_invoking_the_use_case()
    {
        var service = new Reads(); await using var app = await Host(service, false); using var client = app.GetTestClient();
        var root = Path(Guid.NewGuid(), Guid.NewGuid());
        (await client.PostAsJsonAsync(root + "/content-metadata", new FileContentMetadataRequest("ContextHub", "synthetic"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync(root + "/audit?projectId=ContextHub")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        service.Calls.Should().Be(0);
    }

    private static string Path(Guid file, Guid version) => $"/api/files/{file}/versions/{version}";
    private static async Task<WebApplication> Host(Reads reads, bool read = true)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IManagedFileDisclosureService>(reads);
        builder.Services.AddSingleton<IRequestActorAccessor>(new Actor(read));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, Auth>("synthetic", _ => { });
        var app = builder.Build(); app.MapGroup("/api/files").MapFileDisclosureReadEndpoints(true); await app.StartAsync(); return app;
    }
    private sealed class Actor(bool read) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member,
            read ? [SecurityScopes.MemoryRead] : [], ["ContextHub"], true);
    }
    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("synthetic")), "synthetic")));
    }
    private sealed class Reads : IManagedFileDisclosureService
    {
        public string? Failure { get; set; }
        public int Calls { get; private set; }
        public (string, Guid, Guid, string) Request { get; private set; }
        public (int, int) Paging { get; private set; }
        private void Check()
        {
            Calls++;
            if (Failure == "forbidden") throw new UnauthorizedAccessException("private");
            if (Failure == "incomplete") throw new OperationCanceledException();
            if (Failure == "invalid") throw new ArgumentException("private");
        }
        public Task<ManagedFileContentMetadata?> ReadContentMetadataAsync(string p, Guid f, Guid v, string purpose, CancellationToken ct)
        { Check(); Request = (p, f, v, purpose); return Task.FromResult(Failure == "hidden" ? null : new ManagedFileContentMetadata(f, v, 42, new string('a', 64))); }
        public Task<ManagedFileAuditPage?> ReadAuditAsync(string p, Guid f, Guid v, int limit, int offset, CancellationToken ct)
        { Check(); Paging = (limit, offset); return Task.FromResult(Failure == "hidden" ? null : new ManagedFileAuditPage(f, v, [new(Guid.NewGuid(), FileOperation.Download, DateTimeOffset.UnixEpoch)], limit, offset, false)); }
    }
}

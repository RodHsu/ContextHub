using System.Net;
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

public sealed class SecretManagementReadEndpointTests
{
    [Fact]
    public async Task Exact_scope_and_bounded_paging_bind_without_serializing_sensitive_fields()
    {
        var service = new Reads(); await using var app = await Host(service); using var client = app.GetTestClient();
        var secret = Guid.NewGuid(); var path = Path(secret);
        using var expiry = await client.GetAsync(path + "/expiry?projectId=ContextHub");
        expiry.StatusCode.Should().Be(HttpStatusCode.OK); service.Scope.Should().Be(("ContextHub", secret));
        using var leases = await client.GetAsync(path + "/leases?projectId=ContextHub&limit=12&offset=24");
        leases.StatusCode.Should().Be(HttpStatusCode.OK); service.Paging.Should().Be((12, 24));
        using var history = await client.GetAsync(path + "/history?projectId=ContextHub");
        history.StatusCode.Should().Be(HttpStatusCode.OK); service.Paging.Should().Be((25, 0));
        foreach (var response in new[] { expiry, leases, history })
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var json = await response.Content.ReadAsStringAsync();
            foreach (var field in new[] { "capability", "actorId", "targetHash", "requestId", "reasonCode", "ciphertext", "leaseId", "total" }) json.Should().NotContain(field);
        }
        var masked = await history.Content.ReadAsStringAsync();
        masked.Should().Contain("\"actorMasked\":true").And.Contain("\"purposeMasked\":true").And.Contain("\"targetMasked\":true").And.Contain("\"deniedDetailsMasked\":true");
    }

    [Theory]
    [InlineData("hidden", HttpStatusCode.NotFound)]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("incomplete", HttpStatusCode.ServiceUnavailable)]
    [InlineData("invalid", HttpStatusCode.BadRequest)]
    public async Task All_reads_preserve_empty_hidden_result_and_generic_failures(string failure, HttpStatusCode expected)
    {
        var service = new Reads { Failure = failure }; await using var app = await Host(service); using var client = app.GetTestClient();
        foreach (var tab in new[] { "expiry", "leases", "history" })
        {
            using var response = await client.GetAsync(Path(Guid.NewGuid()) + "/" + tab + "?projectId=ContextHub");
            response.StatusCode.Should().Be(expected); response.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await response.Content.ReadAsStringAsync()).Should().NotContain("synthetic-private").And.NotContain("stack").And.NotContain("secretId");
        }
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Each_route_requires_both_existing_read_and_manage_scopes_before_the_use_case(bool read, bool manage)
    {
        var service = new Reads(); await using var app = await Host(service, read, manage); using var client = app.GetTestClient();
        foreach (var tab in new[] { "expiry", "leases", "history" })
            (await client.GetAsync(Path(Guid.NewGuid()) + "/" + tab + "?projectId=ContextHub")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        service.Calls.Should().Be(0);
    }

    private static string Path(Guid secretId) => $"/api/secrets/{secretId}/management";
    private static async Task<WebApplication> Host(Reads reads, bool read = true, bool manage = true)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<ISecretManagementReadService>(reads);
        builder.Services.AddSingleton<IRequestActorAccessor>(new Actor(read, manage));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, Auth>("synthetic", _ => { });
        var app = builder.Build(); app.MapGroup("/api/secrets").MapSecretManagementReadEndpoints(true); await app.StartAsync(); return app;
    }
    private sealed class Actor(bool read, bool manage) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Admin,
            (read ? new[] { SecurityScopes.SecretsRead } : []).Concat(manage ? [SecurityScopes.SecretsManage] : []).ToArray(), ["ContextHub"], true);
    }
    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("synthetic")), "synthetic")));
    }
    private sealed class Reads : ISecretManagementReadService
    {
        public string? Failure { get; init; }
        public int Calls { get; private set; }
        public (string, Guid) Scope { get; private set; }
        public (int, int) Paging { get; private set; }
        private void Check(string project, Guid secret)
        {
            Calls++; Scope = (project, secret);
            if (Failure == "forbidden") throw new UnauthorizedAccessException("synthetic-private");
            if (Failure == "incomplete") throw new OperationCanceledException("synthetic-private");
            if (Failure == "invalid") throw new ArgumentException("synthetic-private");
        }
        public Task<SecretManagementExpiry?> ReadExpiryAsync(string project, Guid secret, CancellationToken ct)
        { Check(project, secret); return Task.FromResult(Failure == "hidden" ? null : new SecretManagementExpiry(secret, 3, 1, SecretVersionState.Active, null)); }
        public Task<SecretManagementLeasePage?> ReadLeasesAsync(string project, Guid secret, int limit, int offset, CancellationToken ct)
        { Check(project, secret); Paging = (limit, offset); return Task.FromResult(Failure == "hidden" ? null : new SecretManagementLeasePage(secret, [new(SecretLeaseKind.Use, SecretLeaseState.Active, DateTimeOffset.UnixEpoch, true, 2, 3, 0)], limit, offset, false)); }
        public Task<SecretManagementHistoryPage?> ReadHistoryAsync(string project, Guid secret, int limit, int offset, CancellationToken ct)
        { Check(project, secret); Paging = (limit, offset); return Task.FromResult(Failure == "hidden" ? null : new SecretManagementHistoryPage(secret, [new(SecretAccessOperation.Use, DateTimeOffset.UnixEpoch)], limit, offset, false)); }
    }
}

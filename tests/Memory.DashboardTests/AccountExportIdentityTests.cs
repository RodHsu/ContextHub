using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Security.Claims;
using Memory.Application;
using Memory.Dashboard.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;

namespace Memory.DashboardTests;

public sealed class AccountExportIdentityTests
{
    [Theory]
    [InlineData("memories")]
    [InlineData("preferences")]
    [InlineData("skills")]
    public async Task Account_export_without_identity_cannot_use_the_configured_bearer_token(string export)
    {
        var account = DashboardExportAccount.Require(Principal(Guid.NewGuid(), Guid.NewGuid()));
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity([], "OwnerCookie")) };
        using var transport = new Capture();
        using var http = CreateClient(new HttpContextAccessor { HttpContext = context }, transport);
        var client = new ContextHubApiClient(http);
        var call = export switch
        {
            "memories" => (Task)client.ExportMemoriesAsync(new(), account, CancellationToken.None),
            "preferences" => client.ExportPreferencesAsync(10, account, CancellationToken.None),
            _ => client.ExportSkillVersionAsync(Guid.NewGuid(), account, CancellationToken.None)
        };

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => call);
        Assert.Equal(0, transport.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Package_export_binds_every_request_to_the_original_tenant_and_account(bool differentTenant, bool differentUser)
    {
        var expected = DashboardExportAccount.Require(Principal(Guid.NewGuid(), Guid.NewGuid()));
        var currentPrincipal = Principal(differentTenant ? Guid.NewGuid() : expected.TenantId,
            differentUser ? Guid.NewGuid() : expected.UserId);
        var context = new DefaultHttpContext { User = currentPrincipal };
        using var transport = new Capture();
        using var http = CreateClient(new HttpContextAccessor { HttpContext = context }, transport);
        var client = new ContextHubApiClient(http);

        if (differentTenant || differentUser)
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ExportMemoriesAsync(new(), expected, CancellationToken.None));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => client.ExportPreferencesAsync(10, expected, CancellationToken.None));
            Assert.Equal(0, transport.Calls);
            return;
        }

        await client.ExportMemoriesAsync(new(), expected, CancellationToken.None);
        await client.ExportPreferencesAsync(10, expected, CancellationToken.None);
        Assert.Equal(2, transport.Calls);
        Assert.All(transport.Accounts, account => Assert.Equal((expected.TenantId, expected.UserId), account));
    }

    [Fact]
    public async Task Server_authenticated_circuit_can_export_its_own_data_without_HttpContext()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var account = await DashboardExportAccount.RequireAsync(new CircuitAuthenticationState(Principal(tenantId, userId)));
        using var transport = new Capture();
        using var http = CreateClient(new HttpContextAccessor { HttpContext = null }, transport);
        var client = new ContextHubApiClient(http);

        await client.ExportMemoriesAsync(new(), account, CancellationToken.None);
        await client.ExportPreferencesAsync(10, account, CancellationToken.None);
        await client.ExportSkillVersionAsync(Guid.NewGuid(), account, CancellationToken.None);

        Assert.Equal(3, transport.Calls);
        Assert.All(transport.Accounts, forwarded => Assert.Equal((tenantId, userId), forwarded));
    }

    [Fact]
    public async Task Anonymous_circuit_cannot_supply_an_export_account()
        => await Assert.ThrowsAsync<UnauthorizedAccessException>(() => DashboardExportAccount.RequireAsync(new CircuitAuthenticationState(new(new ClaimsIdentity()))));

    [Fact]
    public async Task Completed_export_cannot_be_delivered_to_a_different_circuit_account()
    {
        var tenantId = Guid.NewGuid();
        var original = Principal(tenantId, Guid.NewGuid());
        var account = await DashboardExportAccount.RequireAsync(new CircuitAuthenticationState(original));

        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => account.EnsureCurrentAsync(new CircuitAuthenticationState(Principal(tenantId, Guid.NewGuid()))));
        await account.EnsureCurrentAsync(new CircuitAuthenticationState(original));
    }

    private static ClaimsPrincipal Principal(Guid tenantId, Guid userId) => new(new ClaimsIdentity(
        [new("contexthub:tenant_id", tenantId.ToString("D")), new("contexthub:user_id", userId.ToString("D"))], "Test"));

    private sealed class CircuitAuthenticationState(ClaimsPrincipal principal) : AuthenticationStateProvider
    {
        public override Task<AuthenticationState> GetAuthenticationStateAsync() => Task.FromResult(new AuthenticationState(principal));
    }

    private static HttpClient CreateClient(IHttpContextAccessor accessor, HttpMessageHandler transport)
    {
        var client = new HttpClient(new DashboardActAsDelegatingHandler(accessor) { InnerHandler = transport })
        {
            BaseAddress = new Uri("https://context-hub.example.test")
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "synthetic-fallback-token");
        return client;
    }

    private sealed class Capture : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<(Guid TenantId, Guid UserId)> Accounts { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Accounts.Add((Guid.Parse(request.Headers.GetValues("X-ContextHub-Act-As-TenantId").Single()),
                Guid.Parse(request.Headers.GetValues("X-ContextHub-Act-As-UserId").Single())));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Post
                    ? JsonContent.Create(new MemoryTransferDownloadResult("test.json", "application/json", "", 0, false))
                    : request.RequestUri!.AbsolutePath.StartsWith("/api/skills/", StringComparison.Ordinal)
                        ? JsonContent.Create(new PortableSkillBundle([]))
                        : JsonContent.Create(Array.Empty<UserPreferenceResult>())
            });
        }
    }
}

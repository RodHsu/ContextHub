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

/// <summary>Actual route/filter/response mapping, with a synthetic service boundary; no SQL acceptance.</summary>
public sealed class WorkItemProposalEndpointTests
{
    private static readonly Guid Id = Guid.Parse("a39ed00b-4b8b-4c0e-9605-a3af3b8c71a2");

    [Theory]
    [InlineData("approve")]
    [InlineData("reject")]
    public async Task Decision_routes_forward_the_route_identity_and_note_without_rebuilding_the_proposal(string operation)
    {
        var service = new Proposals(); await using var app = await Host(service); using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(Path(operation), new { note = "original operator note" });

        response.StatusCode.Should().Be(HttpStatusCode.OK); response.Headers.CacheControl!.NoStore.Should().BeTrue();
        service.Calls.Should().ContainSingle().Which.Should().Be((operation, new ChatGptProposalDecisionRequest(Id, "original operator note")));
        var result = await response.Content.ReadFromJsonAsync<ChatGptProposalResult>();
        result!.Id.Should().Be(Id); result.PayloadJson.Should().Be("{\"expectedRevision\":7,\"requestId\":\"original-key\"}");
    }

    [Fact]
    public async Task Memory_write_is_required_before_either_decision_service_method()
    {
        var service = new Proposals(); await using var app = await Host(service, write: false); using var client = app.GetTestClient();
        foreach (var operation in new[] { "approve", "reject" })
        {
            using var response = await client.PostAsJsonAsync(Path(operation), new { note = "blocked" });
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        service.Calls.Should().BeEmpty();
    }

    [Theory]
    [InlineData(SafeEditConflictCodes.MissingReadVersion, null)]
    [InlineData(SafeEditConflictCodes.StaleReadVersion, 12L)]
    public async Task Original_missing_or_stale_read_version_remains_a_typed_no_store_409(string code, long? current)
    {
        var service = new Proposals { Failure = new SafeEditConflictException(code, current) };
        await using var app = await Host(service); using var client = app.GetTestClient();
        foreach (var operation in new[] { "approve", "reject" })
        {
            using var response = await client.PostAsJsonAsync(Path(operation), new { note = (string?)null });
            response.StatusCode.Should().Be(HttpStatusCode.Conflict); response.Headers.CacheControl!.NoStore.Should().BeTrue();
            using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            body.RootElement.GetProperty("code").GetString().Should().Be(code);
            if (current.HasValue) body.RootElement.GetProperty("currentRevision").GetInt64().Should().Be(current.Value);
            else body.RootElement.GetProperty("currentRevision").ValueKind.Should().Be(JsonValueKind.Null);
        }
        service.Calls.Should().OnlyContain(call => call.Request.ProposalId == Id && call.Request.Note == "");
        // The synthetic service emits MissingReadVersion: actual payload validation is covered at Application level.
    }

    [Theory]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("hidden", HttpStatusCode.NotFound)]
    [InlineData("unknown", HttpStatusCode.ServiceUnavailable)]
    [InlineData("sql", HttpStatusCode.ServiceUnavailable)]
    [InlineData("noncaller-cancel", HttpStatusCode.ServiceUnavailable)]
    public async Task Private_and_unconfirmed_failures_never_return_private_details_or_false_success(string failure, HttpStatusCode status)
    {
        var service = new Proposals { Failure = Error(failure) };
        await using var app = await Host(service); using var client = app.GetTestClient();
        foreach (var operation in new[] { "approve", "reject" })
        {
            using var response = await client.PostAsJsonAsync(Path(operation), new { note = "same original proposal" });
            response.StatusCode.Should().Be(status); response.Headers.CacheControl!.NoStore.Should().BeTrue();
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain("synthetic-private").And.NotContain("Exception").And.NotContain("stack");
            if (status == HttpStatusCode.ServiceUnavailable)
            {
                using var json = JsonDocument.Parse(body);
                json.RootElement.GetProperty("code").GetString().Should().Be("ProposalOutcomeUnknown");
                json.RootElement.GetProperty("traceId").GetString().Should().NotBeNullOrWhiteSpace();
            }
            else body.Should().BeEmpty();
        }
        service.Calls.Should().HaveCount(2);
    }

    private static string Path(string operation) => $"/api/chatgpt/proposals/{Id:D}/{operation}";
    private static Exception Error(string kind) => kind switch
    {
        "forbidden" => new UnauthorizedAccessException("synthetic-private-project"),
        "hidden" => new KeyNotFoundException("synthetic-private-project"),
        "unknown" => new WorkItemProposalOutcomeUnknownException(),
        "noncaller-cancel" => new OperationCanceledException("synthetic-private-cancel"),
        _ => new Exception("synthetic-private-SQL-path", new Exception("synthetic-private-inner"))
    };

    private static async Task<WebApplication> Host(Proposals service, bool write = true)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IChatGptProposalService>(service);
        builder.Services.AddSingleton<IRequestActorAccessor>(new Actor(write));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, Auth>("synthetic", _ => { });
        var app = builder.Build(); app.MapGroup("/api/chatgpt").MapProposalDecisionEndpoints(true);
        await app.StartAsync(); return app;
    }

    private sealed class Actor(bool write) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member,
            write ? [SecurityScopes.MemoryWrite] : [SecurityScopes.MemoryRead], ["ContextHub"], true);
    }

    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("synthetic")), "synthetic")));
    }

    private sealed class Proposals : IChatGptProposalService
    {
        public Exception? Failure { get; init; }
        public List<(string Operation, ChatGptProposalDecisionRequest Request)> Calls { get; } = [];
        public Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken ct) => Decide("approve", request);
        public Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken ct) => Decide("reject", request);
        private Task<ChatGptProposalResult> Decide(string operation, ChatGptProposalDecisionRequest request)
        {
            Calls.Add((operation, request));
            return Failure is not null ? Task.FromException<ChatGptProposalResult>(Failure) : Task.FromResult(new ChatGptProposalResult(request.ProposalId,
                "project_work_item_archive", ChatGptProposalStatus.Pending, "ContextHub", "ContextHub", "Synthetic", "Synthetic",
                "{\"expectedRevision\":7,\"requestId\":\"original-key\"}", "", "", "", null, "", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        }
        public Task<ChatGptProposalResult> CreateAsync(ChatGptProposalCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ChatGptProposalResult>> ListAsync(ChatGptProposalListRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}

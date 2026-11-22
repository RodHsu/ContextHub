using System.Reflection;
using System.Runtime.CompilerServices;
using System.Security.Claims;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.ChatGptGateway;
using Memory.Domain;
using Microsoft.AspNetCore.Http;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Memory.ChatGptGatewayTests;

/// <summary>Actual gateway methods and SDK schemas with synthetic application results, not a SQL/MCP transport acceptance.</summary>
public sealed class WorkItemProposalBoundaryTests
{
    private static readonly Guid Id = Guid.Parse("f0762cb5-2ec5-45c0-9ea9-d83985453ba7");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Decision_tools_forward_the_original_request_and_cancellation_without_rebuilding_payload(bool reject)
    {
        var service = new Proposals(); var tools = Tools(service); using var cancellation = new CancellationTokenSource();
        var request = new ChatGptProposalDecisionRequest(Id, "original note");
        var actual = await Decide(tools, request, reject, cancellation.Token);
        service.Decisions.Should().ContainSingle().Which.Should().Be((reject, request, cancellation.Token));
        actual.Should().BeSameAs(service.Result);
    }

    [Fact]
    public void Actual_decision_SDK_schema_remains_request_proposalId_and_note_without_new_precondition_fields()
    {
        foreach (var name in new[] { "chatgpt_proposal_approve", "chatgpt_proposal_reject" })
        {
            var tool = McpServerTool.Create(typeof(ChatGptGatewayTools).GetMethod(name)!,
                RuntimeHelpers.GetUninitializedObject(typeof(ChatGptGatewayTools)), new McpServerToolCreateOptions());
            var properties = JsonSerializer.SerializeToElement(tool.ProtocolTool).GetProperty("inputSchema").GetProperty("properties");
            properties.EnumerateObject().Select(property => property.Name).Should().Equal("request");
            var request = properties.GetProperty("request").GetProperty("properties");
            request.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(new[] { "proposalId", "note" });
        }
    }

    [Fact]
    public async Task Known_original_version_conflicts_keep_machine_codes_and_authorized_revision_without_inner_exception()
    {
        var service = new Proposals(); var tools = Tools(service);
        foreach (var reject in new[] { false, true })
            foreach (var code in new[] { SafeEditConflictCodes.MissingReadVersion, SafeEditConflictCodes.StaleReadVersion })
            {
                var revision = code == SafeEditConflictCodes.StaleReadVersion ? 12L : (long?)null;
                service.Failure = new SafeEditConflictException(code, revision);
                var thrown = await FluentActions.Awaiting(() => Decide(tools, new(Id), reject)).Should().ThrowAsync<McpException>();
                using var body = JsonDocument.Parse(thrown.Which.Message);
                body.RootElement.GetProperty("code").GetString().Should().Be(code);
                if (revision.HasValue) body.RootElement.GetProperty("currentRevision").GetInt64().Should().Be(revision.Value);
                else body.RootElement.GetProperty("currentRevision").ValueKind.Should().Be(JsonValueKind.Null);
                thrown.Which.InnerException.Should().BeNull();
            }
    }

    [Theory]
    [InlineData("forbidden")]
    [InlineData("hidden")]
    [InlineData("unknown")]
    [InlineData("sql")]
    [InlineData("noncaller-cancel")]
    public async Task Proposal_boundary_returns_safe_errors_without_private_or_inner_exception_details(string failure)
    {
        var service = new Proposals
        {
            Failure = failure switch
            {
                "forbidden" => new UnauthorizedAccessException("synthetic-private-project"),
                "hidden" => new KeyNotFoundException("synthetic-private-project"),
                "unknown" => new WorkItemProposalOutcomeUnknownException(),
                "noncaller-cancel" => new OperationCanceledException("synthetic-private-cancel"),
                _ => new Exception("synthetic-private-SQL-path", new Exception("synthetic-private-inner"))
            }
        };
        var tools = Tools(service);
        foreach (var reject in new[] { false, true })
        {
            var error = await FluentActions.Awaiting(() => Decide(tools, new(Id), reject)).Should().ThrowAsync<McpException>();
            error.Which.Message.Should().NotContain("synthetic-private"); error.Which.InnerException.Should().BeNull();
            if (failure is "forbidden" or "hidden") error.Which.Message.Should().Be("Proposal operation is unavailable.");
            else
            {
                using var json = JsonDocument.Parse(error.Which.Message);
                json.RootElement.GetProperty("code").GetString().Should().Be("ProposalOutcomeUnknown");
            }
        }
        service.Decisions.Should().HaveCount(2);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_instead_of_returning_an_unconfirmed_outcome()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        var service = new Proposals { Failure = new OperationCanceledException(cancellation.Token) };
        var tools = Tools(service);
        var failure = await FluentActions.Awaiting(() => tools.chatgpt_proposal_approve(new(Id), cancellation.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        failure.Which.CancellationToken.Should().Be(cancellation.Token);
    }

    [Fact]
    public async Task Governance_create_preserves_original_payload_run_and_existing_OAuth_claim_projection_and_uses_safe_boundary()
    {
        var service = new Proposals(); var tools = Tools(service);
        var request = new ChatGptGovernanceProposalRequest("project_work_item_archive", "ContextHub",
            "{\"workItemId\":\"0691f675-a516-47cd-9172-c63b18a92aa8\",\"archived\":true}", "Synthetic", "Synthetic", "same-original-run");
        var actual = await tools.chatgpt_governance_proposal_create(request);
        actual.Should().BeSameAs(service.Result);
        service.Create.Should().Be(new ChatGptProposalCreateRequest(request.ToolName, request.ProjectId, request.PayloadJson,
            request.Title, request.Summary, "synthetic-subject", "synthetic@example.invalid", "synthetic name", request.GovernanceRunId));
        // This adapter does not synthesize a read version or a new request identity for a legacy payload.
        service.Failure = new SafeEditConflictException(SafeEditConflictCodes.MissingReadVersion);
        var error = await FluentActions.Awaiting(() => tools.chatgpt_governance_proposal_create(request)).Should().ThrowAsync<McpException>();
        using var json = JsonDocument.Parse(error.Which.Message);
        json.RootElement.GetProperty("code").GetString().Should().Be(SafeEditConflictCodes.MissingReadVersion);
        error.Which.InnerException.Should().BeNull();
    }

    private static Task<ChatGptProposalResult> Decide(ChatGptGatewayTools tools, ChatGptProposalDecisionRequest request, bool reject,
        CancellationToken ct = default) => reject ? tools.chatgpt_proposal_reject(request, ct) : tools.chatgpt_proposal_approve(request, ct);

    private static ChatGptGatewayTools Tools(Proposals service)
    {
        var http = new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext
            {
                User = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim("sub", "synthetic-subject"), new Claim(ClaimTypes.Email, "synthetic@example.invalid"), new Claim(ClaimTypes.Name, "synthetic name")], "synthetic"))
            }
        };
        var actors = new RequestActorAccessor
        {
            Current = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic actor", TenantUserRole.Member,
            [SecurityScopes.MemoryWrite], ["ContextHub"], true)
        };
        var constructor = typeof(ChatGptGatewayTools).GetConstructors().Single();
        var arguments = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(IChatGptProposalService) ? (object)service
            : parameter.ParameterType == typeof(IHttpContextAccessor) ? http : parameter.ParameterType == typeof(IRequestActorAccessor) ? actors : null).ToArray();
        return (ChatGptGatewayTools)constructor.Invoke(arguments);
    }

    private sealed class Proposals : IChatGptProposalService
    {
        public Exception? Failure { get; set; }
        public ChatGptProposalCreateRequest? Create { get; private set; }
        public List<(bool Reject, ChatGptProposalDecisionRequest Request, CancellationToken Cancellation)> Decisions { get; } = [];
        public ChatGptProposalResult Result { get; } = new(Id, "project_work_item_archive", ChatGptProposalStatus.Pending,
            "ContextHub", "ContextHub", "Synthetic", "Synthetic", "{}", "", "", "", null, "", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
        public Task<ChatGptProposalResult> CreateAsync(ChatGptProposalCreateRequest request, CancellationToken ct)
        { Create = request; return Respond(); }
        public Task<ChatGptProposalResult> ApproveAsync(ChatGptProposalDecisionRequest request, CancellationToken ct)
        { Decisions.Add((false, request, ct)); return Respond(); }
        public Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken ct)
        { Decisions.Add((true, request, ct)); return Respond(); }
        private Task<ChatGptProposalResult> Respond() => Failure is null ? Task.FromResult(Result) : Task.FromException<ChatGptProposalResult>(Failure);
        public Task<IReadOnlyList<ChatGptProposalResult>> ListAsync(ChatGptProposalListRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Components.Pages;
using Memory.Dashboard.Services;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memory.DashboardTests;

public sealed class ChatGptProposalDecisionPageTests
{
    [Fact]
    public async Task Unconfirmed_work_item_result_can_confirm_the_same_proposal_without_a_reject_or_new_request()
    {
        using var handler = new ProposalHandler(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://context-hub.example.test") };
        using var services = Services(http);
        await using var renderer = new WorkItemEditorTestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.MountAsync<ChatGptProposals>();

        await renderer.ClickAsync("proposal-approve");

        renderer.Elements().Should().Contain(element => element.Text.Contains("無法確認原提案是否已寫入", StringComparison.Ordinal));
        renderer.Find("proposal-approve").Text.Should().Contain("確認原提案結果");
        renderer.Elements().Should().NotContain(element => Equals(element.Attribute("data-testid"), "proposal-reject"));
        handler.Approvals.Should().ContainSingle().Which.Should().Be($"/api/chatgpt/proposals/{handler.Proposal.Id:D}/approve");

        await renderer.ClickAsync("proposal-approve");

        handler.Approvals.Should().Equal(
            $"/api/chatgpt/proposals/{handler.Proposal.Id:D}/approve",
            $"/api/chatgpt/proposals/{handler.Proposal.Id:D}/approve");
        renderer.Elements().Should().NotContain(element => element.Text.Contains("無法確認原提案是否已寫入", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_definite_conflict_keeps_the_current_pending_proposal_without_marking_it_unconfirmed()
    {
        using var handler = new ProposalHandler(HttpStatusCode.Conflict);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://context-hub.example.test") };
        using var services = Services(http);
        await using var renderer = new WorkItemEditorTestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.MountAsync<ChatGptProposals>();

        await renderer.ClickAsync("proposal-approve");

        renderer.Find("proposal-approve").Text.Should().Contain("批准寫入");
        renderer.Find("proposal-reject").Disabled.Should().BeFalse();
        renderer.Elements().Should().Contain(element => Equals(element.Attribute("role"), "alert") &&
            element.Text.Contains("核對原提案", StringComparison.Ordinal));
        renderer.Elements().Should().NotContain(element => element.Text.Contains("無法確認原提案是否已寫入", StringComparison.Ordinal));
        handler.Approvals.Should().HaveCount(1);
    }

    [Fact]
    public async Task An_uncertain_rejection_never_becomes_an_approval_recovery_action()
    {
        using var handler = new ProposalHandler(HttpStatusCode.ServiceUnavailable);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://context-hub.example.test") };
        using var services = Services(http);
        await using var renderer = new WorkItemEditorTestRenderer(services, services.GetRequiredService<ILoggerFactory>());
        await renderer.MountAsync<ChatGptProposals>();

        await renderer.ClickAsync("proposal-reject");

        renderer.Find("proposal-approve").Text.Should().Contain("批准寫入");
        renderer.Elements().Should().NotContain(element => element.Text.Contains("無法確認原提案是否已寫入", StringComparison.Ordinal));
        handler.Approvals.Should().BeEmpty();
        handler.Rejections.Should().ContainSingle().Which.Should().Be($"/api/chatgpt/proposals/{handler.Proposal.Id:D}/reject");
    }

    private static ServiceProvider Services(HttpClient http) => new ServiceCollection().AddLogging()
        .AddSingleton<IContextHubApiClient>(new ContextHubApiClient(http))
        .AddSingleton<NavigationManager>(new TestNavigationManager())
        .BuildServiceProvider();

    private sealed class TestNavigationManager : NavigationManager
    {
        public TestNavigationManager() => Initialize("https://context-hub.example.test/", "https://context-hub.example.test/chatgpt-proposals");

        protected override void NavigateToCore(string uri, bool forceLoad)
            => throw new InvalidOperationException("Navigation is not supported by this rendering fixture.");
    }

    private sealed class ProposalHandler(HttpStatusCode firstOutcome) : HttpMessageHandler
    {
        public ChatGptProposalResult Proposal { get; } = new(
            Guid.NewGuid(), WorkItemProposalContract.ToolName, ChatGptProposalStatus.Pending,
            "ContextHub", "ContextHub", "原提案", "提案摘要", "{}",
            "test", "", "", null, "", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);

        public List<string> Approvals { get; } = [];
        public List<string> Rejections { get; } = [];
        private bool _applied;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(_applied ? Array.Empty<ChatGptProposalResult>() : [Proposal])
                });

            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Post && path.EndsWith("/reject", StringComparison.Ordinal))
            {
                Rejections.Add(path);
                return Task.FromResult(new HttpResponseMessage(firstOutcome));
            }
            if (request.Method != HttpMethod.Post || !path.EndsWith("/approve", StringComparison.Ordinal))
                throw new InvalidOperationException("Unexpected test request.");
            Approvals.Add(path);
            if (Approvals.Count == 1)
                return Task.FromResult(new HttpResponseMessage(firstOutcome)
                {
                    Content = JsonContent.Create(new { code = firstOutcome == HttpStatusCode.Conflict ? "StaleReadVersion" : "ProposalOutcomeUnknown" })
                });
            _applied = true;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(Proposal with { Status = ChatGptProposalStatus.Applied, AppliedResourceId = Guid.NewGuid() })
            });
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Tests.Shared;

namespace Memory.ApiContractTests;

public sealed class SkillsAndAgentExecutionReadEndpointTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Skills_list_reads_back_the_published_default_version()
    {
        using var client = environment.GetFactory().CreateClient();
        var fixtureId = Guid.NewGuid().ToString("N");
        var stableKey = $"http-skill-{fixtureId}"[..30];
        var name = $"Published HTTP test skill {fixtureId}";
        const string description = "Verify published skill metadata through its REST inventory.";
        var markdown = $"---\nname: {name}\ndescription: {description}\n---\n# Published skill documentation\n";
        var bundle = new PortableSkillBundle([new PortableSkillFile("SKILL.md", Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)))]);
        var draft = new SkillImportPreviewRequest(stableKey, name, description, "Use for HTTP read-back validation.", "1.0.0",
            bundle, SkillSourceKind.Repository, $"https://example.test/{stableKey}", "test-revision", "MIT", TrustLevel: SkillTrustLevel.SourceVerified);

        using var importResponse = await client.PostAsJsonAsync("/api/skills/import", new SkillImportRequest(draft, $"import-{fixtureId}"));
        importResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var imported = (await importResponse.Content.ReadFromJsonAsync<SkillImportResult>())!;
        imported.Version.Status.Should().Be(SkillLifecycleStatus.Draft);

        var versionId = imported.Version.Id;
        using var publishResponse = await client.PostAsJsonAsync($"/api/skills/versions/{versionId:D}/publish",
            new SkillPublishRequest(versionId, imported.Version.ContentHash, ApprovalGranted: false, IdempotencyKey: $"publish-{fixtureId}"));
        publishResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var skills = await client.GetFromJsonAsync<SkillSummaryResult[]>("/api/skills");
        var published = skills.Should().ContainSingle(skill => skill.StableKey == stableKey).Subject;
        published.Id.Should().Be(imported.Skill.Id);
        published.DefaultVersionId.Should().Be(versionId);
        var version = published.Versions.Should().ContainSingle().Subject;
        version.Id.Should().Be(versionId);
        version.Status.Should().Be(SkillLifecycleStatus.Published);
        version.ContentHash.Should().Be(imported.Version.ContentHash);
        version.PublishedAt.Should().NotBeNull();
    }

    [DockerRequiredFact]
    public async Task Execution_dashboard_and_detail_read_the_same_claim_without_exposing_the_lease_token()
    {
        using var client = environment.GetFactory().CreateClient();
        var fixtureId = Guid.NewGuid().ToString("N");
        var projectId = $"http-execution-{fixtureId}";
        const string repositoryId = "execution-read-route-fixture";
        const string agentId = "execution-read-route-agent";

        using var workItemResponse = await client.PostAsJsonAsync("/api/work-items",
            new ProjectWorkItemCreateRequest(projectId, "Execution REST read-back fixture"));
        workItemResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var workItem = (await workItemResponse.Content.ReadFromJsonAsync<ProjectWorkItemResult>())!;

        var prepare = new AgentExecutionPrepareRequest(workItem.Id, projectId, repositoryId,
            "Verify the execution dashboard and detail contracts.", ["Read back the same claimed execution"], ["fixture:work-item"],
            ["Keep work item state"], ["read"], ["HTTP regression check"], "Codex", [], Priority: 50, MaxAttempts: 2,
            SkillResolutionId: null, IdempotencyKey: $"prepare-{fixtureId}");
        using var prepareResponse = await client.PostAsJsonAsync("/api/agent-executions/prepare", prepare);
        prepareResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var prepared = (await prepareResponse.Content.ReadFromJsonAsync<AgentExecutionResult>())!;
        prepared.Status.Should().Be(AgentExecutionStatus.Ready);

        using var claimResponse = await client.PostAsJsonAsync("/api/agent-executions/claim-next",
            new AgentExecutionClaimRequest(projectId, repositoryId, agentId, "Codex", [], LeaseSeconds: 300, IdempotencyKey: $"claim-{fixtureId}"));
        claimResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var claimed = (await claimResponse.Content.ReadFromJsonAsync<AgentExecutionClaimResult>())!;
        claimed.HasExecution.Should().BeTrue();
        claimed.Execution!.Id.Should().Be(prepared.Id);
        var leaseToken = claimed.LeaseToken;
        string.IsNullOrWhiteSpace(leaseToken).Should().BeFalse("a successful claim must provide proof for the non-disclosure assertions");

        var dashboardJson = await client.GetStringAsync($"/api/agent-executions/dashboard/{projectId}");
        var detailJson = await client.GetStringAsync($"/api/agent-executions/{prepared.Id:D}");
        AssertNoLeaseToken(dashboardJson, leaseToken!);
        AssertNoLeaseToken(detailJson, leaseToken!);

        var dashboard = JsonSerializer.Deserialize<AgentExecutionDashboardResult>(dashboardJson, JsonSerializerOptions.Web)!;
        var detail = JsonSerializer.Deserialize<AgentExecutionResult>(detailJson, JsonSerializerOptions.Web)!;
        var recent = dashboard.Recent.Should().ContainSingle().Subject;
        dashboard.ProjectId.Should().Be(projectId);
        dashboard.Counts[AgentExecutionStatus.Claimed].Should().Be(1);
        dashboard.ActiveLeases.Should().Be(1);
        recent.Id.Should().Be(prepared.Id);
        recent.Status.Should().Be(AgentExecutionStatus.Claimed);
        detail.Id.Should().Be(recent.Id);
        detail.Status.Should().Be(recent.Status);
        detail.ClaimedByAgentId.Should().Be(agentId);
        detail.PackageHash.Should().Be(prepared.PackageHash).And.Be(recent.PackageHash);
        detail.Package.ExecutionId.Should().Be(prepared.Id);
        detail.Package.WorkItemId.Should().Be(workItem.Id);
        detail.Events!.Select(item => item.EventType)
            .Should().ContainInOrder(AgentExecutionEventType.Prepared, AgentExecutionEventType.Claimed);
    }

    private static void AssertNoLeaseToken(string json, string leaseToken)
    {
        json.Contains(leaseToken, StringComparison.Ordinal).Should().BeFalse("execution read endpoints must not return a claim token");
        json.Contains("\"leaseToken\"", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
        json.Contains("\"leaseTokenHash\"", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }
}

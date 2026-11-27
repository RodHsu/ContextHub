using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.ApiContractTests;

public sealed class SkillsAndAgentExecutionReadEndpointTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Skill_canonical_tags_list_is_bounded_and_scoped_to_current_project_and_owner()
    {
        var fixture = Guid.NewGuid();
        var project = $"Canonical-Skills-{fixture:N}";
        var otherProject = $"Other-Skills-{fixture:N}";
        var skill = new Skill { StableKey = $"canonical-{fixture:N}"[..30], Name = "Synthetic canonical tags" };
        var now = DateTimeOffset.UtcNow;
        var factory = environment.GetFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var user = await db.TenantUsers.SingleAsync(candidate => candidate.Username == "contract-test-admin");
            skill.TenantId = user.TenantId;
            skill.OwnerUserId = user.Id;
            skill.CreatedAt = skill.UpdatedAt = now;
            db.Skills.Add(skill);
            var seeds = new[]
            {
                (Definition: CreateTag("Alpha", project), Status: "Active"),
                (Definition: CreateTag("Bravo", project), Status: "Active"),
                (Definition: CreateTag("HiddenInactive", project), Status: "Removed"),
                (Definition: CreateTag("HiddenForeign", otherProject), Status: "Active")
            };
            await using (var transaction = await db.Database.BeginTransactionAsync())
            {
                db.CanonicalTagDefinitions.AddRange(seeds.Select(seed => seed.Definition));
                await db.SaveChangesAsync();
                db.CanonicalTagBindings.AddRange(seeds.Select(seed => new CanonicalTagBinding
                {
                    TenantId = user.TenantId,
                    OwnerUserId = user.Id,
                    DefinitionId = seed.Definition.Id,
                    ProjectId = project,
                    ResourceType = "Skill",
                    ResourceId = skill.Id.ToString("D"),
                    Status = seed.Status,
                    EvidenceRef = "synthetic-private-evidence",
                    CreatedAt = now
                }));
                await db.SaveChangesAsync();
                await transaction.CommitAsync();
            }

            var actors = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            var service = scope.ServiceProvider.GetRequiredService<ISkillService>();
            var permitted = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, TenantUserRole.Member,
                [SecurityScopes.SkillsRead], [project], true);
            actors.Current = permitted with { UserId = Guid.NewGuid() };
            (await service.ListCanonicalTagBindingsAsync(skill.Id, project, 0, 10, default)).Should().BeNull();
            actors.Current = permitted with { AllowedProjectIds = [otherProject] };
            Func<Task> forbidden = () => service.ListCanonicalTagBindingsAsync(skill.Id, project, 0, 10, default);
            await forbidden.Should().ThrowAsync<UnauthorizedAccessException>();

            CanonicalTagDefinition CreateTag(string name, string definitionProject)
                => new()
                {
                    TenantId = user.TenantId,
                    OwnerUserId = user.Id,
                    ProjectId = definitionProject,
                    CanonicalName = name,
                    NormalizedName = name.ToLowerInvariant(),
                    CreatedAt = now,
                    UpdatedAt = now
                };
        }

        using var client = factory.CreateClient();
        var path = $"/api/skills/{skill.Id:D}/canonical-tags";
        var query = $"projectId={Uri.EscapeDataString(project.ToLowerInvariant())}&limit=1";
        using var firstResponse = await client.GetAsync($"{path}?{query}");
        firstResponse.EnsureSuccessStatusCode();
        firstResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await firstResponse.Content.ReadAsStringAsync()).Should().NotContain("synthetic-private-evidence");
        var first = (await firstResponse.Content.ReadFromJsonAsync<SkillCanonicalTagBindingsResult>())!;
        first.SkillId.Should().Be(skill.Id);
        first.HasMore.Should().BeTrue();
        first.Items.Should().ContainSingle(item => item.Name == "Alpha" && item.TagRevision == 1);

        var second = (await client.GetFromJsonAsync<SkillCanonicalTagBindingsResult>($"{path}?{query}&offset=1"))!;
        second.Offset.Should().Be(1);
        second.HasMore.Should().BeFalse();
        second.Items.Should().ContainSingle(item => item.Name == "Bravo");

        using var missing = await client.GetAsync($"/api/skills/{Guid.NewGuid():D}/canonical-tags?{query}");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var noProject = await client.GetAsync(path);
        noProject.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var excessive = await client.GetAsync($"{path}?{query}&offset=10001");
        excessive.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

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

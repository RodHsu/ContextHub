using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.ApiContractTests;

public sealed class GraphProjectGrantApiTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Graph_and_project_suggestions_should_honor_token_grants_before_selection_and_preserve_system_scopes()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var owner = await db.TenantUsers.SingleAsync(user => user.Username == "contract-test-admin");
        var actorAccessor = services.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = new ContextHubRequestActor(owner.TenantId, owner.Id, owner.Username, owner.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.TokenManage, SecurityScopes.SecurityManage], [], true);
        var suffix = Guid.NewGuid().ToString("N");
        var allowedProject = "i-" + suffix;
        var deniedProject = "İ-" + suffix;
        var otherUser = new TenantUser { TenantId = owner.TenantId, Username = "other-" + suffix[..12], DisplayName = "other", Role = TenantUserRole.Member, Status = TenantUserStatus.Active };
        db.TenantUsers.Add(otherUser);
        // Memory ownership FK is enforced by SQL without an EF navigation dependency;
        // persist the owner before inserting its memory rows.
        await db.SaveChangesAsync(default);
        var allowed = Enumerable.Range(0, 2).Select(index => Item(allowedProject, owner.Id, "Allowed " + index, 0.1m)).ToArray();
        var denied = Enumerable.Range(0, 3).Select(index => Item(deniedProject, owner.Id, "Denied " + index, 0.99m)).ToArray();
        var otherOwned = Item(allowedProject, otherUser.Id, "Other owner's item", 0.99m);
        var shared = Item(ProjectContext.SharedProjectId, owner.Id, "Shared summary", 0.1m);
        var userPreference = Item(ProjectContext.UserProjectId, owner.Id, "User preference", 0.1m);
        db.MemoryItems.AddRange(allowed.Concat(denied).Concat([otherOwned, shared, userPreference]));
        await db.SaveChangesAsync(default);
        var token = await services.GetRequiredService<ITenantSecurityService>().CreateMyTokenAsync(
            new ApiTokenCreateRequest(owner.TenantId, owner.Id, "graph-project-grant-test", Scopes: [SecurityScopes.MemoryRead], AllowedProjectIds: [allowedProject]), default);
        var refresh = services.GetRequiredService<IDashboardMemoryGraphIndexRefreshService>();
        DashboardMemoryGraphIndexRefreshResult? refreshed = null;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            refreshed = await refresh.RefreshAsync("manual", 15, default);
            if (refreshed.Trigger == "manual") break;
            await Task.Delay(50);
        }
        refreshed!.Trigger.Should().Be("manual");
        var snapshotStore = services.GetRequiredService<IDashboardSnapshotStore>();
        await snapshotStore.SetAsync(new DashboardSnapshotEnvelope<DashboardProjectSuggestionsSnapshotPayload>(
            DashboardSnapshotKeys.DashboardProjectSuggestions, DateTimeOffset.UtcNow, 15, DateTimeOffset.UtcNow.AddSeconds(60), "",
            new([new(deniedProject, 1000), new(allowedProject, 100)])), default);

        using var client = environment.GetFactory().CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.PlainToken);
        var graph = (await client.GetFromJsonAsync<MemoryGraphResult>("/api/memories/graph?graphMode=ProjectFull&maxNodes=2"))!;
        graph.Nodes.Select(node => node.Id).Should().BeEquivalentTo(allowed.Select(item => item.Id));
        graph.Stats.Truncated.Should().BeFalse("unauthorized nodes must be removed before ranking and truncation");
        using var explicitDenied = await client.GetAsync("/api/memories/graph?projectId=" + Uri.EscapeDataString(deniedProject));
        explicitDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var includedDenied = await client.GetAsync("/api/memories/graph?projectId=" + allowedProject + "&queryMode=CurrentPlusReferencedProjects&includedProjectIds=" + Uri.EscapeDataString(deniedProject));
        includedDenied.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        var sharedGraph = (await client.GetFromJsonAsync<MemoryGraphResult>("/api/memories/graph?queryMode=SummaryOnly"))!;
        sharedGraph.Nodes.Should().ContainSingle(node => node.Id == shared.Id);
        var userGraph = (await client.GetFromJsonAsync<MemoryGraphResult>("/api/memories/graph?projectId=user"))!;
        userGraph.Nodes.Should().ContainSingle(node => node.Id == userPreference.Id);
        var suggestions = (await client.GetFromJsonAsync<List<ProjectSuggestionResult>>("/api/memories/projects?limit=1"))!;
        suggestions.Should().ContainSingle().Which.Should().Be(new ProjectSuggestionResult(allowedProject, 2));
        var deniedSuggestions = (await client.GetFromJsonAsync<List<ProjectSuggestionResult>>("/api/memories/projects?query=" + Uri.EscapeDataString(deniedProject)))!;
        deniedSuggestions.Should().BeEmpty();

        MemoryItem Item(string project, Guid itemOwner, string title, decimal importance) => new()
        {
            TenantId = owner.TenantId,
            OwnerUserId = itemOwner,
            ProjectId = project,
            Title = title,
            Summary = title,
            ExternalKey = "grant-" + Guid.NewGuid().ToString("N"),
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Status = MemoryStatus.Active,
            Importance = importance,
            Confidence = 1m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}

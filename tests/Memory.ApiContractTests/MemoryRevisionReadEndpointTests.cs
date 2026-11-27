using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.ApiContractTests;

public sealed class MemoryRevisionReadEndpointTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Memory_revision_routes_bound_metadata_and_return_only_the_requested_body_for_current_access()
    {
        var project = $"Revision-Project-{Guid.NewGuid():N}";
        var memoryId = Guid.NewGuid();
        var unrelatedMemoryId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var revisions = Enumerable.Range(1, 25).Select(version => new MemoryItemRevision
        {
            Id = Guid.NewGuid(),
            MemoryItemId = memoryId,
            Version = version,
            Title = $"Version {version}",
            Content = $"synthetic-historical-body-{version}",
            Summary = $"Summary {version}",
            CreatedAt = now.AddMinutes(version)
        }).ToList();
        var original = revisions[0];
        var duplicate = new MemoryItemRevision
        {
            MemoryItemId = memoryId,
            Version = original.Version,
            Title = "Different receipt at the same historical version",
            Content = "synthetic-distinct-same-version-body",
            CreatedAt = original.CreatedAt
        };
        revisions.Add(duplicate);

        using var factory = environment.GetFactory();
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var user = await db.TenantUsers.SingleAsync(candidate => candidate.Username == "contract-test-admin");
            db.MemoryItems.AddRange(Item(memoryId, project), Item(unrelatedMemoryId, project));
            await db.SaveChangesAsync();
            db.MemoryItemRevisions.AddRange(revisions);
            await db.SaveChangesAsync();

            var actors = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            var service = scope.ServiceProvider.GetRequiredService<IDashboardQueryService>();
            var permitted = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
                [SecurityScopes.MemoryRead], [project], true);
            actors.Current = permitted with { AllowedProjectIds = ["Another-Project"] };
            (await service.GetMemoryRevisionAsync(project, memoryId, original.Id, default)).Should().BeNull();
            (await service.GetMemoryRevisionsAsync(project, memoryId, null, null, default)).Should().BeNull();
            actors.Current = permitted with { Scopes = [] };
            (await service.GetMemoryRevisionAsync(project, memoryId, original.Id, default)).Should().BeNull();
            (await service.GetMemoryRevisionsAsync(project, memoryId, null, null, default)).Should().BeNull();
            actors.Current = permitted with { UserId = Guid.NewGuid() };
            (await service.GetMemoryRevisionAsync(project, memoryId, original.Id, default)).Should().BeNull();
            actors.Current = permitted with { TenantId = Guid.NewGuid() };
            (await service.GetMemoryRevisionAsync(project, memoryId, original.Id, default)).Should().BeNull();

            MemoryItem Item(Guid id, string projectId) => new()
            {
                Id = id,
                ExternalKey = $"revision-fixture:{id:N}",
                TenantId = user.TenantId,
                OwnerUserId = user.Id,
                ProjectId = projectId,
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Fact,
                Status = MemoryStatus.Active,
                Title = "Memory Revision Fixture",
                Content = "Synthetic current body",
                Summary = "Synthetic current summary",
                SourceType = "test",
                SourceRef = "memory-revision-read-tests",
                Version = 25,
                CreatedAt = now,
                UpdatedAt = now
            };
        }

        using var client = factory.CreateClient();
        var uri = $"/api/memories/{memoryId}/revisions";
        var scopeQuery = $"projectId={Uri.EscapeDataString(project.ToLowerInvariant())}";
        var details = await client.GetFromJsonAsync<MemoryDetailsResult>($"/api/memories/{memoryId}/details");
        details!.Document.Version.Should().Be(25);
        details.Document.Content.Should().Be("Synthetic current body");
        details.HasMoreRevisions.Should().BeTrue();
        details.Revisions.Select(revision => revision.Version).Should().Equal(Enumerable.Range(6, 20).Reverse());

        using var metadataResponse = await client.GetAsync($"{uri}?{scopeQuery}&page=2&pageSize=20");
        metadataResponse.EnsureSuccessStatusCode();
        metadataResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        var metadataJson = await metadataResponse.Content.ReadAsStringAsync();
        using var raw = JsonDocument.Parse(metadataJson);
        foreach (var revision in raw.RootElement.GetProperty("items").EnumerateArray())
        {
            revision.TryGetProperty("content", out _).Should().BeFalse();
            revision.TryGetProperty("metadataJson", out _).Should().BeFalse();
        }
        metadataJson.Should().NotContain("synthetic-historical-body-");
        var page = JsonSerializer.Deserialize<PagedResult<MemoryRevisionResult>>(metadataJson, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        page!.TotalCount.Should().Be(26);
        page.Items.Select(revision => revision.Version).Should().Equal(5, 4, 3, 2, 1, 1);
        page.Items.TakeLast(2).Select(revision => revision.Id).Should().BeEquivalentTo([original.Id, duplicate.Id]);
        var repeated = await client.GetFromJsonAsync<PagedResult<MemoryRevisionResult>>($"{uri}?{scopeQuery}&page=2&pageSize=20");
        repeated!.Items.Select(revision => revision.Id).Should().Equal(page.Items.Select(revision => revision.Id));
        var hugePage = await client.GetFromJsonAsync<PagedResult<MemoryRevisionResult>>($"{uri}?{scopeQuery}&page={int.MaxValue}&pageSize={int.MaxValue}");
        hugePage!.Items.Should().BeEmpty();
        hugePage.PageSize.Should().Be(100);

        using var bodyResponse = await client.GetAsync($"{uri}/{original.Id}?{scopeQuery}");
        bodyResponse.EnsureSuccessStatusCode();
        bodyResponse.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await bodyResponse.Content.ReadFromJsonAsync<MemoryRevisionBodyResult>();
        body!.ProjectId.Should().Be(project);
        body.Id.Should().Be(original.Id);
        body.Content.Should().Be(original.Content);
        var other = await client.GetFromJsonAsync<MemoryRevisionBodyResult>($"{uri}/{duplicate.Id}?{scopeQuery}");
        other!.Content.Should().Be(duplicate.Content);
        (await client.GetAsync($"{uri}/{original.Id}?projectId=other")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"{uri}/{Guid.NewGuid()}?{scopeQuery}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"/api/memories/{unrelatedMemoryId}/revisions/{original.Id}?{scopeQuery}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync($"{uri}?projectId=*")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync(uri)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}

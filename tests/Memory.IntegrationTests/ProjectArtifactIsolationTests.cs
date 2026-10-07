using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ProjectArtifactIsolationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Alias_list_and_by_id_reads_keep_tenant_and_owner_isolation_before_limit()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new Tenant { Slug = "artifact-own-" + suffix, DisplayName = "Owned fixture" };
        var foreignTenant = new Tenant { Slug = "artifact-foreign-" + suffix, DisplayName = "Foreign fixture" };
        var owner = new TenantUser { TenantId = tenant.Id, Username = "owner-" + suffix };
        var peer = new TenantUser { TenantId = tenant.Id, Username = "peer-" + suffix };
        var foreignOwner = new TenantUser { TenantId = foreignTenant.Id, Username = "foreign-" + suffix };
        db.Tenants.AddRange(tenant, foreignTenant);
        db.TenantUsers.AddRange(owner, peer, foreignOwner);
        await db.SaveChangesAsync();
        var project = "Tt-artifact-" + suffix;
        var query = "artifactneedle" + suffix;
        var own = Item(owner, project, query, 0);
        var peers = Enumerable.Range(1, 4).Select(index => Item(peer, project.ToUpperInvariant(), query, index)).ToArray();
        var foreign = Enumerable.Range(5, 4).Select(index => Item(foreignOwner, project.ToLowerInvariant(), query, index)).ToArray();
        db.MemoryItems.Add(own);
        db.MemoryItems.AddRange(peers.Concat(foreign));
        await db.SaveChangesAsync();
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        var actor = new ContextHubRequestActor(tenant.Id, owner.Id, owner.Username, TenantUserRole.Member,
            [SecurityScopes.MemoryRead], [project], true);
        accessor.Current = actor;
        var artifacts = services.GetRequiredService<IProjectArtifactExchangeService>();

        foreach (var role in new[] { TenantUserRole.Member, TenantUserRole.Admin })
        {
            accessor.Current = actor with { Role = role };
            foreach (var alias in new[] { project, project.ToLowerInvariant(), project.ToUpperInvariant() })
            {
                var rows = await artifacts.ListAsync(new ProjectArtifactListRequest(alias, Query: query, Limit: 1), default);
                rows.Select(row => row.MemoryId).Should().Equal(new[] { own.Id }, "foreign newer rows must be filtered before LIMIT");
                (await artifacts.GetAsync(own.Id, default))!.ProjectId.Should().Be(project);
                foreach (var hidden in peers.Concat(foreign))
                    (await artifacts.GetAsync(hidden.Id, default)).Should().BeNull("a known ID must not bypass row ownership");
            }
        }

        accessor.Current = actor with { IsServiceActor = true };
        var serviceRows = await artifacts.ListAsync(new ProjectArtifactListRequest(project, Limit: 100), default);
        serviceRows.Select(row => row.MemoryId).Should().BeEquivalentTo(peers.Select(row => row.Id).Append(own.Id));
        foreach (var hidden in foreign)
            (await artifacts.GetAsync(hidden.Id, default)).Should().BeNull("tenant service actors must remain tenant scoped");
    }

    private static MemoryItem Item(TenantUser owner, string project, string query, int order)
        => new()
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.Id,
            ProjectId = project,
            ExternalKey = Guid.NewGuid().ToString("N"),
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Artifact,
            Status = MemoryStatus.Active,
            SourceType = ProjectArtifactExchangeService.SourceType,
            SourceRef = "owned-artifact-fixture",
            Title = query,
            Summary = query,
            Content = query,
            MetadataJson = "{\"kind\":\"Snippet\",\"sourceSystem\":\"fixture\"}",
            CreatedAt = DateTimeOffset.UtcNow.AddSeconds(order),
            UpdatedAt = DateTimeOffset.UtcNow.AddSeconds(order)
        };
}

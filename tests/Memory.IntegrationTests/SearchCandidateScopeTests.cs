using FluentAssertions;
using FluentAssertions.Execution;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class SearchCandidateScopeTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Foreign_owners_and_tenants_must_not_exhaust_keyword_or_vector_candidates()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var fixture = await SeedAsync(services);
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        var store = services.GetRequiredService<IHybridSearchStore>();
        var searchScope = new MemorySearchScope([fixture.Project]);
        var vector = new EmbeddingVector(fixture.Model, 3, [1, 0, 0]);
        foreach (var role in new[] { TenantUserRole.Member, TenantUserRole.Admin })
        {
            accessor.Current = fixture.Actor with { Role = role };
            foreach (var prefix in new[] { "TT", "Tt", "tT", "tt" })
            {
                searchScope = new MemorySearchScope([prefix + fixture.Project[2..]]);
                var keyword = await store.SearchKeywordChunksAsync(fixture.Query, 3, searchScope, default);
                var semantic = await store.SearchVectorChunksAsync(vector, 3, searchScope, default);
                using var assertions = new AssertionScope();
                keyword.Select(hit => hit.MemoryId).Should().BeEquivalentTo(fixture.Mine, "keyword candidates must be scoped before LIMIT for every project alias");
                semantic.Select(hit => hit.MemoryId).Should().BeEquivalentTo(fixture.Mine, "vector candidates must be scoped before LIMIT for every project alias");
            }
        }

        var memory = services.GetRequiredService<IMemoryService>();
        var request = new MemorySearchRequest(fixture.Query, Limit: 3, ProjectId: fixture.Project);
        var cold = await memory.SearchAsync(request, default);
        cold.Select(hit => hit.MemoryId).Should().BeEquivalentTo(fixture.Mine);
        var warm = await memory.SearchAsync(request, default);
        warm.Select(hit => hit.MemoryId).Should().BeEquivalentTo(fixture.Mine);
    }

    [DockerRequiredFact]
    public async Task Tenant_service_and_background_service_must_keep_their_existing_candidate_scopes()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var fixture = await SeedAsync(services);
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        var store = services.GetRequiredService<IHybridSearchStore>();
        var searchScope = new MemorySearchScope([fixture.Project]);
        var vector = new EmbeddingVector(fixture.Model, 3, [1, 0, 0]);
        foreach (var actor in new[]
        {
            fixture.Actor with { IsServiceActor = true },
            fixture.Actor with { UserId = null, IsServiceActor = true },
            new ContextHubRequestActor(null, null, "snapshot-collector", null,
                [SecurityScopes.MemoryRead], [], true, IsServiceActor: true),
            ContextHubRequestActor.Unrestricted
        })
        {
            accessor.Current = actor;
            var expected = actor.HasUser ? fixture.Mine.Concat(fixture.SameTenant).ToArray() : fixture.All;
            var keyword = await store.SearchKeywordChunksAsync(fixture.Query, 50, searchScope, default);
            keyword.Select(hit => hit.MemoryId).Should().BeEquivalentTo(expected);
            var semantic = await store.SearchVectorChunksAsync(vector, 50, searchScope, default);
            semantic.Select(hit => hit.MemoryId).Should().BeEquivalentTo(expected);
        }
        accessor.Current = fixture.Actor;
        (await store.SearchKeywordChunksAsync(fixture.Query, 50, searchScope, default))
            .Select(hit => hit.MemoryId).Should().BeEquivalentTo(fixture.Mine);
        (await store.SearchVectorChunksAsync(vector, 50, searchScope, default))
            .Select(hit => hit.MemoryId).Should().BeEquivalentTo(fixture.Mine);
        (await store.SearchKeywordChunksAsync(fixture.Query, 50, new MemorySearchScope([fixture.Project + "-absent"]), default))
            .Should().BeEmpty();
        (await store.SearchVectorChunksAsync(vector, 50, new MemorySearchScope([fixture.Project + "-absent"]), default))
            .Should().BeEmpty();
    }

    [DockerRequiredFact]
    public async Task Authenticated_non_service_reads_with_missing_identity_must_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var services = scope.ServiceProvider;
        var accessor = services.GetRequiredService<IRequestActorAccessor>();
        var store = services.GetRequiredService<IHybridSearchStore>();
        foreach (var identity in new[] { (Tenant: (Guid?)null, User: (Guid?)null), (Tenant: (Guid?)Guid.NewGuid(), User: (Guid?)null), (Tenant: (Guid?)null, User: (Guid?)Guid.NewGuid()) })
        {
            accessor.Current = new ContextHubRequestActor(identity.Tenant, identity.User, "invalid", null,
                [SecurityScopes.MemoryRead], [], true);
            var keyword = () => store.SearchKeywordChunksAsync("query", 3, MemorySearchScope.Unscoped, default);
            await keyword.Should().ThrowAsync<UnauthorizedAccessException>();
            var vector = () => store.SearchVectorChunksAsync(new EmbeddingVector("fixture", 3, [1, 0, 0]), 3, MemorySearchScope.Unscoped, default);
            await vector.Should().ThrowAsync<UnauthorizedAccessException>();
        }
    }

    private static async Task<Fixture> SeedAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var tenant = new Tenant { Slug = "candidate-" + suffix, DisplayName = "Candidate scope" };
        var otherTenant = new Tenant { Slug = "foreign-" + suffix, DisplayName = "Other tenant" };
        var owner = new TenantUser { TenantId = tenant.Id, Username = "owner-" + suffix };
        var otherOwner = new TenantUser { TenantId = tenant.Id, Username = "other-" + suffix };
        var foreignOwner = new TenantUser { TenantId = otherTenant.Id, Username = "foreign-" + suffix };
        db.Tenants.AddRange(tenant, otherTenant);
        db.TenantUsers.AddRange(owner, otherOwner, foreignOwner);
        await db.SaveChangesAsync();
        var project = "TT-candidate-" + suffix;
        var query = "needle" + suffix;
        var model = "candidate-" + suffix;
        var actor = new ContextHubRequestActor(tenant.Id, owner.Id, owner.Username, TenantUserRole.Member,
            [SecurityScopes.MemoryRead], [project], true);
        var groups = new List<Guid[]>();
        var chunks = new List<(MemoryItemChunk Chunk, bool Mine)>();
        foreach (var user in new[] { owner, otherOwner, foreignOwner })
        {
            var mine = user == owner;
            var ids = new List<Guid>();
            for (var index = 0; index < (mine ? 3 : 15); index++)
            {
                var item = new MemoryItem
                {
                    TenantId = user.TenantId,
                    OwnerUserId = user.Id,
                    ProjectId = project,
                    ExternalKey = Guid.NewGuid().ToString("N"),
                    Title = "Candidate " + index,
                    Content = query,
                    Summary = query,
                    Scope = MemoryScope.Project,
                    MemoryType = MemoryType.Fact,
                    Status = MemoryStatus.Active,
                    CreatedAt = DateTimeOffset.UtcNow,
                    UpdatedAt = DateTimeOffset.UtcNow
                };
                var chunk = new MemoryItemChunk
                {
                    MemoryItemId = item.Id,
                    ChunkText = string.Join(' ', Enumerable.Repeat(query, mine ? 1 : 20)),
                    ChunkKind = ChunkKind.Document,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.MemoryItems.Add(item);
                db.MemoryItemChunks.Add(chunk);
                chunks.Add((chunk, mine));
                ids.Add(item.Id);
            }
            groups.Add(ids.ToArray());
        }
        await db.SaveChangesAsync();
        var vectors = services.GetRequiredService<IVectorStore>();
        foreach (var (chunk, mine) in chunks)
        {
            await vectors.ReplaceChunkVectorAsync(chunk.Id,
                new EmbeddingVector(model, 3, mine ? [0.8f, 0.6f, 0] : [1, 0, 0]), default);
        }
        return new Fixture(actor, project, query, model, groups[0], groups[1], groups.SelectMany(ids => ids).ToArray());
    }

    private sealed record Fixture(ContextHubRequestActor Actor, string Project, string Query, string Model,
        Guid[] Mine, Guid[] SameTenant, Guid[] All);
}

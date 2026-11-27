using System.Diagnostics;
using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Memory.IntegrationTests;

public sealed class CanonicalTagScaleTests(ContainerTestEnvironment environment, ITestOutputHelper output)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Scoped_skill_tag_lookup_and_replay_at_registry_scale()
    {
        const int registrySize = 2_101;
        const int samples = 12;
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var project = $"tag-scale-{suffix}";
        var adjacentProject = project + "-adjacent";
        var canonical = $"database-{suffix}";
        var alias = $"db-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var services = environment.GetFactory().Services;
        using var fixture = services.CreateScope();
        var db = fixture.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.AsNoTracking().SingleAsync(item => item.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.SkillsRead, SecurityScopes.SkillsExecute,
             SecurityScopes.SkillsManage, SecurityScopes.SkillsPublish], [project], true);
        var actors = fixture.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actors.Current = actor;
        var skills = fixture.ServiceProvider.GetRequiredService<ISkillService>();
        var matched = await PublishAsync(skills, $"match-{suffix}");
        var unranked = await PublishAsync(skills, $"neutral-{suffix}");
        var otherTenant = new Tenant { Slug = $"tag-fixture-{suffix}", DisplayName = "Synthetic tenant", CreatedAt = now, UpdatedAt = now };
        var otherOwner = NewOwner(user.TenantId, $"owner-{suffix}");
        var tenantOwner = NewOwner(otherTenant.Id, $"tenant-owner-{suffix}");
        db.Tenants.Add(otherTenant);
        db.TenantUsers.AddRange(otherOwner, tenantOwner);
        await db.SaveChangesAsync();

        var active = Definition(canonical, project, user.TenantId, user.Id);
        var adjacent = Definition(canonical, adjacentProject, user.TenantId, user.Id);
        var foreignOwner = Definition(canonical, project, otherOwner.TenantId, otherOwner.Id);
        var foreignTenant = Definition(canonical, project, tenantOwner.TenantId, tenantOwner.Id);
        var noise = Enumerable.Range(1, registrySize - 1).Select(index =>
            Definition($"database-{suffix}-noise-{index}", project, user.TenantId, user.Id)).ToArray();
        db.CanonicalTagDefinitions.AddRange(active, adjacent, foreignOwner, foreignTenant);
        db.CanonicalTagDefinitions.AddRange(noise);
        await db.SaveChangesAsync();
        db.CanonicalTagAliases.AddRange(new[] { active, adjacent, foreignOwner, foreignTenant }.Select(item => Alias(item, alias)));
        db.CanonicalTagAliases.AddRange(noise.Select((item, index) => Alias(item, $"db-{suffix}-noise-{index + 1}")));
        db.CanonicalTagBindings.Add(Binding(active, matched.Skill.Id));
        db.CanonicalTagBindings.AddRange(new[] { adjacent, foreignOwner, foreignTenant }.Select(item => Binding(item, unranked.Skill.Id)));
        await db.SaveChangesAsync();

        // Remove incidental keyword and embedding contributions so the assertions prove the tag score.
        var fixtureVersions = new[] { matched.Version.Id, unranked.Version.Id };
        await db.SkillSearchDocuments.Where(document => fixtureVersions.Contains(document.SkillVersionId))
            .ExecuteUpdateAsync(update => update.SetProperty(document => document.TermsJson, "[]")
                .SetProperty(document => document.EmbeddingJson, "[]"));
        (await db.CanonicalTagDefinitions.CountAsync(item => item.TenantId == user.TenantId &&
            item.OwnerUserId == user.Id && item.ProjectId == project)).Should().Be(registrySize);
        (await db.CanonicalTagAliases.CountAsync(item => item.TenantId == user.TenantId &&
            item.OwnerUserId == user.Id && item.ProjectId == project)).Should().Be(registrySize);

        var request = Request(project, [alias]);
        AssertRanking(await SearchAsync(request, actor));
        var elapsed = new List<double>();
        for (var index = 0; index < samples; index++)
        {
            var terms = (index % 3) switch
            {
                0 => new[] { canonical },
                1 => new[] { alias.ToUpperInvariant().Replace('-', '_') },
                _ => new[] { canonical, alias, alias.ToUpperInvariant() }
            };
            var fresh = Request(project.ToUpperInvariant(), terms);
            var stopwatch = Stopwatch.StartNew();
            var result = await SearchAsync(fresh, actor);
            elapsed.Add(stopwatch.Elapsed.TotalMilliseconds);
            AssertRanking(result);
            result.Replayed.Should().BeFalse();
        }
        var ordered = elapsed.Order().ToArray();
        output.WriteLine(FormattableString.Invariant(
            $"PostgreSQL Skill search, {registrySize} scoped definitions and aliases, {samples} fresh calls after warm-up: median {(ordered[5] + ordered[6]) / 2:F2} ms, p95 {ordered[(int)Math.Ceiling(samples * 0.95) - 1]:F2} ms. Includes request scope, query and outcome persistence."));

        var concurrent = Request(project, [alias]);
        var copies = await Task.WhenAll(SearchAsync(concurrent, actor),
            SearchAsync(concurrent with { ProjectId = project.ToUpperInvariant() }, actor));
        foreach (var copy in copies) AssertRanking(copy);
        copies.Select(item => item.ResolutionId).Distinct().Should().ContainSingle();
        copies.Count(item => item.Replayed).Should().Be(1);
        (await db.SkillResolutions.CountAsync(item => item.ExecutionId == concurrent.ExecutionId)).Should().Be(1);
        (await db.SkillTelemetryEvents.CountAsync(item => item.ResolutionId == copies[0].ResolutionId &&
            item.SkillId == matched.Skill.Id && item.EventType == SkillTelemetryEventType.SearchImpression)).Should().Be(1);

        var adjacentOnly = actor with { AllowedProjectIds = [adjacentProject] };
        var adjacentResult = await SearchAsync(Request(adjacentProject, [alias]), adjacentOnly);
        adjacentResult.Candidates.Should().ContainSingle(item => item.Rank == 1 && item.SkillId == unranked.Skill.Id && item.Score == 0.10m);
        var mismatchedReplay = () => SearchAsync(concurrent with { ProjectId = adjacentProject }, adjacentOnly);
        await mismatchedReplay.Should().ThrowAsync<UnauthorizedAccessException>();
        var deniedProject = () => SearchAsync(concurrent, adjacentOnly);
        await deniedProject.Should().ThrowAsync<UnauthorizedAccessException>();

        async Task<SkillSearchForExecutionResult> SearchAsync(SkillSearchForExecutionRequest input, ContextHubRequestActor requestActor)
        {
            await using var scope = services.CreateAsyncScope();
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = requestActor;
            return await scope.ServiceProvider.GetRequiredService<ISkillService>().SearchForExecutionAsync(input, default);
        }

        void AssertRanking(SkillSearchForExecutionResult result)
        {
            result.Candidates.Should().HaveCount(2);
            var first = result.Candidates.Should().ContainSingle(item => item.SkillId == matched.Skill.Id).Subject;
            first.Rank.Should().Be(1);
            first.Score.Should().Be(0.10m);
            first.MatchReasons.Should().Equal("tags", "canonical-tags");
            var fallback = result.Candidates.Should().ContainSingle(item => item.SkillId == unranked.Skill.Id).Subject;
            fallback.Rank.Should().Be(2);
            fallback.Score.Should().Be(0m);
            fallback.MatchReasons.Should().NotContain("canonical-tags");
        }

        TenantUser NewOwner(Guid tenantId, string username) => new()
        {
            TenantId = tenantId,
            Username = username,
            DisplayName = "Synthetic owner",
            Role = TenantUserRole.Owner,
            CreatedAt = now,
            UpdatedAt = now
        };

        CanonicalTagDefinition Definition(string name, string scope, Guid tenantId, Guid ownerId) => new()
        {
            TenantId = tenantId,
            OwnerUserId = ownerId,
            ProjectId = scope,
            CanonicalName = name,
            NormalizedName = name,
            CreatedAt = now,
            UpdatedAt = now
        };

        CanonicalTagBinding Binding(CanonicalTagDefinition item, Guid skillId) => new()
        {
            TenantId = item.TenantId,
            OwnerUserId = item.OwnerUserId,
            ProjectId = item.ProjectId,
            DefinitionId = item.Id,
            ResourceType = "Skill",
            ResourceId = skillId.ToString("D"),
            CreatedAt = now
        };
    }

    private static SkillSearchForExecutionRequest Request(string project, IReadOnlyList<string> tags)
        => new(Guid.NewGuid(), null, project, "syntheticrepo", "syntheticagent", "unmatchedgoal",
            [], tags, [], [], [], [], new SkillSearchPolicy(TopN: 5, Threshold: 0m), $"search-{Guid.NewGuid():N}");

    private static CanonicalTagAlias Alias(CanonicalTagDefinition item, string name) => new()
    {
        TenantId = item.TenantId,
        OwnerUserId = item.OwnerUserId,
        ProjectId = item.ProjectId,
        DefinitionId = item.Id,
        Alias = name,
        NormalizedAlias = name
    };

    private static async Task<SkillImportResult> PublishAsync(ISkillService service, string key)
    {
        const string markdown = "---\nname: Synthetic fixture\ndescription: Synthetic fixture\n---\n# Synthetic fixture";
        var bundle = new PortableSkillBundle([new PortableSkillFile("SKILL.md", Convert.ToBase64String(Encoding.UTF8.GetBytes(markdown)))]);
        var preview = new SkillImportPreviewRequest(key, key, "Synthetic fixture", "Use bounded evidence", "1.0.0",
            bundle, SkillSourceKind.Repository, $"https://example.test/{key}", "fixture", "MIT", TrustLevel: SkillTrustLevel.SourceVerified);
        var imported = await service.ImportAsync(new(preview, $"import-{key}"), default);
        await service.PublishAsync(new(imported.Version.Id, imported.Version.ContentHash, false, $"publish-{key}"), default);
        return imported;
    }
}

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

namespace Memory.IntegrationTests;

public sealed class MemoryManifestIntegrationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Full_frozen_inventory_survives_live_changes_timeout_replay_and_new_service_instance()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var (db, actor, _, project) = Setup(scope.ServiceProvider);
        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow.AddTicks(7) };
        clock.UtcNow = clock.UtcNow.AddTicks(7 - clock.UtcNow.Ticks % 10);
        var service = new MemoryManifestService(db, scope.ServiceProvider.GetRequiredService<IGovernanceProjectScopeResolver>(),
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(), clock);
        var now = DateTimeOffset.UtcNow;
        var rows = Enumerable.Range(0, 3281).Select(i => new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = i % 5 == 0 ? "shared" : project,
            ExternalKey = "manifest-fixture-" + Guid.NewGuid(),
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Status = (MemoryStatus)(i % 4),
            Version = 1,
            CreatedAt = now,
            UpdatedAt = now,
            Content = "stopped-fixture-content-must-never-be-returned",
            Summary = "private-fixture-summary",
            SourceRef = "private-fixture-locator",
            SourceType = "private-fixture-source",
            MetadataJson = "{\"private\":\"fixture\"}"
        }).ToArray();
        rows[0].ExternalKey = DurableMemoryGovernancePolicy.ProjectInformationExternalKey;
        rows[0].Content = rows[0].Summary = "";
        db.MemoryItems.AddRange(rows);
        await db.SaveChangesAsync();
        var expected = await db.MemoryItems.AsNoTracking().ForActor(actor)
            .Where(x => x.ProjectId == project || x.ProjectId == "shared").Select(x => x.Id).ToListAsync();
        var expectedMetadata = await db.MemoryItems.AsNoTracking().ForActor(actor)
            .Where(x => x.ProjectId == project || x.ProjectId == "shared")
            .Select(x => new { MemoryId = x.Id, x.ProjectId, x.Version, x.Status, x.UpdatedAt }).ToListAsync();
        var businessBefore = await BusinessCounts(db);
        var request = new MemoryManifestReadRequest(project, Guid.NewGuid(), PageSize: 73, AllAuthorizedProjects: false);
        var first = await service.ReadAsync(request, default);
        first.AuthorizedTotal.Should().Be(expected.Count);
        var changed = rows.First(x => x.ProjectId == project);
        changed.Version++;
        changed.UpdatedAt = now.AddSeconds(1);
        var archived = rows.Last(x => x.ProjectId == project);
        archived.Status = MemoryStatus.Archived;
        archived.UpdatedAt = now.AddSeconds(2);
        var moved = rows.Skip(2).First(x => x.ProjectId == project && x.Id != changed.Id && x.Id != archived.Id);
        moved.ProjectId = project + "-moved";
        var inserted = new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = project,
            ExternalKey = Guid.NewGuid().ToString(),
            CreatedAt = now,
            UpdatedAt = now,
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact
        };
        db.MemoryItems.Add(inserted);
        await db.SaveChangesAsync();
        var retry = await service.ReadAsync(request, default);
        retry.Should().BeEquivalentTo(first);
        var ids = first.Items.Select(x => x.MemoryId).ToList();
        var entries = first.Items.ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await service.ReadAsync(request with { SnapshotId = first.SnapshotId, Cursor = cursor }, default);
            page.CountInvariantSatisfied.Should().BeTrue();
            page.AuthorizedTotal.Should().Be(first.AuthorizedTotal);
            // A lost continuation response is retried with the exact same cursor.
            (await service.ReadAsync(request with { SnapshotId = first.SnapshotId, Cursor = cursor }, default))
                .Should().BeEquivalentTo(page);
            ids.AddRange(page.Items.Select(x => x.MemoryId));
            entries.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        ids.Order().Should().Equal(expected.Order());
        ids.Distinct().Should().HaveCount(expected.Count);
        entries.Select(x => new { x.MemoryId, x.ProjectId, x.Version, x.Status, x.UpdatedAt })
            .Should().BeEquivalentTo(expectedMetadata);
        var serialized = JsonSerializer.Serialize(first);
        serialized.Should().NotContain("stopped-fixture-content").And.NotContain("private-fixture");
        var verification = await service.VerifyAsync(new(project, first.SnapshotId, [changed.Id, archived.Id, moved.Id]), default);
        verification.Single(x => x.MemoryId == changed.Id).State.Should().Be("Changed");
        verification.Single(x => x.MemoryId == archived.Id).State.Should().Be("Changed");
        verification.Single(x => x.MemoryId == moved.Id).State.Should().Be("RemovedOrMoved");
        var recreated = new MemoryManifestService(db, scope.ServiceProvider.GetRequiredService<IGovernanceProjectScopeResolver>(),
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(), scope.ServiceProvider.GetRequiredService<IClock>());
        (await recreated.ReadAsync(request, default)).Should().BeEquivalentTo(first);
        var next = await recreated.ReadAsync(request with { RequestId = Guid.NewGuid() }, default);
        next.SnapshotId.Should().NotBe(first.SnapshotId);
        next.AuthorizedTotal.Should().Be(first.AuthorizedTotal);
        var freshChanged = await recreated.VerifyAsync(new(project, next.SnapshotId, [changed.Id, archived.Id, inserted.Id]), default);
        freshChanged.Should().OnlyContain(x => x.State == "Unchanged");
        freshChanged.Single(x => x.MemoryId == changed.Id).CurrentVersion.Should().Be(changed.Version);
        var movedNotInNew = () => recreated.VerifyAsync(new(project, next.SnapshotId, [moved.Id]), default);
        (await movedNotInNew.Should().ThrowAsync<MemoryManifestException>()).Which.Code.Should().Be("memory-not-in-snapshot");
        (await BusinessCounts(db)).Should().BeEquivalentTo(businessBefore with { Memories = businessBefore.Memories + 1 });
        // A read-only freeze must never become an executable review or a tracker exclusion authority.
        var stored = await db.KnowledgeGovernanceSnapshots.AsNoTracking().SingleAsync(x => x.Id == first.SnapshotId);
        var durable = scope.ServiceProvider.GetRequiredService<IDurableMemoryGovernanceService>();
        var executeRead = () => durable.GetSnapshotAsync(stored.GovernanceRunId, "manifest:" + stored.Id.ToString("N"), false, true, default);
        await executeRead.Should().ThrowAsync<GovernanceBatchException>();
        var analyze = () => durable.GetOrCreateSnapshotAsync([project], stored.GovernanceRunId, false, default);
        await analyze.Should().ThrowAsync<InvalidOperationException>();
        await environment.RestartApplicationAsync();
        using var restartedScope = environment.GetFactory().Services.CreateScope();
        var restartedAccessor = restartedScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        restartedAccessor.Current = actor;
        var restartedService = restartedScope.ServiceProvider.GetRequiredService<IMemoryManifestService>();
        (await restartedService.ReadAsync(request, default)).Should().BeEquivalentTo(first);
    }

    [DockerRequiredFact]
    public async Task Case_alias_inventory_and_verification_use_existing_identity_without_crossing_actor_boundaries()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var (db, actor, service, project) = Setup(scope.ServiceProvider);
        var aliases = new[] { project, project.ToUpperInvariant(), "Manifest-" + project[9..], "MANIFEST-" + project[9..] };
        var tenant = new Tenant { Slug = "manifest-" + Guid.NewGuid().ToString("N"), DisplayName = "Manifest fixture isolation" };
        var otherOwner = new TenantUser { TenantId = actor.TenantId!.Value, Username = "manifest-owner-" + Guid.NewGuid().ToString("N") };
        var foreignOwner = new TenantUser { TenantId = tenant.Id, Username = "manifest-foreign-" + Guid.NewGuid().ToString("N") };
        db.Tenants.Add(tenant);
        db.TenantUsers.AddRange(otherOwner, foreignOwner);
        await db.SaveChangesAsync();
        var now = DateTimeOffset.UtcNow;
        var rows = aliases.Select(alias => new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = alias,
            ExternalKey = Guid.NewGuid().ToString(),
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Status = MemoryStatus.Active,
            Version = 3,
            CreatedAt = now,
            UpdatedAt = now
        }).ToArray();
        db.MemoryItems.AddRange(rows);
        db.MemoryItems.Add(new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = otherOwner.Id,
            ProjectId = aliases[1],
            ExternalKey = Guid.NewGuid().ToString(),
            CreatedAt = now,
            UpdatedAt = now
        });
        db.MemoryItems.Add(new MemoryItem
        {
            TenantId = tenant.Id,
            OwnerUserId = foreignOwner.Id,
            ProjectId = aliases[0],
            ExternalKey = Guid.NewGuid().ToString(),
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var request = new MemoryManifestReadRequest(aliases[2], Guid.NewGuid(), PageSize: 2, AllAuthorizedProjects: false);
        var first = await service.ReadAsync(request, default);
        var entries = first.Items.ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await service.ReadAsync(request with { SnapshotId = first.SnapshotId, Cursor = cursor }, default);
            entries.AddRange(page.Items);
            cursor = page.NextCursor;
        }
        entries.Where(x => ProjectContext.Matches(x.ProjectId, project)).Select(x => x.MemoryId)
            .Should().BeEquivalentTo(rows.Select(x => x.Id));
        entries.Where(x => ProjectContext.Matches(x.ProjectId, project)).Select(x => x.ProjectId)
            .Should().BeEquivalentTo(aliases);
        var verified = await service.VerifyAsync(new(aliases[1], first.SnapshotId, rows.Select(x => x.Id).ToArray()), default);
        verified.Should().OnlyContain(x => x.State == "Unchanged" && x.CurrentVersion == 3);
        db.TenantProjectGrants.Add(new TenantProjectGrant { TenantId = actor.TenantId!.Value, ProjectId = aliases[1], CanRead = false });
        await db.SaveChangesAsync();
        var revoked = () => service.ReadAsync(request with { SnapshotId = first.SnapshotId }, default);
        (await revoked.Should().ThrowAsync<MemoryManifestException>()).Which.Code.Should().Be("manifest-access-changed");
    }

    [DockerRequiredFact]
    public async Task Tenant_owner_token_scope_and_durable_grant_changes_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var (db, actor, service, project) = Setup(scope.ServiceProvider);
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var request = new MemoryManifestReadRequest(project, Guid.NewGuid(), AllAuthorizedProjects: false);
        var first = await service.ReadAsync(request, default);
        foreach (var other in new[] { actor with { UserId = Guid.NewGuid() }, actor with { TenantId = Guid.NewGuid() } })
        {
            accessor.Current = other;
            var denied = () => service.ReadAsync(request with { SnapshotId = first.SnapshotId }, default);
            (await denied.Should().ThrowAsync<MemoryManifestException>()).Which.Code.Should().Be("manifest-not-found");
        }
        accessor.Current = actor with { AllowedProjectIds = [project] };
        var tokenChange = () => service.ReadAsync(request, default);
        (await tokenChange.Should().ThrowAsync<MemoryManifestException>()).Which.Code.Should().Be("manifest-access-changed");
        accessor.Current = actor with { Scopes = [] };
        var revoked = () => service.ReadAsync(request, default);
        await revoked.Should().ThrowAsync<UnauthorizedAccessException>();
        accessor.Current = actor;
        db.TenantProjectGrants.Add(new TenantProjectGrant { TenantId = actor.TenantId!.Value, ProjectId = project, CanRead = false });
        await db.SaveChangesAsync();
        var grantRevoked = () => service.ReadAsync(request, default);
        (await grantRevoked.Should().ThrowAsync<MemoryManifestException>()).Which.Code.Should().Be("manifest-access-changed");
    }

    [DockerRequiredFact]
    public async Task Legacy_snapshot_is_not_backfilled_and_expired_snapshot_is_not_refrozen()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var (db, actor, _, project) = Setup(scope.ServiceProvider);
        var clock = new TestClock { UtcNow = DateTimeOffset.UtcNow };
        var service = new MemoryManifestService(db, scope.ServiceProvider.GetRequiredService<IGovernanceProjectScopeResolver>(),
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>(), clock);
        var request = new MemoryManifestReadRequest(project, Guid.NewGuid(), AllAuthorizedProjects: false);
        var first = await service.ReadAsync(request, default);
        var legacy = new KnowledgeGovernanceSnapshot
        {
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            GovernanceRunId = Guid.NewGuid().ToString(),
            ProjectIdsJson = "[]",
            ResultJson = "{\"coverage\":null,\"projectCandidates\":[],\"sharedCandidates\":[]}",
            CreatedAt = clock.UtcNow,
            CompletedAt = clock.UtcNow
        };
        db.KnowledgeGovernanceSnapshots.Add(legacy);
        await db.SaveChangesAsync();
        var before = await db.KnowledgeGovernanceSnapshots.CountAsync();
        var unavailable = () => service.ReadAsync(request with { SnapshotId = legacy.Id }, default);
        (await unavailable.Should().ThrowAsync<MemoryManifestException>()).Which.Code.Should().Be("legacy-manifest-unavailable");
        clock.UtcNow = first.ExpiresAt;
        var expired = () => service.ReadAsync(request, default);
        (await expired.Should().ThrowAsync<MemoryManifestException>()).Which.Code.Should().Be("manifest-expired");
        (await db.KnowledgeGovernanceSnapshots.CountAsync()).Should().Be(before);
    }

    [DockerRequiredFact]
    public async Task Authenticated_rest_contract_returns_metadata_and_explicit_conflicts()
    {
        using var client = environment.GetFactory().CreateClient();
        var request = new MemoryManifestReadRequest("ContextHub", Guid.NewGuid(), AllAuthorizedProjects: false);
        var response = await client.PostAsJsonAsync("/api/memories/manifest/read", request);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = (await response.Content.ReadFromJsonAsync<MemoryManifestPage>())!;
        var replay = await client.PostAsJsonAsync("/api/memories/manifest/read", request);
        (await replay.Content.ReadFromJsonAsync<MemoryManifestPage>()).Should().BeEquivalentTo(page);
        var invalid = await client.PostAsJsonAsync("/api/memories/manifest/read", request with { SnapshotId = page.SnapshotId, Cursor = "malformed" });
        invalid.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await invalid.Content.ReadAsStringAsync()).Should().Contain("invalid-manifest-cursor");
        client.DefaultRequestHeaders.Authorization = null;
        (await client.PostAsJsonAsync("/api/memories/manifest/read", request)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static (MemoryDbContext, ContextHubRequestActor, IMemoryManifestService, string) Setup(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return (db, actor, services.GetRequiredService<IMemoryManifestService>(), "manifest-" + Guid.NewGuid().ToString("N"));
    }

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } }
    private sealed record Counts(int Memories, int Findings, int Actions, int Jobs, int Receipts);
    private static async Task<Counts> BusinessCounts(MemoryDbContext db)
        => new(await db.MemoryItems.CountAsync(), await db.GovernanceFindings.CountAsync(),
            await db.SuggestedActions.CountAsync(), await db.MemoryJobs.CountAsync(), await db.GovernanceRunReceipts.CountAsync());
}

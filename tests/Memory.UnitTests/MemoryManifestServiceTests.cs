using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Memory.UnitTests;

public sealed class MemoryManifestServiceTests
{
    [Fact]
    public async Task Application_freeze_and_continuation_use_persisted_members_without_business_mutation()
    {
        await using var fixture = new Fixture();
        var rows = Enumerable.Range(0, 3281).Select(i => fixture.Item(i % 3 == 0 ? "shared" : "TT", (MemoryStatus)(i % 4))).ToArray();
        fixture.Db.MemoryItems.AddRange(rows);
        await fixture.Db.SaveChangesAsync();
        var request = new MemoryManifestReadRequest("TT", Guid.NewGuid(), PageSize: 81, AllAuthorizedProjects: false);
        var first = await fixture.Service.ReadAsync(request, default);
        first.AuthorizedTotal.Should().Be(3281);
        rows[1].Version++;
        rows[2].ProjectId = "moved";
        rows[4].Status = MemoryStatus.Archived;
        rows[4].UpdatedAt = rows[4].UpdatedAt.AddSeconds(1);
        var added = fixture.Item("TT", MemoryStatus.Active);
        fixture.Db.MemoryItems.Add(added);
        await fixture.Db.SaveChangesAsync();
        (await fixture.Service.ReadAsync(request, default)).Should().BeEquivalentTo(first);
        var ids = first.Items.Select(x => x.MemoryId).ToList();
        var cursor = first.NextCursor;
        while (cursor is not null)
        {
            var page = await fixture.Service.ReadAsync(request with { SnapshotId = first.SnapshotId, Cursor = cursor }, default);
            ids.AddRange(page.Items.Select(x => x.MemoryId));
            cursor = page.NextCursor;
        }
        ids.Order().Should().Equal(rows.Select(x => x.Id).Order());
        ids.Distinct().Should().HaveCount(3281);
        var check = await fixture.Service.VerifyAsync(new("TT", first.SnapshotId, [rows[1].Id, rows[2].Id, rows[4].Id]), default);
        check.Select(x => x.State).Should().Equal("Changed", "RemovedOrMoved", "Changed");
        var newSnapshot = await fixture.Service.ReadAsync(request with { RequestId = Guid.NewGuid() }, default);
        newSnapshot.SnapshotId.Should().NotBe(first.SnapshotId);
        // Every non-manifest business DbSet on this fixture rejects access.
        fixture.Db.KnowledgeGovernanceSnapshots.Should().HaveCount(2);
        JsonSerializer.Serialize(first).Should().NotContain("stopped-body-fixture").And.NotContain("raw-locator-fixture");
    }

    [Fact]
    public async Task Permission_changes_expiry_invalid_replay_and_legacy_snapshots_fail_closed()
    {
        await using var fixture = new Fixture();
        var request = new MemoryManifestReadRequest("TT", Guid.NewGuid(), AllAuthorizedProjects: false);
        var page = await fixture.Service.ReadAsync(request, default);
        foreach (var actor in new[]
        {
            fixture.Actor with { TenantId = Guid.NewGuid() }, fixture.Actor with { UserId = Guid.NewGuid() }
        })
        {
            fixture.Accessor.Current = actor;
            await Reject(() => fixture.Service.ReadAsync(request with { SnapshotId = page.SnapshotId }, default), "manifest-not-found");
        }
        fixture.Accessor.Current = fixture.Actor with { AllowedProjectIds = ["TT"] };
        await Reject(() => fixture.Service.ReadAsync(request, default), "manifest-access-changed");
        fixture.Accessor.Current = fixture.Actor;
        fixture.Db.TenantProjectGrants.Add(new TenantProjectGrant { TenantId = fixture.Actor.TenantId!.Value, ProjectId = "TT", CanRead = false });
        await fixture.Db.SaveChangesAsync();
        await Reject(() => fixture.Service.ReadAsync(request, default), "manifest-access-changed");
        fixture.Db.TenantProjectGrants.Single().CanRead = true;
        await fixture.Db.SaveChangesAsync();
        await Reject(() => fixture.Service.ReadAsync(request, default), "manifest-access-changed");
        request = request with { RequestId = Guid.NewGuid() };
        page = await fixture.Service.ReadAsync(request, default);
        await Reject(() => fixture.Service.ReadAsync(request with { IncludedProjectIds = ["Other"] }, default), "manifest-request-mismatch");
        await Reject(() => fixture.Service.VerifyAsync(new("TT", page.SnapshotId, [Guid.NewGuid()]), default), "memory-not-in-snapshot");
        var row = fixture.Db.KnowledgeGovernanceSnapshots.Single(x => x.Id == page.SnapshotId);
        var envelope = JsonSerializer.Deserialize<DurableMemoryGovernanceSnapshotResult>(row.ResultJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        row.ResultJson = JsonSerializer.Serialize(envelope with { ReadManifest = envelope.ReadManifest! with { ScopeContractVersion = "different-contract" } },
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await fixture.Db.SaveChangesAsync();
        await Reject(() => fixture.Service.ReadAsync(request, default), "manifest-scope-contract-changed");
        row.ResultJson = JsonSerializer.Serialize(envelope with { ReadManifest = null }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await fixture.Db.SaveChangesAsync();
        await Reject(() => fixture.Service.ReadAsync(request, default), "legacy-manifest-unavailable");
        row.ResultJson = JsonSerializer.Serialize(envelope, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await fixture.Db.SaveChangesAsync();
        fixture.Clock.UtcNow = page.ExpiresAt;
        await Reject(() => fixture.Service.ReadAsync(request, default), "manifest-expired");
        fixture.Db.KnowledgeGovernanceSnapshots.Should().HaveCount(2);
    }

    [Fact]
    public async Task All_authorized_projects_include_hidden_archived_default_and_shared_but_exclude_user_and_denied()
    {
        await using var fixture = new Fixture();
        foreach (var project in new[] { "TT", "hidden", "archived", "default", "legacy", "shared", "user", "denied" })
            fixture.Db.MemoryItems.Add(fixture.Item(project, MemoryStatus.Archived));
        var foreign = fixture.Item("TT", MemoryStatus.Active);
        foreign.OwnerUserId = Guid.NewGuid();
        fixture.Db.MemoryItems.Add(foreign);
        fixture.Db.TenantProjectGrants.Add(new TenantProjectGrant { TenantId = fixture.Actor.TenantId!.Value, ProjectId = "denied", CanRead = false });
        await fixture.Db.SaveChangesAsync();
        var page = await fixture.Service.ReadAsync(new("TT", Guid.NewGuid()), default);
        page.Items.Select(x => x.ProjectId).Should().BeEquivalentTo("TT", "hidden", "archived", "default", "legacy", "shared");
        page.AuthorizedTotal.Should().Be(6);
    }

    [Fact]
    public async Task Freeze_rejects_pending_business_changes_without_saving_or_discarding_them()
    {
        await using var fixture = new Fixture();
        var pending = fixture.Item("TT", MemoryStatus.Active);
        fixture.Db.MemoryItems.Add(pending);
        await Reject(() => fixture.Service.ReadAsync(new("TT", Guid.NewGuid()), default), "manifest-context-not-clean");
        fixture.Db.Entry(pending).State.Should().Be(EntityState.Added);
        (await fixture.Db.MemoryItems.CountAsync()).Should().Be(0);
        (await fixture.Db.KnowledgeGovernanceSnapshots.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task System_non_retrieval_classification_matches_governance_whitespace_policy()
    {
        await using var fixture = new Fixture();
        var row = fixture.Item("TT", MemoryStatus.Active);
        row.ExternalKey = DurableMemoryGovernancePolicy.ProjectInformationExternalKey;
        row.Content = "\t\r\n\u00a0\u3000";
        row.Summary = "\u2007\u202f";
        fixture.Db.MemoryItems.Add(row);
        await fixture.Db.SaveChangesAsync();
        var page = await fixture.Service.ReadAsync(new("TT", Guid.NewGuid(), AllAuthorizedProjects: false), default);
        page.Items.Single().Classification.Should().Be("SystemNonRetrieval");
        page.PartitionCounts["classification:SystemNonRetrieval"].Should().Be(1);
    }

    [Fact]
    public void Npgsql_projection_translates_metadata_and_filtered_verification_without_body_materialization()
    {
        using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=unused", options => options.UseVector()).Options);
        var accessor = new RequestActorAccessor { Current = ContextHubRequestActor.Unrestricted };
        var service = new MemoryManifestService(db, null!, accessor, new TestClock());
        var sql = service.ProjectEntries(accessor.Current, ["TT", "shared"], [Guid.NewGuid()]).ToQueryString();
        sql.Should().Contain("memory_items").And.Contain("version").And.Contain("memory_item_revisions");
        sql.Should().Contain("btrim(", "whitespace classification must remain a server-side boolean, not fetch body columns for client evaluation");
        sql.Should().NotContain("metadata_json").And.NotContain(" AS \"Content\"").And.NotContain(" AS \"Summary\"");
    }

    private static async Task Reject(Func<Task> action, string code)
        => (await action.Should().ThrowAsync<MemoryManifestException>()).Which.Code.Should().Be(code);

    private sealed class TestClock : IClock { public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.Parse("2026-10-13T00:00:00Z"); }
    private sealed class EmptyProjects : IAccessibleProjectService
    {
        public Task<IReadOnlyList<AccessibleProjectResult>> ListAsync(int limit, CancellationToken cancellationToken)
            => Task.FromResult<IReadOnlyList<AccessibleProjectResult>>([]);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public MemoryManifestTestDb Db { get; } = new();
        public ContextHubRequestActor Actor { get; } = new(Guid.NewGuid(), Guid.NewGuid(), "fixture", TenantUserRole.Member,
            [SecurityScopes.MemoryRead], [], true);
        public RequestActorAccessor Accessor { get; } = new();
        public TestClock Clock { get; } = new();
        public MemoryManifestService Service { get; }
        public Fixture()
        {
            Accessor.Current = Actor;
            Service = new(Db, new GovernanceProjectScopeResolver(Db, new EmptyProjects(), Accessor), Accessor, Clock);
        }
        public MemoryItem Item(string project, MemoryStatus status) => new()
        {
            ProjectId = project,
            Status = status,
            TenantId = Actor.TenantId,
            OwnerUserId = Actor.UserId,
            Version = 1,
            CreatedAt = Clock.UtcNow,
            UpdatedAt = Clock.UtcNow,
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            ExternalKey = Guid.NewGuid().ToString(),
            Content = "stopped-body-fixture",
            SourceRef = "raw-locator-fixture"
        };
        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }
}

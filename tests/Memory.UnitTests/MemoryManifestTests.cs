using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class MemoryManifestTests
{
    [Fact]
    public void Thousands_of_members_have_complete_stable_pages_and_replay_after_restart()
    {
        var (row, data) = Fixture();
        string? cursor = null;
        var ids = new List<Guid>();
        do
        {
            var page = MemoryManifestService.Page(row, data, cursor, 73);
            var restored = JsonSerializer.Deserialize<MemoryManifestData>(JsonSerializer.Serialize(data))!;
            MemoryManifestService.Page(row, restored, cursor, 73).Should().BeEquivalentTo(page);
            page.CountInvariantSatisfied.Should().BeTrue();
            page.PartitionCounts.Where(x => x.Key.StartsWith("status:")).Sum(x => x.Value).Should().Be(3281);
            page.PartitionCounts.Where(x => x.Key.StartsWith("project:")).Sum(x => x.Value).Should().Be(3281);
            page.PartitionCounts.Where(x => x.Key.StartsWith("classification:")).Sum(x => x.Value).Should().Be(3281);
            ids.AddRange(page.Items.Select(x => x.MemoryId));
            cursor = page.NextCursor;
        } while (cursor is not null);
        ids.Should().Equal(data.Entries.Select(x => x.MemoryId));
        ids.Distinct().Should().HaveCount(3281);
    }

    [Theory]
    [InlineData("broken")]
    [InlineData("!!.!!")]
    public void Malformed_cursor_is_rejected(string cursor)
    {
        var (row, data) = Fixture();
        var action = () => MemoryManifestService.Page(row, data, cursor, 100);
        action.Should().Throw<MemoryManifestException>().Which.Code.Should().Be("invalid-manifest-cursor");
    }

    [Fact]
    public void Cursor_is_bound_to_snapshot_page_size_and_authentic_position()
    {
        var (row, data) = Fixture();
        var cursor = MemoryManifestService.Page(row, data, null, 100).NextCursor!;
        var resized = () => MemoryManifestService.Page(row, data, cursor, 99);
        resized.Should().Throw<MemoryManifestException>();
        var other = new KnowledgeGovernanceSnapshot { Id = Guid.NewGuid(), TotalCount = row.TotalCount };
        var swapped = () => MemoryManifestService.Page(other, data, cursor, 100);
        swapped.Should().Throw<MemoryManifestException>();
        var tampered = cursor[..^2] + "AA";
        var forged = () => MemoryManifestService.Page(row, data, tampered, 100);
        forged.Should().Throw<MemoryManifestException>();
    }

    [Fact]
    public void Identity_changes_fail_closed_and_payload_excludes_storage_signing_material()
    {
        var actor = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "fixture", TenantUserRole.Member,
            [SecurityScopes.MemoryRead], ["TT"], true);
        var identity = MemoryManifestService.AccessIdentity(actor, ["TT", "shared"]);
        MemoryManifestService.AccessIdentity(actor with { AllowedProjectIds = ["tt"] }, ["tt", "shared"]).Should().Be(identity);
        MemoryManifestService.AccessIdentity(actor with { UserId = Guid.NewGuid() }, ["TT", "shared"]).Should().NotBe(identity);
        MemoryManifestService.AccessIdentity(actor with { TenantId = Guid.NewGuid() }, ["TT", "shared"]).Should().NotBe(identity);
        MemoryManifestService.AccessIdentity(actor with { AllowedProjectIds = [] }, ["TT", "shared"]).Should().NotBe(identity);
        MemoryManifestService.AccessIdentity(actor with { Scopes = [] }, ["TT", "shared"]).Should().NotBe(identity);
        var (row, data) = Fixture();
        var page = JsonSerializer.Serialize(MemoryManifestService.Page(row, data, null, 100));
        page.Should().NotContain(data.CursorKey);
        var names = typeof(MemoryManifestEntry).GetProperties().Select(x => x.Name).ToArray();
        names.Should().NotContain(["Content", "Summary", "Title", "SourceRef", "MetadataJson", "ContentHash"]);
        McpPublishedToolCatalog.QueryToolNames.Should().Contain(["memory_manifest_read", "memory_manifest_verify"]);
        MemoryManifestService.StoragePrefix.Length.Should().BeGreaterThan(128,
            "legacy application rollback readers must reject the storage key as a governance Run ID");
    }

    private static (KnowledgeGovernanceSnapshot, MemoryManifestData) Fixture()
    {
        var now = DateTimeOffset.Parse("2026-10-13T00:00:00Z");
        var entries = Enumerable.Range(0, 3281).Select(i => new MemoryManifestEntry(Guid.NewGuid(),
            i % 3 == 0 ? "shared" : "TT", MemoryType.Fact, MemoryScope.Project,
            (MemoryStatus)(i % 4), 1, now, false, null, "fixture:v1",
            i % 7 == 0 ? "SystemNonRetrieval" : "Knowledge", "Other", true)).OrderBy(x => x.MemoryId).ToArray();
        return (new KnowledgeGovernanceSnapshot { Id = Guid.NewGuid(), TotalCount = entries.Length, CreatedAt = now },
            new("ContextHub", true, ["ContextHub", "shared"], ["TT", "shared"], "fixture-scope", now.AddHours(24),
                Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)), entries));
    }
}

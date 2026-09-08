using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class AuthorityAwareRetrievalRerankerTests
{
    [Fact]
    public void Current_authority_outranks_a_high_score_superseded_blocker()
    {
        var current = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Scheduler state");
        current.MetadataJson = "{\"authorityState\":\"Current\"}";

        var superseded = CreateMemory("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Scheduler state");
        superseded.MetadataJson = $$"""{"authorityState":"Superseded","supersededByMemoryId":"{{current.Id:D}}"}""";

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
        [
            new(superseded, 0.99m, "old blocker"),
            new(current, 0.10m, "current state")
        ],
        2,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Select(x => x.Item.Id).Should().Equal(current.Id, superseded.Id);
        ranked[0].IsAuthorityConflict.Should().BeFalse();
        ranked[0].AuthorityState.Should().Be("Current");
    }

    [Fact]
    public void Explicit_archived_retrieval_keeps_historical_evidence_without_promoting_it()
    {
        var current = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Release state");
        var historical = CreateMemory("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Release state");
        historical.Status = MemoryStatus.Archived;
        historical.AuthorityState = MemoryAuthorityState.Historical;

        ChunkSearchHit[] keywordHits =
        [
            new ChunkSearchHit(current.Id, Guid.NewGuid(), 0.10m, "current"),
            new ChunkSearchHit(historical.Id, Guid.NewGuid(), 1.00m, "historical")
        ];
        ChunkSearchHit[] semanticHits =
        [
            new ChunkSearchHit(current.Id, Guid.NewGuid(), 0.10m, "current"),
            new ChunkSearchHit(historical.Id, Guid.NewGuid(), 1.00m, "historical")
        ];
        var items = new Dictionary<Guid, MemoryItem>
        {
            [current.Id] = current,
            [historical.Id] = historical
        };

        var defaultResults = HybridSearchComposer.Compose(keywordHits, semanticHits, items, 2, includeArchived: false);
        var explicitResults = HybridSearchComposer.Compose(keywordHits, semanticHits, items, 2, includeArchived: true);

        defaultResults.Select(x => x.MemoryId).Should().Equal(current.Id);
        explicitResults.Select(x => x.MemoryId).Should().Equal(current.Id, historical.Id);
        explicitResults[1].Excerpt.Should().NotContain(AuthorityAwareRetrievalReranker.ConflictMarker);
    }

    [Theory]
    [InlineData(MemoryStatus.Stale)]
    [InlineData(MemoryStatus.Archived)]
    public void Lifecycle_status_without_authority_evidence_does_not_demote_current_authority(MemoryStatus status)
    {
        var memory = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Lifecycle state");
        memory.Status = status;
        memory.AuthorityState = MemoryAuthorityState.Current;

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
            [new(memory, 0.10m, "lifecycle state")],
            1,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Should().ContainSingle();
        ranked[0].AuthorityState.Should().Be("Current");
        ranked[0].IsAuthorityConflict.Should().BeFalse();
    }

    [Fact]
    public void Competing_current_claims_are_returned_deterministically_and_surface_conflict()
    {
        var first = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Deployment state");
        var second = CreateMemory("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Deployment state");

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
        [
            new(second, 0.99m, "second claim"),
            new(first, 0.10m, "first claim")
        ],
        2,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Select(x => x.Item.Id).Should().Equal(first.Id, second.Id);
        ranked.Should().OnlyContain(x => x.IsAuthorityConflict);
        ranked.Should().OnlyContain(x => x.Excerpt.StartsWith(AuthorityAwareRetrievalReranker.ConflictMarker, StringComparison.Ordinal));
    }

    [Fact]
    public void Replacement_chain_resolves_predecessor_before_semantic_score()
    {
        var predecessor = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Policy state");
        predecessor.Status = MemoryStatus.Superseded;
        predecessor.AuthorityState = MemoryAuthorityState.Superseded;
        var successor = CreateMemory("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Policy state");
        predecessor.MetadataJson = $$"""{"supersededByMemoryId":"{{successor.Id:D}}"}""";
        successor.MetadataJson = $$"""{"supersedesMemoryId":"{{predecessor.Id:D}}"}""";

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
        [
            new(predecessor, 0.99m, "old policy"),
            new(successor, 0.10m, "new policy")
        ],
        2,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Select(x => x.Item.Id).Should().Equal(successor.Id, predecessor.Id);
        ranked[0].IsAuthorityConflict.Should().BeFalse();
        ranked[1].AuthorityState.Should().Be("Superseded");
    }

    [Fact]
    public void Typed_authority_fields_are_used_for_replacement_precedence()
    {
        var predecessor = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Typed policy state");
        predecessor.Status = MemoryStatus.Superseded;
        predecessor.AuthorityState = MemoryAuthorityState.Superseded;

        var successor = CreateMemory("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Typed policy state");
        successor.AuthorityState = MemoryAuthorityState.Current;
        predecessor.SupersededById = successor.Id;
        successor.SupersedesId = predecessor.Id;

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
        [
            new(predecessor, 0.99m, "typed old policy"),
            new(successor, 0.10m, "typed current policy")
        ],
        2,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Select(x => x.Item.Id).Should().Equal(successor.Id, predecessor.Id);
        ranked[0].AuthorityState.Should().Be("Current");
        ranked[1].AuthorityState.Should().Be("Superseded");
    }

    [Fact]
    public void Typed_current_with_stale_legacy_metadata_fails_closed_as_conflicted()
    {
        var current = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Typed current state");
        current.AuthorityState = MemoryAuthorityState.Current;
        current.MetadataJson = "{\"authorityState\":\"Superseded\"}";

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
            [new(current, 0.99m, "typed current state")],
            1,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Should().ContainSingle();
        ranked[0].AuthorityState.Should().Be("Conflicted");
        ranked[0].IsAuthorityConflict.Should().BeTrue();
        ranked[0].Excerpt.Should().StartWith(AuthorityAwareRetrievalReranker.ConflictMarker);
    }

    [Fact]
    public void Typed_non_current_with_legacy_current_metadata_fails_closed_as_conflicted()
    {
        var superseded = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Typed superseded state");
        superseded.AuthorityState = MemoryAuthorityState.Superseded;
        superseded.MetadataJson = "{\"authorityState\":\"Current\"}";

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
            [new(superseded, 0.99m, "typed superseded state")],
            1,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Should().ContainSingle();
        ranked[0].AuthorityState.Should().Be("Conflicted");
        ranked[0].IsAuthorityConflict.Should().BeTrue();
    }

    [Fact]
    public void Unloaded_legacy_successor_cannot_demote_without_scope_evidence()
    {
        var current = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Scoped state");
        current.MetadataJson = "{\"supersededByMemoryId\":\"bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb\"}";

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
            [new(current, 0.99m, "current state")],
            1,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Should().ContainSingle();
        ranked[0].AuthorityState.Should().Be("Current");
        ranked[0].IsAuthorityConflict.Should().BeFalse();
    }

    [Fact]
    public void Unloaded_typed_successor_uses_database_scoped_authority_evidence()
    {
        var predecessor = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Typed scoped state");
        predecessor.AuthorityState = MemoryAuthorityState.Superseded;
        predecessor.SupersededById = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
            [new(predecessor, 0.99m, "old state")],
            1,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Should().ContainSingle();
        ranked[0].AuthorityState.Should().Be("Superseded");
        ranked[0].IsAuthorityConflict.Should().BeFalse();
    }

    [Fact]
    public void Authority_claims_remain_isolated_between_projects_and_scopes()
    {
        var project = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Same title");
        project.ProjectId = "project-a";
        project.Scope = MemoryScope.Project;

        var shared = CreateMemory("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Same title");
        shared.ProjectId = ProjectContext.SharedProjectId;
        shared.Scope = MemoryScope.Project;

        var user = CreateMemory("cccccccc-cccc-cccc-cccc-cccccccccccc", "Same title");
        user.ProjectId = ProjectContext.UserProjectId;
        user.Scope = MemoryScope.User;

        // A malformed legacy link must not let one project/scope demote
        // another project's current claim.
        project.SupersededById = shared.Id;
        shared.SupersedesId = project.Id;
        user.SupersedesId = project.Id;

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
        [
            new(project, 0.20m, "project"),
            new(shared, 0.90m, "shared"),
            new(user, 0.10m, "user")
        ],
        3,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Should().OnlyContain(x => !x.IsAuthorityConflict);
        ranked.Select(x => x.Item.Id).Should().Equal(shared.Id, project.Id, user.Id);
    }

    [Fact]
    public void A_large_stale_fixture_cannot_resurrect_a_superseded_blocker()
    {
        var current = CreateMemory("ffffffff-ffff-ffff-ffff-ffffffffffff", "Runtime state");
        var candidates = Enumerable.Range(0, 128)
            .Select(index =>
            {
                var stale = CreateMemory(
                    Guid.Parse($"{index + 1:x8}-0000-0000-0000-000000000000").ToString("D"),
                    "Runtime state");
                stale.Status = MemoryStatus.Superseded;
                stale.AuthorityState = MemoryAuthorityState.Superseded;
                return new AuthorityAwareRetrievalReranker.RetrievalCandidate(stale, 0.99m, "stale blocker");
            })
            .Append(new AuthorityAwareRetrievalReranker.RetrievalCandidate(current, 0.01m, "current state"))
            .ToArray();

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
            candidates,
            1,
            DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Should().ContainSingle();
        ranked[0].Item.Id.Should().Be(current.Id);
        ranked[0].AuthorityState.Should().Be("Current");
    }

    [Fact]
    public void Cyclic_replacement_chain_is_fail_safe_and_does_not_select_a_winner()
    {
        var first = CreateMemory("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", "Cyclic state");
        var second = CreateMemory("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", "Cyclic state");
        first.MetadataJson = $$"""{"supersedesMemoryId":"{{second.Id:D}}"}""";
        second.MetadataJson = $$"""{"supersedesMemoryId":"{{first.Id:D}}"}""";

        var ranked = AuthorityAwareRetrievalReranker.Rerank(
        [
            new(second, 0.99m, "second"),
            new(first, 0.10m, "first")
        ],
        2,
        DateTimeOffset.Parse("2026-09-01T00:00:00Z"));

        ranked.Should().HaveCount(2);
        ranked.Should().OnlyContain(x => x.IsAuthorityConflict);
        ranked.Select(x => x.Item.Id).Should().Equal(first.Id, second.Id);
    }

    private static MemoryItem CreateMemory(string id, string title)
        => new()
        {
            Id = Guid.Parse(id),
            ProjectId = "ContextHub",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = title,
            Content = title,
            Summary = title,
            Importance = 0.5m,
            Confidence = 0.5m,
            CreatedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-08-01T00:00:00Z")
        };
}

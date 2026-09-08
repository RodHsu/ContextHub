using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class SuccessorAwareStaleBlockerTests
{
    [Fact]
    public void Strong_typed_successor_with_durable_same_scope_evidence_is_accepted()
    {
        var now = DateTimeOffset.UtcNow;
        var (predecessor, successor, evidence) = BuildChain(now);

        var result = SuccessorEvidencePolicy.CreateIndex([predecessor, successor, evidence])
            .Evaluate(predecessor, now);

        result.IsStrong.Should().BeTrue();
        result.ReasonCode.Should().Be("strong-same-scope-successor-evidence");
        result.Successor!.Id.Should().Be(successor.Id);
        result.Evidence!.Id.Should().Be(evidence.Id);
    }

    [Fact]
    public void Legacy_metadata_or_external_reference_cannot_prove_a_typed_successor()
    {
        var now = DateTimeOffset.UtcNow;
        var (predecessor, successor, _) = BuildChain(now);
        predecessor.SuccessorEvidenceId = null;
        successor.SuccessorEvidenceId = null;
        successor.SuccessorEvidenceRef = "external://unverified";
        predecessor.MetadataJson = $"{{\"supersededByMemoryId\":\"{successor.Id:D}\"}}";

        var result = SuccessorEvidencePolicy.CreateIndex([predecessor, successor])
            .Evaluate(predecessor, now);

        result.IsStrong.Should().BeFalse();
        result.ReasonCode.Should().Be("successor-evidence-missing");
        result.HasTypedSignal.Should().BeTrue();
    }

    [Fact]
    public void Cross_scope_successor_or_evidence_is_rejected()
    {
        var now = DateTimeOffset.UtcNow;

        foreach (var mutate in new Action<MemoryItem>[]
        {
            successor => successor.Scope = MemoryScope.Task,
            successor => successor.ProjectId = "another-project",
            successor => successor.OwnerUserId = Guid.NewGuid(),
            successor => successor.TenantId = Guid.NewGuid()
        })
        {
            var (predecessor, successor, evidence) = BuildChain(now);
            mutate(successor);

            var result = SuccessorEvidencePolicy.CreateIndex([predecessor, successor, evidence])
                .Evaluate(predecessor, now);
            result.IsStrong.Should().BeFalse();
            result.ReasonCode.Should().Be("successor-scope-mismatch");
        }

        var (evidencePredecessor, validSuccessor, crossScopeEvidence) = BuildChain(now);
        crossScopeEvidence.Scope = MemoryScope.Task;
        var evidenceResult = SuccessorEvidencePolicy.CreateIndex([evidencePredecessor, validSuccessor, crossScopeEvidence])
            .Evaluate(evidencePredecessor, now);
        evidenceResult.IsStrong.Should().BeFalse();
        evidenceResult.ReasonCode.Should().Be("successor-evidence-scope-mismatch");
    }

    [Fact]
    public void Competing_successors_are_treated_as_a_race_and_never_resolve_the_blocker()
    {
        var now = DateTimeOffset.UtcNow;
        var (predecessor, successor, evidence) = BuildChain(now);
        var competing = NewMemory(predecessor.TenantId!.Value, predecessor.OwnerUserId!.Value, predecessor.ProjectId, predecessor.Scope, "competing-successor");
        competing.SupersedesId = predecessor.Id;

        var result = SuccessorEvidencePolicy.CreateIndex([predecessor, successor, evidence, competing])
            .Evaluate(predecessor, now);

        result.IsStrong.Should().BeFalse();
        result.ReasonCode.Should().Be("successor-chain-ambiguous");
    }

    [Fact]
    public void Human_gate_tags_and_irreversible_metadata_remain_human_gated()
    {
        foreach (var tag in new[] { "critical", "legal", "privacy", "business", "irreversible" })
        {
            var (predecessor, _, _) = BuildChain(DateTimeOffset.UtcNow);
            predecessor.Tags = [tag];
            SuccessorEvidencePolicy.RequiresHumanDecision(predecessor).Should().BeTrue(tag);
        }

        var metadataCase = BuildChain(DateTimeOffset.UtcNow).Predecessor;
        metadataCase.MetadataJson = "{\"protectedDestruction\":true}";
        SuccessorEvidencePolicy.RequiresHumanDecision(metadataCase).Should().BeTrue();

        var readOnlyCase = BuildChain(DateTimeOffset.UtcNow).Predecessor;
        readOnlyCase.IsReadOnly = true;
        SuccessorEvidencePolicy.RequiresHumanDecision(readOnlyCase).Should().BeTrue();
    }

    [Fact]
    public void High_confidence_or_high_importance_alone_does_not_block_a_verified_successor()
    {
        var now = DateTimeOffset.UtcNow;
        var (predecessor, successor, evidence) = BuildChain(now);
        predecessor.Importance = .99m;
        predecessor.Confidence = .99m;

        SuccessorEvidencePolicy.RequiresHumanDecision(predecessor).Should().BeFalse();
        SuccessorEvidencePolicy.CreateIndex([predecessor, successor, evidence])
            .Evaluate(predecessor, now)
            .IsStrong.Should().BeTrue();
    }

    [Fact]
    public void Large_fixture_is_indexed_without_unrelated_rows_reviving_a_stale_blocker()
    {
        var now = DateTimeOffset.UtcNow;
        var (predecessor, successor, evidence) = BuildChain(now);
        var unrelated = Enumerable.Range(0, 20_000)
            .Select(index => NewMemory(predecessor.TenantId!.Value, predecessor.OwnerUserId!.Value, predecessor.ProjectId, MemoryScope.Project, $"fixture-{index}"))
            .ToArray();

        var index = SuccessorEvidencePolicy.CreateIndex(unrelated.Append(predecessor).Append(successor).Append(evidence));
        var strong = index.Evaluate(predecessor, now);
        var unrelatedResult = index.Evaluate(unrelated[0], now);

        strong.IsStrong.Should().BeTrue();
        unrelatedResult.IsStrong.Should().BeFalse();
        unrelatedResult.ReasonCode.Should().Be("predecessor-authority-not-superseded");
    }

    private static (MemoryItem Predecessor, MemoryItem Successor, MemoryItem Evidence) BuildChain(DateTimeOffset now)
    {
        var tenantId = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        const string projectId = "successor-aware-tests";
        const MemoryScope scope = MemoryScope.Project;
        var predecessor = NewMemory(tenantId, ownerUserId, projectId, scope, "predecessor");
        var successor = NewMemory(tenantId, ownerUserId, projectId, scope, "successor");
        var evidence = NewMemory(tenantId, ownerUserId, projectId, scope, "evidence", MemoryStatus.Archived, MemoryAuthorityState.Historical);

        predecessor.AuthorityState = MemoryAuthorityState.Superseded;
        predecessor.SupersededById = successor.Id;
        predecessor.ValidFrom = now.AddMinutes(-1);
        successor.SupersedesId = predecessor.Id;
        successor.SuccessorEvidenceId = evidence.Id;
        successor.ValidFrom = now;
        return (predecessor, successor, evidence);
    }

    private static MemoryItem NewMemory(
        Guid tenantId,
        Guid ownerUserId,
        string projectId,
        MemoryScope scope,
        string externalKey,
        MemoryStatus status = MemoryStatus.Active,
        MemoryAuthorityState authorityState = MemoryAuthorityState.Current)
        => new()
        {
            TenantId = tenantId,
            OwnerUserId = ownerUserId,
            ProjectId = projectId,
            ExternalKey = externalKey,
            Scope = scope,
            MemoryType = MemoryType.Episode,
            Title = externalKey,
            Content = externalKey,
            Summary = externalKey,
            Tags = [],
            Importance = .4m,
            Confidence = .6m,
            Status = status,
            AuthorityState = authorityState,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

}

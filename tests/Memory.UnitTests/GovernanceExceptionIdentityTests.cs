using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class GovernanceExceptionIdentityTests
{
    [Fact]
    public void Adjudication_should_publish_only_on_general_catalog_with_correct_read_write_classification()
    {
        McpPublishedToolCatalog.RestrictedToolNames.Should().Contain("conversation_adjudication_preview");
        McpPublishedToolCatalog.BackendToolNames.Should().Contain("conversation_insight_adjudicate");
        McpPublishedToolCatalog.QueryToolNames.Should().Contain("conversation_adjudication_preview");
        McpPublishedToolCatalog.QueryToolNames.Should().NotContain("conversation_insight_adjudicate");
        McpPublishedToolCatalog.DirectMutationToolNames.Should().Contain("conversation_insight_adjudicate");
    }

    [Fact]
    public void Identity_should_remove_only_known_legacy_timestamp_suffixes()
    {
        var id = Guid.NewGuid();
        GovernanceExceptionIdentity.NormalizeKey($"candidate:finding:{id:N}:123").Should().Be($"candidate:finding:{id:N}");
        GovernanceExceptionIdentity.NormalizeKey($"finding:{id:N}:123").Should().Be($"finding:{id:N}:123");
        GovernanceExceptionIdentity.NormalizeKey($"candidate:other:{id:N}:123").Should().Be($"candidate:other:{id:N}:123");
    }

    [Fact]
    public void Timestamps_versions_and_tag_order_should_not_change_semantic_evidence_but_content_should()
    {
        var memory = new MemoryItem { Content = "Original", Tags = ["b", "a"] };
        var hash = GovernanceExceptionIdentity.MemoryEvidence(memory);
        memory.UpdatedAt = DateTimeOffset.UtcNow;
        memory.Version++;
        memory.Tags = ["a", "b"];
        GovernanceExceptionIdentity.MemoryEvidence(memory).Should().Be(hash);
        memory.Content = "Changed";
        GovernanceExceptionIdentity.MemoryEvidence(memory).Should().NotBe(hash);
    }

    [Theory]
    [InlineData("NeedsReview", false, true)]
    [InlineData("Archive", false, false)]
    [InlineData("Archive", true, true)]
    [InlineData("unknown-action", false, true)]
    public void Only_supported_actions_without_manual_approval_are_execution_candidates(string action, bool approval, bool expected)
    {
        var item = new GovernanceReviewItem("key", GovernanceItemKind.Memory, "project", "classification", action,
            GovernanceBatchRiskLevel.Low, approval, Guid.NewGuid(), [], [], "run");
        GovernanceExceptionIdentity.RequiresHumanDecision(item).Should().Be(expected);
    }
}

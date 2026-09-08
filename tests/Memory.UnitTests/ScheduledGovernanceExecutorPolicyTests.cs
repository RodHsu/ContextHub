using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class ScheduledGovernanceExecutorPolicyTests
{
    [Fact]
    public void Pending_Conversation_Insight_Is_Not_Scheduled_Actionable()
    {
        var item = Item(
            GovernanceItemKind.ConversationInsight,
            "PendingConversationInsight",
            ["INSIGHT_DISPOSITION_REQUIRED"]);

        var result = ScheduledGovernanceAutomationEligibility.Evaluate(item);

        result.AutomationActionable.Should().BeFalse();
        result.ReasonClass.Should().Be("deterministic-authority-evidence-required");
    }

    [Fact]
    public void Business_Work_Item_Is_Never_Scheduled_Actionable()
    {
        var item = Item(
            GovernanceItemKind.WorkItem,
            "CompletedWorkItem",
            ["WORK_ITEM_TERMINAL"],
            action: "WorkItemReconcile");

        var result = ScheduledGovernanceAutomationEligibility.Evaluate(item);

        result.AutomationActionable.Should().BeFalse();
        result.ReasonClass.Should().Be("business-work-item-scheduled-forbidden");
    }

    [Fact]
    public void Authority_Conflict_Is_Fail_Closed()
    {
        var item = Item(
            GovernanceItemKind.Memory,
            "SupersededMemoryCandidate",
            ["SUCCESSOR_EVIDENCE", "AUTHORITY_CONFLICT"]);

        var result = ScheduledGovernanceAutomationEligibility.Evaluate(item);

        result.AutomationActionable.Should().BeFalse();
        result.ReasonClass.Should().Be("authority-conflict-or-pending");
    }

    [Fact]
    public void Deterministic_Insight_With_Authority_Is_Scheduled_Actionable()
    {
        var item = Item(
            GovernanceItemKind.ConversationInsight,
            "DuplicateConversationInsight",
            ["INSIGHT_EXACT_DUPLICATE"],
            action: GovernanceBatchActionType.ConversationInsightDisposition.ToString());

        var result = ScheduledGovernanceAutomationEligibility.Evaluate(item);

        result.AutomationActionable.Should().BeTrue();
        result.ReasonClass.Should().Be("deterministic-reversible-authoritative-evidence");
    }

    private static GovernanceReviewItem Item(
        GovernanceItemKind kind,
        string classification,
        IReadOnlyList<string> reasonCodes,
        string? action = null)
        => new(
            $"test:{kind}:{Guid.NewGuid():N}",
            kind,
            "ContextHub",
            classification,
            action ?? GovernanceBatchActionType.Archive.ToString(),
            GovernanceBatchRiskLevel.Low,
            RequiresExplicitApproval: false,
            AuthorityResourceId: Guid.NewGuid(),
            RelatedResourceIds: [],
            reasonCodes,
            "scheduled-policy-test")
        {
            SemanticConfidence = 0.99m,
            IsReversible = true
        };
}

using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class KnowledgeReviewConvergenceTests
{
    [Fact]
    public void Coverage_Incomplete_Must_Not_Converge()
    {
        var result = KnowledgeReviewService.BuildConvergence(
            isReReview: true,
            coverageComplete: false,
            hasMore: false,
            actionableItemCount: 0,
            deferredCount: 0,
            requiresUserDecisionCount: 0,
            hostBlockedCount: 0);

        result.Status.Should().Be("CoverageIncomplete");
        result.IsConverged.Should().BeFalse();
        result.RequiresReReview.Should().BeTrue();
    }

    [Fact]
    public void Remaining_Page_Must_Not_Converge()
    {
        var result = KnowledgeReviewService.BuildConvergence(
            isReReview: true,
            coverageComplete: true,
            hasMore: true,
            actionableItemCount: 0,
            deferredCount: 0,
            requiresUserDecisionCount: 0,
            hostBlockedCount: 0);

        result.Status.Should().Be("CoverageIncomplete");
        result.IsConverged.Should().BeFalse();
    }

    [Fact]
    public void HostBlocked_May_Converge_As_Explicit_Exception()
    {
        var result = KnowledgeReviewService.BuildConvergence(
            isReReview: true,
            coverageComplete: true,
            hasMore: false,
            actionableItemCount: 0,
            deferredCount: 1,
            requiresUserDecisionCount: 2,
            hostBlockedCount: 3);

        result.Status.Should().Be("ConvergedWithExceptions");
        result.IsConverged.Should().BeTrue();
        result.ActionableItemCount.Should().Be(0);
        result.ExceptionCount.Should().Be(6);
    }
}

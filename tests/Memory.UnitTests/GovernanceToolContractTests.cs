using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class GovernanceToolContractTests
{
    [Fact]
    public void Canonical_Governance_Contract_Should_Have_Stable_Hash_And_All_Published_Actions()
    {
        GovernanceToolContract.SchemaHash.Should().MatchRegex("^[a-f0-9]{64}$");
        var contract = GovernanceToolContract.Describe();
        contract.ToolName.Should().Be("governance_batch_execute");
        contract.ToolContractVersion.Should().Be("2.0");
        contract.PublishedCatalogVersion.Should().Be("2026-08-29-v3");
        contract.SupportedActions.Should().BeEquivalentTo(Enum.GetNames<GovernanceBatchActionType>());
        contract.SupportedActions.Should().Contain([
            nameof(GovernanceBatchActionType.Quarantine),
            nameof(GovernanceBatchActionType.MaturedDelete),
            nameof(GovernanceBatchActionType.SemanticReevaluate)
        ]);
    }

    [Fact]
    public void Convergence_Should_Separate_Execution_Backlog_From_Audited_Exceptions()
    {
        var result = KnowledgeReviewService.BuildConvergence(
            isReReview: true,
            coverageComplete: true,
            hasMore: false,
            actionableItemCount: 0,
            deferredCount: 3,
            requiresUserDecisionCount: 4,
            hostBlockedCount: 2,
            governanceActionableCount: 0);

        result.IsConverged.Should().BeTrue();
        result.Status.Should().Be("ConvergedWithExceptions");
        result.ExecutionActionableCount.Should().Be(0);
        result.GovernedExceptionCount.Should().Be(9);
        result.ExceptionCount.Should().Be(9);
    }
}

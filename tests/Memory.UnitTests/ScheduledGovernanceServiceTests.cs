using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class ScheduledGovernanceServiceTests
{
    [Fact]
    public async Task Review_Should_Resolve_Global_Scope_And_Return_Fixed_Server_Decision()
    {
        var executable = new GovernanceReviewItem(
            "finding:1", GovernanceItemKind.Memory, "ProjectA", "Duplicate", "Archive",
            GovernanceBatchRiskLevel.Low, false, Guid.NewGuid(), [], ["DUPLICATE"], "run-1")
        {
            IsReversible = true
        };
        var knowledge = new StubKnowledgeReviewService(CreateReview([executable]));
        var executor = new CapturingExecutor();
        var service = CreateService(knowledge, executor);

        var result = await service.ReviewAsync(new ScheduledGovernanceReviewRequest("run-1"), CancellationToken.None);

        knowledge.Request.Should().NotBeNull();
        knowledge.Request!.ProjectIds.Should().BeNull();
        knowledge.Request.LimitPerSection.Should().Be(200);
        knowledge.Request.Offset.Should().Be(0);
        result.Decision.Should().Be(ScheduledGovernanceDecision.ReversibleExecutionRequired);
        result.CountInvariant.Satisfied.Should().BeTrue();
        result.CountInvariant.SharedScopeOccurrences.Should().Be(1);
        result.CountInvariant.UserScopeOccurrences.Should().Be(0);
        result.CountInvariant.UserScopeHandledSeparately.Should().BeTrue();
        result.ReversibleExecutionCount.Should().Be(1);
        executor.CallCount.Should().Be(0, "review must not execute governed-resource mutations");
    }

    [Fact]
    public async Task Review_Should_Distinguish_NoOp_Human_And_Incomplete_Coverage()
    {
        var service = CreateService(new StubKnowledgeReviewService(CreateReview([])), new CapturingExecutor());
        (await service.ReviewAsync(new("run-noop"), CancellationToken.None)).Decision
            .Should().Be(ScheduledGovernanceDecision.NoOpConverged);

        var human = new GovernanceReviewItem(
            "finding:human", GovernanceItemKind.Memory, "ProjectA", "Ambiguous", "Merge",
            GovernanceBatchRiskLevel.High, true, Guid.NewGuid(), [], ["HUMAN"], "run-human");
        service = CreateService(new StubKnowledgeReviewService(CreateReview([human])), new CapturingExecutor());
        (await service.ReviewAsync(new("run-human"), CancellationToken.None)).Decision
            .Should().Be(ScheduledGovernanceDecision.HumanDecisionOnly);

        service = CreateService(new StubKnowledgeReviewService(CreateReview([], countInvariant: false)), new CapturingExecutor());
        (await service.ReviewAsync(new("run-incomplete"), CancellationToken.None)).Decision
            .Should().Be(ScheduledGovernanceDecision.CoverageIncomplete);
    }

    [Fact]
    public async Task Execute_Should_Map_Only_Fixed_Reversible_Policy()
    {
        var executor = new CapturingExecutor();
        var service = CreateService(new StubKnowledgeReviewService(CreateReview([])), executor);
        var request = new ScheduledGovernanceExecuteRequest(
            "run-1", "snapshot-1", MaxMutations: 25, MaxDurationSeconds: 60,
            ToolContractVersion: ScheduledGovernanceContract.ToolContractVersion,
            SchemaHash: ScheduledGovernanceContract.SchemaHash);

        await service.ExecuteAsync(request, CancellationToken.None);

        executor.Request.Should().NotBeNull();
        var mapped = executor.Request!;
        mapped.ProjectIds.Should().BeNull();
        mapped.AllowedActionTypes.Should().BeEquivalentTo(ScheduledGovernanceContract.FixedReversibleActions);
        mapped.AllowedActionTypes.Should().NotContain([
            GovernanceBatchActionType.MaturedDelete,
            GovernanceBatchActionType.DeleteProposal,
            GovernanceBatchActionType.ProposalApply,
            GovernanceBatchActionType.LogRetentionProposal
        ]);
        mapped.AllowHardDelete.Should().BeFalse();
        mapped.AllowMaturedDelete.Should().BeFalse();
        mapped.DryRun.Should().BeFalse();
        mapped.MaxRiskLevel.Should().Be(GovernanceBatchRiskLevel.Low);
        mapped.ExecutionMode.Should().Be(GovernanceBatchExecutionMode.Scheduled);
        mapped.ToolContractVersion.Should().Be(GovernanceToolContract.ToolContractVersion);
        mapped.SchemaHash.Should().Be(GovernanceToolContract.SchemaHash);
    }

    [Fact]
    public async Task Execute_Should_Fail_Closed_On_Stale_Contract_Or_NonAdmin()
    {
        var service = CreateService(new StubKnowledgeReviewService(CreateReview([])), new CapturingExecutor());
        var stale = () => service.ExecuteAsync(new(
            "run-1", "snapshot-1", ToolContractVersion: "stale", SchemaHash: "stale"), CancellationToken.None);
        await stale.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.SchemaCapabilityMismatch);

        var actor = new RequestActorAccessor
        {
            Current = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "member", TenantUserRole.Member,
                [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.ScheduledGovernance], [], true)
        };
        service = new ScheduledGovernanceService(
            new StubKnowledgeReviewService(CreateReview([])), new CapturingExecutor(), new StubReceipts(), actor);
        var unauthorized = () => service.ReviewAsync(new("run-2"), CancellationToken.None);
        await unauthorized.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    private static ScheduledGovernanceService CreateService(
        IKnowledgeReviewService knowledge,
        IGovernanceBatchExecutor executor)
    {
        var actor = new RequestActorAccessor
        {
            Current = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "admin", TenantUserRole.Admin,
                [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.ScheduledGovernance], [], true)
        };
        return new ScheduledGovernanceService(knowledge, executor, new StubReceipts(), actor);
    }

    private static KnowledgeReviewResult CreateReview(
        IReadOnlyList<GovernanceReviewItem> items,
        bool countInvariant = true)
    {
        var total = 2;
        var covered = countInvariant ? total : total - 1;
        var durable = new KnowledgeGovernanceCoverageResult(
            Guid.NewGuid(), "snapshot-1", DateTimeOffset.UtcNow, total, total, total, 0, 1, 1, true, false, null)
        {
            AuthorizedGovernanceDurableMemoryCount = total,
            GovernanceCoveredDurableMemoryCount = covered,
            GovernanceProjectIds = ["ProjectA", ProjectContext.SharedProjectId]
        };
        var surface = new GovernanceSurfaceCoverageResult(0, 0, 0, 0, 0, 0, 0, false, true);
        var coverage = new FullGovernanceCoverageResult(
            surface, surface, surface, surface, surface, surface, surface, surface, surface, surface, surface);
        var page = new KnowledgeReviewPageResult(0, 200, 0, 0, false);
        var pagination = new KnowledgeReviewPaginationResult(page, page, page, page, page, page, page, page);
        var convergence = new KnowledgeReviewConvergenceResult("Review", items.Count, true, false)
        {
            BusinessWorkItemActionableCount = 3
        };
        return new KnowledgeReviewResult(
            [], null!, [], [], [], [], [], [], [], "run-1", false, pagination, convergence)
        {
            DurableMemoryCoverage = durable,
            GovernancePlan = items,
            GovernanceCoverage = coverage,
            CandidateCount = items.Count,
            ExecutionActionableCount = items.Count,
            GovernedExceptionCount = items.Count(x => x.RequiresExplicitApproval)
        };
    }

    private sealed class StubKnowledgeReviewService(KnowledgeReviewResult result) : IKnowledgeReviewService
    {
        public KnowledgeReviewRequest? Request { get; private set; }
        public Task<KnowledgeReviewResult> ReviewAsync(KnowledgeReviewRequest request, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(result with { GovernanceRunId = request.GovernanceRunId!, IsReReview = request.IsReReview });
        }
    }

    private sealed class CapturingExecutor : IGovernanceBatchExecutor
    {
        public int CallCount { get; private set; }
        public GovernanceBatchExecuteRequest? Request { get; private set; }
        public Task<GovernanceBatchExecuteResult> ExecuteAsync(GovernanceBatchExecuteRequest request, CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            return Task.FromResult(new GovernanceBatchExecuteResult(
                0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
                null, false, true, [], [], request.SnapshotToken!, "Completed"));
        }
    }

    private sealed class StubReceipts : IGovernanceRunReceiptService
    {
        public Task RecordReviewAsync(KnowledgeReviewResult result, DateTimeOffset startedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordExecutionStartedAsync(GovernanceBatchExecuteRequest request, DateTimeOffset startedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordExecutionAsync(GovernanceBatchExecuteRequest request, GovernanceBatchExecuteResult result, DateTimeOffset startedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordExecutionStoppedAsync(GovernanceBatchExecuteRequest request, DateTimeOffset startedAt, string status, string stoppedReason, string failurePhase, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GovernanceBatchExecuteResult?> GetTerminalPreExecutionReplayAsync(GovernanceBatchExecuteRequest request, CancellationToken cancellationToken) => Task.FromResult<GovernanceBatchExecuteResult?>(null);
        public Task RecordInternalRetentionAsync(InternalMaturedDeleteBatchResult result, DateTimeOffset startedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GovernanceRunReceiptResult?> GetAsync(string governanceRunId, CancellationToken cancellationToken) => Task.FromResult<GovernanceRunReceiptResult?>(null);
        public Task<IReadOnlyList<GovernanceRunReceiptResult>> ListAsync(GovernanceRunReceiptListRequest request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GovernanceRunReceiptResult>>([]);
    }
}

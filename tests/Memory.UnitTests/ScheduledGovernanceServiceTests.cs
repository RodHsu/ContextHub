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
            IsReversible = true,
            SemanticConfidence = 0.99m
        };
        var knowledge = new StubKnowledgeReviewService(CreateReview([executable]));
        var executor = new CapturingExecutor();
        var receipts = new StubReceipts();
        var service = CreateService(knowledge, executor, receipts);

        var result = await service.ReviewAsync(new ScheduledGovernanceReviewRequest("run-1"), CancellationToken.None);

        knowledge.Request.Should().NotBeNull();
        knowledge.Request!.ProjectIds.Should().BeNull();
        knowledge.Request.LimitPerSection.Should().Be(200);
        knowledge.Request.Offset.Should().Be(0);
        knowledge.Request.ReceiptContractIdentity.Should().Be(new GovernanceReceiptContractIdentity(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion));
        result.Decision.Should().Be(ScheduledGovernanceDecision.ReversibleExecutionRequired);
        result.CountInvariant.Satisfied.Should().BeTrue();
        result.CountInvariant.SharedScopeOccurrences.Should().Be(1);
        result.CountInvariant.UserScopeOccurrences.Should().Be(0);
        result.CountInvariant.UserScopeHandledSeparately.Should().BeTrue();
        result.ReversibleExecutionCount.Should().Be(1);
        result.CurrentReviewHumanDecisionCandidateCount.Should().Be(0);
        result.GovernedRequiresUserDecisionExceptionCount.Should().Be(0);
        result.GovernedHostBlockedExceptionCount.Should().Be(0);
        result.GovernedDeferredExceptionCount.Should().Be(0);
        receipts.ReviewStartedCount.Should().Be(1);
        receipts.ReviewStoppedCount.Should().Be(0);
        receipts.LastScheduledDecision.Should().NotBeNull();
        receipts.LastScheduledDecision!.AutomationActionableCount.Should().Be(1);
        receipts.LastScheduledDecision.RequiresUserDecisionCount.Should().Be(0);
        receipts.LastScheduledDecision.Decision.Should().Be(ScheduledGovernanceDecision.ReversibleExecutionRequired);
        executor.CallCount.Should().Be(0, "review must not execute governed-resource mutations");
    }

    [Fact]
    public async Task Review_Should_Trim_GovernanceRunId_Before_Recording_And_Reviewing()
    {
        var knowledge = new StubKnowledgeReviewService(CreateReview([]));
        var receipts = new StubReceipts();
        var service = CreateService(knowledge, new CapturingExecutor(), receipts);

        var result = await service.ReviewAsync(
            new ScheduledGovernanceReviewRequest("  run-trimmed  "),
            CancellationToken.None);

        knowledge.Request!.GovernanceRunId.Should().Be("run-trimmed");
        result.GovernanceRunId.Should().Be("run-trimmed");
        receipts.LastReviewStartedRunId.Should().Be("run-trimmed");
    }

    [Fact]
    public async Task GovernanceRunId_Should_Fail_Closed_When_Over_Maximum_Length()
    {
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            new CapturingExecutor());
        var tooLong = new string('r', ScheduledGovernanceReliabilityService.MaxGovernanceRunIdLength + 1);

        var review = () => service.ReviewAsync(new(tooLong), CancellationToken.None);
        await review.Should().ThrowAsync<ArgumentException>();

        var get = () => service.GetReceiptAsync(tooLong, CancellationToken.None);
        await get.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Review_Should_Separate_Current_Human_Candidates_From_Governed_Exception_Counters()
    {
        var human = new GovernanceReviewItem(
            "finding:human", GovernanceItemKind.Memory, "ProjectA", "Ambiguous", "Merge",
            GovernanceBatchRiskLevel.High, true, Guid.NewGuid(), [], ["HUMAN"], "run-human");
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview(
                [human],
                governedDeferred: 2,
                governedRequiresUserDecision: 3,
                governedHostBlocked: 4)),
            new CapturingExecutor());

        var result = await service.ReviewAsync(new("run-human"), CancellationToken.None);

        result.HumanDecisionCount.Should().Be(1);
        result.CurrentReviewHumanDecisionCandidateCount.Should().Be(1);
        result.GovernedDeferredExceptionCount.Should().Be(2);
        result.GovernedRequiresUserDecisionExceptionCount.Should().Be(3);
        result.GovernedHostBlockedExceptionCount.Should().Be(4);
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
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            executor,
            new StubReceipts(CreateDecisionReceipt(
                "run-1",
                ScheduledGovernanceDecision.ReversibleExecutionRequired)));
        var request = new ScheduledGovernanceExecuteRequest(
            "  run-1  ", "snapshot-1", MaxMutations: 25, MaxDurationSeconds: 60,
            ToolContractVersion: ScheduledGovernanceContract.ToolContractVersion,
            SchemaHash: ScheduledGovernanceContract.SchemaHash);

        await service.ExecuteAsync(request, CancellationToken.None);

        executor.Request.Should().NotBeNull();
        var mapped = executor.Request!;
        mapped.GovernanceRunId.Should().Be("run-1");
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
        mapped.ReceiptContractIdentity.Should().Be(new GovernanceReceiptContractIdentity(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion));
    }

    [Theory]
    [InlineData(ScheduledGovernanceDecision.NoOpConverged)]
    [InlineData(ScheduledGovernanceDecision.HumanDecisionOnly)]
    [InlineData(ScheduledGovernanceDecision.CoverageIncomplete)]
    public async Task Execute_Should_Fail_Closed_When_Persisted_Decision_Is_Not_Reversible(
        ScheduledGovernanceDecision decision)
    {
        var executor = new CapturingExecutor();
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            executor,
            new StubReceipts(CreateDecisionReceipt("run-gate", decision)));

        var action = () => service.ExecuteAsync(
            CreateExecuteRequest("run-gate", "snapshot-1"),
            CancellationToken.None);

        await action.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.ReReviewRequired &&
                        x.Message.Contains($"persisted-decision-{decision}", StringComparison.Ordinal));
        executor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Execute_Should_Fail_Closed_When_Persisted_Decision_Is_Missing()
    {
        var executor = new CapturingExecutor();
        var receipt = CreateDecisionReceipt(
            "run-missing-decision",
            ScheduledGovernanceDecision.NoOpConverged) with
        {
            FinalConvergenceStatus = string.Empty
        };
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            executor,
            new StubReceipts(receipt));

        var action = () => service.ExecuteAsync(
            CreateExecuteRequest("run-missing-decision", "snapshot-1"),
            CancellationToken.None);

        await action.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.ReReviewRequired &&
                        x.Message.Contains("persisted-decision-missing", StringComparison.Ordinal));
        executor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Execute_Should_Require_ReReview_After_Item_Execution_Failure()
    {
        var executor = new CapturingExecutor();
        var failedBatch = CreateBatchOutcome(
            "snapshot-1",
            cursorBefore: string.Empty,
            nextCursor: null,
            requiresReReview: true) with
        {
            Executed = false,
            FailurePhase = "ItemExecution",
            Failed = 1,
            StoppedReason = "ItemFailed"
        };
        var receipt = CreateDecisionReceipt(
            "run-item-failed",
            ScheduledGovernanceDecision.ReversibleExecutionRequired,
            latestBatch: failedBatch);
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            executor,
            new StubReceipts(receipt));

        var action = () => service.ExecuteAsync(
            CreateExecuteRequest("run-item-failed", "snapshot-1"),
            CancellationToken.None);

        await action.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.ReReviewRequired &&
                        x.Message.Contains("persisted-latest-batch-requires-re-review", StringComparison.Ordinal));
        executor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Execute_Should_Fail_Closed_When_Persisted_Snapshot_Does_Not_Match_Request()
    {
        var executor = new CapturingExecutor();
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            executor,
            new StubReceipts(CreateDecisionReceipt(
                "run-snapshot-mismatch",
                ScheduledGovernanceDecision.ReversibleExecutionRequired,
                "snapshot-authoritative")));

        var action = () => service.ExecuteAsync(
            CreateExecuteRequest("run-snapshot-mismatch", "snapshot-forged"),
            CancellationToken.None);

        await action.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.CursorSnapshotMismatch &&
                        x.Message.Contains("snapshot-binding-mismatch", StringComparison.Ordinal));
        executor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task Execute_Should_Allow_Exact_Replay_Binding_For_Reversible_Decision()
    {
        var executor = new CapturingExecutor();
        var latestBatch = CreateBatchOutcome(
            snapshotToken: "snapshot-1",
            cursorBefore: "",
            nextCursor: null,
            requiresReReview: true,
            isReplay: true);
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            executor,
            new StubReceipts(CreateDecisionReceipt(
                "run-replay",
                ScheduledGovernanceDecision.ReversibleExecutionRequired,
                latestBatch: latestBatch) with
            {
                IsReplay = true
            }));

        await service.ExecuteAsync(
            CreateExecuteRequest("run-replay", "snapshot-1"),
            CancellationToken.None);

        executor.CallCount.Should().Be(1, "the existing executor owns exact replay/idempotency handling");
    }

    [Fact]
    public void Scheduled_Policy_Should_Fail_Closed_For_Ordinary_Work_Items()
    {
        ScheduledGovernanceContract.FixedReversibleActions
            .Should().NotContain(GovernanceBatchActionType.WorkItemReconcile);

        var workItem = new GovernanceReviewItem(
            "workitem:1", GovernanceItemKind.WorkItem, "ProjectA", "CompletedWorkItem",
            "WorkItemReconcile", GovernanceBatchRiskLevel.Low, false, Guid.NewGuid(), [],
            ["WORK_ITEM_TERMINAL"], "run-workitem")
        {
            IsReversible = true
        };

        var eligibility = ScheduledGovernanceAutomationEligibility.Evaluate(workItem);

        eligibility.AutomationActionable.Should().BeFalse();
        eligibility.RequiresUserDecision.Should().BeTrue();
        eligibility.ReasonClass.Should().Be("business-work-item-scheduled-forbidden");
    }

    [Fact]
    public void Scheduled_Policy_Should_Require_Deterministic_Insight_Evidence()
    {
        var deterministic = new GovernanceReviewItem(
            "insight:deterministic", GovernanceItemKind.ConversationInsight, "ProjectA",
            "DuplicateConversationInsight", "ConversationInsightDisposition",
            GovernanceBatchRiskLevel.Low, false, Guid.NewGuid(), [], ["INSIGHT_EXACT_DUPLICATE"], "run-insight")
        {
            IsReversible = true,
            SemanticConfidence = 0.99m
        };
        var subjective = deterministic with
        {
            Classification = "PendingConversationInsight",
            ReasonCodes = ["INSIGHT_DISPOSITION_REQUIRED"]
        };

        ScheduledGovernanceAutomationEligibility.Evaluate(deterministic)
            .Eligibility.Should().Be(ScheduledGovernanceEligibility.AutomationActionable);
        var subjectiveResult = ScheduledGovernanceAutomationEligibility.Evaluate(subjective);
        subjectiveResult.Eligibility.Should().Be(ScheduledGovernanceEligibility.RequiresUserDecision);
        subjectiveResult.ReasonClass.Should().Be("deterministic-authority-evidence-required");
    }

    [Fact]
    public async Task Review_Should_Return_NoOp_For_Stable_Acknowledged_Exceptions()
    {
        var receipt = CreateReceipt(
            "run-stable",
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion) with
        {
            GovernedExceptionCount = 1,
            Deferred = 1,
            FinalConvergenceStatus = "ConvergedWithExceptions",
            ExceptionDelta = new GovernanceExceptionDeltaResult(0, 0, 1, 0)
        };
        var review = CreateReview([]) with { GovernedExceptionCount = 1 };
        var service = CreateService(
            new StubKnowledgeReviewService(review),
            new CapturingExecutor(),
            new StubReceipts(receipt));

        var result = await service.ReviewAsync(new("run-stable"), CancellationToken.None);

        result.Decision.Should().Be(ScheduledGovernanceDecision.NoOpConverged);
        result.ExceptionDelta.Should().Be(new GovernanceExceptionDeltaResult(0, 0, 1, 0));
    }

    [Fact]
    public async Task Run_Get_Should_Read_Reliability_Without_Observing_Or_Writing()
    {
        var reliability = new CountingReliability();
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            new CapturingExecutor(),
            new StubReceipts(CreateReceipt("run-read", "scheduled-1.3", "sha256:current", "catalog-current")),
            reliability);

        await service.GetReceiptAsync("run-read", CancellationToken.None);

        reliability.ObserveCount.Should().Be(0);
        reliability.GetCount.Should().Be(1);
    }

    [Fact]
    public async Task GetReceipt_Should_Return_Persisted_Contract_Identity_Not_Current_Constants()
    {
        var persisted = CreateReceipt("run-old", "scheduled-0.9", "sha256:old", "catalog-old");
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            new CapturingExecutor(),
            new StubReceipts(persisted));

        var result = await service.GetReceiptAsync("run-old", CancellationToken.None);

        result.Should().NotBeNull();
        result!.ToolContractVersion.Should().Be("scheduled-0.9");
        result.SchemaHash.Should().Be("sha256:old");
        result.PublishedCatalogVersion.Should().Be("catalog-old");
        result.Received.Should().BeTrue();
        result.Terminal.Should().BeTrue();
        result.Decision.Should().BeNull();
        result.Outcome.Should().Be("ContractMismatch");
    }

    [Fact]
    public async Task GetReceipt_Should_Fail_Closed_When_Generic_Review_Pollutes_Scheduled_Lineage()
    {
        var persisted = CreateReceipt(
            "run-polluted",
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion) with
        {
            ExecutionActionableCount = 4,
            FinalConvergenceStatus = nameof(ScheduledGovernanceDecision.ReversibleExecutionRequired)
        };
        var lineage = new GovernanceRunLineageResult(
            RunExists: true,
            IsScheduledMode: false,
            ContractMatches: false,
            Status: "ModeMismatch",
            Reason: "unit-test generic review pollution");
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            new CapturingExecutor(),
            new StubReceipts(persisted, lineage));

        var result = await service.GetReceiptAsync("run-polluted", CancellationToken.None);

        result.RunExists.Should().BeTrue();
        result.Status.Should().Be("ModeMismatch");
        result.Outcome.Should().Be("ModeMismatch");
        result.Decision.Should().BeNull();
        result.ReversibleExecutionActionableCount.Should().Be(0);
        result.CoverageComplete.Should().BeFalse();
    }

    [Fact]
    public async Task Execute_Should_Reject_Generic_Review_Pollution_Before_Executor()
    {
        var executor = new CapturingExecutor();
        var lineage = new GovernanceRunLineageResult(
            RunExists: true,
            IsScheduledMode: false,
            ContractMatches: false,
            Status: "ModeMismatch",
            Reason: "unit-test generic review pollution");
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            executor,
            new StubReceipts(lineage: lineage));
        var request = new ScheduledGovernanceExecuteRequest(
            "run-polluted",
            "snapshot-1",
            ToolContractVersion: ScheduledGovernanceContract.ToolContractVersion,
            SchemaHash: ScheduledGovernanceContract.SchemaHash);

        var action = () => service.ExecuteAsync(request, CancellationToken.None);

        await action.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.SchemaCapabilityMismatch &&
                        x.Message.Contains("ModeMismatch", StringComparison.Ordinal));
        executor.CallCount.Should().Be(0);
    }

    [Fact]
    public async Task GetReceipt_Should_Not_Infer_A_Scheduled_Decision_From_A_Failed_Receipt()
    {
        var failed = CreateReceipt(
            "run-failed-receipt",
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion) with
        {
            Status = "Failed",
            ExecutionActionableCount = 9,
            FinalConvergenceStatus = "CursorExpired"
        };
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            new CapturingExecutor(),
            new StubReceipts(failed));

        var result = await service.GetReceiptAsync("run-failed-receipt", CancellationToken.None);

        result.Decision.Should().BeNull();
        result.Outcome.Should().Be("CursorExpired");
    }

    [Fact]
    public async Task GetReceipt_Should_Return_Structured_NotReceived_Result()
    {
        var service = CreateService(
            new StubKnowledgeReviewService(CreateReview([])),
            new CapturingExecutor(),
            new StubReceipts());

        var result = await service.GetReceiptAsync("missing-run", CancellationToken.None);

        result.RunExists.Should().BeFalse();
        result.Received.Should().BeFalse();
        result.Terminal.Should().BeFalse();
        result.Decision.Should().BeNull();
        result.Status.Should().Be("NotReceived");
        result.Outcome.Should().Be("NotReceived");
        result.GovernanceRunId.Should().Be("missing-run");
    }

    [Fact]
    public async Task Review_Should_Record_Failed_Receipt_After_Handler_Entry()
    {
        var receipts = new StubReceipts();
        var service = CreateService(new ThrowingKnowledgeReviewService(), new CapturingExecutor(), receipts);

        var action = () => service.ReviewAsync(new("run-failed"), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>();
        receipts.ReviewStartedCount.Should().Be(1);
        receipts.ReviewStoppedCount.Should().Be(1);
    }

    [Fact]
    public async Task Review_Should_Observe_Failed_Receipt_On_Mutation_Path()
    {
        var reliability = new CountingReliability();
        var failedReceipt = CreateReceipt(
            "run-failed-observed",
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion) with
        {
            Status = "Failed",
            StoppedReason = "InvalidOperationException"
        };
        var service = CreateService(
            new ThrowingKnowledgeReviewService(),
            new CapturingExecutor(),
            new StubReceipts(failedReceipt),
            reliability);

        var action = () => service.ReviewAsync(new("run-failed-observed"), CancellationToken.None);
        await action.Should().ThrowAsync<InvalidOperationException>();

        reliability.ObserveCount.Should().Be(1);
        reliability.GetCount.Should().Be(0);
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
        IGovernanceBatchExecutor executor,
        IGovernanceRunReceiptService? receipts = null,
        IScheduledGovernanceReliabilityService? reliability = null)
    {
        var actor = new RequestActorAccessor
        {
            Current = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "admin", TenantUserRole.Admin,
                [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.ScheduledGovernance], [], true)
        };
        return new ScheduledGovernanceService(
            knowledge,
            executor,
            receipts ?? new StubReceipts(),
            actor,
            reliability);
    }

    private static GovernanceRunReceiptResult CreateReceipt(
        string runId,
        string toolContractVersion,
        string schemaHash,
        string publishedCatalogVersion)
        => new(
            ReceiptId: Guid.NewGuid(),
            GovernanceRunId: runId,
            Actor: "scheduled-governance",
            ExecutionMode: "Scheduled",
            StartedAt: DateTimeOffset.UtcNow.AddMinutes(-1),
            CompletedAt: DateTimeOffset.UtcNow,
            ToolContractVersion: toolContractVersion,
            SchemaHash: schemaHash,
            PublishedCatalogVersion: publishedCatalogVersion,
            InitialSnapshotToken: "snapshot-old",
            FinalSnapshotToken: "snapshot-old",
            CoverageComplete: true,
            InitialGovernanceActionable: 0,
            FinalGovernanceActionable: 0,
            CandidateCount: 0,
            ExecutionActionableCount: 0,
            GovernedExceptionCount: 0,
            Applied: 0,
            Failed: 0,
            Deferred: 0,
            RequiresUserDecision: 0,
            HostBlocked: 0,
            Quarantined: 0,
            DeleteEligible: 0,
            DeleteMatured: 0,
            AutoDeleted: 0,
            DeleteCancelled: 0,
            Tombstoned: 0,
            SemanticAutoResolved: 0,
            BusinessWorkItemActionable: 0,
            FinalConvergenceStatus: "NoOpConverged",
            StoppedReason: string.Empty,
            AuditIds: [],
            ProjectIds: [],
            IsReplay: false,
            RunExists: true,
            Status: "Completed",
            LatestBatchReceived: false,
            RequestIdentityHash: string.Empty,
            LatestBatch: null);

    private static ScheduledGovernanceExecuteRequest CreateExecuteRequest(
        string runId,
        string snapshotToken)
        => new(
            runId,
            snapshotToken,
            ToolContractVersion: ScheduledGovernanceContract.ToolContractVersion,
            SchemaHash: ScheduledGovernanceContract.SchemaHash);

    private static GovernanceRunReceiptResult CreateDecisionReceipt(
        string runId,
        ScheduledGovernanceDecision decision,
        string snapshotToken = "snapshot-1",
        GovernanceBatchOutcomeResult? latestBatch = null)
    {
        var automationActionable = decision == ScheduledGovernanceDecision.ReversibleExecutionRequired ? 1 : 0;
        return CreateReceipt(
            runId,
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion) with
        {
            InitialSnapshotToken = snapshotToken,
            FinalSnapshotToken = snapshotToken,
            CoverageComplete = decision != ScheduledGovernanceDecision.CoverageIncomplete,
            InitialGovernanceActionable = automationActionable,
            FinalGovernanceActionable = automationActionable,
            CandidateCount = automationActionable,
            ExecutionActionableCount = automationActionable,
            FinalConvergenceStatus = decision.ToString(),
            LatestBatchReceived = latestBatch is not null,
            LatestBatch = latestBatch
        };
    }

    private static GovernanceBatchOutcomeResult CreateBatchOutcome(
        string snapshotToken,
        string cursorBefore,
        string? nextCursor,
        bool requiresReReview = false,
        bool isReplay = false)
    {
        var now = DateTimeOffset.UtcNow;
        return new GovernanceBatchOutcomeResult(
            Received: true,
            Executed: true,
            RequestIdentityHash: "request-identity",
            RequestHash: "request-hash",
            Status: "Completed",
            FailurePhase: string.Empty,
            ReceivedAt: now,
            StartedAt: now.AddSeconds(-1),
            CompletedAt: now,
            SnapshotToken: snapshotToken,
            SnapshotGeneration: 0,
            IsReReview: false,
            CursorBefore: cursorBefore,
            NextCursor: nextCursor,
            HasMore: nextCursor is not null,
            RequiresReReview: requiresReReview,
            StoppedReason: "Completed",
            Scanned: 1,
            Attempted: 1,
            Applied: 1,
            NoOp: 0,
            Failed: 0,
            Deferred: 0,
            RequiresUserDecision: 0,
            Quarantined: 0,
            DeleteEligible: 0,
            DeleteMatured: 0,
            AutoDeleted: 0,
            DeleteCancelled: 0,
            Tombstoned: 0,
            SemanticAutoResolved: 0,
            RemainingHumanDecision: 0,
            ProtectedRetention: 0,
            AuditIds: [],
            IsReplay: isReplay);
    }

    private static KnowledgeReviewResult CreateReview(
        IReadOnlyList<GovernanceReviewItem> items,
        bool countInvariant = true,
        int governedDeferred = 0,
        int governedRequiresUserDecision = 0,
        int governedHostBlocked = 0)
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
            BusinessWorkItemActionableCount = 3,
            DeferredCount = governedDeferred,
            RequiresUserDecisionCount = governedRequiresUserDecision,
            HostBlockedCount = governedHostBlocked
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

    private sealed class ThrowingKnowledgeReviewService : IKnowledgeReviewService
    {
        public Task<KnowledgeReviewResult> ReviewAsync(KnowledgeReviewRequest request, CancellationToken cancellationToken)
            => throw new InvalidOperationException("fixture failure");
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

    private sealed class StubReceipts(
        GovernanceRunReceiptResult? receipt = null,
        GovernanceRunLineageResult? lineage = null) : IGovernanceRunReceiptService
    {
        public int ReviewStartedCount { get; private set; }
        public int ReviewStoppedCount { get; private set; }
        public string? LastReviewStartedRunId { get; private set; }
        public ScheduledGovernanceReviewResult? LastScheduledDecision { get; private set; }

        private GovernanceRunLineageResult Lineage
        {
            get
            {
                if (lineage is not null)
                {
                    return lineage;
                }

                if (receipt is null)
                {
                    return new(true, true, true, "Valid", "unit-test lineage");
                }

                var scheduled = string.Equals(receipt.ExecutionMode, "Scheduled", StringComparison.Ordinal);
                var contractMatches = receipt.ToolContractVersion == ScheduledGovernanceContract.ToolContractVersion &&
                                       receipt.SchemaHash == ScheduledGovernanceContract.SchemaHash &&
                                       receipt.PublishedCatalogVersion == ScheduledGovernanceContract.PublishedCatalogVersion;
                return scheduled && contractMatches
                    ? new(true, true, true, "Valid", "unit-test lineage")
                    : new(true, scheduled, contractMatches,
                        scheduled ? "ContractMismatch" : "ModeMismatch",
                        "unit-test lineage mismatch");
            }
        }

        public Task RecordReviewStartedAsync(string governanceRunId, DateTimeOffset startedAt, GovernanceReceiptContractIdentity contractIdentity, CancellationToken cancellationToken)
        {
            ReviewStartedCount++;
            LastReviewStartedRunId = governanceRunId;
            return Task.CompletedTask;
        }

        public Task RecordReviewAsync(KnowledgeReviewResult result, DateTimeOffset startedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordScheduledDecisionAsync(ScheduledGovernanceReviewResult result, DateTimeOffset startedAt, CancellationToken cancellationToken)
        {
            LastScheduledDecision = result;
            return Task.CompletedTask;
        }
        public Task<IAsyncDisposable> AcquireRunLockAsync(string governanceRunId, CancellationToken cancellationToken)
            => Task.FromResult<IAsyncDisposable>(new NoopAsyncDisposable());
        public Task<GovernanceRunLineageResult> GetScheduledLineageAsync(string governanceRunId, GovernanceReceiptContractIdentity expectedContractIdentity, CancellationToken cancellationToken)
            => Task.FromResult(Lineage);
        public Task RecordReviewStoppedAsync(string governanceRunId, DateTimeOffset startedAt, string status, string stoppedReason, string failurePhase, GovernanceReceiptContractIdentity contractIdentity, CancellationToken cancellationToken)
        {
            ReviewStoppedCount++;
            return Task.CompletedTask;
        }
        public Task RecordExecutionStartedAsync(GovernanceBatchExecuteRequest request, DateTimeOffset startedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordExecutionAsync(GovernanceBatchExecuteRequest request, GovernanceBatchExecuteResult result, DateTimeOffset startedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task RecordExecutionStoppedAsync(GovernanceBatchExecuteRequest request, DateTimeOffset startedAt, string status, string stoppedReason, string failurePhase, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GovernanceBatchExecuteResult?> GetTerminalPreExecutionReplayAsync(GovernanceBatchExecuteRequest request, CancellationToken cancellationToken) => Task.FromResult<GovernanceBatchExecuteResult?>(null);
        public Task RecordInternalRetentionAsync(InternalMaturedDeleteBatchResult result, DateTimeOffset startedAt, CancellationToken cancellationToken) => Task.CompletedTask;
        public Task<GovernanceRunReceiptResult?> GetAsync(string governanceRunId, CancellationToken cancellationToken) => Task.FromResult(receipt);
        public Task<IReadOnlyList<GovernanceRunReceiptResult>> ListAsync(GovernanceRunReceiptListRequest request, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<GovernanceRunReceiptResult>>([]);
    }

    private sealed class NoopAsyncDisposable : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CountingReliability : IScheduledGovernanceReliabilityService
    {
        public int ObserveCount { get; private set; }
        public int GetCount { get; private set; }

        public Task<ScheduledGovernanceReliabilitySummary> ObserveAsync(
            GovernanceRunReceiptResult receipt,
            CancellationToken cancellationToken)
        {
            ObserveCount++;
            return Task.FromResult<ScheduledGovernanceReliabilitySummary>(null!);
        }

        public Task<ScheduledGovernanceReliabilitySummary> GetAsync(CancellationToken cancellationToken)
        {
            GetCount++;
            return Task.FromResult<ScheduledGovernanceReliabilitySummary>(null!);
        }
    }
}

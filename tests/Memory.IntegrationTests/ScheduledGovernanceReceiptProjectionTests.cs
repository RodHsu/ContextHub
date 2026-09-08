using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ScheduledGovernanceReceiptProjectionTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Scheduled_decision_projection_must_survive_execution_and_exact_replay()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-projection-{Guid.NewGuid():N}";
        var review = new ScheduledGovernanceReviewResult(
            runId,
            IsReReview: false,
            ScheduledGovernanceDecision.ReversibleExecutionRequired,
            SnapshotToken: "kg:snapshot:i0",
            new ScheduledGovernanceCountInvariant(4, 4, 4, 4, 1, 0, true, true),
            CoverageComplete: true,
            CandidateCount: 4,
            ReversibleExecutionCount: 2,
            HumanDecisionCount: 1,
            GovernedExceptionCount: 1,
            BusinessWorkItemActionableCount: 0,
            ResolvedProjectIds: [ProjectContext.SharedProjectId],
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            CurrentReviewHumanDecisionCandidateCount: 1,
            GovernedRequiresUserDecisionExceptionCount: 1,
            GovernedHostBlockedExceptionCount: 0,
            GovernedDeferredExceptionCount: 0,
            ExceptionDelta: new GovernanceExceptionDeltaResult(0, 0, 1, 0),
            RuntimeIdentity: ScheduledGovernanceContract.RuntimeIdentity);

        await receipts.RecordScheduledDecisionAsync(review, DateTimeOffset.UtcNow, CancellationToken.None);
        var projected = await receipts.GetAsync(runId, CancellationToken.None);

        projected.Should().NotBeNull();
        projected!.ExecutionActionableCount.Should().Be(2);
        projected.RequiresUserDecision.Should().Be(1);
        projected.FinalConvergenceStatus.Should().Be(nameof(ScheduledGovernanceDecision.ReversibleExecutionRequired));
        projected.ExceptionDelta.Should().Be(new GovernanceExceptionDeltaResult(0, 0, 1, 0));

        var request = new GovernanceBatchExecuteRequest(
            runId,
            [ProjectContext.SharedProjectId],
            "kg:snapshot:i0",
            MaxMutations: 10,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.Archive],
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled,
            ToolContractVersion: GovernanceToolContract.ToolContractVersion,
            SchemaHash: GovernanceToolContract.SchemaHash)
        {
            ReceiptContractIdentity = new GovernanceReceiptContractIdentity(
                ScheduledGovernanceContract.ToolContractVersion,
                ScheduledGovernanceContract.SchemaHash,
                ScheduledGovernanceContract.PublishedCatalogVersion)
        };
        var execution = new GovernanceBatchExecuteResult(
            ScannedCount: 2,
            AttemptedCount: 1,
            AppliedCount: 1,
            NoOpCount: 0,
            FailedCount: 0,
            DeferredCount: 0,
            RequiresUserDecisionCount: 0,
            MergedCount: 0,
            UpdatedCount: 0,
            MovedCount: 0,
            ArchivedCount: 1,
            ReindexedCount: 0,
            DeleteProposalCount: 0,
            NextCursor: null,
            HasMore: false,
            RequiresReReview: true,
            Items: [],
            AuditIds: [],
            SnapshotToken: "kg:snapshot:i0",
            StoppedReason: "Completed")
        {
            GovernanceRunId = runId,
            ErrorCode = GovernanceBatchErrorCode.None,
            RemainingHumanDecisionCount = 1
        };

        await receipts.RecordExecutionStartedAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        await receipts.RecordExecutionAsync(request, execution, DateTimeOffset.UtcNow, CancellationToken.None);
        var afterExecution = await receipts.GetAsync(runId, CancellationToken.None);

        afterExecution.Should().NotBeNull();
        afterExecution!.ExecutionActionableCount.Should().Be(2);
        afterExecution.RequiresUserDecision.Should().Be(1);
        afterExecution.FinalConvergenceStatus.Should().Be(nameof(ScheduledGovernanceDecision.ReversibleExecutionRequired));
        afterExecution.LatestBatchReceived.Should().BeTrue();
        var latestEntity = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .GovernanceRunReceipts
            .Where(x => x.GovernanceRunId == runId)
            .OrderByDescending(x => x.EventSequence)
            .FirstAsync();
        latestEntity.FinalConvergenceStatus.Should().Be(nameof(ScheduledGovernanceDecision.ReversibleExecutionRequired));
        var scheduledService = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>();
        var runAfterExecution = await scheduledService.GetReceiptAsync(runId, CancellationToken.None);
        runAfterExecution.Decision.Should().BeNull();
        runAfterExecution.Outcome.Should().Be("ReReviewRequired");

        await receipts.RecordExecutionAsync(request, execution with { IsReplay = true }, DateTimeOffset.UtcNow, CancellationToken.None);
        var afterReplay = await receipts.GetAsync(runId, CancellationToken.None);

        afterReplay.Should().NotBeNull();
        afterReplay!.ExecutionActionableCount.Should().Be(2);
        afterReplay.RequiresUserDecision.Should().Be(1);
        afterReplay.FinalConvergenceStatus.Should().Be(nameof(ScheduledGovernanceDecision.ReversibleExecutionRequired));
        afterReplay.Applied.Should().Be(1);
        afterReplay.IsReplay.Should().BeTrue();
        var runAfterReplay = await scheduledService.GetReceiptAsync(runId, CancellationToken.None);
        runAfterReplay.Decision.Should().BeNull();
        runAfterReplay.Outcome.Should().Be("ReReviewRequired");
        actor.Should().NotBeNull();
    }

    [DockerRequiredFact]
    public async Task Scheduled_lineage_rejects_same_actor_generic_review_pollution()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var scheduledService = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"scheduled-lineage-polluted-{Guid.NewGuid():N}";
        var identity = new GovernanceReceiptContractIdentity(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);

        await receipts.RecordReviewStartedAsync(
            runId,
            DateTimeOffset.UtcNow,
            identity,
            CancellationToken.None);
        var pollution = () => receipts.RecordReviewAsync(
            CreateGenericReview(runId),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await pollution.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.SchemaCapabilityMismatch);

        var lineage = await receipts.GetScheduledLineageAsync(
            runId,
            identity,
            CancellationToken.None);
        lineage.Status.Should().Be("Valid");
        var lineageEvents = await db.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.GovernanceRunId == runId)
            .OrderBy(x => x.EventSequence)
            .ToArrayAsync();
        lineageEvents.Select(x => x.ExecutionMode)
            .Should().Equal("Scheduled");

        var receipt = await scheduledService.GetReceiptAsync(runId, CancellationToken.None);

        receipt.RunExists.Should().BeTrue();
        receipt.Status.Should().NotBe("ModeMismatch");
        receipt.Outcome.Should().NotBe("ModeMismatch");
    }

    private static ContextHubRequestActor UseBootstrapActor(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SecurityManage, SecurityScopes.ScheduledGovernance],
            [],
            true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }

    private static KnowledgeReviewResult CreateGenericReview(string runId)
    {
        var durable = new KnowledgeGovernanceCoverageResult(
            Guid.NewGuid(),
            "kg:snapshot:i0",
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            0,
            0,
            0,
            true,
            false,
            null)
        {
            AuthorizedGovernanceDurableMemoryCount = 0,
            GovernanceCoveredDurableMemoryCount = 0,
            GovernanceProjectIds = [ProjectContext.SharedProjectId]
        };
        var surface = new GovernanceSurfaceCoverageResult(0, 0, 0, 0, 0, 0, 0, false, true);
        var coverage = new FullGovernanceCoverageResult(
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface,
            surface);
        var page = new KnowledgeReviewPageResult(0, 200, 0, 0, false);
        var pagination = new KnowledgeReviewPaginationResult(page, page, page, page, page, page, page, page);
        var convergence = new KnowledgeReviewConvergenceResult("Review", 0, false, true)
        {
            CoverageComplete = true
        };
        return new KnowledgeReviewResult(
            [new AccessibleProjectResult(ProjectContext.SharedProjectId, true, true)],
            null!,
            [],
            [],
            [],
            [],
            [],
            [],
            [],
            runId,
            false,
            pagination,
            convergence)
        {
            DurableMemoryCoverage = durable,
            GovernanceCoverage = coverage,
            GovernancePlan = [],
            CandidateCount = 0,
            ExecutionActionableCount = 0,
            GovernedExceptionCount = 0,
            ReceiptContractIdentity = null
        };
    }
}

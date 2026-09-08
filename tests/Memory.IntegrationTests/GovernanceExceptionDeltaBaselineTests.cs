using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class GovernanceExceptionDeltaBaselineTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Fresh_run_should_compare_exceptions_with_latest_completed_review_for_same_scope()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var projectId = $"exception-baseline-{Guid.NewGuid():N}";
        var firstRunId = $"exception-baseline-first-{Guid.NewGuid():N}";
        var secondRunId = $"exception-baseline-second-{Guid.NewGuid():N}";
        var identity = ContractIdentity();

        await receipts.RecordReviewAsync(
            CreateReview(firstRunId, projectId,
            [
                new GovernanceExceptionStateResult("finding:a", "GovernanceFinding", "Deferred", 1),
                new GovernanceExceptionStateResult("finding:b", "GovernanceFinding", "RequiresUserDecision", 2)
            ]) with
            { ReceiptContractIdentity = identity },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await receipts.RecordReviewStartedAsync(
            secondRunId,
            DateTimeOffset.UtcNow,
            identity,
            CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateReview(secondRunId, projectId,
            [
                new GovernanceExceptionStateResult("finding:a", "GovernanceFinding", "Deferred", 1),
                new GovernanceExceptionStateResult("finding:b", "GovernanceFinding", "RequiresUserDecision", 2)
            ]) with
            { ReceiptContractIdentity = identity },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var receipt = await receipts.GetAsync(secondRunId, CancellationToken.None);

        receipt.Should().NotBeNull();
        receipt!.ExceptionDelta.Should().Be(new GovernanceExceptionDeltaResult(0, 0, 2, 0));

        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.GovernanceRunReceipts.AsNoTracking().CountAsync(x =>
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.UserId &&
            x.GovernanceRunId == secondRunId)).Should().Be(2,
            "the baseline comparison must not synthesize or mutate prior receipts");
    }

    [DockerRequiredFact]
    public async Task Fresh_run_should_not_reuse_exception_baseline_from_different_project_scope()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var firstRunId = $"exception-scope-first-{Guid.NewGuid():N}";
        var secondRunId = $"exception-scope-second-{Guid.NewGuid():N}";
        var identity = ContractIdentity();
        var exception = new GovernanceExceptionStateResult(
            "finding:shared-key",
            "GovernanceFinding",
            "RequiresUserDecision",
            2);

        await receipts.RecordReviewAsync(
            CreateReview(firstRunId, $"exception-scope-a-{Guid.NewGuid():N}", [exception]) with
            {
                ReceiptContractIdentity = identity
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await receipts.RecordReviewStartedAsync(
            secondRunId,
            DateTimeOffset.UtcNow,
            identity,
            CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateReview(secondRunId, $"exception-scope-b-{Guid.NewGuid():N}", [exception]) with
            {
                ReceiptContractIdentity = identity
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var receipt = await receipts.GetAsync(secondRunId, CancellationToken.None);

        receipt.Should().NotBeNull();
        receipt!.ExceptionDelta.Should().Be(new GovernanceExceptionDeltaResult(1, 0, 0, 0));
    }

    private static GovernanceReceiptContractIdentity ContractIdentity()
        => new(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);

    private static KnowledgeReviewResult CreateReview(
        string runId,
        string projectId,
        IReadOnlyList<GovernanceExceptionStateResult> exceptionStates)
    {
        var durable = new KnowledgeGovernanceCoverageResult(
            Guid.NewGuid(),
            $"snapshot-{Guid.NewGuid():N}",
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
            GovernanceProjectIds = [projectId, ProjectContext.SharedProjectId]
        };
        var surface = new GovernanceSurfaceCoverageResult(0, 0, 0, 0, 0, 0, 0, false, true);
        var coverage = new FullGovernanceCoverageResult(
            surface, surface, surface, surface, surface, surface,
            surface, surface, surface, surface, surface);
        var page = new KnowledgeReviewPageResult(0, 200, 0, 0, false);
        var convergence = new KnowledgeReviewConvergenceResult("ConvergedWithExceptions", 0, false, true)
        {
            CoverageComplete = true,
            GovernedExceptionCount = exceptionStates.Count,
            DeferredCount = exceptionStates.Count(x => x.Disposition == "Deferred"),
            RequiresUserDecisionCount = exceptionStates.Count(x => x.Disposition == "RequiresUserDecision"),
            HostBlockedCount = exceptionStates.Count(x => x.Disposition == "HostBlocked")
        };

        return new KnowledgeReviewResult(
            [new AccessibleProjectResult(projectId, true, true)],
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
            new KnowledgeReviewPaginationResult(page, page, page, page, page, page, page, page),
            convergence)
        {
            DurableMemoryCoverage = durable,
            GovernanceCoverage = coverage,
            GovernedExceptionCount = exceptionStates.Count,
            GovernedExceptionStates = exceptionStates
        };
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
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SecurityManage],
            [],
            true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }
}

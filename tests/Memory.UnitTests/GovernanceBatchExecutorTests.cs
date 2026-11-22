using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class GovernanceBatchExecutorTests
{
    [Fact]
    public void Scheduled_Request_Should_Require_Bounds_Snapshot_And_Reject_Direct_Hard_Delete()
    {
        var valid = new GovernanceBatchExecuteRequest(
            "unit-run",
            ["ContextHub"],
            "snapshot-token",
            MaxMutations: 100,
            MaxDurationSeconds: 120,
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);

        var validAction = () => GovernanceBatchExecutor.ValidateRequest(valid);
        validAction.Should().NotThrow();
        ((Action)(() => GovernanceBatchExecutor.ValidateRequest(valid with { AllowMaturedDelete = true })))
            .Should().NotThrow("scheduled execution may consume only already-matured retention candidates");
        ((Action)(() => GovernanceBatchExecutor.ValidateRequest(valid with { SnapshotToken = null })))
            .Should().Throw<InvalidOperationException>().WithMessage("*snapshotToken*");
        ((Action)(() => GovernanceBatchExecutor.ValidateRequest(valid with { AllowHardDelete = true })))
            .Should().Throw<InvalidOperationException>().WithMessage("*AllowHardDelete=false*");
        ((Action)(() => GovernanceBatchExecutor.ValidateRequest(valid with { MaxMutations = 0 })))
            .Should().Throw<InvalidOperationException>().WithMessage("*MaxMutations*");
        ((Action)(() => GovernanceBatchExecutor.ValidateRequest(valid with { MaxDurationSeconds = 901 })))
            .Should().Throw<InvalidOperationException>().WithMessage("*MaxDurationSeconds*");
        ((Action)(() => GovernanceBatchExecutor.ValidateRequest(valid with { MaxRiskLevel = (GovernanceBatchRiskLevel)999 })))
            .Should().Throw<InvalidOperationException>().WithMessage("*MaxRiskLevel*");
        ((Action)(() => GovernanceBatchExecutor.ValidateRequest(valid with { ExecutionMode = (GovernanceBatchExecutionMode)999 })))
            .Should().Throw<GovernanceBatchException>()
            .Where(ex => ex.Code == GovernanceBatchErrorCode.SchemaCapabilityMismatch)
            .WithMessage("*ExecutionMode*");
        ((Action)(() => GovernanceBatchExecutor.ValidateRequest(valid with { SemanticAutoResolutionConfidenceThreshold = 1.01m })))
            .Should().Throw<InvalidOperationException>().WithMessage("*SemanticAutoResolutionConfidenceThreshold*");
    }

    [Fact]
    public async Task Historical_work_item_snapshot_without_original_revision_records_decision_without_attempting_archive()
    {
        const string project = "ContextHub";
        var workItemId = Guid.NewGuid();
        var db = DispatchProxy.Create<IApplicationDbContext, WorkItemProposalServiceTests.ProposalContext>();
        var fixture = (WorkItemProposalServiceTests.ProposalContext)(object)db;
        var actor = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "operator", TenantUserRole.Owner,
            [SecurityScopes.MemoryWrite], [project], true);
        var actors = new RequestActorAccessor { Current = actor };
        var executor = new GovernanceBatchExecutor(db, null!, null!, null!, null!, null!, null!, null!, null!,
            actors, new SystemClock(), null!, null!);
        var itemType = typeof(GovernanceBatchExecutor).GetNestedType("BatchPlanItem", BindingFlags.NonPublic)!;
        var savedItem = JsonSerializer.Deserialize(JsonSerializer.Serialize(new
        {
            Key = $"workitem:{workItemId:N}",
            Kind = nameof(GovernanceItemKind.WorkItem),
            Id = workItemId,
            ProjectId = project,
            SnapshotToken = "snapshot-token",
            GovernanceRunId = "legacy-run",
            Classification = "CompletedHistoricalWorkItem",
            RecommendedAction = nameof(GovernanceBatchActionType.WorkItemReconcile)
        }), itemType)!;
        var request = new GovernanceBatchExecuteRequest("legacy-run", [project], "snapshot-token",
            ExecutionMode: GovernanceBatchExecutionMode.Manual);
        var allowed = new HashSet<GovernanceBatchActionType> { GovernanceBatchActionType.WorkItemReconcile };
        var dispatch = typeof(GovernanceBatchExecutor).GetMethod("ProcessTypedSurfaceAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

        var result = await (Task<GovernanceBatchItemResult>)dispatch.Invoke(executor,
            [savedItem, request, allowed, actor, CancellationToken.None])!;

        result.ActionType.Should().Be(GovernanceBatchActionType.WorkItemReconcile);
        result.Disposition.Should().Be(GovernanceBatchItemDisposition.RequiresUserDecision);
        result.Summary.Should().Contain("original Work Item revision");
        result.ProposalIds.Should().BeEmpty();
        result.ResourceIds.Should().BeEmpty();
        fixture.ForbiddenCalls.Should().BeEmpty();
        fixture.Audits.Rows.Should().ContainSingle().Which.Outcome.Should().Be(nameof(GovernanceBatchItemDisposition.RequiresUserDecision));
        fixture.Saves.Should().Be(1);
    }
}

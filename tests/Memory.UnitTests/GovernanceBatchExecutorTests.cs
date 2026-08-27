using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class GovernanceBatchExecutorTests
{
    [Fact]
    public void Scheduled_Request_Should_Require_Bounds_Snapshot_And_No_Hard_Delete()
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
    }
}

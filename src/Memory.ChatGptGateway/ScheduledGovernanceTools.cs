using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Memory.Application;
using ModelContextProtocol.Server;

namespace Memory.ChatGptGateway;

[McpServerToolType]
public sealed class ScheduledGovernanceTools(
    IScheduledGovernanceService governance,
    ILogger<ScheduledGovernanceTools>? logger = null)
{
    [McpServerTool(UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read the immutable contract for the dedicated Scheduled Governance least-privilege surface, including fixed decisions and reversible actions. Irreversible retention is owned only by the ContextHub internal worker.")]
    public ScheduledGovernanceContractResult scheduled_governance_contract_get()
        => ScheduledGovernanceContract.Describe();

    [McpServerTool(UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ScheduledGovernanceContract.ReviewDescription)]
    public Task<ScheduledGovernanceReviewResult> scheduled_governance_review(
        ScheduledGovernanceReviewRequest request,
        CancellationToken cancellationToken = default)
        => MeasureAsync("review", () => governance.ReviewAsync(request, cancellationToken));

    [McpServerTool(UseStructuredContent = true, ReadOnly = false, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(ScheduledGovernanceContract.ExecuteDescription)]
    public async Task<ScheduledGovernanceExecutionResult> scheduled_governance_execute(
        ScheduledGovernanceExecuteRequest request,
        CancellationToken cancellationToken = default)
    {
        try
        {
            return await governance.ExecuteAsync(request, cancellationToken);
        }
        catch (GovernanceBatchException ex)
        {
            var failure = GovernanceBatchExecuteResult.Failure(new GovernanceBatchExecuteRequest(
                request.GovernanceRunId,
                SnapshotToken: request.SnapshotToken,
                Cursor: request.Cursor,
                MaxMutations: request.MaxMutations,
                MaxDurationSeconds: request.MaxDurationSeconds,
                IsReReview: request.IsReReview), ex);
            return ScheduledGovernanceService.ToScheduledResult(failure);
        }
    }

    [McpServerTool(UseStructuredContent = true, ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description("Read the latest immutable receipt and replay/recovery state for one Scheduled Governance run. A structured runExists=false and received=false result means ContextHub did not receive that run for the current actor.")]
    public Task<ScheduledGovernanceRunResult> scheduled_governance_run_get(
        string governanceRunId,
        CancellationToken cancellationToken = default,
        [Description("Set false to read the single-run receipt without recalculating reliability history.")]
        bool includeReliabilityHistory = true)
        => MeasureAsync(
            includeReliabilityHistory ? "receipt-with-history" : "receipt",
            () => governance.GetReceiptAsync(governanceRunId, cancellationToken, includeReliabilityHistory));

    private async Task<TResult> MeasureAsync<TResult>(string operation, Func<Task<TResult>> action)
    {
        var started = Stopwatch.GetTimestamp();
        try
        {
            return await action();
        }
        finally
        {
            logger?.LogInformation("Scheduled governance {Operation} took {ElapsedMilliseconds} ms",
                operation, Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        }
    }
}

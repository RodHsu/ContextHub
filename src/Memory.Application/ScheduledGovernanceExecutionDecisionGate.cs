namespace Memory.Application;

/// <summary>
/// Enforces the server-owned decision boundary immediately before a Scheduled
/// governance batch can reach the mutation executor. The caller cannot select
/// a decision; the latest persisted scheduled projection must authorize the
/// bounded reversible policy for the same run and snapshot.
/// </summary>
internal static class ScheduledGovernanceExecutionDecisionGate
{
    public static void EnsureReversible(
        GovernanceRunReceiptResult? receipt,
        GovernanceBatchExecuteRequest request)
    {
        const string expectedDecision = nameof(ScheduledGovernanceDecision.ReversibleExecutionRequired);
        var snapshotToken = request.SnapshotToken?.Trim() ?? string.Empty;

        if (receipt is null)
        {
            Reject("missing-or-mismatched-persisted-run");
            return;
        }

        if (!receipt.RunExists ||
            !string.Equals(receipt.GovernanceRunId, request.GovernanceRunId, StringComparison.Ordinal))
        {
            Reject("missing-or-mismatched-persisted-run");
        }

        if (!string.Equals(receipt.ExecutionMode, "Scheduled", StringComparison.Ordinal) ||
            !string.Equals(receipt.ToolContractVersion, ScheduledGovernanceContract.ToolContractVersion, StringComparison.Ordinal) ||
            !string.Equals(receipt.SchemaHash, ScheduledGovernanceContract.SchemaHash, StringComparison.Ordinal) ||
            !string.Equals(receipt.PublishedCatalogVersion, ScheduledGovernanceContract.PublishedCatalogVersion, StringComparison.Ordinal))
        {
            Reject("persisted-scheduled-contract-mismatch", GovernanceBatchErrorCode.SchemaCapabilityMismatch);
        }

        var completed = string.Equals(receipt.Status, "Completed", StringComparison.OrdinalIgnoreCase);
        var failurePhase = receipt.LatestBatch?.FailurePhase;
        var safePreExecutionFailure =
            string.Equals(receipt.Status, "Failed", StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(failurePhase, "PreExecutionValidation", StringComparison.Ordinal) ||
             string.Equals(failurePhase, "PreExecutionScopeValidation", StringComparison.Ordinal));
        if (!completed && !safePreExecutionFailure)
        {
            Reject($"persisted-receipt-status-{receipt.Status}");
        }

        if (completed &&
            receipt.LatestBatch is { } latestBatch &&
            (!latestBatch.Executed ||
             latestBatch.Failed > 0 ||
             string.Equals(latestBatch.FailurePhase, "ItemExecution", StringComparison.Ordinal) ||
             string.Equals(latestBatch.StoppedReason, "ItemFailed", StringComparison.Ordinal) ||
             string.Equals(latestBatch.StoppedReason, "UnknownResult", StringComparison.Ordinal)))
        {
            Reject("persisted-latest-batch-requires-re-review");
        }

        if (!string.Equals(receipt.FinalConvergenceStatus, expectedDecision, StringComparison.Ordinal))
        {
            Reject(
                $"persisted-decision-{(string.IsNullOrWhiteSpace(receipt.FinalConvergenceStatus) ? "missing" : receipt.FinalConvergenceStatus)}");
        }

        if (!receipt.CoverageComplete ||
            receipt.ExecutionActionableCount <= 0 ||
            receipt.FinalGovernanceActionable != receipt.ExecutionActionableCount)
        {
            Reject("persisted-reversible-plan-is-empty-or-inconsistent");
        }

        if (snapshotToken.Length == 0 ||
            !string.Equals(receipt.FinalSnapshotToken, snapshotToken, StringComparison.Ordinal))
        {
            Reject("snapshot-binding-mismatch", GovernanceBatchErrorCode.CursorSnapshotMismatch);
        }

        // Request identity/hash, persisted plan reconstruction, cursor
        // issuance, exact replay, and request-to-plan binding remain owned by
        // GovernanceBatchExecutor. In particular, a cursor is deliberately
        // not compared with LatestBatch.SnapshotToken here: the executor's
        // durable ledger supports continuation across same-run re-review
        // generations while still validating actor, scope, policy, expiry,
        // and exact payload under the run lock.
    }

    private static void Reject(
        string reason,
        GovernanceBatchErrorCode code = GovernanceBatchErrorCode.ReReviewRequired)
        => throw new GovernanceBatchException(
            code,
            $"Scheduled governance execution decision gate rejected ({reason}); perform a fresh scheduled review.");
}

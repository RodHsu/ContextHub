using Memory.Domain;

namespace Memory.Application;

public sealed class ScheduledGovernanceService(
    IKnowledgeReviewService knowledgeReview,
    IGovernanceBatchExecutor batchExecutor,
    IGovernanceRunReceiptService receipts,
    IRequestActorAccessor actorAccessor,
    IScheduledGovernanceReliabilityService? reliability = null) : IScheduledGovernanceService
{
    private static readonly GovernanceReceiptContractIdentity ReceiptContractIdentity = new(
        ScheduledGovernanceContract.ToolContractVersion,
        ScheduledGovernanceContract.SchemaHash,
        ScheduledGovernanceContract.PublishedCatalogVersion);

    public async Task<ScheduledGovernanceReviewResult> ReviewAsync(
        ScheduledGovernanceReviewRequest request,
        CancellationToken cancellationToken)
    {
        EnsureScheduledAuthority();
        var governanceRunId = NormalizeGovernanceRunId(request.GovernanceRunId);
        var lineage = await receipts.GetScheduledLineageAsync(
            governanceRunId,
            ReceiptContractIdentity,
            cancellationToken);
        EnsureScheduledLineage(lineage, requireExistingRun: false);

        var startedAt = DateTimeOffset.UtcNow;
        await receipts.RecordReviewStartedAsync(
            governanceRunId,
            startedAt,
            ReceiptContractIdentity,
            cancellationToken,
            request.IsReReview);

        KnowledgeReviewResult review;
        try
        {
            review = await knowledgeReview.ReviewAsync(
                new KnowledgeReviewRequest(
                    ProjectIds: null,
                    LimitPerSection: 200,
                    Offset: 0,
                    GovernanceRunId: governanceRunId,
                    IsReReview: request.IsReReview)
                {
                    ReceiptContractIdentity = ReceiptContractIdentity
                },
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            await receipts.RecordReviewStoppedAsync(
                governanceRunId,
                startedAt,
                "Stopped",
                "OperationCanceled",
                "KnowledgeReview",
                ReceiptContractIdentity,
                CancellationToken.None,
                request.IsReReview);
            await ObserveReceiptAsync(
                await receipts.GetAsync(governanceRunId, CancellationToken.None),
                CancellationToken.None);
            throw;
        }
        catch (Exception ex)
        {
            await receipts.RecordReviewStoppedAsync(
                governanceRunId,
                startedAt,
                "Failed",
                ex.GetType().Name,
                "KnowledgeReview",
                ReceiptContractIdentity,
                CancellationToken.None,
                request.IsReReview);
            await ObserveReceiptAsync(
                await receipts.GetAsync(governanceRunId, CancellationToken.None),
                CancellationToken.None);
            throw;
        }

        var durableCoverage = review.DurableMemoryCoverage
            ?? throw new InvalidOperationException("Scheduled governance requires durable-memory coverage evidence.");
        var governanceCoverage = review.GovernanceCoverage
            ?? throw new InvalidOperationException("Scheduled governance requires full-surface coverage evidence.");
        var sharedOccurrences = durableCoverage.GovernanceProjectIds.Count(ProjectContext.IsShared);
        var userOccurrences = durableCoverage.GovernanceProjectIds.Count(ProjectContext.IsUser);
        var countInvariant = new ScheduledGovernanceCountInvariant(
            durableCoverage.AuthorizedGovernanceDurableMemoryCount,
            durableCoverage.GovernanceCoveredDurableMemoryCount,
            durableCoverage.ScannedCount,
            durableCoverage.TotalCount,
            sharedOccurrences,
            userOccurrences,
            userOccurrences == 0,
            durableCoverage.CountInvariantSatisfied && sharedOccurrences == 1 && userOccurrences == 0);
        var coverageComplete = countInvariant.Satisfied &&
                               governanceCoverage.CoverageComplete &&
                               !governanceCoverage.HasMore;

        var eligibility = review.GovernancePlan
            .Select(ScheduledGovernanceAutomationEligibility.Evaluate)
            .ToArray();
        var reversible = eligibility.Count(x => x.AutomationActionable);
        var humanDecision = eligibility.Count(x => x.RequiresUserDecision);

        var reviewedRunId = NormalizeGovernanceRunId(review.GovernanceRunId);
        var receipt = await receipts.GetAsync(reviewedRunId, cancellationToken);
        var decision = ResolveDecision(
            coverageComplete,
            reversible,
            humanDecision,
            review.GovernedExceptionCount,
            receipt?.ExceptionDelta);
        var scheduledResult = new ScheduledGovernanceReviewResult(
            reviewedRunId,
            review.IsReReview,
            decision,
            durableCoverage.SnapshotToken,
            countInvariant,
            coverageComplete,
            review.CandidateCount,
            reversible,
            humanDecision,
            review.GovernedExceptionCount,
            review.Convergence.BusinessWorkItemActionableCount,
            durableCoverage.GovernanceProjectIds,
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            humanDecision,
            review.Convergence.RequiresUserDecisionCount,
            review.Convergence.HostBlockedCount,
            review.Convergence.DeferredCount,
            receipt?.ExceptionDelta,
            ScheduledGovernanceContract.RuntimeIdentity);

        await receipts.RecordScheduledDecisionAsync(scheduledResult, startedAt, cancellationToken);
        receipt = await receipts.GetAsync(reviewedRunId, cancellationToken);
        await ObserveReceiptAsync(receipt, cancellationToken);
        return scheduledResult;
    }

    public async Task<ScheduledGovernanceExecutionResult> ExecuteAsync(
        ScheduledGovernanceExecuteRequest request,
        CancellationToken cancellationToken)
    {
        EnsureScheduledAuthority();
        var governanceRunId = NormalizeGovernanceRunId(request.GovernanceRunId);
        ValidateContract(request with { GovernanceRunId = governanceRunId });
        var batchRequest = new GovernanceBatchExecuteRequest(
            governanceRunId,
            ProjectIds: null,
            SnapshotToken: request.SnapshotToken,
            Cursor: request.Cursor,
            MaxMutations: Math.Clamp(request.MaxMutations, 1, 100),
            MaxDurationSeconds: Math.Clamp(request.MaxDurationSeconds, 1, 120),
            AllowedActionTypes: ScheduledGovernanceContract.FixedReversibleActions,
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            IsReReview: request.IsReReview,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled,
            AllowMaturedDelete: false,
            SemanticAutoResolutionConfidenceThreshold: 0.90m,
            ToolContractVersion: GovernanceToolContract.ToolContractVersion,
            SchemaHash: GovernanceToolContract.SchemaHash)
        {
            ReceiptContractIdentity = ReceiptContractIdentity
        };
        var lineage = await receipts.GetScheduledLineageAsync(
            governanceRunId,
            ReceiptContractIdentity,
            cancellationToken);
        EnsureScheduledLineage(lineage, requireExistingRun: true);
        var receipt = await receipts.GetAsync(governanceRunId, cancellationToken);
        ScheduledGovernanceExecutionDecisionGate.EnsureReversible(receipt, batchRequest);
        GovernanceBatchExecuteResult result;
        try
        {
            result = await batchExecutor.ExecuteAsync(batchRequest, cancellationToken);
        }
        catch
        {
            // The batch executor records stopped/failed terminal receipts before
            // propagating unexpected errors. Preserve the reliability projection
            // on this mutation path even when the request itself is cancelled.
            await ObserveReceiptAsync(
                await receipts.GetAsync(governanceRunId, CancellationToken.None),
                CancellationToken.None);
            throw;
        }

        await ObserveReceiptAsync(
            await receipts.GetAsync(governanceRunId, cancellationToken),
            cancellationToken);

        return ToScheduledResult(result);
    }

    public async Task<ScheduledGovernanceRunResult> GetReceiptAsync(
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        EnsureScheduledAuthority();
        var normalizedRunId = NormalizeGovernanceRunId(governanceRunId);
        var receipt = await receipts.GetAsync(normalizedRunId, cancellationToken);
        if (receipt is null)
        {
            var missing = MissingRun(normalizedRunId);
            return reliability is null
                ? missing
                : missing with { Reliability = await reliability.GetAsync(cancellationToken) };
        }

        var lineage = await receipts.GetScheduledLineageAsync(
            normalizedRunId,
            ReceiptContractIdentity,
            cancellationToken);

        // run_get is intentionally read-only. Projection persistence belongs to
        // review/execute mutation paths, never to receipt retrieval.
        var reliabilitySummary = reliability is null
            ? null
            : await reliability.GetAsync(cancellationToken);
        if (!lineage.IsValid)
        {
            return InvalidLineage(receipt, lineage) with
            {
                Reliability = reliabilitySummary
            };
        }
        var requiresReReview = string.Equals(receipt.ExecutionMode, "Scheduled", StringComparison.Ordinal) &&
                               receipt.LatestBatch is { RequiresReReview: true, Status: "Completed" };
        var decision = requiresReReview ? null : ResolveDecision(receipt);
        var outcome = requiresReReview
            ? "ReReviewRequired"
            : string.IsNullOrWhiteSpace(receipt.FinalConvergenceStatus)
                ? receipt.Status
                : receipt.FinalConvergenceStatus;

        return new ScheduledGovernanceRunResult(
            receipt.ReceiptId,
            receipt.GovernanceRunId,
            receipt.StartedAt,
            receipt.CompletedAt,
            receipt.ToolContractVersion,
            receipt.SchemaHash,
            receipt.PublishedCatalogVersion,
            receipt.InitialSnapshotToken,
            receipt.FinalSnapshotToken,
            receipt.CoverageComplete,
            receipt.InitialGovernanceActionable,
            receipt.FinalGovernanceActionable,
            receipt.CandidateCount,
            receipt.ExecutionActionableCount,
            receipt.GovernedExceptionCount,
            receipt.Applied,
            receipt.Failed,
            receipt.Deferred,
            receipt.RequiresUserDecision,
            receipt.Quarantined,
            receipt.SemanticAutoResolved,
            receipt.BusinessWorkItemActionable,
            receipt.FinalConvergenceStatus,
            receipt.StoppedReason,
            receipt.AuditIds,
            receipt.ProjectIds,
            receipt.IsReplay,
            receipt.RunExists,
            receipt.Status,
            receipt.LatestBatchReceived,
            receipt.RequestIdentityHash,
            receipt.ExceptionDelta,
            ScheduledGovernanceContract.RuntimeIdentity)
        {
            Received = true,
            Terminal = !string.Equals(receipt.Status, "Running", StringComparison.OrdinalIgnoreCase),
            Decision = decision,
            Outcome = outcome,
            Reliability = reliabilitySummary
        };
    }

    private static void EnsureScheduledLineage(
        GovernanceRunLineageResult lineage,
        bool requireExistingRun)
    {
        if ((!requireExistingRun && !lineage.RunExists) || lineage.IsValid)
        {
            return;
        }

        var status = lineage.RunExists ? lineage.Status : "NotReceived";
        throw new GovernanceBatchException(
            GovernanceBatchErrorCode.SchemaCapabilityMismatch,
            $"Scheduled governance run lineage rejected: {status}.");
    }

    private static ScheduledGovernanceRunResult InvalidLineage(
        GovernanceRunReceiptResult receipt,
        GovernanceRunLineageResult lineage)
        => new(
            ReceiptId: receipt.ReceiptId,
            GovernanceRunId: receipt.GovernanceRunId,
            StartedAt: receipt.StartedAt,
            CompletedAt: receipt.CompletedAt,
            ToolContractVersion: receipt.ToolContractVersion,
            SchemaHash: receipt.SchemaHash,
            PublishedCatalogVersion: receipt.PublishedCatalogVersion,
            InitialSnapshotToken: string.Empty,
            FinalSnapshotToken: string.Empty,
            CoverageComplete: false,
            InitialGovernanceActionable: 0,
            FinalGovernanceActionable: 0,
            CandidateCount: 0,
            ReversibleExecutionActionableCount: 0,
            GovernedExceptionCount: 0,
            Applied: 0,
            Failed: 0,
            Deferred: 0,
            RequiresUserDecision: 0,
            Quarantined: 0,
            SemanticAutoResolved: 0,
            BusinessWorkItemActionable: 0,
            FinalConvergenceStatus: lineage.Status,
            StoppedReason: lineage.Reason,
            AuditIds: [],
            ProjectIds: [],
            IsReplay: false,
            RunExists: true,
            Status: lineage.Status,
            LatestBatchReceived: false,
            RequestIdentityHash: string.Empty,
            ExceptionDelta: new GovernanceExceptionDeltaResult(0, 0, 0, 0),
            RuntimeIdentity: ScheduledGovernanceContract.RuntimeIdentity)
        {
            Received = true,
            Terminal = true,
            Decision = null,
            Outcome = lineage.Status
        };

    private static ScheduledGovernanceRunResult MissingRun(string governanceRunId)
        => new(
            Guid.Empty,
            governanceRunId?.Trim() ?? string.Empty,
            DateTimeOffset.UnixEpoch,
            DateTimeOffset.UnixEpoch,
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            string.Empty,
            string.Empty,
            false,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "NotReceived",
            "NotReceived",
            [],
            [],
            false,
            false,
            "NotReceived",
            false,
            string.Empty,
            new GovernanceExceptionDeltaResult(0, 0, 0, 0),
            ScheduledGovernanceContract.RuntimeIdentity)
        {
            Received = false,
            Terminal = false,
            Decision = null,
            Outcome = "NotReceived"
        };

    private static ScheduledGovernanceDecision? ResolveDecision(GovernanceRunReceiptResult receipt)
    {
        if (string.Equals(receipt.Status, "Running", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(receipt.Status, "Failed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(receipt.Status, "Stopped", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (string.Equals(receipt.ExecutionMode, "Scheduled", StringComparison.Ordinal) &&
            Enum.TryParse<ScheduledGovernanceDecision>(receipt.FinalConvergenceStatus, ignoreCase: false, out var persistedDecision))
        {
            return persistedDecision;
        }

        var currentHumanDecision = Math.Max(
            receipt.CandidateCount - receipt.ExecutionActionableCount,
            receipt.RequiresUserDecision + receipt.Deferred + receipt.HostBlocked);
        return ResolveDecision(
            receipt.CoverageComplete,
            receipt.ExecutionActionableCount,
            currentHumanDecision,
            receipt.GovernedExceptionCount,
            receipt.ExceptionDelta);
    }

    private static ScheduledGovernanceDecision ResolveDecision(
        bool coverageComplete,
        int automationActionable,
        int currentHumanDecision,
        int governedExceptionCount,
        GovernanceExceptionDeltaResult? exceptionDelta)
    {
        if (!coverageComplete)
        {
            return ScheduledGovernanceDecision.CoverageIncomplete;
        }

        if (automationActionable > 0)
        {
            return ScheduledGovernanceDecision.ReversibleExecutionRequired;
        }

        if (HasNewOrEscalatedDecision(
                currentHumanDecision,
                governedExceptionCount,
                exceptionDelta))
        {
            return ScheduledGovernanceDecision.HumanDecisionOnly;
        }

        return ScheduledGovernanceDecision.NoOpConverged;
    }

    private static bool HasNewOrEscalatedDecision(
        int currentHumanDecision,
        int governedExceptionCount,
        GovernanceExceptionDeltaResult? exceptionDelta)
    {
        if (currentHumanDecision == 0 && governedExceptionCount == 0)
        {
            return exceptionDelta is { New: > 0 } or { Escalated: > 0 };
        }

        // Without a comparable baseline, preserve the fail-closed decision. A
        // fresh baseline is supplied by GovernanceRunReceiptService across run
        // ids; same-run re-review still compares against the prior review.
        return exceptionDelta is null || exceptionDelta.New > 0 || exceptionDelta.Escalated > 0;
    }

    public static ScheduledGovernanceExecutionResult ToScheduledResult(GovernanceBatchExecuteResult result)
        => new(
            result.GovernanceRunId,
            result.Succeeded,
            result.ScannedCount,
            result.AttemptedCount,
            result.AppliedCount,
            result.NoOpCount,
            result.FailedCount,
            result.DeferredCount,
            result.RequiresUserDecisionCount,
            result.QuarantinedCount,
            result.SemanticAutoResolvedCount,
            result.RemainingHumanDecisionCount,
            result.NextCursor,
            result.HasMore,
            result.RequiresReReview,
            result.Items.Select(item => new ScheduledGovernanceExecutionItem(
                item.ItemKey,
                item.ItemKind,
                item.ResourceId,
                item.ProjectId,
                item.ActionType?.ToString() ?? string.Empty,
                item.Disposition.ToString(),
                item.Summary,
                item.Error,
                item.Retryable,
                item.CursorDisposition,
                item.AuditIds,
                item.ResourceIds,
                item.IsReplay,
                item.SemanticAutoResolved)).ToArray(),
            result.AuditIds,
            result.SnapshotToken,
            result.StoppedReason,
            result.IsReplay,
            result.ElapsedMilliseconds,
            ToScheduledError(result.ErrorCode),
            ScheduledGovernanceContract.RuntimeIdentity);

    private static ScheduledGovernanceExecutionError ToScheduledError(GovernanceBatchErrorCode error)
        => error switch
        {
            GovernanceBatchErrorCode.None => ScheduledGovernanceExecutionError.None,
            GovernanceBatchErrorCode.ReReviewRequired => ScheduledGovernanceExecutionError.ReReviewRequired,
            GovernanceBatchErrorCode.InvalidCursor => ScheduledGovernanceExecutionError.InvalidCursor,
            GovernanceBatchErrorCode.CursorExpired => ScheduledGovernanceExecutionError.CursorExpired,
            GovernanceBatchErrorCode.CursorActorMismatch => ScheduledGovernanceExecutionError.ActorMismatch,
            GovernanceBatchErrorCode.CursorScopeMismatch => ScheduledGovernanceExecutionError.ScopeMismatch,
            GovernanceBatchErrorCode.CursorPolicyMismatch => ScheduledGovernanceExecutionError.PolicyMismatch,
            GovernanceBatchErrorCode.CursorSnapshotMismatch => ScheduledGovernanceExecutionError.SnapshotMismatch,
            GovernanceBatchErrorCode.ReplayPayloadMismatch => ScheduledGovernanceExecutionError.ReplayMismatch,
            GovernanceBatchErrorCode.SchemaCapabilityMismatch => ScheduledGovernanceExecutionError.ContractMismatch,
            _ => ScheduledGovernanceExecutionError.RestrictedActionUnavailable
        };

    private void EnsureScheduledAuthority()
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.ScheduledGovernance);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException("Scheduled governance requires a tenant owner or administrator.");
        }
    }

    internal static string NormalizeGovernanceRunId(string? governanceRunId)
    {
        var normalized = ScheduledGovernanceReliabilityService.NormalizeRunId(governanceRunId);
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("GovernanceRunId is required.");
        }

        return normalized;
    }

    private async Task ObserveReceiptAsync(
        GovernanceRunReceiptResult? receipt,
        CancellationToken cancellationToken)
    {
        if (receipt is not null && reliability is not null)
        {
            await reliability.ObserveAsync(receipt, cancellationToken);
        }
    }

    private static void ValidateContract(ScheduledGovernanceExecuteRequest request)
    {
        NormalizeGovernanceRunId(request.GovernanceRunId);
        if (string.IsNullOrWhiteSpace(request.SnapshotToken))
        {
            throw new InvalidOperationException("SnapshotToken is required.");
        }
        if (!string.Equals(request.ToolContractVersion, ScheduledGovernanceContract.ToolContractVersion, StringComparison.Ordinal) ||
            !string.Equals(request.SchemaHash, ScheduledGovernanceContract.SchemaHash, StringComparison.Ordinal))
        {
            throw new GovernanceBatchException(
                GovernanceBatchErrorCode.SchemaCapabilityMismatch,
                "Scheduled governance contract version or schema hash does not match the published automation surface.");
        }
        if (request.MaxMutations is < 1 or > 100 || request.MaxDurationSeconds is < 1 or > 120)
        {
            throw new InvalidOperationException("Scheduled execution bounds must be MaxMutations 1..100 and MaxDurationSeconds 1..120.");
        }
    }
}

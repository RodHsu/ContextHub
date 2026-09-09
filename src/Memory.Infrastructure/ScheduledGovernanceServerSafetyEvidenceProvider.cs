using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Memory.Infrastructure;

/// <summary>
/// Recovers only safety facts that are explicitly present in immutable
/// server-side receipt events. Missing or conflicting evidence is represented
/// as unknown/false; this provider never treats a scheduled label as proof.
/// </summary>
public sealed class ScheduledGovernanceServerSafetyEvidenceProvider(MemoryDbContext dbContext)
    : IScheduledGovernanceServerSafetyEvidenceProvider
{
    private static readonly HashSet<string> AllowedReceiptEventTypes =
    [
        "ReviewReceived",
        "ReviewCompleted",
        "ReviewStopped",
        "ScheduledDecisionProjected",
        "BatchReceived",
        "BatchCompleted",
        "BatchReplay",
        "BatchStopped",
        "InternalRetentionCompleted"
    ];

    public async Task<ScheduledGovernanceServerSafetyEvidenceSnapshot?> GetAsync(
        ScheduledGovernanceServerSafetyEvidenceQuery query,
        CancellationToken cancellationToken)
    {
        if (query.TenantId == Guid.Empty ||
            query.OwnerUserId == Guid.Empty ||
            query.ReceiptId == Guid.Empty ||
            string.IsNullOrWhiteSpace(query.GovernanceRunId))
        {
            return null;
        }

        var runId = query.GovernanceRunId.Trim();
        var observedReceipt = await dbContext.GovernanceRunReceipts
            .AsNoTracking()
            .SingleOrDefaultAsync(row =>
                    row.Id == query.ReceiptId &&
                    row.TenantId == query.TenantId &&
                    row.OwnerUserId == query.OwnerUserId &&
                    row.GovernanceRunId == runId,
                cancellationToken);
        if (observedReceipt is null)
        {
            return null;
        }

        var events = await dbContext.GovernanceRunReceipts
            .AsNoTracking()
            .Where(row =>
                row.TenantId == query.TenantId &&
                row.OwnerUserId == query.OwnerUserId &&
                row.GovernanceRunId == runId)
            .OrderBy(row => row.EventSequence)
            .Take(1_001)
            .ToArrayAsync(cancellationToken);
        if (events.Length == 0)
        {
            return null;
        }

        var allEventTypesAllowed = events.All(row => AllowedReceiptEventTypes.Contains(row.EventType));
        var observedReceiptIsLatest = events[^1].EventSequence == observedReceipt.EventSequence;
        if (events.Length > 1_000 ||
            !observedReceiptIsLatest ||
            !IsCurrentCompletedScheduledReceipt(observedReceipt))
        {
            return Unknown(observedReceipt.EventSequence);
        }

        var decisionReceipt = events
            .Where(row =>
                row.EventSequence <= observedReceipt.EventSequence &&
                row.ExecutionMode == "Scheduled" &&
                row.EventType == "ScheduledDecisionProjected" &&
                row.Status == "Completed" &&
                IsCurrentContract(row))
            .OrderByDescending(row => row.EventSequence)
            .FirstOrDefault();
        if (decisionReceipt is null)
        {
            return SnapshotWithUnknownSafety(
                observedReceipt.EventSequence,
                reviewRequestIdentityHash: null,
                InitialReviewReceived: false);
        }

        var reviewRequestIdentityHash = IsRecognizedReviewRequestIdentityHash(
            runId,
            decisionReceipt.RequestIdentityHash)
            ? decisionReceipt.RequestIdentityHash
            : null;
        if (reviewRequestIdentityHash is null)
        {
            return SnapshotWithUnknownSafety(
                observedReceipt.EventSequence,
                reviewRequestIdentityHash: null,
                InitialReviewReceived: false,
                CountInvariantSatisfied: EvaluateCountInvariant(decisionReceipt),
                DecisionObeyed: false);
        }

        var reviewReceipt = events
            .Where(row =>
                row.EventSequence < decisionReceipt.EventSequence &&
                row.EventType == "ReviewCompleted")
            .OrderByDescending(row => row.EventSequence)
            .FirstOrDefault();
        if (reviewReceipt is null)
        {
            return SnapshotWithUnknownSafety(
                observedReceipt.EventSequence,
                reviewRequestIdentityHash,
                InitialReviewReceived: false,
                CountInvariantSatisfied: EvaluateCountInvariant(decisionReceipt),
                DecisionObeyed: false);
        }

        var receivedReceipt = events
            .Where(row =>
                row.EventSequence < reviewReceipt.EventSequence &&
                row.EventType == "ReviewReceived")
            .OrderByDescending(row => row.EventSequence)
            .FirstOrDefault();
        var reviewRequestMatches = IsCanonicalReviewRequest(reviewReceipt, reviewRequestIdentityHash);
        var decisionRequestMatches = IsCanonicalReviewRequest(decisionReceipt, reviewRequestIdentityHash);
        var initialReviewReceived = receivedReceipt is not null &&
                                    reviewRequestMatches &&
                                    decisionRequestMatches &&
                                    IsCurrentReviewReceived(receivedReceipt, reviewRequestIdentityHash);
        var reviewLifecycleValid = initialReviewReceived &&
                                   HasValidReviewLifecycle(
                                       events,
                                       receivedReceipt!,
                                       reviewReceipt,
                                       decisionReceipt);
        var capturedRuntimeIdentity = reviewLifecycleValid
            ? TryGetCapturedRuntimeIdentity(receivedReceipt!, reviewReceipt, decisionReceipt)
            : null;
        reviewLifecycleValid = reviewLifecycleValid && capturedRuntimeIdentity is not null;
        var receiptRequestIdentityHash = reviewRequestMatches && decisionRequestMatches
            ? reviewRequestIdentityHash
            : null;
        var countInvariantSatisfied = EvaluateCountInvariant(decisionReceipt);
        var reviewBindsDecision =
            reviewRequestMatches &&
            decisionRequestMatches &&
            ReviewBindsDecision(reviewReceipt, decisionReceipt);
        if (!reviewBindsDecision)
        {
            return SnapshotWithUnknownSafety(
                observedReceipt.EventSequence,
                receiptRequestIdentityHash,
                reviewLifecycleValid,
                countInvariantSatisfied,
                DecisionObeyed: false);
        }

        var decisionObeyed = await EvaluateDecisionObedienceAsync(
            events,
            decisionReceipt,
            observedReceipt.EventSequence,
            reviewLifecycleValid,
            allEventTypesAllowed,
            cancellationToken);

        return new ScheduledGovernanceServerSafetyEvidenceSnapshot(
            observedReceipt.EventSequence,
            receiptRequestIdentityHash,
            capturedRuntimeIdentity,
            reviewLifecycleValid,
            countInvariantSatisfied,
            decisionObeyed,
            NoGeneralConnectorFallback: null,
            NoUnauthorizedMutation: null,
            NoDuplicateMutation: null,
            DisplayNameUnchanged: null,
            BusinessWorkItemsUntouched: null,
            HostDispatchCompleted: null,
            ImmutableSnapshotBound: null,
            FixedReversibleExecutorUsed: null);
    }

    private static ScheduledGovernanceServerSafetyEvidenceSnapshot SnapshotWithUnknownSafety(
        long receiptEventSequence,
        string? reviewRequestIdentityHash,
        bool? InitialReviewReceived,
        bool? CountInvariantSatisfied = null,
        bool? DecisionObeyed = null)
        => new(
            receiptEventSequence,
            reviewRequestIdentityHash,
            CapturedRuntimeIdentity: null,
            InitialReviewReceived,
            CountInvariantSatisfied,
            DecisionObeyed,
            NoGeneralConnectorFallback: null,
            NoUnauthorizedMutation: null,
            NoDuplicateMutation: null,
            DisplayNameUnchanged: null,
            BusinessWorkItemsUntouched: null,
            HostDispatchCompleted: null,
            ImmutableSnapshotBound: null,
            FixedReversibleExecutorUsed: null);

    private static ScheduledGovernanceServerSafetyEvidenceSnapshot Unknown(long receiptEventSequence)
        => SnapshotWithUnknownSafety(
            receiptEventSequence,
            reviewRequestIdentityHash: null,
            InitialReviewReceived: null,
            CountInvariantSatisfied: null,
            DecisionObeyed: null);

    private static bool IsCurrentCompletedScheduledReceipt(GovernanceRunReceipt receipt)
        => receipt.ExecutionMode == "Scheduled" &&
           receipt.Status == "Completed" &&
           IsCurrentContract(receipt);

    private static bool IsCurrentReviewReceived(
        GovernanceRunReceipt receipt,
        string reviewRequestIdentityHash)
        => receipt.ExecutionMode == "Scheduled" &&
           receipt.EventType == "ReviewReceived" &&
           receipt.Status == "Running" &&
           IsCurrentContract(receipt) &&
           IsCanonicalReviewRequest(receipt, reviewRequestIdentityHash);

    private static bool IsCurrentContract(GovernanceRunReceipt receipt)
        => receipt.ToolContractVersion == ScheduledGovernanceContract.ToolContractVersion &&
           receipt.SchemaHash == ScheduledGovernanceContract.SchemaHash &&
           receipt.PublishedCatalogVersion == ScheduledGovernanceContract.PublishedCatalogVersion;

    private static ScheduledGovernanceRuntimeIdentity? TryGetCapturedRuntimeIdentity(
        GovernanceRunReceipt received,
        GovernanceRunReceipt review,
        GovernanceRunReceipt decision)
    {
        var receivedIdentity = ReadCapturedRuntimeIdentity(received);
        var reviewIdentity = ReadCapturedRuntimeIdentity(review);
        var decisionIdentity = ReadCapturedRuntimeIdentity(decision);
        if (receivedIdentity is null || reviewIdentity is null || decisionIdentity is null)
        {
            return null;
        }

        return receivedIdentity == reviewIdentity && reviewIdentity == decisionIdentity
            ? decisionIdentity
            : null;
    }

    private static ScheduledGovernanceRuntimeIdentity? ReadCapturedRuntimeIdentity(
        GovernanceRunReceipt receipt)
    {
        if (!string.Equals(
                receipt.RuntimeEvidenceVersion,
                GovernanceRunReceiptService.ScheduledRuntimeEvidenceVersion,
                StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(receipt.RuntimeServiceName) ||
            string.IsNullOrWhiteSpace(receipt.RuntimeBuildVersion) ||
            !receipt.RuntimeBuildTimestampUtc.HasValue ||
            string.IsNullOrWhiteSpace(receipt.RuntimeDerivedIdentity) ||
            string.IsNullOrWhiteSpace(receipt.RuntimeIdentityHash))
        {
            return null;
        }

        var identity = new ScheduledGovernanceRuntimeIdentity(
            receipt.RuntimeServiceName,
            receipt.RuntimeBuildVersion,
            receipt.RuntimeBuildTimestampUtc.Value.ToUniversalTime(),
            receipt.RuntimeDerivedIdentity);
        return string.Equals(
            receipt.RuntimeIdentityHash,
            ScheduledGovernanceReliabilityEvidenceContract.ComputeRuntimeIdentityHash(identity),
            StringComparison.Ordinal)
            ? identity
            : null;
    }

    private static bool IsCanonicalReviewRequest(
        GovernanceRunReceipt receipt,
        string reviewRequestIdentityHash)
        => string.Equals(
            receipt.RequestIdentityHash,
            reviewRequestIdentityHash,
            StringComparison.Ordinal);

    private static bool IsRecognizedReviewRequestIdentityHash(
        string governanceRunId,
        string? requestIdentityHash)
        => string.Equals(
               requestIdentityHash,
               ScheduledGovernanceReliabilityEvidenceContract.ComputeReviewRequestIdentityHash(
                   governanceRunId,
                   isReReview: false),
               StringComparison.Ordinal) ||
           string.Equals(
               requestIdentityHash,
               ScheduledGovernanceReliabilityEvidenceContract.ComputeReviewRequestIdentityHash(
                   governanceRunId,
                   isReReview: true),
               StringComparison.Ordinal);

    private static bool ReviewBindsDecision(
        GovernanceRunReceipt review,
        GovernanceRunReceipt decision)
        => review.EventSequence < decision.EventSequence &&
           review.ExecutionMode == "Scheduled" &&
           decision.ExecutionMode == "Scheduled" &&
           review.Status == "Completed" &&
           decision.Status == "Completed" &&
           IsCurrentContract(review) &&
           IsCurrentContract(decision) &&
           review.CoverageComplete &&
           decision.CoverageComplete &&
           !string.IsNullOrWhiteSpace(review.FinalSnapshotToken) &&
           string.Equals(review.FinalSnapshotToken, decision.FinalSnapshotToken, StringComparison.Ordinal) &&
           ProjectSetsEqual(review.ProjectIdsJson, decision.ProjectIdsJson) &&
           string.Equals(
               review.AcceptanceEvidenceVersion,
               GovernanceRunReceiptService.ScheduledAcceptanceEvidenceVersion,
               StringComparison.Ordinal) &&
           string.Equals(
               decision.AcceptanceEvidenceVersion,
               GovernanceRunReceiptService.ScheduledAcceptanceEvidenceVersion,
               StringComparison.Ordinal) &&
           review.AuthorizedDurableMemoryCount == decision.AuthorizedDurableMemoryCount &&
           review.CoveredDurableMemoryCount == decision.CoveredDurableMemoryCount &&
           review.ScannedDurableMemoryCount == decision.ScannedDurableMemoryCount &&
           review.TotalDurableMemoryCount == decision.TotalDurableMemoryCount &&
           review.SharedScopeOccurrences == decision.SharedScopeOccurrences &&
           review.UserScopeOccurrences == decision.UserScopeOccurrences &&
           review.UserScopeHandledSeparately == decision.UserScopeHandledSeparately &&
           review.CountInvariantSatisfied == decision.CountInvariantSatisfied;

    private static bool HasValidReviewLifecycle(
        IReadOnlyList<GovernanceRunReceipt> events,
        GovernanceRunReceipt received,
        GovernanceRunReceipt review,
        GovernanceRunReceipt decision)
    {
        if (events.Any(row =>
                row.EventType is "ReviewStopped" or "BatchStopped" ||
                row.Status is "Failed" or "Stopped"))
        {
            return false;
        }

        var cycle = events
            .Where(row => row.EventSequence >= received.EventSequence &&
                          row.EventSequence <= decision.EventSequence)
            .OrderBy(row => row.EventSequence)
            .ToArray();
        return cycle.Length == 3 &&
               cycle[0].EventType == "ReviewReceived" &&
               cycle[1].EventType == "ReviewCompleted" &&
               cycle[2].EventType == "ScheduledDecisionProjected" &&
               cycle[0].Status == "Running" &&
               cycle[1].Status == "Completed" &&
               cycle[2].Status == "Completed" &&
               IsCurrentContract(cycle[0]) &&
               IsCurrentContract(cycle[1]) &&
               IsCurrentContract(cycle[2]);
    }

    private static bool ProjectSetsEqual(string leftJson, string rightJson)
    {
        try
        {
            var left = JsonSerializer.Deserialize<string[]>(leftJson) ?? [];
            var right = JsonSerializer.Deserialize<string[]>(rightJson) ?? [];
            var leftSet = left
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var rightSet = right
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return left.Length > 0 &&
                   leftSet.Count == left.Length &&
                   rightSet.Count == right.Length &&
                   leftSet.SetEquals(rightSet);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool? EvaluateCountInvariant(GovernanceRunReceipt receipt)
    {
        if (!string.Equals(
                receipt.AcceptanceEvidenceVersion,
                GovernanceRunReceiptService.ScheduledAcceptanceEvidenceVersion,
                StringComparison.Ordinal) ||
            receipt.AuthorizedDurableMemoryCount is null ||
            receipt.CoveredDurableMemoryCount is null ||
            receipt.ScannedDurableMemoryCount is null ||
            receipt.TotalDurableMemoryCount is null ||
            receipt.SharedScopeOccurrences is null ||
            receipt.UserScopeOccurrences is null ||
            receipt.UserScopeHandledSeparately is null ||
            receipt.CountInvariantSatisfied is null)
        {
            return null;
        }

        var recomputed = receipt.AuthorizedDurableMemoryCount >= 0 &&
                         receipt.AuthorizedDurableMemoryCount == receipt.CoveredDurableMemoryCount &&
                         receipt.CoveredDurableMemoryCount == receipt.ScannedDurableMemoryCount &&
                         receipt.ScannedDurableMemoryCount == receipt.TotalDurableMemoryCount &&
                         receipt.SharedScopeOccurrences == 1 &&
                         receipt.UserScopeOccurrences == 0 &&
                         receipt.UserScopeHandledSeparately.Value;
        return recomputed && receipt.CountInvariantSatisfied.Value;
    }

    private static Task<bool?> EvaluateDecisionObedienceAsync(
        IReadOnlyList<GovernanceRunReceipt> events,
        GovernanceRunReceipt decisionReceipt,
        long observedEventSequence,
        bool reviewLifecycleValid,
        bool allEventTypesAllowed,
        CancellationToken cancellationToken)
    {
        if (!reviewLifecycleValid || !allEventTypesAllowed)
        {
            return Task.FromResult<bool?>(false);
        }

        if (!Enum.TryParse<ScheduledGovernanceDecision>(
                decisionReceipt.FinalConvergenceStatus,
                ignoreCase: false,
                out var decision))
        {
            return Task.FromResult<bool?>(null);
        }

        if (decision == ScheduledGovernanceDecision.ReversibleExecutionRequired)
        {
            // Proving the reversible path requires immutable executor,
            // write-set and replay evidence not present in this receipt schema.
            return Task.FromResult<bool?>(null);
        }

        // The decision is obedient only while no later receipt event exists.
        // In particular, a later unknown event cannot be treated as harmless
        // just because it is absent from the known batch-event list.
        return Task.FromResult<bool?>(!events.Any(row =>
            row.EventSequence > decisionReceipt.EventSequence &&
            row.EventSequence <= observedEventSequence));
    }
}

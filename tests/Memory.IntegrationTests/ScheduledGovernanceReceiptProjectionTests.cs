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
    public async Task Scheduled_decision_projection_requires_latest_completed_review()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-decision-without-review-{Guid.NewGuid():N}";

        var action = () => receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, "kg:snapshot:i0", isReReview: false, actionable: 1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await action.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.ReReviewRequired);
    }

    [DockerRequiredFact]
    public async Task Scheduled_count_invariant_must_be_persisted_and_recovered_from_immutable_receipts()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-count-evidence-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        const string snapshot = "kg:snapshot:count-evidence";

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, snapshot, durableCount: 3),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var decision = CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 0) with
        {
            CountInvariant = new ScheduledGovernanceCountInvariant(3, 3, 3, 3, 1, 0, true, true)
        };
        await receipts.RecordScheduledDecisionAsync(decision, DateTimeOffset.UtcNow, CancellationToken.None);

        var receipt = await receipts.GetAsync(runId, CancellationToken.None);
        receipt.Should().NotBeNull();
        var evidence = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
            .GetAsync(
                new ScheduledGovernanceServerSafetyEvidenceQuery(
                    actor.TenantId!.Value,
                    actor.UserId!.Value,
                    receipt!.ReceiptId,
                    runId),
                CancellationToken.None);

        evidence.Should().NotBeNull();
        evidence!.ReceiptEventSequence.Should().BeGreaterThan(0);
        evidence.CountInvariantSatisfied.Should().BeTrue();
        evidence.DecisionObeyed.Should().BeTrue();
        evidence.NoGeneralConnectorFallback.Should().BeNull();
        evidence.NoUnauthorizedMutation.Should().BeNull();
        evidence.DisplayNameUnchanged.Should().BeNull();
        evidence.BusinessWorkItemsUntouched.Should().BeNull();
        evidence.HostDispatchCompleted.Should().BeNull();

        var reliability = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityService>()
            .ObserveAsync(receipt, CancellationToken.None);
        var reliabilityRun = reliability.Runs.Single(run => run.GovernanceRunId == runId);
        reliabilityRun.Reasons.Should().NotContain("count-invariant-not-proven");
        reliabilityRun.Reasons.Should().NotContain("decision-obedience-not-proven");
        reliabilityRun.Reasons.Should().Contain("general-connector-fallback-not-proven");
        reliabilityRun.Reasons.Should().Contain("host-dispatch-not-proven");
        reliabilityRun.Qualifies.Should().BeFalse();

        var reliabilityRow = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .ScheduledGovernanceReliabilityRuns.AsNoTracking()
            .SingleAsync(row => row.GovernanceRunId == runId);
        reliabilityRow.ReceiptEventSequence.Should().Be(evidence.ReceiptEventSequence);

        var rows = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
            .GovernanceRunReceipts.AsNoTracking()
            .Where(row => row.GovernanceRunId == runId &&
                          (row.EventType == "ReviewCompleted" ||
                           row.EventType == "ScheduledDecisionProjected"))
            .OrderBy(row => row.EventSequence)
            .ToArrayAsync();
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(row =>
            row.AcceptanceEvidenceVersion == "1" &&
            row.AuthorizedDurableMemoryCount == 3 &&
            row.CoveredDurableMemoryCount == 3 &&
            row.ScannedDurableMemoryCount == 3 &&
            row.TotalDurableMemoryCount == 3 &&
            row.SharedScopeOccurrences == 1 &&
            row.UserScopeOccurrences == 0 &&
            row.UserScopeHandledSeparately == true &&
            row.CountInvariantSatisfied == true);
    }

    [DockerRequiredFact]
    public async Task Scheduled_decision_must_reject_inconsistent_or_review_mismatched_count_evidence()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-count-mismatch-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        const string snapshot = "kg:snapshot:count-mismatch";

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, snapshot, durableCount: 1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var inconsistent = CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 0) with
        {
            CountInvariant = new ScheduledGovernanceCountInvariant(1, 1, 1, 2, 1, 0, true, true)
        };
        var inconsistentAction = () => receipts.RecordScheduledDecisionAsync(
            inconsistent,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await inconsistentAction.Should().ThrowAsync<GovernanceBatchException>()
            .Where(exception => exception.Code == GovernanceBatchErrorCode.SchemaCapabilityMismatch);

        var mismatched = CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 0) with
        {
            CountInvariant = new ScheduledGovernanceCountInvariant(2, 2, 2, 2, 1, 0, true, true)
        };
        var mismatchedAction = () => receipts.RecordScheduledDecisionAsync(
            mismatched,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await mismatchedAction.Should().ThrowAsync<GovernanceBatchException>()
            .Where(exception => exception.Code == GovernanceBatchErrorCode.ReReviewRequired);

        (await scope.ServiceProvider.GetRequiredService<MemoryDbContext>()
                .GovernanceRunReceipts.AsNoTracking()
                .CountAsync(row => row.GovernanceRunId == runId &&
                                   row.EventType == "ScheduledDecisionProjected"))
            .Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Server_safety_evidence_requires_review_received_before_completed_review()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-missing-review-received-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        const string snapshot = "kg:snapshot:missing-review-received";

        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, snapshot),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 0),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
        var evidence = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
            .GetAsync(
                new ScheduledGovernanceServerSafetyEvidenceQuery(
                    actor.TenantId!.Value,
                    actor.UserId!.Value,
                    receipt.ReceiptId,
                    runId),
                CancellationToken.None);

        evidence.Should().NotBeNull();
        evidence!.ReviewRequestIdentityHash.Should().Be(
            ScheduledGovernanceReliabilityEvidenceContract.ComputeReviewRequestIdentityHash(runId));
        evidence.InitialReviewReceived.Should().BeFalse();
        evidence.CountInvariantSatisfied.Should().BeTrue();
        evidence.DecisionObeyed.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Server_safety_evidence_must_reject_a_review_request_identity_mismatch()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"scheduled-request-mismatch-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        const string snapshot = "kg:snapshot:request-mismatch";

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, snapshot),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var review = await db.GovernanceRunReceipts.AsNoTracking()
            .SingleAsync(row => row.GovernanceRunId == runId && row.EventType == "ReviewCompleted");
        var mismatchedReview = CloneReceipt(
            review,
            eventKey: $"request-mismatch-{Guid.NewGuid():N}",
            eventType: "ReviewCompleted");
        mismatchedReview.RequestIdentityHash = "request-identity-mismatch";
        await db.GovernanceRunReceipts.AddAsync(mismatchedReview);
        await db.SaveChangesAsync();

        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 0),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
        var evidence = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
            .GetAsync(
                new ScheduledGovernanceServerSafetyEvidenceQuery(
                    actor.TenantId!.Value,
                    actor.UserId!.Value,
                    receipt.ReceiptId,
                    runId),
                CancellationToken.None);

        evidence.Should().NotBeNull();
        evidence!.ReviewRequestIdentityHash.Should().BeNull();
        evidence.InitialReviewReceived.Should().BeFalse();
        evidence.DecisionObeyed.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Server_safety_evidence_must_not_infer_obedience_after_an_unknown_event()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"scheduled-unknown-event-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        const string snapshot = "kg:snapshot:unknown-event";

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, snapshot),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 0),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var decision = await db.GovernanceRunReceipts.AsNoTracking()
            .SingleAsync(row => row.GovernanceRunId == runId && row.EventType == "ScheduledDecisionProjected");
        var unknown = CloneReceipt(
            decision,
            eventKey: $"unknown-event-{Guid.NewGuid():N}",
            eventType: "UnknownMutation");
        unknown.AcceptanceEvidenceVersion = string.Empty;
        unknown.AuthorizedDurableMemoryCount = null;
        unknown.CoveredDurableMemoryCount = null;
        unknown.ScannedDurableMemoryCount = null;
        unknown.TotalDurableMemoryCount = null;
        unknown.SharedScopeOccurrences = null;
        unknown.UserScopeOccurrences = null;
        unknown.UserScopeHandledSeparately = null;
        unknown.CountInvariantSatisfied = null;
        unknown.StoppedReason = "UnknownMutation";
        await db.GovernanceRunReceipts.AddAsync(unknown);
        await db.SaveChangesAsync();

        var evidence = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
            .GetAsync(
                new ScheduledGovernanceServerSafetyEvidenceQuery(
                    actor.TenantId!.Value,
                    actor.UserId!.Value,
                    unknown.Id,
                    runId),
                CancellationToken.None);

        evidence.Should().NotBeNull();
        evidence!.InitialReviewReceived.Should().BeTrue();
        evidence.CountInvariantSatisfied.Should().BeTrue();
        evidence.DecisionObeyed.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Receipt_replay_with_a_different_cumulative_payload_must_fail_closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"receipt-replay-collision-{Guid.NewGuid():N}";
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [ProjectContext.SharedProjectId],
            "kg:snapshot:replay-collision",
            ExecutionMode: GovernanceBatchExecutionMode.Manual);
        var execution = CreateNoOpExecution(runId, request.SnapshotToken!) with
        {
            ScannedCount = 1,
            AttemptedCount = 1,
            AppliedCount = 1
        };

        await receipts.RecordExecutionStartedAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        await receipts.RecordExecutionAsync(request, execution, DateTimeOffset.UtcNow, CancellationToken.None);

        var replay = () => receipts.RecordExecutionAsync(
            request,
            execution,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await replay.Should().ThrowAsync<GovernanceBatchException>()
            .Where(exception => exception.Code == GovernanceBatchErrorCode.ReplayPayloadMismatch);
    }

    [DockerRequiredFact]
    public async Task Exact_batch_received_replay_after_completion_must_remain_idempotent()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"batch-received-replay-{Guid.NewGuid():N}";
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [ProjectContext.SharedProjectId],
            "kg:snapshot:batch-received-replay",
            ExecutionMode: GovernanceBatchExecutionMode.Manual);

        await receipts.RecordExecutionStartedAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        await receipts.RecordExecutionAsync(
            request,
            CreateNoOpExecution(runId, request.SnapshotToken!),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        var exactReplay = () => receipts.RecordExecutionStartedAsync(
            request,
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await exactReplay.Should().NotThrowAsync();
        (await db.GovernanceRunReceipts.CountAsync(row =>
            row.GovernanceRunId == runId && row.EventType == "BatchReceived")).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Non_reversible_decision_with_a_later_batch_event_must_fail_decision_obedience()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-noop-batch-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        const string snapshot = "kg:snapshot:noop-batch";

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, snapshot),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 0),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var request = new GovernanceBatchExecuteRequest(
            runId,
            [ProjectContext.SharedProjectId],
            snapshot,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled,
            ToolContractVersion: GovernanceToolContract.ToolContractVersion,
            SchemaHash: GovernanceToolContract.SchemaHash)
        {
            ReceiptContractIdentity = identity
        };
        await receipts.RecordExecutionStartedAsync(
            request,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordExecutionAsync(
            request,
            CreateNoOpExecution(runId, snapshot),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var receipt = await receipts.GetAsync(runId, CancellationToken.None);
        var evidence = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
            .GetAsync(
                new ScheduledGovernanceServerSafetyEvidenceQuery(
                    actor.TenantId!.Value,
                    actor.UserId!.Value,
                    receipt!.ReceiptId,
                    runId),
                CancellationToken.None);

        evidence.Should().NotBeNull();
        evidence!.CountInvariantSatisfied.Should().BeTrue();
        evidence.DecisionObeyed.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Review_received_placeholder_must_not_erase_initial_review_evidence()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-initial-review-{Guid.NewGuid():N}";
        var identity = new GovernanceReceiptContractIdentity(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, "kg:snapshot:i0", isReReview: false, actionable: 3),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, "kg:snapshot:i0", isReReview: false, actionable: 3),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var initial = await receipts.GetAsync(runId, CancellationToken.None);
        initial.Should().NotBeNull();
        initial!.InitialSnapshotToken.Should().Be("kg:snapshot:i0");
        initial.FinalSnapshotToken.Should().Be("kg:snapshot:i0");
        initial.InitialGovernanceActionable.Should().Be(3);
        initial.FinalGovernanceActionable.Should().Be(3);

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, "kg:snapshot:i1", isReReview: true, actionable: 1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, "kg:snapshot:i1", isReReview: true, actionable: 1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var reReview = await receipts.GetAsync(runId, CancellationToken.None);
        reReview.Should().NotBeNull();
        reReview!.InitialSnapshotToken.Should().Be("kg:snapshot:i0");
        reReview.FinalSnapshotToken.Should().Be("kg:snapshot:i1");
        reReview.InitialGovernanceActionable.Should().Be(3);
        reReview.FinalGovernanceActionable.Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Zero_initial_actionable_must_remain_a_valid_baseline_after_re_review()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-zero-baseline-{Guid.NewGuid():N}";
        var identity = new GovernanceReceiptContractIdentity(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, "kg:snapshot:i0", isReReview: false, actionable: 0),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, "kg:snapshot:i1", isReReview: true, actionable: 2),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var receipt = await receipts.GetAsync(runId, CancellationToken.None);
        receipt.Should().NotBeNull();
        receipt!.InitialSnapshotToken.Should().Be("kg:snapshot:i0");
        receipt.FinalSnapshotToken.Should().Be("kg:snapshot:i1");
        receipt.InitialGovernanceActionable.Should().Be(0);
        receipt.FinalGovernanceActionable.Should().Be(2);
    }

    [DockerRequiredFact]
    public async Task Empty_first_review_snapshot_must_remain_fail_closed_after_re_review()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-empty-baseline-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, snapshotToken: string.Empty, isReReview: false, actionable: 1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, "kg:snapshot:r1", isReReview: true, actionable: 0),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var receipt = await receipts.GetAsync(runId, CancellationToken.None);
        receipt.Should().NotBeNull();
        receipt!.InitialSnapshotToken.Should().BeEmpty();
        receipt.InitialGovernanceActionable.Should().Be(1);
        receipt.FinalSnapshotToken.Should().Be("kg:snapshot:r1");
    }

    [DockerRequiredFact]
    public async Task Concurrent_first_reviews_must_preserve_the_first_immutable_baseline()
    {
        using var firstScope = environment.GetFactory().Services.CreateScope();
        using var secondScope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(firstScope.ServiceProvider);
        UseBootstrapActor(secondScope.ServiceProvider);
        var firstReceipts = firstScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var secondReceipts = secondScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-concurrent-baseline-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();

        await firstReceipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await Task.WhenAll(
            firstReceipts.RecordReviewAsync(
                CreateGenericReview(runId, identity, "kg:snapshot:i0", isReReview: false, actionable: 1),
                DateTimeOffset.UtcNow,
                CancellationToken.None),
            secondReceipts.RecordReviewAsync(
                CreateGenericReview(runId, identity, "kg:snapshot:i1", isReReview: false, actionable: 2),
                DateTimeOffset.UtcNow,
                CancellationToken.None));

        using var readScope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(readScope.ServiceProvider);
        var db = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var firstCompleted = await db.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.GovernanceRunId == runId && x.EventType == "ReviewCompleted")
            .OrderBy(x => x.EventSequence)
            .FirstAsync();
        var receipt = await readScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>()
            .GetAsync(runId, CancellationToken.None);

        receipt.Should().NotBeNull();
        receipt!.InitialSnapshotToken.Should().Be(firstCompleted.FinalSnapshotToken);
        receipt.InitialGovernanceActionable.Should().Be(firstCompleted.FinalGovernanceActionable);
    }

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

        var identity = new GovernanceReceiptContractIdentity(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);
        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(
                runId,
                identity,
                review.SnapshotToken,
                isReReview: false,
                actionable: 2,
                durableCount: 4),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
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
    public async Task Concurrent_re_review_must_preserve_locked_execution_evidence()
    {
        using var lockScope = environment.GetFactory().Services.CreateScope();
        using var writerScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(lockScope.ServiceProvider);
        writerScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        var receipts = lockScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-cumulative-race-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, "kg:snapshot:i0", isReReview: false, actionable: 1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, "kg:snapshot:i0", isReReview: false, actionable: 1),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

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
            ReceiptContractIdentity = identity
        };
        var auditId = Guid.NewGuid();
        var execution = new GovernanceBatchExecuteResult(
            ScannedCount: 1,
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
            AuditIds: [auditId],
            SnapshotToken: "kg:snapshot:i0",
            StoppedReason: "Completed")
        {
            GovernanceRunId = runId,
            ErrorCode = GovernanceBatchErrorCode.None
        };

        var lockDb = lockScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var writerDb = writerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await writerDb.Database.OpenConnectionAsync(CancellationToken.None);
        var productionLock = await receipts.AcquireRunLockAsync(runId, CancellationToken.None);
        await using var runLock = await PostgresAdvisoryLockBarrier.CreateAsync(
            productionLock,
            lockDb.Database.GetDbConnection(),
            writerDb.Database.GetDbConnection(),
            environment.PostgresConnectionString!);
        var reReview = Task.Run(async () =>
            await writerScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>()
                .RecordReviewAsync(
                    CreateGenericReview(runId, identity, "kg:snapshot:r1", isReReview: true, actionable: 0),
                    DateTimeOffset.UtcNow,
                    CancellationToken.None));

        await runLock.WaitForWriterBlockedAsync();
        await receipts.RecordExecutionStartedAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        await receipts.RecordExecutionAsync(request, execution, DateTimeOffset.UtcNow, CancellationToken.None);
        await runLock.ReleaseHolderAsync();
        await reReview.WaitAsync(TimeSpan.FromSeconds(10));

        var receipt = await receipts.GetAsync(runId, CancellationToken.None);
        receipt.Should().NotBeNull();
        receipt!.FinalSnapshotToken.Should().Be("kg:snapshot:r1");
        receipt.Applied.Should().Be(1);
        receipt.AuditIds.Should().ContainSingle().Which.Should().Be(auditId);
    }

    [DockerRequiredFact]
    public async Task Concurrent_non_replay_executions_must_add_deltas_to_locked_latest_totals()
    {
        using var lockScope = environment.GetFactory().Services.CreateScope();
        using var writerScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(lockScope.ServiceProvider);
        writerScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        var receipts = lockScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-execution-race-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();

        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(
            CreateGenericReview(runId, identity, "kg:snapshot:i0", isReReview: false, actionable: 2),
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, "kg:snapshot:i0", isReReview: false, actionable: 2),
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        var firstRequest = new GovernanceBatchExecuteRequest(
            runId,
            [ProjectContext.SharedProjectId],
            "kg:snapshot:i0",
            Cursor: null,
            MaxMutations: 1,
            MaxDurationSeconds: 30,
            AllowedActionTypes: [GovernanceBatchActionType.Archive],
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled,
            ToolContractVersion: GovernanceToolContract.ToolContractVersion,
            SchemaHash: GovernanceToolContract.SchemaHash)
        {
            ReceiptContractIdentity = identity
        };
        var secondRequest = firstRequest with { Cursor = "cursor:second-page" };
        var firstAuditId = Guid.NewGuid();
        var secondAuditId = Guid.NewGuid();
        var execution = new GovernanceBatchExecuteResult(
            ScannedCount: 1,
            AttemptedCount: 1,
            AppliedCount: 1,
            NoOpCount: 0,
            FailedCount: 1,
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
            AuditIds: [firstAuditId],
            SnapshotToken: "kg:snapshot:i0",
            StoppedReason: "Completed")
        {
            GovernanceRunId = runId,
            ErrorCode = GovernanceBatchErrorCode.None,
            QuarantinedCount = 1,
            SemanticAutoResolvedCount = 1
        };

        var lockDb = lockScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var writerDb = writerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await writerDb.Database.OpenConnectionAsync(CancellationToken.None);
        var productionLock = await receipts.AcquireRunLockAsync(runId, CancellationToken.None);
        await using var runLock = await PostgresAdvisoryLockBarrier.CreateAsync(
            productionLock,
            lockDb.Database.GetDbConnection(),
            writerDb.Database.GetDbConnection(),
            environment.PostgresConnectionString!);
        var writer = Task.Run(async () =>
            await writerScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>()
                .RecordExecutionAsync(
                    secondRequest,
                    execution with { AuditIds = [secondAuditId] },
                    DateTimeOffset.UtcNow,
                    CancellationToken.None));

        await runLock.WaitForWriterBlockedAsync();
        await receipts.RecordExecutionAsync(
            firstRequest,
            execution,
            DateTimeOffset.UtcNow,
            CancellationToken.None);
        await runLock.ReleaseHolderAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));

        var receipt = await receipts.GetAsync(runId, CancellationToken.None);

        receipt.Should().NotBeNull();
        receipt!.Applied.Should().Be(2);
        receipt.Failed.Should().Be(2);
        receipt.Quarantined.Should().Be(2);
        receipt.SemanticAutoResolved.Should().Be(2);
        receipt.AuditIds.Should().BeEquivalentTo([firstAuditId, secondAuditId]);
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

    private static KnowledgeReviewResult CreateGenericReview(
        string runId,
        GovernanceReceiptContractIdentity? receiptIdentity = null,
        string snapshotToken = "kg:snapshot:i0",
        bool isReReview = false,
        int actionable = 0,
        int durableCount = 0)
    {
        var durable = new KnowledgeGovernanceCoverageResult(
            Guid.NewGuid(),
            snapshotToken,
            DateTimeOffset.UtcNow,
            durableCount,
            durableCount,
            0,
            0,
            0,
            0,
            true,
            false,
            null)
        {
            AuthorizedGovernanceDurableMemoryCount = durableCount,
            GovernanceCoveredDurableMemoryCount = durableCount,
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
        var convergence = new KnowledgeReviewConvergenceResult("Review", actionable, isReReview, actionable == 0)
        {
            CoverageComplete = true,
            GovernanceActionableCount = actionable,
            ExecutionActionableCount = actionable
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
            isReReview,
            pagination,
            convergence)
        {
            DurableMemoryCoverage = durable,
            GovernanceCoverage = coverage,
            GovernancePlan = [],
            CandidateCount = 0,
            ExecutionActionableCount = actionable,
            GovernedExceptionCount = 0,
            ReceiptContractIdentity = receiptIdentity
        };
    }

    private static ScheduledGovernanceReviewResult CreateScheduledDecision(
        string runId,
        string snapshotToken,
        bool isReReview,
        int actionable)
        => new(
            runId,
            isReReview,
            actionable > 0
                ? ScheduledGovernanceDecision.ReversibleExecutionRequired
                : ScheduledGovernanceDecision.NoOpConverged,
            snapshotToken,
            new ScheduledGovernanceCountInvariant(0, 0, 0, 0, 1, 0, true, true),
            CoverageComplete: true,
            CandidateCount: actionable,
            ReversibleExecutionCount: actionable,
            HumanDecisionCount: 0,
            GovernedExceptionCount: 0,
            BusinessWorkItemActionableCount: 0,
            ResolvedProjectIds: [ProjectContext.SharedProjectId],
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            CurrentReviewHumanDecisionCandidateCount: 0,
            GovernedRequiresUserDecisionExceptionCount: 0,
            GovernedHostBlockedExceptionCount: 0,
            GovernedDeferredExceptionCount: 0,
            ExceptionDelta: new GovernanceExceptionDeltaResult(0, 0, 0, 0),
            RuntimeIdentity: ScheduledGovernanceContract.RuntimeIdentity);

    private static GovernanceReceiptContractIdentity CurrentScheduledIdentity()
        => new(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);

    private static GovernanceRunReceipt CloneReceipt(
        GovernanceRunReceipt source,
        string eventKey,
        string eventType)
        => new()
        {
            TenantId = source.TenantId,
            OwnerUserId = source.OwnerUserId,
            GovernanceRunId = source.GovernanceRunId,
            EventKey = eventKey,
            Actor = source.Actor,
            ExecutionMode = source.ExecutionMode,
            StartedAt = source.StartedAt,
            CompletedAt = source.CompletedAt,
            ToolContractVersion = source.ToolContractVersion,
            SchemaHash = source.SchemaHash,
            PublishedCatalogVersion = source.PublishedCatalogVersion,
            RuntimeEvidenceVersion = source.RuntimeEvidenceVersion,
            RuntimeServiceName = source.RuntimeServiceName,
            RuntimeBuildVersion = source.RuntimeBuildVersion,
            RuntimeBuildTimestampUtc = source.RuntimeBuildTimestampUtc,
            RuntimeDerivedIdentity = source.RuntimeDerivedIdentity,
            RuntimeIdentityHash = source.RuntimeIdentityHash,
            InitialSnapshotToken = source.InitialSnapshotToken,
            FinalSnapshotToken = source.FinalSnapshotToken,
            CoverageComplete = source.CoverageComplete,
            AcceptanceEvidenceVersion = source.AcceptanceEvidenceVersion,
            AuthorizedDurableMemoryCount = source.AuthorizedDurableMemoryCount,
            CoveredDurableMemoryCount = source.CoveredDurableMemoryCount,
            ScannedDurableMemoryCount = source.ScannedDurableMemoryCount,
            TotalDurableMemoryCount = source.TotalDurableMemoryCount,
            SharedScopeOccurrences = source.SharedScopeOccurrences,
            UserScopeOccurrences = source.UserScopeOccurrences,
            UserScopeHandledSeparately = source.UserScopeHandledSeparately,
            CountInvariantSatisfied = source.CountInvariantSatisfied,
            InitialGovernanceActionable = source.InitialGovernanceActionable,
            FinalGovernanceActionable = source.FinalGovernanceActionable,
            CandidateCount = source.CandidateCount,
            ExecutionActionableCount = source.ExecutionActionableCount,
            GovernedExceptionCount = source.GovernedExceptionCount,
            Applied = source.Applied,
            Failed = source.Failed,
            Deferred = source.Deferred,
            RequiresUserDecision = source.RequiresUserDecision,
            HostBlocked = source.HostBlocked,
            ExceptionNew = source.ExceptionNew,
            ExceptionResolved = source.ExceptionResolved,
            ExceptionUnchanged = source.ExceptionUnchanged,
            ExceptionEscalated = source.ExceptionEscalated,
            GovernedExceptionStatesJson = source.GovernedExceptionStatesJson,
            Quarantined = source.Quarantined,
            DeleteEligible = source.DeleteEligible,
            DeleteMatured = source.DeleteMatured,
            AutoDeleted = source.AutoDeleted,
            DeleteCancelled = source.DeleteCancelled,
            Tombstoned = source.Tombstoned,
            SemanticAutoResolved = source.SemanticAutoResolved,
            BusinessWorkItemActionable = source.BusinessWorkItemActionable,
            FinalConvergenceStatus = source.FinalConvergenceStatus,
            StoppedReason = source.StoppedReason,
            EventType = eventType,
            Status = source.Status,
            LatestBatchReceived = source.LatestBatchReceived,
            RequestIdentityHash = source.RequestIdentityHash,
            RequestHash = source.RequestHash,
            FailurePhase = source.FailurePhase,
            AuditIdsJson = source.AuditIdsJson,
            ProjectIdsJson = source.ProjectIdsJson,
            IsReplay = source.IsReplay,
            CreatedAt = source.CreatedAt
        };

    private static GovernanceBatchExecuteResult CreateNoOpExecution(
        string runId,
        string snapshotToken)
        => new(
            ScannedCount: 0,
            AttemptedCount: 0,
            AppliedCount: 0,
            NoOpCount: 0,
            FailedCount: 0,
            DeferredCount: 0,
            RequiresUserDecisionCount: 0,
            MergedCount: 0,
            UpdatedCount: 0,
            MovedCount: 0,
            ArchivedCount: 0,
            ReindexedCount: 0,
            DeleteProposalCount: 0,
            NextCursor: null,
            HasMore: false,
            RequiresReReview: false,
            Items: [],
            AuditIds: [],
            SnapshotToken: snapshotToken,
            StoppedReason: "Completed")
        {
            GovernanceRunId = runId,
            ErrorCode = GovernanceBatchErrorCode.None
        };
}

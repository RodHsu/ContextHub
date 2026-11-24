using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ScheduledGovernanceExecutorBoundaryIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Scheduled_Executor_Rechecks_Canonical_WorkItem_Eligibility_Before_Mutation()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = ScheduledActor(user);

        var projectId = $"scheduled-boundary-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var workItem = new ProjectWorkItem
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ProjectId = projectId,
            Title = "Completed work item with an incomplete checklist",
            Description = "The checklist mismatch is intentionally not an automation authority.",
            Status = ProjectWorkItemStatus.Completed,
            CompletedAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        workItem.ChecklistItems.Add(new ProjectWorkItemChecklistItem
        {
            WorkItemId = workItem.Id,
            Content = "Requires an explicit human decision",
            IsCompleted = false,
            SortOrder = 0,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.ProjectWorkItems.Add(workItem);
        await db.SaveChangesAsync();

        var runId = $"scheduled-boundary-{Guid.NewGuid():N}";
        var reviewService = scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>();
        var review = await reviewService.ReviewAsync(
            ScheduledReviewRequest(projectId, runId),
            CancellationToken.None);
        await RecordReversibleDecisionAsync(scope.ServiceProvider, review);
        review.GovernancePlan.Should().Contain(x =>
            x.ItemKind == GovernanceItemKind.WorkItem &&
            x.RecommendedAction == GovernanceBatchActionType.WorkItemReconcile.ToString());

        var executor = scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>();
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 100,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.WorkItemReconcile],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: true,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled)
        {
            ReceiptContractIdentity = CurrentScheduledReceiptIdentity()
        };
        var preview = await executor.ExecuteAsync(request, CancellationToken.None);
        preview.ErrorCode.Should().Be(GovernanceBatchErrorCode.None);

        var run = await db.GovernanceBatchRuns.SingleAsync(x => x.GovernanceRunId == runId);
        var plan = JsonNode.Parse(run.PlanJson)?.AsArray()
            ?? throw new InvalidOperationException("Persisted governance plan is invalid.");
        var workItemPlan = plan.Single(node =>
            node is JsonObject json &&
            string.Equals(json["kind"]?.GetValue<string>(), nameof(GovernanceItemKind.WorkItem), StringComparison.Ordinal))!;

        // Simulate a legacy or tampered plan whose broad flags appear executable.
        // The canonical kind remains WorkItem and must be re-evaluated at mutation time.
        workItemPlan["requiresExplicitApproval"] = false;
        workItemPlan["isReversible"] = true;
        workItemPlan["riskLevel"] = (int)GovernanceBatchRiskLevel.Low;
        workItemPlan["semanticConfidence"] = 0.99m;
        run.PlanJson = plan.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var proposalCountBefore = await db.ConversationInsights.CountAsync(x =>
            x.TenantId == user.TenantId &&
            x.OwnerUserId == user.Id &&
            x.SourceSystem == ChatGptProposalService.SourceSystem &&
            x.Tags.Contains("chatgpt-proposal"));
        var result = await executor.ExecuteAsync(request with { DryRun = false }, CancellationToken.None);

        result.ErrorCode.Should().Be(GovernanceBatchErrorCode.None, result.StoppedReason);
        result.AppliedCount.Should().Be(0);
        result.Items.Should().Contain(x =>
            x.ItemKind == nameof(GovernanceItemKind.WorkItem) &&
            x.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision &&
            x.Summary.Contains("integrity validation failed", StringComparison.Ordinal));
        result.Items.SelectMany(x => x.ProposalIds).Should().BeEmpty();
        result.AuditIds.Should().NotBeEmpty("the fail-closed decision must remain auditable");

        var persistedWorkItem = await db.ProjectWorkItems.AsNoTracking().SingleAsync(x => x.Id == workItem.Id);
        persistedWorkItem.Status.Should().Be(ProjectWorkItemStatus.Completed);
        persistedWorkItem.ArchivedAt.Should().BeNull();
        (await db.ConversationInsights.CountAsync(x =>
            x.TenantId == user.TenantId &&
            x.OwnerUserId == user.Id &&
            x.SourceSystem == ChatGptProposalService.SourceSystem &&
            x.Tags.Contains("chatgpt-proposal")))
            .Should().Be(proposalCountBefore);
        (await db.SecurityAuditEvents.CountAsync(x => result.AuditIds.Contains(x.Id)))
            .Should().BeGreaterThan(0);
    }

    [DockerRequiredTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Scheduled_Executor_Allows_Only_Deterministic_Reversible_Retention_Quarantine(
        bool replayDuringReview, bool verifyImmediateReplay)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = ScheduledActor(user);

        var projectId = $"scheduled-valid-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var memory = new MemoryItem
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ProjectId = projectId,
            ExternalKey = $"scheduled-valid:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Episode,
            Title = "Deterministic disposable episode",
            Content = "A bounded test-only retention candidate.",
            Summary = "Deterministic disposable episode.",
            SourceType = "test",
            SourceRef = "integration://scheduled-valid",
            Tags = ["synthetic-disposable"],
            Importance = 0.10m,
            Confidence = 0.20m,
            Status = MemoryStatus.Active,
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();

        var runId = $"scheduled-valid-{Guid.NewGuid():N}";
        var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            ScheduledReviewRequest(projectId, runId),
            CancellationToken.None);
        await RecordReversibleDecisionAsync(scope.ServiceProvider, review);
        review.GovernancePlan.Should().Contain(x =>
            x.ItemKind == GovernanceItemKind.Retention &&
            x.AuthorityResourceId == memory.Id &&
            x.RecommendedAction == GovernanceBatchActionType.Quarantine.ToString());

        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 100,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.Quarantine],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled)
        {
            ReceiptContractIdentity = CurrentScheduledReceiptIdentity()
        };
        var scheduled = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>();
        var initialReceipt = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        var result = await scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>()
            .ExecuteAsync(request, CancellationToken.None);

        result.ErrorCode.Should().Be(GovernanceBatchErrorCode.None, result.StoppedReason);
        result.Items.Should().Contain(x =>
            x.ItemKind == nameof(GovernanceItemKind.Retention) &&
            x.ResourceId == memory.Id &&
            x.ActionType == GovernanceBatchActionType.Quarantine &&
            x.Disposition == GovernanceBatchItemDisposition.Applied);
        result.QuarantinedCount.Should().Be(1);
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status
            .Should().Be(MemoryStatus.Archived);
        (await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == memory.Id))
            .LifecycleStatus.Should().Be("Eligible");

        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var completed = (await receipts.GetAsync(runId, default))!;
        completed.LatestBatch.Should().NotBeNull();
        completed.LatestBatch!.Executed.Should().BeTrue();
        completed.LatestBatch.RequiresReReview.Should().BeTrue();
        var afterBatch = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        afterBatch.Decision.Should().BeNull();
        afterBatch.Outcome.Should().Be("ReReviewRequired");
        if (verifyImmediateReplay)
        {
            // The A1 verifier must be able to bind the first batch, then read the durable replay before re-review.
            initialReceipt.LatestBatchReceived.Should().BeFalse();
            initialReceipt.Applied.Should().Be(0);
            initialReceipt.AuditIds.Should().BeEmpty();
            afterBatch.ReceiptId.Should().NotBe(initialReceipt.ReceiptId);
            afterBatch.Status.Should().Be("Completed");
            afterBatch.Terminal.Should().BeTrue();
            afterBatch.LatestBatchReceived.Should().BeTrue();
            afterBatch.IsReplay.Should().BeFalse();
            afterBatch.InitialSnapshotToken.Should().Be(result.SnapshotToken);
            afterBatch.FinalSnapshotToken.Should().Be(result.SnapshotToken);
            afterBatch.StoppedReason.Should().Be(result.StoppedReason);
            afterBatch.Applied.Should().Be(result.AppliedCount);
            afterBatch.Failed.Should().Be(result.FailedCount);
            afterBatch.Quarantined.Should().Be(result.QuarantinedCount);
            afterBatch.SemanticAutoResolved.Should().Be(result.SemanticAutoResolvedCount);
            afterBatch.AuditIds.Should().NotBeEmpty().And.BeEquivalentTo(result.AuditIds);

            var replay = await scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>()
                .ExecuteAsync(request, default);
            replay.Should().BeEquivalentTo(result with { IsReplay = true });
            var replayReceipt = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
            replayReceipt.ReceiptId.Should().NotBe(afterBatch.ReceiptId);
            replayReceipt.IsReplay.Should().BeTrue();
            replayReceipt.Status.Should().Be("Completed");
            replayReceipt.Outcome.Should().Be(afterBatch.Outcome);
            replayReceipt.RequestIdentityHash.Should().Be(afterBatch.RequestIdentityHash);
            replayReceipt.AuditIds.Should().BeEquivalentTo(afterBatch.AuditIds);
            replayReceipt.Applied.Should().Be(afterBatch.Applied);
            replayReceipt.Quarantined.Should().Be(afterBatch.Quarantined);
            (await db.GovernanceBatchExecutions.AsNoTracking().CountAsync(x =>
                x.Run!.GovernanceRunId == runId)).Should().Be(1);
            return;
        }

        actorAccessor.Current = actorAccessor.Current with { AllowedProjectIds = [projectId] };
        var reviewRequest = new ScheduledGovernanceReviewRequest(runId, IsReReview: true);
        var reReviewStartedAt = DateTimeOffset.UtcNow;
        await receipts.RecordReviewStartedAsync(runId, reReviewStartedAt, CurrentScheduledReceiptIdentity(), default, isReReview: true);
        var running = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        running.Status.Should().Be("Running");
        running.Terminal.Should().BeFalse();
        running.Decision.Should().BeNull();
        if (replayDuringReview)
        {
            await receipts.RecordExecutionAsync(request, result with { IsReplay = true }, DateTimeOffset.UtcNow, default);
            var replayWhileRunning = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
            replayWhileRunning.IsReplay.Should().BeTrue();
            replayWhileRunning.Status.Should().Be("Running");
            replayWhileRunning.Outcome.Should().Be("Running");
            replayWhileRunning.Terminal.Should().BeFalse();
            replayWhileRunning.Decision.Should().BeNull();
        }
        await receipts.RecordReviewStoppedAsync(runId, reReviewStartedAt, "Stopped", "OperationCanceled",
            "KnowledgeReview", CurrentScheduledReceiptIdentity(), default, isReReview: true);
        var stopped = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        stopped.Status.Should().Be("Stopped");
        stopped.Outcome.Should().Be("Stopped");
        stopped.StoppedReason.Should().Be("OperationCanceled");
        stopped.Decision.Should().BeNull();
        var reReview = await scheduled.ReviewAsync(reviewRequest, default);
        reReview.Decision.Should().Be(ScheduledGovernanceDecision.NoOpConverged);

        var finalized = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        finalized.Decision.Should().Be(reReview.Decision);
        finalized.Outcome.Should().Be(reReview.Decision.ToString());
        finalized.Terminal.Should().BeTrue();
        finalized.FinalSnapshotToken.Should().Be(reReview.SnapshotToken);
        finalized.LatestBatchReceived.Should().BeTrue();
        finalized.RequestIdentityHash.Should().Be(afterBatch.RequestIdentityHash);

        var staleRequest = await scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>()
            .ExecuteAsync(request, default);
        staleRequest.ErrorCode.Should().Be(GovernanceBatchErrorCode.ReReviewRequired);

        (await db.GovernanceBatchExecutions.AsNoTracking().CountAsync(x =>
            x.Run!.GovernanceRunId == runId)).Should().Be(1);
        if (replayDuringReview)
            return;

        // Publish an already concluded batch replay after the new review, as can
        // happen when receipt delivery arrives out of order.
        await receipts.RecordExecutionAsync(request, result with { IsReplay = true }, DateTimeOffset.UtcNow, default);
        var afterReplay = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        afterReplay.IsReplay.Should().BeTrue();
        afterReplay.Decision.Should().Be(reReview.Decision);
        afterReplay.Outcome.Should().Be(reReview.Decision.ToString());
        afterReplay.FinalSnapshotToken.Should().Be(reReview.SnapshotToken);
        afterReplay.RequestIdentityHash.Should().Be(afterBatch.RequestIdentityHash);
        afterReplay.AuditIds.Should().Contain(result.AuditIds);
        afterReplay.Applied.Should().Be(afterBatch.Applied);
    }

    [DockerRequiredFact]
    public async Task Scheduled_Executor_Requires_Scheduled_Capability_And_Current_Receipt_Identity()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var projectId = $"scheduled-capability-{Guid.NewGuid():N}";
        var memory = CreateRetentionCandidate(user, projectId);
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();

        var runId = $"scheduled-capability-{Guid.NewGuid():N}";
        actorAccessor.Current = GenericAdminActor(user);
        var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            ScheduledReviewRequest(projectId, runId),
            CancellationToken.None);
        var request = ScheduledRequest(
            runId,
            projectId,
            review.DurableMemoryCoverage!.SnapshotToken,
            [GovernanceBatchActionType.Quarantine]);
        var executor = scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>();

        var missingCapability = await executor.ExecuteAsync(request, CancellationToken.None);

        missingCapability.ErrorCode.Should().Be(GovernanceBatchErrorCode.SchemaCapabilityMismatch);
        missingCapability.AppliedCount.Should().Be(0);
        missingCapability.Items.Should().BeEmpty();
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status
            .Should().Be(MemoryStatus.Active);

        actorAccessor.Current = ScheduledActor(user);
        var missingDecision = await executor.ExecuteAsync(request, CancellationToken.None);
        missingDecision.ErrorCode.Should().Be(GovernanceBatchErrorCode.ReReviewRequired);
        missingDecision.AppliedCount.Should().Be(0);

        var downgradedMode = await executor.ExecuteAsync(
            request with { ExecutionMode = GovernanceBatchExecutionMode.Manual },
            CancellationToken.None);
        downgradedMode.ErrorCode.Should().Be(GovernanceBatchErrorCode.SchemaCapabilityMismatch);
        downgradedMode.AppliedCount.Should().Be(0);
        downgradedMode.Items.Should().BeEmpty();

        var undefinedMode = await executor.ExecuteAsync(
            request with { ExecutionMode = (GovernanceBatchExecutionMode)999 },
            CancellationToken.None);
        undefinedMode.ErrorCode.Should().Be(GovernanceBatchErrorCode.SchemaCapabilityMismatch);
        undefinedMode.AppliedCount.Should().Be(0);
        undefinedMode.Items.Should().BeEmpty();
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status
            .Should().Be(MemoryStatus.Active);

        var staleIdentity = request with
        {
            GovernanceRunId = $"scheduled-stale-identity-{Guid.NewGuid():N}",
            ReceiptContractIdentity = new GovernanceReceiptContractIdentity("stale", "stale", "stale")
        };
        var staleContract = await executor.ExecuteAsync(staleIdentity, CancellationToken.None);

        staleContract.ErrorCode.Should().Be(GovernanceBatchErrorCode.SchemaCapabilityMismatch);
        staleContract.AppliedCount.Should().Be(0);
        staleContract.Items.Should().BeEmpty();
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status
            .Should().Be(MemoryStatus.Active);
    }

    [DockerRequiredFact]
    public async Task Scheduled_Executor_Rejects_Persisted_Canonical_Dispatch_Kind_Mismatch()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = ScheduledActor(user);
        var projectId = $"scheduled-kind-tamper-{Guid.NewGuid():N}";
        var memory = CreateRetentionCandidate(user, projectId);
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();

        var runId = $"scheduled-kind-tamper-{Guid.NewGuid():N}";
        var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            ScheduledReviewRequest(projectId, runId),
            CancellationToken.None);
        await RecordReversibleDecisionAsync(scope.ServiceProvider, review);
        var request = ScheduledRequest(
            runId,
            projectId,
            review.DurableMemoryCoverage!.SnapshotToken,
            [GovernanceBatchActionType.Quarantine],
            dryRun: true);
        var executor = scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>();
        (await executor.ExecuteAsync(request, CancellationToken.None)).ErrorCode
            .Should().Be(GovernanceBatchErrorCode.None);

        var run = await db.GovernanceBatchRuns.SingleAsync(x => x.GovernanceRunId == runId);
        var plan = JsonNode.Parse(run.PlanJson)?.AsArray()
            ?? throw new InvalidOperationException("Persisted governance plan is invalid.");
        var retentionPlan = plan.Single(node =>
            node is JsonObject json &&
            string.Equals(json["kind"]?.GetValue<string>(), nameof(GovernanceItemKind.Retention), StringComparison.Ordinal));
        retentionPlan!["kind"] = nameof(GovernanceItemKind.WorkItem);
        run.PlanJson = plan.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await executor.ExecuteAsync(request with { DryRun = false }, CancellationToken.None);

        result.ErrorCode.Should().Be(GovernanceBatchErrorCode.None, result.StoppedReason);
        result.AppliedCount.Should().Be(0);
        result.Items.Should().Contain(x =>
            x.ItemKind == nameof(GovernanceItemKind.WorkItem) &&
            x.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision &&
            x.Summary.Contains("canonical-dispatch-kind-mismatch", StringComparison.Ordinal));
        result.Items.SelectMany(x => x.ProposalIds).Should().BeEmpty();
        result.AuditIds.Should().NotBeEmpty();
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status
            .Should().Be(MemoryStatus.Active);
    }

    [DockerRequiredFact]
    public async Task Scheduled_Executor_Rejects_Persisted_Empty_Reason_Codes()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = ScheduledActor(user);
        var projectId = $"scheduled-reason-tamper-{Guid.NewGuid():N}";
        var memory = CreateRetentionCandidate(user, projectId);
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();

        var runId = $"scheduled-reason-tamper-{Guid.NewGuid():N}";
        var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            ScheduledReviewRequest(projectId, runId),
            CancellationToken.None);
        await RecordReversibleDecisionAsync(scope.ServiceProvider, review);
        var request = ScheduledRequest(
            runId,
            projectId,
            review.DurableMemoryCoverage!.SnapshotToken,
            [GovernanceBatchActionType.Quarantine],
            dryRun: true);
        var executor = scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>();
        (await executor.ExecuteAsync(request, CancellationToken.None)).ErrorCode
            .Should().Be(GovernanceBatchErrorCode.None);

        var run = await db.GovernanceBatchRuns.SingleAsync(x => x.GovernanceRunId == runId);
        var plan = JsonNode.Parse(run.PlanJson)?.AsArray()
            ?? throw new InvalidOperationException("Persisted governance plan is invalid.");
        var retentionPlan = plan.Single(node =>
            node is JsonObject json &&
            string.Equals(json["kind"]?.GetValue<string>(), nameof(GovernanceItemKind.Retention), StringComparison.Ordinal));
        retentionPlan!["reasonCodes"] = new JsonArray();
        run.PlanJson = plan.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var result = await executor.ExecuteAsync(request with { DryRun = false }, CancellationToken.None);

        result.ErrorCode.Should().Be(GovernanceBatchErrorCode.None, result.StoppedReason);
        result.AppliedCount.Should().Be(0);
        result.Items.Should().Contain(x =>
            x.ItemKind == nameof(GovernanceItemKind.Retention) &&
            x.Disposition == GovernanceBatchItemDisposition.RequiresUserDecision &&
            x.Summary.Contains("reason-codes", StringComparison.Ordinal));
        result.Items.SelectMany(x => x.ProposalIds).Should().BeEmpty();
        result.AuditIds.Should().NotBeEmpty();
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status
            .Should().Be(MemoryStatus.Active);
    }

    [DockerRequiredFact]
    public async Task Rejected_PreDispatch_Request_Does_Not_Create_A_Receipt()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            TenantUserRole.Member,
            [SecurityScopes.MemoryRead],
            [],
            IsAuthenticated: true);
        var runId = $"scheduled-pre-dispatch-{Guid.NewGuid():N}";
        var request = new GovernanceBatchExecuteRequest(
            runId,
            ["ContextHub"],
            $"kg:{Guid.NewGuid():N}:0123456789abcdef:i0",
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled);

        var action = () => scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>()
            .ExecuteAsync(request, CancellationToken.None);

        await action.Should().ThrowAsync<UnauthorizedAccessException>();
        (await db.GovernanceRunReceipts.AsNoTracking().CountAsync(x => x.GovernanceRunId == runId))
            .Should().Be(0);
    }

    [DockerRequiredTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Knowledge_review_checks_explicit_and_required_shared_read_grants_before_writing(bool denyShared)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await CreateReviewGrantOwnerAsync(db);
        var allowed = $"read-grant-allowed-{Guid.NewGuid():N}";
        var denied = denyShared ? ProjectContext.SharedProjectId : $"read-grant-denied-{Guid.NewGuid():N}";
        var runId = $"read-grant-review-{Guid.NewGuid():N}";
        db.MemoryItems.Add(CreateRetentionCandidate(user, allowed));
        db.TenantProjectGrants.Add(new TenantProjectGrant
        {
            TenantId = user.TenantId,
            ProjectId = denied,
            CanRead = false,
            CanWrite = false
        });
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = GenericAdminActor(user);
        var review = scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>();
        var ids = denyShared ? new[] { allowed } : [allowed, denied];
        var invoke = () => review.ReviewAsync(new KnowledgeReviewRequest(ids, GovernanceRunId: runId), default);

        await invoke.Should().ThrowAsync<UnauthorizedAccessException>();
        (await db.KnowledgeGovernanceSnapshots.AsNoTracking().AnyAsync(x => x.GovernanceRunId == runId)).Should().BeFalse();
        (await db.MaintenanceRuns.AsNoTracking().AnyAsync(x =>
            x.TriggeredBy == $"knowledge-review:{runId}" || x.TriggeredBy == $"knowledge-review-shared:{runId}")).Should().BeFalse();
        (await db.GovernanceRunReceipts.AsNoTracking().AnyAsync(x => x.GovernanceRunId == runId)).Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Scheduled_review_filters_denied_regular_projects_from_discovery()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await CreateReviewGrantOwnerAsync(db);
        var allowed = $"scheduled-read-allowed-{Guid.NewGuid():N}";
        var denied = $"scheduled-read-denied-{Guid.NewGuid():N}";
        db.MemoryItems.AddRange(CreateRetentionCandidate(user, allowed), CreateRetentionCandidate(user, denied));
        db.TenantProjectGrants.Add(new TenantProjectGrant
        {
            TenantId = user.TenantId,
            ProjectId = denied.ToUpperInvariant(),
            CanRead = false,
            CanWrite = false
        });
        await db.SaveChangesAsync();

        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current =
            ScheduledActor(user) with { AllowedProjectIds = [allowed, denied] };
        var service = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>();
        var runId = $"scheduled-read-grant-{Guid.NewGuid():N}";
        var result = await service.ReviewAsync(new ScheduledGovernanceReviewRequest(runId), default);
        result.ResolvedProjectIds.Should().Contain(allowed).And.NotContain(denied);
        result.ResolvedProjectIds.Count(ProjectContext.IsShared).Should().Be(1);
        var receipt = await service.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        receipt.ProjectIds.Should().Contain(allowed).And.NotContain(denied);
    }

    [DockerRequiredTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Governance_snapshot_rechecks_revoked_read_grants_before_live_findings(bool revokeShared)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await CreateReviewGrantOwnerAsync(db);
        var project = $"snapshot-grant-{Guid.NewGuid():N}";
        var actor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actor.Current = GenericAdminActor(user) with { AllowedProjectIds = [project] };
        db.MemoryItems.Add(CreateRetentionCandidate(user, project));
        await db.SaveChangesAsync();

        var runId = $"snapshot-grant-{Guid.NewGuid():N}";
        var durable = scope.ServiceProvider.GetRequiredService<IDurableMemoryGovernanceService>();
        var snapshot = await durable.GetOrCreateSnapshotAsync([project], runId, false, default);
        snapshot.FindingIds.Should().NotBeEmpty("a successful read would query the live findings overlay");

        db.TenantProjectGrants.Add(new TenantProjectGrant
        {
            TenantId = user.TenantId,
            ProjectId = revokeShared ? ProjectContext.SharedProjectId : project.ToUpperInvariant(),
            CanRead = false,
            CanWrite = false
        });
        await db.SaveChangesAsync();

        var counter = new LiveFindingReadCounter();
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
            scope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>()).AddInterceptors(counter).Options;
        await using var observerDb = new MemoryDbContext(options);
        var observer = new DurableMemoryGovernanceService(observerDb, scope.ServiceProvider.GetRequiredService<IGovernanceService>(),
            actor, scope.ServiceProvider.GetRequiredService<IClock>());

        var read = () => observer.GetSnapshotAsync(runId, snapshot.Coverage.SnapshotToken, false, false, default);
        await read.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*durable project grant*");
        var reuse = () => observer.GetOrCreateSnapshotAsync([project], runId, false, default);
        await reuse.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*durable project grant*");
        counter.Reads.Should().Be(0);
    }

    private sealed class LiveFindingReadCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("governance_findings", StringComparison.OrdinalIgnoreCase))
                Reads++;
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }
    }

    [DockerRequiredTheory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Governance_executor_rejects_shared_write_revocation_before_execution_or_replay(bool withSnapshot, bool replay)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await CreateReviewGrantOwnerAsync(db);
        var project = $"shared-execution-grant-{Guid.NewGuid():N}";
        var memory = CreateRetentionCandidate(user, project);
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current =
            GenericAdminActor(user) with { AllowedProjectIds = [project] };

        var runId = $"shared-execution-grant-{Guid.NewGuid():N}";
        string? snapshotToken = null;
        if (withSnapshot)
        {
            var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>()
                .ReviewAsync(new KnowledgeReviewRequest([project], GovernanceRunId: runId), default);
            snapshotToken = review.DurableMemoryCoverage!.SnapshotToken;
        }

        var request = new GovernanceBatchExecuteRequest(runId, [project], snapshotToken,
            AllowedActionTypes: [GovernanceBatchActionType.Quarantine], ExecutionMode: GovernanceBatchExecutionMode.Interactive);
        var executor = scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>();
        if (replay)
        {
            var first = await executor.ExecuteAsync(request, default);
            first.Succeeded.Should().BeTrue(first.StoppedReason);
            (await executor.ExecuteAsync(request, default)).IsReplay.Should().BeTrue();
        }

        db.TenantProjectGrants.Add(new TenantProjectGrant
        {
            TenantId = user.TenantId,
            ProjectId = ProjectContext.SharedProjectId.ToUpperInvariant(),
            CanRead = true,
            CanWrite = false
        });
        await db.SaveChangesAsync();

        if (withSnapshot)
        {
            var readable = await scope.ServiceProvider.GetRequiredService<IDurableMemoryGovernanceService>()
                .GetSnapshotAsync(runId, snapshotToken!, false, false, default);
            readable.Coverage.SnapshotToken.Should().Be(snapshotToken);
        }

        var before = await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id);
        var receiptCount = await db.GovernanceRunReceipts.CountAsync(x => x.GovernanceRunId == runId);
        var invoke = () => executor.ExecuteAsync(request, default);
        await invoke.Should().ThrowAsync<UnauthorizedAccessException>().WithMessage("*durable project grant*");
        (await db.GovernanceBatchExecutions.CountAsync(x => x.Run!.GovernanceRunId == runId)).Should().Be(replay ? 1 : 0);
        (await db.GovernanceRunReceipts.CountAsync(x => x.GovernanceRunId == runId)).Should().Be(receiptCount);
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status.Should().Be(before.Status);
    }

    [DockerRequiredFact]
    public async Task Interactive_Executor_Rejects_Durable_ReadOnly_Project_Grant_Before_Mutation()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var projectId = $"governance-read-only-{Guid.NewGuid():N}";
        var memory = CreateRetentionCandidate(user, projectId);
        db.MemoryItems.Add(memory);
        db.TenantProjectGrants.Add(new TenantProjectGrant
        {
            TenantId = user.TenantId,
            ProjectId = projectId,
            CanRead = true,
            CanWrite = false,
            CanManageTokens = false,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        actorAccessor.Current = GenericAdminActor(user) with { AllowedProjectIds = [projectId] };
        var resolvedScope = await scope.ServiceProvider.GetRequiredService<IGovernanceProjectScopeResolver>()
            .ResolveAsync([projectId], CancellationToken.None);
        resolvedScope.Should().ContainSingle()
            .Which.CanWrite.Should().BeFalse();
        var runId = $"governance-read-only-run-{Guid.NewGuid():N}";
        var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>()
            .ReviewAsync(
                new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: runId),
                CancellationToken.None);
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            AllowedActionTypes: [GovernanceBatchActionType.Quarantine],
            ExecutionMode: GovernanceBatchExecutionMode.Interactive);

        var action = () => scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>()
            .ExecuteAsync(request, CancellationToken.None);

        await action.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*durable project grant*");
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status
            .Should().Be(MemoryStatus.Active);
        (await db.MemoryRetentionStates.AsNoTracking().CountAsync(x => x.ResourceId == memory.Id))
            .Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Scheduled_Lineage_Rejects_CrossMode_Pollution_And_Remains_Executable()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = ScheduledActor(user);
        var projectId = $"scheduled-lineage-race-{Guid.NewGuid():N}";
        var memory = CreateRetentionCandidate(user, projectId);
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();

        var runId = $"scheduled-lineage-race-{Guid.NewGuid():N}";
        var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            ScheduledReviewRequest(projectId, runId),
            CancellationToken.None);
        await RecordReversibleDecisionAsync(scope.ServiceProvider, review);
        var scheduledRequest = ScheduledRequest(
            runId,
            projectId,
            review.DurableMemoryCoverage!.SnapshotToken,
            [GovernanceBatchActionType.Quarantine]);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var eventCountBefore = await db.GovernanceRunReceipts.AsNoTracking()
            .CountAsync(x => x.GovernanceRunId == runId);
        var pollution = () => receipts.RecordExecutionStartedAsync(
            scheduledRequest with
            {
                ExecutionMode = GovernanceBatchExecutionMode.Interactive,
                ReceiptContractIdentity = null
            },
            DateTimeOffset.UtcNow,
            CancellationToken.None);

        await pollution.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.SchemaCapabilityMismatch);
        (await db.GovernanceRunReceipts.AsNoTracking().CountAsync(x => x.GovernanceRunId == runId))
            .Should().Be(eventCountBefore, "a cross-mode event cannot pollute a scheduled lineage");

        var result = await scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>()
            .ExecuteAsync(scheduledRequest, CancellationToken.None);

        result.ErrorCode.Should().Be(GovernanceBatchErrorCode.None);
        result.AppliedCount.Should().Be(1);
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status
            .Should().Be(MemoryStatus.Archived);
    }

    [DockerRequiredFact]
    public async Task Run_Lock_Serializes_Concurrent_Receipt_Writes()
    {
        using var lockScope = environment.GetFactory().Services.CreateScope();
        using var writerScope = environment.GetFactory().Services.CreateScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await lockDb.TenantUsers.AsNoTracking()
            .SingleAsync(x => x.Username == "contract-test-admin");
        lockScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = ScheduledActor(user);
        var runId = $"scheduled-lineage-lock-{Guid.NewGuid():N}";
        var writerDb = writerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await writerDb.Database.OpenConnectionAsync(CancellationToken.None);
        var productionLock = await lockScope.ServiceProvider
            .GetRequiredService<IGovernanceRunReceiptService>()
            .AcquireRunLockAsync(runId, CancellationToken.None);
        await using var runLock = await PostgresAdvisoryLockBarrier.CreateAsync(
            productionLock,
            lockDb.Database.GetDbConnection(),
            writerDb.Database.GetDbConnection(),
            environment.PostgresConnectionString!);

        var writer = Task.Run(async () =>
        {
            writerScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = ScheduledActor(user);
            await writerScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>()
                .RecordReviewStartedAsync(
                    runId,
                    DateTimeOffset.UtcNow,
                    CurrentScheduledReceiptIdentity(),
                    CancellationToken.None);
        });

        await runLock.WaitForWriterBlockedAsync();

        await runLock.ReleaseHolderAsync();
        await writer.WaitAsync(TimeSpan.FromSeconds(10));
        (await lockDb.GovernanceRunReceipts.AsNoTracking().CountAsync(x => x.GovernanceRunId == runId))
            .Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Interactive_Executor_Must_Not_Broaden_Exact_Insight_Whitespace_Matching()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = GenericAdminActor(user);
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"insight-exact-whitespace-{suffix}";
        var runId = $"insight-exact-whitespace-run-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var session = new ConversationSession
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ConversationId = $"insight-exact-whitespace-{suffix}",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = "integration-test",
            LastTurnId = "turn-1",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            Session = session,
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = session.SourceSystem,
            SourceRef = "integration://exact-whitespace",
            DedupKey = $"exact-whitespace:{suffix}",
            CreatedAt = now
        };
        var insight = new ConversationInsight
        {
            Session = session,
            Checkpoint = checkpoint,
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ConversationId = session.ConversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = session.SourceSystem,
            SourceRef = checkpoint.SourceRef,
            SourceKind = ConversationSourceKind.AgentSupplemental,
            InsightType = ConversationInsightType.Episode,
            Title = "Exact  whitespace evidence",
            Content = "The candidate must not match collapsed whitespace.",
            Summary = "Exact  whitespace summary",
            Importance = .5m,
            Confidence = .99m,
            DedupKey = $"exact-whitespace-insight:{suffix}",
            PromotionStatus = ConversationPromotionStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now
        };
        var durable = new MemoryItem
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ProjectId = projectId,
            ExternalKey = $"exact-whitespace-memory:{suffix}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Episode,
            Title = "Exact whitespace evidence",
            Content = insight.Content,
            Summary = "Exact whitespace summary",
            SourceType = "verified-test-evidence",
            SourceRef = "integration://exact-whitespace",
            Tags = ["synthetic-disposable"],
            Importance = .6m,
            Confidence = .99m,
            Status = MemoryStatus.Active,
            AuthorityState = MemoryAuthorityState.Current,
            ValidFrom = now,
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(insight, durable);
        await db.SaveChangesAsync(CancellationToken.None);
        var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: runId),
            CancellationToken.None);
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            AllowedActionTypes: [GovernanceBatchActionType.ConversationInsightDisposition],
            ExecutionMode: GovernanceBatchExecutionMode.Interactive);

        var result = await scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>()
            .ExecuteAsync(request, CancellationToken.None);

        result.ErrorCode.Should().Be(GovernanceBatchErrorCode.None);
        result.SemanticAutoResolvedCount.Should().Be(0);
        (await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id))
            .PromotionStatus.Should().Be(ConversationPromotionStatus.Deferred);
    }

    private static async Task<TenantUser> CreateReviewGrantOwnerAsync(MemoryDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var id = Guid.NewGuid();
        var tenant = new Tenant
        {
            Id = id,
            Slug = $"review-acl-{id:N}"[..28],
            DisplayName = "Isolated governance review grants",
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        var user = new TenantUser
        {
            TenantId = tenant.Id,
            Username = "review-grant-owner",
            DisplayName = "Review grant owner",
            Role = TenantUserRole.Admin,
            Status = TenantUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(tenant, user);
        await db.SaveChangesAsync();
        return user;
    }

    private static ContextHubRequestActor ScheduledActor(TenantUser user)
        => new(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.ScheduledGovernance
            ],
            [],
            IsAuthenticated: true);

    private static ContextHubRequestActor GenericAdminActor(TenantUser user)
        => new(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite,
                SecurityScopes.GovernanceTrackerManage
            ],
            [],
            IsAuthenticated: true);

    private static MemoryItem CreateRetentionCandidate(TenantUser user, string projectId)
    {
        var now = DateTimeOffset.UtcNow;
        return new MemoryItem
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ProjectId = projectId,
            ExternalKey = $"scheduled-valid:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Episode,
            Title = "Deterministic disposable episode",
            Content = "A bounded test-only retention candidate.",
            Summary = "Deterministic disposable episode.",
            SourceType = "test",
            SourceRef = "integration://scheduled-valid",
            Tags = ["synthetic-disposable"],
            Importance = 0.10m,
            Confidence = 0.20m,
            Status = MemoryStatus.Active,
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        };
    }

    private static GovernanceBatchExecuteRequest ScheduledRequest(
        string runId,
        string projectId,
        string snapshotToken,
        IReadOnlyList<GovernanceBatchActionType> actions,
        bool dryRun = false)
        => new(
            runId,
            [projectId],
            snapshotToken,
            MaxMutations: 100,
            MaxDurationSeconds: 60,
            AllowedActionTypes: actions,
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: dryRun,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled)
        {
            ReceiptContractIdentity = CurrentScheduledReceiptIdentity()
        };

    private static KnowledgeReviewRequest ScheduledReviewRequest(string projectId, string runId)
        => new([projectId], LimitPerSection: 200, GovernanceRunId: runId)
        {
            ReceiptContractIdentity = CurrentScheduledReceiptIdentity()
        };

    private static async Task RecordReversibleDecisionAsync(
        IServiceProvider services,
        KnowledgeReviewResult review)
    {
        var durable = review.DurableMemoryCoverage
            ?? throw new InvalidOperationException("Scheduled test review requires durable coverage.");
        var sharedOccurrences = durable.GovernanceProjectIds.Count(ProjectContext.IsShared);
        var userOccurrences = durable.GovernanceProjectIds.Count(ProjectContext.IsUser);
        var countInvariant = new ScheduledGovernanceCountInvariant(
            durable.AuthorizedGovernanceDurableMemoryCount,
            durable.GovernanceCoveredDurableMemoryCount,
            durable.ScannedCount,
            durable.TotalCount,
            sharedOccurrences,
            userOccurrences,
            userOccurrences == 0,
            durable.CountInvariantSatisfied && sharedOccurrences == 1 && userOccurrences == 0);
        var result = new ScheduledGovernanceReviewResult(
            review.GovernanceRunId,
            review.IsReReview,
            ScheduledGovernanceDecision.ReversibleExecutionRequired,
            durable.SnapshotToken,
            countInvariant,
            CoverageComplete: true,
            CandidateCount: Math.Max(1, review.CandidateCount),
            ReversibleExecutionCount: 1,
            HumanDecisionCount: 0,
            GovernedExceptionCount: review.GovernedExceptionCount,
            BusinessWorkItemActionableCount: review.Convergence.BusinessWorkItemActionableCount,
            ResolvedProjectIds: durable.GovernanceProjectIds,
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            CurrentReviewHumanDecisionCandidateCount: 0,
            GovernedRequiresUserDecisionExceptionCount: review.Convergence.RequiresUserDecisionCount,
            GovernedHostBlockedExceptionCount: review.Convergence.HostBlockedCount,
            GovernedDeferredExceptionCount: review.Convergence.DeferredCount,
            ExceptionDelta: new GovernanceExceptionDeltaResult(0, 0, 0, 0),
            RuntimeIdentity: ScheduledGovernanceContract.RuntimeIdentity);
        await services.GetRequiredService<IGovernanceRunReceiptService>()
            .RecordScheduledDecisionAsync(result, DateTimeOffset.UtcNow, CancellationToken.None);
    }

    private static GovernanceReceiptContractIdentity CurrentScheduledReceiptIdentity()
        => new(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);
}

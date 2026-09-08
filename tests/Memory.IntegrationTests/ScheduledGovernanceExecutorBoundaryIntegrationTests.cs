using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
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

    [DockerRequiredFact]
    public async Task Scheduled_Executor_Allows_Only_Deterministic_Reversible_Retention_Quarantine()
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
        var runLock = await lockScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>()
            .AcquireRunLockAsync(runId, CancellationToken.None);
        var writerEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var writer = Task.Run(async () =>
        {
            writerScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = ScheduledActor(user);
            writerEntered.SetResult();
            await writerScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>()
                .RecordReviewStartedAsync(
                    runId,
                    DateTimeOffset.UtcNow,
                    CurrentScheduledReceiptIdentity(),
                    CancellationToken.None);
        });

        await writerEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        writer.IsCompleted.Should().BeFalse("the second connection must wait on the same per-run advisory lock");

        await runLock.DisposeAsync();
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

    private static GovernanceReceiptContractIdentity CurrentScheduledReceiptIdentity()
        => new(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);
}

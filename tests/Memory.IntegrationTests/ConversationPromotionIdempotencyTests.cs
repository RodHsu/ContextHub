using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ConversationPromotionIdempotencyTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [DockerRequiredFact]
    public async Task Retry_Should_Find_Active_Promotion_Beyond_The_Previous_Bounded_Window_And_Exact_Replay_Should_Not_Enqueue()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"promotion-idempotency-{suffix}";
        var conversationId = $"promotion-conversation-{suffix}";
        var insight = await SeedInsightAsync(db, actor, projectId, conversationId, ConversationPromotionStatus.Deferred);
        var now = DateTimeOffset.UtcNow;
        var targetJob = CreatePromotionJob(
            actor,
            projectId,
            conversationId,
            now.AddHours(-1));
        var decoyJobs = Enumerable.Range(0, 201)
            .Select(index => CreatePromotionJob(
                actor,
                projectId,
                $"{conversationId}-decoy-{index}",
                now.AddSeconds(index + 1)))
            .ToArray();
        db.MemoryJobs.AddRange(decoyJobs.Append(targetJob));
        await db.SaveChangesAsync(CancellationToken.None);

        var jobCountBeforeRetry = await db.MemoryJobs.CountAsync(CancellationToken.None);
        var request = new ConversationInsightGovernanceRequest(
            insight.Id,
            $"promotion-replay-{suffix}",
            "Retry after host capability became available.");

        var first = await automation.RetryInsightAsync(request, CancellationToken.None);
        var jobCountAfterFirstRetry = await db.MemoryJobs.CountAsync(CancellationToken.None);
        var replay = await automation.RetryInsightAsync(request, CancellationToken.None);
        var jobCountAfterReplay = await db.MemoryJobs.CountAsync(CancellationToken.None);

        first.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        replay.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        jobCountAfterFirstRetry.Should().Be(jobCountBeforeRetry);
        jobCountAfterReplay.Should().Be(jobCountAfterFirstRetry);

        var activeTargetJobs = await FindActivePromotionJobsAsync(db, actor, projectId, conversationId);
        activeTargetJobs.Should().ContainSingle();
        activeTargetJobs[0].Id.Should().Be(targetJob.Id);
    }

    [DockerRequiredFact]
    public async Task Exact_Retry_Replay_Should_Repair_A_Missing_Durable_Promotion_Job()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"promotion-repair-{suffix}";
        var conversationId = $"promotion-conversation-{suffix}";
        var insight = await SeedInsightAsync(
            db,
            actor,
            projectId,
            conversationId,
            ConversationPromotionStatus.Deferred);
        var request = new ConversationInsightGovernanceRequest(
            insight.Id,
            $"promotion-repair-run-{suffix}",
            "Retry with durable job repair.");

        var first = await automation.RetryInsightAsync(request, CancellationToken.None);
        var jobs = await FindActivePromotionJobsAsync(db, actor, projectId, conversationId);
        jobs.Should().ContainSingle();
        var jobIds = jobs.Select(x => x.Id).ToArray();
        await db.MemoryJobs.Where(x => jobIds.Contains(x.Id)).ExecuteDeleteAsync(CancellationToken.None);
        db.ChangeTracker.Clear();

        var replay = await automation.RetryInsightAsync(request, CancellationToken.None);

        replay.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        (await FindActivePromotionJobsAsync(db, actor, projectId, conversationId))
            .Should().ContainSingle("exact replay must repair a missing durable promotion job");
    }

    [DockerRequiredFact]
    public async Task Promotion_Worker_And_Insight_Retry_Must_Not_Apply_ChatGpt_Proposals()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"promotion-proposal-boundary-{suffix}";
        var conversationId = $"promotion-conversation-{suffix}";
        var ordinary = await SeedInsightAsync(
            db,
            actor,
            projectId,
            conversationId,
            ConversationPromotionStatus.Deferred);
        var proposal = await SeedInsightAsync(
            db,
            actor,
            projectId,
            conversationId,
            ConversationPromotionStatus.Pending);
        proposal.SourceSystem = ChatGptProposalService.SourceSystem;
        proposal.Tags = ["chatgpt-proposal"];
        await db.SaveChangesAsync(CancellationToken.None);

        var proposalRetry = () => automation.RetryInsightAsync(
            new ConversationInsightGovernanceRequest(
                proposal.Id,
                $"proposal-retry-{suffix}",
                "This must not bypass proposal approval."),
            CancellationToken.None);
        await proposalRetry.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*proposal lifecycle tools*");

        await automation.RetryInsightAsync(
            new ConversationInsightGovernanceRequest(
                ordinary.Id,
                $"ordinary-retry-{suffix}",
                "Retry the ordinary insight only."),
            CancellationToken.None);
        await automation.PromotePendingInsightsAsync(
            conversationId,
            projectId,
            CancellationToken.None);

        db.ChangeTracker.Clear();
        (await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == ordinary.Id))
            .PromotionStatus.Should().Be(ConversationPromotionStatus.Promoted);
        var proposalReadBack = await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == proposal.Id);
        proposalReadBack.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        proposalReadBack.PromotedMemoryId.Should().BeNull();
    }

    [DockerRequiredFact]
    public async Task Exact_Current_Memory_Evidence_Should_Reopen_A_Deferred_Insight()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"insight-evidence-reopen-{suffix}";
        var conversationId = $"insight-evidence-conversation-{suffix}";
        var insight = await SeedInsightAsync(
            db,
            actor,
            projectId,
            conversationId,
            ConversationPromotionStatus.Deferred);
        await automation.SetInsightDispositionAsync(
            new ConversationInsightDispositionRequest(
                insight.Id,
                ConversationInsightDisposition.Deferred,
                "Wait for exact current durable evidence.",
                $"insight-evidence-baseline-{suffix}"),
            CancellationToken.None);
        var now = DateTimeOffset.UtcNow;
        db.MemoryItems.Add(new MemoryItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            ExternalKey = $"insight-evidence:{suffix}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Episode,
            Title = insight.Title,
            Content = insight.Content,
            Summary = insight.Summary,
            SourceType = "verified-test-evidence",
            SourceRef = $"test://insight-evidence/{suffix}",
            Tags = ["synthetic-disposable"],
            Importance = .6m,
            Confidence = .99m,
            Status = MemoryStatus.Active,
            AuthorityState = MemoryAuthorityState.Current,
            ValidFrom = now,
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(CancellationToken.None);

        await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>().ReviewAsync(
            new KnowledgeReviewRequest(
                [projectId],
                LimitPerSection: 200,
                GovernanceRunId: $"insight-evidence-review-{suffix}"),
            CancellationToken.None);

        db.ChangeTracker.Clear();
        var reopened = await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id);
        reopened.PromotionStatus.Should().Be(ConversationPromotionStatus.Pending);
        reopened.GovernanceRetryCount.Should().Be(1);
        reopened.GovernanceLastEvidenceChangedAt.Should().NotBeNull();
    }

    [DockerRequiredFact]
    public async Task Concurrent_HostBlocked_Transition_Must_Not_Be_Overwritten_By_Skip()
    {
        using var blockerScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(blockerScope.ServiceProvider);
        var blockerDb = blockerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"insight-skip-race-{suffix}";
        var insight = await SeedInsightAsync(
            blockerDb,
            actor,
            projectId,
            $"insight-skip-race-conversation-{suffix}",
            ConversationPromotionStatus.Pending);
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync();
        insight.PromotionStatus = ConversationPromotionStatus.HostBlocked;
        insight.GovernanceReason = "Host capability remains unavailable.";
        insight.GovernanceRunId = $"host-blocked-{suffix}";
        insight.GovernanceBlockingLayer = "Host";
        insight.GovernanceReasonClass = "HostCapabilityUnavailable";
        insight.GovernanceBlockedAt = DateTimeOffset.UtcNow;
        insight.UpdatedAt = DateTimeOffset.UtcNow;
        await blockerDb.SaveChangesAsync(CancellationToken.None);
        var skipEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var skipTask = Task.Run(async () =>
        {
            using var skipScope = environment.GetFactory().Services.CreateScope();
            UseBootstrapActor(skipScope.ServiceProvider);
            skipEntered.SetResult();
            await skipScope.ServiceProvider.GetRequiredService<IConversationAutomationService>()
                .SkipInsightAsync(
                    new ConversationInsightGovernanceRequest(
                        insight.Id,
                        $"skip-race-{suffix}",
                        "A concurrent skip must lose to HostBlocked."),
                    CancellationToken.None);
        });

        await skipEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        skipTask.IsCompleted.Should().BeFalse("the skip update must wait for the concurrent row writer");
        await blockerTransaction.CommitAsync(CancellationToken.None);
        var skipException = await Record.ExceptionAsync(() => skipTask.WaitAsync(TimeSpan.FromSeconds(10)));

        skipException.Should().NotBeNull("the stale serializable skip must fail closed");
        using var verifyScope = environment.GetFactory().Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var readBack = await verifyDb.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id);
        readBack.PromotionStatus.Should().Be(ConversationPromotionStatus.HostBlocked);
        readBack.GovernanceReasonClass.Should().Be("HostCapabilityUnavailable");
    }

    [DockerRequiredFact]
    public async Task Concurrent_HostBlocked_Transition_Must_Roll_Back_Promotion_Output()
    {
        using var blockerScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(blockerScope.ServiceProvider);
        var blockerDb = blockerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"insight-promotion-race-{suffix}";
        var conversationId = $"insight-promotion-race-conversation-{suffix}";
        var insight = await SeedInsightAsync(
            blockerDb,
            actor,
            projectId,
            conversationId,
            ConversationPromotionStatus.Pending);
        var externalKey = $"conversation-insight:{insight.DedupKey}";
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync();
        insight.PromotionStatus = ConversationPromotionStatus.HostBlocked;
        insight.GovernanceReason = "Host capability remains unavailable.";
        insight.GovernanceRunId = $"host-blocked-{suffix}";
        insight.GovernanceReasonClass = "HostCapabilityUnavailable";
        insight.GovernanceBlockedAt = DateTimeOffset.UtcNow;
        insight.UpdatedAt = DateTimeOffset.UtcNow;
        await blockerDb.SaveChangesAsync(CancellationToken.None);

        var promotionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var promotionTask = Task.Run(async () =>
        {
            using var promotionScope = environment.GetFactory().Services.CreateScope();
            UseBootstrapActor(promotionScope.ServiceProvider);
            promotionEntered.SetResult();
            await promotionScope.ServiceProvider.GetRequiredService<IConversationAutomationService>()
                .PromotePendingInsightsAsync(conversationId, projectId, CancellationToken.None);
        });

        await promotionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        promotionTask.IsCompleted.Should().BeFalse("promotion must not commit across a concurrent protected-state writer");
        await blockerTransaction.CommitAsync(CancellationToken.None);
        var promotionException = await Record.ExceptionAsync(() => promotionTask.WaitAsync(TimeSpan.FromSeconds(10)));

        promotionException.Should().NotBeNull("the stale serializable promotion must fail closed");
        using var verifyScope = environment.GetFactory().Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var readBack = await verifyDb.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id);
        readBack.PromotionStatus.Should().Be(ConversationPromotionStatus.HostBlocked);
        readBack.GovernanceReasonClass.Should().Be("HostCapabilityUnavailable");
        (await verifyDb.MemoryItems.AsNoTracking().AnyAsync(x => x.ExternalKey == externalKey)).Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Expected_State_Disposition_Must_Not_Overwrite_Concurrent_HostBlocked()
    {
        using var blockerScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(blockerScope.ServiceProvider);
        var blockerDb = blockerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"insight-disposition-race-{suffix}";
        var insight = await SeedInsightAsync(
            blockerDb,
            actor,
            projectId,
            $"insight-disposition-race-conversation-{suffix}",
            ConversationPromotionStatus.Pending);
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync();
        insight.PromotionStatus = ConversationPromotionStatus.HostBlocked;
        insight.GovernanceReason = "Host capability remains unavailable.";
        insight.GovernanceRunId = $"host-blocked-{suffix}";
        insight.GovernanceReasonClass = "HostCapabilityUnavailable";
        insight.GovernanceBlockedAt = DateTimeOffset.UtcNow;
        insight.UpdatedAt = DateTimeOffset.UtcNow;
        await blockerDb.SaveChangesAsync(CancellationToken.None);

        var dispositionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var dispositionTask = Task.Run(async () =>
        {
            using var dispositionScope = environment.GetFactory().Services.CreateScope();
            UseBootstrapActor(dispositionScope.ServiceProvider);
            dispositionEntered.SetResult();
            await dispositionScope.ServiceProvider.GetRequiredService<IConversationAutomationService>()
                .SetInsightDispositionAsync(
                    new ConversationInsightDispositionRequest(
                        insight.Id,
                        ConversationInsightDisposition.Deferred,
                        "The stale batch must lose to the concurrent host block.",
                        $"disposition-race-{suffix}",
                        ExpectedPromotionStatus: ConversationPromotionStatus.Pending),
                    CancellationToken.None);
        });

        await dispositionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(250);
        dispositionTask.IsCompleted.Should().BeFalse("the stale disposition must wait for the protected-state writer");
        await blockerTransaction.CommitAsync(CancellationToken.None);
        var dispositionException = await Record.ExceptionAsync(() => dispositionTask.WaitAsync(TimeSpan.FromSeconds(10)));

        dispositionException.Should().NotBeNull("the stale serializable disposition must fail closed");
        using var verifyScope = environment.GetFactory().Services.CreateScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var readBack = await verifyDb.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id);
        readBack.PromotionStatus.Should().Be(ConversationPromotionStatus.HostBlocked);
        readBack.GovernanceReasonClass.Should().Be("HostCapabilityUnavailable");
    }

    [DockerRequiredFact]
    public async Task Skip_Must_Reject_Governed_Exception_States()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var protectedStatuses = new[]
        {
            ConversationPromotionStatus.Deferred,
            ConversationPromotionStatus.RequiresUserDecision,
            ConversationPromotionStatus.HostBlocked
        };

        foreach (var protectedStatus in protectedStatuses)
        {
            var suffix = Guid.NewGuid().ToString("N");
            var insight = await SeedInsightAsync(
                db,
                actor,
                $"insight-skip-protected-{suffix}",
                $"insight-skip-protected-conversation-{suffix}",
                protectedStatus);

            var act = async () => await automation.SkipInsightAsync(
                new ConversationInsightGovernanceRequest(
                    insight.Id,
                    $"skip-protected-{suffix}",
                    "Protected governance exceptions must use the explicit disposition or retry workflow."),
                CancellationToken.None);

            await act.Should().ThrowAsync<InvalidOperationException>()
                .WithMessage($"*cannot be skipped from governance state '{protectedStatus}'*");
            db.ChangeTracker.Clear();
            var readBack = await db.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id);
            readBack.PromotionStatus.Should().Be(protectedStatus);
            readBack.GovernanceReason.Should().Be("Seeded exception for promotion idempotency test.");
        }
    }

    [DockerRequiredFact]
    public async Task Concurrent_Retries_Should_Reserve_Only_One_Active_Promotion_Job()
    {
        Guid insightId;
        string projectId;
        string conversationId;
        string suffix;

        using (var seedScope = environment.GetFactory().Services.CreateScope())
        {
            var actor = UseBootstrapActor(seedScope.ServiceProvider);
            var db = seedScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            suffix = Guid.NewGuid().ToString("N");
            projectId = $"promotion-concurrency-{suffix}";
            conversationId = $"promotion-conversation-{suffix}";
            var insight = await SeedInsightAsync(
                db,
                actor,
                projectId,
                conversationId,
                ConversationPromotionStatus.Deferred);
            insightId = insight.Id;
        }

        var results = await Task.WhenAll(
            RetryInNewScopeAsync(insightId, projectId, conversationId, $"concurrent-a-{suffix}"),
            RetryInNewScopeAsync(insightId, projectId, conversationId, $"concurrent-b-{suffix}"));

        results.Should().HaveCount(2);
        results.Should().OnlyContain(x => x.PromotionStatus == ConversationPromotionStatus.Pending);

        using var verifyScope = environment.GetFactory().Services.CreateScope();
        var actorForVerification = UseBootstrapActor(verifyScope.ServiceProvider);
        var dbForVerification = verifyScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var activeTargetJobs = await FindActivePromotionJobsAsync(
            dbForVerification,
            actorForVerification,
            projectId,
            conversationId);

        activeTargetJobs.Should().ContainSingle();
    }

    [DockerRequiredFact]
    public async Task Same_Owner_Ingest_Replay_Should_Return_The_Same_Session_And_Checkpoint()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var suffix = Guid.NewGuid().ToString("N");
        var request = new ConversationIngestRequest(
            ConversationId: $"conversation-ingest-replay-{suffix}",
            TurnId: "turn-1",
            EventType: ConversationEventType.TurnCompleted,
            SourceKind: ConversationSourceKind.HostEvent,
            SourceSystem: $"conversation-replay-{suffix}",
            SourceRef: $"tests/conversation-replay-{suffix}",
            ProjectId: $"conversation-replay-project-{suffix}",
            AgentMessageSummary: "採用這個實作決定。",
            ShortExcerpt: "same-owner replay");

        var first = await automation.IngestAsync(request, CancellationToken.None);
        var replay = await automation.IngestAsync(request, CancellationToken.None);

        replay.SessionId.Should().Be(first.SessionId);
        replay.CheckpointId.Should().Be(first.CheckpointId);
        replay.JobId.Should().Be(first.JobId);
        (await db.ConversationSessions.CountAsync(x =>
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.UserId &&
            x.ConversationId == request.ConversationId &&
            x.SourceSystem == request.SourceSystem)).Should().Be(1);
        (await db.ConversationCheckpoints.CountAsync(x =>
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.UserId &&
            x.DedupKey == first.CheckpointId.ToString())).Should().Be(0);
        (await db.ConversationCheckpoints.CountAsync(x =>
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.UserId &&
            x.SessionId == first.SessionId)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Ingest_Should_Reject_A_Tool_Call_Project_Outside_The_Actor_Write_Scope()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var bootstrapActor = UseBootstrapActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var suffix = Guid.NewGuid().ToString("N");
        var allowedProject = $"conversation-grant-allowed-{suffix}";
        var deniedProject = $"conversation-grant-denied-{suffix}";
        var conversationId = $"conversation-grant-{suffix}";
        actorAccessor.Current = bootstrapActor with { AllowedProjectIds = [allowedProject] };

        var request = new ConversationIngestRequest(
            ConversationId: conversationId,
            TurnId: "turn-1",
            EventType: ConversationEventType.TurnCompleted,
            SourceKind: ConversationSourceKind.AgentSupplemental,
            SourceSystem: $"conversation-grant-{suffix}",
            SourceRef: $"tests/{conversationId}",
            ProjectId: allowedProject,
            ToolCalls: [new ConversationToolCallRequest(
                ToolName: "denied-tool",
                OutputSummary: "must not be persisted",
                ProjectId: deniedProject)]);

        var act = async () => await automation.IngestAsync(request, CancellationToken.None);
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        (await db.ConversationSessions.AnyAsync(x => x.ConversationId == conversationId)).Should().BeFalse();
        (await db.ConversationCheckpoints.AnyAsync(x => x.ConversationId == conversationId)).Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Scoped_Reads_And_Retry_Should_Not_Reach_Another_Project()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var bootstrapActor = UseBootstrapActor(scope.ServiceProvider);
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var suffix = Guid.NewGuid().ToString("N");
        var allowedProject = $"conversation-read-allowed-{suffix}";
        var hiddenProject = $"conversation-read-hidden-{suffix}";
        var hiddenInsight = await SeedInsightAsync(
            db,
            bootstrapActor,
            hiddenProject,
            $"conversation-read-{suffix}",
            ConversationPromotionStatus.Deferred);
        actorAccessor.Current = bootstrapActor with { AllowedProjectIds = [allowedProject] };

        var listed = await automation.ListInsightsAsync(new ConversationInsightListRequest(), CancellationToken.None);
        var fetched = await automation.GetInsightAsync(hiddenInsight.Id, CancellationToken.None);

        listed.Should().NotContain(x => x.Id == hiddenInsight.Id);
        fetched.Should().BeNull();
        var retry = async () => await automation.RetryInsightAsync(
            new ConversationInsightGovernanceRequest(
                hiddenInsight.Id,
                $"cross-project-{suffix}",
                "must remain outside the allowed project scope."),
            CancellationToken.None);
        await retry.Should().ThrowAsync<InvalidOperationException>();
    }

    [DockerRequiredFact]
    public async Task Promotion_Retry_Should_Not_Bulk_Promote_Another_Tenant()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actorAccessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var bootstrapActor = UseBootstrapActor(scope.ServiceProvider);
        var foreignActor = await CreateTenantActorAsync(db);
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = $"conversation-tenant-project-{suffix}";
        var conversationId = $"conversation-tenant-{suffix}";
        var localInsight = await SeedInsightAsync(
            db,
            bootstrapActor,
            projectId,
            conversationId,
            ConversationPromotionStatus.Pending);
        var foreignInsight = await SeedInsightAsync(
            db,
            foreignActor,
            projectId,
            conversationId,
            ConversationPromotionStatus.Pending);
        actorAccessor.Current = bootstrapActor;

        await automation.RetryPromotionAsync(
            new ConversationPromotionRetryRequest(conversationId, projectId),
            CancellationToken.None);

        (await db.ConversationInsights
            .Where(x => x.Id == localInsight.Id)
            .Select(x => x.PromotionStatus)
            .SingleAsync()).Should().Be(ConversationPromotionStatus.Promoted);
        (await db.ConversationInsights
            .Where(x => x.Id == foreignInsight.Id)
            .Select(x => x.PromotionStatus)
            .SingleAsync()).Should().Be(ConversationPromotionStatus.Pending);
    }

    [DockerRequiredFact]
    public async Task Pipeline_Status_Should_Not_Select_Promotion_Job_From_Another_Project()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        var suffix = Guid.NewGuid().ToString("N");
        var conversationId = $"conversation-project-collision-{suffix}";
        var localProjectId = $"conversation-project-a-{suffix}";
        var foreignProjectId = $"conversation-project-b-{suffix}";
        var now = DateTimeOffset.UtcNow;
        var localInsight = await SeedInsightAsync(
            db,
            actor,
            localProjectId,
            conversationId,
            ConversationPromotionStatus.Deferred);
        await SeedInsightAsync(
            db,
            actor,
            foreignProjectId,
            conversationId,
            ConversationPromotionStatus.Deferred);
        var localJob = CreatePromotionJob(actor, localProjectId, conversationId, now.AddMinutes(-2));
        var foreignJob = CreatePromotionJob(actor, foreignProjectId, conversationId, now.AddMinutes(-1));
        db.MemoryJobs.AddRange(localJob, foreignJob);
        await db.SaveChangesAsync(CancellationToken.None);

        var status = await automation.GetPipelineStatusAsync(localInsight.CheckpointId, CancellationToken.None);

        status.Should().NotBeNull();
        status!.PromotionJob.Should().NotBeNull();
        status.PromotionJob!.Id.Should().Be(localJob.Id);
    }

    private async Task<ConversationInsightResult> RetryInNewScopeAsync(
        Guid insightId,
        string projectId,
        string conversationId,
        string governanceRunId)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var automation = scope.ServiceProvider.GetRequiredService<IConversationAutomationService>();
        return await automation.RetryInsightAsync(
            new ConversationInsightGovernanceRequest(
                insightId,
                governanceRunId,
                $"Concurrent retry for {conversationId} in {projectId}."),
            CancellationToken.None);
    }

    private static async Task<ConversationInsight> SeedInsightAsync(
        MemoryDbContext db,
        ContextHubRequestActor actor,
        string projectId,
        string conversationId,
        ConversationPromotionStatus status)
    {
        var now = DateTimeOffset.UtcNow;
        var sourceSystem = $"promotion-test-{Guid.NewGuid():N}";
        var session = new ConversationSession
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ConversationId = conversationId,
            ProjectId = projectId,
            ProjectName = "ContextHub promotion idempotency test",
            SourceSystem = sourceSystem,
            Status = "Active",
            LastTurnId = "turn-1",
            StartedAt = now,
            LastCheckpointAt = now,
            UpdatedAt = now
        };
        var checkpoint = new ConversationCheckpoint
        {
            SessionId = session.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ConversationId = conversationId,
            TurnId = "turn-1",
            ProjectId = projectId,
            ProjectName = session.ProjectName,
            SourceSystem = sourceSystem,
            EventType = ConversationEventType.TurnCompleted,
            SourceKind = ConversationSourceKind.HostEvent,
            SourceRef = $"tests/{conversationId}",
            UserMessageSummary = "promotion idempotency test",
            AgentMessageSummary = "promotion idempotency test",
            SessionSummary = "promotion idempotency test",
            ShortExcerpt = "promotion idempotency test",
            DedupKey = $"promotion-test-checkpoint:{Guid.NewGuid():N}",
            MetadataJson = "{}",
            CreatedAt = now
        };
        var insight = new ConversationInsight
        {
            SessionId = session.Id,
            CheckpointId = checkpoint.Id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ConversationId = conversationId,
            TurnId = checkpoint.TurnId,
            ProjectId = projectId,
            ProjectName = projectId,
            SourceSystem = sourceSystem,
            SourceKind = ConversationSourceKind.HostEvent,
            InsightType = ConversationInsightType.Fact,
            Title = "Promotion idempotency test insight",
            Content = "This insight exists only to exercise promotion job idempotency.",
            Summary = "Promotion idempotency test.",
            SourceRef = checkpoint.SourceRef,
            Tags = ["promotion-idempotency-test"],
            Importance = 0.5m,
            Confidence = 0.5m,
            DedupKey = $"promotion-test-insight:{Guid.NewGuid():N}",
            PromotionStatus = status,
            GovernanceReason = "Seeded exception for promotion idempotency test.",
            GovernanceRunId = "seed-run",
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        };

        db.AddRange(session, checkpoint, insight);
        await db.SaveChangesAsync(CancellationToken.None);
        return insight;
    }

    private static MemoryJob CreatePromotionJob(
        ContextHubRequestActor actor,
        string projectId,
        string conversationId,
        DateTimeOffset createdAt)
        => new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            JobType = MemoryJobType.PromoteConversationInsights,
            Status = MemoryJobStatus.Pending,
            PayloadJson = JsonSerializer.Serialize(new { conversationId, projectId }, JsonOptions),
            CreatedAt = createdAt
        };

    private static async Task<List<MemoryJob>> FindActivePromotionJobsAsync(
        MemoryDbContext db,
        ContextHubRequestActor actor,
        string projectId,
        string conversationId)
    {
        var jobs = await db.MemoryJobs
            .AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.ProjectId == projectId &&
                        x.JobType == MemoryJobType.PromoteConversationInsights &&
                        (x.Status == MemoryJobStatus.Pending || x.Status == MemoryJobStatus.Running))
            .ToListAsync(CancellationToken.None);

        return jobs
            .Where(job =>
            {
                var payload = JsonSerializer.Deserialize<PromotionPayload>(job.PayloadJson, JsonOptions);
                return payload is not null &&
                       payload.ConversationId == conversationId &&
                       payload.ProjectId == projectId;
            })
            .ToList();
    }

    private static async Task<ContextHubRequestActor> CreateTenantActorAsync(MemoryDbContext db)
    {
        var now = DateTimeOffset.UtcNow;
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var suffix = Guid.NewGuid().ToString("N");
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = $"conversation-tenant-{suffix}",
            DisplayName = "Conversation automation isolation test tenant",
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.TenantUsers.Add(new TenantUser
        {
            Id = userId,
            TenantId = tenantId,
            Username = $"conversation-user-{suffix}",
            DisplayName = "Conversation automation isolation test owner",
            Role = TenantUserRole.Member,
            Status = TenantUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync(CancellationToken.None);
        return new ContextHubRequestActor(
            tenantId,
            userId,
            $"conversation-user-{suffix}",
            TenantUserRole.Member,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.PreferencesRead, SecurityScopes.PreferencesWrite],
            [],
            IsAuthenticated: true);
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
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite,
                SecurityScopes.SecurityManage
            ],
            [],
            IsAuthenticated: true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }

    private sealed record PromotionPayload(string ConversationId, string ProjectId);
}

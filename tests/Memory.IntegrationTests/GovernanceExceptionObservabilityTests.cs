using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class GovernanceExceptionObservabilityTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task HostBlocked_Should_Require_Explicit_Manual_Reopen_After_Evidence_Changes()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var governance = scope.ServiceProvider.GetRequiredService<IGovernanceService>();
        var projectId = $"host-blocked-{Guid.NewGuid():N}";
        var memory = CreateLowValueMemory(actor, projectId);
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();

        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var finding = await db.GovernanceFindings.SingleAsync(x =>
            x.ProjectId == projectId && x.PrimaryMemoryId == memory.Id &&
            x.Type == GovernanceFindingType.LowValueMemoryCandidate);
        var blocked = await governance.SetDispositionAsync(new GovernanceFindingDispositionRequest(
            finding.Id,
            GovernanceFindingDisposition.HostBlocked,
            "ChatGPT App OAuth is not available on this host.",
            $"host-blocked-{Guid.NewGuid():N}",
            BlockingLayer: "ChatGptAppOAuth",
            ReasonClass: "UserActionRequired",
            RelatedTool: "scheduled_governance_review"), CancellationToken.None);

        blocked.GovernanceBlockedAt.Should().NotBeNull();
        blocked.GovernanceBlockingLayer.Should().Be("ChatGptAppOAuth");
        blocked.GovernanceReasonClass.Should().Be("UserActionRequired");
        blocked.GovernanceRelatedTool.Should().Be("scheduled_governance_review");
        blocked.GovernanceEvidenceChangedSinceBlock.Should().BeFalse();

        var (otherTenant, otherUser) = CreateOtherOwner();
        db.AddRange(otherTenant, otherUser);
        await db.SaveChangesAsync();
        db.ProjectWorkItems.Add(new ProjectWorkItem
        {
            TenantId = otherTenant.Id,
            OwnerUserId = otherUser.Id,
            ProjectId = projectId,
            Title = "Unrelated owner evidence",
            Description = $"References {memory.Id} but belongs to another tenant.",
            Status = ProjectWorkItemStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var unchanged = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        unchanged.Status.Should().Be(GovernanceFindingStatus.HostBlocked);
        unchanged.GovernanceRetryCount.Should().Be(0);
        unchanged.GovernanceLastReevaluatedAt.Should().NotBeNull();
        unchanged.GovernanceEvidenceChangedSinceBlock.Should().BeFalse();

        memory.MetadataJson = "{\"authority\":\"project-owner-confirmed\"}";
        memory.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);

        var changed = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        changed.Status.Should().Be(GovernanceFindingStatus.HostBlocked);
        changed.GovernanceRetryCount.Should().Be(0);
        changed.GovernanceEvidenceChangedSinceBlock.Should().BeTrue();

        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var changedReadBack = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        changedReadBack.Status.Should().Be(GovernanceFindingStatus.HostBlocked);
        changedReadBack.GovernanceRetryCount.Should().Be(0);
        changedReadBack.GovernanceEvidenceChangedSinceBlock.Should().BeTrue(
            "evidenceChangedSinceBlock is a latched signal until an audited manual reopen");

        var reopened = await governance.ReopenAsync(new GovernanceFindingReopenRequest(
            finding.Id,
            "OAuth was explicitly completed and controlled acceptance can resume.",
            $"manual-reopen-{Guid.NewGuid():N}"), CancellationToken.None);
        reopened.Status.Should().Be(GovernanceFindingStatus.Open);
        reopened.GovernanceRetryCount.Should().Be(1);

        var audit = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == SecurityAuditEventType.GovernanceFindingGovernanceUpdated)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
        audit!.Outcome.Should().Be("Open");
        audit.DetailsJson.Should().Contain("ManualReopen");
    }

    [DockerRequiredFact]
    public async Task Analyze_Should_Not_Read_Or_Update_Other_Tenant_Sources_And_Findings()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var governance = scope.ServiceProvider.GetRequiredService<IGovernanceService>();
        var projectId = $"governance-boundary-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var (otherTenant, otherUser) = CreateOtherOwner();
        var victimSource = new SourceConnection
        {
            TenantId = otherTenant.Id,
            OwnerUserId = otherUser.Id,
            ProjectId = projectId,
            Name = "Victim stale source",
            SourceKind = SourceKind.LocalDocs,
            Enabled = true,
            ConfigJson = "{}",
            CreatedAt = now.AddDays(-2),
            UpdatedAt = now.AddDays(-2)
        };
        var victimFinding = new GovernanceFinding
        {
            TenantId = otherTenant.Id,
            OwnerUserId = otherUser.Id,
            ProjectId = projectId,
            SourceConnectionId = victimSource.Id,
            Type = GovernanceFindingType.StaleSource,
            Status = GovernanceFindingStatus.Accepted,
            Title = "Victim finding must remain unchanged",
            Summary = "Tenant isolation sentinel",
            DedupKey = $"stale-source:{projectId}:{victimSource.Id}",
            CreatedAt = now.AddDays(-1),
            UpdatedAt = now.AddDays(-1)
        };
        db.AddRange(otherTenant, otherUser);
        await db.SaveChangesAsync();
        db.SourceConnections.Add(victimSource);
        await db.SaveChangesAsync();
        db.GovernanceFindings.Add(victimFinding);
        await db.SaveChangesAsync();

        await governance.AnalyzeAsync(projectId, CancellationToken.None);

        db.ChangeTracker.Clear();
        var readBack = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == victimFinding.Id);
        readBack.Status.Should().Be(GovernanceFindingStatus.Accepted);
        readBack.Title.Should().Be("Victim finding must remain unchanged");
        readBack.UpdatedAt.Should().BeCloseTo(now.AddDays(-1), TimeSpan.FromMilliseconds(1));
        (await db.GovernanceFindings.AsNoTracking().AnyAsync(x =>
            x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId &&
            x.SourceConnectionId == victimSource.Id)).Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Receipt_Should_Report_Identity_Based_Exception_Delta_Across_ReReview()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"exception-delta-{Guid.NewGuid():N}";
        var historicalIdentity = new GovernanceReceiptContractIdentity(
            "scheduled-0.9",
            "sha256:historical",
            "catalog-historical");
        var first = CreateReview(runId, "snapshot-1",
        [
            new GovernanceExceptionStateResult("finding:a", "GovernanceFinding", "Deferred", 1),
            new GovernanceExceptionStateResult("finding:b", "GovernanceFinding", "RequiresUserDecision", 2)
        ]) with
        {
            ReceiptContractIdentity = historicalIdentity
        };
        await receipts.RecordReviewAsync(first, DateTimeOffset.UtcNow, CancellationToken.None);
        var firstReceipt = await receipts.GetAsync(runId, CancellationToken.None);
        firstReceipt.Should().NotBeNull();
        firstReceipt!.ExceptionDelta.Should().Be(new GovernanceExceptionDeltaResult(2, 0, 0, 0));
        firstReceipt.ToolContractVersion.Should().Be(historicalIdentity.ToolContractVersion);
        firstReceipt.SchemaHash.Should().Be(historicalIdentity.SchemaHash);
        firstReceipt.PublishedCatalogVersion.Should().Be(historicalIdentity.PublishedCatalogVersion);

        var second = CreateReview(runId, "snapshot-2",
        [
            new GovernanceExceptionStateResult("finding:b", "GovernanceFinding", "HostBlocked", 3),
            new GovernanceExceptionStateResult("finding:c", "GovernanceFinding", "Deferred", 1)
        ], isReReview: true) with
        {
            ReceiptContractIdentity = historicalIdentity
        };
        await receipts.RecordReviewAsync(second, DateTimeOffset.UtcNow, CancellationToken.None);
        var secondReceipt = await receipts.GetAsync(runId, CancellationToken.None);

        secondReceipt.Should().NotBeNull();
        secondReceipt!.ExceptionDelta.Should().Be(new GovernanceExceptionDeltaResult(1, 1, 0, 1));
        secondReceipt.GovernedExceptionStates.Select(x => x.Key)
            .Should().BeEquivalentTo("finding:b", "finding:c");
    }

    [DockerRequiredFact]
    public async Task InternalRetentionWorker_Should_Claim_Concurrent_Batches_Without_Duplicate_Delete()
    {
        Guid memoryId;
        Guid tenantId;
        Guid ownerUserId;
        var projectId = $"retention-claim-{Guid.NewGuid():N}";
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var actor = UseBootstrapActor(scope.ServiceProvider);
            tenantId = actor.TenantId!.Value;
            ownerUserId = actor.UserId!.Value;
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var retention = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
            var memory = CreateLowValueMemory(actor, projectId);
            memory.Tags = ["machine-generated", "execution-evidence", "synthetic-disposable"];
            memoryId = memory.Id;
            db.MemoryItems.Add(memory);
            await db.SaveChangesAsync();
            await retention.QuarantineAsync(memory.Id, projectId, "claim-quarantine", CancellationToken.None);
            var state = await db.MemoryRetentionStates.SingleAsync(x => x.ResourceId == memory.Id);
            state.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-8);
            state.DeleteEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            state.LifecycleStatus = "Eligible";
            await db.SaveChangesAsync();
        }

        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var batches = await Task.WhenAll(
            executor.ExecuteNextBatchAsync(CancellationToken.None),
            executor.ExecuteNextBatchAsync(CancellationToken.None));

        batches.Sum(x => x.DeletedCount).Should().Be(1);
        using var readScope = environment.GetFactory().Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await readDb.MemoryItems.AnyAsync(x => x.Id == memoryId)).Should().BeFalse();
        (await readDb.ResourceTombstones.CountAsync(x =>
            x.ResourceId == memoryId && x.TenantId == tenantId && x.OwnerUserId == ownerUserId)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task InternalRetentionWorker_Should_Persist_Claim_Release_When_Revalidation_Cancels_Eligibility()
    {
        Guid memoryId;
        var projectId = $"retention-cancel-{Guid.NewGuid():N}";
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var actor = UseBootstrapActor(scope.ServiceProvider);
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var retention = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
            var memory = CreateLowValueMemory(actor, projectId);
            memory.Tags = ["machine-generated", "execution-evidence", "synthetic-disposable"];
            memoryId = memory.Id;
            db.MemoryItems.Add(memory);
            await db.SaveChangesAsync();
            await retention.QuarantineAsync(memory.Id, projectId, "cancel-quarantine", CancellationToken.None);
            var state = await db.MemoryRetentionStates.SingleAsync(x => x.ResourceId == memory.Id);
            state.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-8);
            state.DeleteEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            state.LifecycleStatus = "Eligible";
            db.ProjectWorkItems.Add(new ProjectWorkItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.UserId,
                ProjectId = projectId,
                Title = "Late retention dependency",
                Description = $"Resource {memory.Id} must remain available.",
                Status = ProjectWorkItemStatus.InProgress,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var result = await executor.ExecuteNextBatchAsync(CancellationToken.None);

        result.CancelledCount.Should().Be(1);
        result.DeletedCount.Should().Be(0);
        using var readScope = environment.GetFactory().Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var readState = await readDb.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == memoryId);
        readState.LifecycleStatus.Should().Be("Cancelled");
        readState.ClaimToken.Should().BeEmpty();
        readState.ClaimedAt.Should().BeNull();
        readState.ClaimLastError.Should().Be("EligibilityCancelled");
        (await readDb.MemoryItems.AnyAsync(x => x.Id == memoryId)).Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task MaturedDelete_Should_Wait_For_Text_Evidence_Writer_Then_Observe_Committed_Reference()
    {
        var (memoryId, projectId) = await CreateEligibleRetentionCandidateAsync("retention-writer-race");
        using var writerScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(writerScope.ServiceProvider);
        var writerDb = writerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await using var writerTransaction = await writerDb.Database.BeginTransactionAsync();
        writerDb.ProjectWorkItems.Add(new ProjectWorkItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            Title = "Concurrent retention dependency",
            Description = $"Resource {memoryId} must remain available.",
            Status = ProjectWorkItemStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await writerDb.SaveChangesAsync();

        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var execution = executor.ExecuteNextBatchAsync(CancellationToken.None);
        await WaitForClaimAsync(memoryId);
        await Task.Delay(250);
        execution.IsCompleted.Should().BeFalse(
            "the delete transaction must coordinate with text-evidence writers before revalidation");

        await writerTransaction.CommitAsync();
        var result = await execution;

        result.CancelledCount.Should().Be(1);
        using var readScope = environment.GetFactory().Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await readDb.MemoryItems.AnyAsync(x => x.Id == memoryId)).Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task InternalRetentionWorker_Should_Release_Claim_When_Shutdown_Cancels_A_Blocked_Delete()
    {
        var (memoryId, projectId) = await CreateEligibleRetentionCandidateAsync("retention-cancelled-worker");
        using var writerScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(writerScope.ServiceProvider);
        var writerDb = writerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await using var writerTransaction = await writerDb.Database.BeginTransactionAsync();
        writerDb.ProjectWorkItems.Add(new ProjectWorkItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            Title = "Uncommitted writer lock",
            Description = "This transaction exists only to hold the evidence-writer table lock.",
            Status = ProjectWorkItemStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await writerDb.SaveChangesAsync();

        using var cancellation = new CancellationTokenSource();
        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var execution = executor.ExecuteNextBatchAsync(cancellation.Token);
        var claimToken = await WaitForClaimAsync(memoryId);
        cancellation.Cancel();
        await writerTransaction.RollbackAsync();

        await FluentActions.Awaiting(() => execution).Should().ThrowAsync<OperationCanceledException>();
        using var readScope = environment.GetFactory().Services.CreateScope();
        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var state = await readDb.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == memoryId);
        state.ClaimToken.Should().BeEmpty();
        state.ClaimedAt.Should().BeNull();
        state.ClaimLastError.Should().Be("Cancelled");
        claimToken.Should().NotBeEmpty();
    }

    [DockerRequiredFact]
    public async Task InternalRetentionWorker_Should_Recover_Missing_Receipt_After_Restart()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"internal-retention-recovery-{Guid.NewGuid():N}";
        var tombstone = new ResourceTombstone
        {
            ResourceId = Guid.NewGuid(),
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            ProjectId = $"receipt-recovery-{Guid.NewGuid():N}",
            ContentHash = "sha256:test",
            Classification = "SyntheticDisposable",
            ArchivedAt = DateTimeOffset.UtcNow.AddDays(-8),
            DeletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            RetentionPolicyVersion = "test-v1",
            GovernanceRunId = runId,
            AuditId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ResourceTombstones.Add(tombstone);
        await db.SaveChangesAsync();

        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        await executor.ExecuteNextBatchAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var receipt = await db.GovernanceRunReceipts.AsNoTracking().SingleAsync(x =>
            x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId &&
            x.GovernanceRunId == runId && x.EventType == "InternalRetentionCompleted");
        receipt.AutoDeleted.Should().Be(1);
        receipt.Tombstoned.Should().Be(1);
        receipt.StoppedReason.Should().Be("RecoveredAfterRestart");
    }

    [DockerRequiredFact]
    public async Task InternalRetentionWorker_Should_Recover_All_Tombstones_For_Run_Larger_Than_Discovery_Page()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"internal-retention-large-recovery-{Guid.NewGuid():N}";
        var projectId = $"large-recovery-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var tombstones = Enumerable.Range(0, 125).Select(index => new ResourceTombstone
        {
            ResourceId = Guid.NewGuid(),
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            ProjectId = projectId,
            ContentHash = $"sha256:test-{index}",
            Classification = "SyntheticDisposable",
            ArchivedAt = now.AddDays(-8),
            DeletedAt = now.AddMinutes(-2).AddMilliseconds(index),
            RetentionPolicyVersion = "test-v1",
            GovernanceRunId = runId,
            AuditId = Guid.NewGuid(),
            CreatedAt = now
        }).ToArray();
        db.ResourceTombstones.AddRange(tombstones);
        await db.SaveChangesAsync();

        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        await executor.ExecuteNextBatchAsync(CancellationToken.None);

        db.ChangeTracker.Clear();
        var receipt = await db.GovernanceRunReceipts.AsNoTracking().SingleAsync(x =>
            x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId &&
            x.GovernanceRunId == runId && x.EventType == "InternalRetentionCompleted");
        receipt.AutoDeleted.Should().Be(125);
        receipt.Tombstoned.Should().Be(125);
        receipt.StoppedReason.Should().Be("RecoveredAfterRestart");
        var auditIds = System.Text.Json.JsonSerializer.Deserialize<Guid[]>(receipt.AuditIdsJson);
        auditIds.Should().BeEquivalentTo(tombstones.Select(x => x.AuditId));
    }

    private static KnowledgeReviewResult CreateReview(
        string runId,
        string snapshotToken,
        IReadOnlyList<GovernanceExceptionStateResult> exceptionStates,
        bool isReReview = false)
    {
        var durable = new KnowledgeGovernanceCoverageResult(
            Guid.NewGuid(), snapshotToken, DateTimeOffset.UtcNow,
            0, 0, 0, 0, 0, 0, true, false, null)
        {
            AuthorizedGovernanceDurableMemoryCount = 0,
            GovernanceCoveredDurableMemoryCount = 0,
            GovernanceProjectIds = [ProjectContext.SharedProjectId]
        };
        var surface = new GovernanceSurfaceCoverageResult(0, 0, 0, 0, 0, 0, 0, false, true);
        var coverage = new FullGovernanceCoverageResult(
            surface, surface, surface, surface, surface, surface, surface, surface, surface, surface, surface);
        var page = new KnowledgeReviewPageResult(0, 200, 0, 0, false);
        var convergence = new KnowledgeReviewConvergenceResult("HumanDecisionOnly", 0, false, true)
        {
            CoverageComplete = true,
            GovernedExceptionCount = exceptionStates.Count,
            DeferredCount = exceptionStates.Count(x => x.Disposition == "Deferred"),
            RequiresUserDecisionCount = exceptionStates.Count(x => x.Disposition == "RequiresUserDecision"),
            HostBlockedCount = exceptionStates.Count(x => x.Disposition == "HostBlocked")
        };
        return new KnowledgeReviewResult(
            [new AccessibleProjectResult(ProjectContext.SharedProjectId, true, true)],
            null!, [], [], [], [], [], [], [], runId, isReReview,
            new KnowledgeReviewPaginationResult(page, page, page, page, page, page, page, page),
            convergence)
        {
            DurableMemoryCoverage = durable,
            GovernanceCoverage = coverage,
            GovernedExceptionCount = exceptionStates.Count,
            GovernedExceptionStates = exceptionStates
        };
    }

    private static ContextHubRequestActor UseBootstrapActor(IServiceProvider services)
    {
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = db.TenantUsers.Single(x => x.Username == "contract-test-admin");
        var actor = new ContextHubRequestActor(user.TenantId, user.Id, user.Username, user.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite, SecurityScopes.SecurityManage], [], true);
        services.GetRequiredService<IRequestActorAccessor>().Current = actor;
        return actor;
    }

    private static (Tenant Tenant, TenantUser User) CreateOtherOwner()
    {
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        return (
            new Tenant
            {
                Id = tenantId,
                Slug = $"governance-{tenantId:N}"[..28],
                DisplayName = "Governance isolation tenant",
                Status = TenantStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            },
            new TenantUser
            {
                Id = userId,
                TenantId = tenantId,
                Username = $"governance-{userId:N}"[..28],
                DisplayName = "Governance isolation owner",
                Role = TenantUserRole.Admin,
                Status = TenantUserStatus.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
    }

    private async Task<(Guid MemoryId, string ProjectId)> CreateEligibleRetentionCandidateAsync(string prefix)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var retention = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var projectId = $"{prefix}-{Guid.NewGuid():N}";
        var memory = CreateLowValueMemory(actor, projectId);
        memory.Tags = ["machine-generated", "execution-evidence", "synthetic-disposable"];
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();
        await retention.QuarantineAsync(memory.Id, projectId, $"{prefix}-quarantine", CancellationToken.None);
        var state = await db.MemoryRetentionStates.SingleAsync(x => x.ResourceId == memory.Id);
        state.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-8);
        state.DeleteEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        state.LifecycleStatus = "Eligible";
        await db.SaveChangesAsync();
        return (memory.Id, projectId);
    }

    private async Task<string> WaitForClaimAsync(Guid memoryId)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            using var scope = environment.GetFactory().Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var claimToken = await db.MemoryRetentionStates.AsNoTracking()
                .Where(x => x.ResourceId == memoryId)
                .Select(x => x.ClaimToken)
                .SingleAsync();
            if (!string.IsNullOrWhiteSpace(claimToken))
            {
                return claimToken;
            }
            await Task.Delay(100);
        }

        throw new TimeoutException($"Retention worker did not claim resource '{memoryId}' within the test bound.");
    }

    private static MemoryItem CreateLowValueMemory(ContextHubRequestActor actor, string projectId)
        => new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            MemoryType = MemoryType.Episode,
            Scope = MemoryScope.Project,
            Status = MemoryStatus.Active,
            Title = "Low value retention candidate",
            Content = "Synthetic deterministic lifecycle evidence.",
            Summary = "Synthetic deterministic lifecycle evidence.",
            SourceType = "test",
            SourceRef = $"test://{projectId}",
            Tags = ["low-value"],
            Importance = .1m,
            Confidence = .2m,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-90)
        };
}

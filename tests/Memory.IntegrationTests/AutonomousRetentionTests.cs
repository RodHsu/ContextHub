using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class AutonomousRetentionTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Quarantine_Grace_MaturedDelete_Tombstone_And_Replay_Should_Be_ExactlyOnce()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var projectId = $"Retention_{Guid.NewGuid():N}";
        var memory = CreateMemory(actor, projectId, MemoryType.Episode,
            ["machine-generated", "execution-evidence", "synthetic-disposable"]);
        var revision = new MemoryItemRevision
        {
            MemoryItemId = memory.Id,
            Version = 1,
            Title = memory.Title,
            Content = memory.Content,
            Summary = memory.Summary,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        var chunk = new MemoryItemChunk
        {
            MemoryItemId = memory.Id,
            ChunkKind = ChunkKind.Document,
            ChunkIndex = 0,
            ChunkText = memory.Content,
            CreatedAt = DateTimeOffset.UtcNow
        };
        var vector = new MemoryChunkVector
        {
            ChunkId = chunk.Id,
            ModelKey = "test",
            Dimension = 384,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.MemoryItems.Add(memory);
        db.MemoryItemRevisions.Add(revision);
        db.MemoryItemChunks.Add(chunk);
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO memory_chunk_vectors (id, chunk_id, model_key, dimension, embedding, status, created_at)
            VALUES ({vector.Id}, {vector.ChunkId}, {vector.ModelKey}, {vector.Dimension},
                    array_fill(0::real, ARRAY[384])::vector, {vector.Status}, {vector.CreatedAt})
            """);

        var review = await service.ReviewAsync([projectId], "retention-initial", CancellationToken.None);
        review.Candidates.Should().ContainSingle(x => x.ResourceId == memory.Id && x.RecommendedAction == "Quarantine" && x.GracePeriodDays == 7);
        var quarantined = await service.QuarantineAsync(memory.Id, projectId, "retention-quarantine", CancellationToken.None);
        quarantined.DeleteEligibleAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(6));
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == memory.Id)).Status.Should().Be(MemoryStatus.Archived);

        var beforeMaturity = async () => await service.DeleteMaturedAsync(memory.Id, projectId, "retention-too-early", CancellationToken.None);
        await beforeMaturity.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maturity*");

        var state = await db.MemoryRetentionStates.SingleAsync(x => x.ResourceId == memory.Id);
        state.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-8);
        state.DeleteEligibleAt = DateTimeOffset.UtcNow.AddDays(-1);
        state.LifecycleStatus = "Eligible";
        await db.SaveChangesAsync();

        var matured = await service.ReviewAsync([projectId], "retention-matured", CancellationToken.None);
        matured.Candidates.Should().ContainSingle(x => x.ResourceId == memory.Id && x.Matured && x.RecommendedAction == "MaturedDelete");
        var deleted = await service.DeleteMaturedAsync(memory.Id, projectId, "retention-delete", CancellationToken.None);
        deleted.Deleted.Should().BeTrue();
        deleted.IsReplay.Should().BeFalse();
        deleted.DeletedRevisionCount.Should().BeGreaterThan(0);
        deleted.DeletedChunkCount.Should().Be(1);
        deleted.DeletedVectorCount.Should().Be(1);
        (await db.MemoryItems.AnyAsync(x => x.Id == memory.Id)).Should().BeFalse();
        (await db.MemoryItemRevisions.AnyAsync(x => x.MemoryItemId == memory.Id)).Should().BeFalse();
        (await db.MemoryItemChunks.AnyAsync(x => x.MemoryItemId == memory.Id)).Should().BeFalse();
        (await db.MemoryChunkVectors.AnyAsync(x => x.ChunkId == chunk.Id)).Should().BeFalse();

        var tombstone = await service.GetTombstoneAsync(memory.Id, projectId, CancellationToken.None);
        tombstone.Should().NotBeNull();
        tombstone!.ContentHash.Should().NotBeNullOrWhiteSpace();
        tombstone.ReasonCodes.Should().NotBeEmpty();
        var replay = await service.DeleteMaturedAsync(memory.Id, projectId, "retention-delete", CancellationToken.None);
        replay.IsReplay.Should().BeTrue();
        replay.Deleted.Should().BeFalse();
        replay.TombstoneId.Should().Be(deleted.TombstoneId);
        replay.AuditId.Should().Be(deleted.AuditId);
        (await db.ResourceTombstones.CountAsync(x => x.ResourceId == memory.Id)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Grace_Revalidation_Should_Cancel_For_Link_Hit_Hold_And_MissingReplacement()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var projectId = $"RetentionCancel_{Guid.NewGuid():N}";

        var linked = CreateMemory(actor, projectId, MemoryType.Episode, ["machine-generated", "execution-evidence"]);
        var other = CreateMemory(actor, projectId, MemoryType.Episode, ["machine-generated"]);
        db.MemoryItems.AddRange(linked, other);
        await db.SaveChangesAsync();
        await service.QuarantineAsync(linked.Id, projectId, "cancel-link-q", CancellationToken.None);
        db.MemoryLinks.Add(new MemoryLink { FromId = other.Id, ToId = linked.Id, LinkType = "references", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        var linkReview = await service.ReviewAsync([projectId], "cancel-link-r", CancellationToken.None);
        linkReview.DeleteCancelledCount.Should().BeGreaterThan(0);
        (await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == linked.Id)).DeleteEligibleAt.Should().BeNull();

        var hit = CreateMemory(actor, projectId, MemoryType.Episode, ["machine-generated", "execution-evidence"]);
        db.MemoryItems.Add(hit);
        await db.SaveChangesAsync();
        await service.QuarantineAsync(hit.Id, projectId, "cancel-hit-q", CancellationToken.None);
        db.RetrievalTelemetryDailyHitSummaries.Add(new RetrievalTelemetryDailyHitSummary
        {
            SummaryDate = DateOnly.FromDateTime(DateTime.UtcNow),
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            ProjectId = projectId,
            EntryPoint = "test",
            MemoryId = hit.Id,
            Title = hit.Title,
            MemoryType = hit.MemoryType.ToString(),
            SourceType = hit.SourceType,
            SourceRef = hit.SourceRef,
            HitCount = 1,
            BestRank = 1,
            FirstSeenAt = DateTimeOffset.UtcNow,
            LastSeenAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await service.ReviewAsync([projectId], "cancel-hit-r", CancellationToken.None);
        (await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == hit.Id)).BlockedReasonsJson.Should().Contain("recentHits");

        var held = CreateMemory(actor, projectId, MemoryType.Episode, ["machine-generated", "execution-evidence"]);
        db.MemoryItems.Add(held);
        await db.SaveChangesAsync();
        await service.QuarantineAsync(held.Id, projectId, "cancel-hold-q", CancellationToken.None);
        held.Tags = [.. held.Tags, "legal-hold"];
        db.MemoryItems.Update(held);
        await db.SaveChangesAsync();
        await service.ReviewAsync([projectId], "cancel-hold-r", CancellationToken.None);
        (await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == held.Id)).BlockedReasonsJson.Should().Contain("legalHold");

        var replacement = CreateMemory(actor, projectId, MemoryType.Artifact, ["formal"]);
        var temporary = CreateMemory(actor, projectId, MemoryType.Artifact, ["temporary", "obsolete"]);
        temporary.MetadataJson = $$"""{"supersededByMemoryId":"{{replacement.Id:D}}"}""";
        db.MemoryItems.AddRange(replacement, temporary);
        db.MemoryLinks.Add(new MemoryLink { FromId = temporary.Id, ToId = replacement.Id, LinkType = "replaced_by", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync();
        await service.QuarantineAsync(temporary.Id, projectId, "cancel-replacement-q", CancellationToken.None);
        db.MemoryLinks.RemoveRange(await db.MemoryLinks.Where(x => x.FromId == temporary.Id).ToListAsync());
        db.MemoryItems.Remove(replacement);
        await db.SaveChangesAsync();
        await service.ReviewAsync([projectId], "cancel-replacement-r", CancellationToken.None);
        (await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == temporary.Id)).BlockedReasonsJson.Should().Contain("replacementChainIncomplete");

        var workItemReferenced = CreateMemory(actor, projectId, MemoryType.Episode, ["execution-evidence"]);
        db.MemoryItems.Add(workItemReferenced);
        await db.SaveChangesAsync();
        await service.QuarantineAsync(workItemReferenced.Id, projectId, "cancel-work-item-q", CancellationToken.None);
        db.ProjectWorkItems.Add(new ProjectWorkItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            Title = $"Investigate memory {workItemReferenced.Id:D}",
            Description = "Active governance dependency fixture.",
            Status = ProjectWorkItemStatus.InProgress,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await service.ReviewAsync([projectId], "cancel-work-item-r", CancellationToken.None);
        (await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == workItemReferenced.Id))
            .BlockedReasonsJson.Should().Contain("activeWorkItemReference");

        var policyChanged = CreateMemory(actor, projectId, MemoryType.Episode, ["execution-evidence"]);
        db.MemoryItems.Add(policyChanged);
        await db.SaveChangesAsync();
        await service.QuarantineAsync(policyChanged.Id, projectId, "policy-change-q", CancellationToken.None);
        var policyState = await db.MemoryRetentionStates.SingleAsync(x => x.ResourceId == policyChanged.Id);
        policyState.PolicyVersion = "retired-policy";
        policyState.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-10);
        policyState.DeleteEligibleAt = DateTimeOffset.UtcNow.AddDays(-3);
        await db.SaveChangesAsync();
        await service.ReviewAsync([projectId], "policy-change-r", CancellationToken.None);
        var resetPolicyState = await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == policyChanged.Id);
        resetPolicyState.PolicyVersion.Should().NotBe("retired-policy");
        resetPolicyState.DeleteEligibleAt.Should().BeAfter(DateTimeOffset.UtcNow.AddDays(6));
    }

    [DockerRequiredFact]
    public async Task Archived_Work_Items_And_Discussions_Should_Not_Block_Revalidation()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var projectId = $"RetentionArchivedReferences_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;

        var workItemMemory = CreateMemory(actor, projectId, MemoryType.Episode, ["machine-generated", "execution-evidence"]);
        var discussionMemory = CreateMemory(actor, projectId, MemoryType.Episode, ["machine-generated", "execution-evidence"]);
        db.MemoryItems.AddRange(workItemMemory, discussionMemory);
        await db.SaveChangesAsync();
        await service.QuarantineAsync(workItemMemory.Id, projectId, "archive-work-item-q", CancellationToken.None);
        await service.QuarantineAsync(discussionMemory.Id, projectId, "archive-discussion-q", CancellationToken.None);

        var workItem = new ProjectWorkItem
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            Title = $"Investigate memory {workItemMemory.Id:D}",
            Description = "Active work item reference fixture.",
            Status = ProjectWorkItemStatus.InProgress,
            CreatedAt = now,
            UpdatedAt = now
        };
        var discussion = new DiscussionThread
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            HostProjectId = projectId,
            Title = $"Discuss memory {discussionMemory.Id:D}",
            Status = "Open",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ProjectWorkItems.Add(workItem);
        db.DiscussionThreads.Add(discussion);
        db.DiscussionMessages.Add(new DiscussionMessage
        {
            ThreadId = discussion.Id,
            SenderProjectId = projectId,
            Content = $"Active discussion reference for memory {discussionMemory.Id:D}.",
            CreatedAt = now
        });
        await db.SaveChangesAsync();

        await service.ReviewAsync([projectId], "archive-references-active", CancellationToken.None);
        var blockedWorkItemState = await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == workItemMemory.Id);
        var blockedDiscussionState = await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == discussionMemory.Id);
        blockedWorkItemState.BlockedReasonsJson.Should().Contain("activeWorkItemReference");
        blockedDiscussionState.BlockedReasonsJson.Should().Contain("activeDiscussionReference");
        blockedWorkItemState.DeleteEligibleAt.Should().BeNull();
        blockedDiscussionState.DeleteEligibleAt.Should().BeNull();

        workItem.ArchivedAt = now.AddMinutes(1);
        workItem.UpdatedAt = now.AddMinutes(1);
        discussion.ArchivedAt = now.AddMinutes(1);
        discussion.UpdatedAt = now.AddMinutes(1);
        await db.SaveChangesAsync();

        await service.ReviewAsync([projectId], "archive-references-restored", CancellationToken.None);
        var restoredWorkItemState = await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == workItemMemory.Id);
        var restoredDiscussionState = await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == discussionMemory.Id);
        restoredWorkItemState.BlockedReasonsJson.Should().NotContain("activeWorkItemReference");
        restoredDiscussionState.BlockedReasonsJson.Should().NotContain("activeDiscussionReference");
        restoredWorkItemState.DeleteEligibleAt.Should().NotBeNull();
        restoredDiscussionState.DeleteEligibleAt.Should().NotBeNull();
    }

    [DockerRequiredFact]
    public async Task Foreign_MemoryJobs_Should_Be_Isolated_And_Legacy_Unowned_Jobs_Should_Fail_Closed()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var projectId = $"RetentionJobOwnership_{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var memory = CreateMemory(actor, projectId, MemoryType.Episode, ["machine-generated", "execution-evidence"]);
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();
        await service.QuarantineAsync(memory.Id, projectId, "job-ownership-q", CancellationToken.None);

        var foreignTenantId = Guid.NewGuid();
        var foreignUserId = Guid.NewGuid();
        db.Tenants.Add(new Tenant
        {
            Id = foreignTenantId,
            Slug = $"retention-job-{foreignTenantId:N}"[..28],
            DisplayName = "Retention Job Foreign Tenant",
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.TenantUsers.Add(new TenantUser
        {
            Id = foreignUserId,
            TenantId = foreignTenantId,
            Username = $"retention-job-{foreignUserId:N}"[..28],
            DisplayName = "Retention Job Foreign User",
            Role = TenantUserRole.Admin,
            Status = TenantUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();
        var payload = $$"""{"memoryId":"{{memory.Id:D}}"}""";
        var foreignJob = new MemoryJob
        {
            TenantId = foreignTenantId,
            OwnerUserId = foreignUserId,
            ProjectId = projectId,
            JobType = MemoryJobType.Reindex,
            Status = MemoryJobStatus.Pending,
            PayloadJson = payload,
            CreatedAt = now
        };
        var legacyTenantMissing = new MemoryJob
        {
            TenantId = null,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            JobType = MemoryJobType.Reindex,
            Status = MemoryJobStatus.Running,
            PayloadJson = payload,
            CreatedAt = now
        };
        var legacyOwnerMissing = new MemoryJob
        {
            TenantId = actor.TenantId,
            OwnerUserId = null,
            ProjectId = projectId,
            JobType = MemoryJobType.Reindex,
            Status = MemoryJobStatus.Pending,
            PayloadJson = payload,
            CreatedAt = now
        };
        db.MemoryJobs.AddRange(foreignJob, legacyTenantMissing, legacyOwnerMissing);
        await db.SaveChangesAsync();

        await service.ReviewAsync([projectId], "job-ownership-revalidation", CancellationToken.None);
        var state = await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == memory.Id);
        state.BlockedReasonsJson.Should().Contain("activeDependency");
        state.DeleteEligibleAt.Should().BeNull();

        db.MemoryJobs.RemoveRange(legacyTenantMissing, legacyOwnerMissing);
        await db.SaveChangesAsync();
        await service.ReviewAsync([projectId], "job-ownership-legacy-cleared", CancellationToken.None);
        state = await db.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == memory.Id);
        state.BlockedReasonsJson.Should().NotContain("activeDependency");
        state.DeleteEligibleAt.Should().NotBeNull();
    }

    [DockerRequiredFact]
    public async Task Protected_Types_And_Security_Audit_Evidence_Should_Not_Use_Short_AutoDelete()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var projectId = $"RetentionProtected_{Guid.NewGuid():N}";
        var values = new[]
        {
            CreateMemory(actor, projectId, MemoryType.Decision, ["machine-generated", "execution-evidence"]),
            CreateMemory(actor, projectId, MemoryType.Fact, ["machine-generated", "execution-evidence"]),
            CreateMemory(actor, projectId, MemoryType.Artifact, ["formal"]),
            CreateMemory(actor, projectId, MemoryType.Episode, ["security", "audit", "execution-evidence"])
        };
        db.MemoryItems.AddRange(values);
        await db.SaveChangesAsync();
        var review = await service.ReviewAsync([projectId], "protected-review", CancellationToken.None);
        review.ProtectedRetentionCount.Should().Be(values.Length);
        review.Candidates.Should().NotContain(x => values.Select(v => v.Id).Contains(x.ResourceId));

        var runtimeNoise = CreateMemory(actor, projectId, MemoryType.Episode, ["runtime-noise"]);
        runtimeNoise.SourceType = "runtime-log";
        db.MemoryItems.Add(runtimeNoise);
        await db.SaveChangesAsync();
        var typedRuntimeReview = await service.ReviewAsync([projectId], "protected-runtime-typed", CancellationToken.None);
        typedRuntimeReview.Candidates.Should().ContainSingle(x =>
            x.ResourceId == runtimeNoise.Id && x.PolicyKind == "runtime-noise" && x.GracePeriodDays == 14);
    }

    [DockerRequiredFact]
    public async Task RequiresUserDecision_Should_Automatically_Reopen_When_Durable_Evidence_Changes()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var governance = scope.ServiceProvider.GetRequiredService<IGovernanceService>();
        var projectId = $"semantic-reopen-{Guid.NewGuid():N}";
        var memory = CreateMemory(actor, projectId, MemoryType.Episode, ["low-value"]);
        memory.Importance = .1m;
        memory.Confidence = .2m;
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();

        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var finding = await db.GovernanceFindings.SingleAsync(x =>
            x.ProjectId == projectId && x.PrimaryMemoryId == memory.Id &&
            x.Type == GovernanceFindingType.LowValueMemoryCandidate);
        await governance.SetDispositionAsync(
            new GovernanceFindingDispositionRequest(
                finding.Id,
                GovernanceFindingDisposition.RequiresUserDecision,
                "Insufficient durable evidence.",
                $"semantic-defer-{Guid.NewGuid():N}"),
            CancellationToken.None);

        memory.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        memory.MetadataJson = "{\"authority\":\"project-owner-confirmed\"}";
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);

        var reopened = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        reopened.Status.Should().Be(GovernanceFindingStatus.Open);
        reopened.GovernanceRetryCount.Should().Be(1);
        reopened.GovernanceReason.Should().Contain("evidence or policy changed");
    }

    [DockerRequiredFact]
    public async Task Mixed_2000_Items_Should_Classify_Without_Garbage_Backlog_Amplification()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var projectId = $"RetentionScale_{Guid.NewGuid():N}";
        var values = Enumerable.Range(0, 2100).Select(index => (index % 3) switch
        {
            0 => CreateMemory(actor, projectId, MemoryType.Episode, ["machine-generated", "execution-evidence"]),
            1 => CreateMemory(actor, projectId, MemoryType.Fact, ["formal"]),
            _ => CreateMemory(actor, projectId, MemoryType.Artifact, ["formal"])
        }).ToArray();
        db.MemoryItems.AddRange(values);
        await db.SaveChangesAsync();
        var started = DateTimeOffset.UtcNow;
        var review = await service.ReviewAsync([projectId], "scale-review", CancellationToken.None);
        review.Candidates.Count(x => x.RecommendedAction == "Quarantine").Should().Be(700);
        review.ProtectedRetentionCount.Should().Be(1400);
        (DateTimeOffset.UtcNow - started).Should().BeLessThan(TimeSpan.FromSeconds(30));
        (await db.MemoryRetentionStates.CountAsync(x => x.ProjectId == projectId)).Should().Be(0,
            "classification alone must not create durable garbage or bypass the quarantine executor");
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

    private static MemoryItem CreateMemory(ContextHubRequestActor actor, string projectId, MemoryType type, string[] tags)
    {
        var id = Guid.NewGuid();
        return new MemoryItem
        {
            Id = id,
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            ExternalKey = $"retention:{id:N}",
            Scope = MemoryScope.Project,
            MemoryType = type,
            Title = $"Synthetic retention {id:N}",
            Content = $"Disposable content {id:N}",
            Summary = "Disposable evidence",
            Tags = tags,
            SourceType = "tool-execution",
            SourceRef = "integration/autonomous-retention",
            Importance = 0.10m,
            Confidence = 0.20m,
            Status = MemoryStatus.Active,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
    }
}

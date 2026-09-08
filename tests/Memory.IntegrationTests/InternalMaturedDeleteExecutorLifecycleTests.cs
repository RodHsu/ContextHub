using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class InternalMaturedDeleteExecutorLifecycleTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Worker_Should_Advance_Grace_Revalidate_Delete_And_Write_One_Minimal_Tombstone()
    {
        var owner = await CreateOwnerAsync();
        Guid memoryId;
        string projectId;

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseActor(scope.ServiceProvider, owner.Actor);
            projectId = NewProjectId("retention-lifecycle");
            memoryId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId, includeContentGraph: true);
        }

        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var result = await executor.ExecuteNextBatchAsync(CancellationToken.None);

        result.ScannedCount.Should().Be(1);
        result.DeletedCount.Should().Be(1);
        result.CancelledCount.Should().Be(0);
        result.FailedCount.Should().Be(0);
        result.TombstoneIds.Should().ContainSingle();
        result.AuditIds.Should().ContainSingle();
        result.ProjectIds.Should().ContainSingle(projectId);
        result.StoppedReason.Should().Be("Completed");

        using var readScope = environment.GetFactory().Services.CreateScope();
        UseActor(readScope.ServiceProvider, owner.Actor);
        var db = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await db.MemoryItems.AnyAsync(x => x.Id == memoryId)).Should().BeFalse();
        (await db.MemoryItemRevisions.AnyAsync(x => x.MemoryItemId == memoryId)).Should().BeFalse();
        (await db.MemoryItemChunks.AnyAsync(x => x.MemoryItemId == memoryId)).Should().BeFalse();
        (await db.MemoryRetentionStates.AnyAsync(x => x.ResourceId == memoryId)).Should().BeFalse();

        var tombstone = await db.ResourceTombstones.SingleAsync(x => x.ResourceId == memoryId);
        tombstone.Id.Should().Be(result.TombstoneIds.Single());
        tombstone.ResourceType.Should().Be("Memory");
        tombstone.TenantId.Should().Be(owner.TenantId);
        tombstone.OwnerUserId.Should().Be(owner.UserId);
        tombstone.ProjectId.Should().Be(projectId);
        tombstone.ContentHash.Should().NotBeNullOrWhiteSpace();
        tombstone.Classification.Should().Be("LowValueMachineEvidence");
        tombstone.RetentionPolicyVersion.Should().NotBeNullOrWhiteSpace();
        tombstone.GovernanceRunId.Should().Be(result.GovernanceRunId);
        tombstone.AuditId.Should().Be(result.AuditIds.Single());

        using var reasonDocument = JsonDocument.Parse(tombstone.ReasonCodesJson);
        reasonDocument.RootElement.ValueKind.Should().Be(JsonValueKind.Array);
        reasonDocument.RootElement.EnumerateArray().Should().NotBeEmpty();

        var receipt = await db.GovernanceRunReceipts.SingleAsync(x =>
            x.TenantId == owner.TenantId &&
            x.OwnerUserId == owner.UserId &&
            x.GovernanceRunId == result.GovernanceRunId &&
            x.EventType == "InternalRetentionCompleted");
        receipt.Status.Should().Be("Completed");
        receipt.DeleteMatured.Should().Be(1);
        receipt.AutoDeleted.Should().Be(1);
        receipt.Tombstoned.Should().Be(1);
        receipt.Failed.Should().Be(0);

        var retention = readScope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
        var directAdminReplay = async () => await retention.DeleteMaturedAsync(
            memoryId, projectId, result.GovernanceRunId, CancellationToken.None);
        await directAdminReplay.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*internal:retention-delete*");
        UseActor(readScope.ServiceProvider, owner.Actor with
        {
            Scopes = owner.Actor.Scopes.Append(SecurityScopes.InternalRetentionDelete).Distinct().ToArray(),
            IsServiceActor = true
        });
        var replay = await retention.DeleteMaturedAsync(memoryId, projectId, result.GovernanceRunId, CancellationToken.None);
        replay.Deleted.Should().BeFalse();
        replay.IsReplay.Should().BeTrue();
        replay.TombstoneId.Should().Be(tombstone.Id);
        replay.AuditId.Should().Be(tombstone.AuditId);

        var repeated = await executor.ExecuteNextBatchAsync(CancellationToken.None);
        repeated.ScannedCount.Should().Be(0);
        repeated.DeletedCount.Should().Be(0);
        (await db.ResourceTombstones.CountAsync(x => x.ResourceId == memoryId)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Worker_Should_Cancel_New_Hit_Link_Authority_Hold_And_Dependency_Evidence_And_Reset_Policy_Grace()
    {
        var owner = await CreateOwnerAsync();
        var projectId = NewProjectId("retention-revalidation");
        Guid hitId;
        Guid linkId;
        Guid authorityId;
        Guid legalHoldId;
        Guid securityHoldId;
        Guid dependencyId;
        Guid policyId;

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseActor(scope.ServiceProvider, owner.Actor);
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

            hitId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId);
            linkId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId);
            authorityId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId);
            legalHoldId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId);
            securityHoldId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId);
            dependencyId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId);
            policyId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId);

            var related = CreateMemory(owner.Actor, projectId, "related-memory");
            var successor = CreateMemory(owner.Actor, projectId, "authority-successor");
            db.MemoryItems.AddRange(related, successor);

            var hit = await db.MemoryItems.SingleAsync(x => x.Id == hitId);
            hit.Tags = [.. hit.Tags, "retention-hit-fixture"];
            var retrievalEvent = new RetrievalEvent
            {
                TenantId = owner.TenantId,
                OwnerUserId = owner.UserId,
                ProjectId = projectId,
                EntryPoint = "retention-worker-lifecycle-test",
                Success = true,
                ResultCount = 1,
                CreatedAt = DateTimeOffset.UtcNow
            };
            retrievalEvent.Hits.Add(new RetrievalHit
            {
                RetrievalEventId = retrievalEvent.Id,
                MemoryId = hitId,
                Rank = 1,
                ProjectId = projectId,
                CreatedAt = retrievalEvent.CreatedAt
            });
            db.RetrievalEvents.Add(retrievalEvent);

            db.MemoryLinks.Add(new MemoryLink
            {
                FromId = related.Id,
                ToId = linkId,
                LinkType = "references",
                CreatedAt = DateTimeOffset.UtcNow
            });

            var authority = await db.MemoryItems.SingleAsync(x => x.Id == authorityId);
            authority.AuthorityState = MemoryAuthorityState.Superseded;
            authority.SupersededById = successor.Id;
            authority.SuccessorEvidenceId = successor.Id;
            authority.SuccessorEvidenceRef = "retention-worker-authority-fixture";
            authority.ValidUntil = DateTimeOffset.UtcNow.AddMinutes(-1);
            authority.UpdatedAt = DateTimeOffset.UtcNow;
            successor.SupersedesId = authority.Id;

            var legalHold = await db.MemoryItems.SingleAsync(x => x.Id == legalHoldId);
            legalHold.Tags = [.. legalHold.Tags, "legal-hold"];
            var securityHold = await db.MemoryItems.SingleAsync(x => x.Id == securityHoldId);
            securityHold.Tags = [.. securityHold.Tags, "security-hold"];

            db.MemoryJobs.Add(new MemoryJob
            {
                TenantId = owner.TenantId,
                OwnerUserId = owner.UserId,
                ProjectId = projectId,
                JobType = MemoryJobType.Reindex,
                Status = MemoryJobStatus.Running,
                PayloadJson = $$"""{"memoryId":"{{dependencyId:D}}"}""",
                CreatedAt = DateTimeOffset.UtcNow,
                StartedAt = DateTimeOffset.UtcNow
            });

            var stalePolicyState = await db.MemoryRetentionStates.SingleAsync(x => x.ResourceId == policyId);
            stalePolicyState.PolicyVersion = "retention-policy-before-current";
            stalePolicyState.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-8);
            stalePolicyState.DeleteEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            stalePolicyState.LifecycleStatus = "Eligible";
            await db.SaveChangesAsync();
        }

        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var result = await executor.ExecuteNextBatchAsync(CancellationToken.None);

        result.ScannedCount.Should().Be(7);
        result.DeletedCount.Should().Be(0);
        result.CancelledCount.Should().Be(7);
        result.FailedCount.Should().Be(0);
        result.TombstoneIds.Should().BeEmpty();

        using var readScope = environment.GetFactory().Services.CreateScope();
        UseActor(readScope.ServiceProvider, owner.Actor);
        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var cancelledStates = await readDb.MemoryRetentionStates.AsNoTracking()
            .Where(x => x.ResourceId == hitId || x.ResourceId == linkId || x.ResourceId == authorityId ||
                        x.ResourceId == legalHoldId || x.ResourceId == securityHoldId || x.ResourceId == dependencyId)
            .ToArrayAsync();
        cancelledStates.Should().HaveCount(6);
        cancelledStates.Should().OnlyContain(x =>
            x.LifecycleStatus == "Cancelled" &&
            x.DeleteEligibleAt == null &&
            x.ClaimToken == string.Empty &&
            x.ClaimedAt == null &&
            x.ClaimLastError == "EligibilityCancelled");

        var authorityState = cancelledStates.Single(x => x.ResourceId == authorityId);
        authorityState.BlockedReasonsJson.Should().Contain("authorityChanged");
        var policyState = await readDb.MemoryRetentionStates.AsNoTracking().SingleAsync(x => x.ResourceId == policyId);
        policyState.LifecycleStatus.Should().Be("Eligible");
        policyState.PolicyVersion.Should().NotBe("retention-policy-before-current");
        policyState.DeleteEligibleAt.Should().BeAfter(DateTimeOffset.UtcNow);
        policyState.ClaimToken.Should().BeEmpty();
        policyState.ClaimedAt.Should().BeNull();
        policyState.ClaimLastError.Should().Be("EligibilityCancelled");
        (await readDb.ResourceTombstones.CountAsync(x => x.ProjectId == projectId)).Should().Be(0);
        (await readDb.MemoryItems.CountAsync(x => x.ProjectId == projectId)).Should().Be(9);
    }

    [DockerRequiredFact]
    public async Task Worker_Should_Claim_Only_Once_And_Recover_A_Missing_Receipt_After_Restart()
    {
        var owner = await CreateOwnerAsync();
        var projectId = NewProjectId("retention-race");
        Guid memoryId;

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseActor(scope.ServiceProvider, owner.Actor);
            memoryId = await CreateMaturedCandidateAsync(scope.ServiceProvider, owner, projectId);
        }

        var executor = environment.GetFactory().Services.GetRequiredService<IInternalMaturedDeleteExecutor>();
        var concurrentRuns = await Task.WhenAll(
            executor.ExecuteNextBatchAsync(CancellationToken.None),
            executor.ExecuteNextBatchAsync(CancellationToken.None));

        concurrentRuns.Sum(x => x.DeletedCount).Should().Be(1);
        concurrentRuns.Sum(x => x.TombstoneIds.Count).Should().Be(1);
        concurrentRuns.Sum(x => x.FailedCount).Should().Be(0);

        var recoveryRunId = $"internal-retention-recovery-{Guid.NewGuid():N}";
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            UseActor(scope.ServiceProvider, owner.Actor);
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.ResourceTombstones.Add(new ResourceTombstone
            {
                ResourceId = Guid.NewGuid(),
                TenantId = owner.TenantId,
                OwnerUserId = owner.UserId,
                ProjectId = projectId,
                ContentHash = "recovery-fixture-hash",
                Classification = "LowValueMachineEvidence",
                ArchivedAt = DateTimeOffset.UtcNow.AddDays(-8),
                DeletedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
                RetentionPolicyVersion = "retention-test-v1",
                GovernanceRunId = recoveryRunId,
                AuditId = Guid.NewGuid(),
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var recovery = await executor.ExecuteNextBatchAsync(CancellationToken.None);
        recovery.ScannedCount.Should().Be(0);
        recovery.StoppedReason.Should().Be("QueueEmpty");

        using var readScope = environment.GetFactory().Services.CreateScope();
        UseActor(readScope.ServiceProvider, owner.Actor);
        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        (await readDb.MemoryItems.AnyAsync(x => x.Id == memoryId)).Should().BeFalse();
        (await readDb.ResourceTombstones.CountAsync(x => x.ProjectId == projectId)).Should().Be(2);
        var recoveredReceipt = await readDb.GovernanceRunReceipts.SingleAsync(x =>
            x.TenantId == owner.TenantId &&
            x.OwnerUserId == owner.UserId &&
            x.GovernanceRunId == recoveryRunId &&
            x.EventType == "InternalRetentionCompleted");
        recoveredReceipt.Status.Should().Be("Completed");
        recoveredReceipt.AutoDeleted.Should().Be(1);
        recoveredReceipt.Tombstoned.Should().Be(1);
        recoveredReceipt.StoppedReason.Should().Be("RecoveredAfterRestart");
    }

    private async Task<TestOwner> CreateOwnerAsync()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var tenantId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        var slug = $"retention-{tenantId:N}";
        var username = $"retention-{userId:N}";
        db.Tenants.Add(new Tenant
        {
            Id = tenantId,
            Slug = slug,
            DisplayName = "Internal retention lifecycle test tenant",
            Status = TenantStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        db.TenantUsers.Add(new TenantUser
        {
            Id = userId,
            TenantId = tenantId,
            Username = username,
            DisplayName = "Internal retention lifecycle test owner",
            Role = TenantUserRole.Admin,
            Status = TenantUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        });
        await db.SaveChangesAsync();

        return new TestOwner(
            new ContextHubRequestActor(
                tenantId,
                userId,
                username,
                TenantUserRole.Admin,
                [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite],
                [],
                IsAuthenticated: true),
            tenantId,
            userId);
    }

    private static async Task<Guid> CreateMaturedCandidateAsync(
        IServiceProvider services,
        TestOwner owner,
        string projectId,
        bool includeContentGraph = false)
    {
        UseActor(services, owner.Actor);
        var db = services.GetRequiredService<MemoryDbContext>();
        var retention = services.GetRequiredService<IAutonomousRetentionService>();
        var memory = CreateMemory(owner.Actor, projectId, $"candidate-{Guid.NewGuid():N}");
        db.MemoryItems.Add(memory);
        if (includeContentGraph)
        {
            db.MemoryItemRevisions.Add(new MemoryItemRevision
            {
                MemoryItemId = memory.Id,
                Version = 1,
                Title = memory.Title,
                Content = memory.Content,
                Summary = memory.Summary,
                MetadataJson = memory.MetadataJson,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.MemoryItemChunks.Add(new MemoryItemChunk
            {
                MemoryItemId = memory.Id,
                ChunkKind = ChunkKind.Document,
                ChunkIndex = 0,
                ChunkText = memory.Content,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }

        await db.SaveChangesAsync();
        await retention.QuarantineAsync(memory.Id, projectId, $"lifecycle-quarantine-{Guid.NewGuid():N}", CancellationToken.None);
        var state = await db.MemoryRetentionStates.SingleAsync(x => x.ResourceId == memory.Id);
        state.LifecycleStatus.Should().Be("Quarantined");
        state.QuarantinedAt.Should().BeCloseTo(DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1));
        state.DeleteEligibleAt.Should().BeAfter(DateTimeOffset.UtcNow);
        state.QuarantinedAt = DateTimeOffset.UtcNow.AddDays(-8);
        state.DeleteEligibleAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        state.LifecycleStatus = "Eligible";
        state.ClaimToken = string.Empty;
        state.ClaimedAt = null;
        await db.SaveChangesAsync();
        return memory.Id;
    }

    private static MemoryItem CreateMemory(ContextHubRequestActor actor, string projectId, string suffix)
        => new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            ExternalKey = $"retention-worker:{suffix}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Episode,
            Title = $"Retention worker fixture {suffix}",
            Content = $"Disposable retention-worker content {suffix}",
            Summary = "Synthetic retention worker lifecycle fixture.",
            Tags = ["machine-generated", "execution-evidence", "synthetic-disposable"],
            SourceType = "tool-execution",
            SourceRef = "integration/internal-retention-worker",
            Importance = .1m,
            Confidence = .2m,
            Status = MemoryStatus.Active,
            MetadataJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow.AddDays(-90),
            UpdatedAt = DateTimeOffset.UtcNow.AddDays(-90)
        };

    private static void UseActor(IServiceProvider services, ContextHubRequestActor actor)
        => services.GetRequiredService<IRequestActorAccessor>().Current = actor;

    private static string NewProjectId(string prefix) => $"{prefix}-{Guid.NewGuid():N}";

    private sealed record TestOwner(ContextHubRequestActor Actor, Guid TenantId, Guid UserId);
}

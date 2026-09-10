using System.Data;
using System.Diagnostics;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

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
        blocked.GovernanceLastEvidenceChangedAt.Should().BeNull();

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
        unchanged.GovernanceLastEvidenceChangedAt.Should().BeNull();

        memory.MetadataJson = "{\"authority\":\"project-owner-confirmed\"}";
        memory.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(1);
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);

        var changed = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        changed.Status.Should().Be(GovernanceFindingStatus.HostBlocked);
        changed.GovernanceRetryCount.Should().Be(0);
        changed.GovernanceEvidenceChangedSinceBlock.Should().BeTrue();
        changed.GovernanceLastEvidenceChangedAt.Should().NotBeNull();
        var evidenceChangedAt = changed.GovernanceLastEvidenceChangedAt;

        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var changedReadBack = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        changedReadBack.Status.Should().Be(GovernanceFindingStatus.HostBlocked);
        changedReadBack.GovernanceRetryCount.Should().Be(0);
        changedReadBack.GovernanceEvidenceChangedSinceBlock.Should().BeTrue(
            "evidenceChangedSinceBlock is a latched signal until an audited manual reopen");
        changedReadBack.GovernanceLastEvidenceChangedAt.Should().Be(evidenceChangedAt,
            "unchanged evidence must not move the stable aging timestamp");

        memory.Importance = 1m;
        memory.UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(2);
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var disappeared = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        disappeared.Status.Should().Be(GovernanceFindingStatus.HostBlocked,
            "candidate disappearance is evidence, not authorization to clear an audited host block");
        disappeared.GovernanceRetryCount.Should().Be(0);

        var reopened = await governance.ReopenAsync(new GovernanceFindingReopenRequest(
            finding.Id,
            "OAuth was explicitly completed and controlled acceptance can resume.",
            $"manual-reopen-{Guid.NewGuid():N}"), CancellationToken.None);
        reopened.Status.Should().Be(GovernanceFindingStatus.Open);
        reopened.GovernanceRetryCount.Should().Be(1);
        reopened.GovernanceBlockedAt.Should().BeNull();
        reopened.GovernanceBlockingLayer.Should().BeEmpty();

        var exactReplay = await governance.ReopenAsync(new GovernanceFindingReopenRequest(
            finding.Id,
            "OAuth was explicitly completed and controlled acceptance can resume.",
            reopened.GovernanceRunId), CancellationToken.None);
        exactReplay.GovernanceRetryCount.Should().Be(1);

        var audit = await db.SecurityAuditEvents.AsNoTracking()
            .Where(x => x.EventType == SecurityAuditEventType.GovernanceFindingGovernanceUpdated)
            .OrderByDescending(x => x.CreatedAt)
            .FirstOrDefaultAsync();
        audit.Should().NotBeNull();
        audit!.Outcome.Should().Be("Open");
        audit.DetailsJson.Should().Contain("ManualReopen");
    }

    [DockerRequiredFact]
    public async Task Concurrent_Accept_Must_Fail_Closed_When_HostBlocked_Commits_First()
    {
        using var seedScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(seedScope.ServiceProvider);
        var seedDb = seedScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var governance = seedScope.ServiceProvider.GetRequiredService<IGovernanceService>();
        var projectId = $"finding-accept-race-{Guid.NewGuid():N}";
        var memory = CreateLowValueMemory(actor, projectId);
        seedDb.MemoryItems.Add(memory);
        await seedDb.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var finding = await seedDb.GovernanceFindings.SingleAsync(x =>
            x.ProjectId == projectId && x.PrimaryMemoryId == memory.Id &&
            x.Type == GovernanceFindingType.LowValueMemoryCandidate);

        using var blockerScope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(blockerScope.ServiceProvider);
        var blockerDb = blockerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        var blocked = await blockerDb.GovernanceFindings.SingleAsync(x => x.Id == finding.Id);
        var blockedAt = DateTimeOffset.UtcNow;
        blocked.Status = GovernanceFindingStatus.HostBlocked;
        blocked.GovernanceReason = "The host capability is unavailable.";
        blocked.GovernanceRunId = $"accept-race-block-{Guid.NewGuid():N}";
        blocked.GovernanceActor = "contract-test-admin";
        blocked.GovernanceUpdatedAt = blockedAt;
        blocked.GovernanceBlockedAt = blockedAt;
        blocked.GovernanceBlockingLayer = "Host";
        blocked.GovernanceReasonClass = "HostCapabilityUnavailable";
        blocked.UpdatedAt = blockedAt;
        blockerDb.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            TenantId = blocked.TenantId!.Value,
            ActorUserId = blocked.OwnerUserId!.Value,
            EventType = SecurityAuditEventType.GovernanceFindingGovernanceUpdated,
            Outcome = GovernanceFindingStatus.HostBlocked.ToString(),
            DetailsJson = "{\"action\":\"HostBlocked\"}",
            CreatedAt = blockedAt
        });
        await blockerDb.SaveChangesAsync();
        var acceptException = await CommitAfterWriterBlockedAsync(
            blockerDb,
            blockerTransaction,
            "governance_findings",
            (services, cancellationToken) => services.GetRequiredService<IGovernanceService>()
                .AcceptAsync(finding.Id, cancellationToken));
        acceptException.Should().NotBeNull(
            "a stale accept must fail closed after the HostBlocked write commits");

        seedDb.ChangeTracker.Clear();
        var readBack = await seedDb.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        readBack.Status.Should().Be(GovernanceFindingStatus.HostBlocked);
        readBack.GovernanceReasonClass.Should().Be("HostCapabilityUnavailable");
    }

    [DockerRequiredFact]
    public async Task Concurrent_Analyze_EvidenceReopen_Must_Not_Overwrite_HostBlocked_Finding()
    {
        using var seedScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(seedScope.ServiceProvider);
        var seedDb = seedScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var governance = seedScope.ServiceProvider.GetRequiredService<IGovernanceService>();
        var projectId = $"finding-reopen-race-{Guid.NewGuid():N}";
        var memory = CreateLowValueMemory(actor, projectId);
        seedDb.MemoryItems.Add(memory);
        await seedDb.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var finding = await seedDb.GovernanceFindings.SingleAsync(x =>
            x.ProjectId == projectId && x.PrimaryMemoryId == memory.Id &&
            x.Type == GovernanceFindingType.LowValueMemoryCandidate);
        await governance.SetDispositionAsync(new GovernanceFindingDispositionRequest(
            finding.Id,
            GovernanceFindingDisposition.Deferred,
            "Wait for new evidence.",
            $"finding-reopen-race-deferred-{Guid.NewGuid():N}"),
            CancellationToken.None);

        memory.MetadataJson = "{\"evidence\":\"changed-before-reopen\"}";
        memory.UpdatedAt = DateTimeOffset.UtcNow;
        await seedDb.SaveChangesAsync();

        using var blockerScope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(blockerScope.ServiceProvider);
        var blockerDb = blockerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        var blocked = await blockerDb.GovernanceFindings.SingleAsync(x => x.Id == finding.Id);
        var blockedAt = DateTimeOffset.UtcNow;
        blocked.Status = GovernanceFindingStatus.HostBlocked;
        blocked.GovernanceReason = "The host cannot evaluate the changed evidence.";
        blocked.GovernanceRunId = $"finding-reopen-race-block-{Guid.NewGuid():N}";
        blocked.GovernanceActor = "contract-test-admin";
        blocked.GovernanceUpdatedAt = blockedAt;
        blocked.GovernanceBlockedAt = blockedAt;
        blocked.GovernanceBlockingLayer = "Host";
        blocked.GovernanceReasonClass = "HostCapabilityUnavailable";
        blocked.UpdatedAt = blockedAt;
        blockerDb.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            TenantId = blocked.TenantId!.Value,
            ActorUserId = blocked.OwnerUserId!.Value,
            EventType = SecurityAuditEventType.GovernanceFindingGovernanceUpdated,
            Outcome = GovernanceFindingStatus.HostBlocked.ToString(),
            DetailsJson = "{\"action\":\"HostBlocked\"}",
            CreatedAt = blockedAt
        });
        await blockerDb.SaveChangesAsync();
        var analyzeException = await CommitAfterWriterBlockedAsync(
            blockerDb,
            blockerTransaction,
            "governance_findings",
            (services, cancellationToken) => services.GetRequiredService<IGovernanceService>()
                .AnalyzeAsync(projectId, cancellationToken));
        analyzeException.Should().NotBeNull(
            "a stale automatic reopen must fail closed after the HostBlocked write commits");

        seedDb.ChangeTracker.Clear();
        var readBack = await seedDb.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        readBack.Status.Should().Be(GovernanceFindingStatus.HostBlocked);
        readBack.GovernanceReasonClass.Should().Be("HostCapabilityUnavailable");
    }

    [DockerRequiredFact]
    public async Task Concurrent_FullGovernance_Reopen_Must_Not_Overwrite_HostBlocked_Insight()
    {
        using var seedScope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(seedScope.ServiceProvider);
        var seedDb = seedScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var projectId = $"insight-reopen-race-{Guid.NewGuid():N}";
        var insight = await SeedGovernanceInsightAsync(seedDb, actor, projectId);
        var fullGovernance = seedScope.ServiceProvider.GetRequiredService<IFullGovernancePlanService>();
        var baselineRunId = $"insight-reopen-race-baseline-{Guid.NewGuid():N}";
        await fullGovernance.BuildAsync(
            [projectId], baselineRunId, CreateEmptyGovernanceSnapshot(), CancellationToken.None);

        insight.Summary = "Changed evidence must not clear a host block.";
        insight.UpdatedAt = DateTimeOffset.UtcNow;
        await seedDb.SaveChangesAsync();

        using var blockerScope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(blockerScope.ServiceProvider);
        var blockerDb = blockerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await using var blockerTransaction = await blockerDb.Database.BeginTransactionAsync(
            IsolationLevel.Serializable,
            CancellationToken.None);
        var blocked = await blockerDb.ConversationInsights.SingleAsync(x => x.Id == insight.Id);
        var blockedAt = DateTimeOffset.UtcNow;
        blocked.PromotionStatus = ConversationPromotionStatus.HostBlocked;
        blocked.GovernanceReason = "The host cannot evaluate the changed insight evidence.";
        blocked.GovernanceRunId = $"insight-reopen-race-block-{Guid.NewGuid():N}";
        blocked.GovernanceUpdatedAt = blockedAt;
        blocked.GovernanceBlockedAt = blockedAt;
        blocked.GovernanceBlockingLayer = "Host";
        blocked.GovernanceReasonClass = "HostCapabilityUnavailable";
        blocked.UpdatedAt = blockedAt;
        blockerDb.SecurityAuditEvents.Add(new SecurityAuditEvent
        {
            TenantId = blocked.TenantId,
            ActorUserId = blocked.OwnerUserId,
            EventType = SecurityAuditEventType.ConversationInsightGovernanceUpdated,
            Outcome = ConversationPromotionStatus.HostBlocked.ToString(),
            DetailsJson = "{\"action\":\"HostBlocked\"}",
            CreatedAt = blockedAt
        });
        await blockerDb.SaveChangesAsync();
        var buildException = await CommitAfterWriterBlockedAsync(
            blockerDb,
            blockerTransaction,
            "conversation_insights",
            (services, cancellationToken) => services.GetRequiredService<IFullGovernancePlanService>()
                .BuildAsync(
                    [projectId], $"insight-reopen-race-{Guid.NewGuid():N}",
                    CreateEmptyGovernanceSnapshot(), cancellationToken));
        buildException.Should().NotBeNull(
            "a stale automatic insight reopen must fail closed after the HostBlocked write commits");

        seedDb.ChangeTracker.Clear();
        var readBack = await seedDb.ConversationInsights.AsNoTracking().SingleAsync(x => x.Id == insight.Id);
        readBack.PromotionStatus.Should().Be(ConversationPromotionStatus.HostBlocked);
        readBack.GovernanceReasonClass.Should().Be("HostCapabilityUnavailable");
    }

    [DockerRequiredFact]
    public async Task Deferred_Finding_Should_Reopen_For_Link_Hit_And_Retention_Evidence_Changes()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var governance = scope.ServiceProvider.GetRequiredService<IGovernanceService>();
        var projectId = $"evidence-driven-reevaluate-{Guid.NewGuid():N}";
        var memory = CreateLowValueMemory(actor, projectId);
        var related = CreateLowValueMemory(actor, projectId);
        memory.ExternalKey = $"evidence-primary:{memory.Id:N}";
        related.ExternalKey = $"evidence-related:{related.Id:N}";
        related.Title = "Related evidence";
        db.MemoryItems.AddRange(memory, related);
        await db.SaveChangesAsync();

        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var finding = await db.GovernanceFindings.SingleAsync(x =>
            x.ProjectId == projectId && x.PrimaryMemoryId == memory.Id &&
            x.Type == GovernanceFindingType.LowValueMemoryCandidate);

        async Task DeferAsync(string reason)
        {
            await governance.SetDispositionAsync(new GovernanceFindingDispositionRequest(
                finding.Id,
                GovernanceFindingDisposition.Deferred,
                reason,
                $"semantic-defer-{Guid.NewGuid():N}"), CancellationToken.None);
        }

        await DeferAsync("Wait for relationship evidence.");
        finding.GovernanceEvidenceFingerprint = string.Empty;
        finding.GovernancePolicyVersion = string.Empty;
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var backfilled = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        backfilled.Status.Should().Be(GovernanceFindingStatus.Deferred);
        backfilled.GovernanceRetryCount.Should().Be(0);
        backfilled.GovernanceEvidenceFingerprint.Should().NotBeEmpty();
        backfilled.GovernanceLastEvidenceChangedAt.Should().BeNull();

        db.MemoryLinks.Add(new MemoryLink
        {
            FromId = related.Id,
            ToId = memory.Id,
            LinkType = "supports",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);

        var linkReopened = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        linkReopened.Status.Should().Be(GovernanceFindingStatus.Open);
        linkReopened.GovernanceRetryCount.Should().Be(1);
        linkReopened.GovernanceLastEvidenceChangedAt.Should().NotBeNull();

        await DeferAsync("Wait for retrieval evidence.");
        var now = DateTimeOffset.UtcNow;
        var retrievalEvent = new RetrievalEvent
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            EntryPoint = "semantic-reevaluate-test",
            Success = true,
            ResultCount = 1,
            CreatedAt = now
        };
        retrievalEvent.Hits.Add(new RetrievalHit
        {
            RetrievalEventId = retrievalEvent.Id,
            MemoryId = memory.Id,
            Rank = 1,
            ProjectId = projectId,
            CreatedAt = now
        });
        db.RetrievalEvents.Add(retrievalEvent);
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);

        var hitReopened = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        hitReopened.Status.Should().Be(GovernanceFindingStatus.Open);
        hitReopened.GovernanceRetryCount.Should().Be(2);

        await DeferAsync("Wait for retention-policy evidence.");
        db.MemoryRetentionStates.Add(new MemoryRetentionState
        {
            ResourceId = memory.Id,
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            ProjectId = projectId,
            Classification = "LowSignal",
            PolicyKind = "semantic-test",
            PolicyVersion = "semantic-test-v1",
            LifecycleStatus = "Candidate",
            EvidenceFingerprint = "retention-evidence-v1",
            CreatedAt = now,
            UpdatedAt = now.AddMinutes(1)
        });
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);

        var retentionReopened = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        retentionReopened.Status.Should().Be(GovernanceFindingStatus.Open);
        retentionReopened.GovernanceRetryCount.Should().Be(3);
        retentionReopened.GovernanceReason.Should().Contain("evidence or policy changed");

        await DeferAsync("Wait for authority-chain evidence.");
        memory.AuthorityState = MemoryAuthorityState.Superseded;
        memory.SupersededById = related.Id;
        related.SupersedesId = memory.Id;
        memory.ValidUntil = now.AddMinutes(2);
        memory.SuccessorEvidenceId = related.Id;
        memory.SuccessorEvidenceRef = "authority-evidence-test";
        memory.UpdatedAt = now.AddMinutes(2);
        await db.SaveChangesAsync();
        await governance.AnalyzeAsync(projectId, CancellationToken.None);

        var authorityReopened = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        authorityReopened.Status.Should().Be(GovernanceFindingStatus.Open);
        authorityReopened.GovernanceRetryCount.Should().Be(4);
        authorityReopened.GovernanceReason.Should().Contain("evidence or policy changed");
    }

    [DockerRequiredFact]
    public async Task Invalid_successor_evidence_should_not_unlock_requires_decision_or_host_blocked_findings()
    {
        var cases = new[]
        {
            (Name: "future", Reason: "successor-not-effective", Mutate: (Action<MemoryItem>)(successor =>
                successor.ValidFrom = DateTimeOffset.UtcNow.AddHours(1))),
            (Name: "expired", Reason: "successor-not-effective", Mutate: (Action<MemoryItem>)(successor =>
                successor.ValidUntil = DateTimeOffset.UtcNow.AddSeconds(-1))),
            (Name: "missing", Reason: "successor-evidence-missing", Mutate: (Action<MemoryItem>)(successor =>
                successor.SuccessorEvidenceId = null))
        };

        foreach (var testCase in cases)
        {
            using var scope = environment.GetFactory().Services.CreateScope();
            var actor = UseBootstrapActor(scope.ServiceProvider);
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var governance = scope.ServiceProvider.GetRequiredService<IGovernanceService>();
            var projectId = $"invalid-successor-{testCase.Name}-{Guid.NewGuid():N}";
            var now = DateTimeOffset.UtcNow;
            var predecessor = CreateLowValueMemory(actor, projectId);
            predecessor.Tags = ["superseded"];
            predecessor.AuthorityState = MemoryAuthorityState.Superseded;
            predecessor.ValidFrom = now.AddMinutes(-2);
            var successor = CreateAuthorityMemory(actor, projectId, "Current authority", now.AddMinutes(-1));
            var evidence = CreateAuthorityMemory(
                actor,
                projectId,
                "Historical successor evidence",
                now.AddMinutes(-1),
                MemoryStatus.Archived,
                MemoryAuthorityState.Historical);
            db.MemoryItems.AddRange(predecessor, successor, evidence);
            await db.SaveChangesAsync();

            predecessor.SupersededById = successor.Id;
            successor.SupersedesId = predecessor.Id;
            successor.ValidFrom = now.AddMinutes(-1);
            successor.ValidUntil = now.AddDays(1);
            successor.SuccessorEvidenceId = evidence.Id;
            testCase.Mutate(successor);
            await db.SaveChangesAsync();

            await governance.AnalyzeAsync(projectId, CancellationToken.None);
            var evidenceFinding = await db.GovernanceFindings.SingleAsync(x =>
                x.ProjectId == projectId &&
                x.PrimaryMemoryId == predecessor.Id &&
                x.Type == GovernanceFindingType.SupersededMemoryCandidate);
            evidenceFinding.DetailsJson.Should().Contain(testCase.Reason);
            var finding = await db.GovernanceFindings.SingleAsync(x =>
                x.ProjectId == projectId &&
                x.PrimaryMemoryId == predecessor.Id &&
                x.Type == GovernanceFindingType.LowValueMemoryCandidate);

            var decisionRequired = await governance.SetDispositionAsync(new GovernanceFindingDispositionRequest(
                finding.Id,
                GovernanceFindingDisposition.RequiresUserDecision,
                $"Invalid {testCase.Name} successor evidence remains unresolved.",
                $"invalid-successor-deferred-{Guid.NewGuid():N}"), CancellationToken.None);
            decisionRequired.Status.Should().Be(GovernanceFindingStatus.RequiresUserDecision);

            await governance.AnalyzeAsync(projectId, CancellationToken.None);
            var decisionBlocked = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
            decisionBlocked.Status.Should().Be(GovernanceFindingStatus.RequiresUserDecision,
                $"{testCase.Name} evidence must not resolve a user-gated finding");
            decisionBlocked.GovernanceRetryCount.Should().Be(0);
            decisionBlocked.GovernanceEvidenceChangedSinceBlock.Should().BeFalse();
            decisionBlocked.GovernanceLastEvidenceChangedAt.Should().BeNull();

            var hostBlocked = await governance.SetDispositionAsync(new GovernanceFindingDispositionRequest(
                finding.Id,
                GovernanceFindingDisposition.HostBlocked,
                $"Host cannot validate {testCase.Name} successor evidence.",
                $"invalid-successor-host-{Guid.NewGuid():N}",
                BlockingLayer: "ChatGptAppOAuth",
                ReasonClass: "UserActionRequired",
                RelatedTool: "scheduled_governance_review"), CancellationToken.None);
            hostBlocked.Status.Should().Be(GovernanceFindingStatus.HostBlocked);

            await governance.AnalyzeAsync(projectId, CancellationToken.None);
            var hostStillBlocked = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
            hostStillBlocked.Status.Should().Be(GovernanceFindingStatus.HostBlocked,
                $"{testCase.Name} evidence must not clear a host block");
            hostStillBlocked.GovernanceRetryCount.Should().Be(0);
            hostStillBlocked.GovernanceEvidenceChangedSinceBlock.Should().BeFalse();
            hostStillBlocked.GovernanceLastEvidenceChangedAt.Should().BeNull();
            hostStillBlocked.GovernanceBlockingLayer.Should().Be("ChatGptAppOAuth");
        }
    }

    [DockerRequiredFact]
    public async Task Exception_aging_without_new_evidence_should_not_reopen_or_retry()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var governance = scope.ServiceProvider.GetRequiredService<IGovernanceService>();
        var projectId = $"exception-aging-{Guid.NewGuid():N}";
        var memory = CreateLowValueMemory(actor, projectId);
        db.MemoryItems.Add(memory);
        await db.SaveChangesAsync();

        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var finding = await db.GovernanceFindings.SingleAsync(x =>
            x.ProjectId == projectId && x.PrimaryMemoryId == memory.Id &&
            x.Type == GovernanceFindingType.LowValueMemoryCandidate);
        await governance.SetDispositionAsync(new GovernanceFindingDispositionRequest(
            finding.Id,
            GovernanceFindingDisposition.Deferred,
            "Keep deferred while evidence ages.",
            $"exception-aging-{Guid.NewGuid():N}"), CancellationToken.None);

        var agedAt = DateTimeOffset.UtcNow.AddDays(-31);
        var persisted = await db.GovernanceFindings.SingleAsync(x => x.Id == finding.Id);
        var baselineFingerprint = persisted.GovernanceEvidenceFingerprint;
        persisted.GovernanceRetryCount = 7;
        persisted.GovernanceLastReevaluatedAt = agedAt;
        persisted.GovernanceLastEvidenceChangedAt = agedAt;
        await db.SaveChangesAsync();

        var beforeAnalyze = DateTimeOffset.UtcNow;
        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var firstReadBack = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        firstReadBack.Status.Should().Be(GovernanceFindingStatus.Deferred,
            "elapsed age alone is not new authorization evidence");
        firstReadBack.GovernanceRetryCount.Should().Be(7,
            "reevaluation without changed evidence must not create a retry");
        firstReadBack.GovernanceEvidenceFingerprint.Should().Be(baselineFingerprint);
        firstReadBack.GovernanceLastEvidenceChangedAt.Should().BeCloseTo(agedAt, TimeSpan.FromMilliseconds(1));
        firstReadBack.GovernanceLastReevaluatedAt.Should().BeAfter(beforeAnalyze);

        await governance.AnalyzeAsync(projectId, CancellationToken.None);
        var secondReadBack = await db.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        secondReadBack.Status.Should().Be(GovernanceFindingStatus.Deferred);
        secondReadBack.GovernanceRetryCount.Should().Be(7,
            "repeated reevaluation without changed evidence must not form a retry storm");
        secondReadBack.GovernanceLastEvidenceChangedAt.Should().BeCloseTo(agedAt, TimeSpan.FromMilliseconds(1));
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
    public async Task Review_Receipt_Should_Distinguish_Handler_Entry_From_Terminal_Failure_Idempotently()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"review-recovery-{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.UtcNow;
        var identity = new GovernanceReceiptContractIdentity(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);

        await receipts.RecordReviewStartedAsync(runId, startedAt, identity, CancellationToken.None);
        await receipts.RecordReviewStartedAsync(runId, startedAt, identity, CancellationToken.None);

        var running = await receipts.GetAsync(runId, CancellationToken.None);
        running.Should().NotBeNull();
        running!.RunExists.Should().BeTrue();
        running.Status.Should().Be("Running");
        running.StoppedReason.Should().Be("ReviewReceived");

        await receipts.RecordReviewStoppedAsync(
            runId,
            startedAt,
            "Failed",
            nameof(InvalidOperationException),
            "KnowledgeReview",
            identity,
            CancellationToken.None);

        var failed = await receipts.GetAsync(runId, CancellationToken.None);
        failed.Should().NotBeNull();
        failed!.Status.Should().Be("Failed");
        failed.StoppedReason.Should().Be(nameof(InvalidOperationException));
        failed.ToolContractVersion.Should().Be(identity.ToolContractVersion);

        var terminalEntity = await db.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.GovernanceRunId == runId)
            .OrderByDescending(x => x.EventSequence)
            .FirstAsync();
        terminalEntity.FailurePhase.Should().Be("KnowledgeReview");

        (await db.GovernanceRunReceipts.CountAsync(x =>
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.UserId &&
            x.GovernanceRunId == runId)).Should().Be(2);
    }

    [DockerRequiredFact]
    public async Task Review_Stopped_Receipt_Should_Distinguish_Initial_Review_From_ReReview_Idempotently()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"review-stopped-phase-{Guid.NewGuid():N}";
        var startedAt = DateTimeOffset.UtcNow;
        var identity = new GovernanceReceiptContractIdentity(
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion);

        foreach (var isReReview in new[] { false, true })
        {
            await receipts.RecordReviewStoppedAsync(
                runId,
                startedAt,
                "Failed",
                nameof(InvalidOperationException),
                "KnowledgeReview",
                identity,
                CancellationToken.None,
                isReReview);
            await receipts.RecordReviewStoppedAsync(
                runId,
                startedAt,
                "Failed",
                nameof(InvalidOperationException),
                "KnowledgeReview",
                identity,
                CancellationToken.None,
                isReReview);
        }

        var stoppedEvents = await db.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId &&
                        x.OwnerUserId == actor.UserId &&
                        x.GovernanceRunId == runId &&
                        x.EventType == "ReviewStopped")
            .OrderBy(x => x.EventSequence)
            .ToArrayAsync();

        stoppedEvents.Should().HaveCount(2);
        stoppedEvents.Select(x => x.EventKey).Should().OnlyHaveUniqueItems();
        stoppedEvents.Select(x => x.RequestIdentityHash).Should().OnlyHaveUniqueItems();
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

    private async Task<Exception?> CommitAfterWriterBlockedAsync(
        MemoryDbContext blockerDb,
        IDbContextTransaction blockerTransaction,
        string relationName,
        Func<IServiceProvider, CancellationToken, Task> operation)
    {
        var blockerPid = await ReadBackendPidAsync(blockerDb);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var writerPidSource = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = Task.Run(async () =>
        {
            try
            {
                using var workerScope = environment.GetFactory().Services.CreateScope();
                UseBootstrapActor(workerScope.ServiceProvider);
                var workerDb = workerScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                await workerDb.Database.OpenConnectionAsync(timeout.Token);
                writerPidSource.TrySetResult(await ReadBackendPidAsync(workerDb, timeout.Token));
                await operation(workerScope.ServiceProvider, timeout.Token);
            }
            catch (Exception exception)
            {
                writerPidSource.TrySetException(exception);
                throw;
            }
        });

        try
        {
            var writerPid = await writerPidSource.Task.WaitAsync(timeout.Token);
            var blocked = WaitForBackendBlockedByAsync(
                blockerPid,
                writerPid,
                relationName,
                timeout.Token);
            if (await Task.WhenAny(blocked, worker) == worker)
            {
                await worker;
                throw new InvalidOperationException(
                    $"Writer PID {writerPid} completed before PostgreSQL reported the expected '{relationName}' lock wait.");
            }

            await blocked;
            await blockerTransaction.CommitAsync(timeout.Token);
            return await Record.ExceptionAsync(() => worker);
        }
        catch
        {
            timeout.Cancel();
            try
            {
                await blockerTransaction.RollbackAsync(CancellationToken.None);
            }
            catch
            {
            }

            await Record.ExceptionAsync(() => worker);
            throw;
        }
    }

    private static async Task<int> ReadBackendPidAsync(
        MemoryDbContext db,
        CancellationToken cancellationToken = default)
    {
        await using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT pg_backend_pid();";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private async Task WaitForBackendBlockedByAsync(
        int blockerPid,
        int writerPid,
        string relationName,
        CancellationToken cancellationToken)
    {
        var connectionString = environment.PostgresConnectionString ??
                               throw new InvalidOperationException("PostgreSQL test connection is unavailable.");
        var deadline = Stopwatch.GetTimestamp() + 10 * Stopwatch.Frequency;
        await using var observer = new NpgsqlConnection(connectionString);
        await observer.OpenAsync(cancellationToken);
        while (Stopwatch.GetTimestamp() < deadline)
        {
            await using var command = new NpgsqlCommand(
                """
                SELECT @blocker_pid = ANY(pg_blocking_pids(@writer_pid));
                """,
                observer);
            command.Parameters.AddWithValue("blocker_pid", blockerPid);
            command.Parameters.AddWithValue("writer_pid", writerPid);
            if ((bool)(await command.ExecuteScalarAsync(cancellationToken) ?? false))
            {
                return;
            }

            await Task.Delay(25, cancellationToken);
        }

        throw new TimeoutException(
            $"PostgreSQL did not report writer PID {writerPid} on '{relationName}' blocked by backend PID {blockerPid}.");
    }

    private static DurableMemoryGovernanceSnapshotResult CreateEmptyGovernanceSnapshot()
    {
        var coverage = new KnowledgeGovernanceCoverageResult(
            Guid.NewGuid(),
            $"snapshot-{Guid.NewGuid():N}",
            DateTimeOffset.UtcNow,
            0,
            0,
            0,
            0,
            0,
            0,
            CoverageComplete: true,
            HasMore: false,
            Continuation: null)
        {
            AuthorizedGovernanceDurableMemoryCount = 0,
            GovernanceCoveredDurableMemoryCount = 0,
            GovernanceProjectIds = [ProjectContext.SharedProjectId]
        };
        return new DurableMemoryGovernanceSnapshotResult(coverage, [], []);
    }

    private static async Task<ConversationInsight> SeedGovernanceInsightAsync(
        MemoryDbContext db,
        ContextHubRequestActor actor,
        string projectId)
    {
        var now = DateTimeOffset.UtcNow;
        var conversationId = $"governance-race-conversation-{Guid.NewGuid():N}";
        var sourceSystem = $"governance-race-{Guid.NewGuid():N}";
        var session = new ConversationSession
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ConversationId = conversationId,
            ProjectId = projectId,
            ProjectName = "Governance race test",
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
            SourceRef = $"test://{conversationId}",
            UserMessageSummary = "Governance race test",
            AgentMessageSummary = "Governance race test",
            SessionSummary = "Governance race test",
            ShortExcerpt = "Governance race test",
            DedupKey = $"governance-race-checkpoint:{Guid.NewGuid():N}",
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
            Title = "Governance race test insight",
            Content = "This insight exercises automatic evidence re-evaluation concurrency.",
            Summary = "Initial governance race evidence.",
            SourceRef = checkpoint.SourceRef,
            Tags = ["governance-race-test"],
            Importance = .5m,
            Confidence = .5m,
            DedupKey = $"governance-race-insight:{Guid.NewGuid():N}",
            PromotionStatus = ConversationPromotionStatus.Deferred,
            GovernanceReason = "Seeded exception for governance race test.",
            GovernanceRunId = "governance-race-seed",
            MetadataJson = "{}",
            CreatedAt = now,
            UpdatedAt = now
        };
        db.AddRange(session, checkpoint, insight);
        await db.SaveChangesAsync(CancellationToken.None);
        return insight;
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

    private static MemoryItem CreateAuthorityMemory(
        ContextHubRequestActor actor,
        string projectId,
        string title,
        DateTimeOffset timestamp,
        MemoryStatus status = MemoryStatus.Active,
        MemoryAuthorityState authorityState = MemoryAuthorityState.Current)
        => new()
        {
            TenantId = actor.TenantId,
            OwnerUserId = actor.UserId,
            ProjectId = projectId,
            ExternalKey = $"authority:{projectId}:{Guid.NewGuid():N}",
            MemoryType = MemoryType.Fact,
            Scope = MemoryScope.Project,
            Status = status,
            AuthorityState = authorityState,
            Title = title,
            Content = title,
            Summary = title,
            SourceType = "test",
            SourceRef = $"test://authority/{projectId}",
            Tags = [],
            Importance = .9m,
            Confidence = .95m,
            MetadataJson = "{}",
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
}

using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class SuccessorAwareStaleBlockerIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Strong_same_scope_successor_is_reversibly_archived_and_replay_is_idempotent()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite,
                SecurityScopes.LogsRead,
                SecurityScopes.GovernanceTrackerManage
            ],
            [],
            IsAuthenticated: true);

        var projectId = $"successor-executor-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var predecessor = CreateMemory(user, projectId, "resolved-blocker", now.AddMinutes(-3));
        var successor = CreateMemory(user, projectId, "current-authority", now.AddMinutes(-2));
        var evidence = CreateMemory(user, projectId, "successor-evidence", now.AddMinutes(-1));
        evidence.Status = MemoryStatus.Archived;
        evidence.AuthorityState = MemoryAuthorityState.Historical;
        db.MemoryItems.AddRange(predecessor, successor, evidence);
        await db.SaveChangesAsync();

        successor.SupersedesId = predecessor.Id;
        successor.SuccessorEvidenceId = evidence.Id;
        predecessor.AuthorityState = MemoryAuthorityState.Superseded;
        predecessor.SupersededById = successor.Id;
        predecessor.ValidUntil = now;
        await db.SaveChangesAsync();

        var runId = $"successor-executor-{Guid.NewGuid():N}";
        var review = await scope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>()
            .ReviewAsync(new KnowledgeReviewRequest(
                [projectId],
                LimitPerSection: 200,
                GovernanceRunId: runId), CancellationToken.None);
        review.GovernancePlan.Should().Contain(x =>
            x.AuthorityResourceId == predecessor.Id &&
            x.Classification == GovernanceFindingType.SupersededMemoryCandidate.ToString() &&
            x.RecommendedAction == GovernanceBatchActionType.Archive.ToString());

        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 100,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.Archive],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Interactive);
        var executor = scope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>();
        var result = await executor.ExecuteAsync(request, CancellationToken.None);

        result.AppliedCount.Should().BeGreaterThan(0, result.StoppedReason);
        result.Items.Should().Contain(x =>
            x.ResourceIds.Contains(predecessor.Id) &&
            x.Disposition == GovernanceBatchItemDisposition.Applied &&
            x.ActionType == GovernanceBatchActionType.Archive);
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == predecessor.Id)).Status
            .Should().Be(MemoryStatus.Archived);
        (await db.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == successor.Id)).Status
            .Should().Be(MemoryStatus.Active);

        var replay = await executor.ExecuteAsync(request, CancellationToken.None);
        replay.IsReplay.Should().BeTrue();
        (await db.MemoryItems.AsNoTracking().CountAsync(x =>
            x.Id == predecessor.Id && x.Status == MemoryStatus.Archived)).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Concurrent_HostBlocked_Finding_Must_Roll_Back_Archive_And_Proposal()
    {
        using var seedScope = environment.GetFactory().Services.CreateScope();
        var seedDb = seedScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var user = await seedDb.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        SetActor(seedScope.ServiceProvider, user);

        var projectId = $"successor-race-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var predecessor = CreateMemory(user, projectId, "race-predecessor", now.AddMinutes(-3));
        var successor = CreateMemory(user, projectId, "race-successor", now.AddMinutes(-2));
        var evidence = CreateMemory(user, projectId, "race-evidence", now.AddMinutes(-1));
        evidence.Status = MemoryStatus.Archived;
        evidence.AuthorityState = MemoryAuthorityState.Historical;
        seedDb.MemoryItems.AddRange(predecessor, successor, evidence);
        await seedDb.SaveChangesAsync();
        successor.SupersedesId = predecessor.Id;
        successor.SuccessorEvidenceId = evidence.Id;
        predecessor.AuthorityState = MemoryAuthorityState.Superseded;
        predecessor.SupersededById = successor.Id;
        predecessor.ValidUntil = now;
        await seedDb.SaveChangesAsync();

        var runId = $"successor-race-{Guid.NewGuid():N}";
        var review = await seedScope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>()
            .ReviewAsync(
                new KnowledgeReviewRequest([projectId], LimitPerSection: 200, GovernanceRunId: runId),
                CancellationToken.None);
        var finding = await seedDb.GovernanceFindings.SingleAsync(x =>
            x.ProjectId == projectId &&
            x.PrimaryMemoryId == predecessor.Id &&
            x.Type == GovernanceFindingType.SupersededMemoryCandidate);
        var proposalCountBefore = await seedDb.ConversationInsights.CountAsync(x =>
            x.SourceSystem == ChatGptProposalService.SourceSystem && x.ProjectId == projectId);
        var request = new GovernanceBatchExecuteRequest(
            runId,
            [projectId],
            review.DurableMemoryCoverage!.SnapshotToken,
            MaxMutations: 100,
            MaxDurationSeconds: 60,
            AllowedActionTypes: [GovernanceBatchActionType.Archive],
            MaxRiskLevel: GovernanceBatchRiskLevel.Low,
            DryRun: false,
            AllowHardDelete: false,
            ExecutionMode: GovernanceBatchExecutionMode.Interactive);

        using var resourceLockScope = environment.GetFactory().Services.CreateScope();
        SetActor(resourceLockScope.ServiceProvider, user);
        var resourceLockDb = resourceLockScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await using var resourceLockTransaction = await resourceLockDb.Database.BeginTransactionAsync();
        _ = await resourceLockDb.MemoryItems
            .FromSqlInterpolated($"SELECT * FROM memory_items WHERE id = {predecessor.Id} FOR UPDATE")
            .SingleAsync();

        var executionEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var executionTask = Task.Run(async () =>
        {
            using var executionScope = environment.GetFactory().Services.CreateScope();
            SetActor(executionScope.ServiceProvider, user);
            executionEntered.SetResult();
            return await executionScope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>()
                .ExecuteAsync(request, CancellationToken.None);
        });

        await executionEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await WaitForWriterBlockedByResourceLockAsync(resourceLockDb);
        executionTask.IsCompleted.Should().BeFalse("the archive must wait at the locked resource after reading the open finding");

        using (var blockerScope = environment.GetFactory().Services.CreateScope())
        {
            SetActor(blockerScope.ServiceProvider, user);
            await blockerScope.ServiceProvider.GetRequiredService<IGovernanceService>()
                .SetDispositionAsync(
                    new GovernanceFindingDispositionRequest(
                        finding.Id,
                        GovernanceFindingDisposition.HostBlocked,
                        "The host cannot authorize this archive.",
                        $"successor-race-block-{Guid.NewGuid():N}",
                        BlockingLayer: "Host",
                        ReasonClass: "HostCapabilityUnavailable"),
                    CancellationToken.None);
        }

        await resourceLockTransaction.CommitAsync();
        var result = await executionTask.WaitAsync(TimeSpan.FromSeconds(15));

        result.StoppedReason.Should().Be("ItemFailed");
        result.Items.Should().Contain(x => x.Disposition == GovernanceBatchItemDisposition.Failed);
        seedDb.ChangeTracker.Clear();
        (await seedDb.MemoryItems.AsNoTracking().SingleAsync(x => x.Id == predecessor.Id)).Status
            .Should().Be(MemoryStatus.Active);
        var findingReadBack = await seedDb.GovernanceFindings.AsNoTracking().SingleAsync(x => x.Id == finding.Id);
        findingReadBack.Status.Should().Be(GovernanceFindingStatus.HostBlocked);
        findingReadBack.GovernanceReasonClass.Should().Be("HostCapabilityUnavailable");
        (await seedDb.ConversationInsights.CountAsync(x =>
            x.SourceSystem == ChatGptProposalService.SourceSystem && x.ProjectId == projectId))
            .Should().Be(proposalCountBefore);
    }

    private static async Task WaitForWriterBlockedByResourceLockAsync(
        MemoryDbContext holderDb,
        CancellationToken cancellationToken = default)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var writerIsBlocked = await holderDb.Database.SqlQueryRaw<bool>("""
                    SELECT EXISTS (
                        SELECT 1
                        FROM pg_stat_activity AS waiter
                        WHERE waiter.pid <> pg_backend_pid()
                          AND pg_backend_pid() = ANY(pg_blocking_pids(waiter.pid))) AS "Value"
                    """)
                .SingleAsync(cancellationToken);
            if (writerIsBlocked)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken);
        }

        throw new TimeoutException(
            "PostgreSQL did not report a governance writer blocked by the held resource row lock.");
    }

    private static void SetActor(IServiceProvider services, TenantUser user)
        => services.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [
                SecurityScopes.MemoryRead,
                SecurityScopes.MemoryWrite,
                SecurityScopes.PreferencesRead,
                SecurityScopes.PreferencesWrite,
                SecurityScopes.LogsRead,
                SecurityScopes.GovernanceTrackerManage
            ],
            [],
            IsAuthenticated: true);

    private static MemoryItem CreateMemory(
        TenantUser user,
        string projectId,
        string key,
        DateTimeOffset timestamp)
        => new()
        {
            TenantId = user.TenantId,
            OwnerUserId = user.Id,
            ProjectId = projectId,
            ExternalKey = $"{key}:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Episode,
            Title = key,
            Content = key,
            Summary = key,
            SourceType = "successor-integration",
            SourceRef = $"test://{key}",
            Tags = [],
            Importance = .4m,
            Confidence = .6m,
            Status = MemoryStatus.Active,
            AuthorityState = MemoryAuthorityState.Current,
            MetadataJson = "{}",
            CreatedAt = timestamp,
            UpdatedAt = timestamp
        };
}

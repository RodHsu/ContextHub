using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class ScheduledGovernanceReceiptProjectionTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Run_get_preserves_the_receipt_during_a_PostgreSQL_history_lock_timeout()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var provider = scope.ServiceProvider;
        var actor = UseBootstrapActor(provider);
        var accessor = provider.GetRequiredService<IRequestActorAccessor>();
        var receipts = provider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-history-lock-{Guid.NewGuid():N}";
        var snapshot = $"kg:snapshot:{runId}";
        var identity = CurrentScheduledIdentity();
        var startedAt = DateTimeOffset.UtcNow;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var cancellationToken = deadline.Token;
        await receipts.RecordReviewStartedAsync(runId, startedAt, identity, cancellationToken);
        await receipts.RecordReviewAsync(CreateGenericReview(runId, identity, snapshot), startedAt, cancellationToken);
        await receipts.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 0), startedAt, cancellationToken);
        var receipt = (await receipts.GetAsync(runId, cancellationToken))!;
        await provider.GetRequiredService<IScheduledGovernanceReliabilityService>()
            .PersistObservationAsync(receipt, cancellationToken);

        await using var historyDb = new MemoryDbContext(provider.GetRequiredService<DbContextOptions<MemoryDbContext>>());
        await historyDb.Database.OpenConnectionAsync(cancellationToken);
        await historyDb.Database.ExecuteSqlRawAsync("SET lock_timeout = '250ms';", cancellationToken);
        var history = new ScheduledGovernanceReliabilityService(historyDb, accessor, TimeProvider.System);
        var queue = new DatabaseLogQueue();
        using var loggerFactory = LoggerFactory.Create(builder => builder.AddProvider(
            new DatabaseLoggerProvider(queue, Options.Create(new DatabaseLoggingOptions { MinimumLevel = LogLevel.Warning }))));
        var service = new ScheduledGovernanceService(
            provider.GetRequiredService<IKnowledgeReviewService>(),
            provider.GetRequiredService<IGovernanceBatchExecutor>(), receipts, accessor, history,
            loggerFactory.CreateLogger<ScheduledGovernanceService>());
        var receiptOnly = await service.GetReceiptAsync(runId, cancellationToken, includeReliabilityHistory: false);

        var rowQuery = historyDb.ScheduledGovernanceReliabilityRuns.AsNoTracking()
            .Where(row => row.TenantId == actor.TenantId && row.OwnerUserId == actor.UserId && row.GovernanceRunId == runId);
        var stored = await rowQuery.SingleAsync(cancellationToken);
        await using var blocker = new NpgsqlConnection(environment.PostgresConnectionString);
        await blocker.OpenAsync(cancellationToken);
        await using var transaction = await blocker.BeginTransactionAsync(cancellationToken);
        await using var lockTable = new NpgsqlCommand(
            "LOCK TABLE scheduled_governance_reliability_runs IN ACCESS EXCLUSIVE MODE;", blocker, transaction);
        await lockTable.ExecuteNonQueryAsync(cancellationToken);

        var failure = await FluentActions.Invoking(() => history.GetAsync(cancellationToken))
            .Should().ThrowAsync<InvalidOperationException>();
        failure.Which.InnerException.Should().BeOfType<PostgresException>().Which.SqlState
            .Should().Be(PostgresErrorCodes.LockNotAvailable);
        var missing = await service.GetReceiptAsync($"{runId}-absent", cancellationToken);
        missing.RunExists.Should().BeFalse();
        missing.Received.Should().BeFalse();
        missing.Status.Should().Be("NotReceived");
        missing.Reliability.Should().BeNull();
        queue.TryRead(out _).Should().BeFalse();

        var degraded = await service.GetReceiptAsync(runId, cancellationToken);
        degraded.Should().BeEquivalentTo(receiptOnly);
        degraded.ReceiptId.Should().Be(receipt.ReceiptId);
        degraded.Decision.Should().Be(ScheduledGovernanceDecision.NoOpConverged);
        degraded.Reliability.Should().BeNull();
        queue.TryRead(out var warning).Should().BeTrue();
        warning.Level.Should().Be(nameof(LogLevel.Warning));
        warning.ProjectId.Should().Be(ProjectContext.DefaultProjectId);
        warning.Message.Should().Contain("unavailable in reliability-history")
            .And.Contain(Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(runId))))
            .And.NotContain(runId);
        warning.Exception.Should().BeEmpty();

        await transaction.RollbackAsync(cancellationToken);
        var recovered = await service.GetReceiptAsync(runId, cancellationToken);
        recovered.Reliability!.Runs.Should().Contain(run => run.GovernanceRunId == runId);
        queue.TryRead(out _).Should().BeFalse();
        var after = await rowQuery.SingleAsync(cancellationToken);
        after.ProjectionJson.Should().Be(stored.ProjectionJson);
        after.UpdatedAt.Should().Be(stored.UpdatedAt);
        await historyDb.Database.ExecuteSqlRawAsync("RESET lock_timeout;", cancellationToken);
    }

    [DockerRequiredFact]
    public async Task Same_run_review_waits_for_the_active_cycle_and_cancellation_does_not_append_a_receipt()
    {
        var services = environment.GetFactory().Services;
        using var holderScope = services.CreateScope();
        using var waiterScope = services.CreateScope();
        var actor = UseBootstrapActor(holderScope.ServiceProvider);
        waiterScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = actor;
        var holderDb = holderScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var waiterDb = waiterScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await holderDb.Database.OpenConnectionAsync();
        await waiterDb.Database.OpenConnectionAsync();
        var holderPid = ((NpgsqlConnection)holderDb.Database.GetDbConnection()).ProcessID;
        var waiterPid = ((NpgsqlConnection)waiterDb.Database.GetDbConnection()).ProcessID;
        var runId = $"scheduled-serialized-review-{Guid.NewGuid():N}";
        var snapshot = $"kg:snapshot:{runId}:i0";
        var request = new ScheduledGovernanceReviewRequest(runId);
        var holderEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiterEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseHolder = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var holderReceipts = holderScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var holder = CreateService(holderScope.ServiceProvider, async (_, token) =>
        {
            holderEntered.TrySetResult();
            await releaseHolder.Task.WaitAsync(token);
            var response = CreateGenericReview(runId, CurrentScheduledIdentity(), snapshot);
            await holderReceipts.RecordReviewAsync(response, DateTimeOffset.UtcNow, token);
            return response;
        });
        var waiter = CreateService(waiterScope.ServiceProvider, async (_, token) =>
        {
            waiterEntered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            throw new InvalidOperationException("The test waiter must be cancelled.");
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var cancelWaiter = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
        var first = holder.ReviewAsync(request, deadline.Token);
        await holderEntered.Task.WaitAsync(deadline.Token);
        var second = waiter.ReviewAsync(request, cancelWaiter.Token);
        try
        {
            await using var monitor = new NpgsqlConnection(environment.PostgresConnectionString);
            await monitor.OpenAsync(deadline.Token);
            await using var command = new NpgsqlCommand("SELECT @holder = ANY(pg_blocking_pids(@waiter))", monitor);
            command.Parameters.AddWithValue("holder", holderPid);
            command.Parameters.AddWithValue("waiter", waiterPid);
            while (!(bool)(await command.ExecuteScalarAsync(deadline.Token))! && !waiterEntered.Task.IsCompleted)
                await Task.Delay(25, deadline.Token);
            waiterEntered.Task.IsCompleted.Should().BeFalse("the first review must own the run through decision persistence");
        }
        finally
        {
            cancelWaiter.Cancel();
            try { await second; }
            catch (OperationCanceledException) when (cancelWaiter.IsCancellationRequested) { }
            finally { releaseHolder.TrySetResult(); }
            await first;
        }

        var events = await holderDb.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.GovernanceRunId == runId).OrderBy(x => x.EventSequence)
            .Select(x => x.EventType).ToArrayAsync();
        events.Should().Equal("ReviewReceived", "ReviewCompleted", "ScheduledDecisionProjected");
        var finalized = await holder.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        finalized.Decision.Should().Be(ScheduledGovernanceDecision.NoOpConverged);
        finalized.FinalSnapshotToken.Should().Be(snapshot);
    }

    [DockerRequiredTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Initial_review_replay_preserves_the_first_human_decision_delta_and_receipt(bool projectionWasInterrupted)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var provider = scope.ServiceProvider;
        UseBootstrapActor(provider);
        var db = provider.GetRequiredService<MemoryDbContext>();
        var receipts = provider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-decision-replay-{Guid.NewGuid():N}";
        var snapshot = $"kg:snapshot:{runId}:i0";
        var request = new ScheduledGovernanceReviewRequest(runId);
        var underlyingReview = CreateGenericReview(runId, CurrentScheduledIdentity(), snapshot);
        var needsHumanDecision = new GovernanceReviewItem(
            $"finding:{runId}", GovernanceItemKind.Memory, ProjectContext.SharedProjectId, "Ambiguous", "Merge",
            GovernanceBatchRiskLevel.High, true, Guid.NewGuid(), [], ["HUMAN"], runId);
        var review = underlyingReview with
        {
            CandidateCount = 1,
            GovernancePlan = [needsHumanDecision],
            GovernedExceptionCount = 1,
            Convergence = underlyingReview.Convergence with { GovernedExceptionCount = 1, RequiresUserDecisionCount = 1 },
            GovernedExceptionStates = [new(needsHumanDecision.ItemKey, "Finding", "RequiresUserDecision", 3)]
        };
        var service = CreateService(provider, async (_, token) =>
        {
            await receipts.RecordReviewAsync(review, DateTimeOffset.UtcNow, token);
            return review;
        });

        if (projectionWasInterrupted)
        {
            var startedAt = DateTimeOffset.UtcNow;
            await receipts.RecordReviewStartedAsync(runId, startedAt, CurrentScheduledIdentity(), CancellationToken.None);
            await receipts.RecordReviewAsync(review, startedAt, CancellationToken.None);
            var pending = await service.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
            pending.Outcome.Should().Be("DecisionProjectionPending");
        }

        var first = await service.ReviewAsync(request, CancellationToken.None);
        first.Decision.Should().Be(ScheduledGovernanceDecision.HumanDecisionOnly);
        first.ExceptionDelta!.New.Should().Be(1);
        var originalReceipt = await service.GetReceiptAsync(runId, default, includeReliabilityHistory: false);

        using var replayScope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(replayScope.ServiceProvider);
        var replayService = CreateService(replayScope.ServiceProvider, (_, _) =>
            throw new InvalidOperationException("A completed initial review must be read from the receipts."));
        var replay = await replayService.ReviewAsync(request, CancellationToken.None);

        replay.Should().BeEquivalentTo(first);
        var receipt = await replayService.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        receipt.ReceiptId.Should().Be(originalReceipt.ReceiptId);
        receipt.Decision.Should().Be(ScheduledGovernanceDecision.HumanDecisionOnly);
        var events = await db.GovernanceRunReceipts.AsNoTracking()
            .Where(x => x.GovernanceRunId == runId)
            .OrderBy(x => x.EventSequence)
            .Select(x => x.EventType)
            .ToArrayAsync();
        events.Should().Equal("ReviewReceived", "ReviewCompleted", "ScheduledDecisionProjected");
    }

    [DockerRequiredFact]
    public async Task Receipt_reads_and_initial_review_replay_honor_current_project_grants()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var provider = scope.ServiceProvider;
        var actor = UseBootstrapActor(provider);
        var db = provider.GetRequiredService<MemoryDbContext>();
        var receipts = provider.GetRequiredService<IGovernanceRunReceiptService>();
        var reliability = provider.GetRequiredService<IScheduledGovernanceReliabilityService>();
        var projectId = $"ReceiptGrant-{Guid.NewGuid():N}";
        var restricted = new ScheduledGovernanceReviewRequest($"receipt-grant-{Guid.NewGuid():N}");
        var shared = new ScheduledGovernanceReviewRequest($"receipt-shared-{Guid.NewGuid():N}");
        var grant = new TenantProjectGrant
        {
            TenantId = actor.TenantId!.Value,
            ProjectId = projectId.ToUpperInvariant(),
            CanRead = true,
            CanWrite = true
        };
        db.TenantProjectGrants.Add(grant);
        await db.SaveChangesAsync();
        try
        {
            var service = CreateService(provider, async (request, token) =>
            {
                var runId = request.GovernanceRunId!;
                var review = CreateGenericReview(runId, CurrentScheduledIdentity(), $"kg:snapshot:{runId}", durableCount: 1);
                if (runId == restricted.GovernanceRunId)
                {
                    review = review with
                    {
                        Projects = [.. review.Projects, new AccessibleProjectResult(projectId, true, true)],
                        DurableMemoryCoverage = review.DurableMemoryCoverage! with
                        {
                            GovernanceProjectIds = [ProjectContext.SharedProjectId, projectId]
                        }
                    };
                }
                await receipts.RecordReviewAsync(review, DateTimeOffset.UtcNow, token);
                return review;
            }, reliability);
            var first = await service.ReviewAsync(restricted, default);
            await service.ReviewAsync(shared, default);

            grant.CanWrite = false;
            await db.SaveChangesAsync();
            (await service.ReviewAsync(restricted, default)).Should().BeEquivalentTo(first);
            var authorized = await service.GetReceiptAsync(restricted.GovernanceRunId, default);
            authorized.Reliability!.Runs.Should().Contain(run => run.GovernanceRunId == restricted.GovernanceRunId);

            grant.CanRead = false;
            await db.SaveChangesAsync();
            Func<Task> read = () => service.GetReceiptAsync(restricted.GovernanceRunId, default);
            Func<Task> get = () => receipts.GetAsync(restricted.GovernanceRunId, default);
            Func<Task> replay = () => service.ReviewAsync(restricted, default);
            await read.Should().ThrowAsync<UnauthorizedAccessException>();
            await get.Should().ThrowAsync<UnauthorizedAccessException>();
            await replay.Should().ThrowAsync<UnauthorizedAccessException>();
            var page = await receipts.ListAsync(new(), default);
            page.Should().NotContain(receipt => receipt.GovernanceRunId == restricted.GovernanceRunId);
            page.Should().Contain(receipt => receipt.GovernanceRunId == shared.GovernanceRunId);
            var sharedReceipt = await service.GetReceiptAsync(shared.GovernanceRunId, default);
            sharedReceipt.Received.Should().BeTrue();
            sharedReceipt.Reliability.Should().BeNull();

            grant.CanRead = true;
            await db.SaveChangesAsync();
            (await service.GetReceiptAsync(restricted.GovernanceRunId, default)).ReceiptId.Should().Be(authorized.ReceiptId);
            (await service.GetReceiptAsync(shared.GovernanceRunId, default)).Reliability!.Runs
                .Should().Contain(run => run.GovernanceRunId == restricted.GovernanceRunId);
        }
        finally
        {
            db.TenantProjectGrants.Remove(grant);
            await db.SaveChangesAsync();
        }
    }

    private static ScheduledGovernanceService CreateService(IServiceProvider provider,
        Func<KnowledgeReviewRequest, CancellationToken, Task<KnowledgeReviewResult>> review,
        IScheduledGovernanceReliabilityService? reliability = null) => new(
        new CallbackKnowledgeReview(review), provider.GetRequiredService<IGovernanceBatchExecutor>(),
        provider.GetRequiredService<IGovernanceRunReceiptService>(), provider.GetRequiredService<IRequestActorAccessor>(), reliability);

    private sealed class CallbackKnowledgeReview(
        Func<KnowledgeReviewRequest, CancellationToken, Task<KnowledgeReviewResult>> review) : IKnowledgeReviewService
    {
        public Task<KnowledgeReviewResult> ReviewAsync(KnowledgeReviewRequest request, CancellationToken cancellationToken)
            => review(request, cancellationToken);
    }

    [DockerRequiredTheory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Scheduled_run_get_waits_for_the_persisted_decision_after_generic_review_completes(int actionable)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var scheduled = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>();
        var reliability = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"scheduled-projection-pending-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        var startedAt = DateTimeOffset.UtcNow;

        for (var generation = 0; generation < 2; generation++)
        {
            var isReReview = generation > 0;
            var snapshot = $"kg:snapshot:{runId}:{generation}";
            await receipts.RecordReviewStartedAsync(runId, startedAt, identity, default, isReReview);
            await receipts.RecordReviewAsync(CreateGenericReview(runId, identity, snapshot, isReReview, actionable), startedAt, default);
            var generic = (await receipts.GetAsync(runId, default))!;
            generic.Status.Should().Be("Completed");

            var pending = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
            pending.ReceiptId.Should().Be(generic.ReceiptId);
            pending.Received.Should().BeTrue();
            pending.Terminal.Should().BeFalse();
            pending.Status.Should().Be("Running");
            pending.Decision.Should().BeNull();
            pending.Outcome.Should().Be("DecisionProjectionPending");
            pending.FinalConvergenceStatus.Should().Be("DecisionProjectionPending");
            (await receipts.GetAsync(runId, default))!.ReceiptId.Should().Be(generic.ReceiptId);

            await reliability.PersistObservationAsync(generic, default);
            var waitingObservation = await ReadObservationAsync();
            waitingObservation.Status.Should().Be("Running");
            waitingObservation.Terminal.Should().BeFalse();
            waitingObservation.Decision.Should().BeNull();
            waitingObservation.FinalConvergenceStatus.Should().Be("DecisionProjectionPending");

            var legacyProjection = JsonSerializer.Serialize(waitingObservation with
            {
                Status = "Completed",
                Terminal = true,
                Decision = actionable == 0 ? ScheduledGovernanceDecision.NoOpConverged : ScheduledGovernanceDecision.ReversibleExecutionRequired,
                FinalConvergenceStatus = generic.FinalConvergenceStatus,
                StoppedReason = "Completed"
            }, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            await db.ScheduledGovernanceReliabilityRuns.Where(x => x.GovernanceRunId == runId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(x => x.ProjectionJson, legacyProjection));
            var storedLegacyProjection = await ReadObservationJsonAsync();
            var history = await reliability.GetAsync(default);
            var pendingHistory = history.Runs.Single(x => x.GovernanceRunId == runId);
            pendingHistory.Reasons.Should().Contain("decision-projection-pending");
            pendingHistory.ServerInvariants["decisionObeyed"].Should().BeNull();
            pendingHistory.CountedTowardGate.Should().BeFalse();
            (await ReadObservationJsonAsync()).Should().Be(storedLegacyProjection);
            (await receipts.GetAsync(runId, default))!.ReceiptId.Should().Be(generic.ReceiptId);

            var decision = CreateScheduledDecision(runId, snapshot, isReReview, actionable);
            await receipts.RecordScheduledDecisionAsync(decision, startedAt, default);
            var finalized = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
            finalized.ReceiptId.Should().NotBe(generic.ReceiptId);
            finalized.Terminal.Should().BeTrue();
            finalized.Decision.Should().Be(decision.Decision);
            finalized.Outcome.Should().Be(decision.Decision.ToString());
            await reliability.PersistObservationAsync((await receipts.GetAsync(runId, default))!, default);
            var completedObservation = await ReadObservationAsync();
            completedObservation.ReceiptId.Should().Be(finalized.ReceiptId);
            completedObservation.Terminal.Should().BeTrue();
            completedObservation.Decision.Should().Be(decision.Decision);
            (await reliability.GetAsync(default)).Runs.Single(x => x.GovernanceRunId == runId)
                .Reasons.Should().NotContain("decision-projection-pending");
        }

        async Task<ScheduledGovernanceReliabilityReceiptProjection> ReadObservationAsync() =>
            JsonSerializer.Deserialize<ScheduledGovernanceReliabilityReceiptProjection>(
                await ReadObservationJsonAsync(), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Task<string> ReadObservationJsonAsync() =>
            db.ScheduledGovernanceReliabilityRuns.AsNoTracking().Where(x => x.GovernanceRunId == runId)
                .Select(x => x.ProjectionJson).SingleAsync();
    }

    [DockerRequiredFact]
    public async Task Scheduled_run_get_uses_one_snapshot_when_a_batch_receipt_commits_mid_read()
    {
        var services = environment.GetFactory().Services;
        using var writerScope = services.CreateScope();
        UseBootstrapActor(writerScope.ServiceProvider);
        var writer = writerScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-receipt-snapshot-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        const string snapshot = "kg:snapshot:receipt-race";
        var startedAt = DateTimeOffset.UtcNow;
        await writer.RecordReviewStartedAsync(runId, startedAt, identity, default);
        await writer.RecordReviewAsync(CreateGenericReview(runId, identity, snapshot, actionable: 1), startedAt, default);
        await writer.RecordScheduledDecisionAsync(
            CreateScheduledDecision(runId, snapshot, isReReview: false, actionable: 1), startedAt, default);
        var initial = (await writer.GetAsync(runId, default))!;

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
        var write = new AppendBatchAfterReceiptRead(
            cancellationToken => writer.RecordExecutionStartedAsync(request, DateTimeOffset.UtcNow, cancellationToken));
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
                writerScope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .AddInterceptors(write)
            .Options;
        await using var readerDb = new MemoryDbContext(options);
        var actorAccessor = writerScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var reader = new GovernanceRunReceiptService(readerDb, actorAccessor, TimeProvider.System);
        var scheduled = new ScheduledGovernanceService(
            writerScope.ServiceProvider.GetRequiredService<IKnowledgeReviewService>(),
            writerScope.ServiceProvider.GetRequiredService<IGovernanceBatchExecutor>(),
            reader, actorAccessor);

        var before = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        write.Committed.Should().BeTrue();
        before.ReceiptId.Should().Be(initial.ReceiptId);
        before.Status.Should().Be(initial.Status);
        before.Decision.Should().Be(ScheduledGovernanceDecision.ReversibleExecutionRequired);
        before.Terminal.Should().BeTrue();
        before.LatestBatchReceived.Should().BeFalse();
        before.RequestIdentityHash.Should().BeEmpty();

        var after = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        after.ReceiptId.Should().NotBe(initial.ReceiptId);
        after.Status.Should().Be("Running");
        after.Terminal.Should().BeFalse();
        after.LatestBatchReceived.Should().BeTrue();
        after.RequestIdentityHash.Should().NotBeNullOrWhiteSpace();
    }

    private sealed class AppendBatchAfterReceiptRead(Func<CancellationToken, Task> append) : DbCommandInterceptor
    {
        public bool Committed { get; private set; }

        public override async ValueTask<DbDataReader> ReaderExecutedAsync(
            DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!Committed && eventData.CommandSource == CommandSource.LinqQuery &&
                command.CommandText.Contains("governance_run_receipts", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("ORDER BY", StringComparison.OrdinalIgnoreCase) &&
                command.CommandText.Contains("DESC", StringComparison.OrdinalIgnoreCase))
            {
                await append(cancellationToken);
                Committed = true;
            }

            return result;
        }
    }

    [DockerRequiredFact]
    public async Task Batched_safety_history_matches_exact_receipt_lookup_and_limits_each_owner_run_independently()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var accessor = scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var tenantId = actor.TenantId!.Value;
        var ownerId = actor.UserId!.Value;
        var runId = $"history-shared-key-{Guid.NewGuid():N}";
        var overLimitRunId = $"history-over-limit-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var otherOwner = new TenantUser
        {
            TenantId = tenantId,
            Username = $"history-owner-{Guid.NewGuid():N}",
            DisplayName = "Safety history scope fixture",
            Role = TenantUserRole.Owner,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TenantUsers.Add(otherOwner);
        await db.SaveChangesAsync(CancellationToken.None);

        async Task<Guid> RecordCompletedRunAsync(string key)
        {
            var snapshot = $"kg:snapshot:{key}";
            var identity = CurrentScheduledIdentity();
            await receipts.RecordReviewStartedAsync(key, now, identity, CancellationToken.None);
            await receipts.RecordReviewAsync(CreateGenericReview(key, identity, snapshot), now, CancellationToken.None);
            await receipts.RecordScheduledDecisionAsync(
                CreateScheduledDecision(key, snapshot, isReReview: false, actionable: 0), now, CancellationToken.None);
            return (await receipts.GetAsync(key, CancellationToken.None))!.ReceiptId;
        }

        var receiptId = await RecordCompletedRunAsync(runId);
        var auditedRunId = $"history-audited-{Guid.NewGuid():N}";
        var nonMatchingAuditsRunId = $"history-nonmatching-audits-{Guid.NewGuid():N}";
        var auditedReceiptId = await RecordCompletedRunAsync(auditedRunId);
        var nonMatchingAuditsReceiptId = await RecordCompletedRunAsync(nonMatchingAuditsRunId);
        AddFixtureAudit(tenantId, new { governanceRunId = auditedRunId });
        AddFixtureAudit(Guid.NewGuid(), new { governanceRunId = nonMatchingAuditsRunId });
        AddFixtureAudit(tenantId, new { governanceRunId = new[] { nonMatchingAuditsRunId } });
        AddFixtureAudit(tenantId, new[] { new { governanceRunId = nonMatchingAuditsRunId } });
        AddFixtureAudit(tenantId, new { governanceRunId = nonMatchingAuditsRunId.ToUpperInvariant() });

        void AddFixtureAudit(Guid auditTenant, object details)
            => db.SecurityAuditEvents.Add(new SecurityAuditEvent
            {
                TenantId = auditTenant,
                ActorUserId = otherOwner.Id,
                EventType = SecurityAuditEventType.GovernanceBatchExecutionCompleted,
                DetailsJson = System.Text.Json.JsonSerializer.Serialize(details),
                CreatedAt = now
            });

        var overLimitReceiptId = await RecordCompletedRunAsync(overLimitRunId);
        var overLimitSource = await db.GovernanceRunReceipts.AsNoTracking()
            .SingleAsync(row => row.Id == overLimitReceiptId, CancellationToken.None);
        var extraEvents = Enumerable.Range(0, 1_000)
            .Select(index => CloneReceipt(overLimitSource, $"history-extra-{index:D4}", "ReviewCompleted"))
            .ToArray();
        db.GovernanceRunReceipts.AddRange(extraEvents);
        await db.SaveChangesAsync(CancellationToken.None);
        var latestOverLimit = extraEvents.MaxBy(row => row.EventSequence)!;

        accessor.Current = actor with { UserId = otherOwner.Id };
        var otherReceiptId = await RecordCompletedRunAsync(runId);
        accessor.Current = actor;

        var own = new ScheduledGovernanceServerSafetyEvidenceQuery(tenantId, ownerId, receiptId, runId);
        var foreign = new ScheduledGovernanceServerSafetyEvidenceQuery(tenantId, otherOwner.Id, otherReceiptId, runId);
        var wrongOwner = own with { ReceiptId = otherReceiptId };
        var wrongRun = own with { GovernanceRunId = overLimitRunId };
        var missing = own with { ReceiptId = Guid.NewGuid() };
        var tooManyEvents = own with { GovernanceRunId = overLimitRunId, ReceiptId = latestOverLimit.Id };
        var audited = own with { GovernanceRunId = auditedRunId, ReceiptId = auditedReceiptId };
        var nonMatchingAudits = own with { GovernanceRunId = nonMatchingAuditsRunId, ReceiptId = nonMatchingAuditsReceiptId };
        ScheduledGovernanceServerSafetyEvidenceQuery[] queries =
            [tooManyEvents, own, wrongOwner, wrongRun, missing, foreign, audited, nonMatchingAudits];

        var commandCount = 0;
        var options = new DbContextOptionsBuilder<MemoryDbContext>(
                scope.ServiceProvider.GetRequiredService<DbContextOptions<MemoryDbContext>>())
            .LogTo(_ => commandCount++, [RelationalEventId.CommandExecuted])
            .Options;
        await using var readDb = new MemoryDbContext(options);
        var provider = new ScheduledGovernanceServerSafetyEvidenceProvider(readDb);
        var expected = new Dictionary<ScheduledGovernanceServerSafetyEvidenceQuery, ScheduledGovernanceServerSafetyEvidenceSnapshot?>();
        foreach (var query in queries)
        {
            expected[query] = await provider.GetAsync(query, CancellationToken.None);
        }
        var singleReadCommandCount = commandCount;

        commandCount = 0;
        var actual = await provider.GetHistoryAsync([.. queries, own], CancellationToken.None);
        actual.Should().BeEquivalentTo(expected);
        commandCount.Should().BeLessThan(singleReadCommandCount, "batch history must share the PostgreSQL reads");
        commandCount.Should().BeLessThanOrEqualTo(8, "queries are bounded per owner and chunk rather than per run");
        actual[own]!.InitialReviewReceived.Should().BeTrue();
        actual[foreign]!.InitialReviewReceived.Should().BeTrue();
        actual[own]!.ReceiptEventSequence.Should().NotBe(actual[foreign]!.ReceiptEventSequence);
        actual[wrongOwner].Should().BeNull();
        actual[wrongRun].Should().BeNull();
        actual[missing].Should().BeNull();
        actual[tooManyEvents]!.ReceiptEventSequence.Should().Be(latestOverLimit.EventSequence);
        actual[tooManyEvents]!.InitialReviewReceived.Should().BeNull();
        actual[tooManyEvents]!.DecisionObeyed.Should().BeNull();
        actual[audited]!.NoDuplicateMutation.Should().BeNull();
        actual[nonMatchingAudits]!.NoDuplicateMutation.Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task Zero_execute_decisions_have_typed_structural_proof_with_user_scope_handled_separately()
    {
        foreach (var decision in new[] { ScheduledGovernanceDecision.HumanDecisionOnly,
                     ScheduledGovernanceDecision.NoOpConverged, ScheduledGovernanceDecision.CoverageIncomplete,
                     ScheduledGovernanceDecision.ReversibleExecutionRequired })
        {
            using var scope = environment.GetFactory().Services.CreateScope();
            var actor = UseBootstrapActor(scope.ServiceProvider);
            var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var runId = $"typed-zero-{decision}-{Guid.NewGuid():N}";
            var snapshot = $"kg:snapshot:{runId}";
            var complete = decision != ScheduledGovernanceDecision.CoverageIncomplete;
            var generic = CreateGenericReview(runId, CurrentScheduledIdentity(), snapshot) with
            {
                Projects = [new AccessibleProjectResult("ContextHub", true, true),
                    new AccessibleProjectResult(ProjectContext.SharedProjectId, true, true),
                    new AccessibleProjectResult(ProjectContext.UserProjectId, true, true)]
            };
            generic = generic with
            {
                DurableMemoryCoverage = generic.DurableMemoryCoverage! with
                { GovernanceProjectIds = ["ContextHub", ProjectContext.SharedProjectId] },
                Convergence = generic.Convergence with { CoverageComplete = complete }
            };
            await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, CurrentScheduledIdentity(), CancellationToken.None);
            await receipts.RecordReviewAsync(generic, DateTimeOffset.UtcNow, CancellationToken.None);
            var projected = CreateScheduledDecision(runId, snapshot, false, 0) with
            {
                Decision = decision,
                CoverageComplete = complete,
                ResolvedProjectIds = ["ContextHub", ProjectContext.SharedProjectId]
            };
            await receipts.RecordScheduledDecisionAsync(projected, DateTimeOffset.UtcNow, CancellationToken.None);
            var immutableBefore = await db.GovernanceRunReceipts.AsNoTracking()
                .Where(row => row.GovernanceRunId == runId).OrderBy(row => row.EventSequence).ToArrayAsync();
            var beforeJson = System.Text.Json.JsonSerializer.Serialize(immutableBefore);
            var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
            var evidence = (await scope.ServiceProvider.GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
                .GetAsync(new(actor.TenantId!.Value, actor.UserId!.Value, receipt.ReceiptId, runId), CancellationToken.None))!;
            evidence.DecisionObeyed.Should().Be(decision != ScheduledGovernanceDecision.ReversibleExecutionRequired);
            evidence.NoUnauthorizedMutation.Should().BeTrue();
            evidence.NoDuplicateMutation.Should().BeTrue();
            evidence.DisplayNameUnchanged.Should().BeTrue();
            evidence.BusinessWorkItemsUntouched.Should().BeTrue();
            evidence.ImmutableSnapshotBound.Should().BeTrue();
            evidence.FixedReversibleExecutorUsed.Should().BeNull();
            evidence.InvariantProofs["fixedReversibleExecutorUsed"].Status.Should()
                .Be(ScheduledGovernanceInvariantProofStatus.NotApplicable);
            evidence.InvariantProofs["noGeneralConnectorFallback"].Status.Should()
                .Be(ScheduledGovernanceInvariantProofStatus.NotObservable);
            var service = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>();
            await service.ObserveAsync(receipt, CancellationToken.None);
            var replay = await service.GetAsync(CancellationToken.None);
            var run = replay.Runs.Single(row => row.GovernanceRunId == runId);
            run.ServerInvariantProofs["immutableSnapshotBound"].Status.Should().Be(ScheduledGovernanceInvariantProofStatus.ProvenTrue);
            run.ServerInvariantProofs["fixedReversibleExecutorUsed"].Status.Should().Be(ScheduledGovernanceInvariantProofStatus.NotApplicable);
            run.CountedTowardGate.Should().BeFalse(); run.IsIgnored.Should().BeTrue();
            run.StreakBefore.Should().Be(0); run.StreakAfter.Should().Be(0);
            replay.ResetEvents.Should().BeEmpty(); replay.GatePassed.Should().BeFalse();
            var flat = await scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>()
                .GetReceiptAsync(runId, CancellationToken.None);
            flat.ReceiptId.Should().Be(receipt.ReceiptId);
            flat.InitialReviewReceived.Should().BeTrue();
            flat.InitialReviewReceivedStatus.Should().Be(ScheduledGovernanceInvariantProofStatus.ProvenTrue);
            flat.DecisionObeyed.Should().Be(evidence.DecisionObeyed);
            flat.DecisionObeyedStatus.Should().Be(run.ServerInvariantProofs["decisionObeyed"].Status);
            flat.DisplayNameUnchanged.Should().Be(run.ServerInvariants["displayNameUnchangedByRun"]);
            flat.BusinessWorkItemsUntouched.Should().Be(run.ServerInvariants["businessWorkItemsUntouchedByRun"]);
            flat.FixedReversibleExecutorUsed.Should().BeNull();
            flat.FixedReversibleExecutorUsedStatus.Should().Be(ScheduledGovernanceInvariantProofStatus.NotApplicable);
            flat.NoGeneralConnectorFallbackStatus.Should().Be(ScheduledGovernanceInvariantProofStatus.NotObservable);
            flat.HostDispatchCompletedStatus.Should().Be(ScheduledGovernanceInvariantProofStatus.Unproven);
            flat.ServerInvariantProofs.Should().BeEquivalentTo(run.ServerInvariantProofs);
            flat.ServerInvariants.Should().BeEquivalentTo(run.ServerInvariants);

            var immutableAfter = await db.GovernanceRunReceipts.AsNoTracking()
                .Where(row => row.GovernanceRunId == runId).OrderBy(row => row.EventSequence).ToArrayAsync();
            System.Text.Json.JsonSerializer.Serialize(immutableAfter).Should().Be(beforeJson);
            if (decision == ScheduledGovernanceDecision.HumanDecisionOnly)
            {
                // The reliability row still references the prior immutable receipt.
                // A new same-run review must never inherit its zero-execute proof.
                await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow,
                    CurrentScheduledIdentity(), CancellationToken.None, isReReview: true);
                var pending = await scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>()
                    .GetReceiptAsync(runId, CancellationToken.None);
                pending.ReceiptId.Should().NotBe(receipt.ReceiptId);
                pending.InitialReviewReceived.Should().BeNull();
                pending.NoUnauthorizedMutation.Should().BeNull();
                pending.NoUnauthorizedMutationStatus.Should().Be(ScheduledGovernanceInvariantProofStatus.Unproven);
                pending.FixedReversibleExecutorUsedStatus.Should().Be(ScheduledGovernanceInvariantProofStatus.Unproven);
            }

        }
    }

    [DockerRequiredFact]
    public async Task Zero_execute_proof_rejects_orphan_audit_or_unexpected_project_scope()
    {
        foreach (var corruptScope in new[] { false, true })
        {
            using var scope = environment.GetFactory().Services.CreateScope();
            var actor = UseBootstrapActor(scope.ServiceProvider);
            var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var runId = $"zero-conflict-{Guid.NewGuid():N}";
            var snapshot = $"kg:snapshot:{runId}";
            await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, CurrentScheduledIdentity(), CancellationToken.None);
            var generic = CreateGenericReview(runId, CurrentScheduledIdentity(), snapshot);
            if (corruptScope)
                generic = generic with { Projects = [new("UnexpectedScope", true, true), new(ProjectContext.SharedProjectId, true, true)] };
            await receipts.RecordReviewAsync(generic, DateTimeOffset.UtcNow, CancellationToken.None);
            await receipts.RecordScheduledDecisionAsync(CreateScheduledDecision(runId, snapshot, false, 0), DateTimeOffset.UtcNow, CancellationToken.None);
            if (!corruptScope)
            {
                db.SecurityAuditEvents.Add(new SecurityAuditEvent
                {
                    TenantId = actor.TenantId,
                    ActorUserId = actor.UserId,
                    EventType = SecurityAuditEventType.GovernanceBatchExecutionCompleted,
                    DetailsJson = System.Text.Json.JsonSerializer.Serialize(new { governanceRunId = runId }),
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync();
            }
            var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
            var evidence = (await scope.ServiceProvider.GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
                .GetAsync(new(actor.TenantId!.Value, actor.UserId!.Value, receipt.ReceiptId, runId), CancellationToken.None))!;
            evidence.DecisionObeyed.Should().BeFalse();
            evidence.NoUnauthorizedMutation.Should().BeNull();
            evidence.NoDuplicateMutation.Should().BeNull();
            evidence.DisplayNameUnchanged.Should().BeNull();
            evidence.BusinessWorkItemsUntouched.Should().BeNull();
        }
    }

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

    [DockerRequiredTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_execution_remains_stopped_on_scheduled_receipt_readback(bool legacyCompletedReceipt)
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var scheduled = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var runId = $"scheduled-unknown-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        var snapshot = $"kg:snapshot:{runId}:i0";
        var startedAt = DateTimeOffset.UtcNow;
        await receipts.RecordReviewStartedAsync(runId, startedAt, identity, default);
        await receipts.RecordReviewAsync(CreateGenericReview(runId, identity, snapshot, actionable: 1), startedAt, default);
        await receipts.RecordScheduledDecisionAsync(CreateScheduledDecision(runId, snapshot, false, 1), startedAt, default);

        var request = new GovernanceBatchExecuteRequest(runId, [ProjectContext.SharedProjectId], snapshot,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled,
            ToolContractVersion: GovernanceToolContract.ToolContractVersion, SchemaHash: GovernanceToolContract.SchemaHash)
        { ReceiptContractIdentity = identity };
        await receipts.RecordExecutionStartedAsync(request, startedAt, default);
        var execution = CreateNoOpExecution(runId, snapshot) with
        {
            StoppedReason = "UnknownResult",
            RequiresReReview = true,
            FailedCount = 1
        };
        if (legacyCompletedReceipt)
        {
            var received = await db.GovernanceRunReceipts.AsNoTracking()
                .SingleAsync(x => x.GovernanceRunId == runId && x.EventType == "BatchReceived");
            received.Id = Guid.NewGuid();
            received.EventSequence = 0;
            received.EventKey = Guid.NewGuid().ToString("N");
            received.EventType = "BatchCompleted";
            received.Status = "Completed";
            received.StoppedReason = "UnknownResult";
            received.Failed = 1;
            received.FailurePhase = "ItemExecution";
            db.GovernanceRunReceipts.Add(received);
            await db.SaveChangesAsync();
        }
        else
        {
            await receipts.RecordExecutionAsync(request, execution, startedAt, default);
        }

        var batch = db.GovernanceRunReceipts.Where(x => x.GovernanceRunId == runId && x.EventType == "BatchCompleted");
        var rawStatus = await batch.Select(x => x.Status).SingleAsync();
        var readback = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);

        readback.Received.Should().BeTrue();
        readback.Terminal.Should().BeTrue();
        readback.Status.Should().Be("Stopped");
        readback.Outcome.Should().Be("UnknownResult");
        readback.StoppedReason.Should().Be("UnknownResult");
        readback.Decision.Should().BeNull();
        readback.Failed.Should().Be(1);
        rawStatus.Should().Be(legacyCompletedReceipt ? "Completed" : "Stopped");
        (await batch.Select(x => x.Status).SingleAsync()).Should().Be(rawStatus);

        var stored = (await receipts.GetAsync(runId, default))!;
        var reliability = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>();
        await reliability.PersistObservationAsync(stored, default);
        var historyRows = db.ScheduledGovernanceReliabilityRuns.Where(x => x.GovernanceRunId == runId);
        var projectionJson = await historyRows.Select(x => x.ProjectionJson).SingleAsync();
        var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var projection = JsonSerializer.Deserialize<ScheduledGovernanceReliabilityReceiptProjection>(projectionJson, jsonOptions)!;
        projection.Status.Should().Be("Stopped");
        projection.Decision.Should().BeNull();
        if (legacyCompletedReceipt)
        {
            // A legacy view could source its reason from the unfinished execution's progress JSON.
            projectionJson = JsonSerializer.Serialize(projection with
            {
                Status = "Completed",
                StoppedReason = "Running",
                FinalConvergenceStatus = nameof(ScheduledGovernanceDecision.ReversibleExecutionRequired),
                Decision = ScheduledGovernanceDecision.ReversibleExecutionRequired
            }, jsonOptions);
            await historyRows.ExecuteUpdateAsync(set => set.SetProperty(x => x.ProjectionJson, projectionJson));
            projectionJson = await historyRows.Select(x => x.ProjectionJson).SingleAsync();
        }

        var run = (await reliability.GetAsync(default)).Runs.Single(x => x.GovernanceRunId == runId);
        run.IsFailed.Should().BeTrue();
        run.CountedTowardGate.Should().BeFalse();
        (await historyRows.Select(x => x.ProjectionJson).SingleAsync()).Should().Be(projectionJson);

        var reviewAgainAt = DateTimeOffset.UtcNow;
        var reviewAgainSnapshot = $"kg:snapshot:{runId}:r1";
        await receipts.RecordReviewStartedAsync(runId, reviewAgainAt, identity, default, isReReview: true);
        await receipts.RecordReviewAsync(CreateGenericReview(runId, identity, reviewAgainSnapshot, isReReview: true), reviewAgainAt, default);
        await receipts.RecordScheduledDecisionAsync(CreateScheduledDecision(runId, reviewAgainSnapshot, true, 0), reviewAgainAt, default);
        var afterReview = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        afterReview.Outcome.Should().Be("NoOpConverged");
        afterReview.FinalSnapshotToken.Should().Be(reviewAgainSnapshot);
        afterReview.Failed.Should().Be(1);

        await receipts.RecordExecutionAsync(request, execution with { IsReplay = true }, DateTimeOffset.UtcNow, default);
        var afterReplay = await scheduled.GetReceiptAsync(runId, default, includeReliabilityHistory: false);
        afterReplay.Decision.Should().Be(ScheduledGovernanceDecision.NoOpConverged);
        afterReplay.FinalSnapshotToken.Should().Be(reviewAgainSnapshot);
        afterReplay.Failed.Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Authorized_reversible_execution_then_re_review_is_not_misclassified_as_zero_execute()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"reversible-typed-proof-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        var snapshot = $"kg:snapshot:{runId}";
        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None);
        await receipts.RecordReviewAsync(CreateGenericReview(runId, identity, snapshot, actionable: 1), DateTimeOffset.UtcNow, CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(CreateScheduledDecision(runId, snapshot, false, 1), DateTimeOffset.UtcNow, CancellationToken.None);
        var request = new GovernanceBatchExecuteRequest(runId, [ProjectContext.SharedProjectId], snapshot,
            ExecutionMode: GovernanceBatchExecutionMode.Scheduled,
            ToolContractVersion: GovernanceToolContract.ToolContractVersion, SchemaHash: GovernanceToolContract.SchemaHash)
        { ReceiptContractIdentity = identity };
        await receipts.RecordExecutionStartedAsync(request, DateTimeOffset.UtcNow, CancellationToken.None);
        await receipts.RecordExecutionAsync(request, CreateNoOpExecution(runId, snapshot), DateTimeOffset.UtcNow, CancellationToken.None);
        var finalSnapshot = snapshot + ":re-review";
        await receipts.RecordReviewStartedAsync(runId, DateTimeOffset.UtcNow, identity, CancellationToken.None, isReReview: true);
        await receipts.RecordReviewAsync(CreateGenericReview(runId, identity, finalSnapshot, isReReview: true), DateTimeOffset.UtcNow, CancellationToken.None);
        await receipts.RecordScheduledDecisionAsync(CreateScheduledDecision(runId, finalSnapshot, true, 0), DateTimeOffset.UtcNow, CancellationToken.None);
        var receipt = (await receipts.GetAsync(runId, CancellationToken.None))!;
        var evidence = (await scope.ServiceProvider.GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
            .GetAsync(new(actor.TenantId!.Value, actor.UserId!.Value, receipt.ReceiptId, runId), CancellationToken.None))!;
        evidence.InitialReviewReceived.Should().BeTrue();
        evidence.ReviewRequestIdentityHash.Should().Be(
            ScheduledGovernanceReliabilityEvidenceContract.ComputeReviewRequestIdentityHash(runId));
        evidence.DecisionObeyed.Should().BeTrue();
        evidence.ImmutableSnapshotBound.Should().BeTrue();
        evidence.NoUnauthorizedMutation.Should().BeNull("executor write-set safety is not proven by a later no-op decision");
        evidence.FixedReversibleExecutorUsed.Should().BeNull();
        evidence.InvariantProofs.Should().NotContainKey("fixedReversibleExecutorUsed");
        var reliability = await scope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>()
            .ObserveAsync(receipt, CancellationToken.None);
        var run = reliability.Runs.Single(row => row.GovernanceRunId == runId);
        run.ServerInvariantProofs["fixedReversibleExecutorUsed"].Status.Should().Be(ScheduledGovernanceInvariantProofStatus.Unproven);
        run.CountedTowardGate.Should().BeFalse(); run.IsIgnored.Should().BeTrue();
        reliability.ResetEvents.Should().BeEmpty();
        var flat = await scope.ServiceProvider.GetRequiredService<IScheduledGovernanceService>()
            .GetReceiptAsync(runId, CancellationToken.None);
        flat.FixedReversibleExecutorUsedStatus.Should().Be(ScheduledGovernanceInvariantProofStatus.Unproven);
        flat.NoUnauthorizedMutation.Should().BeNull();
        // This fixture retains a batch requiring recovery/re-review; the public
        // receipt must fail closed until its Decision lineage becomes usable.
        flat.Decision.Should().BeNull();
        flat.ServerInvariantProofs.Should().BeEmpty();

        var safety = (IScheduledGovernanceServerSafetyHistoryProvider)scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>();
        var finalQuery = new ScheduledGovernanceServerSafetyEvidenceQuery(
            actor.TenantId.Value, actor.UserId.Value, receipt.ReceiptId, runId);
        var replay = CreateNoOpExecution(runId, snapshot) with { IsReplay = true };
        await receipts.RecordExecutionAsync(request, replay, DateTimeOffset.UtcNow, default);
        var replayReceipt = (await receipts.GetAsync(runId, default))!;
        replayReceipt.ReceiptId.Should().NotBe(receipt.ReceiptId);
        var replayQuery = finalQuery with { ReceiptId = replayReceipt.ReceiptId };

        (await safety.GetAsync(replayQuery, default))!.DecisionObeyed.Should().BeTrue();
        (await safety.GetAsync(finalQuery, default))!.DecisionObeyed.Should().BeTrue();
        var history = await safety.GetHistoryAsync([finalQuery, replayQuery], default);
        history[finalQuery]!.DecisionObeyed.Should().BeTrue();
        history[replayQuery]!.DecisionObeyed.Should().BeTrue();
        history[finalQuery]!.NoUnauthorizedMutation.Should().BeNull();

        await receipts.RecordExecutionAsync(request with { Cursor = "unmatched-replay" }, replay, DateTimeOffset.UtcNow, default);
        var unmatched = (await receipts.GetAsync(runId, default))!;
        var unmatchedQuery = finalQuery with { ReceiptId = unmatched.ReceiptId };
        (await safety.GetAsync(unmatchedQuery, default))!.DecisionObeyed.Should().BeFalse();
        (await safety.GetAsync(finalQuery, default))!.DecisionObeyed.Should().BeNull();
        var invalidHistory = await safety.GetHistoryAsync([finalQuery, unmatchedQuery], default);
        invalidHistory[finalQuery]!.DecisionObeyed.Should().BeNull();
        invalidHistory[unmatchedQuery]!.DecisionObeyed.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Scheduled_re_review_without_an_initial_lifecycle_cannot_bind_natural_dispatch()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var runId = $"scheduled-re-review-only-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();
        const string snapshot = "kg:snapshot:re-review-only";
        var now = DateTimeOffset.UtcNow;

        await receipts.RecordReviewStartedAsync(runId, now, identity, default, isReReview: true);
        await receipts.RecordReviewAsync(CreateGenericReview(runId, identity, snapshot, isReReview: true), now, default);
        await receipts.RecordScheduledDecisionAsync(CreateScheduledDecision(runId, snapshot, true, 0), now, default);

        var receipt = (await receipts.GetAsync(runId, default))!;
        var evidence = await scope.ServiceProvider.GetRequiredService<IScheduledGovernanceServerSafetyEvidenceProvider>()
            .GetAsync(new(actor.TenantId!.Value, actor.UserId!.Value, receipt.ReceiptId, runId), default);

        evidence.Should().NotBeNull();
        evidence!.InitialReviewReceived.Should().BeFalse();
        evidence.ReviewRequestIdentityHash.Should().BeNull();
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
        evidence.NoUnauthorizedMutation.Should().BeTrue();
        evidence.NoDuplicateMutation.Should().BeTrue();
        evidence.DisplayNameUnchanged.Should().BeTrue();
        evidence.BusinessWorkItemsUntouched.Should().BeTrue();
        evidence.HostDispatchCompleted.Should().BeNull();

        var reliability = await scope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityService>()
            .ObserveAsync(receipt, CancellationToken.None);
        var reliabilityRun = reliability.Runs.Single(run => run.GovernanceRunId == runId);
        reliabilityRun.Reasons.Should().NotContain("count-invariant-not-proven");
        reliabilityRun.Reasons.Should().NotContain("decision-obedience-not-proven");
        reliabilityRun.ServerInvariants["noGeneralConnectorFallback"].Should().BeNull();
        reliabilityRun.ServerInvariants["hostDispatchCompleted"].Should().BeNull();
        reliabilityRun.ServerInvariants["noUnauthorizedMutation"].Should().BeTrue();
        reliabilityRun.CountedTowardGate.Should().BeFalse();
        reliabilityRun.IsIgnored.Should().BeTrue();
        reliability.ResetEvents.Should().BeEmpty();
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
            RuntimeIdentity: ScheduledGovernanceContract.RuntimeIdentity)
        {
            SkillCoverage = new GovernanceSurfaceCoverageResult(8, 7, 6, 5, 4, 3, 2, true, true),
            SkillSignalCounts = new Dictionary<string, int> { ["StaleMetadata"] = 2 }
        };

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
        projected.SkillCoverage.Should().Be(review.SkillCoverage);
        projected.SkillSignalCounts.Should().BeEquivalentTo(review.SkillSignalCounts);

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
        afterExecution.SkillCoverage.Should().Be(review.SkillCoverage);
        afterExecution.SkillSignalCounts.Should().BeEquivalentTo(review.SkillSignalCounts);
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
        afterReplay.SkillCoverage.Should().Be(review.SkillCoverage);
        afterReplay.SkillSignalCounts.Should().BeEquivalentTo(review.SkillSignalCounts);
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

    [DockerRequiredFact]
    public async Task Scheduled_lineage_rejects_cross_runtime_or_legacy_replay_without_appending_an_event()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var sourceRunId = $"scheduled-runtime-source-{Guid.NewGuid():N}";
        var legacyRunId = $"scheduled-runtime-legacy-{Guid.NewGuid():N}";
        var identity = CurrentScheduledIdentity();

        await receipts.RecordReviewStartedAsync(
            sourceRunId,
            DateTimeOffset.UtcNow,
            identity,
            CancellationToken.None);
        var source = await db.GovernanceRunReceipts.AsNoTracking()
            .SingleAsync(x => x.TenantId == actor.TenantId &&
                              x.OwnerUserId == actor.UserId &&
                              x.GovernanceRunId == sourceRunId &&
                              x.EventType == "ReviewReceived");
        var legacy = CloneReceipt(source, "legacy-review-received", "ReviewReceived");
        legacy.GovernanceRunId = legacyRunId;
        legacy.RuntimeEvidenceVersion = string.Empty;
        legacy.RuntimeServiceName = string.Empty;
        legacy.RuntimeBuildVersion = string.Empty;
        legacy.RuntimeBuildTimestampUtc = null;
        legacy.RuntimeDerivedIdentity = string.Empty;
        legacy.RuntimeIdentityHash = string.Empty;
        legacy.RequestIdentityHash = string.Empty;
        await db.GovernanceRunReceipts.AddAsync(legacy);
        await db.SaveChangesAsync();

        var replay = () => receipts.RecordReviewStartedAsync(
            legacyRunId,
            DateTimeOffset.UtcNow,
            identity,
            CancellationToken.None);

        await replay.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.SchemaCapabilityMismatch);
        (await db.GovernanceRunReceipts.AsNoTracking()
                .CountAsync(x => x.TenantId == actor.TenantId &&
                                 x.OwnerUserId == actor.UserId &&
                                 x.GovernanceRunId == legacyRunId))
            .Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Generic_legacy_review_replay_must_fail_closed_without_appending_an_event()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var actor = UseBootstrapActor(scope.ServiceProvider);
        var receipts = scope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var sourceRunId = $"generic-runtime-source-{Guid.NewGuid():N}";
        var legacyRunId = $"generic-runtime-legacy-{Guid.NewGuid():N}";
        var identity = new GovernanceReceiptContractIdentity(
            GovernanceToolContract.ToolContractVersion,
            GovernanceToolContract.SchemaHash,
            GovernanceToolContract.PublishedCatalogVersion);

        await receipts.RecordReviewStartedAsync(
            sourceRunId,
            DateTimeOffset.UtcNow,
            identity,
            CancellationToken.None);
        var source = await db.GovernanceRunReceipts.AsNoTracking()
            .SingleAsync(x => x.TenantId == actor.TenantId &&
                              x.OwnerUserId == actor.UserId &&
                              x.GovernanceRunId == sourceRunId &&
                              x.EventType == "ReviewReceived");
        var legacyEventKey = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.UTF8.GetBytes($"review-received\n{legacyRunId}")))
            .ToLowerInvariant();
        var legacy = CloneReceipt(source, legacyEventKey, "ReviewReceived");
        legacy.GovernanceRunId = legacyRunId;
        legacy.RequestIdentityHash = string.Empty;
        await db.GovernanceRunReceipts.AddAsync(legacy);
        await db.SaveChangesAsync();

        var replay = () => receipts.RecordReviewStartedAsync(
            legacyRunId,
            DateTimeOffset.UtcNow,
            identity,
            CancellationToken.None);

        await replay.Should().ThrowAsync<GovernanceBatchException>()
            .Where(x => x.Code == GovernanceBatchErrorCode.ReplayPayloadMismatch);
        (await db.GovernanceRunReceipts.AsNoTracking()
                .CountAsync(x => x.TenantId == actor.TenantId &&
                                 x.OwnerUserId == actor.UserId &&
                                 x.GovernanceRunId == legacyRunId))
            .Should().Be(1);
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

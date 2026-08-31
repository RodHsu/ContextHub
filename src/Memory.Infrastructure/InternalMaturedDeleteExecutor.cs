using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class InternalMaturedDeleteExecutor(
    IServiceScopeFactory scopeFactory,
    IOptions<AutonomousGovernanceOptions> options,
    TimeProvider timeProvider) : IInternalMaturedDeleteExecutor
{
    private readonly AutonomousGovernanceOptions _options = options.Value;

    public async Task<InternalMaturedDeleteBatchResult> ExecuteNextBatchAsync(CancellationToken cancellationToken)
    {
        var startedAt = timeProvider.GetUtcNow();
        var runId = $"internal-retention-{startedAt:yyyyMMddHHmmss}-{Guid.NewGuid():N}";
        await RecoverMissingReceiptsAsync(cancellationToken);
        var claimed = await ClaimNextBatchAsync(runId, startedAt, cancellationToken);
        if (claimed.Owner is null || claimed.Candidates.Length == 0)
        {
            return new InternalMaturedDeleteBatchResult(runId, 0, 0, 0, 0, [], [], [], "QueueEmpty");
        }
        var owner = claimed.Owner;
        var candidates = claimed.Candidates;

        var deleted = 0;
        var cancelled = 0;
        var failed = 0;
        var tombstones = new List<Guid>();
        var auditIds = new List<Guid>();
        foreach (var candidate in candidates)
        {
            await using var itemScope = scopeFactory.CreateAsyncScope();
            var actorAccessor = itemScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            actorAccessor.Current = BuildServiceActor(owner);
            var retention = itemScope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
            try
            {
                var deleteResult = await retention.DeleteMaturedAsync(candidate.ResourceId, candidate.ProjectId, runId, cancellationToken);
                if (deleteResult.Deleted && !deleteResult.IsReplay) deleted++;
                tombstones.Add(deleteResult.TombstoneId);
                auditIds.Add(deleteResult.AuditId);
            }
            catch (InvalidOperationException exception)
            {
                var db = itemScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                db.ChangeTracker.Clear();
                var state = await db.MemoryRetentionStates.SingleOrDefaultAsync(x =>
                    x.ResourceId == candidate.ResourceId && x.TenantId == owner.TenantId && x.OwnerUserId == owner.OwnerUserId,
                    cancellationToken);
                if (state is not null)
                {
                    var eligibilityCancelled = state.DeleteEligibleAt is null || state.LifecycleStatus != "Eligible";
                    if (string.Equals(state.ClaimToken, runId, StringComparison.Ordinal))
                    {
                        state.ClaimToken = string.Empty;
                        state.ClaimedAt = null;
                        state.ClaimLastError = eligibilityCancelled ? "EligibilityCancelled" : exception.GetType().Name;
                        state.UpdatedAt = timeProvider.GetUtcNow();
                        await db.SaveChangesAsync(cancellationToken);
                    }
                    if (eligibilityCancelled) cancelled++;
                    else failed++;
                }
                else
                {
                    failed++;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await ReleaseClaimAsync(candidate, runId, "Cancelled", CancellationToken.None);
                throw;
            }
            catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
                await ReleaseClaimAsync(candidate, runId, exception.GetType().Name, cancellationToken);
            }
        }

        var result = new InternalMaturedDeleteBatchResult(runId, candidates.Length, deleted, cancelled, failed,
            tombstones.Distinct().ToArray(), auditIds.Distinct().ToArray(),
            candidates.Select(x => x.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            failed > 0 ? "CompletedWithItemFailures" : "Completed");
        await using (var receiptScope = scopeFactory.CreateAsyncScope())
        {
            var actorAccessor = receiptScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            actorAccessor.Current = BuildServiceActor(owner);
            var receipts = receiptScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
            await receipts.RecordInternalRetentionAsync(result, startedAt, cancellationToken);
        }
        return result;
    }

    private async Task<ClaimedBatch> ClaimNextBatchAsync(
        string runId,
        DateTimeOffset startedAt,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await (
                from state in db.MemoryRetentionStates.AsNoTracking()
                join user in db.TenantUsers.AsNoTracking()
                    on new { state.TenantId, Id = state.OwnerUserId } equals new { user.TenantId, Id = user.Id }
                where user.Status == TenantUserStatus.Active &&
                      state.LifecycleStatus == "Eligible" && state.DeleteEligibleAt != null && state.DeleteEligibleAt <= startedAt &&
                      (state.ClaimToken == string.Empty || state.ClaimedAt < startedAt.AddMinutes(-15))
                orderby state.DeleteEligibleAt, state.ResourceId
                select new CandidateOwner(state.TenantId, state.OwnerUserId))
            .FirstOrDefaultAsync(cancellationToken);
        if (owner is null) return new ClaimedBatch(null, []);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var expiredBefore = startedAt.AddMinutes(-15);
        var states = await db.MemoryRetentionStates
            .FromSqlInterpolated($"""
                SELECT *
                FROM memory_retention_states
                WHERE tenant_id = {owner.TenantId}
                  AND owner_user_id = {owner.OwnerUserId}
                  AND lifecycle_status = 'Eligible'
                  AND delete_eligible_at IS NOT NULL
                  AND delete_eligible_at <= {startedAt}
                  AND (claim_token = '' OR claimed_at < {expiredBefore})
                ORDER BY delete_eligible_at, resource_id
                FOR UPDATE SKIP LOCKED
                LIMIT {_options.NormalizedInternalMaturedDeleteBatchSize}
                """)
            .ToArrayAsync(cancellationToken);
        foreach (var state in states)
        {
            state.ClaimToken = runId;
            state.ClaimedAt = startedAt;
            state.ClaimAttemptCount += 1;
            state.ClaimLastError = string.Empty;
            state.UpdatedAt = startedAt;
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new ClaimedBatch(owner, states.Select(x => new MaturedCandidate(
            x.ResourceId, x.ProjectId, x.TenantId, x.OwnerUserId)).ToArray());
    }

    private async Task ReleaseClaimAsync(
        MaturedCandidate candidate,
        string runId,
        string error,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var state = await db.MemoryRetentionStates.SingleOrDefaultAsync(x =>
            x.ResourceId == candidate.ResourceId && x.TenantId == candidate.TenantId &&
            x.OwnerUserId == candidate.OwnerUserId && x.ClaimToken == runId, cancellationToken);
        if (state is null) return;
        state.ClaimToken = string.Empty;
        state.ClaimedAt = null;
        state.ClaimLastError = error;
        state.UpdatedAt = timeProvider.GetUtcNow();
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task RecoverMissingReceiptsAsync(CancellationToken cancellationToken)
    {
        await using var discoveryScope = scopeFactory.CreateAsyncScope();
        var db = discoveryScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var orphanedRuns = await db.ResourceTombstones.AsNoTracking()
            .Where(tombstone => tombstone.GovernanceRunId.StartsWith("internal-retention-") &&
                !db.GovernanceRunReceipts.Any(receipt =>
                    receipt.TenantId == tombstone.TenantId && receipt.OwnerUserId == tombstone.OwnerUserId &&
                    receipt.GovernanceRunId == tombstone.GovernanceRunId &&
                    receipt.EventType == "InternalRetentionCompleted"))
            .GroupBy(x => new { x.TenantId, x.OwnerUserId, x.GovernanceRunId })
            .Select(group => new
            {
                group.Key.TenantId,
                group.Key.OwnerUserId,
                group.Key.GovernanceRunId,
                FirstDeletedAt = group.Min(x => x.DeletedAt)
            })
            .OrderBy(x => x.FirstDeletedAt)
            .Take(100)
            .ToArrayAsync(cancellationToken);
        foreach (var run in orphanedRuns)
        {
            var values = await db.ResourceTombstones.AsNoTracking()
                .Where(x => x.TenantId == run.TenantId && x.OwnerUserId == run.OwnerUserId &&
                            x.GovernanceRunId == run.GovernanceRunId)
                .OrderBy(x => x.DeletedAt)
                .ThenBy(x => x.Id)
                .ToArrayAsync(cancellationToken);
            await using var receiptScope = scopeFactory.CreateAsyncScope();
            var owner = new CandidateOwner(run.TenantId, run.OwnerUserId);
            receiptScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = BuildServiceActor(owner);
            var result = new InternalMaturedDeleteBatchResult(
                run.GovernanceRunId,
                values.Length,
                values.Length,
                0,
                0,
                values.Select(x => x.Id).ToArray(),
                values.Select(x => x.AuditId).ToArray(),
                values.Select(x => x.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
                "RecoveredAfterRestart");
            await receiptScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>()
                .RecordInternalRetentionAsync(result, values.Min(x => x.DeletedAt), cancellationToken);
        }
    }

    private static ContextHubRequestActor BuildServiceActor(CandidateOwner owner)
        => new(owner.TenantId, owner.OwnerUserId, "internal-retention-worker", TenantUserRole.Admin,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], IsAuthenticated: true, IsServiceActor: true);

    private sealed record CandidateOwner(Guid TenantId, Guid OwnerUserId);
    private sealed record MaturedCandidate(Guid ResourceId, string ProjectId, Guid TenantId, Guid OwnerUserId);
    private sealed record ClaimedBatch(CandidateOwner? Owner, MaturedCandidate[] Candidates);
}

public sealed class AutonomousMaturedDeleteHostedService(
    IInternalMaturedDeleteExecutor executor,
    IOptions<AutonomousGovernanceOptions> options,
    ILogger<AutonomousMaturedDeleteHostedService> logger) : BackgroundService
{
    private readonly AutonomousGovernanceOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.InternalMaturedDeleteEnabled)
        {
            logger.LogInformation("Internal matured-delete worker is disabled.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var result = await executor.ExecuteNextBatchAsync(stoppingToken);
                if (result.ScannedCount > 0)
                {
                    logger.LogInformation(
                        "Internal matured-delete run {GovernanceRunId} scanned {ScannedCount}, deleted {DeletedCount}, cancelled {CancelledCount}, failed {FailedCount}.",
                        result.GovernanceRunId, result.ScannedCount, result.DeletedCount, result.CancelledCount, result.FailedCount);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Internal matured-delete worker iteration failed closed.");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.NormalizedInternalMaturedDeletePollSeconds), stoppingToken);
        }
    }
}

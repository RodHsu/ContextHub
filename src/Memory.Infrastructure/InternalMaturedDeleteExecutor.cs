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
        CandidateOwner? owner;
        await using (var discoveryScope = scopeFactory.CreateAsyncScope())
        {
            var db = discoveryScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            owner = await db.MemoryRetentionStates.AsNoTracking()
                .Where(x => x.LifecycleStatus == "Eligible" && x.DeleteEligibleAt != null && x.DeleteEligibleAt <= startedAt)
                .OrderBy(x => x.DeleteEligibleAt).ThenBy(x => x.ResourceId)
                .Select(x => new CandidateOwner(x.TenantId, x.OwnerUserId))
                .FirstOrDefaultAsync(cancellationToken);
        }

        if (owner is null)
        {
            return new InternalMaturedDeleteBatchResult(runId, 0, 0, 0, 0, [], [], [], "QueueEmpty");
        }

        MaturedCandidate[] candidates;
        TenantUserRole role;
        await using (var queryScope = scopeFactory.CreateAsyncScope())
        {
            var db = queryScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var user = await db.TenantUsers.AsNoTracking().SingleOrDefaultAsync(x =>
                x.TenantId == owner.TenantId && x.Id == owner.OwnerUserId && x.Status == TenantUserStatus.Active,
                cancellationToken);
            if (user is null)
            {
                return new InternalMaturedDeleteBatchResult(runId, 0, 0, 0, 1, [], [], [], "OwnerUnavailableFailClosed");
            }
            role = user.Role;
            candidates = await db.MemoryRetentionStates.AsNoTracking()
                .Where(x => x.TenantId == owner.TenantId && x.OwnerUserId == owner.OwnerUserId &&
                            x.LifecycleStatus == "Eligible" && x.DeleteEligibleAt != null && x.DeleteEligibleAt <= startedAt)
                .OrderBy(x => x.DeleteEligibleAt).ThenBy(x => x.ResourceId)
                .Take(_options.NormalizedInternalMaturedDeleteBatchSize)
                .Select(x => new MaturedCandidate(x.ResourceId, x.ProjectId))
                .ToArrayAsync(cancellationToken);
        }

        var deleted = 0;
        var cancelled = 0;
        var failed = 0;
        var tombstones = new List<Guid>();
        var auditIds = new List<Guid>();
        foreach (var candidate in candidates)
        {
            await using var itemScope = scopeFactory.CreateAsyncScope();
            var actorAccessor = itemScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            actorAccessor.Current = BuildServiceActor(owner, role);
            var retention = itemScope.ServiceProvider.GetRequiredService<IAutonomousRetentionService>();
            try
            {
                var deleteResult = await retention.DeleteMaturedAsync(candidate.ResourceId, candidate.ProjectId, runId, cancellationToken);
                if (deleteResult.Deleted && !deleteResult.IsReplay) deleted++;
                tombstones.Add(deleteResult.TombstoneId);
                auditIds.Add(deleteResult.AuditId);
            }
            catch (InvalidOperationException)
            {
                var db = itemScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                db.ChangeTracker.Clear();
                var state = await db.MemoryRetentionStates.AsNoTracking().SingleOrDefaultAsync(x =>
                    x.ResourceId == candidate.ResourceId && x.TenantId == owner.TenantId && x.OwnerUserId == owner.OwnerUserId,
                    cancellationToken);
                if (state is not null && (state.DeleteEligibleAt is null || state.LifecycleStatus != "Eligible")) cancelled++;
                else failed++;
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                failed++;
            }
        }

        var result = new InternalMaturedDeleteBatchResult(runId, candidates.Length, deleted, cancelled, failed,
            tombstones.Distinct().ToArray(), auditIds.Distinct().ToArray(),
            candidates.Select(x => x.ProjectId).Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            failed > 0 ? "CompletedWithItemFailures" : "Completed");
        await using (var receiptScope = scopeFactory.CreateAsyncScope())
        {
            var actorAccessor = receiptScope.ServiceProvider.GetRequiredService<IRequestActorAccessor>();
            actorAccessor.Current = BuildServiceActor(owner, role);
            var receipts = receiptScope.ServiceProvider.GetRequiredService<IGovernanceRunReceiptService>();
            await receipts.RecordInternalRetentionAsync(result, startedAt, cancellationToken);
        }
        return result;
    }

    private static ContextHubRequestActor BuildServiceActor(CandidateOwner owner, TenantUserRole role)
        => new(owner.TenantId, owner.OwnerUserId, "internal-retention-worker", role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [], IsAuthenticated: true, IsServiceActor: true);

    private sealed record CandidateOwner(Guid TenantId, Guid OwnerUserId);
    private sealed record MaturedCandidate(Guid ResourceId, string ProjectId);
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

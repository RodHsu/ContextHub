using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public static class PlatformProjectionContract
{
    public const string ProjectionName = "activity-v1";
    public const int DefaultBatchSize = 250;
    public const int MaximumBatchSize = 2000;
    public const int DefaultLeaseSeconds = 120;
}

public sealed record PlatformProjectionRunRequest(
    Guid? TenantId,
    string ProjectId,
    PlatformBackgroundMode Mode,
    string OwnerId,
    int BatchSize = PlatformProjectionContract.DefaultBatchSize,
    int LeaseSeconds = PlatformProjectionContract.DefaultLeaseSeconds,
    string? IdempotencyKey = null);

public sealed record PlatformProjectionRunResult(
    Guid RunId,
    PlatformBackgroundRunStatus Status,
    PlatformBackgroundMode Mode,
    long Generation,
    long AuthoritySequenceBoundary,
    long Cursor,
    long Expected,
    long Scanned,
    bool CoverageComplete,
    long Stale,
    long Drift,
    long Repaired,
    long Rebuilt,
    long Failed,
    TimeSpan Duration,
    DateTimeOffset? LastSuccessAt,
    DateTimeOffset? NextRunAt,
    string LeaseToken,
    long LeaseVersion);

public interface IPlatformProjectionService
{
    Task<PlatformProjectionRunResult> RunAsync(PlatformProjectionRunRequest request, CancellationToken cancellationToken);
    Task ValidateLeaseAsync(Guid runId, string ownerId, string leaseToken, long leaseVersion, CancellationToken cancellationToken);
    Task CancelAsync(Guid runId, string ownerId, string leaseToken, long leaseVersion, CancellationToken cancellationToken);
}

/// <summary>
/// Shared bounded outbox-to-monitoring projector. It uses the same transaction lock, lease,
/// idempotency and append-only event conventions as AgentExecution without sharing its business
/// lifecycle. The monitoring rows are always derived and rebuildable.
/// </summary>
public sealed class PlatformProjectionService(IApplicationDbContext dbContext, IClock clock) : IPlatformProjectionService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<PlatformProjectionRunResult> RunAsync(PlatformProjectionRunRequest request, CancellationToken cancellationToken)
    {
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var tenantScopeKey = TenantScopeKey(request.TenantId);
        var ownerId = Require(request.OwnerId, nameof(request.OwnerId), 200);
        var batchSize = Math.Clamp(request.BatchSize, 1, PlatformProjectionContract.MaximumBatchSize);
        var leaseSeconds = Math.Clamp(request.LeaseSeconds, 30, 900);
        try
        {
            return await dbContext.ExecuteInTransactionAsync(async ct =>
            {
                await dbContext.AcquireTransactionLockAsync($"platform-projection:{tenantScopeKey}:{projectId}:{PlatformProjectionContract.ProjectionName}", ct);
                var now = clock.UtcNow;
                var state = await dbContext.MonitoringProjectionStates.SingleOrDefaultAsync(
                    x => x.ProjectionName == PlatformProjectionContract.ProjectionName && x.TenantScopeKey == tenantScopeKey && x.ProjectId == projectId, ct);
                if (state is null)
                {
                    state = new MonitoringProjectionState
                    {
                        ProjectionName = PlatformProjectionContract.ProjectionName,
                        TenantScopeKey = tenantScopeKey,
                        TenantId = request.TenantId,
                        ProjectId = projectId,
                        UpdatedAt = now
                    };
                    await dbContext.MonitoringProjectionStates.AddAsync(state, ct);
                }

                var run = await dbContext.PlatformBackgroundRuns
                    .Include(x => x.Events)
                    .Where(x => x.TenantId == request.TenantId && x.ProjectId == projectId && x.JobType == PlatformProjectionContract.ProjectionName && x.Mode == request.Mode &&
                                (x.Status == PlatformBackgroundRunStatus.Running || x.Status == PlatformBackgroundRunStatus.RetryScheduled))
                    .OrderByDescending(x => x.CreatedAt)
                    .FirstOrDefaultAsync(ct);

                if (run is not null && run.LeaseExpiresAt > now && !string.Equals(run.OwnerId, ownerId, StringComparison.Ordinal))
                    throw new UnauthorizedAccessException("Projection run is owned by another active worker.");
                if (run is { Status: PlatformBackgroundRunStatus.RetryScheduled } && run.EligibleAt > now)
                    return Result(run, state, string.Empty, now);

                string leaseToken;
                if (run is null)
                {
                    var boundary = await dbContext.AuthorityOutboxEvents
                        .Where(x => x.TenantId == request.TenantId && x.ProjectId == projectId)
                        .Select(x => (long?)x.Sequence).MaxAsync(ct) ?? 0;
                    var startCursor = request.Mode == PlatformBackgroundMode.Incremental ? state.Cursor : 0;
                    run = new PlatformBackgroundRun
                    {
                        TenantId = request.TenantId,
                        ProjectId = projectId,
                        JobType = PlatformProjectionContract.ProjectionName,
                        ScopeKey = $"{tenantScopeKey}:{projectId}",
                        Mode = request.Mode,
                        Status = PlatformBackgroundRunStatus.Running,
                        Generation = request.Mode == PlatformBackgroundMode.Full ? checked(state.Generation + 1) : state.Generation,
                        AuthoritySequenceBoundary = boundary,
                        Cursor = startCursor,
                        ExpectedCount = await dbContext.AuthorityOutboxEvents.LongCountAsync(
                            x => x.TenantId == request.TenantId && x.ProjectId == projectId && x.Sequence > startCursor && x.Sequence <= boundary, ct),
                        OwnerId = ownerId,
                        LeaseVersion = 1,
                        EligibleAt = now,
                        CreatedAt = now,
                        UpdatedAt = now,
                        StartedAt = now
                    };
                    leaseToken = NewToken();
                    run.LeaseTokenHash = Hash(leaseToken);
                    run.LeaseExpiresAt = now.AddSeconds(leaseSeconds);
                    await dbContext.PlatformBackgroundRuns.AddAsync(run, ct);
                    await AppendAsync(run, PlatformBackgroundEventType.Prepared, new { run.Mode, run.Generation, run.AuthoritySequenceBoundary }, ct);
                    await AppendAsync(run, PlatformBackgroundEventType.Claimed, new { run.OwnerId, run.LeaseVersion, run.LeaseExpiresAt }, ct);
                }
                else
                {
                    leaseToken = NewToken();
                    run.Status = PlatformBackgroundRunStatus.Running;
                    run.OwnerId = ownerId;
                    run.LeaseTokenHash = Hash(leaseToken);
                    run.LeaseVersion++;
                    run.LeaseExpiresAt = now.AddSeconds(leaseSeconds);
                    run.UpdatedAt = now;
                    await AppendAsync(run, PlatformBackgroundEventType.Claimed, new { run.OwnerId, run.LeaseVersion, run.LeaseExpiresAt, reclaimed = true }, ct);
                }

                var batch = await dbContext.AuthorityOutboxEvents.AsNoTracking()
                    .Where(x => x.TenantId == request.TenantId && x.ProjectId == projectId && x.Sequence > run.Cursor && x.Sequence <= run.AuthoritySequenceBoundary)
                    .OrderBy(x => x.Sequence)
                    .Take(batchSize)
                    .ToArrayAsync(ct);

                foreach (var authorityEvent in batch)
                {
                    var projection = await dbContext.MonitoringActivityProjections.SingleOrDefaultAsync(x => x.OutboxEventId == authorityEvent.Id, ct);
                    if (projection is null)
                    {
                        projection = Map(authorityEvent, run.Generation, now);
                        await dbContext.MonitoringActivityProjections.AddAsync(projection, ct);
                        run.RebuiltCount++;
                    }
                    else if (projection.AuthoritySequence <= authorityEvent.Sequence)
                    {
                        if (projection.AuthorityRevision != authorityEvent.AuthorityRevision || projection.EventType != authorityEvent.EventType)
                            run.DriftCount++;
                        Apply(projection, authorityEvent, Math.Max(projection.Generation, run.Generation), now);
                        run.RepairedCount++;
                    }
                    else
                    {
                        run.StaleCount++;
                    }

                    var delivery = await dbContext.PlatformOutboxDeliveries.SingleOrDefaultAsync(
                        x => x.OutboxEventId == authorityEvent.Id && x.Consumer == PlatformProjectionContract.ProjectionName, ct);
                    if (delivery is null)
                    {
                        delivery = new PlatformOutboxDelivery
                        {
                            OutboxEventId = authorityEvent.Id,
                            Consumer = PlatformProjectionContract.ProjectionName,
                            Status = PlatformOutboxDeliveryStatus.Delivered,
                            Attempt = 1,
                            EligibleAt = now,
                            DeliveredAt = now,
                            UpdatedAt = now
                        };
                        await dbContext.PlatformOutboxDeliveries.AddAsync(delivery, ct);
                    }
                    else
                    {
                        delivery.Status = PlatformOutboxDeliveryStatus.Delivered;
                        delivery.Attempt++;
                        delivery.DeliveredAt = now;
                        delivery.UpdatedAt = now;
                        delivery.LastErrorCode = string.Empty;
                    }
                    run.Cursor = authorityEvent.Sequence;
                    run.ScannedCount++;
                }

                run.CoverageComplete = run.Cursor >= run.AuthoritySequenceBoundary;
                run.UpdatedAt = now;
                if (run.CoverageComplete)
                {
                    run.Status = PlatformBackgroundRunStatus.Completed;
                    run.CompletedAt = now;
                    run.LeaseExpiresAt = null;
                    run.LeaseTokenHash = string.Empty;
                    run.OwnerId = string.Empty;
                    if (run.Cursor >= state.AuthoritySequence)
                    {
                        state.AuthoritySequence = run.Cursor;
                        state.Cursor = run.Cursor;
                        state.Generation = Math.Max(state.Generation, run.Generation);
                        state.LastSuccessAt = now;
                        state.NextRunAt = now.AddMinutes(15);
                        state.UpdatedAt = now;
                    }
                    await AppendAsync(run, PlatformBackgroundEventType.Completed, new
                    {
                        expected = run.ExpectedCount,
                        scanned = run.ScannedCount,
                        run.CoverageComplete,
                        run.StaleCount,
                        run.DriftCount,
                        run.RepairedCount,
                        run.RebuiltCount,
                        run.FailedCount,
                        run.Cursor,
                        run.Generation
                    }, ct);
                }
                else
                {
                    run.LeaseExpiresAt = now.AddSeconds(leaseSeconds);
                    await AppendAsync(run, PlatformBackgroundEventType.Checkpoint, new { run.Cursor, run.ScannedCount, run.ExpectedCount }, ct);
                }

                await dbContext.SaveChangesAsync(ct);
                return Result(run, state, leaseToken, now);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException)
        {
            throw;
        }
        catch (Exception exception)
        {
            await RecordFailureAsync(request.TenantId, projectId, request.Mode, ownerId, exception, cancellationToken);
            throw;
        }
    }

    private async Task RecordFailureAsync(
        Guid? tenantId,
        string projectId,
        PlatformBackgroundMode mode,
        string ownerId,
        Exception exception,
        CancellationToken cancellationToken)
    {
        dbContext.ClearTrackedChanges();
        await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            var tenantScopeKey = TenantScopeKey(tenantId);
            await dbContext.AcquireTransactionLockAsync($"platform-projection:{tenantScopeKey}:{projectId}:{PlatformProjectionContract.ProjectionName}", ct);
            var now = clock.UtcNow;
            var run = await dbContext.PlatformBackgroundRuns.Include(x => x.Events)
                .Where(x => x.TenantId == tenantId && x.ProjectId == projectId && x.JobType == PlatformProjectionContract.ProjectionName && x.Mode == mode &&
                            (x.Status == PlatformBackgroundRunStatus.Running || x.Status == PlatformBackgroundRunStatus.RetryScheduled))
                .OrderByDescending(x => x.CreatedAt).FirstOrDefaultAsync(ct);
            if (run is null)
            {
                run = new PlatformBackgroundRun
                {
                    TenantId = tenantId,
                    ProjectId = projectId,
                    JobType = PlatformProjectionContract.ProjectionName,
                    ScopeKey = $"{tenantScopeKey}:{projectId}",
                    Mode = mode,
                    Status = PlatformBackgroundRunStatus.RetryScheduled,
                    Attempt = 1,
                    MaxAttempts = 5,
                    OwnerId = string.Empty,
                    EligibleAt = now,
                    CreatedAt = now,
                    UpdatedAt = now,
                    StartedAt = now
                };
                await dbContext.PlatformBackgroundRuns.AddAsync(run, ct);
                await AppendAsync(run, PlatformBackgroundEventType.Prepared, new { run.Mode, run.Attempt }, ct);
            }
            else
            {
                run.Attempt++;
            }

            run.FailedCount++;
            run.FailureCode = exception.GetType().Name[..Math.Min(exception.GetType().Name.Length, 200)];
            run.OwnerId = string.Empty;
            run.LeaseTokenHash = string.Empty;
            run.LeaseExpiresAt = null;
            run.UpdatedAt = now;
            if (run.Attempt >= run.MaxAttempts)
            {
                run.Status = PlatformBackgroundRunStatus.DeadLetter;
                run.CompletedAt = now;
                run.EligibleAt = now;
                await AppendAsync(run, PlatformBackgroundEventType.Failed,
                    new { run.Attempt, run.MaxAttempts, run.FailureCode, terminal = true }, ct);
            }
            else
            {
                run.Status = PlatformBackgroundRunStatus.RetryScheduled;
                run.EligibleAt = now.AddSeconds(Math.Min(300, 5 * (1 << Math.Min(run.Attempt - 1, 6))));
                await AppendAsync(run, PlatformBackgroundEventType.Retrying,
                    new { run.Attempt, run.MaxAttempts, run.FailureCode, run.EligibleAt }, ct);
            }
            await dbContext.SaveChangesAsync(ct);
            return 0;
        }, cancellationToken);
    }

    public async Task ValidateLeaseAsync(Guid runId, string ownerId, string leaseToken, long leaseVersion, CancellationToken cancellationToken)
    {
        var run = await dbContext.PlatformBackgroundRuns.AsNoTracking().SingleOrDefaultAsync(x => x.Id == runId, cancellationToken)
            ?? throw new InvalidOperationException("Background run is unavailable.");
        ValidateLease(run, ownerId, leaseToken, leaseVersion);
    }

    public async Task CancelAsync(Guid runId, string ownerId, string leaseToken, long leaseVersion, CancellationToken cancellationToken)
    {
        await dbContext.ExecuteInTransactionAsync(async ct =>
        {
            await dbContext.AcquireTransactionLockAsync($"platform-background-run:{runId:D}", ct);
            var run = await dbContext.PlatformBackgroundRuns.Include(x => x.Events).SingleOrDefaultAsync(x => x.Id == runId, ct)
                ?? throw new InvalidOperationException("Background run is unavailable.");
            ValidateLease(run, ownerId, leaseToken, leaseVersion);
            run.Status = PlatformBackgroundRunStatus.Cancelled;
            run.CompletedAt = clock.UtcNow;
            run.UpdatedAt = clock.UtcNow;
            run.OwnerId = string.Empty;
            run.LeaseTokenHash = string.Empty;
            run.LeaseExpiresAt = null;
            await AppendAsync(run, PlatformBackgroundEventType.Cancelled, new { run.Cursor }, ct);
            await dbContext.SaveChangesAsync(ct);
            return 0;
        }, cancellationToken);
    }

    private void ValidateLease(PlatformBackgroundRun run, string ownerId, string leaseToken, long leaseVersion)
    {
        if (run.Status != PlatformBackgroundRunStatus.Running || run.LeaseExpiresAt <= clock.UtcNow ||
            run.LeaseVersion != leaseVersion || !string.Equals(run.OwnerId, ownerId, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(run.LeaseTokenHash) || !FixedEquals(run.LeaseTokenHash, Hash(leaseToken)))
            throw new UnauthorizedAccessException("Background run lease is stale or unavailable.");
    }

    private async Task AppendAsync(PlatformBackgroundRun run, PlatformBackgroundEventType type, object payload, CancellationToken cancellationToken)
    {
        var persisted = await dbContext.PlatformBackgroundEvents.Where(x => x.RunId == run.Id).Select(x => (long?)x.Sequence).MaxAsync(cancellationToken) ?? 0;
        var tracked = run.Events.Count == 0 ? 0 : run.Events.Max(x => x.Sequence);
        var entry = new PlatformBackgroundEvent
        {
            RunId = run.Id,
            Sequence = Math.Max(persisted, tracked) + 1,
            EventType = type,
            PayloadJson = JsonSerializer.Serialize(payload, JsonOptions),
            CreatedAt = clock.UtcNow
        };
        run.Events.Add(entry);
        await dbContext.PlatformBackgroundEvents.AddAsync(entry, cancellationToken);
    }

    private static MonitoringActivityProjection Map(AuthorityOutboxEvent value, long generation, DateTimeOffset now)
    {
        var projection = new MonitoringActivityProjection { OutboxEventId = value.Id };
        Apply(projection, value, generation, now);
        return projection;
    }

    private static void Apply(MonitoringActivityProjection target, AuthorityOutboxEvent source, long generation, DateTimeOffset now)
    {
        target.AuthoritySequence = source.Sequence;
        target.Generation = generation;
        target.TenantId = source.TenantId;
        target.ProjectId = source.ProjectId;
        target.Category = source.Category;
        target.AggregateType = source.AggregateType;
        target.AggregateId = source.AggregateId;
        target.EventType = source.EventType;
        target.AuthorityRevision = source.AuthorityRevision;
        target.SecurityCritical = source.SecurityCritical;
        target.RedactedPayloadJson = source.PayloadJson;
        target.OccurredAt = source.OccurredAt;
        target.ProjectedAt = now;
    }

    private static PlatformProjectionRunResult Result(PlatformBackgroundRun run, MonitoringProjectionState state, string leaseToken, DateTimeOffset now)
        => new(run.Id, run.Status, run.Mode, run.Generation, run.AuthoritySequenceBoundary, run.Cursor,
            run.ExpectedCount, run.ScannedCount, run.CoverageComplete, run.StaleCount, run.DriftCount,
            run.RepairedCount, run.RebuiltCount, run.FailedCount, now - (run.StartedAt ?? now),
            state.LastSuccessAt, state.NextRunAt, run.Status == PlatformBackgroundRunStatus.Running ? leaseToken : string.Empty, run.LeaseVersion);

    private static string Require(string value, string name, int maxLength)
    {
        var normalized = value?.Trim() ?? string.Empty;
        if (normalized.Length == 0 || normalized.Length > maxLength) throw new ArgumentException($"{name} is required.", name);
        return normalized;
    }

    private static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    private static string TenantScopeKey(Guid? tenantId) => tenantId?.ToString("D") ?? "system";
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    private static bool FixedEquals(string left, string right)
        => CryptographicOperations.FixedTimeEquals(Convert.FromHexString(left), Convert.FromHexString(right));
}

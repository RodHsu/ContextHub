using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

/// <summary>
/// Persists and reads the server-owned scheduled-governance reliability
/// projection. The service intentionally has no input for caller-asserted
/// natural origin: until a platform-signed scheduler attestation exists, a
/// receipt whose mode says Scheduled is still non-qualifying.
/// </summary>
public sealed class ScheduledGovernanceReliabilityService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    TimeProvider timeProvider) : IScheduledGovernanceReliabilityService
{
    internal const int MaxGovernanceRunIdLength = 128;

    internal const string NaturalOriginAttestationNotProvenReason =
        "platform-signed-natural-origin-attestation-not-proven";

    internal const string EvidenceBoundary =
        "ContextHub observes server receipt metadata and scheduler timing only; " +
        "it has no platform-signed natural-origin attestation and cannot change " +
        "the host platform timezone configuration.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ScheduledGovernanceReliabilityWindowOptions Options =
        ScheduledGovernanceReliabilityWindowOptions.Default;
    private static readonly ScheduledGovernanceReliabilityWindowCalculator Calculator =
        new(Options);

    public async Task<ScheduledGovernanceReliabilitySummary> ObserveAsync(
        GovernanceRunReceiptResult receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var actor = RequireReadActor();
        var projection = BuildProjection(receipt);
        var now = timeProvider.GetUtcNow();
        var runId = NormalizeRunId(receipt.GovernanceRunId);
        var entity = await dbContext.ScheduledGovernanceReliabilityRuns
            .SingleOrDefaultAsync(x => x.TenantId == actor.TenantId &&
                                       x.OwnerUserId == actor.UserId &&
                                       x.GovernanceRunId == runId,
                cancellationToken);
        var isNewEntity = entity is null;

        if (entity is null)
        {
            entity = CreateEntity(actor, projection, now);
            await dbContext.ScheduledGovernanceReliabilityRuns.AddAsync(entity, cancellationToken);
        }
        else if (receipt.IsReplay)
        {
            AddReplayProjection(entity, receipt.ReceiptId, now);
        }
        else
        {
            ApplyCanonicalProjection(entity, projection, now, preserveReplayMetadata: true);
        }

        await SaveWithConcurrentInsertRecoveryAsync(
            actor,
            receipt,
            projection,
            entity,
            now,
            isNewEntity,
            cancellationToken);
        var rows = await ReadRowsAsync(actor, cancellationToken);
        return BuildSummary(rows);
    }

    public async Task<ScheduledGovernanceReliabilitySummary> GetAsync(
        CancellationToken cancellationToken)
    {
        var actor = RequireReadActor();
        var rows = await ReadRowsAsync(actor, cancellationToken);
        return BuildSummary(rows);
    }

    private async Task SaveWithConcurrentInsertRecoveryAsync(
        ContextHubRequestActor actor,
        GovernanceRunReceiptResult receipt,
        ScheduledGovernanceReliabilityReceiptProjection projection,
        ScheduledGovernanceReliabilityRun entity,
        DateTimeOffset now,
        bool isNewEntity,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            if (!isNewEntity)
            {
                throw;
            }

            // Two run_get requests may observe the same receipt concurrently.
            // Retry only after proving that the unique run row now exists; a
            // missing table, invalid migration, or other database failure is
            // allowed to propagate instead of being hidden as a race.
            dbContext.ClearTrackedChanges();
            var concurrent = await dbContext.ScheduledGovernanceReliabilityRuns
                .SingleOrDefaultAsync(x => x.TenantId == actor.TenantId &&
                                           x.OwnerUserId == actor.UserId &&
                                           x.GovernanceRunId == NormalizeRunId(receipt.GovernanceRunId),
                    cancellationToken);
            if (concurrent is null)
            {
                throw;
            }

            if (receipt.IsReplay)
            {
                AddReplayProjection(concurrent, receipt.ReceiptId, now);
            }
            else
            {
                ApplyCanonicalProjection(concurrent, projection, now, preserveReplayMetadata: true);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private async Task<IReadOnlyList<ScheduledGovernanceReliabilityRun>> ReadRowsAsync(
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
        => await dbContext.ScheduledGovernanceReliabilityRuns
            .AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
            .OrderBy(x => x.ObservedAtUtc)
            .ThenBy(x => x.GovernanceRunId)
            .ToArrayAsync(cancellationToken);

    private ScheduledGovernanceReliabilitySummary BuildSummary(
        IReadOnlyList<ScheduledGovernanceReliabilityRun> rows)
    {
        var projections = new List<ScheduledGovernanceReliabilityReceiptProjection>(rows.Count * 2);
        foreach (var row in rows)
        {
            var projection = DeserializeProjection(row);
            if (projection is null)
            {
                projection = CorruptProjection(row);
            }

            projection = projection with
            {
                GovernanceRunId = NormalizeRunId(row.GovernanceRunId),
                ReceiptId = row.ReceiptId,
                IsReplay = row.IsReplay
            };
            projections.Add(projection);

            foreach (var replayReceiptId in DeserializeReplayReceiptIds(row.ReplayReceiptIdsJson))
            {
                projections.Add(projection with
                {
                    ReceiptId = replayReceiptId,
                    IsReplay = true
                });
            }
        }

        var result = Calculator.Calculate(projections);
        return ToSummary(result);
    }

    private static ScheduledGovernanceReliabilitySummary ToSummary(
        ScheduledGovernanceReliabilityWindowResult result)
    {
        var schedule = new ScheduledGovernanceReliabilityScheduleResult(
            result.Schedule.IntendedTimeZoneId,
            result.Schedule.SchedulerTimeZoneId,
            result.Schedule.Cadence,
            result.Schedule.IntendedLocalRunTimes,
            result.Schedule.SchedulerLocalRunTimes,
            result.Schedule.CompensationDescription);
        var naturalOrigin = new ScheduledGovernanceNaturalOriginEvidenceResult(
            PlatformSignedAttestationAvailable: false,
            Status: "Unattested",
            EvidenceBoundary);

        return new ScheduledGovernanceReliabilitySummary(
            result.RequiredRuns,
            result.ConsecutiveQualifyingRuns,
            result.GatePassed,
            result.FirstQualifyingAtUtc,
            result.LatestQualifyingAtUtc,
            result.FirstQualifyingGovernanceRunId,
            result.LatestQualifyingGovernanceRunId,
            result.Runs.Select(ToRunResult).ToArray(),
            result.QualifyingRuns.Select(ToRunResult).ToArray(),
            result.NonQualifyingRuns.Select(ToRunResult).ToArray(),
            result.FailedRuns.Select(ToRunResult).ToArray(),
            result.ResetEvents.Select(x => new ScheduledGovernanceReliabilityResetResult(
                x.AtUtc,
                x.Reason,
                x.GovernanceRunId,
                x.PreviousConsecutiveQualifyingRuns)).ToArray(),
            result.IgnoredManualRunCount,
            result.IgnoredReplayProjectionCount,
            result.IgnoredNonScheduledRunCount,
            schedule,
            result.MaximumAbsoluteDrift,
            result.LatestSignedDrift,
            naturalOrigin,
            HasRelevantDeploymentOrConfigurationReset(result),
            result.LastResetEvent?.Reason);
    }

    private static ScheduledGovernanceReliabilityRunResult ToRunResult(
        ScheduledGovernanceReliabilityRunEvidence evidence)
    {
        var mode = evidence.ExecutionMode?.Trim() ?? string.Empty;
        var naturalOriginStatus = IsScheduled(mode)
            ? "Unattested"
            : IsManual(mode) ? "Manual" : "NotScheduled";
        return new ScheduledGovernanceReliabilityRunResult(
            evidence.GovernanceRunId,
            evidence.ReceiptId,
            mode,
            evidence.ObservedAtUtc,
            evidence.ExpectedAtUtc,
            evidence.SignedDrift,
            evidence.AbsoluteDrift,
            evidence.DriftWithinTolerance,
            evidence.CountedTowardGate,
            evidence.Qualifies,
            evidence.IsIgnored,
            evidence.IsFailed,
            PublicReasons(evidence),
            naturalOriginStatus,
            PlatformSignedNaturalOriginAttested: false,
            EvidenceBoundary);
    }

    private static bool HasRelevantDeploymentOrConfigurationReset(
        ScheduledGovernanceReliabilityWindowResult result)
    {
        if (result.ResetEvents.Any(x => string.Equals(
                x.Reason,
                "relevant-deployment-or-configuration-change",
                StringComparison.Ordinal)))
        {
            return true;
        }

        var relevantReasons = new HashSet<string>(StringComparer.Ordinal)
        {
            "reliability-baseline-changed",
            "reliability-baseline-mismatch",
            "contract-version-mismatch",
            "schema-hash-mismatch",
            "catalog-version-mismatch",
            "runtime-identity-mismatch"
        };
        return result.NonQualifyingRuns.Any(x => x.Reasons.Any(relevantReasons.Contains));
    }

    internal static ScheduledGovernanceReliabilityReceiptProjection BuildProjection(
        GovernanceRunReceiptResult receipt)
    {
        var projection = ScheduledGovernanceReliabilityReceiptProjection.FromReceipt(
            receipt,
            ScheduledGovernanceContract.RuntimeIdentity) with
        {
            GovernanceRunId = NormalizeRunId(receipt.GovernanceRunId)
        };
        if (IsManual(receipt.ExecutionMode))
        {
            return projection;
        }

        // Observation occurs only through the dedicated scheduled-governance
        // application service. That proves the least-privilege surface, but
        // not whether ChatGPT invoked it naturally or interactively. Model it
        // as a scheduled-surface candidate and explicitly fail the natural
        // origin gate until the host provides signed attestation.
        return projection with
        {
            ExecutionMode = "Scheduled",
            ResetReason = NaturalOriginAttestationNotProvenReason
        };
    }

    private static ScheduledGovernanceReliabilityRun CreateEntity(
        ContextHubRequestActor actor,
        ScheduledGovernanceReliabilityReceiptProjection projection,
        DateTimeOffset now)
    {
        var entity = new ScheduledGovernanceReliabilityRun
        {
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            GovernanceRunId = NormalizeRunId(projection.GovernanceRunId),
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyCanonicalProjection(entity, projection, now, preserveReplayMetadata: false);
        if (projection.IsReplay)
        {
            entity.IsReplay = true;
            entity.ReplayReceiptIdsJson = SerializeReplayReceiptIds([projection.ReceiptId]);
            entity.ReplayProjectionCount = 1;
        }

        return entity;
    }

    private static void ApplyCanonicalProjection(
        ScheduledGovernanceReliabilityRun entity,
        ScheduledGovernanceReliabilityReceiptProjection projection,
        DateTimeOffset now,
        bool preserveReplayMetadata)
    {
        // RuntimeIdentity and BaselineIdentity are capture-time evidence. A
        // later receipt event may update phase/result fields, but it must not
        // relabel the original observation with the current deployment.
        var storedProjection = PreserveCapturedIdentity(
            DeserializeProjection(entity),
            projection) with
        {
            GovernanceRunId = NormalizeRunId(projection.GovernanceRunId),
            IsReplay = false
        };
        var evidence = Calculator.Calculate([storedProjection]).Runs.FirstOrDefault();
        var schedule = Options;
        entity.ReceiptId = projection.ReceiptId;
        entity.ExecutionMode = projection.ExecutionMode?.Trim() ?? string.Empty;
        entity.IsReplay = false;
        entity.ObservedAtUtc = ObservedAt(storedProjection);
        entity.ExpectedAtUtc = evidence?.ExpectedAtUtc;
        entity.SignedDriftTicks = evidence?.SignedDrift?.Ticks;
        entity.AbsoluteDriftTicks = evidence?.AbsoluteDrift?.Ticks;
        entity.DriftWithinTolerance = evidence?.DriftWithinTolerance;
        entity.CountedTowardGate = evidence?.CountedTowardGate ?? false;
        entity.Qualifies = evidence?.Qualifies ?? false;
        entity.IsIgnored = evidence?.IsIgnored ?? false;
        entity.IsFailed = evidence?.IsFailed ?? false;
        entity.NaturalOriginStatus = IsScheduled(entity.ExecutionMode)
            ? "Unattested"
            : IsManual(entity.ExecutionMode) ? "Manual" : "NotScheduled";
        entity.PlatformSignedNaturalOriginAttested = false;
        entity.EvidenceBoundary = EvidenceBoundary;
        entity.ReasonsJson = JsonSerializer.Serialize(
            evidence is null ? [] : PublicReasons(evidence),
            JsonOptions);
        entity.ProjectionJson = JsonSerializer.Serialize(storedProjection, JsonOptions);
        entity.IntendedTimeZoneId = schedule.IntendedTimeZoneId;
        entity.SchedulerTimeZoneId = schedule.SchedulerTimeZoneId;
        entity.CadenceTicks = schedule.Cadence.Ticks;
        entity.IntendedLocalRunTimesJson = JsonSerializer.Serialize(schedule.IntendedLocalRunTimes, JsonOptions);
        entity.SchedulerLocalRunTimesJson = JsonSerializer.Serialize(schedule.SchedulerLocalRunTimes, JsonOptions);
        entity.CompensationDescription = BuildCompensationDescription(schedule);
        entity.ResetReason = storedProjection.ResetReason;
        entity.UpdatedAt = now;
        if (!preserveReplayMetadata)
        {
            entity.ReplayProjectionCount = 0;
            entity.ReplayReceiptIdsJson = "[]";
        }
    }

    internal static ScheduledGovernanceReliabilityReceiptProjection PreserveCapturedIdentity(
        ScheduledGovernanceReliabilityReceiptProjection? persisted,
        ScheduledGovernanceReliabilityReceiptProjection incoming)
    {
        ArgumentNullException.ThrowIfNull(incoming);
        if (persisted is null)
        {
            return incoming;
        }

        return incoming with
        {
            RuntimeIdentity = persisted.RuntimeIdentity ?? incoming.RuntimeIdentity,
            BaselineIdentity = persisted.BaselineIdentity ?? incoming.BaselineIdentity
        };
    }

    private static void AddReplayProjection(
        ScheduledGovernanceReliabilityRun entity,
        Guid receiptId,
        DateTimeOffset now)
    {
        var ids = DeserializeReplayReceiptIds(entity.ReplayReceiptIdsJson);
        if (receiptId != Guid.Empty && !ids.Contains(receiptId))
        {
            ids.Add(receiptId);
        }

        entity.ReplayReceiptIdsJson = SerializeReplayReceiptIds(ids);
        entity.ReplayProjectionCount = ids.Count;
        entity.UpdatedAt = now;
    }

    private static ScheduledGovernanceReliabilityReceiptProjection? DeserializeProjection(
        ScheduledGovernanceReliabilityRun entity)
    {
        if (string.IsNullOrWhiteSpace(entity.ProjectionJson))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<ScheduledGovernanceReliabilityReceiptProjection>(
                entity.ProjectionJson,
                JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static ScheduledGovernanceReliabilityReceiptProjection CorruptProjection(
        ScheduledGovernanceReliabilityRun entity)
        => new()
        {
            ReceiptId = entity.ReceiptId,
            GovernanceRunId = NormalizeRunId(entity.GovernanceRunId),
            StartedAt = entity.ObservedAtUtc,
            CompletedAt = entity.ObservedAtUtc,
            ObservedAtUtc = entity.ObservedAtUtc,
            ExpectedAtUtc = entity.ExpectedAtUtc,
            ExecutionMode = entity.ExecutionMode,
            IsReplay = entity.IsReplay,
            RunExists = false,
            Terminal = false,
            Status = "Corrupt",
            ResetReason = "reliability-evidence-corrupt"
        };

    private static List<Guid> DeserializeReplayReceiptIds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<Guid>>(value, JsonOptions) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string SerializeReplayReceiptIds(IEnumerable<Guid> ids)
        => JsonSerializer.Serialize(ids.Where(x => x != Guid.Empty).Distinct().ToArray(), JsonOptions);

    private static IReadOnlyList<string> PublicReasons(
        ScheduledGovernanceReliabilityRunEvidence evidence)
    {
        var reasons = evidence.Reasons.ToList();
        if (IsScheduled(evidence.ExecutionMode) &&
            !reasons.Contains(NaturalOriginAttestationNotProvenReason, StringComparer.Ordinal))
        {
            reasons.Insert(0, NaturalOriginAttestationNotProvenReason);
        }

        return reasons.Distinct(StringComparer.Ordinal).ToArray();
    }

    private ContextHubRequestActor RequireReadActor()
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.ScheduledGovernance);
        if (!actor.IsAdmin)
        {
            throw new UnauthorizedAccessException(
                "Scheduled governance reliability requires a tenant owner or administrator.");
        }

        return actor;
    }

    internal static string NormalizeRunId(string? governanceRunId)
    {
        var normalized = governanceRunId?.Trim() ?? string.Empty;
        if (normalized.Length > MaxGovernanceRunIdLength)
        {
            throw new ArgumentException(
                $"GovernanceRunId must be at most {MaxGovernanceRunIdLength} characters.",
                nameof(governanceRunId));
        }

        return normalized;
    }

    private static DateTimeOffset ObservedAt(
        ScheduledGovernanceReliabilityReceiptProjection projection)
        => projection.ObservedAtUtc ??
           (projection.StartedAt != default ? projection.StartedAt : projection.CompletedAt);

    private static bool IsScheduled(string? executionMode)
        => string.Equals(executionMode?.Trim(), "Scheduled", StringComparison.OrdinalIgnoreCase);

    private static bool IsManual(string? executionMode)
        => string.Equals(executionMode?.Trim(), "Manual", StringComparison.OrdinalIgnoreCase) ||
           string.Equals(executionMode?.Trim(), "Interactive", StringComparison.OrdinalIgnoreCase);

    private static string BuildCompensationDescription(
        ScheduledGovernanceReliabilityWindowOptions options)
        => $"scheduler {options.SchedulerTimeZoneId} " +
           $"{string.Join('/', options.SchedulerLocalRunTimes.Select(x => x.ToString("HH:mm")))} " +
           $"=> intended {options.IntendedTimeZoneId} " +
           string.Join('/', options.IntendedLocalRunTimes.Select(x => x.ToString("HH:mm")));
}

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
    TimeProvider timeProvider,
    IScheduledGovernanceReliabilityEvidenceProvider? evidenceProvider = null,
    IScheduledGovernanceServerSafetyEvidenceProvider? serverSafetyEvidenceProvider = null,
    IScheduledGovernanceNaturalOriginAuthorityProvider? naturalOriginAuthorityProvider = null) : IScheduledGovernanceReliabilityService
{
    internal const int MaxGovernanceRunIdLength = 128;

    internal const string NaturalOriginAttestationNotProvenReason =
        "platform-signed-natural-origin-attestation-not-proven";

    internal const string EvidenceBoundary =
        "Reliability qualification requires server-verified platform-signed natural-origin " +
        "attestation AND authenticated immutable control-plane audit with exact actor, " +
        "scope, run, slot, request, receipt, audience, contract and runtime binding. " +
        "Timing is a consistency check only; missing or ambiguous evidence remains " +
        "Unattested, and ContextHub cannot change the host platform timezone configuration.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly ScheduledGovernanceReliabilityWindowOptions Options =
        ScheduledGovernanceReliabilityWindowOptions.Default;
    internal static readonly string CurrentScheduleDigest =
        ComputeScheduleDigest(Options);

    public async Task<ScheduledGovernanceReliabilitySummary> ObserveAsync(
        GovernanceRunReceiptResult receipt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        var actor = RequireReadActor();
        var current = CreateCurrentAuthorityContext();
        var projection = await BuildProjectionAsync(
            actor,
            receipt,
            current.Authority,
            cancellationToken);
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
            entity = CreateEntity(actor, projection, now, current.Calculator);
            await dbContext.ScheduledGovernanceReliabilityRuns.AddAsync(entity, cancellationToken);
        }
        else if (receipt.IsReplay)
        {
            AddReplayProjection(entity, receipt.ReceiptId, now);
        }
        else
        {
            ApplyCanonicalProjectionIfNewer(
                entity,
                projection,
                now,
                current.Calculator,
                preserveReplayMetadata: true);
        }

        await SaveWithConcurrentInsertRecoveryAsync(
            actor,
            receipt,
            projection,
            entity,
            now,
            isNewEntity,
            current.Calculator,
            cancellationToken);
        var rows = await ReadRowsAsync(actor, cancellationToken);
        return await BuildSummaryAsync(actor, rows, current.Calculator, cancellationToken);
    }

    public async Task<ScheduledGovernanceReliabilitySummary> GetAsync(
        CancellationToken cancellationToken)
    {
        var actor = RequireReadActor();
        var current = CreateCurrentAuthorityContext();
        var rows = await ReadRowsAsync(actor, cancellationToken);
        return await BuildSummaryAsync(actor, rows, current.Calculator, cancellationToken);
    }

    private async Task<ScheduledGovernanceReliabilityReceiptProjection> BuildProjectionAsync(
        ContextHubRequestActor actor,
        GovernanceRunReceiptResult receipt,
        ScheduledGovernanceNaturalOriginAuthority? authority,
        CancellationToken cancellationToken)
    {
        var serverSafetyEvidence = await GetServerSafetyEvidenceAsync(
            actor,
            receipt.ReceiptId,
            receipt.GovernanceRunId,
            cancellationToken);
        var evidence = await GetNaturalOriginEvidenceAsync(
            actor,
            receipt.ReceiptId,
            receipt.GovernanceRunId,
            receipt.ProjectIds,
            serverSafetyEvidence?.ReviewRequestIdentityHash,
            cancellationToken);

        return BuildProjection(receipt, actor, evidence, serverSafetyEvidence, authority);
    }

    private async Task<ScheduledGovernanceServerSafetyEvidenceSnapshot?> GetServerSafetyEvidenceAsync(
        ContextHubRequestActor actor,
        Guid receiptId,
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        if (serverSafetyEvidenceProvider is null ||
            !actor.TenantId.HasValue ||
            !actor.UserId.HasValue ||
            receiptId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return await serverSafetyEvidenceProvider.GetAsync(
                new ScheduledGovernanceServerSafetyEvidenceQuery(
                    actor.TenantId.Value,
                    actor.UserId.Value,
                    receiptId,
                    NormalizeRunId(governanceRunId)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // A read or integrity failure is unknown evidence, never success.
            return null;
        }
    }

    private async Task<ScheduledGovernanceReliabilityEvidenceSnapshot?> GetNaturalOriginEvidenceAsync(
        ContextHubRequestActor actor,
        Guid receiptId,
        string governanceRunId,
        IReadOnlyList<string> projectIds,
        string? reviewRequestIdentityHash,
        CancellationToken cancellationToken)
    {
        if (evidenceProvider is null || !actor.TenantId.HasValue || !actor.UserId.HasValue)
        {
            return null;
        }

        var runId = NormalizeRunId(governanceRunId);
        var projectScopeHash = ScheduledGovernanceReliabilityEvidenceContract
            .ComputeProjectScopeHash(projectIds);
        if (receiptId == Guid.Empty ||
            string.IsNullOrWhiteSpace(projectScopeHash) ||
            string.IsNullOrWhiteSpace(reviewRequestIdentityHash))
        {
            return null;
        }

        try
        {
            return await evidenceProvider.GetAsync(
                new ScheduledGovernanceReliabilityEvidenceQuery(
                    actor.TenantId.Value,
                    actor.UserId.Value,
                    receiptId,
                    runId,
                    reviewRequestIdentityHash,
                    projectScopeHash),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Evidence-provider outage, malformed data, or an ambiguous
            // verification result must never promote timing-only evidence.
            return null;
        }
    }

    private async Task SaveWithConcurrentInsertRecoveryAsync(
        ContextHubRequestActor actor,
        GovernanceRunReceiptResult receipt,
        ScheduledGovernanceReliabilityReceiptProjection projection,
        ScheduledGovernanceReliabilityRun entity,
        DateTimeOffset now,
        bool isNewEntity,
        ScheduledGovernanceReliabilityWindowCalculator calculator,
        CancellationToken cancellationToken)
    {
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException) when (!isNewEntity)
        {
            // ReceiptEventSequence is an optimistic-concurrency token. One
            // bounded reload prevents an older receipt projection from
            // winning a cross-instance race; a second conflict propagates.
            dbContext.ClearTrackedChanges();
            var concurrent = await LoadRunAsync(actor, receipt.GovernanceRunId, cancellationToken);
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
                ApplyCanonicalProjectionIfNewer(
                    concurrent,
                    projection,
                    now,
                    calculator,
                    preserveReplayMetadata: true);
            }

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
            var concurrent = await LoadRunAsync(actor, receipt.GovernanceRunId, cancellationToken);
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
                ApplyCanonicalProjectionIfNewer(
                    concurrent,
                    projection,
                    now,
                    calculator,
                    preserveReplayMetadata: true);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    private Task<ScheduledGovernanceReliabilityRun?> LoadRunAsync(
        ContextHubRequestActor actor,
        string governanceRunId,
        CancellationToken cancellationToken)
        => dbContext.ScheduledGovernanceReliabilityRuns.SingleOrDefaultAsync(
            x => x.TenantId == actor.TenantId &&
                 x.OwnerUserId == actor.UserId &&
                 x.GovernanceRunId == NormalizeRunId(governanceRunId),
            cancellationToken);

    private async Task<IReadOnlyList<ScheduledGovernanceReliabilityRun>> ReadRowsAsync(
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
        => await dbContext.ScheduledGovernanceReliabilityRuns
            .AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId)
            .OrderBy(x => x.ObservedAtUtc)
            .ThenBy(x => x.GovernanceRunId)
            .ToArrayAsync(cancellationToken);

    private async Task<ScheduledGovernanceReliabilitySummary> BuildSummaryAsync(
        ContextHubRequestActor actor,
        IReadOnlyList<ScheduledGovernanceReliabilityRun> rows,
        ScheduledGovernanceReliabilityWindowCalculator calculator,
        CancellationToken cancellationToken)
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
                TenantId = row.TenantId,
                OwnerUserId = row.OwnerUserId,
                GovernanceRunId = NormalizeRunId(row.GovernanceRunId),
                ReceiptId = row.ReceiptId,
                ReceiptEventSequence = row.ReceiptEventSequence,
                IsReplay = row.IsReplay
            };
            if (IsScheduled(projection.ExecutionMode))
            {
                var serverSafetyEvidence = await GetServerSafetyEvidenceAsync(
                    actor,
                    projection.ReceiptId,
                    projection.GovernanceRunId,
                    cancellationToken);
                var evidence = await GetNaturalOriginEvidenceAsync(
                    actor,
                    projection.ReceiptId,
                    projection.GovernanceRunId,
                    projection.ProjectIds,
                    serverSafetyEvidence?.ReviewRequestIdentityHash,
                    cancellationToken);
                projection = projection with
                {
                    NaturalOriginEvidence = evidence,
                    RequestIdentityHash = serverSafetyEvidence?.ReviewRequestIdentityHash ?? string.Empty,
                    RuntimeIdentity = serverSafetyEvidence?.CapturedRuntimeIdentity ?? projection.RuntimeIdentity,
                    InitialReviewReceived = serverSafetyEvidence?.InitialReviewReceived,
                    ReceiptEventSequence = serverSafetyEvidence?.ReceiptEventSequence ??
                                           projection.ReceiptEventSequence,
                    CountInvariantSatisfied = serverSafetyEvidence?.CountInvariantSatisfied,
                    DecisionObeyed = serverSafetyEvidence?.DecisionObeyed,
                    NoGeneralConnectorFallback = serverSafetyEvidence?.NoGeneralConnectorFallback,
                    NoUnauthorizedMutation = serverSafetyEvidence?.NoUnauthorizedMutation,
                    NoDuplicateMutation = serverSafetyEvidence?.NoDuplicateMutation,
                    DisplayNameUnchanged = serverSafetyEvidence?.DisplayNameUnchanged,
                    BusinessWorkItemsUntouched = serverSafetyEvidence?.BusinessWorkItemsUntouched,
                    HostDispatchCompleted = serverSafetyEvidence?.HostDispatchCompleted,
                    ImmutableSnapshotBound = serverSafetyEvidence?.ImmutableSnapshotBound,
                    FixedReversibleExecutorUsed = serverSafetyEvidence?.FixedReversibleExecutorUsed,
                    ResetReason = evidence is null ||
                                  !string.Equals(
                                      projection.ResetReason,
                                      NaturalOriginAttestationNotProvenReason,
                                      StringComparison.Ordinal)
                        ? projection.ResetReason
                        : null
                };
            }
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

        var result = calculator.Calculate(projections);
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
        var latestScheduledRun = result.Runs
            .LastOrDefault(x => IsScheduled(x.ExecutionMode));
        var naturalOrigin = new ScheduledGovernanceNaturalOriginEvidenceResult(
            PlatformSignedAttestationAvailable: latestScheduledRun?.PlatformSignedNaturalOriginAttested ?? false,
            Status: latestScheduledRun?.NaturalOriginStatus ?? "Unattested",
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
        var naturalOriginStatus = evidence.NaturalOriginStatus;
        if (string.IsNullOrWhiteSpace(naturalOriginStatus))
        {
            naturalOriginStatus = IsScheduled(mode)
                ? "Unattested"
                : IsManual(mode) ? "Manual" : "NotScheduled";
        }
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
            PlatformSignedNaturalOriginAttested: evidence.PlatformSignedNaturalOriginAttested,
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
            "runtime-identity-mismatch",
            "natural-origin-authority-baseline-not-proven",
            "natural-origin-authority-baseline-mismatch"
        };
        return result.NonQualifyingRuns.Any(x => x.Reasons.Any(relevantReasons.Contains));
    }

    internal static ScheduledGovernanceReliabilityReceiptProjection BuildProjection(
        GovernanceRunReceiptResult receipt)
        => BuildProjection(
            receipt,
            actor: null,
            naturalOriginEvidence: null,
            serverSafetyEvidence: null,
            authority: null);

    internal static ScheduledGovernanceReliabilityReceiptProjection BuildProjection(
        GovernanceRunReceiptResult receipt,
        ContextHubRequestActor? actor,
        ScheduledGovernanceReliabilityEvidenceSnapshot? naturalOriginEvidence,
        ScheduledGovernanceServerSafetyEvidenceSnapshot? serverSafetyEvidence = null,
        ScheduledGovernanceNaturalOriginAuthority? authority = null)
    {
        var capturedRuntimeIdentity = IsManual(receipt.ExecutionMode)
            ? ScheduledGovernanceContract.RuntimeIdentity
            : serverSafetyEvidence?.CapturedRuntimeIdentity;
        var baseProjection = ScheduledGovernanceReliabilityReceiptProjection.FromReceipt(
            receipt,
            capturedRuntimeIdentity);
        var projection = baseProjection with
        {
            TenantId = actor?.TenantId,
            OwnerUserId = actor?.UserId,
            GovernanceRunId = NormalizeRunId(receipt.GovernanceRunId),
            ReceiptEventSequence = serverSafetyEvidence?.ReceiptEventSequence,
            RequestIdentityHash = IsManual(receipt.ExecutionMode)
                ? receipt.RequestIdentityHash
                : serverSafetyEvidence?.ReviewRequestIdentityHash ?? string.Empty,
            InitialReviewReceived = IsManual(receipt.ExecutionMode)
                ? !string.IsNullOrWhiteSpace(receipt.InitialSnapshotToken)
                : serverSafetyEvidence?.InitialReviewReceived,
            BaselineIdentity = IsManual(receipt.ExecutionMode)
                ? baseProjection.BaselineIdentity
                : ScheduledGovernanceReliabilityReceiptProjection.BindAuthorityEpoch(
                    baseProjection.BaselineIdentity,
                    authority?.AuthorityEpochDigest),
            NaturalOriginEvidence = naturalOriginEvidence,
            CountInvariantSatisfied = serverSafetyEvidence?.CountInvariantSatisfied,
            DecisionObeyed = serverSafetyEvidence?.DecisionObeyed,
            NoGeneralConnectorFallback = serverSafetyEvidence?.NoGeneralConnectorFallback,
            NoUnauthorizedMutation = serverSafetyEvidence?.NoUnauthorizedMutation,
            NoDuplicateMutation = serverSafetyEvidence?.NoDuplicateMutation,
            DisplayNameUnchanged = serverSafetyEvidence?.DisplayNameUnchanged,
            BusinessWorkItemsUntouched = serverSafetyEvidence?.BusinessWorkItemsUntouched,
            HostDispatchCompleted = serverSafetyEvidence?.HostDispatchCompleted,
            ImmutableSnapshotBound = serverSafetyEvidence?.ImmutableSnapshotBound,
            FixedReversibleExecutorUsed = serverSafetyEvidence?.FixedReversibleExecutorUsed,
            ExpectedAtUtc = naturalOriginEvidence?.PlatformAttestation?.Binding.ExpectedAtUtc ??
                            naturalOriginEvidence?.ControlPlaneAudit?.Binding.ExpectedAtUtc
        };
        if (IsManual(receipt.ExecutionMode))
        {
            return projection;
        }

        // Observation occurs only through the dedicated scheduled-governance
        // application service. That proves the least-privilege surface, but
        // not whether ChatGPT invoked it naturally or interactively. The
        // trusted provider is the only source that can satisfy the natural
        // origin gate; timing and the Scheduled mode are never sufficient.
        return projection with
        {
            ExecutionMode = "Scheduled",
            ResetReason = naturalOriginEvidence is null
                ? NaturalOriginAttestationNotProvenReason
                : null
        };
    }

    private static ScheduledGovernanceReliabilityRun CreateEntity(
        ContextHubRequestActor actor,
        ScheduledGovernanceReliabilityReceiptProjection projection,
        DateTimeOffset now,
        ScheduledGovernanceReliabilityWindowCalculator calculator)
    {
        var entity = new ScheduledGovernanceReliabilityRun
        {
            TenantId = actor.TenantId!.Value,
            OwnerUserId = actor.UserId!.Value,
            GovernanceRunId = NormalizeRunId(projection.GovernanceRunId),
            CreatedAt = now,
            UpdatedAt = now
        };
        ApplyCanonicalProjection(
            entity,
            projection,
            now,
            calculator,
            preserveReplayMetadata: false);
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
        ScheduledGovernanceReliabilityWindowCalculator calculator,
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
        var evidence = calculator.Calculate([storedProjection]).Runs.FirstOrDefault();
        var schedule = Options;
        entity.ReceiptId = projection.ReceiptId;
        entity.ReceiptEventSequence = projection.ReceiptEventSequence;
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
        entity.NaturalOriginStatus = evidence?.NaturalOriginStatus ??
            (IsScheduled(entity.ExecutionMode)
                ? "Unattested"
                : IsManual(entity.ExecutionMode) ? "Manual" : "NotScheduled");
        entity.PlatformSignedNaturalOriginAttested =
            evidence?.PlatformSignedNaturalOriginAttested ?? false;
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
            BaselineIdentity = persisted.BaselineIdentity ?? incoming.BaselineIdentity,
            NaturalOriginEvidence = incoming.NaturalOriginEvidence
        };
    }

    private static void ApplyCanonicalProjectionIfNewer(
        ScheduledGovernanceReliabilityRun entity,
        ScheduledGovernanceReliabilityReceiptProjection projection,
        DateTimeOffset now,
        ScheduledGovernanceReliabilityWindowCalculator calculator,
        bool preserveReplayMetadata)
    {
        if (!ShouldApplyCanonicalProjection(
                entity.ReceiptEventSequence,
                entity.ReceiptId,
                projection.ReceiptEventSequence,
                projection.ReceiptId))
        {
            return;
        }

        ApplyCanonicalProjection(entity, projection, now, calculator, preserveReplayMetadata);
    }

    internal static bool ShouldApplyCanonicalProjection(
        long? storedReceiptEventSequence,
        Guid storedReceiptId,
        long? incomingReceiptEventSequence,
        Guid incomingReceiptId)
    {
        if (!incomingReceiptEventSequence.HasValue)
        {
            return !storedReceiptEventSequence.HasValue && storedReceiptId == incomingReceiptId;
        }

        if (!storedReceiptEventSequence.HasValue ||
            incomingReceiptEventSequence.Value > storedReceiptEventSequence.Value)
        {
            return true;
        }

        return incomingReceiptEventSequence.Value == storedReceiptEventSequence.Value &&
               storedReceiptId == incomingReceiptId;
    }

    private CurrentAuthorityContext CreateCurrentAuthorityContext()
    {
        ScheduledGovernanceNaturalOriginAuthority? authority = null;
        try
        {
            var snapshot = naturalOriginAuthorityProvider?.GetCurrent();
            if (snapshot is not null && IsCurrentScheduleDigest(snapshot.ScheduleDigest))
            {
                authority = new ScheduledGovernanceNaturalOriginAuthority(
                    snapshot.PlatformIssuer,
                    snapshot.ControlPlaneSourceSystem,
                    snapshot.Environment,
                    snapshot.TaskBindingHash,
                    snapshot.AutomationBindingHash,
                    snapshot.ScheduleDigest,
                    ComputeConfigurationDigest(
                        snapshot.ConfigurationDigest,
                        snapshot.AuthorityEpochDigest),
                    snapshot.AuthorityEpochDigest);
                authority.Validate();
            }
        }
        catch
        {
            // Configuration/provider failures are deliberately represented as
            // unavailable authority. The calculator then fails closed.
            authority = null;
        }

        var calculator = new ScheduledGovernanceReliabilityWindowCalculator(Options with
        {
            ExpectedNaturalOriginAuthority = authority
        });
        return new CurrentAuthorityContext(authority, calculator);
    }

    private static string ComputeScheduleDigest(ScheduledGovernanceReliabilityWindowOptions options)
        => ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(string.Join('|',
            "scheduled-governance-schedule-v1",
            options.IntendedTimeZoneId,
            options.SchedulerTimeZoneId,
            options.Cadence.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture),
            string.Join(',', options.IntendedLocalRunTimes.Select(value => value.ToString("HH:mm"))),
            string.Join(',', options.SchedulerLocalRunTimes.Select(value => value.ToString("HH:mm")))));

    internal static bool IsCurrentScheduleDigest(string? value)
        => string.Equals(value, CurrentScheduleDigest, StringComparison.Ordinal);

    internal const string ConfigurationDigestBindingVersion =
        "scheduled-governance-configuration-v1";

    internal static string ComputeConfigurationDigest(
        string? baseConfigurationDigest,
        string? authorityEpochDigest)
    {
        if (!IsDigest(baseConfigurationDigest) || !IsDigest(authorityEpochDigest))
        {
            throw new ArgumentException(
                "Natural-origin configuration digest binding is incomplete or malformed.");
        }

        return ScheduledGovernanceReliabilityEvidenceContract.ComputeOpaqueHash(string.Join(
            '|',
            ConfigurationDigestBindingVersion,
            baseConfigurationDigest,
            authorityEpochDigest));
    }

    private static bool IsDigest(string? value)
        => value is { Length: 64 } &&
           value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f');

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
            !string.Equals(evidence.NaturalOriginStatus, "Verified", StringComparison.Ordinal) &&
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

    private sealed record CurrentAuthorityContext(
        ScheduledGovernanceNaturalOriginAuthority? Authority,
        ScheduledGovernanceReliabilityWindowCalculator Calculator);
}

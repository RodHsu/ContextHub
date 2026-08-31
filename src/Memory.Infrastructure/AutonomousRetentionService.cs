using System.Data;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class AutonomousRetentionService(
    MemoryDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    ICacheVersionStore cacheStore,
    IOptions<AutonomousGovernanceOptions> options,
    TimeProvider timeProvider) : IAutonomousRetentionService
{
    private const string ResourceType = "Memory";
    private const string EligibleStatus = "Eligible";
    private const string QuarantinedStatus = "Quarantined";
    private const string CancelledStatus = "Cancelled";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AutonomousGovernanceOptions _options = options.Value;

    public async Task<AutonomousRetentionReviewResult> ReviewAsync(
        IReadOnlyList<string> projectIds,
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var tenantId = actor.TenantId!.Value;
        var ownerUserId = actor.UserId!.Value;
        var projects = projectIds.Select(x => ProjectContext.Normalize(x))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ActorAuthorization.EnsureProjectsAllowed(actor, projects, write: false);

        var memories = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && projects.Contains(x.ProjectId))
            .OrderBy(x => x.ProjectId).ThenBy(x => x.Id)
            .ToListAsync(cancellationToken);
        var ids = memories.Select(x => x.Id).ToArray();
        var states = ids.Length == 0
            ? new Dictionary<Guid, MemoryRetentionState>()
            : await dbContext.MemoryRetentionStates
                .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && ids.Contains(x.ResourceId))
                .ToDictionaryAsync(x => x.ResourceId, cancellationToken);
        var evidence = await LoadEvidenceAsync(ids, tenantId, ownerUserId, projects, cancellationToken);
        var now = timeProvider.GetUtcNow();
        var candidates = new List<AutonomousRetentionCandidateResult>();
        var deleteCancelled = 0;
        var protectedRetention = 0;

        foreach (var memory in memories)
        {
            var policy = ResolvePolicy(memory);
            var evaluation = Evaluate(memory, policy, evidence, now);
            states.TryGetValue(memory.Id, out var state);

            if (policy.Protected)
            {
                protectedRetention++;
            }

            if (state is not null)
            {
                var policyChanged = !string.Equals(state.PolicyVersion, _options.RetentionPolicyVersion, StringComparison.Ordinal);
                var wasScheduled = state.DeleteEligibleAt.HasValue;
                ApplyEvaluation(state, memory, policy, evaluation, governanceRunId, now, policyChanged);
                if (wasScheduled && !state.DeleteEligibleAt.HasValue)
                {
                    deleteCancelled++;
                }
            }

            if (state is null && evaluation.BlockedReasons.Count == 0 && policy.AutoDelete &&
                memory.Status is MemoryStatus.Active or MemoryStatus.Archived)
            {
                candidates.Add(MapCandidate(memory, policy, evaluation, null, "Quarantine"));
                continue;
            }

            if (state is null)
            {
                continue;
            }

            var isEligible = state.LifecycleStatus == EligibleStatus && state.DeleteEligibleAt.HasValue;
            var matured = isEligible && state.DeleteEligibleAt <= now;
            if (matured)
            {
                candidates.Add(MapCandidate(memory, policy, evaluation, state, "MaturedDelete"));
            }
        }

        if (dbContext.ChangeTracker.HasChanges())
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new AutonomousRetentionReviewResult(
            candidates,
            states.Values.Count(x => x.LifecycleStatus == QuarantinedStatus),
            states.Values.Count(x => x.LifecycleStatus == EligibleStatus && x.DeleteEligibleAt.HasValue),
            states.Values.Count(x => x.LifecycleStatus == EligibleStatus && x.DeleteEligibleAt <= now),
            deleteCancelled,
            protectedRetention);
    }

    public async Task<AutonomousRetentionCandidateResult> QuarantineAsync(
        Guid resourceId,
        string projectId,
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryWrite);
        projectId = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        var memory = await dbContext.MemoryItems.SingleOrDefaultAsync(x =>
            x.Id == resourceId && x.ProjectId == projectId && x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId,
            cancellationToken) ?? throw new KeyNotFoundException($"Memory '{resourceId}' was not found.");
        var policy = ResolvePolicy(memory);
        var evidence = await LoadEvidenceAsync([resourceId], actor.TenantId!.Value, actor.UserId!.Value, [projectId], cancellationToken);
        var now = timeProvider.GetUtcNow();
        var evaluation = Evaluate(memory, policy, evidence, now);
        if (!policy.AutoDelete || evaluation.BlockedReasons.Count > 0)
        {
            throw new InvalidOperationException($"Memory is not quarantine eligible: {string.Join(',', evaluation.BlockedReasons)}");
        }

        var state = await dbContext.MemoryRetentionStates.SingleOrDefaultAsync(x => x.ResourceId == resourceId, cancellationToken);
        if (state is null)
        {
            state = new MemoryRetentionState
            {
                ResourceId = resourceId,
                TenantId = actor.TenantId.Value,
                OwnerUserId = actor.UserId.Value,
                ProjectId = projectId,
                CreatedAt = now
            };
            await dbContext.MemoryRetentionStates.AddAsync(state, cancellationToken);
        }

        if (memory.Status != MemoryStatus.Archived)
        {
            memory.Status = MemoryStatus.Archived;
            memory.Version += 1;
            memory.UpdatedAt = now;
            await dbContext.MemoryItemRevisions.AddAsync(new MemoryItemRevision
            {
                MemoryItemId = memory.Id,
                Version = memory.Version,
                Title = memory.Title,
                Content = memory.Content,
                Summary = memory.Summary,
                MetadataJson = memory.MetadataJson,
                ChangedBy = "autonomous-governance-quarantine",
                CreatedAt = now
            }, cancellationToken);
        }

        InitializeQuarantine(state, memory, policy, evaluation, governanceRunId, now);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementProjectAsync(projectId, cancellationToken);
        return MapCandidate(memory, policy, evaluation, state, "Quarantine");
    }

    public async Task<MaturedDeleteResult> DeleteMaturedAsync(
        Guid resourceId,
        string projectId,
        string governanceRunId,
        CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryWrite);
        projectId = ProjectContext.Normalize(projectId);
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: true);
        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockTextEvidenceWritersAsync(cancellationToken);
        var existing = await FindTombstoneAsync(resourceId, projectId, actor, cancellationToken);
        if (existing is not null)
        {
            return MapReplay(existing);
        }

        var memory = await LoadMemoryForDeleteAsync(resourceId, projectId, actor, cancellationToken)
            ?? throw new InvalidOperationException("Matured delete cannot proceed without the original resource or its tombstone.");
        var state = await LoadRetentionStateForDeleteAsync(resourceId, actor, cancellationToken)
            ?? throw new InvalidOperationException("Matured delete requires a persisted quarantine lifecycle.");
        var policy = ResolvePolicy(memory);
        var evidence = await LoadEvidenceAsync([resourceId], actor.TenantId!.Value, actor.UserId!.Value, [projectId], cancellationToken);
        var now = timeProvider.GetUtcNow();
        var evaluation = Evaluate(memory, policy, evidence, now);
        ApplyEvaluation(state, memory, policy, evaluation, governanceRunId, now, policyChanged: false);
        if (state.LifecycleStatus != EligibleStatus || !state.DeleteEligibleAt.HasValue || state.DeleteEligibleAt > now)
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            throw new InvalidOperationException("Matured delete failed closed because current eligibility or grace maturity was not revalidated.");
        }

        var revisionCount = await dbContext.MemoryItemRevisions.LongCountAsync(x => x.MemoryItemId == resourceId, cancellationToken);
        var chunkIds = await dbContext.MemoryItemChunks.Where(x => x.MemoryItemId == resourceId).Select(x => x.Id).ToArrayAsync(cancellationToken);
        var chunkCount = chunkIds.LongLength;
        var vectorCount = chunkIds.Length == 0 ? 0 : await dbContext.MemoryChunkVectors.LongCountAsync(x => chunkIds.Contains(x.ChunkId), cancellationToken);
        var auditId = Guid.NewGuid();
        var tombstone = new ResourceTombstone
        {
            ResourceId = resourceId,
            ResourceType = ResourceType,
            TenantId = actor.TenantId.Value,
            OwnerUserId = actor.UserId.Value,
            ProjectId = projectId,
            ContentHash = ContentHash(memory),
            Classification = state.Classification,
            ArchivedAt = state.QuarantinedAt ?? throw new InvalidOperationException("Quarantine timestamp is required."),
            DeletedAt = now,
            RetentionPolicyVersion = state.PolicyVersion,
            ReasonCodesJson = state.ReasonCodesJson,
            ReplacementResourceId = state.ReplacementResourceId,
            GovernanceRunId = governanceRunId,
            AuditId = auditId,
            CreatedAt = now
        };
        await dbContext.ResourceTombstones.AddAsync(tombstone, cancellationToken);
        await dbContext.SecurityAuditEvents.AddAsync(new SecurityAuditEvent
        {
            Id = auditId,
            TenantId = actor.TenantId,
            ActorUserId = actor.UserId,
            EventType = SecurityAuditEventType.GovernanceBatchItemProcessed,
            Outcome = "MaturedHardDelete",
            DetailsJson = JsonSerializer.Serialize(new
            {
                resourceId,
                resourceType = ResourceType,
                projectId,
                tombstoneId = tombstone.Id,
                retentionPolicyVersion = state.PolicyVersion,
                governanceRunId
            }, JsonOptions),
            CreatedAt = now
        }, cancellationToken);
        var links = await dbContext.MemoryLinks.Where(x => x.FromId == resourceId || x.ToId == resourceId).ToListAsync(cancellationToken);
        dbContext.MemoryLinks.RemoveRange(links);
        dbContext.MemoryRetentionStates.Remove(state);
        dbContext.MemoryItems.Remove(memory);

        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(cancellationToken);
            dbContext.ChangeTracker.Clear();
            var winner = await FindTombstoneAsync(resourceId, projectId, actor, cancellationToken);
            if (winner is null)
            {
                throw;
            }
            return MapReplay(winner);
        }

        var originalStillExists = await dbContext.MemoryItems.AsNoTracking().AnyAsync(x =>
            x.Id == resourceId && x.ProjectId == projectId && x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId,
            cancellationToken);
        var tombstoneReadBack = await FindTombstoneAsync(resourceId, projectId, actor, cancellationToken);
        if (originalStillExists || tombstoneReadBack is null || tombstoneReadBack.Id != tombstone.Id || tombstoneReadBack.AuditId != auditId)
        {
            throw new InvalidOperationException(
                "Matured delete committed an unknown read-back result; retry is fail-closed and must resolve through the immutable tombstone.");
        }

        await cacheStore.IncrementProjectAsync(projectId, cancellationToken);
        return new MaturedDeleteResult(resourceId, projectId, true, false, tombstone.Id, auditId,
            revisionCount, chunkCount, vectorCount, DeserializeCodes(tombstone.ReasonCodesJson));
    }

    public async Task<ResourceTombstoneResult?> GetTombstoneAsync(Guid resourceId, string? projectId, CancellationToken cancellationToken)
    {
        var actor = RequireActor(SecurityScopes.MemoryRead);
        var normalizedProjectId = string.IsNullOrWhiteSpace(projectId) ? null : ProjectContext.Normalize(projectId);
        if (normalizedProjectId is not null)
        {
            ActorAuthorization.EnsureProjectAllowed(actor, normalizedProjectId, write: false);
        }
        var query = dbContext.ResourceTombstones.AsNoTracking().Where(x =>
            x.ResourceId == resourceId && x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
        if (normalizedProjectId is not null)
        {
            query = query.Where(x => x.ProjectId == normalizedProjectId);
        }
        var tombstone = await query.SingleOrDefaultAsync(cancellationToken);
        if (tombstone is null)
        {
            return null;
        }
        ActorAuthorization.EnsureProjectAllowed(actor, tombstone.ProjectId, write: false);
        return Map(tombstone);
    }

    private async Task<EvidenceSnapshot> LoadEvidenceAsync(
        IReadOnlyList<Guid> memoryIds,
        Guid tenantId,
        Guid ownerUserId,
        IReadOnlyList<string> projectIds,
        CancellationToken cancellationToken)
    {
        if (memoryIds.Count == 0)
        {
            return EvidenceSnapshot.Empty;
        }
        var hitStart = DateOnly.FromDateTime(timeProvider.GetUtcNow().AddDays(-_options.NormalizedHitWindowDays).UtcDateTime);
        var hits = await dbContext.RetrievalTelemetryDailyHitSummaries.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && memoryIds.Contains(x.MemoryId) && x.SummaryDate >= hitStart)
            .GroupBy(x => x.MemoryId).Select(x => new { MemoryId = x.Key, Count = x.Sum(v => v.HitCount) })
            .ToDictionaryAsync(x => x.MemoryId, x => x.Count, cancellationToken);
        var links = await dbContext.MemoryLinks.AsNoTracking()
            .Where(x => memoryIds.Contains(x.FromId) || memoryIds.Contains(x.ToId))
            .ToListAsync(cancellationToken);
        var replacements = links.Where(x => x.LinkType == "replaced_by")
            .GroupBy(x => x.FromId).ToDictionary(x => x.Key, x => x.Select(v => v.ToId).Distinct().ToArray());
        var targetIds = replacements.Values.SelectMany(x => x).Distinct().ToArray();
        var replacementTargets = targetIds.Length == 0
            ? new Dictionary<Guid, ReplacementTarget>()
            : await dbContext.MemoryItems.AsNoTracking().Where(x => targetIds.Contains(x.Id))
                .Select(x => new ReplacementTarget(x.Id, x.ProjectId, x.Status))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
        var hierarchy = await dbContext.ProjectHierarchies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                        (projectIds.Contains(x.ParentProjectId) || projectIds.Contains(x.ChildProjectId)))
            .Select(x => new { x.ParentProjectId, x.ChildProjectId, x.UpdatedAt })
            .ToListAsync(cancellationToken);
        var evidenceProjectIds = projectIds.Concat(hierarchy.SelectMany(x => new[] { x.ParentProjectId, x.ChildProjectId }))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var activeWorkItems = await dbContext.ProjectWorkItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && evidenceProjectIds.Contains(x.ProjectId) &&
                        x.ArchivedAt == null &&
                        (x.Status == ProjectWorkItemStatus.Pending || x.Status == ProjectWorkItemStatus.InProgress || x.Status == ProjectWorkItemStatus.Blocked))
            .Select(x => new { x.Id, x.Title, x.Description }).ToListAsync(cancellationToken);
        var activeWorkItemIds = activeWorkItems.Select(x => x.Id).ToArray();
        var activeChecklistTexts = activeWorkItemIds.Length == 0
            ? []
            : await dbContext.ProjectWorkItemChecklistItems.AsNoTracking()
                .Where(x => activeWorkItemIds.Contains(x.WorkItemId) && !x.IsCompleted)
                .Select(x => x.Content).ToListAsync(cancellationToken);
        var activeWorkItemTexts = activeWorkItems.SelectMany(x => new[] { x.Title, x.Description })
            .Concat(activeChecklistTexts).ToArray();
        var openThreads = await dbContext.DiscussionThreads.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && x.ArchivedAt == null && x.Status == "Open" &&
                        (evidenceProjectIds.Contains(x.HostProjectId) || x.Participants.Any(p => evidenceProjectIds.Contains(p.ProjectId))))
            .Select(x => new { x.Id, x.Title }).ToListAsync(cancellationToken);
        var openThreadIds = openThreads.Select(x => x.Id).ToArray();
        var openDiscussionTexts = openThreadIds.Length == 0
            ? []
            : await dbContext.DiscussionMessages.AsNoTracking()
                .Where(x => openThreadIds.Contains(x.ThreadId))
                .Select(x => x.Content).ToListAsync(cancellationToken);
        openDiscussionTexts = openDiscussionTexts.Concat(openThreads.Select(x => x.Title)).ToList();
        var activeJobPayloads = await dbContext.MemoryJobs.AsNoTracking()
            .Where(x => evidenceProjectIds.Contains(x.ProjectId) &&
                        ((!x.TenantId.HasValue || !x.OwnerUserId.HasValue) ||
                         (x.TenantId == tenantId && x.OwnerUserId == ownerUserId)) &&
                        (x.Status == MemoryJobStatus.Pending || x.Status == MemoryJobStatus.Running))
            .Select(x => x.PayloadJson).ToListAsync(cancellationToken);
        var relationshipFingerprint = Hash(string.Join('|', hierarchy.OrderBy(x => x.ParentProjectId).ThenBy(x => x.ChildProjectId)
            .Select(x => $"{x.ParentProjectId}>{x.ChildProjectId}:{x.UpdatedAt:O}")));
        return new EvidenceSnapshot(hits, links, replacements, replacementTargets, activeWorkItemTexts,
            openDiscussionTexts, activeJobPayloads, relationshipFingerprint);
    }

    private EligibilityEvaluation Evaluate(MemoryItem memory, TypedPolicy policy, EvidenceSnapshot evidence, DateTimeOffset now)
    {
        var reasons = new List<string> { policy.ReasonCode };
        var blocked = new List<string>();
        var tags = memory.Tags.Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var recentHits = evidence.Hits.GetValueOrDefault(memory.Id);
        var relatedLinks = evidence.Links.Where(x => x.FromId == memory.Id || x.ToId == memory.Id).ToArray();
        var linkedReplacements = evidence.Replacements.GetValueOrDefault(memory.Id) ?? [];
        var metadataReplacement = ReadGuid(memory.MetadataJson, "supersededByMemoryId") ?? ReadGuid(memory.MetadataJson, "replacedByMemoryId");
        var ambiguousReplacement = linkedReplacements.Length > 1 ||
            (metadataReplacement.HasValue && linkedReplacements.Any(x => x != metadataReplacement.Value));
        var replacementId = metadataReplacement ?? (linkedReplacements.Length == 1 ? linkedReplacements[0] : null);
        var needsReplacement = policy.RequiresReplacement || tags.Contains("superseded") || tags.Contains("replaced") || replacementId.HasValue;

        if (policy.Protected || memory.MemoryType is MemoryType.Decision or MemoryType.Fact or MemoryType.Preference)
            blocked.Add("protectedType");
        if (memory.IsReadOnly || HasAny(tags, "authoritative", "formal", "source-of-truth", "governance-acceptance"))
            blocked.Add("authoritativeSource");
        if (HasAny(tags, "legal-hold", "legalhold") || JsonFlag(memory.MetadataJson, "legalHold"))
            blocked.Add("legalHold");
        if (HasAny(tags, "security-hold", "securityhold") || JsonFlag(memory.MetadataJson, "securityHold"))
            blocked.Add("securityHold");
        if (HasAny(tags, "security", "audit", "credential", "secret", "private-key", "pii"))
            blocked.Add("secureRemovalPolicyRequired");
        if (recentHits > _options.NormalizedMaxRecentHitCount)
            blocked.Add("recentHits");
        else reasons.Add("lowRecentHits");
        if (relatedLinks.Length > _options.NormalizedMaxLinkDegree && !needsReplacement)
            blocked.Add("linkedMemory");
        else reasons.Add("lowLinkDegree");
        if (memory.Importance > _options.NormalizedMaxImportance)
            blocked.Add("highImportance");
        else reasons.Add("lowImportance");
        var deterministicInvalid = tags.Contains("deterministic-invalid") || JsonFlag(memory.MetadataJson, "deterministicInvalid");
        if (memory.Confidence > _options.NormalizedMaxConfidence && !deterministicInvalid)
            blocked.Add("highConfidence");
        else reasons.Add(deterministicInvalid ? "deterministicInvalid" : "lowConfidence");
        if (References(memory.Id, evidence.ActiveWorkItemTexts))
            blocked.Add("activeWorkItemReference");
        if (References(memory.Id, evidence.OpenDiscussionTexts))
            blocked.Add("activeDiscussionReference");
        if (References(memory.Id, evidence.ActiveJobPayloads))
            blocked.Add("activeDependency");
        if (needsReplacement)
        {
            if (ambiguousReplacement)
                blocked.Add("replacementChainAmbiguous");
            else if (!replacementId.HasValue || !evidence.ReplacementTargets.TryGetValue(replacementId.Value, out var target) ||
                target.Status != MemoryStatus.Active || !string.Equals(target.ProjectId, memory.ProjectId, StringComparison.OrdinalIgnoreCase))
                blocked.Add("replacementChainIncomplete");
            else reasons.Add("replacementChainComplete");
        }

        var fingerprint = Hash(string.Join('|', new[]
        {
            _options.RetentionPolicyVersion,
            recentHits.ToString(),
            relatedLinks.Length.ToString(),
            memory.Importance.ToString(System.Globalization.CultureInfo.InvariantCulture),
            memory.Confidence.ToString(System.Globalization.CultureInfo.InvariantCulture),
            replacementId?.ToString("N") ?? string.Empty,
            evidence.ProjectRelationshipFingerprint,
            string.Join(',', blocked.Order(StringComparer.Ordinal))
        }));
        return new EligibilityEvaluation(reasons.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(),
            blocked.Distinct(StringComparer.OrdinalIgnoreCase).ToArray(), replacementId, fingerprint);
    }

    private TypedPolicy ResolvePolicy(MemoryItem memory)
    {
        var tags = memory.Tags.Select(x => x.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var source = memory.SourceType.ToLowerInvariant();
        if (DurableMemoryGovernancePolicy.IsSystemProjectMetadata(memory))
            return new("system-metadata", "SystemMetadataProtected", 0, false, true, false, "systemMetadataProtected");
        if (memory.MemoryType is MemoryType.Decision or MemoryType.Fact or MemoryType.Preference ||
            HasAny(tags, "security", "audit", "formal", "authoritative", "governance-acceptance"))
            return new("protected", "ProtectedRetention", 0, false, true, false, "protectedRetention");
        if (memory.MemoryType == MemoryType.Artifact && HasAny(tags, "temporary", "obsolete") && !HasAny(tags, "formal"))
            return new("temporary-artifact", "ObsoleteTemporaryArtifact", _options.NormalizedTemporaryArtifactGraceDays, true, false, true, "obsoleteTemporaryArtifact");
        if (tags.Contains("runtime-noise") || source.Contains("runtime-log", StringComparison.Ordinal))
            return new("runtime-noise", "LowValueRuntimeNoise", _options.NormalizedRuntimeNoiseGraceDays, true, false, false, "runtimeNoise");
        if (HasAny(tags, "execution-evidence", "tool-execution", "build-evidence", "test-evidence", "format-evidence", "shell-evidence") ||
            source.Contains("tool", StringComparison.Ordinal) || source.Contains("shell", StringComparison.Ordinal) || source.Contains("build", StringComparison.Ordinal))
            return new("machine-execution-evidence", "LowValueMachineEvidence", _options.NormalizedMachineExecutionEvidenceGraceDays, true, false, false, "machineExecutionEvidence");
        if (memory.MemoryType == MemoryType.Episode && HasAny(tags, "machine-generated", "automated", "low-value", "synthetic-disposable"))
            return new("automated-episode", "LowValueAutomatedEpisode", _options.NormalizedAutomatedEpisodeGraceDays, true, false, false, "lowValueAutomatedEpisode");
        return new("ordinary-retention", "OrdinaryProtectedRetention", 0, false, true, false, "ordinaryRetentionProtected");
    }

    private void ApplyEvaluation(MemoryRetentionState state, MemoryItem memory, TypedPolicy policy,
        EligibilityEvaluation evaluation, string governanceRunId, DateTimeOffset now, bool policyChanged)
    {
        var wasCancelled = state.LifecycleStatus == CancelledStatus;
        state.ProjectId = memory.ProjectId;
        state.Classification = policy.Classification;
        state.PolicyKind = policy.Kind;
        state.PolicyVersion = _options.RetentionPolicyVersion;
        state.GracePeriodDays = policy.GraceDays;
        state.LastRevalidatedAt = now;
        state.EvidenceFingerprint = evaluation.Fingerprint;
        state.ReasonCodesJson = JsonSerializer.Serialize(evaluation.ReasonCodes, JsonOptions);
        state.BlockedReasonsJson = JsonSerializer.Serialize(evaluation.BlockedReasons, JsonOptions);
        state.ReplacementResourceId = evaluation.ReplacementResourceId;
        state.GovernanceRunId = governanceRunId;
        state.UpdatedAt = now;

        if (!policy.AutoDelete || evaluation.BlockedReasons.Count > 0 || memory.Status != MemoryStatus.Archived)
        {
            state.LifecycleStatus = CancelledStatus;
            state.DeleteEligibleAt = null;
            return;
        }
        if (policyChanged || wasCancelled || !state.QuarantinedAt.HasValue)
        {
            state.QuarantinedAt = now;
            state.DeleteEligibleAt = now.AddDays(policy.GraceDays);
        }
        state.LifecycleStatus = EligibleStatus;
    }

    private void InitializeQuarantine(MemoryRetentionState state, MemoryItem memory, TypedPolicy policy,
        EligibilityEvaluation evaluation, string governanceRunId, DateTimeOffset now)
    {
        state.ResourceType = ResourceType;
        state.ProjectId = memory.ProjectId;
        state.Classification = policy.Classification;
        state.PolicyKind = policy.Kind;
        state.PolicyVersion = _options.RetentionPolicyVersion;
        state.GracePeriodDays = policy.GraceDays;
        state.LifecycleStatus = QuarantinedStatus;
        state.QuarantinedAt ??= now;
        state.DeleteEligibleAt = state.QuarantinedAt.Value.AddDays(policy.GraceDays);
        state.LastRevalidatedAt = now;
        state.EvidenceFingerprint = evaluation.Fingerprint;
        state.ReasonCodesJson = JsonSerializer.Serialize(evaluation.ReasonCodes, JsonOptions);
        state.BlockedReasonsJson = "[]";
        state.ReplacementResourceId = evaluation.ReplacementResourceId;
        state.GovernanceRunId = governanceRunId;
        state.UpdatedAt = now;
    }

    private AutonomousRetentionCandidateResult MapCandidate(MemoryItem memory, TypedPolicy policy,
        EligibilityEvaluation evaluation, MemoryRetentionState? state, string action)
    {
        var eligible = state is not null && state.DeleteEligibleAt.HasValue && evaluation.BlockedReasons.Count == 0;
        return new(memory.Id, memory.ProjectId, policy.Classification, action, policy.Kind,
            _options.RetentionPolicyVersion, policy.GraceDays, state?.QuarantinedAt, state?.DeleteEligibleAt,
            eligible, eligible && state!.DeleteEligibleAt <= timeProvider.GetUtcNow(), evaluation.ReplacementResourceId,
            evaluation.ReasonCodes, evaluation.BlockedReasons);
    }

    private ContextHubRequestActor RequireActor(string scope)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, scope);
        if (!actor.IsAdmin)
            throw new UnauthorizedAccessException("Autonomous retention requires an administrator.");
        return actor;
    }

    private Task<ResourceTombstone?> FindTombstoneAsync(Guid resourceId, string projectId, ContextHubRequestActor actor, CancellationToken cancellationToken)
        => dbContext.ResourceTombstones.AsNoTracking().SingleOrDefaultAsync(x =>
            x.ResourceId == resourceId && x.ProjectId == projectId && x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId,
            cancellationToken);

    private Task LockTextEvidenceWritersAsync(CancellationToken cancellationToken)
    {
        if (!string.Equals(
                dbContext.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return Task.CompletedTask;
        }

        return dbContext.Database.ExecuteSqlRawAsync(
            """
            LOCK TABLE project_hierarchies,
                       project_work_items,
                       project_work_item_checklist_items,
                       discussion_threads,
                       discussion_participants,
                       discussion_messages,
                       memory_jobs
            IN SHARE MODE
            """,
            cancellationToken);
    }

    private Task<MemoryItem?> LoadMemoryForDeleteAsync(
        Guid resourceId,
        string projectId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                dbContext.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return dbContext.MemoryItems.SingleOrDefaultAsync(x =>
                x.Id == resourceId && x.ProjectId == projectId &&
                x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId,
                cancellationToken);
        }

        return dbContext.MemoryItems.FromSqlInterpolated($"""
                SELECT *
                FROM memory_items
                WHERE id = {resourceId}
                  AND project_id = {projectId}
                  AND tenant_id = {actor.TenantId}
                  AND owner_user_id = {actor.UserId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private Task<MemoryRetentionState?> LoadRetentionStateForDeleteAsync(
        Guid resourceId,
        ContextHubRequestActor actor,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(
                dbContext.Database.ProviderName,
                "Npgsql.EntityFrameworkCore.PostgreSQL",
                StringComparison.Ordinal))
        {
            return dbContext.MemoryRetentionStates.SingleOrDefaultAsync(x =>
                x.ResourceId == resourceId && x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId,
                cancellationToken);
        }

        return dbContext.MemoryRetentionStates.FromSqlInterpolated($"""
                SELECT *
                FROM memory_retention_states
                WHERE resource_id = {resourceId}
                  AND tenant_id = {actor.TenantId}
                  AND owner_user_id = {actor.UserId}
                FOR UPDATE
                """)
            .SingleOrDefaultAsync(cancellationToken);
    }

    private static MaturedDeleteResult MapReplay(ResourceTombstone tombstone)
        => new(tombstone.ResourceId, tombstone.ProjectId, false, true, tombstone.Id, tombstone.AuditId,
            0, 0, 0, DeserializeCodes(tombstone.ReasonCodesJson));

    private static ResourceTombstoneResult Map(ResourceTombstone tombstone)
        => new(tombstone.Id, tombstone.ResourceId, tombstone.ResourceType, tombstone.ProjectId, tombstone.ContentHash,
            tombstone.Classification, tombstone.ArchivedAt, tombstone.DeletedAt, tombstone.RetentionPolicyVersion,
            DeserializeCodes(tombstone.ReasonCodesJson), tombstone.ReplacementResourceId, tombstone.GovernanceRunId, tombstone.AuditId);

    private static IReadOnlyList<string> DeserializeCodes(string json)
    {
        try { return JsonSerializer.Deserialize<string[]>(json, JsonOptions) ?? []; }
        catch (JsonException) { return []; }
    }

    private static Guid? ReadGuid(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String && Guid.TryParse(value.GetString(), out var id) ? id : null;
        }
        catch (JsonException) { return null; }
    }

    private static bool JsonFlag(string json, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (JsonException) { return false; }
    }

    private static bool References(Guid id, IReadOnlyList<string> texts)
    {
        var value = id.ToString("D");
        return texts.Any(x => x.Contains(value, StringComparison.OrdinalIgnoreCase));
    }

    private static bool HasAny(IReadOnlySet<string> tags, params string[] values) => values.Any(tags.Contains);
    private static string ContentHash(MemoryItem memory) => Hash(JsonSerializer.Serialize(new
    {
        memory.Id,
        memory.ProjectId,
        memory.MemoryType,
        memory.Title,
        memory.Content,
        memory.Summary,
        memory.Tags,
        memory.SourceType,
        memory.SourceRef,
        memory.MetadataJson
    }, JsonOptions));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private sealed record TypedPolicy(string Kind, string Classification, int GraceDays, bool AutoDelete, bool Protected, bool RequiresReplacement, string ReasonCode);
    private sealed record EligibilityEvaluation(IReadOnlyList<string> ReasonCodes, IReadOnlyList<string> BlockedReasons, Guid? ReplacementResourceId, string Fingerprint);
    private sealed record ReplacementTarget(Guid Id, string ProjectId, MemoryStatus Status);
    private sealed record EvidenceSnapshot(
        IReadOnlyDictionary<Guid, long> Hits,
        IReadOnlyList<MemoryLink> Links,
        IReadOnlyDictionary<Guid, Guid[]> Replacements,
        IReadOnlyDictionary<Guid, ReplacementTarget> ReplacementTargets,
        IReadOnlyList<string> ActiveWorkItemTexts,
        IReadOnlyList<string> OpenDiscussionTexts,
        IReadOnlyList<string> ActiveJobPayloads,
        string ProjectRelationshipFingerprint)
    {
        public static EvidenceSnapshot Empty { get; } = new(new Dictionary<Guid, long>(), [],
            new Dictionary<Guid, Guid[]>(), new Dictionary<Guid, ReplacementTarget>(), [], [], [], string.Empty);
    }
}

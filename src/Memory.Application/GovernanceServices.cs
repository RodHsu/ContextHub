using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class GovernanceService(
    IApplicationDbContext dbContext,
    IEmbeddingProvider embeddingProvider,
    ICacheVersionStore cacheStore,
    IClock clock,
    IRequestActorAccessor actorAccessor) : IGovernanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<GovernanceFindingResult>> ListAsync(GovernanceFindingListRequest request, CancellationToken cancellationToken)
    {
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
        var query = dbContext.GovernanceFindings.AsNoTracking().ForActor(actor).Where(x => x.ProjectId == projectId);

        if (request.Type.HasValue)
        {
            query = query.Where(x => x.Type == request.Type.Value);
        }

        if (request.Status.HasValue)
        {
            query = query.Where(x => x.Status == request.Status.Value);
        }

        var entities = await query
            .OrderByDescending(x => x.UpdatedAt)
            .Take(Math.Clamp(request.Limit, 1, 200))
            .ToListAsync(cancellationToken);
        return entities.Select(Map).ToArray();
    }

    public async Task<GovernanceFindingResult> AcceptAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await GetRequiredAsync(id, cancellationToken);
        ActorAuthorization.EnsureProjectAllowed(actorAccessor.Current, entity.ProjectId, write: true);
        entity.Status = GovernanceFindingStatus.Accepted;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<GovernanceFindingResult> DismissAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await GetRequiredAsync(id, cancellationToken);
        ActorAuthorization.EnsureProjectAllowed(actorAccessor.Current, entity.ProjectId, write: true);
        entity.Status = GovernanceFindingStatus.Dismissed;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<GovernanceFindingResult> SetDispositionAsync(
        GovernanceFindingDispositionRequest request,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var entity = await GetRequiredAsync(request.FindingId, cancellationToken);
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: true);
        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            throw new InvalidOperationException("A governance disposition reason is required.");
        }

        var status = request.Disposition switch
        {
            GovernanceFindingDisposition.Deferred => GovernanceFindingStatus.Deferred,
            GovernanceFindingDisposition.RequiresUserDecision => GovernanceFindingStatus.RequiresUserDecision,
            GovernanceFindingDisposition.HostBlocked => GovernanceFindingStatus.HostBlocked,
            _ => throw new InvalidOperationException($"Unsupported governance finding disposition '{request.Disposition}'.")
        };
        var reason = request.Reason.Trim();
        var governanceRunId = NormalizeGovernanceRunId(request.GovernanceRunId);
        if (entity.Status == status &&
            string.Equals(entity.GovernanceReason, reason, StringComparison.Ordinal) &&
            string.Equals(entity.GovernanceRunId, governanceRunId, StringComparison.Ordinal))
        {
            return Map(entity);
        }

        entity.Status = status;
        entity.GovernanceReason = reason;
        entity.GovernanceRunId = governanceRunId;
        entity.GovernanceActor = actor.Username;
        entity.GovernanceUpdatedAt = clock.UtcNow;
        entity.GovernancePolicyVersion = GovernanceEvidenceFingerprint.PolicyVersion;
        entity.GovernanceEvidenceFingerprint = await GovernanceEvidenceFingerprint.BuildAsync(
            dbContext, entity.ProjectId, entity.PrimaryMemoryId, entity.SecondaryMemoryId, null,
            GovernanceEvidenceFingerprint.FindingPayload(entity), cancellationToken);
        entity.UpdatedAt = clock.UtcNow;
        await SupersedePendingActionsForFindingAsync(entity.ProjectId, entity.DedupKey, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<GovernanceFindingResult> ReopenAsync(
        GovernanceFindingReopenRequest request,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryWrite);
        var entity = await GetRequiredAsync(request.FindingId, cancellationToken);
        ActorAuthorization.EnsureProjectAllowed(actor, entity.ProjectId, write: true);
        if (entity.Status is not (GovernanceFindingStatus.Deferred or GovernanceFindingStatus.RequiresUserDecision or GovernanceFindingStatus.HostBlocked))
        {
            throw new InvalidOperationException($"Governance finding '{entity.Id}' is not in an exception disposition.");
        }

        entity.Status = GovernanceFindingStatus.Open;
        entity.GovernanceReason = string.IsNullOrWhiteSpace(request.Reason) ? "Reopened for governance review." : request.Reason.Trim();
        entity.GovernanceRunId = NormalizeGovernanceRunId(request.GovernanceRunId);
        entity.GovernanceActor = actor.Username;
        entity.GovernanceRetryCount += 1;
        entity.GovernanceUpdatedAt = clock.UtcNow;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task AnalyzeAsync(string projectId, CancellationToken cancellationToken)
    {
        var normalizedProjectId = ProjectContext.Normalize(projectId);
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureProjectAllowed(actor, normalizedProjectId, write: true);
        var now = clock.UtcNow;
        var findings = new List<GovernanceDraft>();
        if (actor.HasUser)
        {
            ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
            ActorAuthorization.EnsureProjectAllowed(actor, normalizedProjectId, write: false);
        }

        var sourceQuery = dbContext.SourceConnections.AsNoTracking().Where(x => x.ProjectId == normalizedProjectId);
        var memoryQuery = dbContext.MemoryItems.AsNoTracking().Where(x => x.ProjectId == normalizedProjectId);
        if (actor.HasUser)
        {
            memoryQuery = memoryQuery.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
        }

        var sources = await sourceQuery.ToListAsync(cancellationToken);
        var memories = await memoryQuery.ToListAsync(cancellationToken);
        var memoryIdsForLinks = memories.Select(x => x.Id).ToArray();
        var replacementPairs = memoryIdsForLinks.Length == 0
            ? new HashSet<string>(StringComparer.Ordinal)
            : (await dbContext.MemoryLinks
                .AsNoTracking()
                .Where(x => x.LinkType == "replaced_by" &&
                            memoryIdsForLinks.Contains(x.FromId) &&
                            memoryIdsForLinks.Contains(x.ToId))
                .Select(x => new { x.FromId, x.ToId })
                .ToListAsync(cancellationToken))
                .Select(x => CanonicalPairKey(x.FromId, x.ToId))
                .ToHashSet(StringComparer.Ordinal);
        await MaterializeExecutedMergeRelationshipsAsync(normalizedProjectId, memories, replacementPairs, cancellationToken);

        foreach (var source in sources.Where(static source => source.Enabled))
        {
            var isStale = !source.LastSuccessfulSyncAt.HasValue ||
                          source.LastSuccessfulSyncAt.Value < now.AddHours(-24);
            if (!isStale)
            {
                continue;
            }

            findings.Add(new GovernanceDraft(
                $"stale-source:{normalizedProjectId}:{source.Id}",
                GovernanceFindingType.StaleSource,
                $"來源同步已過期：{source.Name}",
                source.LastSuccessfulSyncAt.HasValue
                    ? $"最後成功同步時間為 {source.LastSuccessfulSyncAt.Value:O}。"
                    : "此來源尚未成功同步。",
                source.Id,
                null,
                null,
                JsonSerializer.Serialize(new { sourceId = source.Id, source.Name }, JsonOptions)));
        }

        foreach (var memory in memories.Where(IsMissingSourceCandidate))
        {
            findings.Add(new GovernanceDraft(
                $"missing-source:{normalizedProjectId}:{memory.Id}",
                GovernanceFindingType.MissingSource,
                $"來源內容已消失：{memory.Title}",
                "此條目在最近一次來源同步時未再次出現，已被標記為 archived。",
                TryGetConnectorId(memory.MetadataJson),
                memory.Id,
                null,
                memory.MetadataJson));
        }

        var activeArtifacts = memories
            .Where(x => x.Status == MemoryStatus.Active)
            .Where(x => x.MemoryType == MemoryType.Artifact)
            .ToArray();

        foreach (var group in activeArtifacts.GroupBy(x => NormalizeKey(x.Title)).Where(group => group.Count() > 1))
        {
            var items = group.OrderBy(x => x.UpdatedAt).ToArray();
            for (var i = 0; i < items.Length; i++)
            {
                for (var j = i + 1; j < items.Length; j++)
                {
                    var left = items[i];
                    var right = items[j];
                    if (replacementPairs.Contains(CanonicalPairKey(left.Id, right.Id)))
                    {
                        continue;
                    }
                    var similarity = ComputeTokenOverlap(left.Summary, right.Summary);
                    if (similarity >= 0.45m)
                    {
                        findings.Add(new GovernanceDraft(
                            $"duplicate:{normalizedProjectId}:{CanonicalPairKey(left.Id, right.Id)}",
                            GovernanceFindingType.DuplicateMemoryCandidate,
                            $"可能重複的記憶：{left.Title}",
                            $"兩筆記憶具有相近標題與高重疊摘要，相似度 {similarity:P0}。",
                            null,
                            left.Id,
                            right.Id,
                            JsonSerializer.Serialize(new { similarity }, JsonOptions)));
                    }

                    if (!string.Equals(left.Summary, right.Summary, StringComparison.OrdinalIgnoreCase) && similarity <= 0.35m)
                    {
                        findings.Add(new GovernanceDraft(
                            $"conflict:{normalizedProjectId}:{CanonicalPairKey(left.Id, right.Id)}",
                            GovernanceFindingType.ConflictCandidate,
                            $"可能衝突的記憶：{left.Title}",
                            "標題相近，但摘要內容差異明顯，建議人工比對來源。",
                            null,
                            left.Id,
                            right.Id,
                            JsonSerializer.Serialize(new { left = left.Summary, right = right.Summary }, JsonOptions)));
                    }
                }
            }
        }

        foreach (var memory in memories.Where(memory => IsSupersededMemoryCandidate(memory, memories)))
        {
            findings.Add(new GovernanceDraft(
                $"superseded-memory:{normalizedProjectId}:{memory.Id}",
                GovernanceFindingType.SupersededMemoryCandidate,
                $"可能已被新版取代：{memory.Title}",
                "此 active memory 帶有 superseded / replaced 訊號，建議人工確認後再封存或改鏈結。",
                TryGetConnectorId(memory.MetadataJson),
                memory.Id,
                TryGetSupersededByMemoryId(memory.MetadataJson),
                JsonSerializer.Serialize(new { memory.Status, memory.UpdatedAt, memory.Tags }, JsonOptions)));
        }

        foreach (var memory in memories.Where(memory => IsStaleMemoryCandidate(memory, now)))
        {
            findings.Add(new GovernanceDraft(
                $"stale-memory:{normalizedProjectId}:{memory.Id}",
                GovernanceFindingType.StaleMemoryCandidate,
                $"可能過期的記憶：{memory.Title}",
                $"此 memory 已 {Math.Max(1, (now - memory.UpdatedAt).Days)} 天未更新，且重要性 / 信心分數不高；建議週期性 review，不自動刪除。",
                TryGetConnectorId(memory.MetadataJson),
                memory.Id,
                null,
                JsonSerializer.Serialize(new { memory.MemoryType, memory.Importance, memory.Confidence, memory.UpdatedAt }, JsonOptions)));
        }

        foreach (var memory in memories.Where(memory => IsLowSignalEpisodeCandidate(memory, now)))
        {
            findings.Add(new GovernanceDraft(
                $"low-signal-episode:{normalizedProjectId}:{memory.Id}",
                GovernanceFindingType.LowSignalEpisodeCandidate,
                $"低訊號 episode 候選：{memory.Title}",
                "此 episode 較舊且 importance / confidence 偏低；建議人工確認是否合併、封存或保留。",
                TryGetConnectorId(memory.MetadataJson),
                memory.Id,
                null,
                JsonSerializer.Serialize(new { memory.Importance, memory.Confidence, memory.UpdatedAt, memory.Tags }, JsonOptions)));
        }

        foreach (var memory in memories)
        {
            var expectedProjectId = TryGetMetadataString(memory.MetadataJson, "expectedProjectId") ??
                                    TryGetMetadataString(memory.MetadataJson, "targetProjectId");
            if (!string.IsNullOrWhiteSpace(expectedProjectId) &&
                !string.Equals(ProjectContext.Normalize(expectedProjectId), normalizedProjectId, StringComparison.OrdinalIgnoreCase))
            {
                var targetProjectId = ProjectContext.Normalize(expectedProjectId);
                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    memory,
                    GovernanceFindingType.MisplacedProjectCandidate,
                    "misplaced-project",
                    $"ProjectId 可能放置錯誤：{memory.Title}",
                    $"metadata 明確指定 target ProjectId '{targetProjectId}'，目前位於 '{normalizedProjectId}'。",
                    new { targetProjectId, reasonCodes = new[] { "explicit-target-project-mismatch" } }));
                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    memory,
                    GovernanceFindingType.MoveMemoryCandidate,
                    "move-memory",
                    $"建議搬移記憶：{memory.Title}",
                    $"此記憶具有明確 target ProjectId '{targetProjectId}'；搬移必須經 proposal 與 audit。",
                    new { targetProjectId, reasonCodes = new[] { "explicit-target-project" } }));
            }

            if (memory.Status is MemoryStatus.Stale or MemoryStatus.Superseded || HasTag(memory, "obsolete") || HasTag(memory, "deprecated"))
            {
                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    memory,
                    GovernanceFindingType.ObsoleteMemoryCandidate,
                    "obsolete-memory",
                    $"可能已過時：{memory.Title}",
                    "Lifecycle status 或明確 tag 顯示此記憶可能已過時；需確認 authoritative replacement。",
                    new { memory.Status, memory.Tags, reasonCodes = new[] { "lifecycle-obsolete-signal" } }));
            }

            if (IsLifecycleCandidate(memory) && IsLowValueMemoryCandidate(memory))
            {
                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    memory,
                    GovernanceFindingType.LowValueMemoryCandidate,
                    "low-value-memory",
                    $"低價值記憶候選：{memory.Title}",
                    "importance/confidence 偏低，或內容為明確 tombstone；預設僅建議封存。",
                    new { memory.Importance, memory.Confidence, reasonCodes = new[] { "low-signal-or-tombstone" } }));
            }

            if (IsInvalidMemoryCandidate(memory, out var invalidReason))
            {
                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    memory,
                    GovernanceFindingType.InvalidMemoryCandidate,
                    "invalid-memory",
                    $"無效或不正確記憶候選：{memory.Title}",
                    invalidReason,
                    new { reasonCodes = new[] { "invalid-memory-contract" } }));
            }

            if (memory.Status == MemoryStatus.Active &&
                (IsStaleMemoryCandidate(memory, now) || IsLowValueMemoryCandidate(memory) || IsSupersededMemoryCandidate(memory, memories)))
            {
                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    memory,
                    GovernanceFindingType.ArchiveMemoryCandidate,
                    "archive-memory",
                    $"建議先封存：{memory.Title}",
                    "此記憶符合非破壞性 archive-first 條件；scheduled governance 不得 hard-delete。",
                    new { reasonCodes = new[] { "archive-first" } }));
            }

            if (!ProjectContext.IsShared(normalizedProjectId) &&
                (HasTag(memory, "shared-candidate") || HasTag(memory, "cross-project")))
            {
                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    memory,
                    GovernanceFindingType.SharedKnowledgePromotionCandidate,
                    "shared-promotion",
                    $"Shared Knowledge 提升候選：{memory.Title}",
                    "此記憶帶有明確跨專案重用訊號；提升需 proposal-first 並保留來源鏈。",
                    new { targetProjectId = ProjectContext.SharedProjectId, reasonCodes = new[] { "explicit-shared-signal" } }));
            }

            if (ProjectContext.IsShared(normalizedProjectId) &&
                (HasTag(memory, "project-specific") || !string.IsNullOrWhiteSpace(expectedProjectId)))
            {
                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    memory,
                    GovernanceFindingType.SharedKnowledgeDemotionCandidate,
                    "shared-demotion",
                    $"Shared Knowledge 降級候選：{memory.Title}",
                    "此 shared 記憶帶有專案專屬訊號；應人工確認後搬回明確 ProjectId。",
                    new { targetProjectId = expectedProjectId, reasonCodes = new[] { "project-specific-shared-memory" } }));
            }

            var successorId = TryGetSupersededByMemoryId(memory.MetadataJson);
            if (successorId.HasValue)
            {
                var successor = memories.FirstOrDefault(x => x.Id == successorId.Value);
                var successorSuccessorId = successor is null ? null : TryGetSupersededByMemoryId(successor.MetadataJson);
                if (successor is null || successorSuccessorId.HasValue)
                {
                    findings.Add(CreateMemoryDraft(
                        normalizedProjectId,
                        memory,
                        GovernanceFindingType.ReplacementChainCandidate,
                        "replacement-chain",
                        $"Replacement chain 需檢閱：{memory.Title}",
                        successor is null ? "replacement 指向不存在的記憶。" : "replacement 形成多段鏈，需確認最終 authoritative source。",
                        new { successorId, successorSuccessorId, reasonCodes = new[] { successor is null ? "broken-replacement" : "multi-hop-replacement" } },
                        successorId));
                }
            }
        }

        foreach (var group in memories
                     .Where(x => x.Status == MemoryStatus.Active)
                     .GroupBy(x => NormalizeKey(x.Title))
                     .Where(x => !string.IsNullOrWhiteSpace(x.Key) && x.Count() > 1))
        {
            var ordered = group.OrderByDescending(AuthorityScore).ThenByDescending(x => x.UpdatedAt).ToArray();
            var authoritative = ordered[0];
            foreach (var candidate in ordered.Skip(1))
            {
                if (replacementPairs.Contains(CanonicalPairKey(authoritative.Id, candidate.Id)))
                {
                    continue;
                }

                var overlap = ComputeTokenOverlap(authoritative.Summary, candidate.Summary);
                if (overlap >= .55m)
                {
                    findings.Add(CreateMemoryDraft(
                        normalizedProjectId,
                        candidate,
                        GovernanceFindingType.MergeMemoryCandidate,
                        "merge-memory",
                        $"合併候選：{candidate.Title}",
                        $"與較高 authority 記憶摘要重疊 {overlap:P0}；合併需保留來源與 replacement chain。",
                        new { overlap, authoritativeMemoryId = authoritative.Id, reasonCodes = new[] { "same-title-high-overlap" } },
                        authoritative.Id));
                }

                findings.Add(CreateMemoryDraft(
                    normalizedProjectId,
                    candidate,
                    GovernanceFindingType.AuthoritativeSourceCandidate,
                    "authoritative-source",
                    $"Authoritative source 判斷：{candidate.Title}",
                    $"同標題 active 記憶共 {ordered.Length} 筆；目前依 explicit authority、Decision、confidence、importance、version 與更新時間排序。",
                    new { authoritativeMemoryId = authoritative.Id, candidateMemoryId = candidate.Id, overlap, reasonCodes = new[] { "same-title-authority-ranking" } },
                    authoritative.Id));
            }
        }

        var vectorCandidateQuery = dbContext.MemoryItems
            .AsNoTracking()
            .ForActor(actor)
            .Include(x => x.Chunks)
                .ThenInclude(x => x.Vectors)
            .Where(x => x.ProjectId == normalizedProjectId)
            .Where(x => x.Status == MemoryStatus.Active)
            .Where(x => x.MemoryType == MemoryType.Artifact);
        if (actor.HasUser)
        {
            vectorCandidateQuery = vectorCandidateQuery.Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.UserId);
        }
        var vectorCandidates = await vectorCandidateQuery.ToListAsync(cancellationToken);
        foreach (var item in vectorCandidates)
        {
            var hasCurrentModel = item.Chunks.Any(chunk => chunk.Vectors.Any(vector => vector.ModelKey == embeddingProvider.ModelKey && vector.Status == VectorStatus.Active.ToString()));
            if (hasCurrentModel)
            {
                continue;
            }

            findings.Add(new GovernanceDraft(
                $"reindex-required:{normalizedProjectId}:{item.Id}",
                GovernanceFindingType.ReindexRequired,
                $"需要重新索引：{item.Title}",
                $"目前向量資料未對齊 model key '{embeddingProvider.ModelKey}'。",
                TryGetConnectorId(item.MetadataJson),
                item.Id,
                null,
                JsonSerializer.Serialize(new { expectedModelKey = embeddingProvider.ModelKey }, JsonOptions)));
        }

        var existingQuery = dbContext.GovernanceFindings.Where(x => x.ProjectId == normalizedProjectId);
        if (actor.HasUser)
        {
            var memoryIds = memories.Select(x => x.Id).ToArray();
            var sourceIds = sources.Select(x => x.Id).ToArray();
            existingQuery = existingQuery.Where(x =>
                (x.PrimaryMemoryId.HasValue && memoryIds.Contains(x.PrimaryMemoryId.Value)) ||
                (x.SourceConnectionId.HasValue && sourceIds.Contains(x.SourceConnectionId.Value)));
        }
        var existing = await existingQuery.ToListAsync(cancellationToken);
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var draft in findings)
        {
            currentKeys.Add(draft.DedupKey);
            var entity = existing.FirstOrDefault(x => string.Equals(x.DedupKey, draft.DedupKey, StringComparison.OrdinalIgnoreCase));
            if (entity is null)
            {
                entity = new GovernanceFinding
                {
                    TenantId = actor.TenantId,
                    OwnerUserId = actor.UserId,
                    ProjectId = normalizedProjectId,
                    Status = GovernanceFindingStatus.Open,
                    CreatedAt = clock.UtcNow
                };
                await dbContext.GovernanceFindings.AddAsync(entity, cancellationToken);
                existing.Add(entity);
            }
            else if (entity.Status == GovernanceFindingStatus.Resolved)
            {
                entity.Status = GovernanceFindingStatus.Open;
            }

            entity.SourceConnectionId = draft.SourceConnectionId;
            entity.PrimaryMemoryId = draft.PrimaryMemoryId;
            entity.SecondaryMemoryId = draft.SecondaryMemoryId;
            entity.Type = draft.Type;
            entity.Title = draft.Title;
            entity.Summary = draft.Summary;
            entity.DetailsJson = draft.DetailsJson;
            entity.DedupKey = draft.DedupKey;
            entity.UpdatedAt = clock.UtcNow;

            if (entity.Status is GovernanceFindingStatus.Deferred or GovernanceFindingStatus.RequiresUserDecision &&
                !string.IsNullOrWhiteSpace(entity.GovernanceEvidenceFingerprint))
            {
                var currentFingerprint = await GovernanceEvidenceFingerprint.BuildAsync(
                    dbContext, normalizedProjectId, entity.PrimaryMemoryId, entity.SecondaryMemoryId, null,
                    GovernanceEvidenceFingerprint.FindingPayload(entity), cancellationToken);
                if (!string.Equals(entity.GovernancePolicyVersion, GovernanceEvidenceFingerprint.PolicyVersion, StringComparison.Ordinal) ||
                    !string.Equals(entity.GovernanceEvidenceFingerprint, currentFingerprint, StringComparison.Ordinal))
                {
                    entity.Status = GovernanceFindingStatus.Open;
                    entity.GovernanceReason = "Automatically reopened because governance evidence or policy changed.";
                    entity.GovernanceRunId = string.Empty;
                    entity.GovernanceRetryCount += 1;
                    entity.GovernanceUpdatedAt = clock.UtcNow;
                    entity.GovernancePolicyVersion = GovernanceEvidenceFingerprint.PolicyVersion;
                    entity.GovernanceEvidenceFingerprint = currentFingerprint;
                }
            }

            await EnsureLinkAsync(draft, cancellationToken);
            if (entity.Status == GovernanceFindingStatus.Open)
            {
                await EnsureSuggestedActionAsync(normalizedProjectId, draft, cancellationToken);
            }
            else
            {
                await SupersedePendingActionsForFindingAsync(normalizedProjectId, draft.DedupKey, cancellationToken);
            }
        }

        foreach (var entity in existing.Where(x => !currentKeys.Contains(x.DedupKey) &&
                                                    x.Status is GovernanceFindingStatus.Open or
                                                        GovernanceFindingStatus.Accepted or
                                                        GovernanceFindingStatus.Deferred or
                                                        GovernanceFindingStatus.RequiresUserDecision or
                                                        GovernanceFindingStatus.HostBlocked))
        {
            entity.Status = GovernanceFindingStatus.Resolved;
            entity.UpdatedAt = clock.UtcNow;
            await SupersedePendingActionsForFindingAsync(normalizedProjectId, entity.DedupKey, cancellationToken);
        }

        await SupersedePendingActionsWithTerminalEquivalentAsync(normalizedProjectId, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementProjectAsync(normalizedProjectId, cancellationToken);
    }

    private async Task EnsureLinkAsync(GovernanceDraft draft, CancellationToken cancellationToken)
    {
        if (!draft.PrimaryMemoryId.HasValue || !draft.SecondaryMemoryId.HasValue)
        {
            return;
        }

        var linkType = draft.Type switch
        {
            GovernanceFindingType.DuplicateCandidate or GovernanceFindingType.DuplicateMemoryCandidate => "duplicate_of",
            GovernanceFindingType.ConflictCandidate => "conflicts_with",
            GovernanceFindingType.SupersededMemoryCandidate => "superseded_by",
            _ => string.Empty
        };
        if (string.IsNullOrWhiteSpace(linkType))
        {
            return;
        }

        var exists = await dbContext.MemoryLinks.AnyAsync(
            x => x.FromId == draft.PrimaryMemoryId.Value &&
                 x.ToId == draft.SecondaryMemoryId.Value &&
                 x.LinkType == linkType,
            cancellationToken);
        if (!exists)
        {
            await dbContext.MemoryLinks.AddAsync(new MemoryLink
            {
                FromId = draft.PrimaryMemoryId.Value,
                ToId = draft.SecondaryMemoryId.Value,
                LinkType = linkType,
                CreatedAt = clock.UtcNow
            }, cancellationToken);
        }
    }

    private async Task EnsureSuggestedActionAsync(string projectId, GovernanceDraft draft, CancellationToken cancellationToken)
    {
        var actionType = draft.Type switch
        {
            GovernanceFindingType.StaleSource => SuggestedActionType.SyncSourceNow,
            GovernanceFindingType.DuplicateCandidate or GovernanceFindingType.DuplicateMemoryCandidate or GovernanceFindingType.MergeMemoryCandidate => SuggestedActionType.MergeDuplicateCandidate,
            GovernanceFindingType.ConflictCandidate or GovernanceFindingType.SupersededMemoryCandidate or GovernanceFindingType.InvalidMemoryCandidate or GovernanceFindingType.ReplacementChainCandidate or GovernanceFindingType.AuthoritativeSourceCandidate => SuggestedActionType.ReviewConflictCandidate,
            GovernanceFindingType.StaleMemoryCandidate or GovernanceFindingType.LowSignalEpisodeCandidate or GovernanceFindingType.ObsoleteMemoryCandidate or GovernanceFindingType.LowValueMemoryCandidate or GovernanceFindingType.ArchiveMemoryCandidate => SuggestedActionType.ArchiveStaleMemory,
            GovernanceFindingType.MissingSource => SuggestedActionType.ArchiveStaleMemory,
            GovernanceFindingType.ReindexRequired => SuggestedActionType.ReindexProject,
            _ => (SuggestedActionType?)null
        };
        if (!actionType.HasValue)
        {
            return;
        }

        var dedupKey = BuildSuggestedActionDedupKey(actionType.Value, draft);
        var matchingActions = await dbContext.SuggestedActions
            .ForActor(actorAccessor.Current)
            .Where(x => x.ProjectId == projectId && x.Type == actionType.Value)
            .ToListAsync(cancellationToken);
        var probe = new SuggestedAction
        {
            Type = actionType.Value,
            DedupKey = dedupKey,
            PayloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["dedupKey"] = dedupKey,
                ["findingId"] = draft.DedupKey,
                ["primaryMemoryId"] = draft.PrimaryMemoryId,
                ["secondaryMemoryId"] = draft.SecondaryMemoryId
            }, JsonOptions)
        };
        var identity = SuggestedActionEquivalence.GetIdentity(probe);
        var equivalents = matchingActions
            .Concat(dbContext.SuggestedActions.Local.Where(x => x.ProjectId == projectId && x.Type == actionType.Value))
            .DistinctBy(x => x.Id)
            .Where(x => string.Equals(SuggestedActionEquivalence.GetIdentity(x), identity, StringComparison.Ordinal))
            .ToArray();
        if (equivalents.Any(x => x.Status is SuggestedActionStatus.Executed or SuggestedActionStatus.Dismissed or SuggestedActionStatus.Superseded))
        {
            foreach (var pending in equivalents.Where(x => x.Status is SuggestedActionStatus.Pending or SuggestedActionStatus.Accepted))
            {
                pending.Status = SuggestedActionStatus.Superseded;
                pending.UpdatedAt = clock.UtcNow;
            }
            return;
        }

        if (equivalents.Any(x => x.Status is SuggestedActionStatus.Pending or SuggestedActionStatus.Accepted))
        {
            return;
        }

        await dbContext.SuggestedActions.AddAsync(new SuggestedAction
        {
            TenantId = actorAccessor.Current.TenantId,
            OwnerUserId = actorAccessor.Current.UserId,
            ProjectId = projectId,
            Type = actionType.Value,
            Status = SuggestedActionStatus.Pending,
            Title = draft.Title,
            Summary = draft.Summary,
            DedupKey = dedupKey,
            PayloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["dedupKey"] = dedupKey,
                ["findingId"] = draft.DedupKey,
                ["sourceConnectionId"] = draft.SourceConnectionId,
                ["primaryMemoryId"] = draft.PrimaryMemoryId,
                ["secondaryMemoryId"] = draft.SecondaryMemoryId,
                ["projectId"] = projectId
            }, JsonOptions),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        }, cancellationToken);
    }

    private async Task<GovernanceFinding> GetRequiredAsync(Guid id, CancellationToken cancellationToken)
        => await dbContext.GovernanceFindings
            .ForActor(actorAccessor.Current)
            .FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
            ?? throw new InvalidOperationException($"Governance finding '{id}' was not found.");

    private static bool IsMissingSourceCandidate(MemoryItem entity)
        => entity.Status == MemoryStatus.Archived &&
           entity.MetadataJson.Contains("\"missing\":true", StringComparison.OrdinalIgnoreCase) &&
           entity.MetadataJson.Contains("\"sourceManaged\":true", StringComparison.OrdinalIgnoreCase);

    private static bool IsStaleMemoryCandidate(MemoryItem entity, DateTimeOffset now)
        => IsLifecycleCandidate(entity) &&
           entity.MemoryType is MemoryType.Fact or MemoryType.Episode or MemoryType.Artifact or MemoryType.Summary &&
           entity.UpdatedAt < now.AddDays(-60) &&
           entity.Importance <= 0.65m &&
           entity.Confidence <= 0.80m;

    private static bool IsLowSignalEpisodeCandidate(MemoryItem entity, DateTimeOffset now)
        => IsLifecycleCandidate(entity) &&
           entity.MemoryType == MemoryType.Episode &&
           entity.UpdatedAt < now.AddDays(-30) &&
           (entity.Importance <= 0.55m || entity.Confidence <= 0.70m);

    private static bool IsSupersededMemoryCandidate(MemoryItem entity, IReadOnlyList<MemoryItem> memories)
        => IsLifecycleCandidate(entity) &&
           (HasTag(entity, "superseded") ||
            HasTag(entity, "replaced") ||
            HasMetadataProperty(entity.MetadataJson, "supersededByMemoryId") ||
            HasMetadataProperty(entity.MetadataJson, "replacedByMemoryId") ||
            TryGetSupersededByMemoryId(entity.MetadataJson) is { } supersededById &&
            memories.Any(memory => memory.Id == supersededById));

    private static bool IsLifecycleCandidate(MemoryItem entity)
        => entity.Status == MemoryStatus.Active &&
           !entity.IsReadOnly &&
           !HasTag(entity, "keep") &&
           !HasTag(entity, "pinned") &&
           entity.MemoryType is not MemoryType.Decision and not MemoryType.Preference;

    private static bool HasTag(MemoryItem entity, string tag)
        => entity.Tags.Any(candidate => string.Equals(candidate, tag, StringComparison.OrdinalIgnoreCase));

    private static GovernanceDraft CreateMemoryDraft(
        string projectId,
        MemoryItem memory,
        GovernanceFindingType type,
        string keyPrefix,
        string title,
        string summary,
        object details,
        Guid? secondaryMemoryId = null)
    {
        var dedupKey = secondaryMemoryId.HasValue &&
                       type is GovernanceFindingType.MergeMemoryCandidate or GovernanceFindingType.AuthoritativeSourceCandidate
            ? $"{keyPrefix}:{projectId}:{CanonicalPairKey(memory.Id, secondaryMemoryId.Value)}"
            : $"{keyPrefix}:{projectId}:{memory.Id}:{secondaryMemoryId}";
        return new(
            dedupKey,
            type,
            title,
            summary,
            TryGetConnectorId(memory.MetadataJson),
            memory.Id,
            secondaryMemoryId,
            JsonSerializer.Serialize(details, JsonOptions));
    }

    private static bool IsLowValueMemoryCandidate(MemoryItem memory)
        => !memory.IsReadOnly &&
           ((memory.Importance <= .25m && memory.Confidence <= .50m) ||
            HasTag(memory, "removed") ||
            HasTag(memory, "migrated") ||
            string.Equals(memory.Content.Trim(), "REMOVED", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(memory.Content.Trim(), "MIGRATED", StringComparison.OrdinalIgnoreCase));

    private static bool IsInvalidMemoryCandidate(MemoryItem memory, out string reason)
    {
        if (string.IsNullOrWhiteSpace(memory.Title) ||
            (string.IsNullOrWhiteSpace(memory.Content) && string.IsNullOrWhiteSpace(memory.Summary)))
        {
            reason = "title 或 knowledge body/summary 為空。";
            return true;
        }

        try
        {
            using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(memory.MetadataJson) ? "{}" : memory.MetadataJson);
        }
        catch (JsonException)
        {
            reason = "metadataJson 不是合法 JSON。";
            return true;
        }

        if (HasTag(memory, "invalid") || HasTag(memory, "incorrect"))
        {
            reason = "記憶帶有 explicit invalid/incorrect tag。";
            return true;
        }

        reason = string.Empty;
        return false;
    }

    private static string BuildSuggestedActionDedupKey(SuggestedActionType actionType, GovernanceDraft draft)
    {
        if (actionType == SuggestedActionType.ArchiveStaleMemory && draft.PrimaryMemoryId.HasValue)
        {
            return $"action:{actionType}:memory:{draft.PrimaryMemoryId.Value:N}";
        }

        if (actionType == SuggestedActionType.MergeDuplicateCandidate &&
            draft.PrimaryMemoryId.HasValue && draft.SecondaryMemoryId.HasValue)
        {
            var ids = new[] { draft.PrimaryMemoryId.Value, draft.SecondaryMemoryId.Value }
                .OrderBy(x => x)
                .Select(x => x.ToString("N"));
            return $"action:{actionType}:pair:{string.Join(':', ids)}";
        }

        return $"action:{actionType}:{draft.DedupKey}";
    }

    private async Task MaterializeExecutedMergeRelationshipsAsync(
        string projectId,
        IReadOnlyList<MemoryItem> memories,
        ISet<string> replacementPairs,
        CancellationToken cancellationToken)
    {
        if (memories.Count < 2)
        {
            return;
        }

        var executedActions = await dbContext.SuggestedActions
            .AsNoTracking()
            .Where(x => x.ProjectId == projectId &&
                        x.Type == SuggestedActionType.MergeDuplicateCandidate &&
                        x.Status == SuggestedActionStatus.Executed)
            .Select(x => x.PayloadJson)
            .ToListAsync(cancellationToken);
        var findingKeys = executedActions
            .Select(x => TryGetPayloadString(x, "findingId"))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (findingKeys.Length == 0)
        {
            return;
        }

        var findings = await dbContext.GovernanceFindings
            .AsNoTracking()
            .Where(x => findingKeys.Contains(x.DedupKey) &&
                        x.PrimaryMemoryId.HasValue &&
                        x.SecondaryMemoryId.HasValue)
            .ToListAsync(cancellationToken);
        foreach (var finding in findings)
        {
            var left = memories.FirstOrDefault(x => x.Id == finding.PrimaryMemoryId!.Value);
            var right = memories.FirstOrDefault(x => x.Id == finding.SecondaryMemoryId!.Value);
            if (left is null || right is null)
            {
                continue;
            }

            var pairKey = CanonicalPairKey(left.Id, right.Id);
            if (!replacementPairs.Add(pairKey))
            {
                continue;
            }

            var authoritative = new[] { left, right }
                .OrderByDescending(AuthorityScore)
                .ThenByDescending(x => x.UpdatedAt)
                .ThenBy(x => x.Id)
                .First();
            var replaced = authoritative.Id == left.Id ? right : left;
            await dbContext.MemoryLinks.AddAsync(new MemoryLink
            {
                Id = DeterministicReplacementLinkId(replaced.Id, authoritative.Id),
                FromId = replaced.Id,
                ToId = authoritative.Id,
                LinkType = "replaced_by",
                CreatedAt = clock.UtcNow
            }, cancellationToken);
        }
    }

    private async Task SupersedePendingActionsForFindingAsync(
        string projectId,
        string findingDedupKey,
        CancellationToken cancellationToken)
    {
        var pending = await dbContext.SuggestedActions
            .Where(x => x.ProjectId == projectId &&
                        (x.Status == SuggestedActionStatus.Pending || x.Status == SuggestedActionStatus.Accepted))
            .ToListAsync(cancellationToken);
        foreach (var action in pending.Where(x => PayloadReferencesFinding(x.PayloadJson, findingDedupKey)))
        {
            action.Status = SuggestedActionStatus.Superseded;
            action.UpdatedAt = clock.UtcNow;
        }
    }

    private async Task SupersedePendingActionsWithTerminalEquivalentAsync(string projectId, CancellationToken cancellationToken)
    {
        var actions = await dbContext.SuggestedActions
            .Where(x => x.ProjectId == projectId)
            .ToListAsync(cancellationToken);
        foreach (var group in actions
                     .Concat(dbContext.SuggestedActions.Local.Where(x => x.ProjectId == projectId))
                     .DistinctBy(x => x.Id)
                     .Where(x => !string.IsNullOrWhiteSpace(SuggestedActionEquivalence.GetIdentity(x)))
                     .GroupBy(SuggestedActionEquivalence.GetIdentity, StringComparer.Ordinal))
        {
            if (!group.Any(x => x.Status is SuggestedActionStatus.Executed or SuggestedActionStatus.Dismissed or SuggestedActionStatus.Superseded))
            {
                continue;
            }

            foreach (var pending in group.Where(x => x.Status is SuggestedActionStatus.Pending or SuggestedActionStatus.Accepted))
            {
                pending.Status = SuggestedActionStatus.Superseded;
                pending.UpdatedAt = clock.UtcNow;
            }
        }
    }

    private static string GetActionDedupKey(SuggestedAction action)
    {
        if (!string.IsNullOrWhiteSpace(action.DedupKey))
        {
            return action.DedupKey;
        }

        return TryGetPayloadString(action.PayloadJson, "dedupKey") ?? string.Empty;
    }

    private static bool PayloadReferencesFinding(string payloadJson, string findingDedupKey)
        => string.Equals(TryGetPayloadString(payloadJson, "findingId"), findingDedupKey, StringComparison.Ordinal);

    private static string? TryGetPayloadString(string payloadJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(payloadJson) ? "{}" : payloadJson);
            return document.RootElement.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string CanonicalPairKey(Guid left, Guid right)
        => string.Join(':', new[] { left, right }.OrderBy(x => x).Select(x => x.ToString("N")));

    private static Guid DeterministicReplacementLinkId(Guid replacedId, Guid authoritativeId)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes($"replaced_by:{replacedId:N}:{authoritativeId:N}"));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private static string NormalizeGovernanceRunId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.Trim();
        if (normalized.Length > 128)
        {
            throw new InvalidOperationException("GovernanceRunId must not exceed 128 characters.");
        }

        return normalized;
    }

    private static decimal AuthorityScore(MemoryItem memory)
    {
        var score = memory.Confidence * 3m + memory.Importance * 2m + Math.Min(memory.Version, 20) / 20m;
        if (memory.MemoryType == MemoryType.Decision) score += 2m;
        if (HasTag(memory, "authoritative") || HasTag(memory, "source-of-truth")) score += 10m;
        return score;
    }

    private static string? TryGetMetadataString(string metadataJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(propertyName, out var value) &&
                   value.ValueKind == JsonValueKind.String
                ? value.GetString()
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool HasMetadataProperty(string metadataJson, string propertyName)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty(propertyName, out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static Guid? TryGetSupersededByMemoryId(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            foreach (var propertyName in new[] { "supersededByMemoryId", "replacedByMemoryId" })
            {
                if (document.RootElement.TryGetProperty(propertyName, out var value) &&
                    value.ValueKind == JsonValueKind.String &&
                    Guid.TryParse(value.GetString(), out var id))
                {
                    return id;
                }
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static Guid? TryGetConnectorId(string metadataJson)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
            if (document.RootElement.TryGetProperty("connectorId", out var connector) &&
                connector.ValueKind == JsonValueKind.String &&
                Guid.TryParse(connector.GetString(), out var id))
            {
                return id;
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static string NormalizeKey(string input)
        => string.Join(' ', input
            .ToLowerInvariant()
            .Split([' ', '-', '_', ':', '/', '.', ',', ';', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries));

    private static decimal ComputeTokenOverlap(string left, string right)
    {
        var leftTokens = Tokenize(left);
        var rightTokens = Tokenize(right);
        if (leftTokens.Count == 0 || rightTokens.Count == 0)
        {
            return 0m;
        }

        var intersection = leftTokens.Intersect(rightTokens, StringComparer.Ordinal).Count();
        var union = leftTokens.Union(rightTokens, StringComparer.Ordinal).Count();
        return union == 0 ? 0m : intersection / (decimal)union;
    }

    private static HashSet<string> Tokenize(string text)
        => text
            .ToLowerInvariant()
            .Split([' ', '-', '_', ':', '/', '.', ',', ';', '\r', '\n', '\t', '(', ')', '[', ']'], StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length > 2)
            .ToHashSet(StringComparer.Ordinal);

    private static GovernanceFindingResult Map(GovernanceFinding entity)
        => new(
            entity.Id,
            entity.ProjectId,
            entity.SourceConnectionId,
            entity.PrimaryMemoryId,
            entity.SecondaryMemoryId,
            entity.Type,
            entity.Status,
            entity.Title,
            entity.Summary,
            entity.DetailsJson,
            entity.DedupKey,
            entity.CreatedAt,
            entity.UpdatedAt)
        {
            GovernanceReason = entity.GovernanceReason,
            GovernanceRunId = entity.GovernanceRunId,
            GovernanceActor = entity.GovernanceActor,
            GovernanceRetryCount = entity.GovernanceRetryCount,
            GovernanceUpdatedAt = entity.GovernanceUpdatedAt
        };

    private sealed record GovernanceDraft(
        string DedupKey,
        GovernanceFindingType Type,
        string Title,
        string Summary,
        Guid? SourceConnectionId,
        Guid? PrimaryMemoryId,
        Guid? SecondaryMemoryId,
        string DetailsJson);
}

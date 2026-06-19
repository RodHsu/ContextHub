using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

public sealed class GovernanceService(
    IApplicationDbContext dbContext,
    IEmbeddingProvider embeddingProvider,
    ICacheVersionStore cacheStore,
    IClock clock) : IGovernanceService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<GovernanceFindingResult>> ListAsync(GovernanceFindingListRequest request, CancellationToken cancellationToken)
    {
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var query = dbContext.GovernanceFindings.AsNoTracking().Where(x => x.ProjectId == projectId);

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
        entity.Status = GovernanceFindingStatus.Accepted;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task<GovernanceFindingResult> DismissAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await GetRequiredAsync(id, cancellationToken);
        entity.Status = GovernanceFindingStatus.Dismissed;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        return Map(entity);
    }

    public async Task AnalyzeAsync(string projectId, CancellationToken cancellationToken)
    {
        var normalizedProjectId = ProjectContext.Normalize(projectId);
        var now = clock.UtcNow;
        var findings = new List<GovernanceDraft>();
        var sources = await dbContext.SourceConnections
            .AsNoTracking()
            .Where(x => x.ProjectId == normalizedProjectId)
            .ToListAsync(cancellationToken);
        var memories = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => x.ProjectId == normalizedProjectId)
            .ToListAsync(cancellationToken);

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
                    var similarity = ComputeTokenOverlap(left.Summary, right.Summary);
                    if (similarity >= 0.45m)
                    {
                        findings.Add(new GovernanceDraft(
                            $"duplicate:{normalizedProjectId}:{left.Id}:{right.Id}",
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
                            $"conflict:{normalizedProjectId}:{left.Id}:{right.Id}",
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

        var vectorCandidates = await dbContext.MemoryItems
            .AsNoTracking()
            .Include(x => x.Chunks)
                .ThenInclude(x => x.Vectors)
            .Where(x => x.ProjectId == normalizedProjectId)
            .Where(x => x.Status == MemoryStatus.Active)
            .Where(x => x.MemoryType == MemoryType.Artifact)
            .ToListAsync(cancellationToken);
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

        var existing = await dbContext.GovernanceFindings.Where(x => x.ProjectId == normalizedProjectId).ToListAsync(cancellationToken);
        var currentKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var draft in findings)
        {
            currentKeys.Add(draft.DedupKey);
            var entity = existing.FirstOrDefault(x => string.Equals(x.DedupKey, draft.DedupKey, StringComparison.OrdinalIgnoreCase));
            if (entity is null)
            {
                entity = new GovernanceFinding
                {
                    ProjectId = normalizedProjectId,
                    CreatedAt = clock.UtcNow
                };
                await dbContext.GovernanceFindings.AddAsync(entity, cancellationToken);
                existing.Add(entity);
            }

            entity.SourceConnectionId = draft.SourceConnectionId;
            entity.PrimaryMemoryId = draft.PrimaryMemoryId;
            entity.SecondaryMemoryId = draft.SecondaryMemoryId;
            entity.Type = draft.Type;
            entity.Status = GovernanceFindingStatus.Open;
            entity.Title = draft.Title;
            entity.Summary = draft.Summary;
            entity.DetailsJson = draft.DetailsJson;
            entity.DedupKey = draft.DedupKey;
            entity.UpdatedAt = clock.UtcNow;

            await EnsureLinkAsync(draft, cancellationToken);
            await EnsureSuggestedActionAsync(normalizedProjectId, draft, cancellationToken);
        }

        foreach (var entity in existing.Where(x => !currentKeys.Contains(x.DedupKey) && x.Status == GovernanceFindingStatus.Open))
        {
            entity.Status = GovernanceFindingStatus.Resolved;
            entity.UpdatedAt = clock.UtcNow;
        }

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
            GovernanceFindingType.DuplicateCandidate or GovernanceFindingType.DuplicateMemoryCandidate => SuggestedActionType.MergeDuplicateCandidate,
            GovernanceFindingType.ConflictCandidate or GovernanceFindingType.SupersededMemoryCandidate => SuggestedActionType.ReviewConflictCandidate,
            GovernanceFindingType.StaleMemoryCandidate or GovernanceFindingType.LowSignalEpisodeCandidate => SuggestedActionType.ArchiveStaleMemory,
            GovernanceFindingType.MissingSource => SuggestedActionType.ArchiveStaleMemory,
            GovernanceFindingType.ReindexRequired => SuggestedActionType.ReindexProject,
            _ => (SuggestedActionType?)null
        };
        if (!actionType.HasValue)
        {
            return;
        }

        var dedupKey = $"action:{actionType}:{draft.DedupKey}";
        var pendingPayloads = await dbContext.SuggestedActions
            .Where(x => x.ProjectId == projectId &&
                        x.Status == SuggestedActionStatus.Pending &&
                        x.Type == actionType.Value)
            .Select(x => x.PayloadJson)
            .ToListAsync(cancellationToken);
        var exists = pendingPayloads.Any(payload => payload.Contains(dedupKey, StringComparison.Ordinal));
        if (exists)
        {
            return;
        }

        await dbContext.SuggestedActions.AddAsync(new SuggestedAction
        {
            ProjectId = projectId,
            Type = actionType.Value,
            Status = SuggestedActionStatus.Pending,
            Title = draft.Title,
            Summary = draft.Summary,
            PayloadJson = JsonSerializer.Serialize(new Dictionary<string, object?>
            {
                ["dedupKey"] = dedupKey,
                ["findingId"] = draft.DedupKey,
                ["sourceConnectionId"] = draft.SourceConnectionId,
                ["primaryMemoryId"] = draft.PrimaryMemoryId,
                ["projectId"] = projectId
            }, JsonOptions),
            CreatedAt = clock.UtcNow,
            UpdatedAt = clock.UtcNow
        }, cancellationToken);
    }

    private async Task<GovernanceFinding> GetRequiredAsync(Guid id, CancellationToken cancellationToken)
        => await dbContext.GovernanceFindings.FirstOrDefaultAsync(x => x.Id == id, cancellationToken)
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
            entity.UpdatedAt);

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

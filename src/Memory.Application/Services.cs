using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Memory.Domain;

namespace Memory.Application;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public sealed class ChunkingService : IChunkingService
{
    public IReadOnlyList<ChunkDraft> Chunk(MemoryType memoryType, string sourceType, string content)
    {
        var normalizedSourceType = sourceType ?? string.Empty;
        var kind = ResolveChunkKind(memoryType, normalizedSourceType);
        var (targetTokens, overlap) = ResolveChunkWindow(kind);
        var segments = ExpandSegments(kind, content, targetTokens);

        if (segments.Length == 0)
        {
            return [new ChunkDraft(kind, 0, content.Trim(), "{}")];
        }

        var results = new List<ChunkDraft>();
        var currentSegments = new List<string>();
        var currentTokens = 0;

        foreach (var segment in segments)
        {
            var trimmed = segment.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            var segmentTokens = ApproximateTokenCount(trimmed);
            if (currentTokens > 0 && currentTokens + segmentTokens > targetTokens)
            {
                AddChunk(results, kind, currentSegments, currentTokens);
                currentSegments = BuildOverlapSegments(currentSegments, overlap);
                currentTokens = currentSegments.Sum(ApproximateTokenCount);
                while (currentTokens > 0 && currentTokens + segmentTokens > targetTokens && currentSegments.Count > 0)
                {
                    currentTokens -= ApproximateTokenCount(currentSegments[0]);
                    currentSegments.RemoveAt(0);
                }
            }

            currentSegments.Add(trimmed);
            currentTokens += segmentTokens;
        }

        if (currentSegments.Count > 0)
        {
            AddChunk(results, kind, currentSegments, currentTokens);
        }

        return results.Count == 0 ? [new ChunkDraft(kind, 0, content.Trim(), "{}")] : results;
    }

    internal static int ApproximateTokenCount(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return 0;
        }

        var count = 0;
        for (var index = 0; index < text.Length;)
        {
            var current = text[index];
            if (char.IsWhiteSpace(current))
            {
                index++;
                continue;
            }

            if (IsCjkLike(current))
            {
                count++;
                index++;
                continue;
            }

            if (IsAsciiWordLike(current))
            {
                var start = index;
                while (index < text.Length && IsAsciiWordLike(text[index]))
                {
                    index++;
                }

                count += Math.Max(1, (int)Math.Ceiling((index - start) / 4d));
                continue;
            }

            if (char.IsLetterOrDigit(current))
            {
                var start = index;
                while (index < text.Length && char.IsLetterOrDigit(text[index]) && !IsAsciiWordLike(text[index]) && !IsCjkLike(text[index]))
                {
                    index++;
                }

                count += Math.Max(1, (int)Math.Ceiling((index - start) / 2d));
                continue;
            }

            count++;
            index++;
        }

        return count;
    }

    private static (int TargetTokens, int Overlap) ResolveChunkWindow(ChunkKind kind)
        => kind switch
        {
            ChunkKind.Code => (180, 40),
            ChunkKind.Log => (220, 30),
            _ => (360, 60)
        };

    private static string[] ExpandSegments(ChunkKind kind, string content, int targetTokens)
    {
        var segments = kind switch
        {
            ChunkKind.Code => content.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            ChunkKind.Log => content.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            _ => content.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };

        var results = new List<string>();
        foreach (var segment in segments)
        {
            AppendExpandedSegments(results, kind, segment, targetTokens);
        }

        return results.ToArray();
    }

    private static void AppendExpandedSegments(List<string> results, ChunkKind kind, string segment, int targetTokens)
    {
        var trimmed = segment.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return;
        }

        if (ApproximateTokenCount(trimmed) <= targetTokens)
        {
            results.Add(trimmed);
            return;
        }

        var refinedSegments = SplitOversizedSegment(kind, trimmed);
        if (refinedSegments.Count > 1)
        {
            foreach (var refined in refinedSegments)
            {
                AppendExpandedSegments(results, kind, refined, targetTokens);
            }

            return;
        }

        foreach (var slice in SplitByEstimatedTokens(trimmed, targetTokens))
        {
            if (!string.IsNullOrWhiteSpace(slice))
            {
                results.Add(slice.Trim());
            }
        }
    }

    private static IReadOnlyList<string> SplitOversizedSegment(ChunkKind kind, string text)
    {
        var refined = kind switch
        {
            ChunkKind.Code => SplitByLine(text),
            ChunkKind.Log => SplitByLine(text),
            _ => SplitDocumentSegment(text)
        };

        return refined.Count > 1 ? refined : SplitByClause(text);
    }

    private static List<string> SplitByLine(string text)
        => text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static x => !string.IsNullOrWhiteSpace(x))
            .ToList();

    private static IReadOnlyList<string> SplitDocumentSegment(string text)
    {
        var results = new List<string>();
        var builder = new StringBuilder();

        foreach (var current in text)
        {
            builder.Append(current);

            if (IsSentenceBoundary(current))
            {
                FlushBuilder(results, builder);
            }
        }

        FlushBuilder(results, builder);
        return results;
    }

    private static IReadOnlyList<string> SplitByClause(string text)
    {
        var results = new List<string>();
        var builder = new StringBuilder();

        foreach (var current in text)
        {
            builder.Append(current);

            if (IsClauseBoundary(current))
            {
                FlushBuilder(results, builder);
            }
        }

        FlushBuilder(results, builder);
        return results;
    }

    private static IReadOnlyList<string> SplitByEstimatedTokens(string text, int targetTokens)
    {
        var results = new List<string>();
        var builder = new StringBuilder();
        double currentTokens = 0;

        foreach (var current in text)
        {
            builder.Append(current);
            currentTokens += ApproximateTokenWeight(current);

            if (currentTokens < targetTokens)
            {
                continue;
            }

            FlushBuilder(results, builder);
            currentTokens = 0;
        }

        FlushBuilder(results, builder);
        return results;
    }

    private static void AddChunk(List<ChunkDraft> results, ChunkKind kind, IReadOnlyList<string> segments, int estimatedTokens)
    {
        if (segments.Count == 0)
        {
            return;
        }

        var separator = kind == ChunkKind.Log ? Environment.NewLine : Environment.NewLine + Environment.NewLine;
        var text = string.Join(separator, segments).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var metadata = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["strategy"] = "heuristic-v2",
            ["estimatedTokens"] = estimatedTokens
        });
        results.Add(new ChunkDraft(kind, results.Count, text, metadata));
    }

    private static List<string> BuildOverlapSegments(IReadOnlyList<string> segments, int overlapTokens)
    {
        var overlap = new List<string>();
        var consumed = 0;

        for (var index = segments.Count - 1; index >= 0; index--)
        {
            var segment = segments[index];
            var segmentTokens = ApproximateTokenCount(segment);
            if (overlap.Count > 0 && consumed + segmentTokens > overlapTokens)
            {
                break;
            }

            overlap.Insert(0, segment);
            consumed += segmentTokens;
        }

        return overlap;
    }

    private static void FlushBuilder(List<string> results, StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        var value = builder.ToString().Trim();
        builder.Clear();
        if (!string.IsNullOrWhiteSpace(value))
        {
            results.Add(value);
        }
    }

    private static bool IsAsciiWordLike(char value)
        => value is >= 'a' and <= 'z'
            or >= 'A' and <= 'Z'
            or >= '0' and <= '9'
            or '_';

    private static bool IsCjkLike(char value)
        => value is >= '\u3400' and <= '\u4DBF'
            or >= '\u4E00' and <= '\u9FFF'
            or >= '\u3040' and <= '\u30FF'
            or >= '\uAC00' and <= '\uD7AF'
            or >= '\u1100' and <= '\u11FF';

    private static bool IsSentenceBoundary(char value)
        => value is '.' or '!' or '?' or '。' or '！' or '？' or '\n' or '\r';

    private static bool IsClauseBoundary(char value)
        => value is ',' or ';' or ':' or '，' or '；' or '：' or '、';

    private static double ApproximateTokenWeight(char value)
    {
        if (char.IsWhiteSpace(value))
        {
            return 0;
        }

        if (IsCjkLike(value))
        {
            return 1;
        }

        if (IsAsciiWordLike(value))
        {
            return 0.25;
        }

        if (char.IsLetterOrDigit(value))
        {
            return 0.5;
        }

        return 1;
    }

    private static ChunkKind ResolveChunkKind(MemoryType memoryType, string sourceType)
    {
        if (memoryType == MemoryType.Episode || sourceType.Contains("log", StringComparison.OrdinalIgnoreCase))
        {
            return ChunkKind.Log;
        }

        if (sourceType.Contains("code", StringComparison.OrdinalIgnoreCase))
        {
            return ChunkKind.Code;
        }

        return ChunkKind.Document;
    }
}

public sealed class MemoryService(
    IApplicationDbContext dbContext,
    IChunkingService chunkingService,
    IHybridSearchStore searchStore,
    IEmbeddingProvider embeddingProvider,
    ICacheVersionStore cacheStore,
    IClock clock,
    IInstanceBehaviorSettingsAccessor behaviorSettingsAccessor,
    IRetrievalTelemetryService retrievalTelemetryService) : IMemoryService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<MemoryDocument> UpsertAsync(MemoryUpsertRequest request, CancellationToken cancellationToken)
    {
        var externalKey = NormalizeRequiredText(request.ExternalKey, nameof(request.ExternalKey));
        var title = NormalizeRequiredText(request.Title, nameof(request.Title));
        var content = request.Content ?? string.Empty;
        var summary = NormalizeSummary(request.Summary, title, content);
        var sourceType = NormalizeSourceType(request.SourceType);
        var sourceRef = NormalizeSourceRef(request.SourceRef, externalKey);
        var tags = NormalizeTags(request.Tags);
        var metadataJson = NormalizeMetadataJson(request.MetadataJson);
        var projectId = ProjectContext.Normalize(request.ProjectId, ResolveDefaultProjectId(request.Scope, request.MemoryType));
        EnsureWritableProject(projectId, allowSharedLayer: IsSummaryWrite(request));

        var entity = await dbContext.MemoryItems
            .FirstOrDefaultAsync(x => x.ProjectId == projectId && x.ExternalKey == externalKey, cancellationToken);
        var previousType = entity?.MemoryType;

        if (entity is null)
        {
            entity = new MemoryItem
            {
                ProjectId = projectId,
                ExternalKey = externalKey,
                Scope = request.Scope,
                MemoryType = request.MemoryType,
                Title = title,
                Content = content,
                Summary = summary,
                SourceType = sourceType,
                SourceRef = sourceRef,
                Tags = tags,
                Importance = request.Importance,
                Confidence = request.Confidence,
                MetadataJson = metadataJson,
                IsReadOnly = projectId == ProjectContext.SharedProjectId,
                CreatedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            };
            await dbContext.MemoryItems.AddAsync(entity, cancellationToken);
        }
        else
        {
            EnsureMemoryWritable(entity, allowSharedLayer: IsSummaryWrite(request));
            entity.Scope = request.Scope;
            entity.MemoryType = request.MemoryType;
            entity.Title = title;
            entity.Content = content;
            entity.Summary = summary;
            entity.SourceType = sourceType;
            entity.SourceRef = sourceRef;
            entity.Tags = tags;
            entity.Importance = request.Importance;
            entity.Confidence = request.Confidence;
            entity.MetadataJson = metadataJson;
            entity.IsReadOnly = projectId == ProjectContext.SharedProjectId;
            entity.Version += 1;
            entity.UpdatedAt = clock.UtcNow;
        }

        await AddRevisionAsync(entity, "upsert", cancellationToken);
        await ReplaceChunksAsync(entity, content, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await InvalidateCachesAsync(cancellationToken);

        if (!entity.IsReadOnly)
        {
            await EnqueueReindexAsync(new EnqueueReindexRequest(MemoryItemId: entity.Id, ProjectId: entity.ProjectId), cancellationToken);
        }

        var behavior = await behaviorSettingsAccessor.GetCurrentAsync(cancellationToken);
        if (behavior.SharedSummaryAutoRefreshEnabled &&
            ShouldAutoRefreshSharedSummary(previousType, entity.MemoryType, entity.IsReadOnly, entity.ProjectId))
        {
            await EnqueuePendingSummaryRefreshIfNeededAsync(entity.ProjectId, cancellationToken);
        }

        return Map(entity);
    }

    public async Task<MemoryDocument> UpdateAsync(MemoryUpdateRequest request, CancellationToken cancellationToken)
    {
        var projectId = request.ProjectId == null ? null : ProjectContext.Normalize(request.ProjectId);
        var entity = await dbContext.MemoryItems
            .FirstOrDefaultAsync(x => x.Id == request.Id && (projectId == null || x.ProjectId == projectId), cancellationToken)
            ?? throw new InvalidOperationException($"Memory item '{request.Id}' was not found.");
        var previousType = entity.MemoryType;

        EnsureMemoryWritable(entity);

        entity.Title = request.Title ?? entity.Title;
        entity.Content = request.Content ?? entity.Content;
        entity.Summary = request.Summary ?? entity.Summary;
        entity.Tags = request.Tags?.ToArray() ?? entity.Tags;
        entity.Importance = request.Importance ?? entity.Importance;
        entity.Confidence = request.Confidence ?? entity.Confidence;
        entity.MetadataJson = request.MetadataJson ?? entity.MetadataJson;
        entity.Version += 1;
        entity.UpdatedAt = clock.UtcNow;

        await AddRevisionAsync(entity, "update", cancellationToken);
        await ReplaceChunksAsync(entity, entity.Content, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await InvalidateCachesAsync(cancellationToken);
        await EnqueueReindexAsync(new EnqueueReindexRequest(MemoryItemId: entity.Id, ProjectId: entity.ProjectId), cancellationToken);

        var behavior = await behaviorSettingsAccessor.GetCurrentAsync(cancellationToken);
        if (behavior.SharedSummaryAutoRefreshEnabled &&
            ShouldAutoRefreshSharedSummary(previousType, entity.MemoryType, entity.IsReadOnly, entity.ProjectId))
        {
            await EnqueuePendingSummaryRefreshIfNeededAsync(entity.ProjectId, cancellationToken);
        }

        return Map(entity);
    }

    public async Task<MemoryDocument?> GetAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MemoryItems.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return entity is null ? null : Map(entity);
    }

    public async Task<IReadOnlyList<MemorySearchHit>> SearchAsync(MemorySearchRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var allowedProjects = ProjectContext.ResolveSearchProjects(request.ProjectId, request.IncludedProjectIds, request.QueryMode, request.UseSummaryLayer);
        var version = await cacheStore.GetVersionAsync(cancellationToken);
        var cacheKey = $"search:{version}:{Sha(request.Query)}:{request.Limit}:{request.IncludeArchived}:{string.Join("|", allowedProjects.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
        var cached = await cacheStore.GetAsync<IReadOnlyList<MemorySearchHit>>(cacheKey, cancellationToken);
        var cacheHit = cached is not null;
        if (cached is not null)
        {
            await TryRecordSearchTelemetryAsync(request, cached, cacheHit, stopwatch.Elapsed.TotalMilliseconds, true, string.Empty, cancellationToken);
            return cached;
        }

        try
        {
            var keywordHits = await searchStore.SearchKeywordChunksAsync(request.Query, request.Limit * 4, cancellationToken);
            var queryVector = await embeddingProvider.EmbedAsync(request.Query, EmbeddingPurpose.Query, cancellationToken);
            var semanticHits = await searchStore.SearchVectorChunksAsync(queryVector, request.Limit * 4, cancellationToken);

            var itemIds = keywordHits.Select(x => x.MemoryId).Concat(semanticHits.Select(x => x.MemoryId)).Distinct().ToArray();
            var items = await dbContext.MemoryItems
                .Where(x => itemIds.Contains(x.Id))
                .Where(x => allowedProjects.Contains(x.ProjectId))
                .ToDictionaryAsync(x => x.Id, cancellationToken);
            var merged = HybridSearchComposer.Compose(keywordHits, semanticHits, items, request.Limit, request.IncludeArchived);

            await cacheStore.SetAsync(cacheKey, merged, TimeSpan.FromMinutes(5), cancellationToken);
            await TryRecordSearchTelemetryAsync(request, merged, cacheHit, stopwatch.Elapsed.TotalMilliseconds, true, string.Empty, cancellationToken);
            return merged;
        }
        catch (Exception ex)
        {
            await TryRecordSearchTelemetryAsync(request, [], cacheHit, stopwatch.Elapsed.TotalMilliseconds, false, ex.Message, cancellationToken);
            throw;
        }
    }

    public async Task<WorkingContextResult> BuildWorkingContextAsync(WorkingContextRequest request, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var allowedProjects = ProjectContext.ResolveSearchProjects(request.ProjectId, request.IncludedProjectIds, request.QueryMode, request.UseSummaryLayer);
        var version = await cacheStore.GetVersionAsync(cancellationToken);
        var cacheKey = $"context:{version}:{Sha(request.Query)}:{request.Limit}:{request.RecentLogLimit}:{string.Join("|", allowedProjects.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))}";
        var cached = await cacheStore.GetAsync<WorkingContextResult>(cacheKey, cancellationToken);
        var cacheHit = cached is not null;
        if (cached is not null)
        {
            await TryRecordWorkingContextTelemetryAsync(request, [], cached, cacheHit, false, stopwatch.Elapsed.TotalMilliseconds, true, string.Empty, cancellationToken);
            return cached;
        }

        IReadOnlyList<MemorySearchHit> hits = [];
        var usedFallback = false;
        try
        {
            hits = await SearchAsync(
                new MemorySearchRequest(
                    request.Query,
                    request.Limit * 3,
                    false,
                    request.ProjectId,
                    request.IncludedProjectIds,
                    request.QueryMode,
                    request.UseSummaryLayer,
                    new RetrievalTelemetryContext("build_working_context.search", "internal", "working context seed search", false)),
                cancellationToken);
            if (hits.Count == 0)
            {
                hits = await LoadFallbackWorkingContextHitsAsync(allowedProjects, request.Limit * 3, cancellationToken);
                usedFallback = hits.Count > 0;
            }

            var logService = new LogQueryService(dbContext);
            var recentLogs = await logService.SearchAsync(
                new LogQueryRequest(Query: request.Query, Limit: request.RecentLogLimit, ProjectId: request.ProjectId),
                cancellationToken);
            var userPreferenceSearch = await SearchUserPreferencesAsync(request.Query, 3, cancellationToken);

            var facts = MapContext(hits.Where(x => x.MemoryType == MemoryType.Fact).Take(request.Limit));
            var decisions = MapContext(hits.Where(x => x.MemoryType == MemoryType.Decision).Take(request.Limit));
            var episodes = MapContext(hits.Where(x => x.MemoryType == MemoryType.Episode).Take(request.Limit));
            var artifacts = MapContext(hits.Where(x => x.MemoryType is MemoryType.Artifact or MemoryType.Summary).Take(request.Limit));
            var suggestedTests = recentLogs.Where(x => x.Level is "Error" or "Warning")
                .Select(x => $"Verify service '{x.ServiceName}' around trace '{x.TraceId}'.")
                .Distinct()
                .Take(request.Limit)
                .ToArray();
            var citations = hits.Take(request.Limit * 2)
                .Select(x => new WorkingContextCitation(x.MemoryId, null, x.SourceRef, x.Excerpt, x.ProjectId))
                .Concat(userPreferenceSearch.Citations)
                .ToArray();

            var result = new WorkingContextResult(
                facts,
                decisions,
                episodes,
                artifacts,
                recentLogs,
                userPreferenceSearch.Preferences,
                suggestedTests,
                citations);
            await cacheStore.SetAsync(cacheKey, result, TimeSpan.FromMinutes(5), cancellationToken);
            await TryRecordWorkingContextTelemetryAsync(request, hits, result, cacheHit, usedFallback, stopwatch.Elapsed.TotalMilliseconds, true, string.Empty, cancellationToken);
            return result;
        }
        catch (Exception ex)
        {
            await TryRecordWorkingContextTelemetryAsync(
                request,
                hits,
                new WorkingContextResult([], [], [], [], [], [], [], []),
                cacheHit,
                usedFallback,
                stopwatch.Elapsed.TotalMilliseconds,
                false,
                ex.Message,
                cancellationToken);
            throw;
        }
    }

    private async Task<IReadOnlyList<MemorySearchHit>> LoadFallbackWorkingContextHitsAsync(
        IReadOnlyList<string> allowedProjects,
        int limit,
        CancellationToken cancellationToken)
    {
        if (allowedProjects.Count == 0 || limit < 1)
        {
            return [];
        }

        var items = await dbContext.MemoryItems
            .AsNoTracking()
            .Where(x => allowedProjects.Contains(x.ProjectId))
            .Where(x => x.Status != MemoryStatus.Archived)
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return items
            .Select(item => new MemorySearchHit(
                item.Id,
                item.Title,
                item.MemoryType,
                item.Scope,
                item.Importance,
                BuildFallbackExcerpt(item),
                item.SourceType,
                item.SourceRef,
                item.Tags,
                item.ProjectId))
            .ToArray();
    }

    private static string BuildFallbackExcerpt(MemoryItem item)
    {
        var excerpt = !string.IsNullOrWhiteSpace(item.Summary)
            ? item.Summary
            : item.Content;
        if (string.IsNullOrWhiteSpace(excerpt))
        {
            return item.Title;
        }

        const int maxLength = 220;
        excerpt = excerpt.Trim();
        return excerpt.Length <= maxLength ? excerpt : $"{excerpt[..maxLength]}…";
    }

    private async Task TryRecordSearchTelemetryAsync(
        MemorySearchRequest request,
        IReadOnlyList<MemorySearchHit> hits,
        bool cacheHit,
        double durationMs,
        bool success,
        string error,
        CancellationToken cancellationToken)
    {
        if (request.Telemetry?.Enabled == false)
        {
            return;
        }

        var context = request.Telemetry ?? new RetrievalTelemetryContext("memory.search", "internal", "memory search");
        var purpose = string.IsNullOrWhiteSpace(context.Purpose) ? "memory search" : context.Purpose!;
        var hitSnapshots = hits
            .Take(Math.Min(request.Limit, 10))
            .Select((hit, index) => new RetrievalTelemetryHitWriteRequest(
                index + 1,
                hit.MemoryId,
                hit.Title,
                hit.MemoryType.ToString(),
                hit.SourceType,
                hit.SourceRef,
                hit.Score,
                hit.Excerpt,
                hit.ProjectId))
            .ToArray();

        await TryRecordTelemetryAsync(
            new RetrievalTelemetryWriteRequest(
                ProjectContext.Normalize(request.ProjectId),
                context.Channel,
                context.EntryPoint,
                purpose,
                request.Query,
                request.QueryMode.ToString(),
                request.IncludedProjectIds ?? [],
                request.UseSummaryLayer,
                request.Limit,
                cacheHit,
                hits.Count,
                durationMs,
                success,
                error,
                "{}",
                GetTraceId(),
                GetRequestId(),
                hitSnapshots),
            cancellationToken);
    }

    private async Task TryRecordWorkingContextTelemetryAsync(
        WorkingContextRequest request,
        IReadOnlyList<MemorySearchHit> hits,
        WorkingContextResult result,
        bool cacheHit,
        bool usedFallback,
        double durationMs,
        bool success,
        string error,
        CancellationToken cancellationToken)
    {
        if (request.Telemetry?.Enabled == false)
        {
            return;
        }

        var context = request.Telemetry ?? new RetrievalTelemetryContext("build_working_context", "internal", "working context bootstrap");
        var purpose = string.IsNullOrWhiteSpace(context.Purpose) ? "working context bootstrap" : context.Purpose!;
        var metadataJson = JsonSerializer.Serialize(new
        {
            request.RecentLogLimit,
            usedFallback,
            facts = result.Facts.Count,
            decisions = result.Decisions.Count,
            episodes = result.Episodes.Count,
            artifacts = result.Artifacts.Count,
            recentLogs = result.RecentLogs.Count,
            userPreferences = result.UserPreferences.Count,
            suggestedTests = result.SuggestedTests.Count
        }, JsonOptions);
        var hitSnapshots = hits
            .Take(Math.Min(request.Limit * 2, 10))
            .Select((hit, index) => new RetrievalTelemetryHitWriteRequest(
                index + 1,
                hit.MemoryId,
                hit.Title,
                hit.MemoryType.ToString(),
                hit.SourceType,
                hit.SourceRef,
                hit.Score,
                hit.Excerpt,
                hit.ProjectId))
            .ToArray();

        await TryRecordTelemetryAsync(
            new RetrievalTelemetryWriteRequest(
                ProjectContext.Normalize(request.ProjectId),
                context.Channel,
                context.EntryPoint,
                purpose,
                request.Query,
                request.QueryMode.ToString(),
                request.IncludedProjectIds ?? [],
                request.UseSummaryLayer,
                request.Limit,
                cacheHit,
                result.Facts.Count + result.Decisions.Count + result.Episodes.Count + result.Artifacts.Count,
                durationMs,
                success,
                error,
                metadataJson,
                GetTraceId(),
                GetRequestId(),
                hitSnapshots),
            cancellationToken);
    }

    private async Task TryRecordTelemetryAsync(RetrievalTelemetryWriteRequest request, CancellationToken cancellationToken)
    {
        try
        {
            await retrievalTelemetryService.RecordAsync(request, cancellationToken);
        }
        catch
        {
        }
    }

    private static string GetTraceId()
        => Activity.Current?.TraceId.ToString() ?? string.Empty;

    private static string GetRequestId()
        => Activity.Current?.SpanId.ToString() ?? string.Empty;

    public async Task<EnqueueReindexResult> EnqueueReindexAsync(EnqueueReindexRequest request, CancellationToken cancellationToken)
    {
        var job = new MemoryJob
        {
            ProjectId = ProjectContext.Normalize(request.ProjectId),
            JobType = MemoryJobType.Reindex,
            Status = MemoryJobStatus.Pending,
            PayloadJson = JsonSerializer.Serialize(
                new ReindexJobPayload(request.ModelKey ?? embeddingProvider.ModelKey, request.MemoryItemId, ProjectContext.Normalize(request.ProjectId)),
                JsonOptions),
            CreatedAt = clock.UtcNow
        };

        await dbContext.MemoryJobs.AddAsync(job, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.PublishJobSignalAsync(job.Id, cancellationToken);

        return new EnqueueReindexResult(job.Id, job.Status);
    }

    public async Task<EnqueueSummaryRefreshResult> EnqueueSummaryRefreshAsync(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken)
    {
        var rebuildAll = string.IsNullOrWhiteSpace(request.ProjectId);
        var projectId = rebuildAll ? null : ProjectContext.Normalize(request.ProjectId);
        var referenced = rebuildAll
            ? []
            : (request.IncludedProjectIds ?? [])
            .Select(x => ProjectContext.Normalize(x))
            .Where(x => !ProjectContext.IsShared(x) && !ProjectContext.IsUser(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var job = new MemoryJob
        {
            ProjectId = rebuildAll ? ProjectContext.SharedProjectId : projectId!,
            JobType = MemoryJobType.RefreshSummary,
            Status = MemoryJobStatus.Pending,
            PayloadJson = JsonSerializer.Serialize(new SummaryRefreshJobPayload(projectId, referenced, rebuildAll), JsonOptions),
            CreatedAt = clock.UtcNow
        };

        await dbContext.MemoryJobs.AddAsync(job, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.PublishJobSignalAsync(job.Id, cancellationToken);

        return new EnqueueSummaryRefreshResult(job.Id, job.Status);
    }

    private async Task EnqueuePendingSummaryRefreshIfNeededAsync(string projectId, CancellationToken cancellationToken)
    {
        var normalizedProjectId = ProjectContext.Normalize(projectId);
        if (ProjectContext.IsShared(normalizedProjectId) || ProjectContext.IsUser(normalizedProjectId))
        {
            return;
        }

        var hasPending = await dbContext.MemoryJobs.AnyAsync(
            x => x.ProjectId == normalizedProjectId &&
                 x.JobType == MemoryJobType.RefreshSummary &&
                 x.Status == MemoryJobStatus.Pending,
            cancellationToken);

        if (hasPending)
        {
            return;
        }

        await EnqueueSummaryRefreshAsync(new EnqueueSummaryRefreshRequest(normalizedProjectId), cancellationToken);
    }

    private static bool ShouldAutoRefreshSharedSummary(MemoryType? previousType, MemoryType currentType, bool isReadOnly, string projectId)
    {
        if (isReadOnly || ProjectContext.IsShared(projectId) || ProjectContext.IsUser(projectId))
        {
            return false;
        }

        return IsSharedSummarySourceType(currentType) || (previousType.HasValue && IsSharedSummarySourceType(previousType.Value));
    }

    private static string NormalizeRequiredText(string? value, string fieldName)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException($"{fieldName} is required.");
        }

        return normalized;
    }

    private static string NormalizeSummary(string? summary, string title, string content)
    {
        var normalized = summary?.Trim();
        if (!string.IsNullOrWhiteSpace(normalized))
        {
            return normalized;
        }

        if (!string.IsNullOrWhiteSpace(content))
        {
            var collapsed = content.ReplaceLineEndings(" ").Trim();
            return collapsed.Length <= 280
                ? collapsed
                : $"{collapsed[..280].TrimEnd()}…";
        }

        return title;
    }

    private static string NormalizeSourceType(string? sourceType)
        => string.IsNullOrWhiteSpace(sourceType)
            ? "document"
            : sourceType.Trim();

    private static string NormalizeSourceRef(string? sourceRef, string externalKey)
        => string.IsNullOrWhiteSpace(sourceRef)
            ? externalKey
            : sourceRef.Trim();

    private static string[] NormalizeTags(IReadOnlyList<string>? tags)
        => tags is null
            ? []
            : tags
                .Where(tag => !string.IsNullOrWhiteSpace(tag))
                .Select(tag => tag.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static string NormalizeMetadataJson(string? metadataJson)
        => string.IsNullOrWhiteSpace(metadataJson)
            ? "{}"
            : metadataJson;

    private static bool IsSharedSummarySourceType(MemoryType memoryType)
        => memoryType is MemoryType.Fact or MemoryType.Decision or MemoryType.Artifact;

    public async Task<JobResult?> GetJobAsync(Guid id, CancellationToken cancellationToken)
    {
        var job = await dbContext.MemoryJobs.FirstOrDefaultAsync(x => x.Id == id, cancellationToken);
        return job is null ? null : Map(job);
    }

    public async Task<MemoryDocument> PromoteLogSliceAsync(PromoteLogSliceRequest request, CancellationToken cancellationToken)
    {
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var logService = new LogQueryService(dbContext);
        var logs = await logService.SearchAsync(
            new LogQueryRequest(
                Query: request.Query,
                ServiceName: request.ServiceName,
                TraceId: request.TraceId,
                From: request.From,
                To: request.To,
                Limit: 100,
                ProjectId: projectId),
            cancellationToken);

        var content = string.Join(Environment.NewLine, logs.Select(x => $"[{x.CreatedAt:O}] {x.ServiceName} {x.Level}: {x.Message} {x.Exception}".Trim()));
        var upsert = new MemoryUpsertRequest(
            ExternalKey: $"log-promoted:{Sha(projectId + request.Title + content)}",
            Scope: MemoryScope.Project,
            MemoryType: MemoryType.Episode,
            Title: request.Title,
            Content: content,
            Summary: logs.FirstOrDefault()?.Message ?? request.Title,
            SourceType: "runtime-log",
            SourceRef: request.TraceId ?? request.ServiceName ?? "runtime-log",
            Tags: request.Tags ?? ["runtime-log"],
            Importance: 0.7m,
            Confidence: 0.8m,
            ProjectId: projectId);

        return await UpsertAsync(upsert, cancellationToken);
    }

    public async Task<UserPreferenceResult> UpsertUserPreferenceAsync(UserPreferenceUpsertRequest request, CancellationToken cancellationToken)
    {
        var upsertRequest = new MemoryUpsertRequest(
            ExternalKey: $"user-preference:{NormalizePreferenceKey(request.Key)}",
            Scope: MemoryScope.User,
            MemoryType: MemoryType.Preference,
            Title: request.Title,
            Content: request.Content,
            Summary: request.Rationale,
            SourceType: "user-preference",
            SourceRef: request.Key.Trim(),
            Tags: BuildPreferenceTags(request),
            Importance: request.Importance,
            Confidence: request.Confidence,
            MetadataJson: UserPreferenceMetadataSerializer.Serialize(request.Kind, request.Rationale),
            ProjectId: ProjectContext.UserProjectId);

        var document = await UpsertAsync(upsertRequest, cancellationToken);
        return await GetUserPreferenceRequiredAsync(document.Id, cancellationToken);
    }

    public async Task<IReadOnlyList<UserPreferenceResult>> ListUserPreferencesAsync(UserPreferenceListRequest request, CancellationToken cancellationToken)
    {
        var items = await dbContext.MemoryItems
            .Where(x => x.ProjectId == ProjectContext.UserProjectId)
            .Where(x => x.Scope == MemoryScope.User && x.MemoryType == MemoryType.Preference)
            .Where(x => request.IncludeArchived || x.Status == MemoryStatus.Active)
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.Confidence)
            .ThenByDescending(x => x.UpdatedAt)
            .Take(Math.Max(request.Limit * 4, 32))
            .ToListAsync(cancellationToken);

        return items
            .Select(MapUserPreference)
            .Where(x => !request.Kind.HasValue || x.Kind == request.Kind.Value)
            .Take(request.Limit)
            .ToArray();
    }

    public async Task<UserPreferenceResult> ArchiveUserPreferenceAsync(UserPreferenceArchiveRequest request, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MemoryItems
            .FirstOrDefaultAsync(
                x => x.Id == request.Id &&
                     x.ProjectId == ProjectContext.UserProjectId &&
                     x.Scope == MemoryScope.User &&
                     x.MemoryType == MemoryType.Preference,
                cancellationToken)
            ?? throw new InvalidOperationException($"User preference '{request.Id}' was not found.");

        entity.Status = request.Archived ? MemoryStatus.Archived : MemoryStatus.Active;
        entity.Version += 1;
        entity.UpdatedAt = clock.UtcNow;
        await dbContext.MemoryItemRevisions.AddAsync(new MemoryItemRevision
        {
            MemoryItemId = entity.Id,
            Version = entity.Version,
            Title = entity.Title,
            Content = entity.Content,
            Summary = entity.Summary,
            MetadataJson = entity.MetadataJson,
            ChangedBy = request.Archived ? "archive" : "restore",
            CreatedAt = clock.UtcNow
        }, cancellationToken);

        await dbContext.SaveChangesAsync(cancellationToken);
        await InvalidateCachesAsync(cancellationToken);
        return MapUserPreference(entity);
    }

    private async Task InvalidateCachesAsync(CancellationToken cancellationToken)
        => await cacheStore.IncrementAsync(cancellationToken);

    private async Task<UserPreferenceSearchResult> SearchUserPreferencesAsync(string query, int limit, CancellationToken cancellationToken)
    {
        var keywordHits = await searchStore.SearchKeywordChunksAsync(query, limit * 4, cancellationToken);
        var queryVector = await embeddingProvider.EmbedAsync(query, EmbeddingPurpose.Query, cancellationToken);
        var semanticHits = await searchStore.SearchVectorChunksAsync(queryVector, limit * 4, cancellationToken);

        var itemIds = keywordHits.Select(x => x.MemoryId).Concat(semanticHits.Select(x => x.MemoryId)).Distinct().ToArray();
        var items = await dbContext.MemoryItems
            .Where(x => itemIds.Contains(x.Id))
            .Where(x => x.ProjectId == ProjectContext.UserProjectId)
            .Where(x => x.Scope == MemoryScope.User && x.MemoryType == MemoryType.Preference && x.Status == MemoryStatus.Active)
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        var hits = HybridSearchComposer.Compose(keywordHits, semanticHits, items, limit, includeArchived: false);
        if (hits.Count > 0)
        {
            return new UserPreferenceSearchResult(
                hits.Select(x => MapUserPreference(items[x.MemoryId])).ToArray(),
                hits.Select(x => new WorkingContextCitation(x.MemoryId, null, x.SourceRef, x.Excerpt, x.ProjectId)).ToArray());
        }

        var fallback = await dbContext.MemoryItems
            .Where(x => x.ProjectId == ProjectContext.UserProjectId)
            .Where(x => x.Scope == MemoryScope.User && x.MemoryType == MemoryType.Preference && x.Status == MemoryStatus.Active)
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.Confidence)
            .ThenByDescending(x => x.UpdatedAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return new UserPreferenceSearchResult(
            fallback.Select(MapUserPreference).ToArray(),
            fallback.Select(x => new WorkingContextCitation(x.Id, null, x.SourceRef, x.Summary, x.ProjectId)).ToArray());
    }

    private async Task ReplaceChunksAsync(MemoryItem entity, string content, CancellationToken cancellationToken)
    {
        var existingChunks = await dbContext.MemoryItemChunks
            .Where(x => x.MemoryItemId == entity.Id)
            .ToListAsync(cancellationToken);

        if (existingChunks.Count > 0)
        {
            dbContext.MemoryItemChunks.RemoveRange(existingChunks);
        }

        var chunks = chunkingService.Chunk(entity.MemoryType, entity.SourceType, content);
        var createdAt = clock.UtcNow;
        var chunkEntities = new List<MemoryItemChunk>(chunks.Count);
        foreach (var draft in chunks)
        {
            chunkEntities.Add(new MemoryItemChunk
            {
                MemoryItemId = entity.Id,
                ChunkKind = draft.Kind,
                ChunkIndex = draft.Index,
                ChunkText = draft.Text,
                MetadataJson = draft.MetadataJson,
                CreatedAt = createdAt
            });
        }

        if (chunkEntities.Count > 0)
        {
            await dbContext.MemoryItemChunks.AddRangeAsync(chunkEntities, cancellationToken);
        }
    }

    private async Task AddRevisionAsync(MemoryItem entity, string changedBy, CancellationToken cancellationToken)
    {
        await dbContext.MemoryItemRevisions.AddAsync(new MemoryItemRevision
        {
            MemoryItemId = entity.Id,
            Version = entity.Version,
            Title = entity.Title,
            Content = entity.Content,
            Summary = entity.Summary,
            MetadataJson = entity.MetadataJson,
            ChangedBy = changedBy,
            CreatedAt = clock.UtcNow
        }, cancellationToken);
    }

    private static string Sha(string input)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input)));

    private static string NormalizePreferenceKey(string key)
        => key.Trim().ToLowerInvariant();

    private static string[] BuildPreferenceTags(UserPreferenceUpsertRequest request)
        => (request.Tags ?? [])
            .Append("user-preference")
            .Append(request.Kind.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private async Task<UserPreferenceResult> GetUserPreferenceRequiredAsync(Guid id, CancellationToken cancellationToken)
    {
        var entity = await dbContext.MemoryItems
            .FirstOrDefaultAsync(
                x => x.Id == id &&
                     x.ProjectId == ProjectContext.UserProjectId &&
                     x.Scope == MemoryScope.User &&
                     x.MemoryType == MemoryType.Preference,
                cancellationToken)
            ?? throw new InvalidOperationException($"User preference '{id}' was not found.");

        return MapUserPreference(entity);
    }

    private static string ResolveDefaultProjectId(MemoryScope scope, MemoryType memoryType)
        => scope == MemoryScope.User && memoryType == MemoryType.Preference
            ? ProjectContext.UserProjectId
            : ProjectContext.DefaultProjectId;

    private static bool IsSummaryWrite(MemoryUpsertRequest request)
        => ProjectContext.IsShared(request.ProjectId) && request.MemoryType == MemoryType.Summary;

    private static void EnsureWritableProject(string projectId, bool allowSharedLayer)
    {
        if (ProjectContext.IsShared(projectId) && !allowSharedLayer)
        {
            throw new InvalidOperationException("Summary layer is read-only. Update the source project memories and rebuild the summary layer instead.");
        }
    }

    private static void EnsureMemoryWritable(MemoryItem entity, bool allowSharedLayer = false)
    {
        if (entity.IsReadOnly && !(allowSharedLayer && ProjectContext.IsShared(entity.ProjectId)))
        {
            throw new InvalidOperationException("This memory item belongs to the read-only summary layer.");
        }
    }

    private static MemoryDocument Map(MemoryItem entity)
        => new(
            entity.Id,
            entity.ExternalKey,
            entity.Scope,
            entity.MemoryType,
            entity.Title,
            entity.Content,
            entity.Summary,
            entity.SourceType,
            entity.SourceRef,
            entity.Tags,
            entity.Importance,
            entity.Confidence,
            entity.Version,
            entity.Status,
            entity.MetadataJson,
            entity.CreatedAt,
            entity.UpdatedAt,
            entity.ProjectId,
            entity.IsReadOnly);

    private static JobResult Map(MemoryJob job)
        => new(job.Id, job.JobType, job.Status, job.PayloadJson, job.Error, job.CreatedAt, job.StartedAt, job.CompletedAt, job.ProjectId);

    private static UserPreferenceResult MapUserPreference(MemoryItem entity)
    {
        var metadata = UserPreferenceMetadataSerializer.Deserialize(entity.MetadataJson);
        return new UserPreferenceResult(
            entity.Id,
            entity.SourceRef,
            metadata.Kind,
            entity.Title,
            entity.Content,
            metadata.Rationale,
            entity.Tags,
            entity.Importance,
            entity.Confidence,
            entity.Status,
            entity.CreatedAt,
            entity.UpdatedAt);
    }

    private static WorkingContextSection[] MapContext(IEnumerable<MemorySearchHit> hits)
        => hits.Select(x => new WorkingContextSection(x.MemoryId, x.Title, x.Excerpt, x.Excerpt, x.ProjectId)).ToArray();

    private sealed record UserPreferenceSearchResult(
        IReadOnlyList<UserPreferenceResult> Preferences,
        IReadOnlyList<WorkingContextCitation> Citations);

    private sealed record ReindexJobPayload(string ModelKey, Guid? MemoryItemId, string ProjectId);
    private sealed record SummaryRefreshJobPayload(string? ProjectId, IReadOnlyList<string> IncludedProjectIds, bool RebuildAll);
}

public sealed class LogQueryService(IApplicationDbContext dbContext) : ILogQueryService
{
    public async Task<IReadOnlyList<LogEntryResult>> SearchAsync(LogQueryRequest request, CancellationToken cancellationToken)
    {
        var query = dbContext.RuntimeLogEntries.AsQueryable();
        var projectId = ProjectContext.Normalize(request.ProjectId);
        var serviceNames = SplitFilterValues(request.ServiceName);
        var levels = SplitFilterValues(request.Level);

        query = query.Where(x => x.ProjectId == projectId);

        if (serviceNames.Length > 0)
        {
            query = query.Where(x => serviceNames.Contains(x.ServiceName));
        }

        if (levels.Length > 0)
        {
            query = query.Where(x => levels.Contains(x.Level));
        }

        if (!string.IsNullOrWhiteSpace(request.TraceId))
        {
            query = query.Where(x => x.TraceId == request.TraceId);
        }

        if (!string.IsNullOrWhiteSpace(request.RequestId))
        {
            query = query.Where(x => x.RequestId == request.RequestId);
        }

        if (request.From.HasValue)
        {
            var fromUtc = NormalizeUtc(request.From.Value);
            query = query.Where(x => x.CreatedAt >= fromUtc);
        }

        if (request.To.HasValue)
        {
            var toUtc = NormalizeUtc(request.To.Value);
            query = query.Where(x => x.CreatedAt <= toUtc);
        }

        if (!string.IsNullOrWhiteSpace(request.Query))
        {
            query = query.Where(x =>
                x.Message.Contains(request.Query) ||
                x.Exception.Contains(request.Query));
        }

        return await query
            .OrderByDescending(x => x.CreatedAt)
            .Take(request.Limit)
            .Select(x => new LogEntryResult(
                x.Id,
                x.ServiceName,
                x.Category,
                x.Level,
                x.Message,
                x.Exception,
                x.TraceId,
                x.RequestId,
                x.PayloadJson,
                x.CreatedAt,
                x.ProjectId))
            .ToListAsync(cancellationToken);
    }

    private static string[] SplitFilterValues(string? filter)
        => string.IsNullOrWhiteSpace(filter)
            ? []
            : filter
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

    private static DateTimeOffset NormalizeUtc(DateTimeOffset value)
        => value.Offset == TimeSpan.Zero ? value : value.ToUniversalTime();

    public async Task<LogEntryResult?> GetAsync(long id, CancellationToken cancellationToken)
    {
        return await dbContext.RuntimeLogEntries
            .Where(x => x.Id == id)
            .Select(x => new LogEntryResult(
                x.Id,
                x.ServiceName,
                x.Category,
                x.Level,
                x.Message,
                x.Exception,
                x.TraceId,
                x.RequestId,
                x.PayloadJson,
                x.CreatedAt,
                x.ProjectId))
            .FirstOrDefaultAsync(cancellationToken);
    }
}

public sealed class BackgroundJobProcessor(
    IApplicationDbContext dbContext,
    IEmbeddingProvider embeddingProvider,
    IVectorStore vectorStore,
    IClock clock,
    IConversationAutomationService conversationAutomationService,
    ISourceSyncService sourceSyncService,
    IGovernanceService governanceService,
    IEvaluationService evaluationService,
    ISuggestedActionService suggestedActionService) : IBackgroundJobProcessor
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<JobResult?> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var job = await dbContext.MemoryJobs
            .OrderBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(x => x.Status == MemoryJobStatus.Pending, cancellationToken);

        if (job is null)
        {
            return null;
        }

        job.Status = MemoryJobStatus.Running;
        job.StartedAt = clock.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);

        try
        {
            switch (job.JobType)
            {
                case MemoryJobType.Reindex:
                    await ProcessReindexAsync(job, cancellationToken);
                    break;
                case MemoryJobType.RefreshSummary:
                    await ProcessRefreshSummaryAsync(job, cancellationToken);
                    break;
                case MemoryJobType.IngestConversation:
                    await ProcessConversationCheckpointAsync(job, cancellationToken);
                    break;
                case MemoryJobType.PromoteConversationInsights:
                    await ProcessConversationPromotionAsync(job, cancellationToken);
                    break;
                case MemoryJobType.SyncSource:
                    await ProcessSourceSyncAsync(job, cancellationToken);
                    break;
                case MemoryJobType.AnalyzeGovernance:
                    await ProcessGovernanceAnalysisAsync(job, cancellationToken);
                    break;
                case MemoryJobType.RunEvaluation:
                    await ProcessEvaluationAsync(job, cancellationToken);
                    break;
                case MemoryJobType.ExecuteSuggestedAction:
                    await ProcessSuggestedActionAsync(job, cancellationToken);
                    break;
            }

            job.Status = MemoryJobStatus.Completed;
            job.CompletedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            job.Status = MemoryJobStatus.Failed;
            job.Error = ex.ToString();
            job.CompletedAt = clock.UtcNow;
            await dbContext.SaveChangesAsync(cancellationToken);
        }

        return new JobResult(job.Id, job.JobType, job.Status, job.PayloadJson, job.Error, job.CreatedAt, job.StartedAt, job.CompletedAt, job.ProjectId);
    }

    private async Task ProcessReindexAsync(MemoryJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ReindexJobPayload>(job.PayloadJson, JsonOptions)
            ?? new ReindexJobPayload(null, null, job.ProjectId);
        var chunks = dbContext.MemoryItemChunks.AsQueryable();

        if (payload.MemoryItemId.HasValue)
        {
            chunks = chunks.Where(x => x.MemoryItemId == payload.MemoryItemId.Value);
        }
        else
        {
            var projectId = ProjectContext.Normalize(payload.ProjectId, job.ProjectId);
            chunks = chunks.Where(x => x.MemoryItem!.ProjectId == projectId);
        }

        var list = await chunks
            .Where(x => !string.IsNullOrWhiteSpace(x.ChunkText))
            .ToListAsync(cancellationToken);
        var batchSize = Math.Max(1, embeddingProvider.BatchSize);
        for (var offset = 0; offset < list.Count; offset += batchSize)
        {
            var batch = list.Skip(offset).Take(batchSize).ToArray();
            var embeddings = await embeddingProvider.EmbedBatchAsync(
                batch.Select(chunk => new BatchEmbeddingItem(chunk.ChunkText, EmbeddingPurpose.Document)).ToArray(),
                cancellationToken);

            for (var index = 0; index < batch.Length; index++)
            {
                var chunk = batch[index];
                var embedding = embeddings[index];
                await vectorStore.ReplaceChunkVectorAsync(
                    chunk.Id,
                    embedding with { ModelKey = string.IsNullOrWhiteSpace(payload.ModelKey) ? embedding.ModelKey : payload.ModelKey },
                    cancellationToken);
            }
        }
    }

    private async Task ProcessRefreshSummaryAsync(MemoryJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<SummaryRefreshJobPayload>(job.PayloadJson, JsonOptions)
            ?? new SummaryRefreshJobPayload(job.ProjectId, [], false);

        if (payload.RebuildAll || string.IsNullOrWhiteSpace(payload.ProjectId))
        {
            var projectIds = (await dbContext.MemoryItems
                .Select(x => x.ProjectId)
                .Distinct()
                .OrderBy(x => x)
                .ToListAsync(cancellationToken))
                .Where(x => !ProjectContext.IsShared(x) && !ProjectContext.IsUser(x))
                .ToList();

            foreach (var projectId in projectIds)
            {
                await RefreshSharedSummaryForProjectAsync(projectId, [], cancellationToken);
            }

            return;
        }

        await RefreshSharedSummaryForProjectAsync(
            ProjectContext.Normalize(payload.ProjectId, job.ProjectId),
            payload.IncludedProjectIds,
            cancellationToken);
    }

    private async Task RefreshSharedSummaryForProjectAsync(
        string projectId,
        IReadOnlyList<string>? includedProjectIds,
        CancellationToken cancellationToken)
    {
        var normalizedProjectId = ProjectContext.Normalize(projectId);
        var projectIds = (includedProjectIds ?? [])
            .Select(x => ProjectContext.Normalize(x))
            .Append(normalizedProjectId)
            .Where(x => !ProjectContext.IsShared(x) && !ProjectContext.IsUser(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sourceItems = await dbContext.MemoryItems
            .Where(x => projectIds.Contains(x.ProjectId))
            .Where(x => x.Status == MemoryStatus.Active)
            .Where(x =>
                x.MemoryType == MemoryType.Fact ||
                x.MemoryType == MemoryType.Decision ||
                x.MemoryType == MemoryType.Artifact)
            .OrderByDescending(x => x.Importance)
            .ThenByDescending(x => x.Confidence)
            .ThenByDescending(x => x.UpdatedAt)
            .Take(40)
            .ToListAsync(cancellationToken);

        var citations = sourceItems
            .Select(x => new SummaryCitation(x.Id, x.ProjectId, x.Title, x.SourceRef))
            .ToArray();
        var content = sourceItems.Count == 0
            ? "No reusable project knowledge is available yet."
            : string.Join(Environment.NewLine, sourceItems.Select(x => $"- [{x.ProjectId}] {x.Title}: {x.Summary}"));
        var summary = sourceItems.Count == 0
            ? $"No shared summary is available for project '{normalizedProjectId}'."
            : $"Shared summary for project '{normalizedProjectId}' and {Math.Max(0, projectIds.Length - 1)} referenced project(s).";

        var entity = await dbContext.MemoryItems
            .FirstOrDefaultAsync(
                x => x.ProjectId == ProjectContext.SharedProjectId &&
                     x.ExternalKey == BuildSharedSummaryExternalKey(normalizedProjectId),
                cancellationToken);

        if (entity is null)
        {
            entity = new MemoryItem
            {
                ProjectId = ProjectContext.SharedProjectId,
                ExternalKey = BuildSharedSummaryExternalKey(normalizedProjectId),
                Scope = MemoryScope.Project,
                MemoryType = MemoryType.Summary,
                Title = $"Shared summary for {normalizedProjectId}",
                SourceType = "summary-layer",
                SourceRef = normalizedProjectId,
                IsReadOnly = true,
                CreatedAt = clock.UtcNow
            };
            await dbContext.MemoryItems.AddAsync(entity, cancellationToken);
        }

        entity.Title = $"Shared summary for {normalizedProjectId}";
        entity.Content = content;
        entity.Summary = summary;
        entity.Tags = ["summary-layer", $"project:{normalizedProjectId}"];
        entity.Importance = 0.85m;
        entity.Confidence = 0.8m;
        entity.Version = entity.Version <= 0 ? 1 : entity.Version + 1;
        entity.Status = MemoryStatus.Active;
        entity.MetadataJson = JsonSerializer.Serialize(new SharedSummaryMetadata(projectIds, citations), JsonOptions);
        entity.UpdatedAt = clock.UtcNow;
        entity.IsReadOnly = true;

        await dbContext.MemoryItemRevisions.AddAsync(new MemoryItemRevision
        {
            MemoryItemId = entity.Id,
            Version = entity.Version,
            Title = entity.Title,
            Content = entity.Content,
            Summary = entity.Summary,
            MetadataJson = entity.MetadataJson,
            ChangedBy = "summary-refresh",
            CreatedAt = clock.UtcNow
        }, cancellationToken);

        var existingChunks = await dbContext.MemoryItemChunks
            .Where(x => x.MemoryItemId == entity.Id)
            .ToListAsync(cancellationToken);
        if (existingChunks.Count > 0)
        {
            dbContext.MemoryItemChunks.RemoveRange(existingChunks);
        }

        foreach (var draft in ChunkingServiceChunk(entity.Content))
        {
            await dbContext.MemoryItemChunks.AddAsync(new MemoryItemChunk
            {
                MemoryItemId = entity.Id,
                ChunkKind = ChunkKind.Document,
                ChunkIndex = draft.Index,
                ChunkText = draft.Text,
                MetadataJson = draft.MetadataJson,
                CreatedAt = clock.UtcNow
            }, cancellationToken);
        }
    }

    private async Task ProcessConversationCheckpointAsync(MemoryJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ConversationCheckpointJobPayload>(job.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Conversation checkpoint payload is invalid.");
        await conversationAutomationService.ProcessCheckpointJobAsync(payload.CheckpointId, cancellationToken);
    }

    private async Task ProcessConversationPromotionAsync(MemoryJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ConversationPromotionJobPayload>(job.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Conversation promotion payload is invalid.");
        await conversationAutomationService.PromotePendingInsightsAsync(payload.ConversationId, payload.ProjectId, cancellationToken);
    }

    private Task ProcessSourceSyncAsync(MemoryJob job, CancellationToken cancellationToken)
        => sourceSyncService.ProcessSyncJobAsync(job.Id, cancellationToken);

    private async Task ProcessGovernanceAnalysisAsync(MemoryJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<GovernanceAnalysisJobPayload>(job.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Governance analysis payload is invalid.");
        await governanceService.AnalyzeAsync(payload.ProjectId, cancellationToken);
    }

    private async Task ProcessEvaluationAsync(MemoryJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<EvaluationRunRequest>(job.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Evaluation run payload is invalid.");
        await evaluationService.RunAsync(payload, cancellationToken);
    }

    private async Task ProcessSuggestedActionAsync(MemoryJob job, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<ExecuteSuggestedActionPayload>(job.PayloadJson, JsonOptions)
            ?? throw new InvalidOperationException("Suggested action payload is invalid.");
        await suggestedActionService.AcceptAsync(payload.ActionId, cancellationToken);
    }

    private static IReadOnlyList<ChunkDraft> ChunkingServiceChunk(string content)
        => new ChunkingService().Chunk(MemoryType.Summary, "summary-layer", content);

    private static string BuildSharedSummaryExternalKey(string projectId)
        => $"shared-summary:{ProjectContext.Normalize(projectId)}";

    private sealed record ReindexJobPayload(string? ModelKey, Guid? MemoryItemId, string ProjectId);
    private sealed record SummaryRefreshJobPayload(string? ProjectId, IReadOnlyList<string> IncludedProjectIds, bool RebuildAll);
    private sealed record ConversationCheckpointJobPayload(Guid CheckpointId);
    private sealed record ConversationPromotionJobPayload(string ConversationId, string ProjectId);
    private sealed record GovernanceAnalysisJobPayload(string ProjectId);
    private sealed record ExecuteSuggestedActionPayload(Guid ActionId);
    private sealed record SummaryCitation(Guid MemoryId, string ProjectId, string Title, string SourceRef);
    private sealed record SharedSummaryMetadata(IReadOnlyList<string> SourceProjects, IReadOnlyList<SummaryCitation> Citations);
}

public static class DependencyInjection
{
    public static IServiceCollection AddMemoryApplication(this IServiceCollection services)
    {
        services.AddSingleton<IClock, SystemClock>();
        services.AddScoped<IChunkingService, ChunkingService>();
        services.AddScoped<IMemoryService, MemoryService>();
        services.AddScoped<IDashboardQueryService, DashboardQueryService>();
        services.AddScoped<IMemoryTransferService, MemoryTransferService>();
        services.AddScoped<ILogQueryService, LogQueryService>();
        services.AddScoped<IPerformanceProbeService, PerformanceProbeService>();
        services.AddScoped<IInstanceBehaviorSettingsAccessor, InstanceBehaviorSettingsAccessor>();
        services.AddScoped<IConversationAutomationService, ConversationAutomationService>();
        services.AddScoped<ISourceConnectionService, SourceConnectionService>();
        services.AddScoped<ISourceSyncService, SourceSyncService>();
        services.AddScoped<IGovernanceService, GovernanceService>();
        services.AddScoped<IEvaluationService, EvaluationService>();
        services.AddScoped<ISuggestedActionService, SuggestedActionService>();
        services.AddScoped<IBackgroundJobProcessor, BackgroundJobProcessor>();
        return services;
    }
}

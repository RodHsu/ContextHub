using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace Memory.Application;

public sealed class DashboardMemoryGraphIndexBuilder(
    IApplicationDbContext dbContext,
    IMemoryService memoryService) : IDashboardMemoryGraphIndexBuilder
{
    private const int MaxSimilaritySourceNodes = 160;
    private const int SimilaritySearchLimit = 10;
    private const int MaxSimilarityNeighborsPerNode = 2;

    public async Task<DashboardMemoryGraphIndexSnapshotPayload> BuildAsync(CancellationToken cancellationToken)
    {
        var items = await dbContext.MemoryItems
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            return new DashboardMemoryGraphIndexSnapshotPayload(
                new MemoryGraphResult([], [], new MemoryGraphStatsResult(0, 0, 0, false)));
        }

        var sourceConnections = await dbContext.SourceConnections
            .AsNoTracking()
            .ToDictionaryAsync(source => source.Id, cancellationToken);
        var byId = items.ToDictionary(item => item.Id);
        var ids = byId.Keys.ToHashSet();
        var explicitEdges = await BuildExplicitEdgesAsync(ids, cancellationToken);
        var explicitEdgeKeys = explicitEdges
            .Select(edge => BuildUndirectedEdgeKey(edge.FromId, edge.ToId, "explicit"))
            .ToHashSet(StringComparer.Ordinal);
        var similarityEdges = await BuildSimilarityEdgesAsync(items, byId, explicitEdgeKeys, cancellationToken);
        var edges = explicitEdges
            .Concat(similarityEdges)
            .ToArray();
        var explicitCounts = BuildNeighborCountLookup(edges, "explicit");
        var similarityCounts = BuildNeighborCountLookup(edges, "similar");
        var nodes = items
            .OrderBy(item => item.ProjectId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .Select(item => BuildGraphNode(
                item,
                explicitCounts.GetValueOrDefault(item.Id),
                similarityCounts.GetValueOrDefault(item.Id),
                sourceConnections))
            .ToArray();

        return new DashboardMemoryGraphIndexSnapshotPayload(
            new MemoryGraphResult(
                nodes,
                edges,
                new MemoryGraphStatsResult(0, nodes.Length, edges.Length, false)));
    }

    private async Task<IReadOnlyList<MemoryGraphEdgeResult>> BuildExplicitEdgesAsync(
        IReadOnlySet<Guid> ids,
        CancellationToken cancellationToken)
    {
        var links = await dbContext.MemoryLinks
            .AsNoTracking()
            .Where(link => ids.Contains(link.FromId) && ids.Contains(link.ToId))
            .ToListAsync(cancellationToken);

        return links
            .GroupBy(link => new { link.FromId, link.ToId, link.LinkType })
            .Select(group => group.OrderByDescending(link => link.CreatedAt).First())
            .Select(link => new MemoryGraphEdgeResult(link.FromId, link.ToId, "explicit", link.LinkType))
            .ToArray();
    }

    private async Task<IReadOnlyList<MemoryGraphEdgeResult>> BuildSimilarityEdgesAsync(
        IReadOnlyList<MemoryItem> items,
        IReadOnlyDictionary<Guid, MemoryItem> byId,
        IReadOnlySet<string> explicitEdgeKeys,
        CancellationToken cancellationToken)
    {
        var degreeMap = BuildScopedDegreeMap(items.Select(item => item.Id).ToHashSet(), explicitEdgeKeys);
        var sourceItems = items
            .Where(item => item.Status == MemoryStatus.Active)
            .OrderByDescending(item => degreeMap.GetValueOrDefault(item.Id))
            .ThenByDescending(item => item.Importance)
            .ThenByDescending(item => item.UpdatedAt)
            .Take(MaxSimilaritySourceNodes)
            .ToArray();
        var edges = new List<MemoryGraphEdgeResult>();
        var edgeKeys = new HashSet<string>(StringComparer.Ordinal);

        foreach (var source in sourceItems)
        {
            var query = BuildSimilarityQuery(source);
            if (string.IsNullOrWhiteSpace(query))
            {
                continue;
            }

            var hits = await memoryService.SearchAsync(
                new MemorySearchRequest(
                    query,
                    SimilaritySearchLimit,
                    IncludeArchived: false,
                    ProjectId: source.ProjectId,
                    IncludedProjectIds: null,
                    QueryMode: MemoryQueryMode.CurrentOnly,
                    UseSummaryLayer: false,
                    Telemetry: new RetrievalTelemetryContext(
                        "dashboard.memory_graph_index",
                        "dashboard",
                        "background graph index refresh",
                        DetailLevel: RetrievalTelemetryDetailLevel.SummaryOnly)),
                cancellationToken);
            var searchCandidates = hits
                .Where(hit => byId.ContainsKey(hit.MemoryId))
                .Select(hit => new ScoredMemoryItem(byId[hit.MemoryId], hit.Score))
                .GroupBy(candidate => candidate.Item.Id)
                .Select(group => group.OrderByDescending(candidate => candidate.Score).First())
                .ToArray();
            var searchCandidateIds = searchCandidates
                .Select(candidate => candidate.Item.Id)
                .ToHashSet();
            var lexicalCandidates = RankItemsByLexicalSimilarity(
                    query,
                    byId.Values.Where(item => string.Equals(item.ProjectId, source.ProjectId, StringComparison.OrdinalIgnoreCase)),
                    SimilaritySearchLimit * 2)
                .Where(candidate => !searchCandidateIds.Contains(candidate.Item.Id));
            var taken = 0;

            foreach (var candidate in searchCandidates.Concat(lexicalCandidates).OrderByDescending(candidate => candidate.Score))
            {
                var target = candidate.Item;
                if (target.Id == source.Id || target.Status != MemoryStatus.Active)
                {
                    continue;
                }

                if (explicitEdgeKeys.Contains(BuildUndirectedEdgeKey(source.Id, target.Id, "explicit")))
                {
                    continue;
                }

                var key = BuildUndirectedEdgeKey(source.Id, target.Id, "similar");
                if (!edgeKeys.Add(key))
                {
                    continue;
                }

                edges.Add(new MemoryGraphEdgeResult(
                    source.Id,
                    target.Id,
                    "similar",
                    "Similarity",
                    candidate.Score));
                taken++;

                if (taken >= MaxSimilarityNeighborsPerNode)
                {
                    break;
                }
            }
        }

        return edges;
    }

    private static IReadOnlyList<ScoredMemoryItem> RankItemsByLexicalSimilarity(
        string query,
        IEnumerable<MemoryItem> items,
        int limit)
    {
        if (string.IsNullOrWhiteSpace(query) || limit < 1)
        {
            return [];
        }

        var tokens = Tokenize(query);
        return items
            .Select(item => new ScoredMemoryItem(item, ScoreLexicalSimilarity(item, query, tokens)))
            .Where(candidate => candidate.Score > 0m)
            .OrderByDescending(candidate => candidate.Score)
            .ThenByDescending(candidate => candidate.Item.Importance)
            .ThenByDescending(candidate => candidate.Item.UpdatedAt)
            .Take(limit)
            .ToArray();
    }

    private static decimal ScoreLexicalSimilarity(MemoryItem item, string rawQuery, IReadOnlySet<string> queryTokens)
    {
        var title = item.Title ?? string.Empty;
        var summary = item.Summary ?? string.Empty;
        var sourceRef = item.SourceRef ?? string.Empty;
        var normalizedQuery = rawQuery.Trim();
        var haystack = string.Join(' ', [title, summary, sourceRef, string.Join(' ', item.Tags)]).Trim();
        if (string.IsNullOrWhiteSpace(haystack))
        {
            return decimal.Zero;
        }

        var score = decimal.Zero;
        if (title.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.7m;
        }

        if (summary.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.35m;
        }

        if (sourceRef.Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
        {
            score += 0.15m;
        }

        if (queryTokens.Count == 0)
        {
            return score;
        }

        var candidateTokens = Tokenize(haystack);
        if (candidateTokens.Count == 0)
        {
            return score;
        }

        var overlap = queryTokens.Count(candidateTokens.Contains);
        return overlap == 0 ? score : score + decimal.Divide(overlap, queryTokens.Count);
    }

    private static IReadOnlySet<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return text
            .Split(
                [' ', '\t', '\r', '\n', ',', '.', ';', ':', '!', '?', '/', '\\', '-', '_', '(', ')', '[', ']', '{', '}', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(token => token.Length >= 2)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static Dictionary<Guid, int> BuildScopedDegreeMap(IReadOnlySet<Guid> ids, IReadOnlySet<string> explicitEdgeKeys)
    {
        var degreeMap = ids.ToDictionary(id => id, _ => 0);
        foreach (var key in explicitEdgeKeys)
        {
            var parts = key.Split(':');
            if (parts.Length != 3 ||
                !Guid.TryParse(parts[1], out var left) ||
                !Guid.TryParse(parts[2], out var right))
            {
                continue;
            }

            if (degreeMap.ContainsKey(left))
            {
                degreeMap[left]++;
            }

            if (degreeMap.ContainsKey(right))
            {
                degreeMap[right]++;
            }
        }

        return degreeMap;
    }

    private static MemoryGraphNodeResult BuildGraphNode(
        MemoryItem entity,
        int explicitLinkCount,
        int similarityNeighborCount,
        IReadOnlyDictionary<Guid, SourceConnection> sourceConnections)
    {
        var sourceContext = BuildSourceContext(entity, sourceConnections);
        var thumbnailUrl = ResolveThumbnailUrl(sourceContext?.OriginPathOrUrl);
        var faviconUrl = thumbnailUrl is null ? ResolveFaviconUrl(sourceContext?.OriginPathOrUrl) : null;
        var sourceLabel = sourceContext?.ConnectorName
            ?? (!string.IsNullOrWhiteSpace(sourceContext?.OriginPathOrUrl) ? sourceContext!.OriginPathOrUrl! : entity.SourceType);

        return new MemoryGraphNodeResult(
            entity.Id,
            entity.Title,
            entity.Summary,
            entity.ProjectId,
            entity.MemoryType,
            entity.Scope,
            entity.Status,
            entity.Tags,
            entity.SourceType,
            entity.SourceRef,
            entity.UpdatedAt,
            entity.Importance,
            entity.Confidence,
            entity.IsReadOnly,
            thumbnailUrl,
            faviconUrl,
            sourceLabel,
            explicitLinkCount,
            similarityNeighborCount);
    }

    private static MemorySourceContextResult? BuildSourceContext(
        MemoryItem entity,
        IReadOnlyDictionary<Guid, SourceConnection> sourceConnections)
    {
        if (string.IsNullOrWhiteSpace(entity.MetadataJson))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(entity.MetadataJson);
            if (!document.RootElement.TryGetProperty("connectorId", out var connectorElement) ||
                connectorElement.ValueKind != JsonValueKind.String ||
                !Guid.TryParse(connectorElement.GetString(), out var connectorId))
            {
                return null;
            }

            sourceConnections.TryGetValue(connectorId, out var source);
            var lineage = document.RootElement.TryGetProperty("lineage", out var lineageElement) && lineageElement.ValueKind == JsonValueKind.Array
                ? lineageElement.EnumerateArray().Select(item => item.GetString() ?? string.Empty).Where(text => !string.IsNullOrWhiteSpace(text)).ToArray()
                : [];
            var syncedAt = document.RootElement.TryGetProperty("syncedAt", out var syncedElement) && syncedElement.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(syncedElement.GetString(), out var syncedAtValue)
                ? syncedAtValue
                : (DateTimeOffset?)null;

            return new MemorySourceContextResult(
                connectorId,
                source?.Name,
                document.RootElement.TryGetProperty("cursor", out var cursorElement) ? cursorElement.GetString() : null,
                document.RootElement.TryGetProperty("sourceVersion", out var sourceVersionElement) ? sourceVersionElement.GetString() : null,
                document.RootElement.TryGetProperty("originPathOrUrl", out var originElement) ? originElement.GetString() : null,
                syncedAt,
                source?.LastSuccessfulSyncAt,
                lineage);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static Dictionary<Guid, int> BuildNeighborCountLookup(IReadOnlyList<MemoryGraphEdgeResult> edges, string edgeType)
        => edges
            .Where(edge => string.Equals(edge.EdgeType, edgeType, StringComparison.OrdinalIgnoreCase))
            .SelectMany(edge => new[] { (NodeId: edge.FromId, NeighborId: edge.ToId), (NodeId: edge.ToId, NeighborId: edge.FromId) })
            .GroupBy(entry => entry.NodeId)
            .ToDictionary(
                group => group.Key,
                group => group.Select(entry => entry.NeighborId).Distinct().Count());

    private static string BuildSimilarityQuery(MemoryItem item)
        => string.Join(' ', new[] { item.Title, item.Summary }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

    private static string BuildUndirectedEdgeKey(Guid fromId, Guid toId, string edgeType)
    {
        var ordered = fromId.CompareTo(toId) <= 0 ? $"{fromId}:{toId}" : $"{toId}:{fromId}";
        return $"{edgeType}:{ordered}";
    }

    private static string? ResolveThumbnailUrl(string? originPathOrUrl)
    {
        if (!Uri.TryCreate(originPathOrUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var path = uri.AbsolutePath;
        var isImage = path.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ||
                      path.EndsWith(".avif", StringComparison.OrdinalIgnoreCase);

        return isImage ? uri.ToString() : null;
    }

    private static string? ResolveFaviconUrl(string? originPathOrUrl)
    {
        if (!Uri.TryCreate(originPathOrUrl, UriKind.Absolute, out var uri))
        {
            return null;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return $"{uri.Scheme}://{uri.Host}/favicon.ico";
    }

    private sealed record ScoredMemoryItem(MemoryItem Item, decimal Score);
}

using System.Security.Cryptography;
using System.Text;
using Memory.Domain;

namespace Memory.Application;

public static class RedisCacheKeyBuilder
{
    public static string Search(
        CacheVersionStamp version,
        MemorySearchRequest request,
        ContextHubRequestActor actor,
        IReadOnlyList<string> allowedProjects,
        string modelKey)
        => string.Join(
            ':',
            "cache:search",
            Hash(version.Value),
            Hash(request.Query),
            request.Limit,
            request.IncludeArchived,
            request.QueryMode,
            request.UseSummaryLayer,
            Hash(Actor(actor)),
            Hash(ProjectSet(allowedProjects)),
            Hash(modelKey));

    public static string WorkingContext(
        CacheVersionStamp version,
        WorkingContextRequest request,
        ContextHubRequestActor actor,
        IReadOnlyList<string> allowedProjects,
        string modelKey)
        => string.Join(
            ':',
            "cache:context",
            Hash(version.Value),
            Hash(request.Query),
            request.Limit,
            request.RecentLogLimit,
            request.QueryMode,
            request.UseSummaryLayer,
            Hash(Actor(actor)),
            Hash(ProjectSet(allowedProjects)),
            Hash(modelKey));

    public static string Embedding(string modelKey, EmbeddingPurpose purpose, string text)
        => $"cache:embedding:{Hash(modelKey)}:{purpose}:{Hash(text)}";

    public static string SemanticHits(
        CacheVersionStamp version,
        string modelKey,
        string query,
        int limit,
        ContextHubRequestActor actor,
        IReadOnlyList<string> allowedProjects)
        => string.Join(
            ':',
            "cache:semantic",
            Hash(version.Value),
            Hash(modelKey),
            Hash(query),
            limit,
            Hash(Actor(actor)),
            Hash(ProjectSet(allowedProjects)));

    public static string DashboardMemories(CacheVersionStamp version, MemoryListRequest request, ContextHubRequestActor actor)
        => $"cache:dashboard:memories:{Hash(version.Value)}:{Hash(Actor(actor))}:{Hash(DashboardMemoryRequest(request))}";

    public static string DashboardMemoryDetails(CacheVersionStamp version, Guid id, ContextHubRequestActor actor)
        => $"cache:dashboard:memory-details:{Hash(version.Value)}:{Hash(Actor(actor))}:{id:N}";

    public static string DashboardJobs(long jobVersion, JobListRequest request)
        => $"cache:dashboard:jobs:{jobVersion}:{Hash($"{request.Status}:{request.JobType}:{request.Page}:{request.PageSize}")}";

    public static string DashboardLogs(LogQueryRequest request, ContextHubRequestActor actor)
        => $"cache:dashboard:logs:{Hash(Actor(actor))}:{Hash($"{request.Query}:{request.ServiceName}:{request.Level}:{request.TraceId}:{request.RequestId}:{request.From?.ToString("O")}:{request.To?.ToString("O")}:{request.Limit}:{request.ProjectId}")}";

    public static string Hash(string? value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    public static string Actor(ContextHubRequestActor actor)
        => actor.HasUser
            ? $"{actor.TenantId!.Value:N}:{actor.UserId!.Value:N}:{string.Join(",", actor.Scopes.Order(StringComparer.OrdinalIgnoreCase))}"
            : "unrestricted";

    public static string ProjectSet(IReadOnlyList<string> projects)
        => string.Join(
            "|",
            projects
                .Select(x => ProjectContext.Normalize(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                .Select(x => x.ToLowerInvariant()));

    private static string DashboardMemoryRequest(MemoryListRequest request)
        => string.Join(
            ':',
            request.Query,
            request.Scope,
            request.MemoryType,
            request.Status,
            request.SourceType,
            request.Tag,
            request.ProjectId,
            request.ProjectQuery,
            ProjectSet(request.IncludedProjectIds ?? []),
            request.QueryMode,
            request.UseSummaryLayer,
            request.Page,
            request.PageSize);
}

public sealed record CachedChunkSearchHit(
    Guid MemoryId,
    Guid ChunkId,
    decimal Score,
    string Excerpt);

public static class CachedChunkSearchHitMapper
{
    public static CachedChunkSearchHit ToCached(this ChunkSearchHit hit)
        => new(hit.MemoryId, hit.ChunkId, hit.Score, hit.Excerpt);

    public static ChunkSearchHit ToSearchHit(this CachedChunkSearchHit hit)
        => new(hit.MemoryId, hit.ChunkId, hit.Score, hit.Excerpt);
}

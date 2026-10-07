using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
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
            "cache:v4:search",
            AuthorityAwareRetrievalReranker.RankingVersion,
            Hash(version.Value),
            Hash(request.Query),
            request.Limit,
            request.IncludeArchived,
            request.QueryMode,
            request.UseSummaryLayer,
            Hash(Actor(actor)),
            Hash(RetrievalProjectSet(allowedProjects)),
            Hash(modelKey));

    public static string WorkingContext(
        CacheVersionStamp version,
        WorkingContextRequest request,
        ContextHubRequestActor actor,
        IReadOnlyList<string> allowedProjects,
        string modelKey)
        => string.Join(
            ':',
            "cache:v4:context",
            AuthorityAwareRetrievalReranker.RankingVersion,
            Hash(version.Value),
            Hash(request.Query),
            request.Limit,
            request.RecentLogLimit,
            request.QueryMode,
            request.UseSummaryLayer,
            Hash(ProjectContext.IdentityKey(ProjectContext.Normalize(request.ProjectId))),
            Hash(Actor(actor)),
            Hash(RetrievalProjectSet(allowedProjects)),
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
            "cache:v4:semantic",
            Hash(version.Value),
            Hash(modelKey),
            Hash(query),
            limit,
            Hash(Actor(actor)),
            Hash(RetrievalProjectSet(allowedProjects)));

    public static string DashboardMemories(CacheVersionStamp version, MemoryListRequest request, ContextHubRequestActor actor)
        => $"cache:v3:dashboard:memories:{Hash(version.Value)}:{Hash(Actor(actor))}:{Hash(DashboardMemoryRequest(request))}";

    public static string DashboardMemoryDetails(CacheVersionStamp version, Guid id, ContextHubRequestActor actor)
        => $"cache:v3:dashboard:memory-details:{Hash(version.Value)}:{Hash(Actor(actor))}:{id:N}";

    public static string DashboardJobs(long jobVersion, JobListRequest request)
        => $"cache:dashboard:jobs:{jobVersion}:{Hash($"{request.Status}:{request.JobType}:{request.Page}:{request.PageSize}")}";

    public static string DashboardLogs(LogQueryRequest request, ContextHubRequestActor actor)
        => $"cache:v3:dashboard:logs:{Hash(Actor(actor))}:{Hash(JsonSerializer.Serialize(request with { ProjectId = ProjectContext.IdentityKey(ProjectContext.Normalize(request.ProjectId)) }))}";

    public static string Hash(string? value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value ?? string.Empty))).ToLowerInvariant();

    public static string Actor(ContextHubRequestActor actor)
        => JsonSerializer.Serialize(new
        {
            actor.TenantId,
            actor.UserId,
            actor.Role,
            actor.IsAuthenticated,
            actor.IsServiceActor,
            actor.IsInteractiveUser,
            Scopes = actor.Scopes.Distinct(StringComparer.OrdinalIgnoreCase)
                .Order(StringComparer.OrdinalIgnoreCase).Select(x => x.ToLowerInvariant()).ToArray(),
            Projects = ProjectSet(actor.AllowedProjectIds)
        });

    public static string ProjectSet(IReadOnlyList<string> projects)
        => JsonSerializer.Serialize(ProjectContext.IdentityKeys(projects.Select(x => ProjectContext.Normalize(x))));

    // Retrieval, authorization and cache scopes use the same versioned project identity contract.
    private static string RetrievalProjectSet(IReadOnlyList<string> projects)
        => ProjectSet(projects);

    private static string DashboardMemoryRequest(MemoryListRequest request)
        => JsonSerializer.Serialize(new
        {
            request.Query,
            request.Scope,
            request.MemoryType,
            request.Status,
            request.SourceType,
            request.Tag,
            ProjectId = string.IsNullOrWhiteSpace(request.ProjectId) ? null : ProjectContext.IdentityKey(request.ProjectId),
            request.ProjectQuery,
            Projects = RetrievalProjectSet(request.IncludedProjectIds ?? []),
            request.QueryMode,
            request.UseSummaryLayer,
            request.Page,
            request.PageSize
        });
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

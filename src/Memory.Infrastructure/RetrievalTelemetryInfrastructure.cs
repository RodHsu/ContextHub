using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

public sealed class DatabaseRetrievalTelemetryService(
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    TimeProvider timeProvider) : IRetrievalTelemetryService
{
    public async Task RecordAsync(RetrievalTelemetryWriteRequest request, CancellationToken cancellationToken)
    {
        await using var dbContext = await dbContextFactory.CreateDbContextAsync(cancellationToken);
        var entity = new RetrievalEvent
        {
            ProjectId = ProjectContext.Normalize(request.ProjectId),
            Channel = request.Channel?.Trim() ?? string.Empty,
            EntryPoint = request.EntryPoint?.Trim() ?? string.Empty,
            Purpose = request.Purpose?.Trim() ?? string.Empty,
            QueryText = request.QueryText ?? string.Empty,
            QueryHash = ComputeQueryHash(request.QueryText),
            QueryMode = request.QueryMode?.Trim() ?? string.Empty,
            IncludedProjectIds = (request.IncludedProjectIds ?? [])
                .Select(projectId => ProjectContext.Normalize(projectId))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            UseSummaryLayer = request.UseSummaryLayer,
            Limit = Math.Max(1, request.Limit),
            CacheHit = request.CacheHit,
            ResultCount = Math.Max(0, request.ResultCount),
            DurationMs = request.DurationMs < 0 ? 0 : request.DurationMs,
            Success = request.Success,
            Error = request.Error?.Trim() ?? string.Empty,
            TraceId = request.TraceId?.Trim() ?? string.Empty,
            RequestId = request.RequestId?.Trim() ?? string.Empty,
            MetadataJson = request.MetadataJson ?? "{}",
            CreatedAt = timeProvider.GetUtcNow()
        };

        entity.Hits = (request.Hits ?? Array.Empty<RetrievalTelemetryHitWriteRequest>())
            .OrderBy(x => x.Rank)
            .Select(hit => new RetrievalHit
            {
                RetrievalEventId = entity.Id,
                Rank = Math.Max(1, hit.Rank),
                MemoryId = hit.MemoryId,
                Title = hit.Title?.Trim() ?? string.Empty,
                MemoryType = hit.MemoryType?.Trim() ?? string.Empty,
                SourceType = hit.SourceType?.Trim() ?? string.Empty,
                SourceRef = hit.SourceRef?.Trim() ?? string.Empty,
                Score = hit.Score,
                Excerpt = hit.Excerpt?.Trim() ?? string.Empty,
                ProjectId = ProjectContext.Normalize(hit.ProjectId)
            })
            .ToList();

        await dbContext.RetrievalEvents.AddAsync(entity, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
    }

    private static string ComputeQueryHash(string? queryText)
        => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(queryText ?? string.Empty)));
}

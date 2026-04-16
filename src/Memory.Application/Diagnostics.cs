using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Memory.Domain;

namespace Memory.Application;

internal static class HybridSearchComposer
{
    public static IReadOnlyList<MemorySearchHit> Compose(
        IReadOnlyList<ChunkSearchHit> keywordHits,
        IReadOnlyList<ChunkSearchHit> semanticHits,
        IReadOnlyDictionary<Guid, MemoryItem> items,
        int limit,
        bool includeArchived)
    {
        var itemIds = keywordHits.Select(x => x.MemoryId).Concat(semanticHits.Select(x => x.MemoryId)).Distinct().ToArray();
        var keywordMax = NormalizeMax(keywordHits.Select(x => x.Score));
        var semanticMax = NormalizeMax(semanticHits.Select(x => x.Score));

        return itemIds
            .Where(id => items.ContainsKey(id))
            .Where(id => includeArchived || items[id].Status == MemoryStatus.Active)
            .Select(id =>
            {
                var keyword = keywordHits.Where(x => x.MemoryId == id).OrderByDescending(x => x.Score).FirstOrDefault();
                var semantic = semanticHits.Where(x => x.MemoryId == id).OrderByDescending(x => x.Score).FirstOrDefault();
                var item = items[id];
                var score =
                    (keyword?.Score ?? 0m) / keywordMax * 0.4m +
                    (semantic?.Score ?? 0m) / semanticMax * 0.5m +
                    ((item.Importance + item.Confidence) / 2m) * 0.1m;

                var excerpt = semantic?.Excerpt ?? keyword?.Excerpt ?? item.Summary ?? item.Content[..Math.Min(item.Content.Length, 180)];
                return new MemorySearchHit(item.Id, item.Title, item.MemoryType, item.Scope, decimal.Round(score, 4), excerpt, item.SourceRef, item.Tags, item.ProjectId);
            })
            .OrderByDescending(x => x.Score)
            .Take(limit)
            .ToArray();
    }

    internal static decimal NormalizeMax(IEnumerable<decimal> values)
    {
        var max = values.DefaultIfEmpty(0m).Max();
        return max <= 0m ? 1m : max;
    }
}

public sealed class PerformanceProbeService(
    IApplicationDbContext dbContext,
    IChunkingService chunkingService,
    IHybridSearchStore searchStore,
    IEmbeddingProvider embeddingProvider,
    IClock clock) : IPerformanceProbeService
{
    public async Task<PerformanceMeasureResult> MeasureAsync(PerformanceMeasureRequest request, CancellationToken cancellationToken)
    {
        var document = string.IsNullOrWhiteSpace(request.Document) ? request.Query : request.Document.Trim();
        var measurementDurationSeconds = request.MeasurementMode == PerformanceMeasurementMode.Duration
            ? Math.Max(1, request.MeasurementDurationSeconds)
            : 0;
        var maxMeasurementIterations = Math.Max(1, request.MaxMeasurementIterations);

        var completedWarmupIterations = 0;
        for (var i = 0; i < request.WarmupIterations; i++)
        {
            await ExecuteIterationAsync(request, document, cancellationToken);
            completedWarmupIterations++;
        }

        var expectedSampleCount = request.MeasurementMode == PerformanceMeasurementMode.Duration
            ? maxMeasurementIterations
            : Math.Max(1, request.MeasurementIterations);

        var chunkingSamples = new List<double>(expectedSampleCount);
        var queryEmbeddingSamples = new List<double>(expectedSampleCount);
        var documentEmbeddingSamples = new List<double>(expectedSampleCount);
        var keywordSamples = new List<double>(expectedSampleCount);
        var vectorSamples = new List<double>(expectedSampleCount);
        var hybridSamples = new List<double>(expectedSampleCount);

        var last = IterationSnapshot.Empty;
        var completedMeasurementIterations = 0;
        var measurementStopwatch = Stopwatch.StartNew();

        if (request.MeasurementMode == PerformanceMeasurementMode.Duration)
        {
            var targetDuration = TimeSpan.FromSeconds(measurementDurationSeconds);
            while (completedMeasurementIterations == 0 ||
                (measurementStopwatch.Elapsed < targetDuration && completedMeasurementIterations < maxMeasurementIterations))
            {
                last = await ExecuteAndCollectAsync(
                    request,
                    document,
                    cancellationToken,
                    chunkingSamples,
                    queryEmbeddingSamples,
                    documentEmbeddingSamples,
                    keywordSamples,
                    vectorSamples,
                    hybridSamples);

                completedMeasurementIterations++;
            }
        }
        else
        {
            var targetIterations = Math.Max(1, request.MeasurementIterations);
            for (var i = 0; i < targetIterations; i++)
            {
                last = await ExecuteAndCollectAsync(
                    request,
                    document,
                    cancellationToken,
                    chunkingSamples,
                    queryEmbeddingSamples,
                    documentEmbeddingSamples,
                    keywordSamples,
                    vectorSamples,
                    hybridSamples);

                completedMeasurementIterations++;
            }
        }
        measurementStopwatch.Stop();

        var chunks = chunkingService.Chunk(request.DocumentMemoryType, request.DocumentSourceType, document);
        var tokenEstimate = ChunkingService.ApproximateTokenCount(document);

        return new PerformanceMeasureResult(
            embeddingProvider.ProviderName,
            embeddingProvider.EmbeddingProfile,
            embeddingProvider.ModelKey,
            embeddingProvider.Dimensions,
            request.SearchLimit,
            request.IncludeArchived,
            completedWarmupIterations,
            completedMeasurementIterations,
            chunks.Count,
            tokenEstimate,
            last.KeywordHitCount,
            last.VectorHitCount,
            last.HybridHitCount,
            request.MeasurementMode,
            measurementDurationSeconds,
            maxMeasurementIterations,
            Math.Round(measurementStopwatch.Elapsed.TotalMilliseconds, 3),
            PerformanceMetricSummary.FromSamples("documents", chunkingSamples, 1),
            PerformanceMetricSummary.FromSamples("queries", queryEmbeddingSamples, 1),
            PerformanceMetricSummary.FromSamples("chunks", documentEmbeddingSamples, Math.Max(1, chunks.Count)),
            PerformanceMetricSummary.FromSamples("searches", keywordSamples, 1),
            PerformanceMetricSummary.FromSamples("searches", vectorSamples, 1),
            PerformanceMetricSummary.FromSamples("searches", hybridSamples, 1),
            clock.UtcNow);
    }

    private async Task<IterationSnapshot> ExecuteAndCollectAsync(
        PerformanceMeasureRequest request,
        string document,
        CancellationToken cancellationToken,
        ICollection<double> chunkingSamples,
        ICollection<double> queryEmbeddingSamples,
        ICollection<double> documentEmbeddingSamples,
        ICollection<double> keywordSamples,
        ICollection<double> vectorSamples,
        ICollection<double> hybridSamples)
    {
        var snapshot = await ExecuteIterationAsync(request, document, cancellationToken);
        chunkingSamples.Add(snapshot.ChunkingMilliseconds);
        queryEmbeddingSamples.Add(snapshot.QueryEmbeddingMilliseconds);
        documentEmbeddingSamples.Add(snapshot.DocumentEmbeddingMilliseconds);
        keywordSamples.Add(snapshot.KeywordSearchMilliseconds);
        vectorSamples.Add(snapshot.VectorSearchMilliseconds);
        hybridSamples.Add(snapshot.HybridSearchMilliseconds);
        return snapshot;
    }

    private async Task<IterationSnapshot> ExecuteIterationAsync(
        PerformanceMeasureRequest request,
        string document,
        CancellationToken cancellationToken)
    {
        var chunkingMeasurement = await Measure(() => Task.FromResult(
            chunkingService.Chunk(request.DocumentMemoryType, request.DocumentSourceType, document)));
        var chunks = chunkingMeasurement;

        var queryEmbeddingMeasurement = await Measure(() => embeddingProvider.EmbedAsync(request.Query, EmbeddingPurpose.Query, cancellationToken));
        var queryVector = queryEmbeddingMeasurement;

        var documentEmbeddingMeasurement = await Measure(async () =>
        {
            var batchSize = Math.Max(1, embeddingProvider.BatchSize);
            for (var offset = 0; offset < chunks.Result.Count; offset += batchSize)
            {
                var batch = chunks.Result.Skip(offset).Take(batchSize)
                    .Select(chunk => new BatchEmbeddingItem(chunk.Text, EmbeddingPurpose.Document))
                    .ToArray();
                await embeddingProvider.EmbedBatchAsync(batch, cancellationToken);
            }

            return true;
        });

        var keywordMeasurement = await Measure(() => searchStore.SearchKeywordChunksAsync(request.Query, request.SearchLimit * 3, cancellationToken));
        var keywordHits = keywordMeasurement;

        var vectorMeasurement = await Measure(() => searchStore.SearchVectorChunksAsync(queryVector.Result, request.SearchLimit * 3, cancellationToken));
        var vectorHits = vectorMeasurement;

        var hybridMeasurement = await Measure(async () =>
        {
            var result = await ExecuteHybridSearchAsync(request.Query, request.SearchLimit, request.IncludeArchived, cancellationToken);
            return result.Count;
        });
        var hybridHits = hybridMeasurement;

        return new IterationSnapshot(
            chunks.Elapsed.TotalMilliseconds,
            queryVector.Elapsed.TotalMilliseconds,
            documentEmbeddingMeasurement.Elapsed.TotalMilliseconds,
            keywordMeasurement.Elapsed.TotalMilliseconds,
            vectorMeasurement.Elapsed.TotalMilliseconds,
            hybridMeasurement.Elapsed.TotalMilliseconds,
            keywordHits.Result.Count,
            vectorHits.Result.Count,
            hybridHits.Result);
    }

    private async Task<IReadOnlyList<MemorySearchHit>> ExecuteHybridSearchAsync(
        string query,
        int limit,
        bool includeArchived,
        CancellationToken cancellationToken)
    {
        var keywordHits = await searchStore.SearchKeywordChunksAsync(query, limit * 3, cancellationToken);
        var queryVector = await embeddingProvider.EmbedAsync(query, EmbeddingPurpose.Query, cancellationToken);
        var semanticHits = await searchStore.SearchVectorChunksAsync(queryVector, limit * 3, cancellationToken);

        var itemIds = keywordHits.Select(x => x.MemoryId).Concat(semanticHits.Select(x => x.MemoryId)).Distinct().ToArray();
        var items = await dbContext.MemoryItems
            .Where(x => itemIds.Contains(x.Id))
            .ToDictionaryAsync(x => x.Id, cancellationToken);

        return HybridSearchComposer.Compose(keywordHits, semanticHits, items, limit, includeArchived);
    }

    private static async Task<TimedResult<T>> Measure<T>(Func<Task<T>> action)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = await action();
        stopwatch.Stop();
        return new TimedResult<T>(result, stopwatch.Elapsed);
    }

    private readonly record struct TimedResult<T>(T Result, TimeSpan Elapsed);

    private readonly record struct IterationSnapshot(
        double ChunkingMilliseconds,
        double QueryEmbeddingMilliseconds,
        double DocumentEmbeddingMilliseconds,
        double KeywordSearchMilliseconds,
        double VectorSearchMilliseconds,
        double HybridSearchMilliseconds,
        int KeywordHitCount,
        int VectorHitCount,
        int HybridHitCount)
    {
        public static IterationSnapshot Empty => new(0, 0, 0, 0, 0, 0, 0, 0, 0);
    }
}

internal static class PerformanceMetricSummary
{
    public static PerformanceMetricResult FromSamples(string unit, IReadOnlyList<double> samples, int workUnitsPerIteration)
    {
        if (samples.Count == 0)
        {
            return new PerformanceMetricResult(unit, 0, 0, 0, 0, 0, 0);
        }

        var ordered = samples.OrderBy(x => x).ToArray();
        var average = samples.Average();
        var p95Index = (int)Math.Ceiling(ordered.Length * 0.95) - 1;
        var safeIndex = Math.Clamp(p95Index, 0, ordered.Length - 1);
        var throughput = average <= 0
            ? 0
            : workUnitsPerIteration / (average / 1000d);

        return new PerformanceMetricResult(
            unit,
            samples.Count,
            Math.Round(average, 3),
            Math.Round(ordered[0], 3),
            Math.Round(ordered[^1], 3),
            Math.Round(ordered[safeIndex], 3),
            Math.Round(throughput, 3));
    }
}

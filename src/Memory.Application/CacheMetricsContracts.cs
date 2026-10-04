namespace Memory.Application;

public enum CacheMetricOutcome { Hit, Miss, Set, Bypass, Error, InvalidPayload, Observation }

public static class CacheMetricKinds
{
    public const string OriginSearch = "origin-search";
    public const string OriginContext = "origin-context";
    public const string OriginSemantic = "origin-semantic";
    public const string OriginEmbedding = "origin-embedding";

    public static bool IsOrigin(string kind)
        => kind is OriginSearch or OriginContext or OriginSemantic or OriginEmbedding;
}

public interface ICacheMetricsRecorder
{
    void Record(string kind, CacheMetricOutcome outcome, string? trafficClass = null, double durationMs = 0);
}

public interface ICacheMetricsQueryService
{
    Task<CacheMetricsWindowResult> GetAsync(string period, CancellationToken cancellationToken);
}

public sealed record CacheMetricsSeriesResult(
    string Kind, string TrafficClass, long Hits, long Misses, long Sets, long Bypasses,
    long Errors, long InvalidPayloads, double DurationMs, long Observations = 0)
{
    public long Samples => Hits + Misses + Observations;
    public double? HitRate => Hits + Misses == 0 ? null : 100d * Hits / (Hits + Misses);
}

public sealed record CacheMetricsInstanceResult(
    string InstanceId, Guid BootId, DateTimeOffset StartedAtUtc, DateTimeOffset LastFlushedAtUtc,
    int PendingBuckets, long DroppedSamples, bool UncleanShutdown,
    long ObservedMinutes = 0, long ExpectedMinutes = 0, DateTimeOffset? StoppedAtUtc = null);

public sealed record CacheMetricsWindowResult(
    bool Supported, string Period, DateTimeOffset StartedAtUtc, DateTimeOffset EndedAtUtc,
    DateTimeOffset ObservedAtUtc, string CoverageStatus, long DroppedSamples,
    IReadOnlyList<CacheMetricsSeriesResult> Series, IReadOnlyList<CacheMetricsInstanceResult> Instances,
    DateTimeOffset? CoverageStartedAtUtc = null, int FlushIntervalSeconds = 15,
    string CrashLossStatus = "UnknownUntilGracefulShutdown", string ExpectedInventoryStatus = "Unknown",
    string DroppedSamplesScope = "BootLifetime");

/// <summary>Classifies nested cache operations without putting query, user, or project identifiers in metric labels.</summary>
public sealed class CacheMetricsTrafficScope : IDisposable
{
    private static readonly AsyncLocal<string?> TrafficClass = new();
    private readonly string? _previous;

    private CacheMetricsTrafficScope(string value)
    {
        _previous = TrafficClass.Value;
        TrafficClass.Value = value;
    }

    public static string Current => TrafficClass.Value ?? "application";
    public static CacheMetricsTrafficScope Begin(string value) => new(value);
    public void Dispose() => TrafficClass.Value = _previous;
}

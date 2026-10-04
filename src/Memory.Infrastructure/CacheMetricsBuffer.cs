using Memory.Application;

namespace Memory.Infrastructure;

public sealed record CacheMetricBucket(
    string InstanceId, Guid BootId, DateTimeOffset BucketStartUtc, string Kind, string TrafficClass,
    long Revision, long Hits, long Misses, long Sets, long Bypasses, long Errors,
    long InvalidPayloads, double DurationMs, long Observations = 0);

/// <summary>Bounded process-local staging; the database stores cumulative, revision-guarded snapshots.</summary>
public sealed class CacheMetricsBuffer : ICacheMetricsRecorder
{
    private readonly object _gate = new();
    private readonly TimeProvider _timeProvider;
    private readonly int _capacity;
    private readonly Dictionary<(DateTimeOffset Minute, string Kind, string Traffic), CacheMetricBucket> _buckets = [];
    private readonly Dictionary<(DateTimeOffset Minute, string Kind, string Traffic), long> _acknowledged = [];
    private long _revision;
    private long _dropped;
    private DateTimeOffset _latestMinute;

    public CacheMetricsBuffer(TimeProvider timeProvider, int capacity = 4096, string? instanceId = null, Guid? bootId = null)
    {
        if (capacity < 1) throw new ArgumentOutOfRangeException(nameof(capacity));
        _timeProvider = timeProvider;
        _capacity = capacity;
        InstanceId = instanceId ?? "i-" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(Environment.MachineName)))[..16].ToLowerInvariant();
        BootId = bootId ?? Guid.NewGuid();
        StartedAtUtc = timeProvider.GetUtcNow();
    }

    public string InstanceId { get; }
    public Guid BootId { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public long DroppedSamples { get { lock (_gate) return _dropped; } }
    public int PendingBuckets { get { lock (_gate) return _buckets.Count(x => !_acknowledged.TryGetValue(x.Key, out var revision) || revision < x.Value.Revision); } }

    public void Record(string kind, CacheMetricOutcome outcome, string? trafficClass = null, double durationMs = 0)
    {
        // Fixed vocabularies prevent accidental high-cardinality or sensitive labels.
        kind = kind switch
        {
            "search-final" or "working-context-final" or "semantic-hits" or "embedding-query" or "final-result"
                or "query-compute" or "telemetry-write" or "server-request"
                or CacheMetricKinds.OriginSearch or CacheMetricKinds.OriginContext
                or CacheMetricKinds.OriginSemantic or CacheMetricKinds.OriginEmbedding => kind,
            _ => "other"
        };
        trafficClass ??= CacheMetricsTrafficScope.Current;
        trafficClass = trafficClass switch
        {
            "interactive" or "graph-background" or "application" => trafficClass,
            _ => "other"
        };
        var minute = TruncateMinute(_timeProvider.GetUtcNow());
        lock (_gate)
        {
            // A clock correction must not recreate an already persisted minute with reset totals.
            minute = minute < _latestMinute ? _latestMinute : minute;
            _latestMinute = minute;
            var key = (minute, kind, trafficClass);
            if (!_buckets.TryGetValue(key, out var value))
            {
                PruneAcknowledged(minute);
                if (_buckets.Count >= _capacity)
                {
                    _dropped++;
                    return;
                }
                value = new(InstanceId, BootId, minute, kind, trafficClass, 0, 0, 0, 0, 0, 0, 0, 0);
            }
            _buckets[key] = value with
            {
                Revision = ++_revision,
                Hits = value.Hits + (outcome == CacheMetricOutcome.Hit ? 1 : 0),
                Misses = value.Misses + (outcome == CacheMetricOutcome.Miss ? 1 : 0),
                Sets = value.Sets + (outcome == CacheMetricOutcome.Set ? 1 : 0),
                Bypasses = value.Bypasses + (outcome == CacheMetricOutcome.Bypass ? 1 : 0),
                Errors = value.Errors + (outcome == CacheMetricOutcome.Error ? 1 : 0),
                InvalidPayloads = value.InvalidPayloads + (outcome == CacheMetricOutcome.InvalidPayload ? 1 : 0),
                DurationMs = value.DurationMs + (double.IsFinite(durationMs) && durationMs > 0 ? durationMs : 0),
                Observations = value.Observations + (outcome == CacheMetricOutcome.Observation ? 1 : 0)
            };
        }
    }

    public IReadOnlyList<CacheMetricBucket> Snapshot()
    {
        lock (_gate)
        {
            PruneAcknowledged(ObserveMinute());
            return _buckets.Where(x => !_acknowledged.TryGetValue(x.Key, out var revision) || revision < x.Value.Revision)
                .Select(x => x.Value).ToArray();
        }
    }

    public void Acknowledge(IReadOnlyList<CacheMetricBucket> snapshots)
    {
        lock (_gate)
        {
            foreach (var snapshot in snapshots)
            {
                var key = (snapshot.BucketStartUtc, snapshot.Kind, snapshot.TrafficClass);
                if (snapshot.InstanceId == InstanceId && snapshot.BootId == BootId && _buckets.ContainsKey(key))
                    _acknowledged[key] = Math.Max(_acknowledged.GetValueOrDefault(key), snapshot.Revision);
            }
            PruneAcknowledged(ObserveMinute());
        }
    }

    private DateTimeOffset ObserveMinute()
    {
        var minute = TruncateMinute(_timeProvider.GetUtcNow());
        _latestMinute = minute > _latestMinute ? minute : _latestMinute;
        return _latestMinute;
    }

    private void PruneAcknowledged(DateTimeOffset minute)
    {
        foreach (var key in _buckets.Where(x => x.Key.Minute < minute && _acknowledged.GetValueOrDefault(x.Key) == x.Value.Revision).Select(x => x.Key).ToArray())
        {
            _buckets.Remove(key);
            _acknowledged.Remove(key);
        }
    }

    public static DateTimeOffset TruncateMinute(DateTimeOffset value)
        => DateTimeOffset.FromUnixTimeSeconds(value.ToUnixTimeSeconds() / 60 * 60);
}

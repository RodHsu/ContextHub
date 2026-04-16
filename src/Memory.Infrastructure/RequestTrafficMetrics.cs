using System.Collections.Concurrent;
using System.Net.Http;
using Memory.Application;
using System.Threading;

namespace Memory.Infrastructure;

public sealed class RequestTrafficMetricsCollector : IRequestTrafficSnapshotAccessor
{
    private readonly ConcurrentDictionary<long, TrafficBucket> _buckets = new();

    public void RecordInbound()
        => GetOrCreateBucket().IncrementInbound();

    public void RecordOutbound()
        => GetOrCreateBucket().IncrementOutbound();

    public IReadOnlyList<RequestTrafficSampleResult> GetRecentSamples(int limit)
    {
        var normalizedLimit = Math.Max(1, limit);
        var thresholdSecond = DateTimeOffset.UtcNow.AddSeconds(-(normalizedLimit - 1)).ToUnixTimeSeconds();

        foreach (var staleKey in _buckets.Keys.Where(x => x < thresholdSecond - 5))
        {
            _buckets.TryRemove(staleKey, out _);
        }

        return Enumerable.Range(0, normalizedLimit)
            .Select(offset =>
            {
                var second = thresholdSecond + offset;
                var timestamp = DateTimeOffset.FromUnixTimeSeconds(second);
                return _buckets.TryGetValue(second, out var bucket)
                    ? new RequestTrafficSampleResult(timestamp, bucket.InboundRequests, bucket.OutboundRequests)
                    : new RequestTrafficSampleResult(timestamp, 0, 0);
            })
            .ToArray();
    }

    private TrafficBucket GetOrCreateBucket()
    {
        var second = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return _buckets.GetOrAdd(second, static key => new TrafficBucket(key));
    }

    private sealed class TrafficBucket(long unixSecond)
    {
        private int _inboundRequests;
        private int _outboundRequests;

        public long UnixSecond { get; } = unixSecond;
        public int InboundRequests => _inboundRequests;
        public int OutboundRequests => _outboundRequests;

        public void IncrementInbound()
            => Interlocked.Increment(ref _inboundRequests);

        public void IncrementOutbound()
            => Interlocked.Increment(ref _outboundRequests);
    }
}

public sealed class RequestTrafficDelegatingHandler(RequestTrafficMetricsCollector collector) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!RequestTrafficSuppressionScope.IsSuppressed)
        {
            collector.RecordOutbound();
        }

        return await base.SendAsync(request, cancellationToken);
    }
}

public static class RequestTrafficSuppressionScope
{
    private static readonly AsyncLocal<int> SuppressionDepth = new();

    public static bool IsSuppressed => SuppressionDepth.Value > 0;

    public static IDisposable Suppress()
    {
        SuppressionDepth.Value++;
        return new Scope();
    }

    private sealed class Scope : IDisposable
    {
        private bool _disposed;

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            SuppressionDepth.Value = Math.Max(0, SuppressionDepth.Value - 1);
        }
    }
}

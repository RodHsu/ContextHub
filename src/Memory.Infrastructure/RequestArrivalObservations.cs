using Microsoft.Extensions.Options;
using System.Globalization;

namespace Memory.Infrastructure;

public sealed class RequestArrivalObservationOptions
{
    public const string SectionName = "RequestArrivalObservations";
    public bool Enabled { get; set; }
    public string? UntilUtc { get; set; }
    public int Capacity { get; set; } = 2048;
    public int MaximumSamples { get; set; } = 100_000;
}

public sealed record RequestArrivalSample(
    Guid BootId, long Sequence, long ParentSequence, string Layer, string Operation,
    DateTimeOffset StartedAtUtc, long StartedTimestamp, int ActiveAtStart,
    DateTimeOffset? FinishedAtUtc = null, double? DurationMs = null, string Outcome = "inflight", int Revision = 1);

public sealed record RequestArrivalCoverage(
    Guid BootId, DateTimeOffset StartedAtUtc, DateTimeOffset UntilUtc, long TimestampFrequency, long Revision,
    long HttpStarted, long HttpCompleted, int HttpActive, long ToolStarted, long ToolCompleted, int ToolActive,
    long Admitted, long DroppedStarts, long DroppedFinishes, int Pending, bool WindowClosed);

/// <summary>Opt-in, bounded, payload-free observations. Each boot has an explicit fixed end time.</summary>
public sealed class RequestArrivalObservations
{
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly int _maximumSamples;
    private readonly Dictionary<long, RequestArrivalSample> _pending = [];
    private long _sequence, _admitted, _droppedStarts, _droppedFinishes;
    private long _httpStarted, _httpCompleted, _toolStarted, _toolCompleted;
    private int _httpActive, _toolActive;
    private long _revision;
    private readonly long _bootTimestamp;
    private bool _windowClosed;

    public RequestArrivalObservations(TimeProvider time, IOptions<RequestArrivalObservationOptions> options)
    {
        _time = time;
        StartedAtUtc = time.GetUtcNow();
        _bootTimestamp = time.GetTimestamp();
        var settings = options.Value;
        UntilUtc = DateTimeOffset.TryParse(settings.UntilUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var untilUtc) ? untilUtc : StartedAtUtc;
        // Invalid or stale configuration fails closed for capture, without changing request results.
        Enabled = settings.Enabled && UntilUtc > StartedAtUtc && UntilUtc <= StartedAtUtc.AddHours(24);
        _capacity = Math.Clamp(settings.Capacity, 1, 8192);
        _maximumSamples = Math.Clamp(settings.MaximumSamples, 1, 1_000_000);
    }

    public Guid BootId { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset UntilUtc { get; }
    public bool Enabled { get; }

    public RequestArrivalLease? Begin(string layer, string operation, long parentSequence = 0)
    {
        if (!Enabled) return null;
        lock (_gate)
        {
            var startedUtc = _time.GetUtcNow();
            if (WindowClosed()) return null;
            layer = layer == "mcp-tool" ? "mcp-tool" : "http";
            // Never persist caller-provided paths, tool names, headers, identifiers or arguments.
            operation = operation switch
            {
                "memory_search" or "build_working_context" or "memory_upsert" or "memory_update"
                    or "rest-memory-search" or "rest-working-context" or "http-mcp" => operation,
                _ => "other"
            };
            var active = layer == "http" ? ++_httpActive : ++_toolActive;
            if (layer == "http") _httpStarted++; else _toolStarted++;
            _revision++;
            var row = new RequestArrivalSample(BootId, ++_sequence, Math.Max(0, parentSequence), layer,
                operation, startedUtc, _time.GetTimestamp(), active);
            var admitted = _admitted < _maximumSamples && _pending.Count < _capacity;
            if (admitted) { _pending.Add(row.Sequence, row); _admitted++; }
            else _droppedStarts++;
            return new RequestArrivalLease(this, row, admitted);
        }
    }

    internal void Complete(RequestArrivalSample sample, bool admitted, string outcome)
    {
        lock (_gate)
        {
            _revision++;
            if (sample.Layer == "http") { _httpCompleted++; _httpActive--; }
            else { _toolCompleted++; _toolActive--; }
            if (!admitted) return;
            if (!_pending.ContainsKey(sample.Sequence) && _pending.Count >= _capacity)
            {
                _droppedFinishes++;
                return;
            }
            _pending[sample.Sequence] = sample with
            {
                FinishedAtUtc = _time.GetUtcNow(),
                DurationMs = _time.GetElapsedTime(sample.StartedTimestamp).TotalMilliseconds,
                Outcome = outcome switch { "success" or "cancelled" => outcome, _ => "error" },
                Revision = 2
            };
        }
    }

    public (RequestArrivalCoverage Coverage, IReadOnlyList<RequestArrivalSample> Samples) Snapshot()
    {
        lock (_gate)
        {
            var closed = WindowClosed();
            return (new(BootId, StartedAtUtc, UntilUtc, _time.TimestampFrequency, _revision,
                _httpStarted, _httpCompleted, _httpActive, _toolStarted, _toolCompleted, _toolActive,
                _admitted, _droppedStarts, _droppedFinishes, _pending.Count, closed),
                _pending.Values.ToArray());
        }
    }

    public void Acknowledge(IReadOnlyList<RequestArrivalSample> samples)
    {
        lock (_gate)
        {
            foreach (var sample in samples)
                if (sample.BootId == BootId && _pending.TryGetValue(sample.Sequence, out var current)
                    && current.Revision == sample.Revision)
                    _pending.Remove(sample.Sequence);
        }
    }

    private bool WindowClosed()
    {
        if (!_windowClosed && (_time.GetUtcNow() >= UntilUtc || _time.GetElapsedTime(_bootTimestamp) >= UntilUtc - StartedAtUtc))
        {
            _windowClosed = true;
            _revision++;
        }
        return _windowClosed;
    }
}

public sealed class RequestArrivalLease(RequestArrivalObservations buffer, RequestArrivalSample sample, bool admitted)
{
    private int _completed;
    public long Sequence => sample.Sequence;

    public void Complete(string outcome)
    {
        if (Interlocked.Exchange(ref _completed, 1) == 0) buffer.Complete(sample, admitted, outcome);
    }
}

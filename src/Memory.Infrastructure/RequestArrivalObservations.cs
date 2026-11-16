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
    public int CaptureSchemaVersion { get; set; } = 1;
    public int ClockAnchorCapacity { get; set; } = 128;
    public int MaximumClockAnchors { get; set; } = 86_402;
}

public sealed record RequestArrivalSample(
    Guid BootId, long Sequence, long ParentSequence, string Layer, string Operation,
    DateTimeOffset StartedAtUtc, long StartedTimestamp, int ActiveAtStart,
    DateTimeOffset? FinishedAtUtc = null, double? DurationMs = null, string Outcome = "inflight", int Revision = 1,
    long? RawStartedBeforeNs = null, long? RawStartedAfterNs = null,
    long? RawFinishedBeforeNs = null, long? RawFinishedAfterNs = null,
    string? RequestFamily = null, string? RequestMethod = null, string? PayloadSizeBucket = null);

public sealed record RequestArrivalCoverage(
    Guid BootId, DateTimeOffset StartedAtUtc, DateTimeOffset UntilUtc, long TimestampFrequency, long Revision,
    long HttpStarted, long HttpCompleted, int HttpActive, long ToolStarted, long ToolCompleted, int ToolActive,
    long Admitted, long DroppedStarts, long DroppedFinishes, int Pending, bool WindowClosed,
    int CaptureSchemaVersion = 1, RequestArrivalClockCoverage? Clock = null);

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
    private readonly IRequestArrivalClock? _clock;
    private readonly int _anchorCapacity, _maximumAnchors;
    private readonly Dictionary<long, RequestArrivalClockAnchor> _anchors = [];
    private RequestArrivalClockDomain? _domain;
    private RequestArrivalClockReading? _firstReading, _lastReading, _lastAnchor, _lastClockCheck;
    private long _clockChecks, _clockFailures, _discontinuities, _suspends, _anchorSequence, _anchorsAdmitted, _anchorsDropped;
    private bool _terminalAnchorAdded;
    private string? _clockInvalidReason;
    private readonly long _rawMaximumSpanNs;

    public RequestArrivalObservations(TimeProvider time, IOptions<RequestArrivalObservationOptions> options,
        IRequestArrivalClock? clock = null)
    {
        _time = time;
        StartedAtUtc = time.GetUtcNow();
        _bootTimestamp = time.GetTimestamp();
        var settings = options.Value;
        UntilUtc = DateTimeOffset.TryParse(settings.UntilUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var untilUtc) ? untilUtc : StartedAtUtc;
        // Invalid or stale configuration fails closed for capture, without changing request results.
        CaptureSchemaVersion = settings.CaptureSchemaVersion;
        Enabled = settings.Enabled && CaptureSchemaVersion is 1 or 2 && UntilUtc > StartedAtUtc && UntilUtc <= StartedAtUtc.AddHours(24);
        _capacity = Math.Clamp(settings.Capacity, 1, 8192);
        _maximumSamples = Math.Clamp(settings.MaximumSamples, 1, 1_000_000);
        _anchorCapacity = Math.Clamp(settings.ClockAnchorCapacity, 1, 8192);
        _maximumAnchors = Math.Clamp(settings.MaximumClockAnchors, 1, 86_402);
        _rawMaximumSpanNs = Enabled ? checked((UntilUtc - StartedAtUtc).Ticks * 100) : 0;
        if (Enabled && CaptureSchemaVersion == 2)
        {
            _clock = clock ?? new LinuxRequestArrivalClock();
            _clockChecks++;
            _revision++;
            _firstReading = ReadClock(validateDomain: true);
            if (_firstReading is not null) AddAnchor(_firstReading);
        }
    }

    public Guid BootId { get; } = Guid.NewGuid();
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset UntilUtc { get; }
    public bool Enabled { get; }
    public int CaptureSchemaVersion { get; }

    public RequestArrivalLease? Begin(string layer, string operation, long parentSequence = 0,
        ArrivalRequestClassification? classification = null)
    {
        if (!Enabled) return null;
        lock (_gate)
        {
            if (_windowClosed) return null;
            var rawBefore = CaptureSchemaVersion == 2 ? ReadClock() : null;
            var startedUtc = _time.GetUtcNow();
            if (WindowClosed()) return null;
            var timestamp = CaptureSchemaVersion == 2 ? _time.GetTimestamp() : 0;
            var rawAfter = CaptureSchemaVersion == 2 ? ReadClock() : null;
            ValidateSampleBracket(rawBefore, rawAfter);
            if (_clockInvalidReason is not null || (CaptureSchemaVersion == 2 && WindowClosed())) return null;
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
            var shape = CaptureSchemaVersion == 2 ? ArrivalRequestClassification.Normalize(classification) : null;
            var row = new RequestArrivalSample(BootId, ++_sequence, Math.Max(0, parentSequence), layer,
                operation, startedUtc, CaptureSchemaVersion == 2 ? timestamp : _time.GetTimestamp(), active,
                RawStartedBeforeNs: rawBefore?.RawBeforeNs, RawStartedAfterNs: rawAfter?.RawAfterNs,
                RequestFamily: shape?.Family, RequestMethod: shape?.Method, PayloadSizeBucket: shape?.PayloadSizeBucket);
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
            var rawBefore = CaptureSchemaVersion == 2 ? ReadClock() : null;
            var finished = _time.GetUtcNow();
            var duration = _time.GetElapsedTime(sample.StartedTimestamp).TotalMilliseconds;
            var rawAfter = CaptureSchemaVersion == 2 ? ReadClock() : null;
            ValidateSampleBracket(rawBefore, rawAfter);
            _pending[sample.Sequence] = sample with
            {
                FinishedAtUtc = finished,
                DurationMs = duration,
                RawFinishedBeforeNs = rawBefore?.RawBeforeNs,
                RawFinishedAfterNs = rawAfter?.RawAfterNs,
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
                _admitted, _droppedStarts, _droppedFinishes, _pending.Count, closed, CaptureSchemaVersion,
                CaptureSchemaVersion == 2 ? new(_domain, _firstReading?.RawBeforeNs, _firstReading?.RawAfterNs,
                    _rawMaximumSpanNs, _clockChecks, _clockFailures, _discontinuities, _suspends, _clockInvalidReason,
                    _anchorsAdmitted, _anchorsDropped, _anchors.Values.ToArray()) : null),
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

    public void CaptureClockAnchor(bool final = false)
    {
        if (!Enabled || CaptureSchemaVersion != 2) return;
        lock (_gate)
        {
            if (final) CloseWindow();
            if (_clockInvalidReason is not null || _terminalAnchorAdded) return;
            _clockChecks++;
            _revision++;
            var reading = ReadClock(validateDomain: true);
            if (reading is not null)
            {
                var closed = WindowClosed();
                AddAnchor(reading, (final || closed) && _httpActive == 0 && _toolActive == 0);
            }
        }
    }

    public void AcknowledgeClockAnchors(IReadOnlyList<RequestArrivalClockAnchor> anchors)
    {
        lock (_gate)
            foreach (var anchor in anchors)
                if (anchor.BootId == BootId && _anchors.TryGetValue(anchor.Sequence, out var current) && current == anchor)
                    _anchors.Remove(anchor.Sequence);
    }

    private RequestArrivalClockReading? ReadClock(bool validateDomain = false)
    {
        if (_clockInvalidReason is not null) return null;
        try
        {
            if (validateDomain)
            {
                if (_clock is null || !_clock.TryReadDomain(out var domain) || domain is null ||
                    !ValidDomain(domain)) return InvalidateClock("CLOCK_DOMAIN_UNAVAILABLE");
                if (_domain is not null && _domain != domain) return InvalidateClock("CLOCK_DOMAIN_CHANGED");
                _domain = domain;
            }
            if (_clock is null || !_clock.TryRead(out var reading) || reading is null ||
                reading.RawBeforeNs < 0 || reading.RawAfterNs < reading.RawBeforeNs ||
                reading.MonotonicNs < 0 || reading.BoottimeNs < 0 || reading.RealtimeNs < 0)
                return InvalidateClock("CLOCK_READ_FAILED");
            if (reading.RawAfterNs - reading.RawBeforeNs > 50_000_000) return InvalidateClock("CLOCK_READ_BRACKET_EXCEEDED");
            if (_lastReading is not null && reading.RawBeforeNs < _lastReading.RawAfterNs)
                return InvalidateClock("RAW_CLOCK_REGRESSED");
            _lastReading = reading;
            return reading;
        }
        catch (Exception) { return InvalidateClock("CLOCK_READ_FAILED"); }
    }

    private static bool ValidDomain(RequestArrivalClockDomain domain)
        => domain.KernelBootId != Guid.Empty && domain.MonotonicOffsetNs == 0 && domain.BoottimeOffsetNs == 0 &&
           System.Text.RegularExpressions.Regex.IsMatch(domain.TimeNamespace, @"\Atime:\[[0-9]{1,20}\]\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant) &&
           System.Text.RegularExpressions.Regex.IsMatch(domain.Clocksource, @"\A[A-Za-z0-9_.-]{1,64}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private RequestArrivalClockReading? InvalidateClock(string reason)
    {
        if (_clockInvalidReason is null) { _clockInvalidReason = reason; _clockFailures++; _revision++; }
        CloseWindow();
        return null;
    }

    private void ValidateSampleBracket(RequestArrivalClockReading? before, RequestArrivalClockReading? after)
    {
        if (before is not null && after is not null && after.RawAfterNs - before.RawBeforeNs > 50_000_000)
            InvalidateClock("CLOCK_READ_BRACKET_EXCEEDED");
    }

    private void AddAnchor(RequestArrivalClockReading reading, bool terminal = false)
    {
        if (_lastClockCheck is not null)
        {
            var rawDelta = reading.RawBeforeNs - _lastClockCheck.RawBeforeNs;
            var realtimeDelta = reading.RealtimeNs - _lastClockCheck.RealtimeNs;
            if (Math.Abs((double)realtimeDelta - rawDelta) > 50_000_000) _discontinuities++;
            var suspendDelta = ((double)reading.BoottimeNs - reading.MonotonicNs) -
                ((double)_lastClockCheck.BoottimeNs - _lastClockCheck.MonotonicNs);
            if (Math.Abs(suspendDelta) > 50_000_000)
            {
                _suspends++;
                InvalidateClock("CLOCK_SUSPEND_OBSERVED");
                terminal = true;
            }
        }
        _lastClockCheck = reading;
        if (_terminalAnchorAdded || (!terminal && _lastAnchor is not null &&
            reading.RawBeforeNs / 1_000_000_000 == _lastAnchor.RawBeforeNs / 1_000_000_000)) return;
        if (terminal) _terminalAnchorAdded = true;
        _lastAnchor = reading;
        var sequence = ++_anchorSequence;
        if (_anchorsAdmitted >= _maximumAnchors || _anchors.Count >= _anchorCapacity) { _anchorsDropped++; return; }
        _anchors.Add(sequence, new(BootId, sequence, _domain!, reading));
        _anchorsAdmitted++;
    }

    private void CloseWindow()
    {
        if (!_windowClosed) { _windowClosed = true; _revision++; }
    }

    private bool WindowClosed()
    {
        if (!_windowClosed && (_time.GetUtcNow() >= UntilUtc ||
            (CaptureSchemaVersion == 1 && _time.GetElapsedTime(_bootTimestamp) >= UntilUtc - StartedAtUtc) ||
            (CaptureSchemaVersion == 2 && _firstReading is not null && _lastReading is not null &&
             _lastReading.RawAfterNs - _firstReading.RawBeforeNs >= _rawMaximumSpanNs)))
        {
            CloseWindow();
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

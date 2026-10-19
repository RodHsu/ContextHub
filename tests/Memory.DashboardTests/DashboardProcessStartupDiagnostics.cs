using System.Diagnostics;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Memory.DashboardTests;

internal sealed class DashboardProcessStartupDiagnostics
{
    internal const int MaximumAttempts = 32;
    internal const int MaximumScannedLineCharacters = 4096;
    internal const int MaximumSdkCodes = 16;
    internal const int MaximumJsonBytes = 16 * 1024;
    private const int MaximumPathCharacters = 1024;
    private static readonly Regex SdkCodePattern = new(
        @"(?:MSB[0-9]{4}|NETSDK[0-9]{4}|CS[0-9]{4})",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
    private readonly object _gate = new();
    private readonly HashSet<string> _sdkCodes = new(StringComparer.Ordinal);
    private readonly string[]? _requestedArguments;
    private readonly int _expectedPort;
    private readonly long _startupTimestamp = Stopwatch.GetTimestamp();
    private long? _readinessTimestamp;
    private double? _startupElapsedMillisecondsAtReadinessOutcome;
    private double? _readinessElapsedMilliseconds;
    private DateTime? _readyAtUtc;
    private int? _processId;
    private DateTime? _processStartedAtUtc;
    private string? _resolvedHostPath;
    private int? _exitCode;
    private int _stdoutLines;
    private int _stderrLines;
    private int _httpAttempts;
    private int? _lastHttpStatus;
    private int? _listeningPort;
    private int _omittedAttempts;
    private bool _stdoutCompleted;
    private bool _stderrCompleted;
    private bool _outputTruncated;
    private bool _unclassifiedOutput;
    private bool _addressAlreadyInUse;
    private bool _failedToBind;
    private bool _unhandledException;
    private bool _incomplete;
    private bool _persistAttempted;
    private string _readiness = "Unknown";
    private string _initialization = "Unknown";
    private string _fixtureDisposal = "Unknown";
    private string _lastHttpFailure = "Unknown";

    private DashboardProcessStartupDiagnostics(string projectPath, string configuration, Uri baseUri, int expectedPort)
    {
        _expectedPort = expectedPort;
        if (projectPath.Length <= MaximumPathCharacters && Path.IsPathFullyQualified(projectPath)
            && Path.GetFileName(projectPath) == "Memory.Dashboard.csproj"
            && configuration is "Debug" or "Release" && baseUri.IsLoopback
            && baseUri.Scheme == Uri.UriSchemeHttp && baseUri.Port == expectedPort
            && baseUri.AbsolutePath == "/" && baseUri.Query.Length == 0 && baseUri.Fragment.Length == 0
            && baseUri.UserInfo.Length == 0)
        {
            _requestedArguments = ["run", "--no-build", "--no-launch-profile", "--configuration", configuration,
                "--project", projectPath, "--", "--urls", baseUri.AbsoluteUri.TrimEnd('/')];
        }
        else
        {
            _incomplete = true;
        }
    }

    internal static DashboardProcessStartupDiagnostics? TryCreate(string projectPath, string configuration, Uri baseUri, int expectedPort)
    {
        try { return new(projectPath, configuration, baseUri, expectedPort); }
        catch { return null; }
    }

    internal void ObserveStartedProcess(Process process)
    {
        try
        {
            lock (_gate)
            {
                _processId = process.Id;
                _processStartedAtUtc = process.StartTime.ToUniversalTime();
                var hostPath = process.MainModule?.FileName;
                if (hostPath is not null && hostPath.Length <= MaximumPathCharacters && Path.IsPathFullyQualified(hostPath))
                    _resolvedHostPath = hostPath;
                else
                    _incomplete = true;
            }
        }
        catch { MarkIncomplete(); }
    }

    // The Process line-event reader owns its temporary line buffer. No line is retained here.
    internal void ObserveOutput(string? line, bool standardError)
    {
        try
        {
            lock (_gate)
            {
                if (line is null)
                {
                    if (standardError) _stderrCompleted = true;
                    else _stdoutCompleted = true;
                    return;
                }

                if (standardError) Increment(ref _stderrLines);
                else Increment(ref _stdoutLines);
                var scannedLength = Math.Min(line.Length, MaximumScannedLineCharacters);
                if (scannedLength != line.Length) _outputTruncated = true;
                var scanned = line.AsSpan(0, scannedLength);
                _addressAlreadyInUse |= scanned.Contains("Address already in use", StringComparison.OrdinalIgnoreCase);
                _failedToBind |= scanned.Contains("Failed to bind", StringComparison.OrdinalIgnoreCase);
                _unhandledException |= scanned.Contains("Unhandled exception.", StringComparison.Ordinal);
                var matches = SdkCodePattern.EnumerateMatches(scanned);
                var classified = false;
                foreach (var match in matches)
                {
                    var end = match.Index + match.Length;
                    if ((match.Index > 0 && char.IsAsciiLetterOrDigit(scanned[match.Index - 1]))
                        || (end < scannedLength && char.IsAsciiLetterOrDigit(scanned[end])))
                        continue;
                    // A match at the scan boundary may be a prefix of an unscanned token.
                    if (scannedLength != line.Length && end == scannedLength)
                    {
                        _outputTruncated = true;
                        continue;
                    }
                    classified = true;
                    var code = line.Substring(match.Index, match.Length);
                    if (_sdkCodes.Count < MaximumSdkCodes || _sdkCodes.Contains(code)) _sdkCodes.Add(code);
                    else _outputTruncated = true;
                }
                if (!classified && line.Length != 0) _unclassifiedOutput = true;
            }
        }
        catch { MarkIncomplete(); }
    }

    internal void ObserveProcessExit(Process? process)
    {
        try
        {
            if (process is not null && process.HasExited)
                lock (_gate) _exitCode = process.ExitCode;
        }
        catch { MarkIncomplete(); }
    }

    internal void RecordHttpAttempt() { lock (_gate) Increment(ref _httpAttempts); }
    internal void RecordHttpStatus(int status) { lock (_gate) _lastHttpStatus = status is >= 100 and <= 599 ? status : null; }
    internal void RecordHttpFailure(bool timedOut) { lock (_gate) _lastHttpFailure = timedOut ? "TaskCanceledException" : "HttpRequestException"; }
    internal void RecordListeningPort(int port) { lock (_gate) _listeningPort = port is > 0 and <= 65535 ? port : null; }
    internal void RecordReadinessStarted() { lock (_gate) _readinessTimestamp = Stopwatch.GetTimestamp(); }
    internal void RecordReady()
    {
        lock (_gate)
        {
            _readiness = "Ready";
            _readyAtUtc = DateTime.UtcNow;
            RecordReadinessElapsed();
        }
    }
    internal void RecordInitializationCompleted() { lock (_gate) _initialization = "Completed"; }
    internal void RecordInitializationFailed()
    {
        lock (_gate)
        {
            _initialization = "Failed";
            if (_readiness != "Ready")
            {
                _readiness = "Failed";
                RecordReadinessElapsed();
            }
        }
    }
    internal void RecordDisposal(bool completed) { lock (_gate) _fixtureDisposal = completed ? "Completed" : "NotCompleted"; }
    internal void RecordOmittedAttempt() { lock (_gate) { Increment(ref _omittedAttempts); _incomplete = true; } }
    internal void MarkIncomplete() { lock (_gate) _incomplete = true; }

    internal byte[] SerializeSnapshot()
    {
        lock (_gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                kind = "DashboardFixtureStartupMetadata",
                requestedHost = "dotnet",
                requestedArguments = _requestedArguments,
                actualArgumentsIdentity = "Unverified",
                moduleIdentity = "Unverified",
                processId = _processId,
                processStartedAtUtc = _processStartedAtUtc,
                resolvedHostPath = _resolvedHostPath,
                observedExitCode = _exitCode,
                expectedPort = _expectedPort,
                observedListeningPort = _listeningPort,
                readiness = _readiness,
                readyAtUtc = _readyAtUtc,
                startupElapsedMillisecondsAtReadinessOutcome = _startupElapsedMillisecondsAtReadinessOutcome,
                readinessElapsedMilliseconds = _readinessElapsedMilliseconds,
                initialization = _initialization,
                fixtureDisposal = _fixtureDisposal,
                processTreeCleanup = "Unverified",
                httpAttempts = _httpAttempts,
                lastHttpStatus = _lastHttpStatus,
                lastHttpFailure = _lastHttpFailure,
                stdoutLines = _stdoutLines,
                stderrLines = _stderrLines,
                stdoutCompleted = _stdoutCompleted,
                stderrCompleted = _stderrCompleted,
                sdkCodeCandidates = _sdkCodes.Order(StringComparer.Ordinal).ToArray(),
                outputTruncated = _outputTruncated,
                unclassifiedOutputObserved = _unclassifiedOutput,
                addressAlreadyInUseMarkerObserved = _addressAlreadyInUse,
                failedToBindMarkerObserved = _failedToBind,
                unhandledExceptionMarkerObserved = _unhandledException,
                omittedStartupAttempts = _omittedAttempts,
                recordingIncomplete = _incomplete,
                persistence = "AttemptedOnlyWhenOutcomeKnown",
                rawOutputRetained = false
            });
            if (bytes.Length > MaximumJsonBytes)
                throw new InvalidOperationException("Dashboard startup metadata exceeded its fixed size limit.");
            return bytes;
        }
    }

    internal void TryPersist(string artifactDirectory)
    {
        lock (_gate)
        {
            if (_persistAttempted) return;
            _persistAttempted = true;
        }
        try
        {
            var bytes = SerializeSnapshot();
            var directory = Path.Combine(artifactDirectory, "startup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            using var file = new FileStream(Path.Combine(directory, "startup.json"), FileMode.CreateNew, FileAccess.Write, FileShare.Read);
            file.Write(bytes);
        }
        catch { MarkIncomplete(); }
    }

    private static void Increment(ref int value)
    {
        if (value < int.MaxValue) value++;
    }

    private void RecordReadinessElapsed()
    {
        var now = Stopwatch.GetTimestamp();
        _startupElapsedMillisecondsAtReadinessOutcome = Stopwatch.GetElapsedTime(_startupTimestamp, now).TotalMilliseconds;
        if (_readinessTimestamp is long started)
            _readinessElapsedMilliseconds = Stopwatch.GetElapsedTime(started, now).TotalMilliseconds;
    }
}

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Memory.DashboardTests;

internal enum DashboardTeardownStep
{
    BrowserClose,
    HostKill,
    HostWait,
    PlaywrightDispose
}

internal enum DashboardTeardownOutcome
{
    NotReached,
    Started,
    Completed,
    Failed,
    Skipped
}

internal enum DashboardTeardownFailureKind
{
    Unknown,
    Timeout,
    Canceled,
    Other
}

internal enum DashboardBrowserCloseTaskState
{
    Pending,
    RanToCompletion,
    Canceled,
    Faulted,
    Unavailable
}

internal enum DashboardFixtureTestClass
{
    Unknown,
    DashboardBrowserUiTests,
    GraphViewportTests,
    GraphVisibleViewportTests,
    HeaderViewportTests,
    DashboardAuthenticationBrowserTests
}

internal enum DashboardHostObservationOutcome
{
    NotObserved,
    Observed,
    MissingPath,
    InvalidPath,
    GetterFailed
}

internal enum DashboardHostObservationStage
{
    None,
    ProcessId,
    StartTime,
    MainModulePath,
    PathValidation
}

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
    private readonly TeardownStepState[] _teardownSteps = [new(), new(), new(), new()];
    private bool _teardownObserved;
    private bool _browserCloseStartObserved;
    private bool _browserCloseTimeoutObserved;
    private bool? _browserConnectedBeforeClose;
    private bool? _browserConnectedAtTimeout;
    private DashboardBrowserCloseTaskState? _browserCloseTaskStateAtTimeout;
    private DashboardBrowserCloseTaskState? _browserCloseTaskStateAfterCleanup;
    private bool _browserCloseAfterCleanupObserved;
    private DashboardFixtureTestClass _fixtureTestClass;
    private bool _fixtureTestClassConflicted;
    private bool _firstTeardownFailureObserved;
    private DashboardTeardownStep? _firstTeardownFailureStep;
    private DashboardTeardownFailureKind? _firstTeardownFailureKind;
    private DashboardTeardownFailureKind? _outerTeardownFailureKind;
    private readonly long _startupTimestamp = Stopwatch.GetTimestamp();
    private long? _readinessTimestamp;
    private double? _startupElapsedMillisecondsAtReadinessOutcome;
    private double? _readinessElapsedMilliseconds;
    private DateTime? _readyAtUtc;
    private int? _processId;
    private DateTime? _processStartedAtUtc;
    private string? _resolvedHostPath;
    private DashboardHostObservationOutcome _hostObservationOutcome;
    private DashboardHostObservationStage _hostObservationFailedStage;
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

    private DashboardProcessStartupDiagnostics(string assemblyPath, string configuration, Uri baseUri, int expectedPort)
    {
        _expectedPort = expectedPort;
        if (assemblyPath.Length <= MaximumPathCharacters && Path.IsPathFullyQualified(assemblyPath)
            && Path.GetFileName(assemblyPath) == "Memory.Dashboard.dll"
            && Path.GetFileName(Path.GetDirectoryName(assemblyPath)) == "net10.0"
            && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(assemblyPath))) == configuration
            && Path.GetFileName(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(assemblyPath)))) == "bin"
            && configuration is "Debug" or "Release" && baseUri.IsLoopback
            && baseUri.Scheme == Uri.UriSchemeHttp && baseUri.Port == expectedPort
            && baseUri.AbsolutePath == "/" && baseUri.Query.Length == 0 && baseUri.Fragment.Length == 0
            && baseUri.UserInfo.Length == 0)
        {
            _requestedArguments = [assemblyPath, "--urls", baseUri.AbsoluteUri.TrimEnd('/')];
        }
        else
        {
            _incomplete = true;
        }
    }

    internal static DashboardProcessStartupDiagnostics? TryCreate(string assemblyPath, string configuration, Uri baseUri, int expectedPort)
    {
        try { return new(assemblyPath, configuration, baseUri, expectedPort); }
        catch { return null; }
    }

    internal void ObserveStartedProcess(Process process)
        => ObserveStartedProcess(() => process.Id, () => process.StartTime, () => ReadHostImagePath(process));

    private static string? ReadHostImagePath(Process process)
    {
        if (!OperatingSystem.IsWindows()) return process.MainModule?.FileName;

        var path = new char[MaximumPathCharacters + 1];
        var length = (uint)path.Length;
        if (!QueryFullProcessImageNameW(process.SafeHandle, 0, path, ref length))
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        if (length > MaximumPathCharacters)
            throw new InvalidOperationException("The observed process image path exceeded the diagnostic bound.");
        return new string(path, 0, (int)length);
    }

    [SupportedOSPlatform("windows")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageNameW(
        SafeProcessHandle process, uint flags, [Out] char[] imageName, ref uint size);

    internal void ObserveStartedProcess(Func<int> readProcessId, Func<DateTime> readStartTime, Func<string?> readHostPath)
    {
        var stage = DashboardHostObservationStage.ProcessId;
        try
        {
            lock (_gate)
            {
                _processId = readProcessId();
                stage = DashboardHostObservationStage.StartTime;
                _processStartedAtUtc = readStartTime().ToUniversalTime();
                stage = DashboardHostObservationStage.MainModulePath;
                var hostPath = readHostPath();
                if (hostPath is null)
                {
                    _hostObservationOutcome = DashboardHostObservationOutcome.MissingPath;
                    _hostObservationFailedStage = stage;
                    _incomplete = true;
                    return;
                }
                stage = DashboardHostObservationStage.PathValidation;
                if (hostPath.Length <= MaximumPathCharacters && Path.IsPathFullyQualified(hostPath))
                {
                    _resolvedHostPath = hostPath;
                    _hostObservationOutcome = DashboardHostObservationOutcome.Observed;
                    _hostObservationFailedStage = DashboardHostObservationStage.None;
                }
                else
                {
                    _hostObservationOutcome = DashboardHostObservationOutcome.InvalidPath;
                    _hostObservationFailedStage = stage;
                    _incomplete = true;
                }
            }
        }
        catch
        {
            lock (_gate)
            {
                _hostObservationOutcome = DashboardHostObservationOutcome.GetterFailed;
                _hostObservationFailedStage = stage;
                _incomplete = true;
            }
        }
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

    internal static DashboardTeardownFailureKind ClassifyTeardownFailure(Exception? failure) => failure switch
    {
        TimeoutException => DashboardTeardownFailureKind.Timeout,
        OperationCanceledException => DashboardTeardownFailureKind.Canceled,
        null => DashboardTeardownFailureKind.Unknown,
        _ => DashboardTeardownFailureKind.Other
    };

    internal void RecordFixtureTestClass(DashboardFixtureTestClass testClass)
    {
        try
        {
            lock (_gate)
            {
                if (_fixtureTestClassConflicted) return;
                if (!Enum.IsDefined(testClass) || testClass == DashboardFixtureTestClass.Unknown
                    || (_fixtureTestClass != DashboardFixtureTestClass.Unknown && _fixtureTestClass != testClass))
                {
                    _fixtureTestClass = DashboardFixtureTestClass.Unknown;
                    _fixtureTestClassConflicted = true;
                    _incomplete = true;
                    return;
                }
                _fixtureTestClass = testClass;
            }
        }
        catch { MarkIncomplete(); }
    }

    internal void ObserveBrowserCloseAfterCleanup(Task? closeTask)
    {
        try
        {
            lock (_gate)
            {
                if (_browserCloseAfterCleanupObserved || closeTask is null
                    || _teardownSteps[(int)DashboardTeardownStep.BrowserClose].Outcome
                        is not (DashboardTeardownOutcome.Completed or DashboardTeardownOutcome.Failed)
                    || _teardownSteps[(int)DashboardTeardownStep.PlaywrightDispose].Outcome
                        is not (DashboardTeardownOutcome.Completed or DashboardTeardownOutcome.Failed or DashboardTeardownOutcome.Skipped))
                {
                    _incomplete = true;
                    return;
                }
                _browserCloseAfterCleanupObserved = true;
                _browserCloseTaskStateAfterCleanup = ClassifyBrowserCloseTaskState(closeTask.Status);
            }
        }
        catch { MarkIncomplete(); }
    }

    internal void ObserveBrowserCloseStart(Func<bool>? readIsConnected)
    {
        try
        {
            lock (_gate)
            {
                if (_browserCloseStartObserved
                    || _teardownSteps[(int)DashboardTeardownStep.BrowserClose].Outcome != DashboardTeardownOutcome.Started)
                {
                    _incomplete = true;
                    return;
                }
                _browserCloseStartObserved = true;
            }

            var connected = ReadBrowserConnection(readIsConnected);
            lock (_gate) _browserConnectedBeforeClose = connected;
        }
        catch { MarkIncomplete(); }
    }

    internal void ObserveBrowserCloseTimeout(Func<bool>? readIsConnected, Task? closeTask)
    {
        try
        {
            lock (_gate)
            {
                if (!_browserCloseStartObserved || _browserCloseTimeoutObserved
                    || _teardownSteps[(int)DashboardTeardownStep.BrowserClose].Outcome != DashboardTeardownOutcome.Started)
                {
                    _incomplete = true;
                    return;
                }
                _browserCloseTimeoutObserved = true;
            }

            var connected = ReadBrowserConnection(readIsConnected);
            var taskState = ClassifyBrowserCloseTaskState(closeTask?.Status);
            lock (_gate)
            {
                _browserConnectedAtTimeout = connected;
                _browserCloseTaskStateAtTimeout = taskState;
                if (taskState == DashboardBrowserCloseTaskState.Unavailable) _incomplete = true;
            }
        }
        catch { MarkIncomplete(); }
    }

    internal static DashboardBrowserCloseTaskState ClassifyBrowserCloseTaskState(TaskStatus? status) => status switch
    {
        TaskStatus.Created or TaskStatus.WaitingForActivation or TaskStatus.WaitingToRun
            or TaskStatus.Running or TaskStatus.WaitingForChildrenToComplete => DashboardBrowserCloseTaskState.Pending,
        TaskStatus.RanToCompletion => DashboardBrowserCloseTaskState.RanToCompletion,
        TaskStatus.Canceled => DashboardBrowserCloseTaskState.Canceled,
        TaskStatus.Faulted => DashboardBrowserCloseTaskState.Faulted,
        _ => DashboardBrowserCloseTaskState.Unavailable
    };

    private bool? ReadBrowserConnection(Func<bool>? readIsConnected)
    {
        try
        {
            if (readIsConnected is not null) return readIsConnected();
        }
        catch { }
        MarkIncomplete();
        return null;
    }

    internal void RecordTeardownStep(
        DashboardTeardownStep step,
        DashboardTeardownOutcome outcome,
        DashboardTeardownFailureKind failureKind = DashboardTeardownFailureKind.Unknown)
    {
        // Diagnostic failures must never replace an exception from the original teardown.
        try
        {
            lock (_gate)
            {
                if ((uint)step >= (uint)_teardownSteps.Length
                    || outcome is not (DashboardTeardownOutcome.Started or DashboardTeardownOutcome.Completed
                        or DashboardTeardownOutcome.Failed or DashboardTeardownOutcome.Skipped)
                    || (outcome != DashboardTeardownOutcome.Failed && failureKind != DashboardTeardownFailureKind.Unknown))
                {
                    _incomplete = true;
                    return;
                }

                _teardownObserved = true;
                var state = _teardownSteps[(int)step];
                if (outcome is DashboardTeardownOutcome.Started or DashboardTeardownOutcome.Skipped)
                {
                    if (state.Outcome != DashboardTeardownOutcome.NotReached)
                    {
                        _incomplete = true;
                        return;
                    }
                    state.Outcome = outcome;
                    if (outcome == DashboardTeardownOutcome.Started) state.StartedTimestamp = Stopwatch.GetTimestamp();
                    return;
                }

                if (state.Outcome != DashboardTeardownOutcome.Started)
                {
                    _incomplete = true;
                    return;
                }
                state.Outcome = outcome;
                if (state.StartedTimestamp is long started)
                    state.ElapsedMilliseconds = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                if (outcome == DashboardTeardownOutcome.Failed)
                {
                    state.FailureKind = NormalizeTeardownFailureKind(failureKind);
                    RecordFirstTeardownFailure(step, state.FailureKind.Value);
                }
            }
        }
        catch { _incomplete = true; }
    }

    internal void RecordTeardownOuterFailure(DashboardTeardownFailureKind failureKind)
    {
        try
        {
            lock (_gate)
            {
                _teardownObserved = true;
                var normalized = NormalizeTeardownFailureKind(failureKind);
                _outerTeardownFailureKind ??= normalized;
                // An original resource predicate or Process.Dispose can fail outside the four observed operations.
                RecordFirstTeardownFailure(null, normalized);
            }
        }
        catch { _incomplete = true; }
    }

    private DashboardTeardownFailureKind NormalizeTeardownFailureKind(DashboardTeardownFailureKind failureKind)
    {
        if (failureKind is DashboardTeardownFailureKind.Unknown or DashboardTeardownFailureKind.Timeout
            or DashboardTeardownFailureKind.Canceled or DashboardTeardownFailureKind.Other)
            return failureKind;
        _incomplete = true;
        return DashboardTeardownFailureKind.Unknown;
    }

    private void RecordFirstTeardownFailure(DashboardTeardownStep? step, DashboardTeardownFailureKind failureKind)
    {
        if (_firstTeardownFailureObserved) return;
        _firstTeardownFailureObserved = true;
        _firstTeardownFailureStep = step;
        _firstTeardownFailureKind = failureKind;
    }

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
                hostObservationOutcome = _hostObservationOutcome.ToString(),
                hostObservationFailedStage = _hostObservationFailedStage.ToString(),
                observedExitCode = _exitCode,
                expectedPort = _expectedPort,
                observedListeningPort = _listeningPort,
                readiness = _readiness,
                readyAtUtc = _readyAtUtc,
                startupElapsedMillisecondsAtReadinessOutcome = _startupElapsedMillisecondsAtReadinessOutcome,
                readinessElapsedMilliseconds = _readinessElapsedMilliseconds,
                initialization = _initialization,
                fixtureDisposal = _fixtureDisposal,
                fixtureTestClass = _fixtureTestClass switch
                {
                    DashboardFixtureTestClass.DashboardBrowserUiTests => nameof(DashboardBrowserUiTests),
                    DashboardFixtureTestClass.GraphViewportTests => nameof(GraphViewportTests),
                    DashboardFixtureTestClass.GraphVisibleViewportTests => nameof(GraphVisibleViewportTests),
                    DashboardFixtureTestClass.HeaderViewportTests => nameof(HeaderViewportTests),
                    DashboardFixtureTestClass.DashboardAuthenticationBrowserTests => nameof(DashboardAuthenticationBrowserTests),
                    _ => "Unknown"
                },
                processTreeCleanup = "Unverified",
                teardownScope = "FixtureDisposeCurrentAttemptOnly",
                teardownObserved = _teardownObserved,
                browserConnectedBeforeClose = _browserConnectedBeforeClose,
                browserConnectedAtTimeout = _browserConnectedAtTimeout,
                browserCloseTaskStateAtTimeout = _browserCloseTaskStateAtTimeout?.ToString(),
                browserCloseAfterCleanupObserved = _browserCloseAfterCleanupObserved,
                browserCloseTaskStateAfterCleanup = _browserCloseTaskStateAfterCleanup?.ToString(),
                teardownSteps = _teardownSteps.Select((step, index) => new
                {
                    step = ((DashboardTeardownStep)index).ToString(),
                    outcome = step.Outcome.ToString(),
                    elapsedMilliseconds = step.ElapsedMilliseconds,
                    failureKind = step.FailureKind?.ToString()
                }).ToArray(),
                firstObservedTeardownFailure = _firstTeardownFailureObserved ? new
                {
                    step = _firstTeardownFailureStep?.ToString() ?? "Unknown",
                    failureKind = _firstTeardownFailureKind?.ToString() ?? "Unknown"
                } : null,
                outerPropagatedTeardownFailureKind = _outerTeardownFailureKind?.ToString(),
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

    private sealed class TeardownStepState
    {
        internal DashboardTeardownOutcome Outcome { get; set; } = DashboardTeardownOutcome.NotReached;
        internal long? StartedTimestamp { get; set; }
        internal double? ElapsedMilliseconds { get; set; }
        internal DashboardTeardownFailureKind? FailureKind { get; set; }
    }
}

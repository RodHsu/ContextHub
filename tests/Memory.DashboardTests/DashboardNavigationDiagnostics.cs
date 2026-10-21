using System.Diagnostics;
using System.Text.Json;
using Microsoft.Playwright;

namespace Memory.DashboardTests;

// Test-only metadata. Raw URLs, query values, failures and browser objects never enter snapshots.
internal sealed class DashboardNavigationDiagnostics
{
    internal const int MaximumRequests = 32;
    internal const int MaximumJsonBytes = 16 * 1024;
    private static readonly (string Path, string Type)[] Assets =
    [
        ("/login", "document"), ("/favicon.svg", "image"),
        ("/app.css", "stylesheet"), ("/quiet-signal.css", "stylesheet"),
        ("/Memory.Dashboard.styles.css", "stylesheet"),
        ("/dashboard-theme.js", "script"), ("/dashboard-viewport.js", "script"),
        ("/dashboard-time.js", "script"), ("/dashboard-clipboard.js", "script"),
        ("/dashboard-session.js", "script"), ("/dashboard-graph.js", "script"),
        ("/dashboard-transfer.js", "script"), ("/dashboard-discussions.js", "script"),
        ("/_framework/blazor.web.js", "script"),
        ("/Components/Layout/ReconnectModal.razor.js", "script")
    ];
    private readonly object _gate = new();
    private readonly Uri _origin;
    private readonly string _theme;
    private readonly string _viewport;
    private readonly string _repoRoot;
    private readonly long _started = Stopwatch.GetTimestamp();
    private readonly Dictionary<object, RequestRecord> _active = new(ReferenceEqualityComparer.Instance);
    private readonly List<RequestRecord> _requests = [];
    private readonly List<Action> _unsubscribe = [];
    private bool _accepting = true;
    private bool _detachAttempted;
    private bool _detachCompleted;
    private bool _incomplete;
    private bool _truncated;
    private int _dropped;
    private long? _domContentLoaded;
    private long? _load;
    private string _gotoOutcome = "Unknown";
    private bool _persistenceAttempted;
    private bool _persistenceSucceeded;

    internal DashboardNavigationDiagnostics(Uri origin, string theme = "dark", string viewport = "desktop", string route = "/login")
    {
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttp || !origin.IsLoopback
            || origin.AbsolutePath != "/" || origin.UserInfo.Length != 0
            || origin.Query.Length != 0 || origin.Fragment.Length != 0)
            throw new ArgumentException("NAVIGATION_DIAGNOSTIC_ORIGIN_REJECTED");
        if (theme is not ("dark" or "light") || viewport is not ("desktop" or "tab-s7-portrait" or "mobile")
            || route != "/login")
            throw new ArgumentException("NAVIGATION_DIAGNOSTIC_CASE_REJECTED");
        _origin = origin;
        _theme = theme == "dark" ? "dark" : "light";
        _viewport = viewport switch { "desktop" => "desktop", "tab-s7-portrait" => "tab-s7-portrait", _ => "mobile" };
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        _repoRoot = repo;
        ArtifactDirectory = Path.Combine(repo, ".agent", "local", "test-results", "dashboard-tests",
            "navigation-artifacts", "navigation-" + Guid.NewGuid().ToString("N"));
        ArtifactPath = Path.Combine(ArtifactDirectory, "navigation.json");
    }

    internal string ArtifactDirectory { get; }
    internal string ArtifactPath { get; }
    internal bool PersistenceSucceeded { get { lock (_gate) return _persistenceSucceeded; } }

    internal static DashboardNavigationDiagnostics? TryAttach(IPage page, Uri origin, string theme, string viewport, string route)
    {
        DashboardNavigationDiagnostics? diagnostics = null;
        try
        {
            var attached = new DashboardNavigationDiagnostics(origin, theme, viewport, route);
            diagnostics = attached;
            void Request(object? sender, IRequest request) => attached.TryDiagnosticOperation(
                () => attached.ObserveRequest(request, request.Url, request.ResourceType));
            void Response(object? sender, IResponse response) => attached.TryDiagnosticOperation(
                () => attached.ObserveResponse(response.Request, response.Status));
            void Finished(object? sender, IRequest request) => attached.TryDiagnosticOperation(
                () => attached.ObserveEnd(request, failed: false));
            void Failed(object? sender, IRequest request) => attached.TryDiagnosticOperation(
                () => attached.ObserveEnd(request, failed: true));
            void Dom(object? sender, IPage observedPage) => attached.TryDiagnosticOperation(
                () => attached.ObserveStage(isLoad: false));
            void Load(object? sender, IPage observedPage) => attached.TryDiagnosticOperation(
                () => attached.ObserveStage(isLoad: true));
            attached.BindObserver(() => page.Request += Request, () => page.Request -= Request);
            attached.BindObserver(() => page.Response += Response, () => page.Response -= Response);
            attached.BindObserver(() => page.RequestFinished += Finished, () => page.RequestFinished -= Finished);
            attached.BindObserver(() => page.RequestFailed += Failed, () => page.RequestFailed -= Failed);
            attached.BindObserver(() => page.DOMContentLoaded += Dom, () => page.DOMContentLoaded -= Dom);
            attached.BindObserver(() => page.Load += Load, () => page.Load -= Load);
            return diagnostics;
        }
        catch
        {
            if (diagnostics is not null)
            {
                lock (diagnostics._gate) diagnostics._incomplete = true;
                diagnostics.Detach();
            }
            return diagnostics;
        }
    }

    // The same no-throw boundary protects real event getters, subscriptions and persistence.
    internal bool TryDiagnosticOperation(Action operation)
    {
        try { operation(); return true; }
        catch { lock (_gate) _incomplete = true; return false; }
    }

    internal void BindObserver(Action subscribe, Action unsubscribe)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            if (_unsubscribe.Count >= 6) { Drop(truncated: true); return; }
            // Register rollback first: a subscription may attach and then throw.
            _unsubscribe.Add(unsubscribe);
        }
        if (!TryDiagnosticOperation(subscribe)) Detach();
    }

    internal void ObserveRequest(object key, string? rawUrl, string? resourceType)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            var path = SafePath(rawUrl, resourceType);
            if (path is null || _active.ContainsKey(key)) { Drop(); return; }
            if (_requests.Count >= MaximumRequests) { Drop(truncated: true); return; }
            var record = new RequestRecord(path, resourceType!, Elapsed());
            _active.Add(key, record);
            _requests.Add(record);
        }
    }

    internal void ObserveResponse(object key, int status)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            if (status is < 100 or > 599 || !_active.TryGetValue(key, out var record)
                || record.State != "Started") { Drop(); return; }
            record.Status = status;
            record.ResponseMilliseconds = Elapsed();
            record.State = "Response";
        }
    }

    internal void ObserveEnd(object key, bool failed)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            if (!_active.Remove(key, out var record)) { Drop(); return; }
            record.State = failed ? "Failed" : "Finished";
            record.EndMilliseconds = Elapsed();
        }
    }

    internal void ObserveStage(bool isLoad)
    {
        lock (_gate)
        {
            if (!_accepting) return;
            if (isLoad)
            {
                if (_load.HasValue) { Drop(); return; }
                _load = Elapsed();
            }
            else
            {
                if (_domContentLoaded.HasValue) { Drop(); return; }
                _domContentLoaded = Elapsed();
            }
        }
    }

    internal void Detach(bool? gotoSucceeded = null)
    {
        Action[] actions;
        lock (_gate)
        {
            if (_detachAttempted) return;
            _detachAttempted = true;
            _accepting = false;
            _active.Clear();
            _gotoOutcome = gotoSucceeded.HasValue ? (gotoSucceeded.Value ? "Succeeded" : "Failed") : "Unknown";
            actions = _unsubscribe.ToArray();
            _unsubscribe.Clear();
        }
        var completed = true;
        foreach (var action in actions)
            completed = TryDiagnosticOperation(action) && completed;
        lock (_gate) _detachCompleted = completed;
    }

    internal byte[] SerializeSnapshot()
    {
        lock (_gate)
        {
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schemaVersion = 1,
                kind = "PublicLoopbackNavigation",
                theme = _theme,
                viewport = _viewport,
                route = "/login",
                gotoOutcome = _gotoOutcome,
                domContentLoadedMilliseconds = _domContentLoaded,
                loadMilliseconds = _load,
                detachAttempted = _detachAttempted,
                detachCompleted = _detachCompleted,
                diagnosticIncomplete = _incomplete,
                truncated = _truncated,
                droppedEvents = _dropped,
                requests = _requests.Select(record => new
                {
                    path = record.Path,
                    resourceType = record.Type,
                    status = record.Status,
                    state = record.State,
                    startedMilliseconds = record.StartedMilliseconds,
                    responseMilliseconds = record.ResponseMilliseconds,
                    endMilliseconds = record.EndMilliseconds
                }).ToArray()
            });
            if (bytes.Length > MaximumJsonBytes)
                throw new InvalidOperationException("NAVIGATION_DIAGNOSTIC_BYTE_LIMIT");
            return bytes;
        }
    }

    internal bool TryPersist()
    {
        Detach();
        lock (_gate)
        {
            if (_persistenceAttempted) return _persistenceSucceeded;
            _persistenceAttempted = true;
        }
        var succeeded = TryDiagnosticOperation(() =>
        {
            var bytes = SerializeSnapshot();
            AssertExpectedRepository();
            AssertNoReparseAncestors(ArtifactDirectory);
            Directory.CreateDirectory(ArtifactDirectory);
            AssertNoReparseAncestors(ArtifactDirectory);
            using (var stream = new FileStream(ArtifactPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(bytes);
                stream.Flush(flushToDisk: true);
            }
            if ((File.GetAttributes(ArtifactPath) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("NAVIGATION_DIAGNOSTIC_READBACK_REJECTED");
            using var held = new FileStream(ArtifactPath, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (held.Length != bytes.Length || held.Length > MaximumJsonBytes)
                throw new IOException("NAVIGATION_DIAGNOSTIC_READBACK_REJECTED");
            var actual = new byte[bytes.Length];
            held.ReadExactly(actual);
            if (!actual.AsSpan().SequenceEqual(bytes))
                throw new IOException("NAVIGATION_DIAGNOSTIC_READBACK_REJECTED");
            using var json = JsonDocument.Parse(actual);
        });
        lock (_gate)
        {
            _persistenceSucceeded = succeeded;
            _requests.Clear();
            _active.Clear();
        }
        // A missing/partial file after failure is Unknown; never rewrite/delete it or emit a sidecar.
        return succeeded;
    }

    private string? SafePath(string? rawUrl, string? resourceType)
    {
        if (rawUrl is null || rawUrl.Length > 2048 || rawUrl.Contains('\\')
            || rawUrl.Any(char.IsControl) || rawUrl.Contains('%')
            || !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != _origin.Scheme || !uri.IsLoopback
            || !string.Equals(uri.Host, _origin.Host, StringComparison.OrdinalIgnoreCase)
            || uri.Port != _origin.Port || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0)
            return null;
        // Require an exact raw path: Uri dot-segment normalization cannot turn an unknown path into an asset.
        var originEnd = rawUrl.IndexOf('/', rawUrl.IndexOf("://", StringComparison.Ordinal) + 3);
        if (originEnd < 0 || !string.Equals(rawUrl[originEnd..], uri.AbsolutePath, StringComparison.Ordinal))
            return null;
        foreach (var asset in Assets)
        {
            if (!string.Equals(resourceType, asset.Type, StringComparison.Ordinal)) continue;
            if (uri.AbsolutePath == asset.Path) return asset.Path;
            if (asset.Path == "/login") continue;
            var dot = asset.Path switch
            {
                "/Memory.Dashboard.styles.css" => asset.Path.Length - ".styles.css".Length,
                "/Components/Layout/ReconnectModal.razor.js" => asset.Path.Length - ".razor.js".Length,
                _ => asset.Path.LastIndexOf('.')
            };
            var prefix = asset.Path[..dot] + ".";
            var suffix = asset.Path[dot..];
            var path = uri.AbsolutePath;
            if (path.Length != prefix.Length + 10 + suffix.Length
                || !path.StartsWith(prefix, StringComparison.Ordinal) || !path.EndsWith(suffix, StringComparison.Ordinal))
                continue;
            var fingerprint = path.AsSpan(prefix.Length, 10);
            var valid = true;
            foreach (var character in fingerprint)
                if (character is not (>= 'a' and <= 'z') and not (>= '0' and <= '9')) valid = false;
            if (valid) return asset.Path;
        }
        return null;
    }

    private long Elapsed()
    {
        var elapsed = Stopwatch.GetElapsedTime(_started).TotalMilliseconds;
        if (elapsed > 60000) { _incomplete = true; _truncated = true; return 60000; }
        return (long)Math.Max(0, elapsed);
    }

    private void Drop(bool truncated = false)
    {
        _incomplete = true;
        _truncated |= truncated;
        if (_dropped < int.MaxValue) _dropped++;
    }

    // Pure structural predicate only: it cannot select a persistence destination.
    internal static bool HasExpectedOutputLayout(string baseDirectory, string assemblyLocation)
    {
        try
        {
            if (!Path.IsPathFullyQualified(baseDirectory) || !Path.IsPathFullyQualified(assemblyLocation)) return false;
            var output = new DirectoryInfo(Path.GetFullPath(baseDirectory));
            var configuration = output.Parent;
            var bin = configuration?.Parent;
            var project = bin?.Parent;
            var tests = project?.Parent;
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            return output.Name == "net10.0" && (configuration?.Name is "Debug" or "Release")
                && bin?.Name == "bin" && project?.Name == "Memory.DashboardTests" && tests?.Name == "tests"
                && string.Equals(Path.GetFullPath(assemblyLocation),
                    Path.Combine(output.FullName, "Memory.DashboardTests.dll"), comparison);
        }
        catch { return false; }
    }

    private void AssertExpectedRepository()
    {
        if (!HasExpectedOutputLayout(AppContext.BaseDirectory, typeof(DashboardNavigationDiagnostics).Assembly.Location))
            throw new IOException("NAVIGATION_DIAGNOSTIC_REPOSITORY_REJECTED");
        AssertNoReparseAncestors(_repoRoot);
        var solution = Path.Combine(_repoRoot, "ContextHub.slnx");
        var project = Path.Combine(_repoRoot, "tests", "Memory.DashboardTests", "Memory.DashboardTests.csproj");
        var dashboard = Path.Combine(_repoRoot, "src", "Memory.Dashboard");
        foreach (var marker in new[] { solution, project })
        {
            if (!File.Exists(marker) || (File.GetAttributes(marker) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException("NAVIGATION_DIAGNOSTIC_REPOSITORY_REJECTED");
            AssertNoReparseAncestors(Path.GetDirectoryName(marker)!);
        }
        if (!Directory.Exists(dashboard))
            throw new IOException("NAVIGATION_DIAGNOSTIC_REPOSITORY_REJECTED");
        AssertNoReparseAncestors(dashboard);
        var assembly = typeof(DashboardNavigationDiagnostics).Assembly.Location;
        if (!File.Exists(assembly) || (File.GetAttributes(assembly) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new IOException("NAVIGATION_DIAGNOSTIC_REPOSITORY_REJECTED");
        AssertNoReparseAncestors(Path.GetDirectoryName(assembly)!);
    }

    private static void AssertNoReparseAncestors(string path)
    {
        for (DirectoryInfo? directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("NAVIGATION_DIAGNOSTIC_PATH_REJECTED");
    }

    private sealed class RequestRecord(string path, string type, long startedMilliseconds)
    {
        internal string Path { get; } = path;
        internal string Type { get; } = type;
        internal long StartedMilliseconds { get; } = startedMilliseconds;
        internal int? Status { get; set; }
        internal string State { get; set; } = "Started";
        internal long? ResponseMilliseconds { get; set; }
        internal long? EndMilliseconds { get; set; }
    }
}

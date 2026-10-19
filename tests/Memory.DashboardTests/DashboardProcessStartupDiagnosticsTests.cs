using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Memory.DashboardTests;

public sealed class DashboardProcessStartupDiagnosticsTests
{
    [Fact]
    public void Unknown_process_and_readiness_are_not_reported_as_success()
    {
        var diagnostics = CreateDiagnostics();

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        root.GetProperty("processId").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("processStartedAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("observedExitCode").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("readiness").GetString().Should().Be("Unknown");
        root.GetProperty("readyAtUtc").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("readinessElapsedMilliseconds").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("fixtureDisposal").GetString().Should().Be("Unknown");
        root.GetProperty("actualArgumentsIdentity").GetString().Should().Be("Unverified");
        root.GetProperty("moduleIdentity").GetString().Should().Be("Unverified");
        root.GetProperty("stdoutCompleted").GetBoolean().Should().BeFalse();
        root.GetProperty("stderrCompleted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Arbitrary_output_is_reduced_to_bounded_code_candidates_and_counts()
    {
        var diagnostics = CreateDiagnostics();
        const string payload = "sensitive-canary-payload";
        diagnostics.ObserveOutput($"Authorization: Bearer {payload}; cookie={payload}; error MSB1009: {payload}", false);
        diagnostics.ObserveOutput($"{{\"token\":\"{payload}\",\"error\":\"NETSDK1045 CS1002\"}}", true);

        var serialized = Encoding.UTF8.GetString(diagnostics.SerializeSnapshot());
        serialized.Should().NotContain(payload).And.NotContain("Authorization").And.NotContain("cookie");
        using var json = JsonDocument.Parse(serialized);
        json.RootElement.GetProperty("sdkCodeCandidates").EnumerateArray().Select(x => x.GetString())
            .Should().Equal("CS1002", "MSB1009", "NETSDK1045");
        json.RootElement.GetProperty("stdoutLines").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("stderrLines").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("rawOutputRetained").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Oversized_lines_and_code_sets_are_truncated_without_retaining_payload()
    {
        var diagnostics = CreateDiagnostics();
        var line = new string('x', DashboardProcessStartupDiagnostics.MaximumScannedLineCharacters + 100_000);
        diagnostics.ObserveOutput(line + " NETSDK9999", true);
        for (var index = 0; index < DashboardProcessStartupDiagnostics.MaximumSdkCodes + 5; index++)
            diagnostics.ObserveOutput($"CS{index:0000}", false);

        var bytes = diagnostics.SerializeSnapshot();
        bytes.Length.Should().BeLessThanOrEqualTo(DashboardProcessStartupDiagnostics.MaximumJsonBytes);
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("outputTruncated").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("unclassifiedOutputObserved").GetBoolean().Should().BeTrue();
        var codes = json.RootElement.GetProperty("sdkCodeCandidates").EnumerateArray().Select(x => x.GetString()).ToArray();
        codes.Should().HaveCount(DashboardProcessStartupDiagnostics.MaximumSdkCodes).And.NotContain("NETSDK9999");
        Encoding.UTF8.GetString(bytes).Should().NotContain(line);
    }

    [Fact]
    public void Fixed_failure_markers_retain_only_booleans_and_not_surrounding_text()
    {
        var diagnostics = CreateDiagnostics();
        const string payload = "startup-private-canary";
        diagnostics.ObserveOutput($"Unhandled exception. {payload}: Failed to bind http://{payload}; Address already in use", true);
        var bytes = diagnostics.SerializeSnapshot();

        Encoding.UTF8.GetString(bytes).Should().NotContain(payload).And.NotContain("http://startup-private");
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("addressAlreadyInUseMarkerObserved").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("failedToBindMarkerObserved").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("unhandledExceptionMarkerObserved").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Code_like_substrings_and_partial_scan_boundary_tokens_are_not_codes()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.ObserveOutput("xCS1002 CS10020 MSB1009x NETSDK12345 cs1002", false);
        diagnostics.ObserveOutput(new string(' ', DashboardProcessStartupDiagnostics.MaximumScannedLineCharacters - 6) + "CS1002extra", true);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("sdkCodeCandidates").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("outputTruncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Eof_is_separate_from_a_line_and_does_not_invent_a_process_exit()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.ObserveOutput(null, false);
        diagnostics.ObserveOutput(null, true);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("stdoutCompleted").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("stderrCompleted").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("stdoutLines").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("observedExitCode").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void Fixed_request_and_readiness_metadata_do_not_claim_actual_argv_identity()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordReadinessStarted();
        diagnostics.RecordHttpAttempt();
        diagnostics.RecordHttpFailure(timedOut: false);
        diagnostics.RecordHttpAttempt();
        diagnostics.RecordHttpStatus(200);
        diagnostics.RecordListeningPort(58048);
        diagnostics.RecordReady();
        diagnostics.RecordInitializationCompleted();

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("requestedArguments").EnumerateArray().Select(x => x.GetString()).Should().Equal(
            "run", "--no-build", "--no-launch-profile", "--configuration", "Debug", "--project", ProjectPath,
            "--", "--urls", "http://127.0.0.1:58048");
        json.RootElement.GetProperty("readiness").GetString().Should().Be("Ready");
        json.RootElement.GetProperty("readyAtUtc").GetDateTime().Kind.Should().Be(DateTimeKind.Utc);
        json.RootElement.GetProperty("readinessElapsedMilliseconds").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        json.RootElement.GetProperty("startupElapsedMillisecondsAtReadinessOutcome").GetDouble().Should()
            .BeGreaterThanOrEqualTo(json.RootElement.GetProperty("readinessElapsedMilliseconds").GetDouble());
        json.RootElement.GetProperty("httpAttempts").GetInt32().Should().Be(2);
        json.RootElement.GetProperty("lastHttpStatus").GetInt32().Should().Be(200);
        json.RootElement.GetProperty("actualArgumentsIdentity").GetString().Should().Be("Unverified");
    }

    [Fact]
    public void Failure_after_readiness_preserves_the_distinct_readiness_outcome()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordReady();
        diagnostics.RecordInitializationFailed();
        diagnostics.RecordDisposal(completed: false);
        diagnostics.RecordOmittedAttempt();

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("readiness").GetString().Should().Be("Ready");
        json.RootElement.GetProperty("initialization").GetString().Should().Be("Failed");
        json.RootElement.GetProperty("fixtureDisposal").GetString().Should().Be("NotCompleted");
        json.RootElement.GetProperty("omittedStartupAttempts").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Unsupported_request_metadata_is_unknown_and_not_serialized_as_arguments()
    {
        var diagnostics = DashboardProcessStartupDiagnostics.TryCreate(ProjectPath, "unsupported-canary", new Uri("http://127.0.0.1:58048/"), 58048)!;
        var bytes = diagnostics.SerializeSnapshot();

        Encoding.UTF8.GetString(bytes).Should().NotContain("unsupported-canary");
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("requestedArguments").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Persistence_failure_is_best_effort_and_leaves_the_capture_incomplete()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordReady();

        var persist = () => diagnostics.TryPersist("\0");
        persist.Should().NotThrow();
        persist.Should().NotThrow();

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("readiness").GetString().Should().Be("Ready");
        json.RootElement.GetProperty("observedExitCode").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("fixtureDisposal").GetString().Should().Be("Unknown");
        json.RootElement.GetProperty("processTreeCleanup").GetString().Should().Be("Unverified");
    }

    [Fact]
    public void Persistence_is_unique_bounded_and_once_per_instance_in_owned_repo_artifacts()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var artifactParent = Path.Combine(repoRoot, ".agent", "local", "test-results", "dashboard-tests",
            "startup-diagnostics-unit", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(artifactParent);
        var first = CreateDiagnostics();
        var second = CreateDiagnostics();
        first.RecordReady();

        Directory.EnumerateFileSystemEntries(artifactParent).Should().BeEmpty();
        first.TryPersist(artifactParent);
        var firstFile = Directory.GetFiles(artifactParent, "startup.json", SearchOption.AllDirectories).Should().ContainSingle().Which;
        var firstBytes = File.ReadAllBytes(firstFile);
        first.ObserveOutput("CS1002 after-persist-canary", true);
        first.TryPersist(artifactParent);
        File.ReadAllBytes(firstFile).Should().Equal(firstBytes);
        second.TryPersist(artifactParent);

        var directories = Directory.GetDirectories(artifactParent);
        directories.Should().HaveCount(2).And.OnlyContain(path => IsStartupDirectoryName(Path.GetFileName(path)));
        var files = Directory.GetFiles(artifactParent, "startup.json", SearchOption.AllDirectories);
        files.Should().HaveCount(2);
        foreach (var file in files)
        {
            var bytes = File.ReadAllBytes(file);
            bytes.Length.Should().BeInRange(1, DashboardProcessStartupDiagnostics.MaximumJsonBytes);
            Encoding.UTF8.GetString(bytes).Should().NotContain("after-persist-canary");
            using var json = JsonDocument.Parse(bytes);
            json.RootElement.GetProperty("kind").GetString().Should().Be("DashboardFixtureStartupMetadata");
        }
        File.ReadAllBytes(firstFile).Should().Equal(firstBytes);
    }

    [Fact]
    public void Snapshot_bytes_are_detached_from_later_output_and_state_changes()
    {
        var diagnostics = CreateDiagnostics();
        var before = diagnostics.SerializeSnapshot();
        diagnostics.ObserveOutput("CS1002 snapshot-canary", true);
        diagnostics.RecordReady();

        using var original = JsonDocument.Parse(before);
        original.RootElement.GetProperty("sdkCodeCandidates").GetArrayLength().Should().Be(0);
        original.RootElement.GetProperty("readiness").GetString().Should().Be("Unknown");
        Encoding.UTF8.GetString(diagnostics.SerializeSnapshot()).Should().NotContain("snapshot-canary");
    }

    private static string ProjectPath => Path.GetFullPath(Path.Combine("diagnostic-fixture", "Memory.Dashboard.csproj"));

    private static DashboardProcessStartupDiagnostics CreateDiagnostics()
        => DashboardProcessStartupDiagnostics.TryCreate(ProjectPath, "Debug", new Uri("http://127.0.0.1:58048/"), 58048)!;

    private static bool IsStartupDirectoryName(string name)
        => name.StartsWith("startup-", StringComparison.Ordinal) && name.Length == 40
            && Guid.TryParseExact(name[8..], "N", out _);
}

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
        root.GetProperty("fixtureTestClass").GetString().Should().Be("Unknown");
        root.GetProperty("browserConnectedBeforeClose").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("browserConnectedAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("browserCloseTaskStateAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("browserCloseAfterCleanupObserved").GetBoolean().Should().BeFalse();
        root.GetProperty("browserCloseTaskStateAfterCleanup").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("hostObservationOutcome").GetString().Should().Be("NotObserved");
        root.GetProperty("hostObservationFailedStage").GetString().Should().Be("None");
        root.GetProperty("actualArgumentsIdentity").GetString().Should().Be("Unverified");
        root.GetProperty("moduleIdentity").GetString().Should().Be("Unverified");
        root.GetProperty("stdoutCompleted").GetBoolean().Should().BeFalse();
        root.GetProperty("stderrCompleted").GetBoolean().Should().BeFalse();
    }

    [Fact]
    public void Started_process_observation_reads_each_value_once_in_order_and_keeps_identity_unverified()
    {
        var diagnostics = CreateDiagnostics();
        var reads = new List<string>();
        var started = new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc);
        var host = Path.GetFullPath(Path.Combine("diagnostic-host", "dotnet.exe"));

        diagnostics.ObserveStartedProcess(
            () => { reads.Add("id"); return 123; },
            () => { reads.Add("start"); return started; },
            () => { reads.Add("path"); return host; });

        reads.Should().Equal("id", "start", "path");
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        root.GetProperty("processId").GetInt32().Should().Be(123);
        root.GetProperty("processStartedAtUtc").GetDateTime().Should().Be(started);
        root.GetProperty("resolvedHostPath").GetString().Should().Be(host);
        root.GetProperty("hostObservationOutcome").GetString().Should().Be("Observed");
        root.GetProperty("hostObservationFailedStage").GetString().Should().Be("None");
        root.GetProperty("recordingIncomplete").GetBoolean().Should().BeFalse();
        root.GetProperty("actualArgumentsIdentity").GetString().Should().Be("Unverified");
        root.GetProperty("moduleIdentity").GetString().Should().Be("Unverified");
    }

    [Theory]
    [InlineData(1, "ProcessId")]
    [InlineData(2, "StartTime")]
    [InlineData(3, "MainModulePath")]
    public void Started_process_getter_failure_records_only_the_fixed_stage_and_stops_later_reads(int failAt, string expectedStage)
    {
        var diagnostics = CreateDiagnostics();
        var reads = 0;
        var canary = new MessageCanaryException();
        void Read()
        {
            reads++;
            if (reads == failAt) throw canary;
        }

        diagnostics.ObserveStartedProcess(
            () => { Read(); return 123; },
            () => { Read(); return new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc); },
            () => { Read(); return Path.GetFullPath("dotnet.exe"); });

        reads.Should().Be(failAt);
        canary.MessageRead.Should().BeFalse();
        var bytes = diagnostics.SerializeSnapshot();
        Encoding.UTF8.GetString(bytes).Should().NotContain("message-access-canary").And.NotContain(nameof(MessageCanaryException));
        bytes.Length.Should().BeLessThanOrEqualTo(DashboardProcessStartupDiagnostics.MaximumJsonBytes);
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        root.GetProperty("hostObservationOutcome").GetString().Should().Be("GetterFailed");
        root.GetProperty("hostObservationFailedStage").GetString().Should().Be(expectedStage);
        root.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
        root.GetProperty("resolvedHostPath").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("processId").ValueKind.Should().Be(failAt > 1 ? JsonValueKind.Number : JsonValueKind.Null);
        root.GetProperty("processStartedAtUtc").ValueKind.Should().Be(failAt > 2 ? JsonValueKind.String : JsonValueKind.Null);
    }

    [Fact]
    public void Null_host_path_is_missing_without_guessing_the_requested_host()
    {
        var diagnostics = CreateDiagnostics();
        var pathReads = 0;
        diagnostics.ObserveStartedProcess(() => 123, () => DateTime.UnixEpoch, () => { pathReads++; return null; });

        pathReads.Should().Be(1);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        root.GetProperty("hostObservationOutcome").GetString().Should().Be("MissingPath");
        root.GetProperty("hostObservationFailedStage").GetString().Should().Be("MainModulePath");
        root.GetProperty("resolvedHostPath").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("processId").GetInt32().Should().Be(123);
        root.GetProperty("processStartedAtUtc").GetDateTime().Should().Be(DateTime.UnixEpoch);
        root.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("relative-private-path-canary/dotnet.exe")]
    [InlineData("oversize")]
    public void Invalid_host_path_is_classified_without_retaining_the_rejected_value(string invalidPath)
    {
        var diagnostics = CreateDiagnostics();
        if (invalidPath == "oversize") invalidPath = Path.GetFullPath("private-path-canary") + new string('x', 1100);
        diagnostics.ObserveStartedProcess(() => 123, () => DateTime.UnixEpoch, () => invalidPath);

        var bytes = diagnostics.SerializeSnapshot();
        Encoding.UTF8.GetString(bytes).Should().NotContain("private-path-canary");
        using var json = JsonDocument.Parse(bytes);
        var root = json.RootElement;
        root.GetProperty("hostObservationOutcome").GetString().Should().Be("InvalidPath");
        root.GetProperty("hostObservationFailedStage").GetString().Should().Be("PathValidation");
        root.GetProperty("resolvedHostPath").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Valid_host_observation_does_not_clear_an_unrelated_incomplete_capture()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.MarkIncomplete();
        diagnostics.ObserveStartedProcess(() => 123, () => DateTime.UnixEpoch, () => Path.GetFullPath("dotnet.exe"));

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("hostObservationOutcome").GetString().Should().Be("Observed");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Process_entry_point_uses_the_same_failed_getter_contract_for_an_unstarted_component()
    {
        var diagnostics = CreateDiagnostics();
        using var process = new System.Diagnostics.Process();
        diagnostics.ObserveStartedProcess(process);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("hostObservationOutcome").GetString().Should().Be("GetterFailed");
        json.RootElement.GetProperty("hostObservationFailedStage").GetString().Should().Be("ProcessId");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Current_process_observation_uses_the_real_image_path_without_closing_the_borrowed_handle()
    {
        var diagnostics = CreateDiagnostics();
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        var expectedPath = Environment.ProcessPath ?? throw new InvalidOperationException("The current process image path is unavailable.");
        var borrowedHandle = OperatingSystem.IsWindows() ? process.SafeHandle : null;

        diagnostics.ObserveStartedProcess(process);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        var observedPath = root.GetProperty("resolvedHostPath").GetString();
        observedPath.Should().NotBeNull();
        string.Equals(Path.GetFullPath(observedPath!), Path.GetFullPath(expectedPath),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal).Should().BeTrue();
        root.GetProperty("processId").GetInt32().Should().Be(process.Id);
        root.GetProperty("hostObservationOutcome").GetString().Should().Be("Observed");
        root.GetProperty("hostObservationFailedStage").GetString().Should().Be("None");
        root.GetProperty("recordingIncomplete").GetBoolean().Should().BeFalse();
        root.GetProperty("actualArgumentsIdentity").GetString().Should().Be("Unverified");
        root.GetProperty("moduleIdentity").GetString().Should().Be("Unverified");
        if (borrowedHandle is not null)
        {
            borrowedHandle.IsClosed.Should().BeFalse();
            borrowedHandle.IsInvalid.Should().BeFalse();
            process.SafeHandle.Should().BeSameAs(borrowedHandle);
        }
    }

    [Fact]
    public void Persisted_host_failure_is_not_rewritten_by_later_observations()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var artifactParent = Path.Combine(repoRoot, ".agent", "local", "test-results", "dashboard-tests",
            "startup-diagnostics-unit", Guid.NewGuid().ToString("N"));
        var diagnostics = CreateDiagnostics();
        diagnostics.ObserveStartedProcess(() => 123, () => DateTime.UnixEpoch, () => null);
        diagnostics.TryPersist(artifactParent);
        var file = Directory.GetFiles(artifactParent, "startup.json", SearchOption.AllDirectories).Should().ContainSingle().Which;
        var original = File.ReadAllBytes(file);

        diagnostics.ObserveStartedProcess(() => 123, () => DateTime.UnixEpoch, () => Path.GetFullPath("dotnet.exe"));
        diagnostics.TryPersist(artifactParent);

        File.ReadAllBytes(file).Should().Equal(original);
        using var json = JsonDocument.Parse(original);
        json.RootElement.GetProperty("hostObservationOutcome").GetString().Should().Be("MissingPath");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
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
            AssemblyPath, "--urls", "http://127.0.0.1:58048");
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
        var diagnostics = DashboardProcessStartupDiagnostics.TryCreate(AssemblyPath, "unsupported-canary", new Uri("http://127.0.0.1:58048/"), 58048)!;
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

    [Fact]
    public void Teardown_defaults_do_not_claim_that_any_operation_ran()
    {
        var diagnostics = CreateDiagnostics();
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        root.GetProperty("teardownObserved").GetBoolean().Should().BeFalse();
        root.GetProperty("firstObservedTeardownFailure").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("outerPropagatedTeardownFailureKind").ValueKind.Should().Be(JsonValueKind.Null);
        var steps = root.GetProperty("teardownSteps").EnumerateArray().ToArray();
        steps.Select(step => step.GetProperty("step").GetString()).Should()
            .Equal("BrowserClose", "HostKill", "HostWait", "PlaywrightDispose");
        foreach (var step in steps)
        {
            step.GetProperty("outcome").GetString().Should().Be("NotReached");
            step.GetProperty("elapsedMilliseconds").ValueKind.Should().Be(JsonValueKind.Null);
            step.GetProperty("failureKind").ValueKind.Should().Be(JsonValueKind.Null);
        }
    }

    [Fact]
    public void Successful_teardown_steps_have_elapsed_time_but_skipped_steps_do_not()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Completed);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostKill, DashboardTeardownOutcome.Skipped);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostWait, DashboardTeardownOutcome.Skipped);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Completed);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        foreach (var phase in new[] { DashboardTeardownStep.BrowserClose, DashboardTeardownStep.PlaywrightDispose })
        {
            var step = ReadTeardownStep(root, phase);
            step.GetProperty("outcome").GetString().Should().Be("Completed");
            step.GetProperty("elapsedMilliseconds").GetDouble().Should().BeGreaterThanOrEqualTo(0);
        }
        foreach (var phase in new[] { DashboardTeardownStep.HostKill, DashboardTeardownStep.HostWait })
        {
            var step = ReadTeardownStep(root, phase);
            step.GetProperty("outcome").GetString().Should().Be("Skipped");
            step.GetProperty("elapsedMilliseconds").ValueKind.Should().Be(JsonValueKind.Null);
        }
        root.GetProperty("firstObservedTeardownFailure").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Fact]
    public void First_browser_timeout_survives_successful_later_cleanup()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        foreach (var step in new[] { DashboardTeardownStep.HostKill, DashboardTeardownStep.HostWait, DashboardTeardownStep.PlaywrightDispose })
        {
            diagnostics.RecordTeardownStep(step, DashboardTeardownOutcome.Started);
            diagnostics.RecordTeardownStep(step, DashboardTeardownOutcome.Completed);
        }
        diagnostics.RecordTeardownOuterFailure(DashboardTeardownFailureKind.Timeout);
        diagnostics.RecordDisposal(completed: false);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        var first = root.GetProperty("firstObservedTeardownFailure");
        first.GetProperty("step").GetString().Should().Be("BrowserClose");
        first.GetProperty("failureKind").GetString().Should().Be("Timeout");
        ReadTeardownStep(root, DashboardTeardownStep.BrowserClose).GetProperty("outcome").GetString().Should().Be("Failed");
        ReadTeardownStep(root, DashboardTeardownStep.PlaywrightDispose).GetProperty("outcome").GetString().Should().Be("Completed");
        root.GetProperty("fixtureDisposal").GetString().Should().Be("NotCompleted");
    }

    [Fact]
    public void Later_finally_failure_is_distinct_from_the_first_observed_failure()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Other);
        diagnostics.RecordTeardownOuterFailure(DashboardTeardownFailureKind.Other);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        root.GetProperty("firstObservedTeardownFailure").GetProperty("step").GetString().Should().Be("BrowserClose");
        root.GetProperty("firstObservedTeardownFailure").GetProperty("failureKind").GetString().Should().Be("Timeout");
        root.GetProperty("outerPropagatedTeardownFailureKind").GetString().Should().Be("Other");
        ReadTeardownStep(root, DashboardTeardownStep.PlaywrightDispose).GetProperty("failureKind").GetString().Should().Be("Other");
    }

    [Fact]
    public void Host_wait_timeout_does_not_relabel_the_successful_kill()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostKill, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostKill, DashboardTeardownOutcome.Completed);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostWait, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostWait, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        ReadTeardownStep(root, DashboardTeardownStep.HostKill).GetProperty("outcome").GetString().Should().Be("Completed");
        ReadTeardownStep(root, DashboardTeardownStep.HostWait).GetProperty("outcome").GetString().Should().Be("Failed");
        root.GetProperty("firstObservedTeardownFailure").GetProperty("step").GetString().Should().Be("HostWait");
    }

    [Fact]
    public void Terminal_failure_cannot_be_replaced_by_a_later_success_record()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostWait, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostWait, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Canceled);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostWait, DashboardTeardownOutcome.Completed);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostWait, DashboardTeardownOutcome.Started);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        ReadTeardownStep(root, DashboardTeardownStep.HostWait).GetProperty("outcome").GetString().Should().Be("Failed");
        ReadTeardownStep(root, DashboardTeardownStep.HostWait).GetProperty("failureKind").GetString().Should().Be("Canceled");
        root.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Invalid_phase_outcome_or_transition_is_incomplete_not_a_fabricated_completion()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep((DashboardTeardownStep)int.MaxValue, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.HostKill, (DashboardTeardownOutcome)int.MaxValue);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Completed);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        root.GetProperty("teardownSteps").GetArrayLength().Should().Be(4);
        root.GetProperty("teardownSteps").EnumerateArray().Should().OnlyContain(
            step => step.GetProperty("outcome").GetString() == "NotReached");
        root.GetProperty("firstObservedTeardownFailure").ValueKind.Should().Be(JsonValueKind.Null);
        root.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Unknown_failure_category_and_unattributed_failure_remain_explicit()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownOuterFailure((DashboardTeardownFailureKind)int.MaxValue);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        var root = json.RootElement;
        root.GetProperty("firstObservedTeardownFailure").GetProperty("step").GetString().Should().Be("Unknown");
        root.GetProperty("firstObservedTeardownFailure").GetProperty("failureKind").GetString().Should().Be("Unknown");
        root.GetProperty("outerPropagatedTeardownFailureKind").GetString().Should().Be("Unknown");
        root.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Failure_classification_does_not_read_or_serialize_arbitrary_exception_members()
    {
        var diagnostics = CreateDiagnostics();
        var canary = new MessageCanaryException();
        DashboardProcessStartupDiagnostics.ClassifyTeardownFailure(new TimeoutException("private-timeout-canary")).Should().Be(DashboardTeardownFailureKind.Timeout);
        DashboardProcessStartupDiagnostics.ClassifyTeardownFailure(new TaskCanceledException("private-cancel-canary")).Should().Be(DashboardTeardownFailureKind.Canceled);
        DashboardProcessStartupDiagnostics.ClassifyTeardownFailure(null).Should().Be(DashboardTeardownFailureKind.Unknown);
        var category = DashboardProcessStartupDiagnostics.ClassifyTeardownFailure(canary);
        category.Should().Be(DashboardTeardownFailureKind.Other);
        canary.MessageRead.Should().BeFalse();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Failed, category);
        var serialized = Encoding.UTF8.GetString(diagnostics.SerializeSnapshot());
        serialized.Should().NotContain("private-timeout-canary").And.NotContain("private-cancel-canary")
            .And.NotContain(nameof(MessageCanaryException)).And.NotContain("message-access-canary");
    }

    [Fact]
    public void Repeated_invalid_updates_keep_the_teardown_state_and_snapshot_bounded()
    {
        var diagnostics = CreateDiagnostics();
        for (var index = 0; index < 1024; index++)
            diagnostics.RecordTeardownStep((DashboardTeardownStep)(index + 4), DashboardTeardownOutcome.Started);
        var bytes = diagnostics.SerializeSnapshot();
        bytes.Length.Should().BeLessThanOrEqualTo(DashboardProcessStartupDiagnostics.MaximumJsonBytes);
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("teardownSteps").GetArrayLength().Should().Be(4);
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Browser_close_start_observes_connection_once_without_inventing_timeout_data(bool connected)
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        var reads = 0;
        diagnostics.ObserveBrowserCloseStart(() => { reads++; return connected; });
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Completed);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        reads.Should().Be(1);
        json.RootElement.GetProperty("browserConnectedBeforeClose").GetBoolean().Should().Be(connected);
        json.RootElement.GetProperty("browserConnectedAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Browser_timeout_records_connection_and_pending_task_without_completing_it(bool connected)
    {
        var diagnostics = CreateDiagnostics();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseStart(() => true);
        diagnostics.ObserveBrowserCloseTimeout(() => connected, pending.Task);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        pending.Task.IsCompleted.Should().BeFalse();
        pending.TrySetResult();
        json.RootElement.GetProperty("browserConnectedAtTimeout").GetBoolean().Should().Be(connected);
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").GetString().Should().Be("Pending");
        ReadTeardownStep(json.RootElement, DashboardTeardownStep.BrowserClose).GetProperty("outcome").GetString().Should().Be("Failed");
    }

    [Theory]
    [InlineData(TaskStatus.Created, "Pending")]
    [InlineData(TaskStatus.WaitingForActivation, "Pending")]
    [InlineData(TaskStatus.WaitingToRun, "Pending")]
    [InlineData(TaskStatus.Running, "Pending")]
    [InlineData(TaskStatus.WaitingForChildrenToComplete, "Pending")]
    [InlineData(TaskStatus.RanToCompletion, "RanToCompletion")]
    [InlineData(TaskStatus.Canceled, "Canceled")]
    [InlineData(TaskStatus.Faulted, "Faulted")]
    [InlineData((TaskStatus)int.MaxValue, "Unavailable")]
    public void Browser_close_task_states_have_a_closed_serialization_contract(
        TaskStatus state, string expected)
        => DashboardProcessStartupDiagnostics.ClassifyBrowserCloseTaskState(state).ToString().Should().Be(expected);

    [Theory]
    [InlineData("RanToCompletion")]
    [InlineData("Canceled")]
    [InlineData("Faulted")]
    public async Task Browser_timeout_samples_real_terminal_tasks_without_relabeling_the_original_failure(string expected)
    {
        var diagnostics = CreateDiagnostics();
        var canary = new MessageCanaryException();
        var task = expected switch
        {
            "Canceled" => Task.FromCanceled(new CancellationToken(canceled: true)),
            "Faulted" => Task.FromException(canary),
            _ => Task.CompletedTask
        };
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseStart(() => true);
        diagnostics.ObserveBrowserCloseTimeout(() => false, task);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").GetString().Should().Be(expected);
        ReadTeardownStep(json.RootElement, DashboardTeardownStep.BrowserClose).GetProperty("failureKind").GetString().Should().Be("Timeout");
        canary.MessageRead.Should().BeFalse();
        if (expected == "Faulted")
        {
            try { await task; }
            catch (MessageCanaryException) { }
        }
    }

    [Fact]
    public void Canceled_browser_close_keeps_the_cancellation_failure_without_timeout_samples()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseStart(() => true);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Canceled);
        diagnostics.RecordTeardownOuterFailure(DashboardTeardownFailureKind.Canceled);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("browserConnectedAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        ReadTeardownStep(json.RootElement, DashboardTeardownStep.BrowserClose).GetProperty("outcome").GetString().Should().Be("Failed");
        json.RootElement.GetProperty("firstObservedTeardownFailure").GetProperty("failureKind").GetString().Should().Be("Canceled");
        json.RootElement.GetProperty("outerPropagatedTeardownFailureKind").GetString().Should().Be("Canceled");
    }

    [Fact]
    public void Browser_connection_observer_failure_is_incomplete_and_does_not_hide_other_observations()
    {
        var diagnostics = CreateDiagnostics();
        var canary = new MessageCanaryException();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseStart(() => throw canary);
        diagnostics.ObserveBrowserCloseTimeout(() => throw canary, Task.CompletedTask);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        diagnostics.RecordTeardownOuterFailure(DashboardTeardownFailureKind.Timeout);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("browserConnectedBeforeClose").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("browserConnectedAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").GetString().Should().Be("RanToCompletion");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("firstObservedTeardownFailure").GetProperty("failureKind").GetString().Should().Be("Timeout");
        json.RootElement.GetProperty("outerPropagatedTeardownFailureKind").GetString().Should().Be("Timeout");
        canary.MessageRead.Should().BeFalse();
    }

    [Fact]
    public void Browser_timeout_without_a_task_remains_unavailable_and_incomplete()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseStart(() => true);
        diagnostics.ObserveBrowserCloseTimeout(() => false, null);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("browserConnectedAtTimeout").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").GetString().Should().Be("Unavailable");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
        DashboardProcessStartupDiagnostics.ClassifyBrowserCloseTaskState(null).Should().Be(DashboardBrowserCloseTaskState.Unavailable);
    }

    [Fact]
    public void Repeated_browser_observations_keep_the_first_sample_and_mark_incomplete()
    {
        var diagnostics = CreateDiagnostics();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var duplicateStartReads = 0;
        var duplicateTimeoutReads = 0;
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseStart(() => true);
        diagnostics.ObserveBrowserCloseStart(() =>
        {
            duplicateStartReads++;
            throw new InvalidOperationException("must-not-run");
        });
        diagnostics.ObserveBrowserCloseTimeout(() => false, pending.Task);
        diagnostics.ObserveBrowserCloseTimeout(() =>
        {
            duplicateTimeoutReads++;
            throw new InvalidOperationException("must-not-run");
        }, Task.CompletedTask);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        pending.TrySetResult();
        duplicateStartReads.Should().Be(0);
        duplicateTimeoutReads.Should().Be(0);
        json.RootElement.GetProperty("browserConnectedBeforeClose").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("browserConnectedAtTimeout").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").GetString().Should().Be("Pending");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Browser_observations_outside_the_started_phase_do_not_invent_samples()
    {
        var diagnostics = CreateDiagnostics();
        var invalidStartReads = 0;
        var invalidTimeoutReads = 0;
        diagnostics.ObserveBrowserCloseStart(() =>
        {
            invalidStartReads++;
            throw new InvalidOperationException("must-not-run");
        });
        diagnostics.ObserveBrowserCloseTimeout(() =>
        {
            invalidTimeoutReads++;
            throw new InvalidOperationException("must-not-run");
        }, Task.CompletedTask);
        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        invalidStartReads.Should().Be(0);
        invalidTimeoutReads.Should().Be(0);
        json.RootElement.GetProperty("browserConnectedBeforeClose").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("browserConnectedAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").ValueKind.Should().Be(JsonValueKind.Null);
        ReadTeardownStep(json.RootElement, DashboardTeardownStep.BrowserClose).GetProperty("outcome").GetString().Should().Be("NotReached");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Browser_observation_schema_is_bounded_and_never_retains_exception_payloads()
    {
        const string payload = "https://private-canary.invalid/?token=browser-close-secret";
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseStart(() => true);
        diagnostics.ObserveBrowserCloseTimeout(() => throw new InvalidOperationException(payload), Task.CompletedTask);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Other);
        diagnostics.RecordTeardownOuterFailure(DashboardTeardownFailureKind.Other);
        var bytes = diagnostics.SerializeSnapshot();
        bytes.Length.Should().BeLessThanOrEqualTo(DashboardProcessStartupDiagnostics.MaximumJsonBytes);
        Encoding.UTF8.GetString(bytes).Should().NotContain(payload).And.NotContain("browser-close-secret");
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("teardownSteps").GetArrayLength().Should().Be(4);
        json.RootElement.GetProperty("firstObservedTeardownFailure").GetProperty("failureKind").GetString().Should().Be("Timeout");
        json.RootElement.GetProperty("outerPropagatedTeardownFailureKind").GetString().Should().Be("Other");
    }

    [Theory]
    [InlineData("DashboardBrowserUiTests")]
    [InlineData("GraphViewportTests")]
    [InlineData("GraphVisibleViewportTests")]
    [InlineData("HeaderViewportTests")]
    [InlineData("DashboardAuthenticationBrowserTests")]
    public void Repeated_test_class_binding_preserves_the_fixed_class_identity(string expected)
    {
        var diagnostics = CreateDiagnostics();
        var testClass = Enum.Parse<DashboardFixtureTestClass>(expected);
        diagnostics.RecordFixtureTestClass(testClass);
        diagnostics.RecordFixtureTestClass(testClass);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("fixtureTestClass").GetString().Should().Be(expected);
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeFalse();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    [InlineData((int)DashboardFixtureTestClass.HeaderViewportTests)]
    public void Conflicting_or_invalid_class_binding_cannot_be_repaired_into_false_identity(int conflictingValue)
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordFixtureTestClass(DashboardFixtureTestClass.GraphViewportTests);
        diagnostics.RecordFixtureTestClass((DashboardFixtureTestClass)conflictingValue);
        diagnostics.RecordFixtureTestClass(DashboardFixtureTestClass.GraphViewportTests);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("fixtureTestClass").GetString().Should().Be("Unknown");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("Pending")]
    [InlineData("RanToCompletion")]
    [InlineData("Canceled")]
    [InlineData("Faulted")]
    public async Task Cleanup_observation_distinguishes_late_close_completion_without_rewriting_timeout(string expected)
    {
        var diagnostics = CreateDiagnostics();
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var canary = new MessageCanaryException();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseStart(() => true);
        diagnostics.ObserveBrowserCloseTimeout(() => false, close.Task);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose,
            DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Completed);
        diagnostics.RecordTeardownOuterFailure(DashboardTeardownFailureKind.Timeout);
        diagnostics.RecordDisposal(false);
        switch (expected)
        {
            case "RanToCompletion": close.SetResult(); break;
            case "Canceled": close.SetCanceled(); break;
            case "Faulted": close.SetException(canary); break;
        }

        diagnostics.ObserveBrowserCloseAfterCleanup(close.Task);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("browserCloseTaskStateAtTimeout").GetString().Should().Be("Pending");
        json.RootElement.GetProperty("browserCloseAfterCleanupObserved").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("browserCloseTaskStateAfterCleanup").GetString().Should().Be(expected);
        json.RootElement.GetProperty("fixtureDisposal").GetString().Should().Be("NotCompleted");
        json.RootElement.GetProperty("firstObservedTeardownFailure").GetProperty("failureKind").GetString().Should().Be("Timeout");
        json.RootElement.GetProperty("outerPropagatedTeardownFailureKind").GetString().Should().Be("Timeout");
        canary.MessageRead.Should().BeFalse();
        if (expected == "Pending") close.SetResult();
        try { await close.Task; }
        catch (MessageCanaryException) when (expected == "Faulted") { }
        catch (OperationCanceledException) when (expected == "Canceled") { }
        canary.MessageRead.Should().BeFalse();
    }

    [Fact]
    public void Cleanup_observation_keeps_the_first_sample_when_the_close_task_later_changes()
    {
        var diagnostics = CreateDiagnostics();
        var close = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose,
            DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Skipped);
        diagnostics.ObserveBrowserCloseAfterCleanup(close.Task);
        close.SetResult();

        diagnostics.ObserveBrowserCloseAfterCleanup(close.Task);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("browserCloseTaskStateAfterCleanup").GetString().Should().Be("Pending");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
        close.Task.Status.Should().Be(TaskStatus.RanToCompletion);
    }

    [Fact]
    public void Premature_or_missing_task_observation_does_not_invent_cleanup_completion()
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.ObserveBrowserCloseAfterCleanup(Task.CompletedTask);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Completed);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Skipped);
        diagnostics.ObserveBrowserCloseAfterCleanup(null);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("browserCloseAfterCleanupObserved").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("browserCloseTaskStateAfterCleanup").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Cleanup_observation_is_available_after_a_failed_or_skipped_final_cleanup_attempt(bool failed)
    {
        var diagnostics = CreateDiagnostics();
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose, DashboardTeardownOutcome.Started);
        diagnostics.RecordTeardownStep(DashboardTeardownStep.BrowserClose,
            DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Timeout);
        if (failed)
        {
            diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Started);
            diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose,
                DashboardTeardownOutcome.Failed, DashboardTeardownFailureKind.Other);
            diagnostics.RecordTeardownOuterFailure(DashboardTeardownFailureKind.Other);
        }
        else diagnostics.RecordTeardownStep(DashboardTeardownStep.PlaywrightDispose, DashboardTeardownOutcome.Skipped);

        diagnostics.ObserveBrowserCloseAfterCleanup(Task.CompletedTask);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("browserCloseTaskStateAfterCleanup").GetString().Should().Be("RanToCompletion");
        json.RootElement.GetProperty("firstObservedTeardownFailure").GetProperty("failureKind").GetString().Should().Be("Timeout");
        ReadTeardownStep(json.RootElement, DashboardTeardownStep.PlaywrightDispose).GetProperty("outcome").GetString()
            .Should().Be(failed ? "Failed" : "Skipped");
    }

    [Fact]
    public void Fixture_binding_updates_existing_and_restarted_attempts_without_rewriting_saved_unknown_evidence()
    {
        var fixture = new DashboardBrowserFixture();
        var first = CreateDiagnostics();
        fixture.RegisterStartupDiagnostics(first);
        var repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var directory = Path.Combine(repoRoot, ".agent", "local", "test-results", "dashboard-tests",
            "fixture-binding-" + Guid.NewGuid().ToString("N"));
        first.TryPersist(directory);
        var saved = Directory.GetFiles(directory, "startup.json", SearchOption.AllDirectories).Single();
        var original = File.ReadAllBytes(saved);
        using (var json = JsonDocument.Parse(original))
            json.RootElement.GetProperty("fixtureTestClass").GetString().Should().Be("Unknown");

        fixture.BindTestClass(DashboardFixtureTestClass.GraphViewportTests);
        fixture.BindTestClass(DashboardFixtureTestClass.GraphViewportTests);
        var restarted = CreateDiagnostics();
        fixture.RegisterStartupDiagnostics(restarted);

        foreach (var diagnostics in new[] { first, restarted })
        {
            using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
            json.RootElement.GetProperty("fixtureTestClass").GetString().Should().Be("GraphViewportTests");
        }
        first.TryPersist(directory);
        File.ReadAllBytes(saved).Should().Equal(original);

        fixture.BindTestClass(DashboardFixtureTestClass.HeaderViewportTests);
        fixture.BindTestClass(DashboardFixtureTestClass.GraphViewportTests);
        var afterConflict = CreateDiagnostics();
        fixture.RegisterStartupDiagnostics(afterConflict);
        foreach (var diagnostics in new[] { first, restarted, afterConflict })
        {
            using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
            json.RootElement.GetProperty("fixtureTestClass").GetString().Should().Be("Unknown");
            json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(999)]
    public void Fixture_rejects_initial_invalid_binding_before_any_startup_attempt(int invalidValue)
    {
        var fixture = new DashboardBrowserFixture();
        fixture.BindTestClass((DashboardFixtureTestClass)invalidValue);
        fixture.BindTestClass(DashboardFixtureTestClass.GraphViewportTests);
        var diagnostics = CreateDiagnostics();

        fixture.RegisterStartupDiagnostics(diagnostics);

        using var json = JsonDocument.Parse(diagnostics.SerializeSnapshot());
        json.RootElement.GetProperty("fixtureTestClass").GetString().Should().Be("Unknown");
        json.RootElement.GetProperty("recordingIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Fixture_limits_startup_registration_and_marks_omission_on_the_last_retained_attempt()
    {
        var fixture = new DashboardBrowserFixture();
        fixture.BindTestClass(DashboardFixtureTestClass.GraphViewportTests);
        var attempts = Enumerable.Range(0, DashboardProcessStartupDiagnostics.MaximumAttempts + 1)
            .Select(_ => CreateDiagnostics()).ToArray();
        foreach (var attempt in attempts) fixture.RegisterStartupDiagnostics(attempt);

        for (var index = 0; index < DashboardProcessStartupDiagnostics.MaximumAttempts; index++)
        {
            using var json = JsonDocument.Parse(attempts[index].SerializeSnapshot());
            json.RootElement.GetProperty("fixtureTestClass").GetString().Should().Be("GraphViewportTests");
            json.RootElement.GetProperty("omittedStartupAttempts").GetInt32()
                .Should().Be(index == DashboardProcessStartupDiagnostics.MaximumAttempts - 1 ? 1 : 0);
        }
        using var omitted = JsonDocument.Parse(attempts[^1].SerializeSnapshot());
        omitted.RootElement.GetProperty("fixtureTestClass").GetString().Should().Be("Unknown");
    }

    private static JsonElement ReadTeardownStep(JsonElement root, DashboardTeardownStep step)
        => root.GetProperty("teardownSteps").EnumerateArray().Single(value => value.GetProperty("step").GetString() == step.ToString());

    private sealed class MessageCanaryException : Exception
    {
        internal bool MessageRead { get; private set; }
        public override string Message
        {
            get
            {
                MessageRead = true;
                throw new InvalidOperationException("message-access-canary");
            }
        }
    }

    private static string AssemblyPath => Path.GetFullPath(Path.Combine("diagnostic-fixture", "bin", "Debug", "net10.0", "Memory.Dashboard.dll"));

    private static DashboardProcessStartupDiagnostics CreateDiagnostics()
        => DashboardProcessStartupDiagnostics.TryCreate(AssemblyPath, "Debug", new Uri("http://127.0.0.1:58048/"), 58048)!;

    private static bool IsStartupDirectoryName(string name)
        => name.StartsWith("startup-", StringComparison.Ordinal) && name.Length == 40
            && Guid.TryParseExact(name[8..], "N", out _);
}

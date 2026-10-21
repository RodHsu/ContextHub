using System.Text;
using System.Text.Json;
using FluentAssertions;

namespace Memory.DashboardTests;

public sealed class DashboardNavigationDiagnosticsTests
{
    private static DashboardNavigationDiagnostics Create() => new(new Uri("http://127.0.0.1:58123/"));
    private static JsonDocument Snapshot(DashboardNavigationDiagnostics diagnostics) => JsonDocument.Parse(diagnostics.SerializeSnapshot());

    [Fact]
    public void Completed_navigation_has_a_closed_safe_schema_and_actual_event_states()
    {
        var diagnostics = Create();
        var document = new object();
        var stylesheet = new object();
        diagnostics.ObserveRequest(document, "http://127.0.0.1:58123/login", "document");
        diagnostics.ObserveResponse(document, 200);
        diagnostics.ObserveEnd(document, failed: false);
        diagnostics.ObserveRequest(stylesheet, "http://127.0.0.1:58123/app.css", "stylesheet");
        diagnostics.ObserveResponse(stylesheet, 304);
        diagnostics.ObserveEnd(stylesheet, failed: false);
        diagnostics.ObserveStage(isLoad: false);
        diagnostics.ObserveStage(isLoad: true);
        diagnostics.Detach(gotoSucceeded: true);

        using var json = Snapshot(diagnostics);
        var root = json.RootElement;
        root.EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(new[]
        {
            "schemaVersion", "kind", "theme", "viewport", "route", "gotoOutcome", "domContentLoadedMilliseconds", "loadMilliseconds",
            "detachAttempted", "detachCompleted", "diagnosticIncomplete", "truncated", "droppedEvents", "requests"
        });
        root.GetProperty("schemaVersion").GetInt32().Should().Be(1);
        root.GetProperty("kind").GetString().Should().Be("PublicLoopbackNavigation");
        root.GetProperty("gotoOutcome").GetString().Should().Be("Succeeded");
        root.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeFalse();
        root.GetProperty("detachCompleted").GetBoolean().Should().BeTrue();
        root.GetProperty("domContentLoadedMilliseconds").GetInt64().Should().BeGreaterThanOrEqualTo(0);
        root.GetProperty("loadMilliseconds").GetInt64().Should().BeGreaterThanOrEqualTo(
            root.GetProperty("domContentLoadedMilliseconds").GetInt64());
        var rows = root.GetProperty("requests").EnumerateArray().ToArray();
        rows.Should().HaveCount(2);
        rows[0].EnumerateObject().Select(property => property.Name).Should().BeEquivalentTo(new[]
        {
            "path", "resourceType", "status", "state", "startedMilliseconds", "responseMilliseconds", "endMilliseconds"
        });
        rows[0].GetProperty("path").GetString().Should().Be("/login");
        rows[1].GetProperty("path").GetString().Should().Be("/app.css");
        rows.Select(row => row.GetProperty("state").GetString()).Should().OnlyContain(state => state == "Finished");
        rows[0].GetProperty("status").GetInt32().Should().Be(200);
        rows[1].GetProperty("status").GetInt32().Should().Be(304);
        foreach (var row in rows)
            row.GetProperty("endMilliseconds").GetInt64().Should().BeGreaterThanOrEqualTo(
                row.GetProperty("startedMilliseconds").GetInt64());
    }

    [Theory]
    [InlineData("http://127.0.0.1:58123/login?token=PRIVATE_QUERY_CANARY", "document")]
    [InlineData("http://127.0.0.1:58123/login#PRIVATE_FRAGMENT_CANARY", "document")]
    [InlineData("http://PRIVATE_USER_CANARY@127.0.0.1:58123/login", "document")]
    [InlineData("http://example.invalid:58123/login", "document")]
    [InlineData("http://127.0.0.1:58124/login", "document")]
    [InlineData("https://127.0.0.1:58123/login", "document")]
    [InlineData("http://localhost:58123/login", "document")]
    [InlineData("http://127.0.0.1:58123/api/PRIVATE_PATH_CANARY", "fetch")]
    [InlineData("http://127.0.0.1:58123/login", "script")]
    [InlineData("http://127.0.0.1:58123/unknown/../app.css", "stylesheet")]
    [InlineData("http://127.0.0.1:58123/%61pp.css", "stylesheet")]
    [InlineData("http://127.0.0.1:58123/unknown.0123456789.js", "script")]
    [InlineData("http://127.0.0.1:58123/app.PRIVATE_TOKEN.css", "stylesheet")]
    public void Rejected_input_never_enters_the_serialized_record(string url, string resourceType)
    {
        var diagnostics = Create();
        diagnostics.ObserveRequest(new object(), url, resourceType);
        var bytes = diagnostics.SerializeSnapshot();
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("requests").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("droppedEvents").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
        var text = Encoding.UTF8.GetString(bytes);
        text.Should().NotContain("PRIVATE_").And.NotContain("example.invalid").And.NotContain("58123");
    }

    [Theory]
    [InlineData("app.0123456789.css", "stylesheet", "/app.css")]
    [InlineData("Memory.Dashboard.a1b2c3d4e5.styles.css", "stylesheet", "/Memory.Dashboard.styles.css")]
    [InlineData("_framework/blazor.web.abcdefghij.js", "script", "/_framework/blazor.web.js")]
    public void Only_fixed_asset_names_accept_a_fixed_fingerprint_shape(string path, string type, string expected)
    {
        var diagnostics = Create();
        diagnostics.ObserveRequest(new object(), "http://127.0.0.1:58123/" + path, type);
        using var json = Snapshot(diagnostics);
        var record = json.RootElement.GetProperty("requests")[0];
        record.GetProperty("path").GetString().Should().Be(expected);
        Encoding.UTF8.GetString(diagnostics.SerializeSnapshot()).Should().NotContain(path);
    }

    [Theory]
    [InlineData(99)]
    [InlineData(600)]
    public void Invalid_status_is_dropped_without_erasing_a_later_valid_response(int invalidStatus)
    {
        var diagnostics = Create();
        var key = new object();
        diagnostics.ObserveRequest(key, "http://127.0.0.1:58123/login", "document");
        diagnostics.ObserveResponse(key, invalidStatus);
        diagnostics.ObserveResponse(key, 503);
        diagnostics.ObserveEnd(key, failed: true);
        diagnostics.Detach(gotoSucceeded: false);
        using var json = Snapshot(diagnostics);
        var record = json.RootElement.GetProperty("requests")[0];
        record.GetProperty("status").GetInt32().Should().Be(503);
        record.GetProperty("state").GetString().Should().Be("Failed");
        json.RootElement.GetProperty("gotoOutcome").GetString().Should().Be("Failed");
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Unknown_and_duplicate_events_are_incomplete_and_do_not_overwrite_terminal_state()
    {
        var diagnostics = Create();
        var key = new object();
        diagnostics.ObserveRequest(key, "http://127.0.0.1:58123/login", "document");
        diagnostics.ObserveRequest(key, "http://127.0.0.1:58123/app.css", "stylesheet");
        diagnostics.ObserveResponse(key, 200);
        diagnostics.ObserveResponse(key, 201);
        diagnostics.ObserveEnd(key, failed: false);
        diagnostics.ObserveEnd(key, failed: true);
        diagnostics.ObserveResponse(new object(), 200);
        diagnostics.ObserveStage(isLoad: true);
        diagnostics.ObserveStage(isLoad: true);
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("requests").GetArrayLength().Should().Be(1);
        json.RootElement.GetProperty("requests")[0].GetProperty("state").GetString().Should().Be("Finished");
        json.RootElement.GetProperty("requests")[0].GetProperty("status").GetInt32().Should().Be(200);
        json.RootElement.GetProperty("droppedEvents").GetInt32().Should().Be(5);
    }

    [Fact]
    public void Collection_and_serialized_bytes_stay_bounded_after_overflow_and_later_bad_input()
    {
        var diagnostics = Create();
        for (var index = 0; index < DashboardNavigationDiagnostics.MaximumRequests + 100; index++)
            diagnostics.ObserveRequest(new object(), "http://127.0.0.1:58123/login", "document");
        diagnostics.ObserveRequest(new object(), "http://127.0.0.1:58123/PRIVATE_LATE_CANARY", "fetch");
        var bytes = diagnostics.SerializeSnapshot();
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("requests").GetArrayLength().Should().Be(32);
        json.RootElement.GetProperty("droppedEvents").GetInt32().Should().Be(101);
        json.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
        bytes.Length.Should().BeLessThanOrEqualTo(DashboardNavigationDiagnostics.MaximumJsonBytes);
        Encoding.UTF8.GetString(bytes).Should().NotContain("PRIVATE_LATE_CANARY");
    }

    [Fact]
    public void Request_identity_never_calls_custom_hash_or_string_members()
    {
        var diagnostics = Create();
        var key = new IdentityCanary();
        diagnostics.ObserveRequest(key, "http://127.0.0.1:58123/login", "document");
        diagnostics.ObserveResponse(key, 200);
        diagnostics.ObserveEnd(key, failed: false);
        key.MemberReads.Should().Be(0);
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("requests")[0].GetProperty("state").GetString().Should().Be("Finished");
    }

    [Fact]
    public void Partial_attach_rolls_back_and_does_not_attempt_later_subscriptions()
    {
        var diagnostics = Create();
        var attached = 0;
        var detached = 0;
        var later = 0;
        var canary = new MessageCanaryException();
        diagnostics.BindObserver(() => attached++, () => detached++);
        diagnostics.BindObserver(() => { attached++; throw canary; }, () => detached++);
        diagnostics.BindObserver(() => later++, () => detached++);
        attached.Should().Be(2);
        detached.Should().Be(2);
        later.Should().Be(0);
        canary.MessageReads.Should().Be(0);
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("detachCompleted").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Detach_failure_does_not_skip_later_detaches_or_admit_more_events()
    {
        var diagnostics = Create();
        var detaches = 0;
        diagnostics.BindObserver(() => { }, () => { detaches++; throw new IOException(); });
        diagnostics.BindObserver(() => { }, () => detaches++);
        Action detach = () => diagnostics.Detach(gotoSucceeded: false);
        detach.Should().NotThrow();
        diagnostics.Detach(gotoSucceeded: true);
        diagnostics.ObserveRequest(new object(), "http://127.0.0.1:58123/login", "document");
        diagnostics.ObserveStage(isLoad: true);
        detaches.Should().Be(2);
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("detachCompleted").GetBoolean().Should().BeFalse();
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
        json.RootElement.GetProperty("gotoOutcome").GetString().Should().Be("Failed");
        json.RootElement.GetProperty("loadMilliseconds").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("requests").GetArrayLength().Should().Be(0);
    }

    [Fact]
    public void Subscription_collection_is_bounded_and_excess_callbacks_are_not_called()
    {
        var diagnostics = Create();
        var subscriptions = 0;
        var detaches = 0;
        for (var index = 0; index < 7; index++)
            diagnostics.BindObserver(() => subscriptions++, () => detaches++);
        diagnostics.Detach();
        subscriptions.Should().Be(6);
        detaches.Should().Be(6);
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("truncated").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Actual_diagnostic_boundary_handles_serialization_and_io_failures_without_reading_messages()
    {
        var diagnostics = Create();
        var canary = new MessageCanaryException();
        var calls = 0;
        diagnostics.TryDiagnosticOperation(() => { calls++; throw canary; }).Should().BeFalse();
        diagnostics.TryDiagnosticOperation(() => { calls++; throw new JsonException(); }).Should().BeFalse();
        diagnostics.TryDiagnosticOperation(() => { calls++; throw new IOException(); }).Should().BeFalse();
        calls.Should().Be(3);
        canary.MessageReads.Should().Be(0);
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
        Encoding.UTF8.GetString(diagnostics.SerializeSnapshot()).Should().NotContain("PRIVATE_MESSAGE_CANARY");
    }

    [Fact]
    public void Fixed_repository_persistence_is_unique_bounded_read_back_and_not_repeated()
    {
        var first = Create();
        var second = Create();
        first.ArtifactPath.Should().NotBe(second.ArtifactPath);
        first.ObserveRequest(new object(), "http://127.0.0.1:58123/login", "document");
        first.Detach(gotoSucceeded: true);
        var expected = first.SerializeSnapshot();
        first.TryPersist().Should().BeTrue();
        first.PersistenceSucceeded.Should().BeTrue();
        var actual = File.ReadAllBytes(first.ArtifactPath);
        actual.Should().Equal(expected);
        actual.Length.Should().BeLessThanOrEqualTo(16 * 1024);
        first.TryPersist().Should().BeTrue();
        File.ReadAllBytes(first.ArtifactPath).Should().Equal(actual);
        Directory.GetFiles(first.ArtifactDirectory).Should().HaveCount(1);
        second.TryPersist().Should().BeTrue();
        using var json = JsonDocument.Parse(File.ReadAllBytes(second.ArtifactPath));
        json.RootElement.GetProperty("gotoOutcome").GetString().Should().Be("Unknown");
    }

    [Fact]
    public void Existing_own_artifact_is_not_overwritten_and_persistence_failure_stays_incomplete()
    {
        var diagnostics = Create();
        Directory.CreateDirectory(diagnostics.ArtifactDirectory);
        var original = "OWN_EXISTING_FILE_CANARY"u8.ToArray();
        File.WriteAllBytes(diagnostics.ArtifactPath, original);
        diagnostics.TryPersist().Should().BeFalse();
        diagnostics.TryPersist().Should().BeFalse();
        diagnostics.PersistenceSucceeded.Should().BeFalse();
        File.ReadAllBytes(diagnostics.ArtifactPath).Should().Equal(original);
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void Blocked_own_directory_does_not_throw_or_report_success()
    {
        var diagnostics = Create();
        Directory.CreateDirectory(Path.GetDirectoryName(diagnostics.ArtifactDirectory)!);
        File.WriteAllText(diagnostics.ArtifactDirectory, "OWN_DIRECTORY_BLOCKER");
        Action persist = () => diagnostics.TryPersist().Should().BeFalse();
        persist.Should().NotThrow();
        diagnostics.PersistenceSucceeded.Should().BeFalse();
        File.Exists(diagnostics.ArtifactPath).Should().BeFalse();
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
    }

    [Theory]
    [InlineData("https://127.0.0.1:58123/")]
    [InlineData("http://example.invalid:58123/")]
    [InlineData("http://127.0.0.1:58123/login")]
    [InlineData("http://127.0.0.1:58123/?PRIVATE_ORIGIN_CANARY")]
    public void Invalid_fixture_origin_is_rejected_before_any_observer_or_file_io(string origin)
    {
        Action construct = () => new DashboardNavigationDiagnostics(new Uri(origin));
        construct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Oversized_input_and_failed_request_without_a_response_stay_safe()
    {
        var diagnostics = Create();
        diagnostics.ObserveRequest(new object(), "http://127.0.0.1:58123/" + new string('x', 2048), "document");
        var key = new object();
        diagnostics.ObserveRequest(key, "http://127.0.0.1:58123/login", "document");
        diagnostics.ObserveEnd(key, failed: true);
        using var json = Snapshot(diagnostics);
        var row = json.RootElement.GetProperty("requests")[0];
        row.GetProperty("state").GetString().Should().Be("Failed");
        row.GetProperty("status").ValueKind.Should().Be(JsonValueKind.Null);
        json.RootElement.GetProperty("droppedEvents").GetInt32().Should().Be(1);
    }

    [Theory]
    [InlineData("dark", "desktop")]
    [InlineData("dark", "tab-s7-portrait")]
    [InlineData("dark", "mobile")]
    [InlineData("light", "desktop")]
    [InlineData("light", "tab-s7-portrait")]
    [InlineData("light", "mobile")]
    public void Six_existing_public_boundary_cases_have_exact_closed_labels(string theme, string viewport)
    {
        var diagnostics = new DashboardNavigationDiagnostics(new Uri("http://127.0.0.1:58123/"), theme, viewport, "/login");
        using var json = Snapshot(diagnostics);
        json.RootElement.GetProperty("theme").GetString().Should().Be(theme);
        json.RootElement.GetProperty("viewport").GetString().Should().Be(viewport);
        json.RootElement.GetProperty("route").GetString().Should().Be("/login");
    }

    [Theory]
    [InlineData("PRIVATE_THEME_CANARY", "desktop", "/login")]
    [InlineData("dark", "PRIVATE_VIEWPORT_CANARY", "/login")]
    [InlineData("dark", "desktop", "/login?PRIVATE_ROUTE_CANARY")]
    [InlineData("dark", "desktop", "/login#PRIVATE_ROUTE_CANARY")]
    public void Unknown_case_labels_are_rejected_before_observation_or_persistence(string theme, string viewport, string route)
    {
        Action construct = () => new DashboardNavigationDiagnostics(new Uri("http://127.0.0.1:58123/"), theme, viewport, route);
        construct.Should().Throw<ArgumentException>().WithMessage("NAVIGATION_DIAGNOSTIC_CASE_REJECTED");
    }

    [Theory]
    [InlineData("Release")]
    [InlineData("Debug")]
    public void Only_the_same_test_assembly_in_the_fixed_bin_layout_is_structurally_eligible(string configuration)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var output = Path.Combine(repo, "tests", "Memory.DashboardTests", "bin", configuration, "net10.0");
        DashboardNavigationDiagnostics.HasExpectedOutputLayout(output, Path.Combine(output, "Memory.DashboardTests.dll"))
            .Should().BeTrue();
        DashboardNavigationDiagnostics.HasExpectedOutputLayout(output, Path.Combine(output, "Other.dll"))
            .Should().BeFalse();
        DashboardNavigationDiagnostics.HasExpectedOutputLayout(output, Path.Combine(repo, "Memory.DashboardTests.dll"))
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("other-tests", "Memory.DashboardTests", "Release", "net10.0")]
    [InlineData("tests", "OtherTests", "Release", "net10.0")]
    [InlineData("tests", "Memory.DashboardTests", "Publish", "net10.0")]
    [InlineData("tests", "Memory.DashboardTests", "Release", "net9.0")]
    public void Unknown_output_layout_is_not_a_persistence_authority(string testsFolder, string project, string configuration, string framework)
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".."));
        var output = Path.Combine(repo, testsFolder, project, "bin", configuration, framework);
        DashboardNavigationDiagnostics.HasExpectedOutputLayout(output, Path.Combine(output, "Memory.DashboardTests.dll"))
            .Should().BeFalse();
        DashboardNavigationDiagnostics.HasExpectedOutputLayout("relative", "Memory.DashboardTests.dll")
            .Should().BeFalse();
    }

    [Theory]
    [InlineData("/Components/Layout/ReconnectModal.razor.js")]
    [InlineData("/Components/Layout/ReconnectModal.a1b2c3d4e5.razor.js")]
    public void Existing_reconnect_module_has_an_exact_safe_identity(string path)
    {
        var diagnostics = Create();
        var key = new object();
        diagnostics.ObserveRequest(key, "http://127.0.0.1:58123" + path, "script");
        diagnostics.ObserveResponse(key, 200);
        diagnostics.ObserveEnd(key, failed: false);
        using var json = Snapshot(diagnostics);
        var rows = json.RootElement.GetProperty("requests");
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("path").GetString().Should().Be("/Components/Layout/ReconnectModal.razor.js");
        rows[0].GetProperty("resourceType").GetString().Should().Be("script");
        rows[0].GetProperty("status").GetInt32().Should().Be(200);
        rows[0].GetProperty("state").GetString().Should().Be("Finished");
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeFalse();
        Encoding.UTF8.GetString(diagnostics.SerializeSnapshot()).Should().NotContain("a1b2c3d4e5");
    }

    [Theory]
    [InlineData("/Components/Layout/Unknown.razor.js", "script")]
    [InlineData("/Components/Layout/Unknown.razor.a1b2c3d4e5.js", "script")]
    [InlineData("/Components/Layout/ReconnectModal.razor.js", "stylesheet")]
    [InlineData("/Components/Layout/ReconnectModal.razor.js?token=PRIVATE_MODULE_CANARY", "script")]
    [InlineData("/Components/Layout/ReconnectModal.razor.js#PRIVATE_MODULE_CANARY", "script")]
    public void Reconnect_module_admission_does_not_expand_to_siblings_types_or_private_values(string path, string type)
    {
        var diagnostics = Create();
        diagnostics.ObserveRequest(new object(), "http://127.0.0.1:58123" + path, type);
        var bytes = diagnostics.SerializeSnapshot();
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("requests").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("droppedEvents").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
        Encoding.UTF8.GetString(bytes).Should().NotContain("Unknown.razor").And.NotContain("PRIVATE_MODULE_CANARY");
    }

    [Theory]
    [InlineData("/Memory.Dashboard.0123456789.styles.css", "stylesheet", "/Memory.Dashboard.styles.css")]
    [InlineData("/Components/Layout/ReconnectModal.0123456789.razor.js", "script", "/Components/Layout/ReconnectModal.razor.js")]
    public void Compiled_compound_extension_shapes_preserve_only_the_predefined_asset_identity(string path, string type, string expected)
    {
        var diagnostics = Create();
        var key = new object();
        diagnostics.ObserveRequest(key, "http://127.0.0.1:58123" + path, type);
        diagnostics.ObserveResponse(key, 200);
        diagnostics.ObserveEnd(key, failed: false);
        using var json = Snapshot(diagnostics);
        var rows = json.RootElement.GetProperty("requests");
        rows.GetArrayLength().Should().Be(1);
        rows[0].GetProperty("path").GetString().Should().Be(expected);
        rows[0].GetProperty("resourceType").GetString().Should().Be(type);
        rows[0].GetProperty("status").GetInt32().Should().Be(200);
        rows[0].GetProperty("state").GetString().Should().Be("Finished");
        json.RootElement.GetProperty("droppedEvents").GetInt32().Should().Be(0);
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeFalse();
        Encoding.UTF8.GetString(diagnostics.SerializeSnapshot()).Should().NotContain("0123456789");
    }

    [Theory]
    [InlineData("/Memory.Dashboard.styles.0123456789.css", "stylesheet")]
    [InlineData("/Components/Layout/ReconnectModal.razor.0123456789.js", "script")]
    [InlineData("/Unknown.0123456789.styles.css", "stylesheet")]
    [InlineData("/Components/Layout/Unknown.0123456789.razor.js", "script")]
    [InlineData("/Memory.Dashboard.0123456789.styles.css", "script")]
    [InlineData("/Components/Layout/ReconnectModal.0123456789.razor.js", "stylesheet")]
    [InlineData("/Memory.Dashboard.0123456789.styles.css?token=PRIVATE_COMPOUND_CANARY", "stylesheet")]
    [InlineData("/Components/Layout/ReconnectModal.0123456789.razor.js?token=PRIVATE_COMPOUND_CANARY", "script")]
    [InlineData("/Memory.Dashboard.0123456789.styles.css#PRIVATE_COMPOUND_CANARY", "stylesheet")]
    [InlineData("/Components/Layout/ReconnectModal.0123456789.razor.js#PRIVATE_COMPOUND_CANARY", "script")]
    public void Compound_extension_admission_rejects_old_position_unknown_siblings_types_and_private_values(string path, string type)
    {
        var diagnostics = Create();
        diagnostics.ObserveRequest(new object(), "http://127.0.0.1:58123" + path, type);
        var bytes = diagnostics.SerializeSnapshot();
        using var json = JsonDocument.Parse(bytes);
        json.RootElement.GetProperty("requests").GetArrayLength().Should().Be(0);
        json.RootElement.GetProperty("droppedEvents").GetInt32().Should().Be(1);
        json.RootElement.GetProperty("diagnosticIncomplete").GetBoolean().Should().BeTrue();
        Encoding.UTF8.GetString(bytes).Should().NotContain("PRIVATE_COMPOUND_CANARY")
            .And.NotContain("0123456789").And.NotContain("/Unknown.").And.NotContain("/Components/Layout/Unknown.");
    }

    private sealed class IdentityCanary
    {
        internal int MemberReads { get; private set; }
        public override int GetHashCode() { MemberReads++; throw new InvalidOperationException(); }
        public override string ToString() { MemberReads++; throw new InvalidOperationException(); }
    }

    private sealed class MessageCanaryException : Exception
    {
        internal int MessageReads { get; private set; }
        public override string Message { get { MessageReads++; return "PRIVATE_MESSAGE_CANARY"; } }
    }
}

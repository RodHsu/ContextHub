using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Services;
using Memory.Dashboard.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memory.DashboardTests;

public sealed class CanonicalTagQualityTests
{
    [Theory]
    [InlineData(7)]
    [InlineData(30)]
    [InlineData(90)]
    public async Task Client_should_use_read_only_scoped_quality_window(int days)
    {
        using var handler = new QualityHandler(HttpStatusCode.OK, [Row()]);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://context-hub.example.test") };
        var result = await new ContextHubApiClient(http).GetCanonicalTagQualityAsync("Team/One", days, CancellationToken.None);
        handler.Method.Should().Be(HttpMethod.Get);
        handler.Uri!.OriginalString.Should().Contain("/api/canonical-tags/Team%2FOne/quality");
        handler.Uri.Query.Should().Contain($"days={days}").And.Contain("minimumSamples=20");
        result.Should().ContainSingle().Which.DefinitionId.Should().Be(Row().DefinitionId);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task Missing_surface_should_remain_unknown(HttpStatusCode status)
    {
        using var http = new HttpClient(new QualityHandler(status)) { BaseAddress = new Uri("https://context-hub.example.test") };
        (await new ContextHubApiClient(http).GetCanonicalTagQualityAsync("p", 7, CancellationToken.None)).Should().BeNull();
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.ServiceUnavailable)]
    public async Task Permission_and_service_failures_should_not_become_empty_or_unsupported(HttpStatusCode status)
    {
        using var http = new HttpClient(new QualityHandler(status)) { BaseAddress = new Uri("https://context-hub.example.test") };
        var action = () => new ContextHubApiClient(http).GetCanonicalTagQualityAsync("p", 7, CancellationToken.None);
        await action.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Client_should_reject_missing_scope_and_unapproved_window_before_request()
    {
        using var handler = new QualityHandler(HttpStatusCode.OK, []);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://context-hub.example.test") };
        var client = new ContextHubApiClient(http);
        var noScope = () => client.GetCanonicalTagQualityAsync(" ", 7, CancellationToken.None);
        var wrongWindow = () => client.GetCanonicalTagQualityAsync("p", 14, CancellationToken.None);
        await noScope.Should().ThrowAsync<ArgumentException>();
        await wrongWindow.Should().ThrowAsync<ArgumentOutOfRangeException>();
        handler.Uri.Should().BeNull();
    }

    [Fact]
    public async Task Scope_or_window_change_should_cancel_old_request_and_ignore_late_success()
    {
        using var state = new CanonicalTagQualityState();
        var old = new TaskCompletionSource<IReadOnlyList<CanonicalTagQualityWindow>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default;
        var first = state.LoadAsync("old-project", 7, (_, _, token) => { oldToken = token; return old.Task; });
        state.Loading.Should().BeTrue();
        await state.LoadAsync("new-project", 90, (_, days, _) =>
        {
            days.Should().Be(90);
            return Task.FromResult<IReadOnlyList<CanonicalTagQualityWindow>?>([]);
        });
        oldToken.IsCancellationRequested.Should().BeTrue();
        old.SetResult([Row()]);
        await first;
        state.Rows.Should().BeEmpty();
        state.Loading.Should().BeFalse();
        state.Error.Should().BeNull();
    }

    [Fact]
    public async Task Clearing_scope_should_clear_data_cancel_request_and_ignore_late_failure()
    {
        using var state = new CanonicalTagQualityState();
        await state.LoadAsync("p", 7, (_, _, _) => Task.FromResult<IReadOnlyList<CanonicalTagQualityWindow>?>([Row()]));
        var old = new TaskCompletionSource<IReadOnlyList<CanonicalTagQualityWindow>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = state.LoadAsync("p", 30, (_, _, _) => old.Task);
        state.Rows.Should().BeEmpty("previous scope/window data cannot appear during loading");
        var called = false;
        await state.LoadAsync("", 30, (_, _, _) => { called = true; return Task.FromResult<IReadOnlyList<CanonicalTagQualityWindow>?>([Row()]); });
        old.SetException(new InvalidOperationException("private diagnostic detail"));
        await first;
        called.Should().BeFalse();
        state.Rows.Should().BeEmpty();
        state.Error.Should().BeNull();
        state.Loading.Should().BeFalse();
    }

    [Fact]
    public async Task Current_errors_should_be_safe_and_unsupported_should_be_distinct_from_empty()
    {
        using var state = new CanonicalTagQualityState();
        await state.LoadAsync("p", 7, (_, _, _) => Task.FromException<IReadOnlyList<CanonicalTagQualityWindow>?>(new Exception("private diagnostic detail")));
        state.Error.Should().NotBeNull().And.NotContain("private diagnostic detail");
        state.Rows.Should().BeEmpty();
        await state.LoadAsync("p", 7, (_, _, _) => Task.FromResult<IReadOnlyList<CanonicalTagQualityWindow>?>(null));
        state.Unsupported.Should().BeTrue();
        state.Error.Should().BeNull();
        await state.LoadAsync("p", 7, (_, _, _) => Task.FromResult<IReadOnlyList<CanonicalTagQualityWindow>?>([]));
        state.Unsupported.Should().BeFalse();
    }

    [Fact]
    public async Task Disposal_should_cancel_active_read_and_reject_late_results()
    {
        var state = new CanonicalTagQualityState();
        var result = new TaskCompletionSource<IReadOnlyList<CanonicalTagQualityWindow>?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var pending = state.LoadAsync("p", 7, (_, _, cancellation) => { token = cancellation; return result.Task; });
        state.Dispose();
        token.IsCancellationRequested.Should().BeTrue();
        result.SetResult([Row()]);
        await pending;
        state.Rows.Should().BeEmpty();
        state.Loading.Should().BeFalse();
    }

    [Fact]
    public void No_samples_should_be_unknown_instead_of_zero_or_healthy()
    {
        CanonicalTagQualityState.Rate(0, 0).Should().Contain("未知");
        CanonicalTagQualityState.Rate(.5m, 20).Should().Contain("50");
    }

    [Fact]
    public async Task Rendered_panel_should_keep_read_only_controls_and_accessible_window_and_table()
    {
        var html = await RenderAsync(HttpStatusCode.OK, [Row()]);
        html.Should().Contain("value=\"7\"").And.Contain("value=\"30\"").And.Contain("value=\"90\"");
        html.Should().Contain("scope=\"col\"").And.Contain("scope=\"row\"");
        html.Should().MatchRegex(@"<caption(?:\s[^>]*)?>");
        html.Should().Contain("b132049f-91a9-4d5f-bc66-05563d34ca95");
        html.Should().NotContain("merge-preview").And.NotContain("onclick=\"merge");
    }

    [Fact]
    public async Task Rendered_empty_unsupported_and_error_states_should_be_distinct()
    {
        var empty = await RenderAsync(HttpStatusCode.OK, []);
        var unsupported = await RenderAsync(HttpStatusCode.NotFound, null);
        var denied = await RenderAsync(HttpStatusCode.Forbidden, null);
        // HTML encodes localized text, so assert the semantic state and visible row absence.
        empty.Should().Contain("role=\"status\"").And.NotContain("<tbody>");
        unsupported.Should().Contain("role=\"status\"").And.NotBe(empty);
        denied.Should().Contain("role=\"alert\"").And.NotContain("<tbody>");
    }

    private static async Task<string> RenderAsync(HttpStatusCode status, IReadOnlyList<CanonicalTagQualityWindow>? rows)
    {
        using var http = new HttpClient(new QualityHandler(status, rows)) { BaseAddress = new Uri("https://context-hub.example.test") };
        using var services = new ServiceCollection().AddLogging()
            .AddSingleton<IContextHubApiClient>(new ContextHubApiClient(http)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var output = await renderer.RenderComponentAsync<CanonicalTagQualityPanel>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(CanonicalTagQualityPanel.ProjectId)] = "p" }));
            return output.ToHtmlString();
        });
    }

    private static CanonicalTagQualityWindow Row() => new(Guid.Parse("b132049f-91a9-4d5f-bc66-05563d34ca95"), 7, 20, 0, 10, 0, 0, .5m, 0, 0, .5m, [], true);

    private sealed class QualityHandler(HttpStatusCode status, IReadOnlyList<CanonicalTagQualityWindow>? payload = null) : HttpMessageHandler
    {
        public HttpMethod? Method { get; private set; }
        public Uri? Uri { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Method = request.Method;
            Uri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(status) { Content = payload is null ? new StringContent(string.Empty) : JsonContent.Create(payload) });
        }
    }
}

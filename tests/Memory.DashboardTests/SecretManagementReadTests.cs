using System.Reflection;
using System.Net;
using System.Text;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Services;
using Memory.Domain;
using Memory.Dashboard.Components.Shared;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memory.DashboardTests;

public sealed class SecretManagementReadTests
{
    [Fact]
    public void Binding_scope_never_prefetches_management_data()
    {
        var calls = 0;
        using var state = new SecretManagementReadState(Api((_, _) => { calls++; throw new InvalidOperationException(); }));
        state.Bind("p", Guid.NewGuid(), 1);
        calls.Should().Be(0); state.Requested.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scope_change_cancels_and_ignores_late_management_success_or_error(bool error)
    {
        var old = new TaskCompletionSource<SecretManagementExpiry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken oldToken = default; var id = Guid.NewGuid();
        using var state = new SecretManagementReadState(Api((_, args) => { oldToken = (CancellationToken)args[^1]!; return old.Task; }));
        state.Bind("old", id, 1); var load = state.LoadAsync(SecretManagementTab.Expiry);
        state.Bind("new", id, 1); oldToken.IsCancellationRequested.Should().BeTrue();
        if (error) old.SetException(new HttpRequestException("private")); else old.SetResult(new(id, 1, 1, SecretVersionState.Active, null));
        await load;
        state.ProjectId.Should().Be("new"); state.Expiry.Should().BeNull(); state.Error.Should().BeNull(); state.Requested.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Selection_or_revision_change_clears_prior_projection(bool revisionOnly)
    {
        var id = Guid.NewGuid();
        using var state = new SecretManagementReadState(Api((_, _) => Task.FromResult<SecretManagementExpiry?>(new(id, 1, 1, SecretVersionState.Active, null))));
        state.Bind("p", id, 1); await state.LoadAsync(SecretManagementTab.Expiry);
        state.Expiry.Should().NotBeNull();
        state.Bind("p", revisionOnly ? id : Guid.NewGuid(), revisionOnly ? 2 : 1);
        state.Expiry.Should().BeNull(); state.Requested.Should().BeFalse();
    }

    [Fact]
    public async Task New_tab_cancels_old_result_and_preserves_current_loading_state()
    {
        var id = Guid.NewGuid();
        var old = new TaskCompletionSource<SecretManagementExpiry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<SecretManagementHistoryPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var state = new SecretManagementReadState(Api((method, _) => method == nameof(IContextHubApiClient.GetSecretManagementExpiryAsync) ? old.Task : current.Task));
        state.Bind("p", id, 1); var first = state.LoadAsync(SecretManagementTab.Expiry); var second = state.LoadAsync(SecretManagementTab.History);
        old.SetResult(new(id, 1, 1, SecretVersionState.Active, null)); await first;
        state.Loading.Should().BeTrue(); state.Expiry.Should().BeNull();
        current.SetResult(new(id, [], 25, 0, false)); await second;
        state.History.Should().NotBeNull(); state.Loading.Should().BeFalse();
    }

    [Theory]
    [InlineData(SecretManagementTab.Expiry)]
    [InlineData(SecretManagementTab.Leases)]
    [InlineData(SecretManagementTab.History)]
    public async Task Mismatched_secret_results_fail_closed(SecretManagementTab tab)
    {
        var wrong = Guid.NewGuid();
        using var state = new SecretManagementReadState(Api((method, _) => method switch
        {
            nameof(IContextHubApiClient.GetSecretManagementExpiryAsync) => Task.FromResult<SecretManagementExpiry?>(new(wrong, 1, null, null, null)),
            nameof(IContextHubApiClient.GetSecretManagementLeasesAsync) => Task.FromResult<SecretManagementLeasePage?>(new(wrong, [], 25, 0, false)),
            _ => Task.FromResult<SecretManagementHistoryPage?>(new(wrong, [], 25, 0, false))
        }));
        state.Bind("p", Guid.NewGuid(), 1); await state.LoadAsync(tab);
        state.Expiry.Should().BeNull(); state.Leases.Should().BeNull(); state.History.Should().BeNull(); state.Error.Should().NotBeNull();
    }

    [Fact]
    public async Task Dispose_blocks_late_success_and_further_reads()
    {
        var calls = 0; var id = Guid.NewGuid();
        var gate = new TaskCompletionSource<SecretManagementExpiry?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var state = new SecretManagementReadState(Api((_, _) => { calls++; return gate.Task; }));
        state.Bind("p", id, 1); var load = state.LoadAsync(SecretManagementTab.Expiry); state.Dispose();
        gate.SetResult(new(id, 1, 1, SecretVersionState.Active, null)); await load; await state.LoadAsync(SecretManagementTab.Leases);
        calls.Should().Be(1); state.Expiry.Should().BeNull(); state.SecretId.Should().BeEmpty();
    }

    [Fact]
    public async Task Current_error_is_generic_and_paging_is_explicit()
    {
        var id = Guid.NewGuid(); var offset = -1;
        using var state = new SecretManagementReadState(Api((_, args) =>
        {
            offset = (int)args[3]!;
            return Task.FromException<SecretManagementLeasePage?>(new HttpRequestException("private-material-location"));
        }));
        state.Bind("p", id, 1); await state.LoadAsync(SecretManagementTab.Leases, 25);
        offset.Should().Be(25); state.Error.Should().NotContain("private"); state.Loading.Should().BeFalse();
    }

    [Fact]
    public async Task Http_client_encodes_scope_and_preserves_management_only_routes()
    {
        var handler = new Handler(); var client = new ContextHubApiClient(new HttpClient(handler) { BaseAddress = new("https://synthetic.invalid") });
        var id = Guid.NewGuid();
        await client.GetSecretManagementExpiryAsync("p & q", id, default);
        await client.GetSecretManagementLeasesAsync("p & q", id, 25, 50, default);
        await client.GetSecretManagementHistoryAsync("p & q", id, 25, 75, default);
        handler.Urls.Should().HaveCount(3);
        handler.Urls[0].Should().Contain($"/{id:D}/management/expiry").And.Contain("projectId=p%20%26%20q");
        handler.Urls[1].Should().Contain("limit=25").And.Contain("offset=50");
        handler.Urls[2].Should().Contain("offset=75");
    }

    [Fact]
    public async Task Rendered_management_panel_has_explicit_tabs_without_prefetch_or_capability_controls()
    {
        var calls = 0;
        var api = Api((_, _) => { calls++; throw new InvalidOperationException(); });
        using var services = new ServiceCollection().AddLogging().AddSingleton(api).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<SecretManagementPanel>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(SecretManagementPanel.ProjectId)] = "p",
                [nameof(SecretManagementPanel.SecretId)] = Guid.NewGuid(),
                [nameof(SecretManagementPanel.Revision)] = 1L
            }))).ToHtmlString()));
        calls.Should().Be(0);
        html.Should().Contain("版本期限").And.Contain("租約狀態").And.Contain("使用歷史").And.Contain("僅既有管理讀者");
        html.Should().NotContain("Capability").And.NotContain("Reveal").And.NotContain("Revoke").And.NotContain("hidden");
    }

    private static IContextHubApiClient Api(Func<string, object?[], object> handler)
    {
        var api = DispatchProxy.Create<IContextHubApiClient, Proxy>(); ((Proxy)(object)api).Handler = handler; return api;
    }
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[], object> Handler { get; set; } = null!;
        protected override object Invoke(MethodInfo? method, object?[]? args) => Handler(method!.Name, args!);
    }
    private sealed class Handler : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { Content = new StringContent("", Encoding.UTF8) });
        }
    }
}

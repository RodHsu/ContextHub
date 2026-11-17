using System.Reflection;
using System.Net;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Components.Pages;
using Memory.Dashboard.Services;
using Memory.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.DashboardTests;

public sealed class SecretMetadataReadTests
{
    [Fact]
    public async Task Actual_scope_handler_must_not_accept_old_inventory_after_new_project_completes()
    {
        var late = new TaskCompletionSource<IReadOnlyList<SecretSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var old = Row("old"); var current = Row("current");
        var api = Api((method, args) => method switch
        {
            nameof(IContextHubApiClient.GetSecretsAsync) => (string)args[0]! == "old" ? late.Task : Task.FromResult<IReadOnlyList<SecretSummary>>([current]),
            nameof(IContextHubApiClient.GetEffectiveRightsAsync) => Task.FromResult(Rights((string)args[0]!)),
            nameof(IContextHubApiClient.GetStepUpRequirementAsync) => Task.FromResult(Requirement()),
            _ => null
        });
        var page = new Secrets();
        typeof(Secrets).GetProperty("ApiClient", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, api);
        var first = Call(page, "ChangeProjectAsync", "old");
        await Call(page, "ChangeProjectAsync", "current");
        late.SetResult([old]);
        await first;
        Value<IReadOnlyList<SecretSummary>>(page, "_secrets").Should().ContainSingle().Which.Id.Should().Be(current.Id);
        Value<SecretSummary>(page, "_selected").ProjectId.Should().Be("current");
        await page.DisposeAsync();
    }

    [Fact]
    public async Task Starting_new_scope_clears_metadata_selection_rights_requirements_and_cancels_both_old_requests()
    {
        var old = Row("old"); var current = Row("current");
        var pending = new TaskCompletionSource<IReadOnlyList<SecretSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken inventory = default, selection = default;
        var api = Api((method, args) => method switch
        {
            nameof(IContextHubApiClient.GetSecretsAsync) => (string)args[0]! == "current" ? pending.Task : Task.FromResult<IReadOnlyList<SecretSummary>>([old]),
            nameof(IContextHubApiClient.GetEffectiveRightsAsync) => Task.FromResult(Rights((string)args[0]!)),
            nameof(IContextHubApiClient.GetStepUpRequirementAsync) => Task.FromResult(Requirement()),
            _ => null
        });
        ((MetadataApiProxy)(object)api).Observe = (method, args) =>
        {
            if (method == nameof(IContextHubApiClient.GetSecretsAsync) && (string)args[0]! == "old") inventory = (CancellationToken)args[^1]!;
            if (method == nameof(IContextHubApiClient.GetEffectiveRightsAsync)) selection = (CancellationToken)args[^1]!;
        };
        using var state = new SecretMetadataReadState(api);
        await state.LoadAsync("old");
        state.Rights.Should().NotBeNull(); state.RevealRequirement.Should().NotBeNull(); state.RevokeRequirement.Should().NotBeNull();
        var next = state.LoadAsync("current");
        inventory.IsCancellationRequested.Should().BeTrue(); selection.IsCancellationRequested.Should().BeTrue();
        state.Secrets.Should().BeEmpty(); state.Selected.Should().BeNull(); state.Rights.Should().BeNull();
        state.RevealRequirement.Should().BeNull(); state.RevokeRequirement.Should().BeNull();
        state.LastUpdatedAt.Should().BeNull(); state.Loading.Should().BeTrue();
        pending.SetResult([current]); await next;
        state.Selected!.Id.Should().Be(current.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_inventory_success_or_error_cannot_finish_the_current_loading_state(bool error)
    {
        var old = new TaskCompletionSource<IReadOnlyList<SecretSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var current = new TaskCompletionSource<IReadOnlyList<SecretSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = Api((method, args) => method == nameof(IContextHubApiClient.GetSecretsAsync)
            ? (string)args[0]! == "old" ? old.Task : current.Task : null);
        using var state = new SecretMetadataReadState(api);
        var first = state.LoadAsync("old"); var next = state.LoadAsync("current");
        if (error) old.SetException(new HttpRequestException("synthetic-private-error")); else old.SetResult([Row("old")]);
        await first;
        state.ProjectId.Should().Be("current"); state.Secrets.Should().BeEmpty(); state.Error.Should().BeNull(); state.Loading.Should().BeTrue();
        current.SetResult([]); await next;
        state.Loading.Should().BeFalse(); state.Error.Should().BeNull();
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    public async Task Every_selection_await_rejects_late_success_error_and_follow_up_calls(int stage, bool error)
    {
        var first = Row("p", "first"); var second = Row("p", "second");
        var rightsGate = new TaskCompletionSource<EffectiveRightsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var requirementGate = new TaskCompletionSource<StepUpAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = false; CancellationToken oldToken = default;
        var firstCalls = 0;
        var api = Api((method, args) =>
        {
            if (method == nameof(IContextHubApiClient.GetSecretsAsync)) return Task.FromResult<IReadOnlyList<SecretSummary>>([first, second]);
            var resource = (string)args[3]!;
            if (resource == first.Id.ToString("D")) firstCalls++;
            var atGate = method == nameof(IContextHubApiClient.GetEffectiveRightsAsync) ? stage == 0
                : (StepUpOperationClass)args[0]! == StepUpOperationClass.SecretReveal ? stage == 1 : stage == 2;
            if (delay && resource == first.Id.ToString("D") && atGate)
            {
                oldToken = (CancellationToken)args[^1]!;
                return stage == 0 ? (object)rightsGate.Task : requirementGate.Task;
            }
            return method == nameof(IContextHubApiClient.GetEffectiveRightsAsync)
                ? (object)Task.FromResult(Rights((string)args[0]!)) : Task.FromResult(Requirement(resource));
        });
        using var state = new SecretMetadataReadState(api);
        await state.LoadAsync("p"); delay = true;
        var old = state.SelectAsync(first.Id);
        state.Rights.Should().BeNull(); state.RevealRequirement.Should().BeNull(); state.RevokeRequirement.Should().BeNull();
        await state.SelectAsync(second.Id);
        var callsBeforeRelease = firstCalls;
        oldToken.IsCancellationRequested.Should().BeTrue();
        if (stage == 0)
        {
            if (error) rightsGate.SetException(new HttpRequestException("private")); else rightsGate.SetResult(Rights("p"));
        }
        else
        {
            if (error) requirementGate.SetException(new HttpRequestException("private")); else requirementGate.SetResult(Requirement(first.Id.ToString("D")));
        }
        await old;
        state.Selected!.Id.Should().Be(second.Id);
        state.Rights.Should().NotBeNull();
        state.RevealRequirement!.ReasonCode.Should().Be(second.Id.ToString("D"));
        state.RevokeRequirement!.ReasonCode.Should().Be(second.Id.ToString("D"));
        state.SelectionError.Should().BeNull(); state.SelectionLoading.Should().BeFalse();
        firstCalls.Should().Be(callsBeforeRelease, "an obsolete chain must not request another resource preview");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Inventory_denial_or_wrong_project_payload_is_an_error_not_empty_success(bool mismatch)
    {
        var api = Api((method, _) => method == nameof(IContextHubApiClient.GetSecretsAsync)
            ? mismatch ? Task.FromResult<IReadOnlyList<SecretSummary>>([Row("other")])
                : Task.FromException<IReadOnlyList<SecretSummary>>(new UnauthorizedAccessException("synthetic-private-value"))
            : throw new InvalidOperationException("No authority request is allowed for hidden or wrong-scope metadata."));
        using var state = new SecretMetadataReadState(api);
        await state.LoadAsync("p");
        state.Error.Should().NotBeNull().And.NotContain("synthetic-private-value");
        state.Secrets.Should().BeEmpty(); state.Selected.Should().BeNull(); state.Loading.Should().BeFalse();
    }

    [Fact]
    public async Task Invalid_or_stale_selection_cannot_replace_current_selection_or_call_the_api()
    {
        var row = Row("p"); var calls = 0;
        var api = Api((method, args) =>
        {
            calls++;
            return method switch
            {
                nameof(IContextHubApiClient.GetSecretsAsync) => Task.FromResult<IReadOnlyList<SecretSummary>>([row]),
                nameof(IContextHubApiClient.GetEffectiveRightsAsync) => Task.FromResult(Rights((string)args[0]!)),
                _ => Task.FromResult(Requirement())
            };
        });
        using var state = new SecretMetadataReadState(api);
        await state.LoadAsync("p"); var before = calls;
        await state.SelectAsync(Guid.NewGuid());
        calls.Should().Be(before); state.Selected!.Id.Should().Be(row.Id); state.Rights.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Dispose_cancels_and_rejects_pending_inventory_success_or_failure(bool error)
    {
        var gate = new TaskCompletionSource<IReadOnlyList<SecretSummary>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var api = Api((_, args) => { token = (CancellationToken)args[^1]!; return gate.Task; });
        var state = new SecretMetadataReadState(api);
        var load = state.LoadAsync("p"); state.Dispose();
        token.IsCancellationRequested.Should().BeTrue();
        if (error) gate.SetException(new HttpRequestException("private")); else gate.SetResult([Row("p")]);
        await load;
        state.Secrets.Should().BeEmpty(); state.Selected.Should().BeNull(); state.Rights.Should().BeNull();
        state.Loading.Should().BeFalse(); state.Error.Should().BeNull(); state.LastUpdatedAt.Should().BeNull();
        await FluentActions.Awaiting(() => state.LoadAsync("p")).Should().ThrowAsync<ObjectDisposedException>();
    }

    [Fact]
    public async Task Blank_scope_clears_state_without_requesting_a_default_or_all_project_scope()
    {
        var calls = 0;
        using var state = new SecretMetadataReadState(Api((_, _) => { calls++; throw new InvalidOperationException(); }));
        await state.LoadAsync(" ");
        calls.Should().Be(0); state.ProjectId.Should().BeEmpty(); state.Secrets.Should().BeEmpty();
        state.Loading.Should().BeFalse(); state.Error.Should().BeNull();
    }

    [Fact]
    public async Task Scope_switch_rejects_pending_old_assurance_and_does_not_request_old_revoke()
    {
        var old = Row("old"); var current = Row("current");
        var gate = new TaskCompletionSource<StepUpAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var oldRevokeCalls = 0; CancellationToken token = default;
        var api = Api((method, args) =>
        {
            if (method == nameof(IContextHubApiClient.GetSecretsAsync)) return Task.FromResult<IReadOnlyList<SecretSummary>>([(string)args[0]! == "old" ? old : current]);
            if (method == nameof(IContextHubApiClient.GetEffectiveRightsAsync)) return Task.FromResult(Rights((string)args[0]!));
            var id = (string)args[3]!;
            if (id == old.Id.ToString("D") && (StepUpOperationClass)args[0]! == StepUpOperationClass.SecretReveal)
            { token = (CancellationToken)args[^1]!; return gate.Task; }
            if (id == old.Id.ToString("D")) oldRevokeCalls++;
            return Task.FromResult(Requirement(id));
        });
        using var state = new SecretMetadataReadState(api);
        var pending = state.LoadAsync("old"); await state.LoadAsync("current");
        token.IsCancellationRequested.Should().BeTrue();
        gate.SetResult(Requirement(old.Id.ToString("D"))); await pending;
        state.Selected!.Id.Should().Be(current.Id); state.Rights!.ProjectId.Should().Be("current");
        state.RevealRequirement!.ReasonCode.Should().Be(current.Id.ToString("D")); oldRevokeCalls.Should().Be(0);
    }

    [Fact]
    public async Task Current_assurance_failure_publishes_no_partial_authority_and_dispose_rejects_late_selection()
    {
        var row = Row("p");
        var gate = new TaskCompletionSource<StepUpAuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = false; CancellationToken token = default;
        var api = Api((method, args) =>
        {
            if (method == nameof(IContextHubApiClient.GetSecretsAsync)) return Task.FromResult<IReadOnlyList<SecretSummary>>([row]);
            if (method == nameof(IContextHubApiClient.GetEffectiveRightsAsync)) return Task.FromResult(Rights("p"));
            if ((StepUpOperationClass)args[0]! == StepUpOperationClass.CredentialRevoke)
            {
                if (!delay) return Task.FromException<StepUpAuthorizationResult>(new HttpRequestException("synthetic-private-error"));
                token = (CancellationToken)args[^1]!; return gate.Task;
            }
            return Task.FromResult(Requirement());
        });
        var state = new SecretMetadataReadState(api);
        await state.LoadAsync("p");
        state.SelectionError.Should().NotBeNull().And.NotContain("synthetic-private-error");
        state.Rights.Should().BeNull(); state.RevealRequirement.Should().BeNull(); state.RevokeRequirement.Should().BeNull();
        delay = true; var pending = state.SelectAsync(row.Id); state.Dispose();
        token.IsCancellationRequested.Should().BeTrue(); gate.SetResult(Requirement()); await pending;
        state.Selected.Should().BeNull(); state.Rights.Should().BeNull(); state.RevealRequirement.Should().BeNull();
        state.SelectionError.Should().BeNull(); state.SelectionLoading.Should().BeFalse();
    }

    [Fact]
    public async Task Obsolete_selection_finally_cannot_finish_a_pending_new_selection()
    {
        var first = Row("p"); var second = Row("p");
        var oldGate = new TaskCompletionSource<EffectiveRightsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newGate = new TaskCompletionSource<EffectiveRightsResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delay = false;
        var api = Api((method, args) => method switch
        {
            nameof(IContextHubApiClient.GetSecretsAsync) => Task.FromResult<IReadOnlyList<SecretSummary>>([first, second]),
            nameof(IContextHubApiClient.GetEffectiveRightsAsync) => delay ? (string)args[3]! == first.Id.ToString("D") ? oldGate.Task : newGate.Task : Task.FromResult(Rights("p")),
            _ => Task.FromResult(Requirement())
        });
        using var state = new SecretMetadataReadState(api);
        await state.LoadAsync("p"); delay = true;
        var old = state.SelectAsync(first.Id); var current = state.SelectAsync(second.Id);
        oldGate.SetResult(Rights("p")); await old;
        state.SelectionLoading.Should().BeTrue(); state.Selected!.Id.Should().Be(second.Id); state.Rights.Should().BeNull();
        newGate.SetResult(Rights("p")); await current; state.SelectionLoading.Should().BeFalse();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposed_page_ignores_late_bootstrap_results_without_starting_metadata_reads(bool error)
    {
        var gate = new TaskCompletionSource<IReadOnlyList<ProjectInformationListItem>>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var api = Api((method, args) =>
        {
            method.Should().Be(nameof(IContextHubApiClient.GetProjectInformationProjectsAsync));
            token = (CancellationToken)args[^1]!; return gate.Task;
        });
        var page = new Secrets();
        Set(page, "ApiClient", api); Set(page, "RuntimeSettingsAccessor", await Settings());
        var init = Call(page, "OnInitializedAsync");
        await page.DisposeAsync(); token.IsCancellationRequested.Should().BeTrue();
        if (error) gate.SetException(new HttpRequestException("private")); else gate.SetResult([]);
        await init;
        await Call(page, "ChangeProjectAsync", "after-dispose");
        Value<IReadOnlyList<string>>(page, "_projectIds").Should().BeEmpty();
    }

    [Fact]
    public async Task Rendered_page_binds_real_project_and_keeps_both_actions_disabled_even_for_allowed_previews()
    {
        var row = Row("p"); var requests = new List<string>();
        var api = Api((method, args) =>
        {
            requests.Add(method);
            return method switch
            {
                nameof(IContextHubApiClient.GetSecretsAsync) => (string)args[0]! == "p" ? Task.FromResult<IReadOnlyList<SecretSummary>>([row]) : throw new InvalidOperationException("Wrong project"),
                nameof(IContextHubApiClient.GetProjectInformationProjectsAsync) => Task.FromResult<IReadOnlyList<ProjectInformationListItem>>([]),
                nameof(IContextHubApiClient.GetEffectiveRightsAsync) => Task.FromResult(Rights((string)args[0]!)),
                nameof(IContextHubApiClient.GetStepUpRequirementAsync) => Task.FromResult(new StepUpAuthorizationResult(StepUpRequirementOutcome.Allowed, AssuranceLevel.Aal2, "synthetic-only")),
                _ => throw new InvalidOperationException("Unexpected metadata request")
            };
        });
        using var services = new ServiceCollection().AddLogging().AddSingleton(api)
            .AddSingleton(await Settings()).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<Secrets>(ParameterView.Empty)).ToHtmlString()));
        html.Should().MatchRegex("<select[^>]*value=\"p\"");
        html.Should().Contain(row.Id.ToString("D")).And.NotContain("_projectId").And.NotContain("_searchText").And.NotContain("_loadError");
        html.Should().MatchRegex(@"<button[^>]*disabled[^>]*>\s*開始 Reveal");
        html.Should().MatchRegex(@"<button[^>]*disabled[^>]*>\s*撤銷 credential");
        requests.Should().HaveCount(5).And.OnlyContain(method => method.StartsWith("Get", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task Refresh_prefers_fresh_selected_row_only_in_same_scope_and_falls_back_when_missing(bool sameScope, bool exists)
    {
        var first = Row("p"); var selected = Row("p"); var refresh = false;
        var project = sameScope ? "p" : "other";
        var freshFirst = first with { ProjectId = project, Revision = 3 };
        var freshSelected = selected with { ProjectId = project, Revision = 3 };
        var authorityReads = 0;
        var api = Api((method, args) =>
        {
            if (method == nameof(IContextHubApiClient.GetSecretsAsync)) return Task.FromResult<IReadOnlyList<SecretSummary>>(
                !refresh ? [first, selected] : exists ? [freshFirst, freshSelected] : [freshFirst]);
            authorityReads++;
            return method == nameof(IContextHubApiClient.GetEffectiveRightsAsync) ? (object)Task.FromResult(Rights((string)args[0]!)) : Task.FromResult(Requirement((string)args[3]!));
        });
        using var state = new SecretMetadataReadState(api);
        await state.LoadAsync("p"); await state.SelectAsync(selected.Id); refresh = true;
        await state.LoadAsync(project);
        state.Selected!.Id.Should().Be(sameScope && exists ? selected.Id : first.Id);
        state.Selected.Revision.Should().Be(3);
        state.Rights!.ProjectId.Should().Be(project);
        state.RevealRequirement!.ReasonCode.Should().Be(state.Selected.Id.ToString("D"));
        authorityReads.Should().Be(9, "a refresh rechecks all authority instead of keeping old previews");
    }

    private static SecretSummary Row(string project, string name = "synthetic") => new(Guid.NewGuid(), project, name,
        SecretKind.ApiToken, SecretState.Active, 1, 2, DateTimeOffset.UnixEpoch);
    private static EffectiveRightsResult Rights(string project) => new(project, "synthetic-reader", new(1, 1, 1, 1), []);
    private static StepUpAuthorizationResult Requirement(string tag = "synthetic-disabled") => new(StepUpRequirementOutcome.Disabled, AssuranceLevel.Aal2, tag);
    private static void Set(Secrets page, string name, object value)
        => typeof(Secrets).GetProperty(name, BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(page, value);
    private static async Task<IDashboardRuntimeSettingsAccessor> Settings()
    {
        var settings = new FakeInstanceSettingsService(Options.Create(new DashboardOptions()));
        var snapshot = await settings.GetSnapshotAsync(CancellationToken.None);
        return new RuntimeSettings(snapshot with { Behavior = snapshot.Behavior with { DefaultProjectId = "p" } });
    }
    private sealed class RuntimeSettings(InstanceSettingsSnapshot snapshot) : IDashboardRuntimeSettingsAccessor
    {
        public Task<InstanceSettingsSnapshot> GetCurrentAsync(CancellationToken ct) => Task.FromResult(snapshot);
        public Task RefreshAsync(CancellationToken ct) => Task.CompletedTask;
    }
    private static Task Call(Secrets page, string method, params object[] args)
        => (Task)typeof(Secrets).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(page, args)!;
    private static T Value<T>(Secrets page, string name)
    {
        var flags = BindingFlags.NonPublic | BindingFlags.Instance;
        return (T)(typeof(Secrets).GetField(name, flags)?.GetValue(page) ?? typeof(Secrets).GetProperty(name, flags)!.GetValue(page))!;
    }
    private static IContextHubApiClient Api(Func<string, object?[], object?> handler)
    {
        var api = DispatchProxy.Create<IContextHubApiClient, MetadataApiProxy>();
        ((MetadataApiProxy)(object)api).Handler = handler;
        return api;
    }
    public class MetadataApiProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Handler { get; set; } = null!;
        public Action<string, object?[]>? Observe { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Observe?.Invoke(method!.Name, args!);
            return Handler(method!.Name, args!) ?? typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(method.ReturnType.GenericTypeArguments[0]).Invoke(null, [null]);
        }
    }
}

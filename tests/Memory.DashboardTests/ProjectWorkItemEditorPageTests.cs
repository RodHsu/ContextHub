using System.Reflection;
using System.Security.Claims;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Components.Pages;
using Memory.Dashboard.Services;
using Memory.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memory.DashboardTests;

public sealed class ProjectWorkItemEditorPageTests
{
    [Fact]
    public async Task Actual_page_dispatches_versioned_save_and_updates_its_list_from_the_bound_receipt()
    {
        var api = new Api(); using var services = Services(api);
        await using var renderer = Renderer(services); await renderer.MountAsync<PageHost>(Parameters(Auth()));
        await renderer.ChangeAsync("editor-title", "saved-draft"); await renderer.ClickAsync("editor-save");
        api.Command!.ExpectedRevision.Should().Be(7); api.Command.WorkItemId.Should().Be(api.First.Id);
        api.Command.Patch.Title.Should().Be("saved-draft"); api.LegacyWrites.Should().Be(0);
        renderer.Find($"work-item-select-{api.First.Id:N}").Text.Should().Contain("saved-draft");
    }

    [Fact]
    public async Task Same_scope_list_refresh_retains_draft_and_does_not_adopt_a_new_list_revision()
    {
        var api = new Api(); using var services = Services(api);
        await using var renderer = Renderer(services); await renderer.MountAsync<PageHost>(Parameters(Auth()));
        await renderer.ChangeAsync("editor-title", "retained-draft");
        api.ListRevision = 99; await renderer.ClickAsync("work-items-refresh");
        renderer.Find("editor-title").Attributes["value"].Should().Be("retained-draft");
        renderer.Find("editor-original-revision").Text.Should().Contain("7");
        api.DetailReads.Should().Be(1);
        api.ApplyFailure = new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, 99);
        await renderer.ClickAsync("editor-save");
        api.Command!.ExpectedRevision.Should().Be(7); api.Command.Patch.Title.Should().Be("retained-draft");
        renderer.Find("editor-title").Attributes["value"].Should().Be("retained-draft");
    }

    [Fact]
    public async Task Bounded_list_absence_does_not_rebind_the_selected_draft_to_another_target()
    {
        var api = new Api(); using var services = Services(api);
        await using var renderer = Renderer(services); await renderer.MountAsync<PageHost>(Parameters(Auth()));
        await renderer.ChangeAsync("editor-title", "retained-outside-list");
        api.OmitFirstFromLists = true; await renderer.ClickAsync("work-items-refresh");
        renderer.Find("editor-title").Attributes["value"].Should().Be("retained-outside-list");
        renderer.Elements().Should().Contain(element => element.Text.Contains("不代表已失去存取權"));
        await renderer.ClickAsync("editor-save"); api.Command!.WorkItemId.Should().Be(api.First.Id);
    }

    [Fact]
    public async Task An_already_verified_same_subject_auth_refresh_keeps_the_existing_draft()
    {
        var api = new Api(); using var services = Services(api); var auth = Auth();
        await using var renderer = Renderer(services); await renderer.MountAsync<PageHost>(Parameters(auth));
        await renderer.ChangeAsync("editor-title", "retained-auth-draft"); var reads = api.Reads;
        await renderer.UpdateAsync(Parameters(Task.FromResult(await auth)));
        renderer.Find("editor-title").Attributes["value"].Should().Be("retained-auth-draft"); api.Reads.Should().Be(reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_new_anonymous_or_invalid_identity_invalidates_an_older_pending_authentication(bool partial)
    {
        var api = new Api(); using var services = Services(api);
        await using var renderer = Renderer(services);
        var old = new TaskCompletionSource<AuthenticationState>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mounting = renderer.MountAsync<PageHost>(Parameters(old.Task)); await renderer.FlushAsync();
        var latest = new AuthenticationState(partial ? new(new ClaimsIdentity([new("contexthub:tenant_id", "invalid"), new(ClaimTypes.Name, "synthetic")], "synthetic-cookie"))
            : new(new ClaimsIdentity()));
        var updating = renderer.UpdateAsync(Parameters(Task.FromResult(latest))); await renderer.FlushAsync();
        old.SetResult(await Auth()); await Task.WhenAll(mounting, updating);
        api.Reads.Should().Be(0); api.Command.Should().BeNull();
        renderer.Elements().Should().NotContain(element => element.Attributes.ContainsKey("data-testid") && Equals(element.Attributes["data-testid"], "editor-title"));
    }

    [Fact]
    public async Task Project_selection_binds_the_new_actual_detail_before_any_edit()
    {
        var api = new Api(); using var services = Services(api);
        await using var renderer = Renderer(services); await renderer.MountAsync<PageHost>(Parameters(Auth()));
        await renderer.ChangeAsync("editor-title", "old-project-unsaved");
        await renderer.ChangeAsync("work-items-project", "Other");
        renderer.Find("editor-title").Attributes["value"].Should().Be(api.Second.Title);
        await renderer.ChangeAsync("editor-title", "other-draft"); await renderer.ClickAsync("editor-save");
        api.Command!.ProjectId.Should().Be("Other"); api.Command.WorkItemId.Should().Be(api.Second.Id);
        api.Command.Patch.Title.Should().Be("other-draft"); api.LegacyWrites.Should().Be(0);
    }

    [Fact]
    public async Task Sign_out_clears_private_list_and_editor_without_another_api_read()
    {
        var api = new Api(); using var services = Services(api);
        await using var renderer = Renderer(services); await renderer.MountAsync<PageHost>(Parameters(Auth()));
        await renderer.ChangeAsync("editor-title", "private-unsaved"); var reads = api.Reads;
        await renderer.UpdateAsync(Parameters(Task.FromResult(new AuthenticationState(new(new ClaimsIdentity())))));
        renderer.Elements().Should().NotContain(element => element.Text.Contains("private-unsaved") || element.Text.Contains(api.First.Title));
        renderer.Elements().Should().NotContain(element => element.Attributes.ContainsKey("data-testid") && Equals(element.Attributes["data-testid"], "editor-title"));
        api.Reads.Should().Be(reads); api.Command.Should().BeNull();
    }

    [Fact]
    public async Task Old_page_composer_events_cannot_refill_or_submit_after_identity_changes()
    {
        var api = new Api(); using var services = Services(api);
        await using var renderer = Renderer(services); await renderer.MountAsync<PageHost>(Parameters(Auth()));
        await renderer.ClickAsync("work-items-compose"); await renderer.ChangeAsync("work-items-create-title", "old private title");
        var oldTitle = renderer.Find("work-items-create-title"); var oldSubmit = renderer.Find("work-items-create-submit");
        await renderer.UpdateAsync(Parameters(Auth()));
        await renderer.ClickAsync("work-items-compose"); await renderer.ChangeAsync("work-items-create-title", "new subject title");
        var changeError = await Record.ExceptionAsync(() => renderer.DispatchAsync(oldTitle, "onchange", new ChangeEventArgs { Value = "late old private title" }));
        if (changeError is not null) changeError.Should().BeOfType<ArgumentException>();
        renderer.Find("work-items-create-title").Attribute("value").Should().Be("new subject title");
        var submitError = await Record.ExceptionAsync(() => renderer.DispatchAsync(oldSubmit, "onclick", new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        if (submitError is not null) submitError.Should().BeOfType<ArgumentException>();
        api.LegacyWrites.Should().Be(0); api.Command.Should().BeNull();
    }

    [Fact]
    public async Task Exact_old_receipt_does_not_replace_a_newer_version_already_read_into_the_list()
    {
        var api = new Api { ApplyFailure = new HttpRequestException("synthetic transport") }; using var services = Services(api);
        await using var renderer = Renderer(services); await renderer.MountAsync<PageHost>(Parameters(Auth()));
        await renderer.ChangeAsync("editor-title", "original-edit"); await renderer.ClickAsync("editor-save");
        api.ListRevision = 99; await renderer.ClickAsync("work-items-refresh");
        api.RecoveryReceipt = api.Receipt(api.Command!); await renderer.ClickAsync("editor-recover");
        renderer.Find($"work-item-select-{api.First.Id:N}").Text.Should().Contain("newer-list-title").And.NotContain("original-edit");
        api.Recoveries.Should().Be(1);
    }

    private static ServiceProvider Services(Api api) => new ServiceCollection().AddLogging().AddSingleton(api.Client).BuildServiceProvider();
    private static WorkItemEditorTestRenderer Renderer(ServiceProvider services) => new(services, services.GetRequiredService<ILoggerFactory>());
    private static ParameterView Parameters(Task<AuthenticationState> auth) => ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(PageHost.Authentication)] = auth });
    private static Task<AuthenticationState> Auth() => Task.FromResult(new AuthenticationState(new(new ClaimsIdentity(
        [new("contexthub:tenant_id", Guid.NewGuid().ToString()), new("contexthub:user_id", Guid.NewGuid().ToString()), new(ClaimTypes.Name, "synthetic")], "synthetic-cookie"))));
    public sealed class PageHost : ComponentBase
    {
        [Parameter] public Task<AuthenticationState> Authentication { get; set; } = null!;
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<CascadingValue<Task<AuthenticationState>>>(0); builder.AddAttribute(1, "Value", Authentication);
            builder.AddAttribute(2, "ChildContent", (RenderFragment)(child => { child.OpenComponent<ProjectWorkItems>(0); child.CloseComponent(); })); builder.CloseComponent();
        }
    }
    private sealed class Api
    {
        public ProjectWorkItemResult First { get; } = Row(Guid.NewGuid(), "ContextHub", "private-first-title");
        public ProjectWorkItemResult Second { get; } = Row(Guid.NewGuid(), "Other", "other-title");
        public long ListRevision { get; set; } = 7;
        public bool OmitFirstFromLists { get; set; }
        public int Reads { get; private set; }
        public int DetailReads { get; private set; }
        public int LegacyWrites { get; private set; }
        public int Recoveries { get; private set; }
        public WorkItemEditCommand? Command { get; private set; }
        public Exception? ApplyFailure { get; set; }
        public WorkItemEditReceipt? RecoveryReceipt { get; set; }
        public IContextHubApiClient Client { get; }
        public Api() { Client = DispatchProxy.Create<IContextHubApiClient, Proxy>(); ((Proxy)(object)Client).InvokeCall = Invoke; }
        public WorkItemEditReceipt Receipt(WorkItemEditCommand command)
        {
            var result = (command.WorkItemId == First.Id ? First : Second) with { Title = command.Patch.Title ?? First.Title, Revision = command.ExpectedRevision!.Value + 1 };
            return new(Guid.NewGuid(), Guid.NewGuid(), command.ProjectId, command.WorkItemId, command.RequestId, WorkItemEditContract.Hash(command),
                WorkItemEditContract.IntentHashVersion, command.Operation, command.ExpectedRevision.Value, result.Revision!.Value, true, DateTimeOffset.UnixEpoch, result);
        }
        private object Invoke(string name, object?[] args)
        {
            if (name == nameof(IContextHubApiClient.GetProjectInformationProjectsAsync))
            {
                Reads++; return Task.FromResult<IReadOnlyList<ProjectInformationListItem>>([new(new(Guid.NewGuid(), "ContextHub", "ContextHub", "", DateTimeOffset.UnixEpoch), 1), new(new(Guid.NewGuid(), "Other", "Other", "", DateTimeOffset.UnixEpoch), 1)]);
            }
            if (name == nameof(IContextHubApiClient.GetProjectWorkItemsAsync))
            {
                Reads++; var request = (ProjectWorkItemListRequest)args[0]!; var row = request.ProjectId == "ContextHub" ? First : Second;
                if (OmitFirstFromLists && row.Id == First.Id) return Task.FromResult<IReadOnlyList<ProjectWorkItemResult>>([]);
                return Task.FromResult<IReadOnlyList<ProjectWorkItemResult>>([row with { Revision = ListRevision, Title = ListRevision > 7 ? "newer-list-title" : row.Title }]);
            }
            if (name == nameof(IWorkItemEditingApi.GetWorkItemForEditAsync))
            {
                Reads++; DetailReads++; return Task.FromResult<ProjectWorkItemResult?>((Guid)args[1]! == First.Id ? First : Second);
            }
            if (name == nameof(IWorkItemEditingApi.ApplyWorkItemEditAsync))
            {
                Command = (WorkItemEditCommand)args[0]!;
                return ApplyFailure is null ? Task.FromResult(Receipt(Command)) : Task.FromException<WorkItemEditReceipt>(ApplyFailure);
            }
            if (name == nameof(IWorkItemEditingApi.RecoverWorkItemEditAsync)) { Recoveries++; return Task.FromResult(RecoveryReceipt); }
            LegacyWrites++; throw new NotSupportedException("Unexpected legacy or unrelated API path.");
        }
        private static ProjectWorkItemResult Row(Guid id, string project, string title) => new(id, project, title, "description", [], [], ProjectWorkItemStatus.Pending,
            0, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null)
        { Revision = 7 };
    }
    public class Proxy : DispatchProxy
    {
        public Func<string, object?[], object> InvokeCall { get; set; } = null!;
        protected override object Invoke(MethodInfo? targetMethod, object?[]? args) => InvokeCall(targetMethod!.Name, args!);
    }
}

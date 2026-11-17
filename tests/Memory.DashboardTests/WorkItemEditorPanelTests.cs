using System.Net;
using System.Reflection;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Components.Shared;
using Memory.Dashboard.Services;
using Memory.Domain;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Memory.DashboardTests;

/// <summary>
/// Native component render/event tests. The API is a synthetic response boundary and
/// uses the real pure aggregate only to construct coherent receipts. No database,
/// HTTP identity, browser or durable ledger behavior is established here.
/// </summary>
public sealed class WorkItemEditorPanelTests
{
    private static readonly Guid ItemId = Guid.Parse("e32723b6-e7df-419f-812e-d7f3a3318692");
    private static readonly Guid LeafId = Guid.Parse("114833b7-bea6-4014-bd96-b3bd4224c735");
    private const string Project = "ContextHub", Identity = "synthetic-tenant:synthetic-user";

    [Fact]
    public async Task Empty_selection_makes_no_read_and_null_revision_remains_read_only()
    {
        await using var fixture = new Fixture();
        await fixture.Renderer.MountAsync<WorkItemEditorPanel>(fixture.Parameters("", Guid.Empty));
        fixture.Api.Reads.Should().BeEmpty();
        fixture.Text.Should().Contain("從左側選擇一筆專案代辦");
        fixture.Api.Rows[(Project, ItemId)] = Row(revision: null);
        await fixture.Renderer.UpdateAsync(fixture.Parameters());
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("original");
        fixture.Renderer.Find("editor-title").Disabled.Should().BeTrue();
        fixture.Renderer.Find("editor-save").Disabled.Should().BeTrue();
        fixture.Renderer.Find($"editor-leaf-{LeafId:N}").Disabled.Should().BeTrue();
        fixture.Text.Should().Contain("唯讀").And.Contain("未知").And.Contain("檢核清單執行面板");
        fixture.Api.Commands.Should().BeEmpty();
    }

    [Theory]
    [InlineData(EditDueAtMode.Keep)]
    [InlineData(EditDueAtMode.Clear)]
    [InlineData(EditDueAtMode.Set)]
    public async Task Real_form_save_carries_original_version_and_explicit_due_at_intent(EditDueAtMode mode)
    {
        await using var fixture = new Fixture(); await fixture.MountAsync();
        await fixture.Renderer.ChangeAsync("editor-title", "my title");
        await fixture.Renderer.ChangeAsync("editor-description", "my description");
        await fixture.Renderer.ChangeAsync("editor-tags", "one, two");
        await fixture.Renderer.ChangeAsync("editor-priority", "42");
        await fixture.Renderer.ChangeAsync("editor-definition", ProjectWorkItemDefinitionState.Frozen.ToString());
        if (mode != EditDueAtMode.Keep) await fixture.Renderer.ChangeAsync("editor-due-mode", mode.ToString());
        if (mode == EditDueAtMode.Set) await fixture.Renderer.ChangeAsync("editor-due-at", "2026-12-01T13:14:15");
        fixture.Renderer.Find("editor-save").Disabled.Should().BeFalse();
        await fixture.Renderer.ClickAsync("editor-save");
        var command = fixture.Api.Commands.Should().ContainSingle().Which;
        command.ExpectedRevision.Should().Be(7); command.ProjectId.Should().Be(Project); command.WorkItemId.Should().Be(ItemId);
        command.Patch.Title.Should().Be("my title"); command.Patch.Description.Should().Be("my description");
        command.Patch.Tags.Should().Equal("one", "two"); command.Patch.Priority.Should().Be(42);
        command.Patch.DefinitionState.Should().Be(ProjectWorkItemDefinitionState.Frozen);
        command.Patch.DueAt!.Mode.Should().Be(mode);
        if (mode == EditDueAtMode.Set)
        {
            command.Patch.DueAt.Value.Should().Be(new DateTimeOffset(2026, 12, 1, 13, 14, 15, TimeSpan.Zero));
            command.Patch.DueAt.Value!.Value.Offset.Should().Be(TimeSpan.Zero);
        }
        else command.Patch.DueAt.Value.Should().BeNull();
        fixture.Committed.Should().ContainSingle().Which.Revision.Should().Be(8);
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("my title");
        fixture.Renderer.Find("editor-original-revision").Text.Should().Contain("8");
    }

    [Fact]
    public async Task Invalid_UTC_date_blocks_submit_without_discarding_the_typed_input()
    {
        await using var fixture = new Fixture(); await fixture.MountAsync();
        await fixture.Renderer.ChangeAsync("editor-due-mode", EditDueAtMode.Set.ToString());
        await fixture.Renderer.ChangeAsync("editor-due-at", "not-a-date");
        fixture.Renderer.Find("editor-due-at").Attribute("value").Should().Be("not-a-date");
        fixture.Renderer.Find("editor-save").Disabled.Should().BeTrue();
        fixture.Api.Commands.Should().BeEmpty();
        await fixture.Renderer.ChangeAsync("editor-due-mode", EditDueAtMode.Keep.ToString());
        fixture.Renderer.Find("editor-save").Disabled.Should().BeFalse();
    }

    [Fact]
    public async Task Conflict_keeps_form_and_original_version_until_user_compares_and_explicitly_adopts()
    {
        await using var fixture = new Fixture(); await fixture.MountAsync();
        fixture.Api.Apply = (command, _) =>
        {
            if (fixture.Api.Commands.Count != 1) return Task.FromResult(fixture.Api.CreateReceipt(command));
            fixture.Api.Rows[(Project, ItemId)] = Row(title: "server title", revision: 12);
            return Task.FromException<WorkItemEditReceipt>(new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, 12));
        };
        await fixture.Renderer.ChangeAsync("editor-title", "my kept draft");
        await fixture.Renderer.ClickAsync("editor-save");
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("my kept draft");
        fixture.Renderer.Find("editor-original-revision").Text.Should().Contain("7");
        fixture.Renderer.Find("editor-save").Disabled.Should().BeTrue();
        fixture.Committed.Should().BeEmpty();
        await fixture.Renderer.ClickAsync("editor-compare");
        fixture.Renderer.Find("comparison-current-title").Text.Should().Contain("server title");
        fixture.Renderer.Find("comparison-base-revision").Text.Should().Be("7");
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("my kept draft");
        fixture.Api.Commands.Should().ContainSingle();
        await fixture.Renderer.ClickAsync("editor-adopt");
        fixture.Renderer.Find("editor-original-revision").Text.Should().Contain("12");
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("my kept draft");
        await fixture.Renderer.ClickAsync("editor-save");
        fixture.Api.Commands.Should().HaveCount(2); fixture.Api.Commands[1].ExpectedRevision.Should().Be(12);
        fixture.Api.Commands[1].RequestId.Should().NotBe(fixture.Api.Commands[0].RequestId);
        fixture.Committed.Should().ContainSingle().Which.Revision.Should().Be(13);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Archived_comparison_can_be_explicitly_restored_without_sending_or_losing_metadata_draft(bool invalidPriority)
    {
        await using var fixture = new Fixture(); await fixture.MountAsync();
        fixture.Api.Apply = (command, _) =>
        {
            if (fixture.Api.Commands.Count != 1) return Task.FromResult(fixture.Api.CreateReceipt(command));
            fixture.Api.Rows[(Project, ItemId)] = Row(title: "server archived title", revision: 12) with { ArchivedAt = DateTimeOffset.UnixEpoch };
            return Task.FromException<WorkItemEditReceipt>(new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, 12));
        };
        await fixture.Renderer.ChangeAsync("editor-title", "my retained draft");
        await fixture.Renderer.ClickAsync("editor-save");
        if (invalidPriority) await fixture.Renderer.ChangeAsync("editor-priority", "-1");
        await fixture.Renderer.ClickAsync("editor-compare");
        await fixture.Renderer.ClickAsync("editor-adopt");
        fixture.Renderer.Find("editor-title").Disabled.Should().BeTrue();
        fixture.Renderer.Find("editor-restore").Disabled.Should().BeFalse();
        fixture.Text.Should().Contain("還原只改封存狀態，保留草稿");
        await fixture.Renderer.ClickAsync("editor-restore");
        var restore = fixture.Api.Commands[1];
        restore.Operation.Should().Be(WorkItemEditOperation.Restore); restore.ExpectedRevision.Should().Be(12);
        restore.Patch.Title.Should().BeNull(); restore.Patch.Description.Should().BeNull(); restore.Patch.Tags.Should().BeNull();
        restore.Patch.Status.Should().BeNull(); restore.Patch.DueAt!.Mode.Should().Be(EditDueAtMode.Keep);
        fixture.Committed.Should().ContainSingle().Which.Title.Should().Be("server archived title");
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("my retained draft");
        if (invalidPriority)
        {
            fixture.Renderer.Find("editor-save").Disabled.Should().BeTrue();
            fixture.Renderer.Find("editor-priority").Attribute("value").Should().Be("-1");
            await fixture.Renderer.ChangeAsync("editor-priority", "23");
        }
        fixture.Renderer.Find("editor-save").Disabled.Should().BeFalse();
        await fixture.Renderer.ClickAsync("editor-save");
        fixture.Api.Commands[2].ExpectedRevision.Should().Be(13);
        fixture.Api.Commands[2].Patch.Title.Should().Be("my retained draft");
        fixture.Committed.Should().HaveCount(2); fixture.Committed[1].Revision.Should().Be(14);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_submission_allows_only_exact_receipt_recovery_and_404_does_not_enable_a_new_write(bool http404)
    {
        await using var fixture = new Fixture(); await fixture.MountAsync();
        WorkItemEditReceipt? committed = null;
        fixture.Api.Apply = (command, _) =>
        {
            committed = fixture.Api.CreateReceipt(command);
            return Task.FromException<WorkItemEditReceipt>(new HttpRequestException("synthetic-private-transport"));
        };
        fixture.Api.Recover = (_, _) => fixture.Api.Recoveries.Count == 1
            ? http404 ? Task.FromException<WorkItemEditReceipt?>(new HttpRequestException("synthetic-private-404", null, HttpStatusCode.NotFound))
                : Task.FromResult<WorkItemEditReceipt?>(null)
            : Task.FromResult(committed);
        await fixture.Renderer.ChangeAsync("editor-title", "pending draft");
        await fixture.Renderer.ClickAsync("editor-save");
        fixture.Renderer.Find("editor-save").Disabled.Should().BeTrue();
        fixture.Renderer.Find("editor-title").Disabled.Should().BeTrue();
        fixture.Text.Should().Contain("結果未知").And.NotContain("synthetic-private");
        await fixture.Renderer.ClickAsync("editor-recover");
        fixture.Renderer.Find("editor-save").Disabled.Should().BeTrue();
        fixture.Text.Should().Contain("結果仍未知"); fixture.Committed.Should().BeEmpty();
        fixture.Api.Commands.Should().ContainSingle();
        await fixture.Renderer.ClickAsync("editor-recover");
        fixture.Api.Commands.Should().ContainSingle(); fixture.Api.Recoveries.Should().HaveCount(2);
        var original = fixture.Api.Commands[0];
        fixture.Api.Recoveries.Should().OnlyContain(request => request.RequestId == original.RequestId &&
            request.ExpectedRequestHash == WorkItemEditContract.Hash(original) && request.WorkItemId == ItemId);
        fixture.Committed.Should().ContainSingle().Which.Revision.Should().Be(8);
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("pending draft");
    }

    [Fact]
    public async Task Completed_item_must_explicitly_reopen_before_the_real_checkbox_can_uncheck()
    {
        await using var fixture = new Fixture();
        fixture.Api.Rows[(Project, ItemId)] = Row(status: ProjectWorkItemStatus.Completed, completedLeaf: true);
        await fixture.MountAsync();
        fixture.Renderer.Find($"editor-leaf-{LeafId:N}").Disabled.Should().BeTrue();
        fixture.Api.Commands.Should().BeEmpty();
        await fixture.Renderer.ClickAsync("editor-reopen");
        fixture.Api.Commands.Should().ContainSingle().Which.Operation.Should().Be(WorkItemEditOperation.Reopen);
        fixture.Renderer.Find($"editor-leaf-{LeafId:N}").Disabled.Should().BeFalse();
        await fixture.Renderer.ChangeAsync($"editor-leaf-{LeafId:N}", false);
        fixture.Api.Commands.Should().HaveCount(2);
        fixture.Api.Commands[1].ExpectedRevision.Should().Be(8);
        fixture.Api.Commands[1].Operation.Should().Be(WorkItemEditOperation.ChecklistComplete);
        fixture.Api.Commands[1].Patch.ChecklistItemId.Should().Be(LeafId);
        fixture.Api.Commands[1].Patch.IsCompleted.Should().BeFalse();
        fixture.Committed.Should().HaveCount(2);
        fixture.Committed[1].Status.Should().Be(ProjectWorkItemStatus.InProgress);
        fixture.Committed[1].ChecklistItems.Should().ContainSingle().Which.Id.Should().Be(LeafId);
    }

    [Fact]
    public async Task Dirty_metadata_disables_structural_actions_until_it_is_saved()
    {
        await using var fixture = new Fixture();
        fixture.Api.Rows[(Project, ItemId)] = Row(completedLeaf: true); await fixture.MountAsync();
        fixture.Renderer.Find("editor-complete").Disabled.Should().BeFalse();
        await fixture.Renderer.ChangeAsync("editor-description", "unsaved metadata");
        fixture.Renderer.Find("editor-complete").Disabled.Should().BeTrue();
        fixture.Renderer.Find("editor-archive").Disabled.Should().BeTrue();
        fixture.Renderer.Find($"editor-leaf-{LeafId:N}").Disabled.Should().BeTrue();
        await fixture.Renderer.ClickAsync("editor-save");
        fixture.Renderer.Find("editor-complete").Disabled.Should().BeFalse();
        fixture.Renderer.Find($"editor-leaf-{LeafId:N}").Disabled.Should().BeFalse();
        fixture.Api.Commands.Should().ContainSingle().Which.Patch.Description.Should().Be("unsaved metadata");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scope_change_clears_private_form_and_ignores_late_write_response(bool error)
    {
        await using var fixture = new Fixture(); await fixture.MountAsync();
        var gate = new TaskCompletionSource<WorkItemEditReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        fixture.Api.Apply = (_, ct) => { token = ct; return gate.Task; };
        await fixture.Renderer.ChangeAsync("editor-title", "old private draft");
        var saving = fixture.Renderer.ClickAsync("editor-save"); await fixture.Renderer.FlushAsync();
        fixture.Renderer.Find("editor-status").Text.Should().Contain("儲存中");
        var other = Guid.NewGuid(); fixture.Api.Rows[("Other", other)] = Row(other, "Other", "new visible title", 9);
        var changed = fixture.Renderer.UpdateAsync(fixture.Parameters("Other", other, "other-subject"));
        await fixture.Renderer.FlushAsync();
        token.IsCancellationRequested.Should().BeTrue();
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("new visible title");
        fixture.Text.Should().NotContain("old private draft");
        if (error) gate.SetException(new HttpRequestException("old-private-error"));
        else gate.SetResult(fixture.Api.CreateReceipt(fixture.Api.Commands[0]));
        await Task.WhenAll(saving, changed);
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("new visible title");
        fixture.Committed.Should().BeEmpty(); fixture.Text.Should().NotContain("old-private-error");
    }

    [Fact]
    public async Task Disposing_actual_panel_cancels_pending_write_and_never_notifies_a_late_commit()
    {
        await using var fixture = new Fixture(); await fixture.MountAsync();
        var gate = new TaskCompletionSource<WorkItemEditReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        fixture.Api.Apply = (_, ct) => { token = ct; return gate.Task; };
        await fixture.Renderer.ChangeAsync("editor-title", "pending");
        var saving = fixture.Renderer.ClickAsync("editor-save"); await fixture.Renderer.FlushAsync();
        await fixture.Renderer.RemoveAsync(); token.IsCancellationRequested.Should().BeTrue();
        gate.SetResult(fixture.Api.CreateReceipt(fixture.Api.Commands[0])); await saving;
        fixture.Committed.Should().BeEmpty(); fixture.Api.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task Old_rendered_events_cannot_edit_or_submit_the_new_scope()
    {
        await using var fixture = new Fixture(); await fixture.MountAsync();
        await fixture.Renderer.ChangeAsync("editor-title", "old draft");
        var oldTitle = fixture.Renderer.Find("editor-title"); var oldSave = fixture.Renderer.Find("editor-save");
        var other = Guid.NewGuid(); fixture.Api.Rows[("Other", other)] = Row(other, "Other", "other title", 9);
        await fixture.Renderer.UpdateAsync(fixture.Parameters("Other", other, "other-subject"));
        await fixture.Renderer.ChangeAsync("editor-title", "new subject draft");
        var changeError = await Record.ExceptionAsync(() => fixture.Renderer.DispatchAsync(oldTitle, "onchange", new ChangeEventArgs { Value = "late old value" }));
        if (changeError is not null) changeError.Should().BeOfType<ArgumentException>("the framework may already have retired an old handler");
        fixture.Renderer.Find("editor-title").Attribute("value").Should().Be("new subject draft");
        var saveError = await Record.ExceptionAsync(() => fixture.Renderer.DispatchAsync(oldSave, "onclick", new Microsoft.AspNetCore.Components.Web.MouseEventArgs()));
        if (saveError is not null) saveError.Should().BeOfType<ArgumentException>();
        fixture.Api.Commands.Should().BeEmpty();
    }

    private static ProjectWorkItemResult Row(Guid? id = null, string project = Project, string title = "original", long? revision = 7,
        ProjectWorkItemStatus status = ProjectWorkItemStatus.InProgress, bool completedLeaf = false)
        => new(id ?? ItemId, project, title, "description", ["initial"], [new(LeafId, "stable leaf", completedLeaf, 0)], status, 10,
            new DateTimeOffset(2026, 11, 1, 10, 0, 0, TimeSpan.FromHours(8)), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch,
            status == ProjectWorkItemStatus.Completed ? DateTimeOffset.UnixEpoch : null)
        { Revision = revision, DefinitionState = ProjectWorkItemDefinitionState.ReadyForDevelopment };

    private sealed class Fixture : IAsyncDisposable
    {
        private readonly ServiceProvider _services;
        public Api Api { get; } = new();
        public List<ProjectWorkItemResult> Committed { get; } = [];
        public WorkItemEditorTestRenderer Renderer { get; }
        public string Text => string.Join(" ", Renderer.Elements().Select(element => element.Text));
        public Fixture()
        {
            var client = DispatchProxy.Create<IContextHubApiClient, PanelApiProxy>();
            ((PanelApiProxy)(object)client).Handler = (method, args) => method switch
            {
                nameof(IWorkItemEditingApi.GetWorkItemForEditAsync) => Api.ReadAsync((string)args[0]!, (Guid)args[1]!, (CancellationToken)args[2]!),
                nameof(IWorkItemEditingApi.ApplyWorkItemEditAsync) => Api.ApplyAsync((WorkItemEditCommand)args[0]!, (CancellationToken)args[1]!),
                nameof(IWorkItemEditingApi.RecoverWorkItemEditAsync) => Api.RecoverAsync((WorkItemEditRecoveryRequest)args[0]!, (CancellationToken)args[1]!),
                _ => throw new NotSupportedException("This component test permits only its explicit edit API boundary.")
            };
            _services = new ServiceCollection().AddLogging().AddSingleton(client).BuildServiceProvider();
            Renderer = new(_services, _services.GetRequiredService<ILoggerFactory>());
        }
        public ParameterView Parameters(string project = Project, Guid? id = null, string identity = Identity)
            => ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(WorkItemEditorPanel.ProjectId)] = project,
                [nameof(WorkItemEditorPanel.WorkItemId)] = id ?? ItemId,
                [nameof(WorkItemEditorPanel.IdentityKey)] = identity,
                [nameof(WorkItemEditorPanel.Committed)] = EventCallback.Factory.Create<ProjectWorkItemResult>(this, (Action<ProjectWorkItemResult>)(row => Committed.Add(row)))
            });
        public Task MountAsync() => Renderer.MountAsync<WorkItemEditorPanel>(Parameters());
        public async ValueTask DisposeAsync() { await Renderer.DisposeRendererAsync(); _services.Dispose(); }
    }

    public class PanelApiProxy : DispatchProxy
    {
        public Func<string, object?[], object> Handler { get; set; } = null!;
        protected override object Invoke(MethodInfo? targetMethod, object?[]? args) => Handler(targetMethod!.Name, args!);
    }

    private sealed class Api
    {
        public Dictionary<(string Project, Guid Id), ProjectWorkItemResult> Rows { get; } = new() { [(Project, ItemId)] = Row() };
        public List<(string Project, Guid Id)> Reads { get; } = [];
        public List<WorkItemEditCommand> Commands { get; } = [];
        public List<WorkItemEditRecoveryRequest> Recoveries { get; } = [];
        public Func<WorkItemEditCommand, CancellationToken, Task<WorkItemEditReceipt>>? Apply { get; set; }
        public Func<WorkItemEditRecoveryRequest, CancellationToken, Task<WorkItemEditReceipt?>>? Recover { get; set; }
        public Task<ProjectWorkItemResult?> ReadAsync(string project, Guid id, CancellationToken ct)
        { Reads.Add((project, id)); return Task.FromResult(Rows.GetValueOrDefault((project, id))); }
        public Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditCommand command, CancellationToken ct)
        { Commands.Add(command); return Apply?.Invoke(command, ct) ?? Task.FromResult(CreateReceipt(command)); }
        public Task<WorkItemEditReceipt?> RecoverAsync(WorkItemEditRecoveryRequest request, CancellationToken ct)
        { Recoveries.Add(request); return Recover?.Invoke(request, ct) ?? Task.FromResult<WorkItemEditReceipt?>(null); }
        public WorkItemEditReceipt CreateReceipt(WorkItemEditCommand command)
        {
            var normalized = WorkItemEditContract.Normalize(command); var before = Rows[(normalized.ProjectId, normalized.WorkItemId)];
            var entity = new ProjectWorkItem
            {
                Id = before.Id,
                ProjectId = before.ProjectId,
                Revision = before.Revision!.Value,
                Title = before.Title,
                Description = before.Description,
                Tags = before.Tags.ToArray(),
                Status = before.Status,
                Priority = before.Priority,
                DueAt = before.DueAt,
                DefinitionState = before.DefinitionState,
                CreatedAt = before.CreatedAt,
                UpdatedAt = before.UpdatedAt,
                CompletedAt = before.CompletedAt,
                ArchivedAt = before.ArchivedAt
            };
            entity.ChecklistItems = before.ChecklistItems.Select(row => new ProjectWorkItemChecklistItem
            { Id = row.Id, WorkItemId = entity.Id, WorkItem = entity, Content = row.Content, IsCompleted = row.IsCompleted, SortOrder = row.SortOrder }).ToList();
            var time = new DateTimeOffset(2026, 10, 26, 13, 0, 0, TimeSpan.Zero);
            var changed = WorkItemEditAggregate.Apply(entity, normalized, time);
            var result = WorkItemEditProjection.ToResult(entity); Rows[(result.ProjectId, result.Id)] = result;
            return new(Guid.NewGuid(), changed ? Guid.NewGuid() : null, result.ProjectId, result.Id, normalized.RequestId,
                WorkItemEditContract.Hash(normalized), WorkItemEditContract.IntentHashVersion, normalized.Operation,
                before.Revision.Value, entity.Revision, changed, time, result);
        }
    }
}

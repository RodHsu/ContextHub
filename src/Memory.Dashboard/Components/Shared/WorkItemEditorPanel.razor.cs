using System.Globalization;
using Memory.Application;
using Memory.Dashboard.Services;
using Memory.Domain;
using Microsoft.AspNetCore.Components;

namespace Memory.Dashboard.Components.Shared;

public partial class WorkItemEditorPanel
{
    [Parameter] public string ProjectId { get; set; } = string.Empty;
    [Parameter] public Guid WorkItemId { get; set; }
    [Parameter] public string IdentityKey { get; set; } = string.Empty;
    [Parameter] public EventCallback<ProjectWorkItemResult> Committed { get; set; }

    private WorkItemEditDraftState? _state;
    private WorkItemEditDraftState State => _state ??= new(ApiClient);
    private bool _disposed, _priorityValid = true, _dueValid = true;
    private string _boundProject = string.Empty, _boundIdentity = string.Empty, _priorityInput = string.Empty, _dueInput = string.Empty;
    private Guid _boundId;
    private Guid? _lastNotifiedReceipt;
    private long _generation;
    private bool ValidSelection => _boundId != Guid.Empty && _boundProject.Length > 0 && _boundIdentity.Length > 0;
    private bool Busy => State.Loading || State.Saving || State.Recovering;
    private bool FieldsDisabled => _disposed || Busy || State.OutcomeUnknown || State.Base is null || State.Base.IsArchived || State.Base.Revision is not > 0;
    private bool CanSave => !FieldsDisabled && State.CanApply && State.HasDraft && _priorityValid && _dueValid;
    private bool LifecycleDisabled => _disposed || !State.CanApply || State.HasDraft || !_priorityValid || !_dueValid;
    private bool RestoreDisabled => _disposed || !State.CanApply || State.Base?.IsArchived != true;
    private bool StructuralDisabled => LifecycleDisabled || State.Base?.IsArchived != false;
    private string TitleValue => State.Draft.Title ?? State.Base?.Title ?? string.Empty;
    private string DescriptionValue => State.Draft.Description ?? State.Base?.Description ?? string.Empty;
    private string TagsValue => string.Join(", ", State.Draft.Tags ?? State.Base?.Tags ?? []);
    private string PriorityValue => !_priorityValid ? _priorityInput : (State.Draft.Priority ?? State.Base?.Priority)?.ToString(CultureInfo.InvariantCulture) ?? string.Empty;
    private string DefinitionValue => (State.Draft.DefinitionState ?? State.Base?.DefinitionState)?.ToString() ?? string.Empty;
    private EditDueAtMode DueMode => State.Draft.DueAt?.Mode ?? EditDueAtMode.Keep;
    private string DueInputValue => DueMode == EditDueAtMode.Set ? _dueInput : UtcInput(State.Base?.DueAt);
    private string StatusText => State.Saving ? "儲存中；請勿重新送出。" : State.Recovering ? "核對原請求回執中。" : State.Loading ? "讀取目前範圍資料中。" : State.OutcomeUnknown ? "原請求結果未知。" : "僅使用實際讀取版本進行編輯。";

    protected override async Task OnParametersSetAsync()
    {
        if (_disposed) return;
        var project = ProjectId?.Trim() ?? string.Empty; var identity = IdentityKey?.Trim() ?? string.Empty;
        if (_boundId == WorkItemId && _boundIdentity == identity && ProjectContext.Matches(_boundProject, project)) return;
        _generation++; _boundProject = project; _boundId = WorkItemId; _boundIdentity = identity; _lastNotifiedReceipt = null;
        ResetValidation(); State.Bind(project, WorkItemId, identity);
        if (ValidSelection) await State.LoadAsync();
    }

    private void TitleChanged(ChangeEventArgs args) => Change(patch => patch with { Title = args.Value?.ToString() ?? string.Empty });
    private void DescriptionChanged(ChangeEventArgs args) => Change(patch => patch with { Description = args.Value?.ToString() ?? string.Empty });
    private void TagsChanged(ChangeEventArgs args) => Change(patch => patch with
    {
        Tags = (args.Value?.ToString() ?? string.Empty)
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
    });
    private void PriorityChanged(ChangeEventArgs args)
    {
        if (FieldsDisabled) return;
        _priorityInput = args.Value?.ToString() ?? string.Empty;
        _priorityValid = int.TryParse(_priorityInput, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value is >= 0 and <= 100;
        if (_priorityValid) Change(patch => patch with { Priority = value });
    }
    private void DefinitionChanged(ChangeEventArgs args)
    {
        if (Enum.TryParse<ProjectWorkItemDefinitionState>(args.Value?.ToString(), out var value) && Enum.IsDefined(value))
            Change(patch => patch with { DefinitionState = value });
    }
    private void DueModeChanged(ChangeEventArgs args)
    {
        if (FieldsDisabled || !Enum.TryParse<EditDueAtMode>(args.Value?.ToString(), out var mode) || !Enum.IsDefined(mode)) return;
        _dueValid = true;
        if (mode == EditDueAtMode.Set)
        {
            _dueInput = UtcInput(State.Base?.DueAt);
            SetDueValue();
        }
        else Change(patch => patch with { DueAt = mode == EditDueAtMode.Keep ? null : new(EditDueAtMode.Clear) });
    }
    private void DueValueChanged(ChangeEventArgs args)
    {
        if (FieldsDisabled || DueMode != EditDueAtMode.Set) return;
        _dueInput = args.Value?.ToString() ?? string.Empty; SetDueValue();
    }
    private void SetDueValue()
    {
        _dueValid = DateTimeOffset.TryParseExact(_dueInput, ["yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ss.FFFFFFF"],
            CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var value);
        Change(patch => patch with { DueAt = new(EditDueAtMode.Set, _dueValid ? value : null) });
    }
    private void Change(Func<WorkItemEditPatch, WorkItemEditPatch> change)
    {
        if (!FieldsDisabled) State.SetDraft(change(State.Draft));
    }

    private Task SaveAsync() => CanSave ? RunAsync(() => State.ApplyAsync()) : Task.CompletedTask;
    private Task CompleteAsync() => StructuralDisabled || State.Base!.ChecklistItems.Any(row => !row.IsCompleted)
        ? Task.CompletedTask : RunAsync(() => State.ApplyAsync(WorkItemEditOperation.Update, new(Status: ProjectWorkItemStatus.Completed)));
    private Task ReopenAsync() => StructuralDisabled || State.Base?.Status != ProjectWorkItemStatus.Completed
        ? Task.CompletedTask : RunAsync(() => State.ApplyAsync(WorkItemEditOperation.Reopen));
    private Task ArchiveAsync() => LifecycleDisabled || State.Base?.IsArchived != false
        ? Task.CompletedTask : RunAsync(() => State.ApplyAsync(WorkItemEditOperation.Archive));
    private Task RestoreAsync() => RestoreDisabled
        ? Task.CompletedTask : RunAsync(() => State.ApplyAsync(WorkItemEditOperation.Restore));
    private Task ChecklistChangedAsync(ProjectWorkItemChecklistItemResult leaf, ChangeEventArgs args)
    {
        if (StructuralDisabled || State.Base?.Status == ProjectWorkItemStatus.Completed && leaf.IsCompleted) return Task.CompletedTask;
        var completed = args.Value is bool value ? value : bool.TryParse(args.Value?.ToString(), out var parsed) && parsed;
        return RunAsync(() => State.ApplyAsync(WorkItemEditOperation.ChecklistComplete, new(ChecklistItemId: leaf.Id, IsCompleted: completed)));
    }
    private Task CompareAsync() => Busy || State.OutcomeUnknown ? Task.CompletedTask : RunAsync(State.ReloadForComparisonAsync, notify: false);
    private void AdoptComparison() { if (!_disposed) State.AcceptComparison(); }
    private Task RecoverAsync() => Busy || !State.OutcomeUnknown ? Task.CompletedTask : RunAsync(State.RecoverAsync);
    private Task LoadAsync() => Busy || State.OutcomeUnknown ? Task.CompletedTask : RunAsync(State.LoadAsync, notify: false);

    // Event IDs can survive a render when the callback delegate is otherwise equal.
    // Bind the rendered controls to their original scope, not the current target at dispatch.
    private void InScope(long renderedGeneration, Action action)
    {
        if (!_disposed && renderedGeneration == _generation) action();
    }
    private Task InScopeAsync(long renderedGeneration, Func<Task> action)
        => !_disposed && renderedGeneration == _generation ? action() : Task.CompletedTask;

    private async Task RunAsync(Func<Task> operation, bool notify = true)
    {
        if (_disposed) return;
        var generation = _generation;
        var task = operation();
        await InvokeAsync(StateHasChanged);
        await task;
        if (_disposed || generation != _generation) return;
        if (notify && State.LastReceipt is { } receipt && receipt.ReceiptId != _lastNotifiedReceipt &&
            receipt.WorkItemId == _boundId && ProjectContext.Matches(receipt.ProjectId, _boundProject))
        {
            _lastNotifiedReceipt = receipt.ReceiptId;
            if (!State.HasDraft) ResetValidation();
            await Committed.InvokeAsync(receipt.Result);
        }
    }

    private void ResetValidation() { _priorityValid = _dueValid = true; _priorityInput = _dueInput = string.Empty; }
    private static string UtcInput(DateTimeOffset? value) => value?.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture) ?? string.Empty;
    private static string UtcText(DateTimeOffset? value) => value?.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture) ?? "未設定";
    private static string StatusLabel(ProjectWorkItemStatus value) => value switch
    {
        ProjectWorkItemStatus.Pending => "待處理",
        ProjectWorkItemStatus.InProgress => "進行中",
        ProjectWorkItemStatus.Blocked => "已阻塞",
        ProjectWorkItemStatus.Completed => "已完成",
        _ => "已取消"
    };
    private static string DefinitionLabel(ProjectWorkItemDefinitionState value) => value switch
    {
        ProjectWorkItemDefinitionState.Discussing => "討論中",
        ProjectWorkItemDefinitionState.Draft => "草稿",
        ProjectWorkItemDefinitionState.ReadyForDevelopment => "可進入開發",
        ProjectWorkItemDefinitionState.Frozen => "已凍結",
        _ => "已取代"
    };
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++; _state?.Dispose();
        ResetValidation(); _boundProject = _boundIdentity = string.Empty; _boundId = Guid.Empty;
    }
}

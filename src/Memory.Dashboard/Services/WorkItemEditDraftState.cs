using System.Net;
using Memory.Application;

namespace Memory.Dashboard.Services;

/// <summary>Non-secret recovery metadata only. IdentityKey must be a subject key, never a credential.</summary>
public sealed record WorkItemEditRecoveryCheckpoint(string IdentityKey, string ProjectId, Guid WorkItemId,
    string RequestId, string ExpectedRequestHash, string HashVersion);

/// <summary>
/// Holds one scoped draft and exact pending intent. It does not store browser data or
/// promise restart durability: a host must retain CaptureCheckpoint before discarding
/// this controller. Receipts describe the original edit, not guaranteed current state.
/// </summary>
public sealed class WorkItemEditDraftState(IWorkItemEditingApi api, Func<string>? requestIdFactory = null) : IDisposable
{
    private readonly Func<string> _requestId = requestIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private readonly Dictionary<BindingKey, WorkItemEditRecoveryCheckpoint> _unresolved = [];
    private CancellationTokenSource? _read, _write, _recovery;
    private PendingEdit? _pending;
    private long _generation;
    private bool _disposed;

    public string ProjectId { get; private set; } = string.Empty;
    public Guid WorkItemId { get; private set; }
    public string IdentityKey { get; private set; } = string.Empty;
    public ProjectWorkItemResult? Base { get; private set; }
    public ProjectWorkItemResult? Comparison { get; private set; }
    public WorkItemEditPatch Draft { get; private set; } = new();
    public bool HasDraft { get; private set; }
    public bool Loading { get; private set; }
    public bool Saving { get; private set; }
    public bool Recovering { get; private set; }
    public bool OutcomeUnknown { get; private set; }
    public string? ConflictCode { get; private set; }
    public long? ServerRevision { get; private set; }
    public string? Error { get; private set; }
    public WorkItemEditReceipt? LastReceipt { get; private set; }
    public bool CanApply => ValidBinding && Base?.Revision is > 0 && !Loading && !Saving && !Recovering &&
        !OutcomeUnknown && ConflictCode is null;

    public void Bind(string projectId, Guid workItemId, string identityKey)
    {
        if (_disposed) return;
        var project = projectId.Trim(); var identity = identityKey.Trim();
        if (ProjectContext.Matches(ProjectId, project) && WorkItemId == workItemId && IdentityKey == identity) return;
        _generation++;
        Cancel(ref _read); Cancel(ref _write); Cancel(ref _recovery);
        ProjectId = project; WorkItemId = workItemId; IdentityKey = identity;
        Base = Comparison = null; Draft = new(); HasDraft = false;
        Loading = Saving = Recovering = OutcomeUnknown = false;
        ConflictCode = Error = null; ServerRevision = null; LastReceipt = null; _pending = null;
        // Only metadata survives scope changes. It is not exposed for another identity/target.
        if (ValidBinding && _unresolved.TryGetValue(Key, out var checkpoint))
        {
            _pending = new(checkpoint, null, false, null);
            OutcomeUnknown = true;
        }
    }

    public Task LoadAsync()
    {
        if (HasDraft || ConflictCode is not null)
        {
            Error = "草稿已保留，請先重新載入比較，再明確採用讀取版本。";
            return Task.CompletedTask;
        }
        return ReadAsync(comparison: false);
    }

    public Task ReloadForComparisonAsync() => ReadAsync(comparison: true);

    public bool AcceptComparison()
    {
        if (_disposed || Saving || Recovering || Loading || OutcomeUnknown || !ValidRow(Comparison) || Comparison!.Revision is not > 0)
            return false;
        Base = Comparison; Comparison = null; ConflictCode = Error = null; ServerRevision = null;
        return true;
    }

    public void SetDraft(WorkItemEditPatch patch)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(patch);
        if (Loading || Saving || Recovering || OutcomeUnknown)
            throw new InvalidOperationException("Finish the scoped read or resolve the exact pending edit before changing its draft.");
        Draft = patch with
        {
            Tags = patch.Tags is null ? null : Array.AsReadOnly(patch.Tags.ToArray()),
            ChecklistOrder = patch.ChecklistOrder is null ? null : Array.AsReadOnly(patch.ChecklistOrder.ToArray())
        };
        HasDraft = true; Error = null;
    }

    public async Task ApplyAsync(WorkItemEditOperation operation = WorkItemEditOperation.Update, WorkItemEditPatch? patch = null)
    {
        if (_disposed || !ValidBinding || Saving || Recovering || Loading || OutcomeUnknown) return;
        if (ConflictCode is not null)
        {
            Error = "草稿已保留，請比較並明確採用重新讀取的版本後再送出。";
            return;
        }
        if (Base?.Revision is not > 0)
        {
            ConflictCode = SafeEditConflictCodes.MissingReadVersion;
            Error = "尚未取得有效的原讀取版本；請重新載入或更新程式。";
            return;
        }
        WorkItemEditCommand command;
        PendingEdit pending;
        try
        {
            command = WorkItemEditContract.Normalize(new(ProjectId, WorkItemId, Base.Revision, _requestId(), operation,
                patch ?? (operation == WorkItemEditOperation.Update ? Draft : new())));
            var checkpoint = new WorkItemEditRecoveryCheckpoint(IdentityKey, ProjectId, WorkItemId, command.RequestId,
                WorkItemEditContract.Hash(command), WorkItemEditContract.IntentHashVersion);
            pending = new(checkpoint, operation, operation == WorkItemEditOperation.Update && patch is null, command.ExpectedRevision);
        }
        catch (Exception)
        {
            Error = "無法建立有效的編輯要求，草稿已保留；尚未送出儲存。";
            return;
        }

        Cancel(ref _read); Cancel(ref _write);
        Loading = false; Comparison = null; LastReceipt = null; Error = null;
        var request = _write = new(); var generation = _generation;
        _pending = pending; _unresolved[Key] = pending.Checkpoint; // Before any network work.
        Saving = true;
        try
        {
            var receipt = await api.ApplyWorkItemEditAsync(command, request.Token);
            if (!Current(generation, request, _write)) return;
            if (!Matches(receipt, pending)) { Unknown("回應無法綁定原編輯；保留原請求並核對回執。"); return; }
            Complete(receipt, pending);
        }
        catch (SafeEditConflictException ex)
        {
            if (!Current(generation, request, _write)) return;
            Forget(pending);
            ConflictCode = KnownConflict(ex.Code);
            ServerRevision = ex.CurrentRevision is > 0 ? ex.CurrentRevision : null;
            Error = "儲存發生衝突，草稿及原讀取版本已保留；請重新載入比較。";
        }
        catch (NotSupportedException)
        {
            if (!Current(generation, request, _write)) return;
            Forget(pending); Error = "此伺服器尚未提供版本化編輯；草稿已保留。";
        }
        catch (HttpRequestException ex) when (DefiniteRejection(ex.StatusCode))
        {
            if (!Current(generation, request, _write)) return;
            Forget(pending); Error = "伺服器拒絕此編輯，草稿已保留；請確認存取範圍及契約。";
        }
        catch (Exception)
        {
            if (Current(generation, request, _write)) Unknown("儲存結果未知；保留原請求，請核對回執，不要重新送出。");
        }
        finally { if (Current(generation, request, _write)) Saving = false; }
    }

    public async Task RecoverAsync()
    {
        if (_disposed || !OutcomeUnknown || _pending is null || Saving || Recovering || !ValidBinding) return;
        Cancel(ref _read); Loading = false; Comparison = null;
        Cancel(ref _recovery);
        var pending = _pending; var request = _recovery = new(); var generation = _generation;
        Recovering = true; Error = null;
        try
        {
            var recovery = Recovery(pending.Checkpoint);
            var receipt = await api.RecoverWorkItemEditAsync(recovery, request.Token);
            if (!Current(generation, request, _recovery)) return;
            if (receipt is null) { Unknown("尚未取得原請求的已提交回執；結果仍未知，不會自動重送。"); return; }
            if (!Matches(receipt, pending)) { Unknown("回執不符合原編輯指紋；結果仍未知。"); return; }
            Complete(receipt, pending);
        }
        catch (Exception)
        {
            if (Current(generation, request, _recovery)) Unknown("無法核對原請求回執；結果仍未知，不會自動重送。");
        }
        finally { if (Current(generation, request, _recovery)) Recovering = false; }
    }

    public WorkItemEditRecoveryCheckpoint? CaptureCheckpoint() => _disposed ? null : _pending?.Checkpoint;

    public bool RestoreCheckpoint(WorkItemEditRecoveryCheckpoint checkpoint)
    {
        if (_disposed || !ValidBinding || Saving || Recovering || HasDraft || checkpoint is null ||
            checkpoint.IdentityKey != IdentityKey || checkpoint.WorkItemId != WorkItemId || !ProjectContext.Matches(checkpoint.ProjectId, ProjectId))
            return false;
        WorkItemEditRecoveryRequest recovery;
        try { recovery = WorkItemEditRecovery.Normalize(Recovery(checkpoint)); }
        catch (ArgumentException) { return false; }
        var normalized = checkpoint with { ProjectId = recovery.ProjectId, RequestId = recovery.RequestId, ExpectedRequestHash = recovery.ExpectedRequestHash };
        if (_pending is not null && !SameCheckpoint(_pending.Checkpoint, normalized)) return false;
        _pending = new(normalized, null, false, null); _unresolved[Key] = normalized;
        OutcomeUnknown = true; LastReceipt = null; Error = "已還原原請求核對資料；尚未確認儲存結果。";
        return true;
    }

    private async Task ReadAsync(bool comparison)
    {
        if (_disposed || !ValidBinding || Saving || Recovering || (comparison && OutcomeUnknown)) return;
        Cancel(ref _read);
        var request = _read = new(); var generation = _generation;
        Loading = true; Error = null;
        if (comparison) Comparison = null; else Base = null;
        try
        {
            var result = await api.GetWorkItemForEditAsync(ProjectId, WorkItemId, request.Token);
            if (!Current(generation, request, _read)) return;
            if (!ValidRow(result)) { Error = "目前範圍的待辦資料不可用。"; return; }
            if (comparison) Comparison = result; else Base = result;
            if (result!.Revision is not > 0) Error = "讀取未提供有效版本；不會以預設版本代填或儲存。";
        }
        catch (Exception)
        {
            if (Current(generation, request, _read)) Error = "無法讀取目前範圍的待辦；草稿已保留。";
        }
        finally { if (Current(generation, request, _read)) Loading = false; }
    }

    private void Complete(WorkItemEditReceipt receipt, PendingEdit pending)
    {
        LastReceipt = receipt;
        // A separately obtained newer read may be retained, but server conflict counters
        // never become a base version. The receipt itself is only its original snapshot.
        if (Base?.Revision is not > 0 || Base.Revision <= receipt.Revision) Base = receipt.Result;
        if (pending.ClearDraft) { Draft = new(); HasDraft = false; }
        Comparison = null; ConflictCode = Error = null; ServerRevision = null;
        OutcomeUnknown = false; Forget(pending);
    }

    private bool Matches(WorkItemEditReceipt? receipt, PendingEdit pending)
        => receipt is not null && receipt.ReceiptId != Guid.Empty && receipt.WorkItemId == WorkItemId &&
           ProjectContext.Matches(receipt.ProjectId, ProjectId) && receipt.RequestId == pending.Checkpoint.RequestId &&
           receipt.RequestHash == pending.Checkpoint.ExpectedRequestHash && receipt.HashVersion == pending.Checkpoint.HashVersion &&
           (!pending.Operation.HasValue || receipt.Operation == pending.Operation.Value) && receipt.Revision > 0 &&
           receipt.PreviousRevision > 0 && (!pending.OriginalRevision.HasValue || receipt.PreviousRevision == pending.OriginalRevision.Value) &&
           Enum.IsDefined(receipt.Operation) &&
           (receipt.Changed ? receipt.HistoryId is not null && receipt.HistoryId != Guid.Empty &&
               receipt.PreviousRevision < long.MaxValue && receipt.Revision == receipt.PreviousRevision + 1
               : receipt.HistoryId is null && receipt.Revision == receipt.PreviousRevision) &&
           receipt.Result is not null && ValidRow(receipt.Result) && receipt.Result.Revision == receipt.Revision;

    private void Unknown(string message) { OutcomeUnknown = true; Error = message; }
    private void Forget(PendingEdit pending)
    {
        var key = new BindingKey(pending.Checkpoint.IdentityKey, ProjectContext.IdentityKey(pending.Checkpoint.ProjectId), pending.Checkpoint.WorkItemId);
        if (_unresolved.TryGetValue(key, out var checkpoint) && SameCheckpoint(checkpoint, pending.Checkpoint)) _unresolved.Remove(key);
        if (ReferenceEquals(_pending, pending)) _pending = null;
    }
    private bool ValidRow(ProjectWorkItemResult? result) => result is not null && result.Id == WorkItemId && ProjectContext.Matches(result.ProjectId, ProjectId);
    private bool ValidBinding => IdentityKey.Length > 0 && ProjectId.Length > 0 && WorkItemId != Guid.Empty &&
        !ProjectContext.IsShared(ProjectId) && !ProjectContext.IsUser(ProjectId) && ProjectId != ProjectContext.AllProjectIdsSentinel;
    private BindingKey Key => new(IdentityKey, ProjectContext.IdentityKey(ProjectId), WorkItemId);
    private bool Current(long generation, CancellationTokenSource request, CancellationTokenSource? active)
        => !_disposed && generation == _generation && ReferenceEquals(request, active);
    private static WorkItemEditRecoveryRequest Recovery(WorkItemEditRecoveryCheckpoint checkpoint)
        => new(checkpoint.ProjectId, checkpoint.WorkItemId, checkpoint.RequestId, checkpoint.ExpectedRequestHash, checkpoint.HashVersion);
    private static bool SameCheckpoint(WorkItemEditRecoveryCheckpoint left, WorkItemEditRecoveryCheckpoint right)
        => left.IdentityKey == right.IdentityKey && ProjectContext.Matches(left.ProjectId, right.ProjectId) &&
           left.WorkItemId == right.WorkItemId && left.RequestId == right.RequestId &&
           left.ExpectedRequestHash == right.ExpectedRequestHash && left.HashVersion == right.HashVersion;
    private static string KnownConflict(string code) => code is SafeEditConflictCodes.MissingReadVersion or SafeEditConflictCodes.StaleReadVersion or
        SafeEditConflictCodes.RequestIdentityMismatch or SafeEditConflictCodes.RequiresReopen ? code : "EditConflict";
    private static bool DefiniteRejection(HttpStatusCode? status) => status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.PreconditionFailed or HttpStatusCode.UnprocessableEntity;
    private static void Cancel(ref CancellationTokenSource? request) { request?.Cancel(); request?.Dispose(); request = null; }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true; _generation++;
        Cancel(ref _read); Cancel(ref _write); Cancel(ref _recovery);
        Base = Comparison = null; Draft = new(); HasDraft = false; LastReceipt = null;
        Loading = Saving = Recovering = OutcomeUnknown = false;
        ConflictCode = Error = null; ServerRevision = null; _pending = null; _unresolved.Clear();
    }

    private sealed record PendingEdit(WorkItemEditRecoveryCheckpoint Checkpoint, WorkItemEditOperation? Operation, bool ClearDraft, long? OriginalRevision);
    private readonly record struct BindingKey(string IdentityKey, string ProjectKey, Guid WorkItemId);
}

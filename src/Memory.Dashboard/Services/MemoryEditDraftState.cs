using System.Net;
using Memory.Application;
using Memory.Domain;

namespace Memory.Dashboard.Services;

internal sealed class MemoryEditDraftState(IMemoryEditingApi api, Func<string>? requestIdFactory = null) : IDisposable
{
    private readonly Func<string> _requestId = requestIdFactory ?? (() => Guid.NewGuid().ToString("N"));
    private readonly Dictionary<SelectionKey, PendingEdit> _unresolved = [];
    private CancellationTokenSource? _request;
    private MemoryDocument? _selection;
    private PendingEdit? _pending;
    private MemoryEditCommand? _submission;
    private string _identity = string.Empty;
    private bool _disposed;

    public long SelectionGeneration { get; private set; }
    public MemoryDocument? Base { get; private set; }
    public MemoryDocument? Comparison { get; private set; }
    public MemoryEditPatch Draft { get; private set; } = new();
    public MemoryEditReceipt? LastReceipt { get; private set; }
    public bool IsEditable => _selection is not null;
    public bool Busy => _request is not null;
    public bool OutcomeUnknown => _pending is not null && !Busy;
    public bool HasDraft => Draft != new MemoryEditPatch();
    public bool CanChangeDraft => Base is not null && !Busy && _pending is null;
    public bool CanApply => CanChangeDraft && HasDraft && ConflictCode is null;
    public string? ConflictCode { get; private set; }
    public long? ServerVersion { get; private set; }
    public string? Message { get; private set; }

    public void Bind(string subjectKey, MemoryDocument? selected)
    {
        if (_disposed) return;
        var eligible = subjectKey.Length > 0 && IsEditableDocument(selected) ? selected : null;
        if (subjectKey == _identity && SameSelection(_selection, eligible)) return;

        SelectionGeneration++;
        CancelRequest();
        if (subjectKey != _identity) _unresolved.Clear();
        _identity = subjectKey;
        _selection = eligible;
        ClearEditor();
        LastReceipt = null;
        _pending = eligible is not null && _unresolved.TryGetValue(Key(eligible), out var pending) ? pending : null;
        if (_pending is not null) Message = "原儲存結果尚未確認，請核對原請求的回執。";
    }

    public Task OpenAsync() => Base is null ? ReadAsync(compare: false) : Task.CompletedTask;
    public Task ReloadForComparisonAsync() => Base is not null && ConflictCode is not null
        ? ReadAsync(compare: true) : Task.CompletedTask;

    public void SetDraft(MemoryEditPatch patch)
    {
        if (!CanChangeDraft) return;
        var updated = ChangesFrom(patch, Base!);
        if (updated.Title == Draft.Title && updated.Content == Draft.Content && updated.Summary == Draft.Summary &&
            SameTags(updated.Tags, Draft.Tags)) return;
        Draft = updated;
        _submission = null;
    }

    public void AdoptComparison()
    {
        if (!CanChangeDraft || Comparison is null) return;
        Base = Comparison;
        Draft = ChangesFrom(Draft, Comparison);
        _submission = null;
        Comparison = null;
        ConflictCode = Message = null;
        ServerVersion = null;
    }

    public void Close()
    {
        if (!Busy && _pending is null) ClearEditor();
    }

    public async Task ApplyAsync()
    {
        if (!CanApply || _selection is null) return;
        MemoryEditCommand command;
        PendingEdit pending;
        try
        {
            command = _submission ??= MemoryEditContract.Normalize(new MemoryEditCommand(Base!.ProjectId, Base.Id, Base.Version, _requestId(), Draft));
            var recovery = new MemoryEditRecoveryRequest(command.ProjectId, command.MemoryId, command.RequestId, MemoryEditContract.Hash(command));
            pending = new(Key(_selection), recovery, command.ExpectedVersion);
        }
        catch (Exception error) when (error is ArgumentException or MemoryScoreValidationException or SafeEditConflictException)
        {
            Message = "無法建立有效編輯。請檢查草稿後再儲存。";
            return;
        }

        var request = BeginRequest("正在儲存編輯。");
        LastReceipt = null;
        _pending = pending;
        _unresolved[pending.Key] = pending;
        try
        {
            var receipt = await api.ApplyMemoryEditAsync(command, request.Token);
            if (Current(request)) ConfirmReceipt(receipt, pending);
        }
        catch (SafeEditConflictException error) when (error.Code is SafeEditConflictCodes.StaleReadVersion or
            SafeEditConflictCodes.MissingReadVersion or SafeEditConflictCodes.RequestIdentityMismatch)
        {
            if (!Current(request)) return;
            Forget(pending);
            ConflictCode = error.Code;
            ServerVersion = error.Code == SafeEditConflictCodes.StaleReadVersion ? error.CurrentRevision : null;
            Message = "儲存發生衝突，草稿已保留。請重新讀取並比較目前版本。";
        }
        catch (NotSupportedException)
        {
            if (!Current(request)) return;
            Forget(pending);
            Message = "此伺服器尚未提供版本化編輯。草稿已保留。";
        }
        catch (HttpRequestException error) when (DefiniteRejection(error.StatusCode))
        {
            if (!Current(request)) return;
            Forget(pending);
            Message = "伺服器拒絕此編輯。請確認權限或欄位，草稿已保留。";
        }
        catch (Exception)
        {
            if (Current(request)) Message = "儲存結果未知。請核對原請求的回執。";
        }
        finally { FinishRequest(request); }
    }

    public async Task RecoverAsync()
    {
        if (!OutcomeUnknown || _pending is not { } pending) return;
        var request = BeginRequest("正在核對原請求的回執。");
        try
        {
            var receipt = await api.RecoverMemoryEditAsync(pending.Recovery, request.Token);
            if (Current(request)) ConfirmReceipt(receipt, pending);
        }
        catch (Exception)
        {
            if (Current(request)) Message = "無法核對原請求的回執。結果仍未知。";
        }
        finally { FinishRequest(request); }
    }

    private async Task ReadAsync(bool compare)
    {
        if (_selection is not { } selected || Busy || _pending is not null) return;
        var request = BeginRequest("正在讀取目前版本。");
        if (compare) Comparison = null;
        try
        {
            var document = await api.GetMemoryForEditAsync(selected.ProjectId, selected.Id, request.Token);
            if (!Current(request)) return;
            if (!IsEditableDocument(document) || !SameSelection(document, selected) ||
                (compare && (document!.Version < Base!.Version || document.Version < ServerVersion)))
            {
                Message = "目前範圍或版本無法確認。草稿已保留。";
                return;
            }

            if (compare) Comparison = document;
            else Base = document;
            Message = null;
        }
        catch (Exception)
        {
            if (Current(request)) Message = "無法讀取可編輯的記憶。";
        }
        finally { FinishRequest(request); }
    }

    private static MemoryEditPatch ChangesFrom(MemoryEditPatch patch, MemoryDocument original)
        => new(
            Title: patch.Title != original.Title ? patch.Title : null,
            Content: patch.Content != original.Content ? patch.Content : null,
            Summary: patch.Summary != original.Summary ? patch.Summary : null,
            Tags: patch.Tags is { } tags && !tags.SequenceEqual(original.Tags, StringComparer.Ordinal)
                ? Array.AsReadOnly(tags.ToArray()) : null);

    private static bool SameTags(IReadOnlyList<string>? left, IReadOnlyList<string>? right)
        => left is null ? right is null : right is not null && left.SequenceEqual(right, StringComparer.Ordinal);

    private void ConfirmReceipt(MemoryEditReceipt? receipt, PendingEdit pending)
    {
        var original = pending.Recovery;
        if (receipt is null || receipt.ReceiptId == Guid.Empty || receipt.RevisionId == Guid.Empty ||
            receipt.MemoryId != original.MemoryId || !ProjectContext.Matches(receipt.ProjectId, original.ProjectId) ||
            receipt.RequestId != original.RequestId || receipt.RequestHash != original.ExpectedRequestHash ||
            receipt.HashVersion != original.HashVersion || receipt.PreviousVersion != pending.ExpectedVersion ||
            receipt.Version <= receipt.PreviousVersion || receipt.Version - receipt.PreviousVersion != 1)
        {
            Message = "尚未確認原請求的回執，結果仍未知。";
            return;
        }

        Forget(pending);
        ClearEditor();
        LastReceipt = receipt;
        Message = $"已確認編輯回執，提交版本 {receipt.Version}。";
    }

    private void Forget(PendingEdit pending)
    {
        _unresolved.Remove(pending.Key);
        _pending = null;
    }

    private void ClearEditor()
    {
        Base = Comparison = null;
        Draft = new();
        _submission = null;
        Message = ConflictCode = null;
        ServerVersion = null;
    }

    private CancellationTokenSource BeginRequest(string message)
    {
        Message = message;
        return _request = new();
    }

    private bool Current(CancellationTokenSource request) => !_disposed && ReferenceEquals(_request, request);
    private void FinishRequest(CancellationTokenSource request)
    {
        if (!Current(request)) return;
        _request = null;
        request.Dispose();
    }

    private void CancelRequest()
    {
        var request = _request;
        _request = null;
        request?.Cancel();
        request?.Dispose();
    }

    private SelectionKey Key(MemoryDocument document)
        => new(_identity, ProjectContext.IdentityKey(document.ProjectId), document.Id);

    private static bool SameSelection(MemoryDocument? left, MemoryDocument? right)
        => left is null ? right is null : right is not null && left.Id == right.Id &&
            ProjectContext.Matches(left.ProjectId, right.ProjectId);

    private static bool IsEditableDocument(MemoryDocument? document)
        => document is not null && document.Id != Guid.Empty && document.Version > 0 && !document.IsReadOnly &&
            document.MemoryType != MemoryType.Preference && !string.IsNullOrWhiteSpace(document.ProjectId) &&
            !ProjectContext.IsShared(document.ProjectId) && !ProjectContext.IsUser(document.ProjectId) &&
            document.ProjectId != ProjectContext.AllProjectIdsSentinel;

    private static bool DefiniteRejection(HttpStatusCode? status) => status is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or
        HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed or HttpStatusCode.UnprocessableEntity;

    public void Dispose()
    {
        _disposed = true;
        SelectionGeneration++;
        CancelRequest();
        ClearEditor();
        _unresolved.Clear();
        _pending = null;
        _selection = null;
        LastReceipt = null;
    }

    private readonly record struct SelectionKey(string SubjectKey, string ProjectKey, Guid MemoryId);
    private sealed record PendingEdit(SelectionKey Key, MemoryEditRecoveryRequest Recovery, int ExpectedVersion);
}

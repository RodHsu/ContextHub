using System.Net;
using Memory.Application;
using Memory.Domain;

namespace Memory.Dashboard.Services;

public sealed class FileMetadataReadState(IContextHubApiClient api) : IDisposable
{
    public const int PageSize = 25;
    private CancellationTokenSource? _pageRequest;
    private CancellationTokenSource? _selectionRequest;
    private CancellationTokenSource? _contentRequest;
    private CancellationTokenSource? _auditRequest;
    private bool _disposed;
    public string ProjectId { get; private set; } = string.Empty;
    public IReadOnlyList<ManagedFileInventoryResult> Files { get; private set; } = [];
    public ManagedFileInventoryResult? Selected { get; private set; }
    public ManagedFileInventoryResult? Version { get; private set; }
    public ManagedFileMetadataPage? Versions { get; private set; }
    public EffectiveRightsResult? Rights { get; private set; }
    public StepUpAuthorizationResult? ReleaseRequirement { get; private set; }
    public int Offset { get; private set; }
    public int VersionsOffset { get; private set; }
    public bool HasMore { get; private set; }
    public bool Legacy { get; private set; }
    public bool Loading { get; private set; }
    public bool PermissionDenied { get; private set; }
    public bool SelectionLoading { get; private set; }
    public string? Error { get; private set; }
    public string? SelectionError { get; private set; }
    public bool DetailUnavailable { get; private set; }
    public bool PinnedVersion { get; private set; }
    public Guid? RequestedVersionId { get; private set; }
    public DateTimeOffset? LastUpdatedAt { get; private set; }
    public ManagedFileContentMetadata? ContentMetadata { get; private set; }
    public ManagedFileAuditPage? Audit { get; private set; }
    public bool ContentLoading { get; private set; }
    public bool AuditLoading { get; private set; }
    public string? ContentError { get; private set; }
    public string? AuditError { get; private set; }

    public async Task ReadContentMetadataAsync(string purpose)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Cancel(ref _contentRequest);
        ContentMetadata = null; ContentError = null; ContentLoading = false;
        if (Version is not { } version) return;
        if (string.IsNullOrWhiteSpace(purpose) || purpose.Trim().Length > 200)
        { ContentError = "請輸入不含秘密的明確用途（最多 200 字）。"; return; }
        var project = ProjectId;
        var selection = _selectionRequest;
        var request = _contentRequest = new();
        var token = request.Token;
        ContentLoading = true;
        try
        {
            var result = await api.ReadFileContentMetadataAsync(project, version.FileId, version.FileVersionId, purpose, token);
            if (!CurrentDisclosure(request, _contentRequest, selection, project, version)) return;
            if (result is null || result.FileId != version.FileId || result.FileVersionId != version.FileVersionId)
                ContentError = "大小與雜湊不可用：尚未通過目前版本的下載授權，或服務未提供此資料。";
            else ContentMetadata = result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        { if (CurrentDisclosure(request, _contentRequest, selection, project, version)) ContentError = "大小與雜湊讀取未完成，請確認用途與下載權限。"; }
        finally { if (CurrentDisclosure(request, _contentRequest, selection, project, version)) ContentLoading = false; }
    }

    public async Task ReadAuditAsync(int offset = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        Cancel(ref _auditRequest);
        Audit = null; AuditError = null; AuditLoading = false;
        if (Version is not { } version) return;
        var project = ProjectId;
        var selection = _selectionRequest;
        var request = _auditRequest = new();
        var token = request.Token;
        AuditLoading = true;
        try
        {
            var result = await api.ReadFileAuditAsync(project, version.FileId, version.FileVersionId, PageSize, offset, token);
            if (!CurrentDisclosure(request, _auditRequest, selection, project, version)) return;
            if (result is null || result.FileId != version.FileId || result.FileVersionId != version.FileVersionId)
                AuditError = "稽核資料不可用；不代表沒有稽核紀錄。";
            else Audit = result;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        { if (CurrentDisclosure(request, _auditRequest, selection, project, version)) AuditError = "稽核讀取未完成；需要既有稽核讀取資格。"; }
        finally { if (CurrentDisclosure(request, _auditRequest, selection, project, version)) AuditLoading = false; }
    }

    private bool CurrentDisclosure(CancellationTokenSource request, CancellationTokenSource? active,
        CancellationTokenSource? selection, string project, ManagedFileInventoryResult version)
        => Current(request, active) && ReferenceEquals(selection, _selectionRequest) && ProjectContext.Matches(project, ProjectId)
            && Version?.FileId == version.FileId && Version.FileVersionId == version.FileVersionId;

    public async Task LoadAsync(string projectId, int offset = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        Cancel(ref _pageRequest);
        Cancel(ref _selectionRequest);
        var request = _pageRequest = new();
        var token = request.Token;
        var project = projectId.Trim();
        var samePage = string.Equals(ProjectId, project, StringComparison.Ordinal) && Offset == offset;
        var cachedFiles = samePage ? Files : [];
        var cachedAt = samePage ? LastUpdatedAt : null;
        var cachedHasMore = HasMore;
        var cachedLegacy = Legacy;
        var selectedId = samePage ? Selected?.FileId : null;
        ProjectId = project;
        ClearInventory();
        Offset = offset;
        PermissionDenied = Loading = false;
        Error = null;
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        Loading = true;
        try
        {
            var page = await api.GetManagedFilePageAsync(project, PageSize, offset, token);
            if (!Current(request, _pageRequest)) return;
            var files = page?.Items ?? await api.GetManagedFilesAsync(project, PageSize, token);
            if (!Current(request, _pageRequest)) return;
            Legacy = page is null;
            Offset = Legacy ? 0 : page!.Offset;
            HasMore = page?.HasMore ?? false;
            Files = files;
            LastUpdatedAt = DateTimeOffset.UtcNow;
            var selected = Files.FirstOrDefault(file => file.FileId == selectedId) ?? Files.FirstOrDefault();
            if (selected is not null) await SelectAsync(selected);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            if (Current(request, _pageRequest)) DenyInventory();
        }
        catch (Exception exception)
        {
            if (!Current(request, _pageRequest)) return;
            var httpFailure = ContextHubApiClient.GetInventoryHttpFailure(exception);
            if (cachedAt.HasValue && httpFailure?.StatusCode is { } status && (int)status is >= 500 and <= 599)
            {
                Files = cachedFiles;
                Selected = Files.FirstOrDefault(file => file.FileId == selectedId);
                LastUpdatedAt = cachedAt;
                Legacy = cachedLegacy;
                HasMore = cachedHasMore;
            }
            Error = httpFailure is not null
                ? DashboardUiErrorFormatter.BuildLoadError("受管檔案", httpFailure, Files.Count > 0)
                : "無法載入受管檔案，請確認權限或稍後重試。";
        }
        finally { if (Current(request, _pageRequest)) Loading = false; }
    }

    public async Task SelectAsync(ManagedFileInventoryResult file, Guid? versionId = null, int versionsOffset = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (versionsOffset < 0) throw new ArgumentOutOfRangeException(nameof(versionsOffset));
        if (PermissionDenied || Error is not null || !Files.Any(x => x.FileId == file.FileId)) return;
        Cancel(ref _selectionRequest);
        var request = _selectionRequest = new();
        var token = request.Token;
        var project = ProjectId;
        ClearSelection();
        Selected = file;
        SelectionLoading = true;
        VersionsOffset = versionsOffset;
        PinnedVersion = versionId.HasValue;
        RequestedVersionId = versionId;
        try
        {
            var latest = await api.GetManagedFileMetadataAsync(project, file.FileId, token);
            if (!Current(request, _selectionRequest)) return;
            var selected = latest ?? file;
            if ((latest is null && !Legacy) || !MatchesFile(selected, project, file.FileId))
            {
                DetailUnavailable = true;
                return;
            }
            var version = versionId.HasValue
                ? await api.GetManagedFileVersionAsync(project, file.FileId, versionId.Value, token)
                : selected;
            if (!Current(request, _selectionRequest)) return;
            if (versionId.HasValue && version is not null &&
                (!MatchesFile(version, project, file.FileId) || version.FileVersionId != versionId.Value))
            {
                DetailUnavailable = true;
                return;
            }
            var versions = await api.GetManagedFileVersionsAsync(project, file.FileId, PageSize, versionsOffset, token);
            if (!Current(request, _selectionRequest)) return;
            var rights = await api.GetEffectiveRightsAsync(project, ["metadata", "read", "download", "searchindex", "securityrelease", "delete"], "File", file.FileId.ToString("D"), token);
            if (!Current(request, _selectionRequest)) return;
            StepUpAuthorizationResult? requirement = version is null ? null : await api.GetStepUpRequirementAsync(
                StepUpOperationClass.RestrictedRelease, "file:quarantine-release", "FileVersion", version.FileVersionId.ToString("D"), token);
            if (!Current(request, _selectionRequest)) return;
            Selected = selected;
            Version = version;
            Versions = versions;
            VersionsOffset = versions?.Offset ?? 0;
            Rights = rights;
            ReleaseRequirement = requirement;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            if (Current(request, _selectionRequest)) DenyInventory();
        }
        catch (Exception)
        {
            if (Current(request, _selectionRequest)) SelectionError = "無法載入檔案詳情或權限；請重新選取以重試。";
        }
        finally { if (Current(request, _selectionRequest)) SelectionLoading = false; }
    }

    private static bool MatchesFile(ManagedFileInventoryResult result, string projectId, Guid fileId)
        => result.FileId == fileId && ProjectContext.Matches(result.ProjectId, projectId);

    private bool Current(CancellationTokenSource request, CancellationTokenSource? active)
        => !_disposed && ReferenceEquals(request, active);

    private static bool IsAccessDenied(Exception exception)
        => ContextHubApiClient.GetInventoryHttpFailure(exception)?.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden;

    private void DenyInventory()
    {
        Cancel(ref _selectionRequest);
        ClearInventory();
        PermissionDenied = true;
        Error = null;
    }

    private void ClearInventory()
    {
        Files = [];
        HasMore = Legacy = false;
        LastUpdatedAt = null;
        ClearSelection();
    }

    private void ClearSelection()
    {
        Cancel(ref _contentRequest);
        Cancel(ref _auditRequest);
        ContentMetadata = null; Audit = null;
        ContentError = AuditError = null;
        ContentLoading = AuditLoading = false;
        Selected = Version = null;
        Versions = null;
        Rights = null;
        ReleaseRequirement = null;
        SelectionError = null;
        SelectionLoading = DetailUnavailable = PinnedVersion = false;
        VersionsOffset = 0;
        RequestedVersionId = null;
    }

    private static void Cancel(ref CancellationTokenSource? request)
    {
        request?.Cancel();
        request?.Dispose();
        request = null;
    }

    public void Dispose()
    {
        _disposed = true;
        Cancel(ref _pageRequest);
        Cancel(ref _selectionRequest);
        ClearInventory();
        Loading = false;
    }
}

using Memory.Application;
using Memory.Domain;

namespace Memory.Dashboard.Services;

public sealed class FileMetadataReadState(IContextHubApiClient api) : IDisposable
{
    public const int PageSize = 25;
    private CancellationTokenSource? _pageRequest;
    private CancellationTokenSource? _selectionRequest;
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
    public bool SelectionLoading { get; private set; }
    public string? Error { get; private set; }
    public string? SelectionError { get; private set; }
    public bool DetailUnavailable { get; private set; }
    public bool PinnedVersion { get; private set; }
    public Guid? RequestedVersionId { get; private set; }
    public DateTimeOffset? LastUpdatedAt { get; private set; }

    public async Task LoadAsync(string projectId, int offset = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0) throw new ArgumentOutOfRangeException(nameof(offset));
        Cancel(ref _pageRequest);
        Cancel(ref _selectionRequest);
        var request = _pageRequest = new();
        var token = request.Token;
        ProjectId = projectId.Trim();
        Files = [];
        Offset = offset;
        HasMore = Legacy = Loading = false;
        Error = null;
        LastUpdatedAt = null;
        ClearSelection();
        if (string.IsNullOrWhiteSpace(ProjectId)) return;
        Loading = true;
        try
        {
            var project = ProjectId;
            var page = await api.GetManagedFilePageAsync(project, PageSize, offset, token);
            if (!Current(request, _pageRequest)) return;
            var files = page?.Items ?? await api.GetManagedFilesAsync(project, PageSize, token);
            if (!Current(request, _pageRequest)) return;
            Legacy = page is null;
            Offset = Legacy ? 0 : page!.Offset;
            HasMore = page?.HasMore ?? false;
            Files = files;
            LastUpdatedAt = DateTimeOffset.UtcNow;
            if (Files.FirstOrDefault() is { } first) await SelectAsync(first);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception)
        {
            if (Current(request, _pageRequest)) Error = "無法載入受管檔案，請確認權限或稍後重試。";
        }
        finally { if (Current(request, _pageRequest)) Loading = false; }
    }

    public async Task SelectAsync(ManagedFileInventoryResult file, Guid? versionId = null, int versionsOffset = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (versionsOffset < 0) throw new ArgumentOutOfRangeException(nameof(versionsOffset));
        if (!Files.Any(x => x.FileId == file.FileId)) return;
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
            if (latest is null && !Legacy)
            {
                DetailUnavailable = true;
                return;
            }
            var selected = latest ?? file;
            var version = versionId.HasValue
                ? await api.GetManagedFileVersionAsync(project, file.FileId, versionId.Value, token)
                : selected;
            if (!Current(request, _selectionRequest)) return;
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
        catch (Exception)
        {
            if (Current(request, _selectionRequest)) SelectionError = "無法載入檔案詳情或權限；請重新選取以重試。";
        }
        finally { if (Current(request, _selectionRequest)) SelectionLoading = false; }
    }

    private bool Current(CancellationTokenSource request, CancellationTokenSource? active)
        => !_disposed && ReferenceEquals(request, active);

    private void ClearSelection()
    {
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
        Files = [];
        ClearSelection();
        Loading = false;
    }
}

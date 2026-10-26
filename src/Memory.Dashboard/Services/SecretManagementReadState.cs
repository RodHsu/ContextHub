using Memory.Application;

namespace Memory.Dashboard.Services;

public enum SecretManagementTab { Expiry, Leases, History }

public sealed class SecretManagementReadState(IContextHubApiClient api) : IDisposable
{
    private CancellationTokenSource? _request;
    private bool _disposed;
    public string ProjectId { get; private set; } = string.Empty;
    public Guid SecretId { get; private set; }
    public long Revision { get; private set; }
    public SecretManagementTab Tab { get; private set; }
    public SecretManagementExpiry? Expiry { get; private set; }
    public SecretManagementLeasePage? Leases { get; private set; }
    public SecretManagementHistoryPage? History { get; private set; }
    public bool Loading { get; private set; }
    public bool Requested { get; private set; }
    public string? Error { get; private set; }
    public int Offset { get; private set; }
    public bool HasMore => Leases?.HasMore ?? History?.HasMore ?? false;
    public const int PageSize = 25;

    public void Bind(string projectId, Guid secretId, long revision)
    {
        if (_disposed || (ProjectContext.Matches(ProjectId, projectId) && SecretId == secretId && Revision == revision)) return;
        Cancel(); Clear();
        ProjectId = projectId.Trim(); SecretId = secretId; Revision = revision;
        Requested = false; Offset = 0;
    }

    public async Task LoadAsync(SecretManagementTab tab, int offset = 0)
    {
        if (_disposed || string.IsNullOrWhiteSpace(ProjectId) || SecretId == Guid.Empty) return;
        Cancel(); Clear();
        var project = ProjectId; var secret = SecretId;
        var request = _request = new(); var token = request.Token;
        Tab = tab; Offset = Math.Max(0, offset); Requested = Loading = true;
        try
        {
            switch (tab)
            {
                case SecretManagementTab.Expiry:
                    var expiry = await api.GetSecretManagementExpiryAsync(project, secret, token);
                    if (!Current(request)) return;
                    if (expiry is null || expiry.SecretId != secret) throw new InvalidOperationException();
                    Expiry = expiry; break;
                case SecretManagementTab.Leases:
                    var leases = await api.GetSecretManagementLeasesAsync(project, secret, PageSize, Offset, token);
                    if (!Current(request)) return;
                    if (leases is null || leases.SecretId != secret || leases.Offset != Offset || leases.Limit != PageSize) throw new InvalidOperationException();
                    Leases = leases; break;
                case SecretManagementTab.History:
                    var history = await api.GetSecretManagementHistoryAsync(project, secret, PageSize, Offset, token);
                    if (!Current(request)) return;
                    if (history is null || history.SecretId != secret || history.Offset != Offset || history.Limit != PageSize) throw new InvalidOperationException();
                    History = history; break;
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception) { if (Current(request)) Error = "管理資訊無法取得。請確認既有管理讀取權限，或稍後重試。"; }
        finally { if (Current(request)) Loading = false; }
    }

    private bool Current(CancellationTokenSource request) => !_disposed && ReferenceEquals(request, _request);
    private void Clear() { Expiry = null; Leases = null; History = null; Error = null; Loading = false; }
    private void Cancel() { _request?.Cancel(); _request?.Dispose(); _request = null; }
    public void Dispose() { _disposed = true; Cancel(); Clear(); Requested = false; ProjectId = string.Empty; SecretId = Guid.Empty; }
}

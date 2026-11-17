using Memory.Application;

namespace Memory.Dashboard.Services;

public sealed class CanonicalTagQualityState : IDisposable
{
    private CancellationTokenSource? _request;
    private bool _disposed;

    public IReadOnlyList<CanonicalTagQualityWindow> Rows { get; private set; } = [];
    public bool Loading { get; private set; }
    public bool Unsupported { get; private set; }
    public string? Error { get; private set; }

    public async Task LoadAsync(string projectId, int days,
        Func<string, int, CancellationToken, Task<IReadOnlyList<CanonicalTagQualityWindow>?>> load)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _request?.Cancel();
        _request?.Dispose();
        var request = _request = new CancellationTokenSource();
        var token = request.Token;
        Rows = [];
        Error = null;
        Unsupported = false;
        Loading = false;
        if (string.IsNullOrWhiteSpace(projectId)) return;
        if (days is not 7 and not 30 and not 90) throw new ArgumentOutOfRangeException(nameof(days));
        Loading = true;
        try
        {
            var rows = await load(projectId.Trim(), days, token);
            if (_disposed || !ReferenceEquals(request, _request)) return;
            Unsupported = rows is null;
            Rows = rows ?? [];
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (!_disposed && ReferenceEquals(request, _request))
                Error = "無法載入標籤品質；請確認存取權限或稍後重試。";
        }
        finally
        {
            if (!_disposed && ReferenceEquals(request, _request)) Loading = false;
        }
    }

    public static string Rate(decimal value, long denominator)
        => denominator <= 0 ? "未知（無樣本）" : DashboardFormatting.Percent((double)(value * 100m));

    public void Dispose()
    {
        _disposed = true;
        _request?.Cancel();
        _request?.Dispose();
        _request = null;
        Rows = [];
        Loading = false;
    }
}

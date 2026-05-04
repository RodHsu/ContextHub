using System.Net;
using Memory.Application;

namespace Memory.Dashboard.Services;

internal static class DashboardUiErrorFormatter
{
    public static string? BuildSnapshotWarning(DashboardPageSnapshotStatusResult? snapshotStatus, string subject)
    {
        if (snapshotStatus is null)
        {
            return null;
        }

        if (!snapshotStatus.IsStale)
        {
            return string.IsNullOrWhiteSpace(snapshotStatus.Warning)
                ? null
                : snapshotStatus.Warning;
        }

        var age = DateTimeOffset.UtcNow - snapshotStatus.SnapshotAtUtc;
        if (!string.IsNullOrWhiteSpace(snapshotStatus.Warning))
        {
            return snapshotStatus.Warning;
        }

        var ageSeconds = Math.Max(1, (int)Math.Floor(age.TotalSeconds));
        return $"{subject}資料延遲 {ageSeconds} 秒，畫面顯示的是最後一份可用資料。";
    }

    public static string BuildLoadError(string subject, Exception exception, bool hasCachedData)
    {
        var prefix = hasCachedData
            ? $"{subject} 刷新失敗，保留最後一份可用資料。"
            : $"{subject} 載入失敗。";

        return $"{prefix} {Describe(exception)}";
    }

    public static string BuildActionError(string action, Exception exception)
        => $"{action}失敗：{Describe(exception)}";

    private static string Describe(Exception exception)
        => exception switch
        {
            HttpRequestException httpException when httpException.StatusCode is HttpStatusCode statusCode
                => $"API 回應 {(int)statusCode} {statusCode}。",
            HttpRequestException
                => "無法連線到 dashboard API。",
            TaskCanceledException
                => "要求逾時。",
            InvalidOperationException invalidOperation when invalidOperation.Message.Contains("empty payload", StringComparison.OrdinalIgnoreCase)
                => "API 回傳空白內容。",
            _ => string.IsNullOrWhiteSpace(exception.Message)
                ? "發生未預期錯誤。"
                : exception.Message.Trim().TrimEnd('.').TrimEnd('。') + "。"
        };
}

using Memory.Application;

namespace Memory.Dashboard.Services;

public static class CacheTelemetryPresentation
{
    public static CacheMetricsSeriesResult InteractiveResults(IEnumerable<CacheMetricsSeriesResult> series)
    {
        var selected = series.Where(x => x.Kind == "final-result" && x.TrafficClass == "interactive").ToArray();
        return new("final-result", "interactive", selected.Sum(x => x.Hits), selected.Sum(x => x.Misses),
            selected.Sum(x => x.Sets), selected.Sum(x => x.Bypasses), selected.Sum(x => x.Errors),
            selected.Sum(x => x.InvalidPayloads), selected.Sum(x => x.DurationMs));
    }

    public static string Rate(CacheMetricsSeriesResult series)
        => series.HitRate.HasValue ? DashboardFormatting.Percent(series.HitRate.Value) : "無樣本";

    public static string Coverage(string? value) => value switch
    {
        "Observed" => "已觀測實例正常（未保證所有副本）",
        "Partial" => "部分資料：部署、延遲、重啟或遺失",
        "Unavailable" => "尚未提供期間統計",
        _ => "覆蓋狀態未知"
    };

    public static string Kind(string value) => value switch
    {
        "final-result" => "最終結果",
        "search-final" => "搜尋結果快取",
        "working-context-final" => "工作脈絡快取",
        "semantic-hits" => "語意檢索快取",
        "embedding-query" => "查詢向量快取",
        CacheMetricKinds.OriginSearch => "搜尋回源",
        CacheMetricKinds.OriginContext => "工作脈絡重建",
        CacheMetricKinds.OriginSemantic => "向量資料庫回源",
        CacheMetricKinds.OriginEmbedding => "向量服務回源",
        _ => value
    };

    public static string Traffic(string value) => value switch
    {
        "interactive" => "互動請求",
        "graph-background" => "圖譜背景刷新",
        "application" => "應用程序",
        _ => value
    };
}

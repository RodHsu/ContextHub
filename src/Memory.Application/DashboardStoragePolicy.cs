namespace Memory.Application;

public static class DashboardStoragePolicy
{
    public const int LargeTablePreviewPageSize = 25;
    public const int LargeTableMaxPageSize = 50;
    public const string LargeTablePreviewWarning = "Large table preview is served from Redis. Add a filter to run a guarded PostgreSQL drilldown query.";
    public const string LargeTableDeepPageWarning = "Unfiltered deep paging is disabled for large telemetry tables. Add a filter or use the latest preview.";

    private static readonly HashSet<string> LargeTables = new(StringComparer.OrdinalIgnoreCase)
    {
        "retrieval_events",
        "retrieval_hits"
    };

    public static bool IsLargeTable(string table)
        => LargeTables.Contains(table);

    public static IReadOnlyList<string> LargeTableNames => LargeTables.Order(StringComparer.OrdinalIgnoreCase).ToArray();

    public static bool IsUnfiltered(string? query, string? column)
        => string.IsNullOrWhiteSpace(query) && string.IsNullOrWhiteSpace(column);

    public static bool IsLargeTablePreviewRequest(StorageRowsRequest request)
        => IsLargeTable(request.Table) && IsUnfiltered(request.Query, request.Column) && request.Page <= 1;

    public static bool IsBlockedUnfilteredLargeTablePage(StorageRowsRequest request)
        => IsLargeTable(request.Table) && IsUnfiltered(request.Query, request.Column) && request.Page > 1;
}

public sealed class StorageExplorerQueryRejectedException(string message) : Exception(message);

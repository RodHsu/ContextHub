using System.Net.Http.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.AspNetCore.WebUtilities;

namespace Memory.Dashboard.Services;

public interface IContextHubApiClient
{
    Task<SystemStatusResult> GetStatusAsync(CancellationToken cancellationToken);
    Task<DashboardOverviewResult> GetOverviewAsync(CancellationToken cancellationToken);
    Task<DashboardRuntimeResult> GetRuntimeAsync(CancellationToken cancellationToken);
    Task<DashboardMonitoringResult> GetMonitoringAsync(CancellationToken cancellationToken);
    Task<PagedResult<MemoryListItemResult>> GetMemoriesAsync(MemoryListRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectSuggestionResult>> GetMemoryProjectsAsync(string? query, int limit, CancellationToken cancellationToken);
    Task<MemoryDetailsResult?> GetMemoryDetailsAsync(Guid id, CancellationToken cancellationToken);
    Task<MemoryTransferDownloadResult> ExportMemoriesAsync(MemoryExportRequest request, CancellationToken cancellationToken);
    Task<MemoryImportPreviewResult> PreviewMemoryImportAsync(MemoryImportRequest request, CancellationToken cancellationToken);
    Task<MemoryImportApplyResult> ApplyMemoryImportAsync(MemoryImportRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<UserPreferenceResult>> GetPreferencesAsync(UserPreferenceKind? kind, bool includeArchived, int limit, CancellationToken cancellationToken);
    Task<UserPreferenceResult> UpsertPreferenceAsync(UserPreferenceUpsertRequest request, CancellationToken cancellationToken);
    Task<UserPreferenceResult> ArchivePreferenceAsync(Guid id, bool archived, CancellationToken cancellationToken);
    Task<IReadOnlyList<LogEntryResult>> SearchLogsAsync(LogQueryRequest request, CancellationToken cancellationToken);
    Task<LogEntryResult?> GetLogAsync(long id, CancellationToken cancellationToken);
    Task<PagedResult<JobListItemResult>> GetJobsAsync(JobListRequest request, CancellationToken cancellationToken);
    Task<EnqueueReindexResult> EnqueueReindexAsync(EnqueueReindexRequest request, CancellationToken cancellationToken);
    Task<EnqueueSummaryRefreshResult> EnqueueSummaryRefreshAsync(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken);
    Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken);
    Task<PerformanceMeasureResult> MeasurePerformanceAsync(PerformanceMeasureRequest request, CancellationToken cancellationToken);
}

public sealed class ContextHubApiClient(HttpClient httpClient) : IContextHubApiClient
{
    public Task<SystemStatusResult> GetStatusAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<SystemStatusResult>("/api/status", cancellationToken);

    public Task<DashboardOverviewResult> GetOverviewAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<DashboardOverviewResult>("/api/dashboard/overview", cancellationToken);

    public Task<DashboardRuntimeResult> GetRuntimeAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<DashboardRuntimeResult>("/api/dashboard/runtime", cancellationToken);

    public Task<DashboardMonitoringResult> GetMonitoringAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<DashboardMonitoringResult>("/api/dashboard/monitoring", cancellationToken);

    public Task<PagedResult<MemoryListItemResult>> GetMemoriesAsync(MemoryListRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<PagedResult<MemoryListItemResult>>(BuildMemoriesUrl(request), cancellationToken);

    public Task<IReadOnlyList<ProjectSuggestionResult>> GetMemoryProjectsAsync(string? query, int limit, CancellationToken cancellationToken)
    {
        var queryString = new Dictionary<string, string?>
        {
            ["query"] = query,
            ["limit"] = limit.ToString()
        };

        return GetRequiredAsync<IReadOnlyList<ProjectSuggestionResult>>(QueryHelpers.AddQueryString("/api/memories/projects", queryString), cancellationToken);
    }

    public async Task<MemoryDetailsResult?> GetMemoryDetailsAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/api/memories/{id}/details", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadRequiredAsync<MemoryDetailsResult>(response, cancellationToken);
    }

    public async Task<MemoryTransferDownloadResult> ExportMemoriesAsync(MemoryExportRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/memories/export", request, cancellationToken);
        return await ReadRequiredAsync<MemoryTransferDownloadResult>(response, cancellationToken);
    }

    public async Task<MemoryImportPreviewResult> PreviewMemoryImportAsync(MemoryImportRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/memories/import/preview", request, cancellationToken);
        return await ReadRequiredAsync<MemoryImportPreviewResult>(response, cancellationToken);
    }

    public async Task<MemoryImportApplyResult> ApplyMemoryImportAsync(MemoryImportRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/memories/import/apply", request, cancellationToken);
        return await ReadRequiredAsync<MemoryImportApplyResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<UserPreferenceResult>> GetPreferencesAsync(UserPreferenceKind? kind, bool includeArchived, int limit, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["includeArchived"] = includeArchived.ToString(),
            ["limit"] = limit.ToString()
        };

        if (kind.HasValue)
        {
            query["kind"] = kind.Value.ToString();
        }

        return GetRequiredAsync<IReadOnlyList<UserPreferenceResult>>(QueryHelpers.AddQueryString("/api/user/preferences", query), cancellationToken);
    }

    public async Task<UserPreferenceResult> UpsertPreferenceAsync(UserPreferenceUpsertRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/user/preferences", request, cancellationToken);
        return await ReadRequiredAsync<UserPreferenceResult>(response, cancellationToken);
    }

    public async Task<UserPreferenceResult> ArchivePreferenceAsync(Guid id, bool archived, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/api/user/preferences/{id}", new { archived }, cancellationToken);
        return await ReadRequiredAsync<UserPreferenceResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<LogEntryResult>> SearchLogsAsync(LogQueryRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<LogEntryResult>>(BuildLogsUrl(request), cancellationToken);

    public async Task<LogEntryResult?> GetLogAsync(long id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/api/logs/{id}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadRequiredAsync<LogEntryResult>(response, cancellationToken);
    }

    public Task<PagedResult<JobListItemResult>> GetJobsAsync(JobListRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<PagedResult<JobListItemResult>>(BuildJobsUrl(request), cancellationToken);

    public async Task<EnqueueReindexResult> EnqueueReindexAsync(EnqueueReindexRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/jobs/reindex", request, cancellationToken);
        return await ReadRequiredAsync<EnqueueReindexResult>(response, cancellationToken);
    }

    public async Task<EnqueueSummaryRefreshResult> EnqueueSummaryRefreshAsync(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/jobs/summary-refresh", request, cancellationToken);
        return await ReadRequiredAsync<EnqueueSummaryRefreshResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<StorageTableSummaryResult>>("/api/storage/tables", cancellationToken);

    public Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<StorageTableRowsResult>(BuildStorageRowsUrl(request), cancellationToken);

    public async Task<PerformanceMeasureResult> MeasurePerformanceAsync(PerformanceMeasureRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/performance/measure", request, cancellationToken);
        return await ReadRequiredAsync<PerformanceMeasureResult>(response, cancellationToken);
    }

    private Task<T> GetRequiredAsync<T>(string url, CancellationToken cancellationToken)
        => GetAndReadAsync<T>(url, cancellationToken);

    private async Task<T> GetAndReadAsync<T>(string url, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync(url, cancellationToken);
        return await ReadRequiredAsync<T>(response, cancellationToken);
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"ContextHub API returned an empty payload for '{typeof(T).Name}'.");
    }

    private static string BuildMemoriesUrl(MemoryListRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["scope"] = request.Scope?.ToString(),
            ["memoryType"] = request.MemoryType?.ToString(),
            ["status"] = request.Status?.ToString(),
            ["sourceType"] = request.SourceType,
            ["tag"] = request.Tag,
            ["projectId"] = request.ProjectId,
            ["projectQuery"] = request.ProjectQuery,
            ["includedProjectIds"] = request.IncludedProjectIds is null ? null : string.Join(",", request.IncludedProjectIds),
            ["queryMode"] = request.QueryMode.ToString(),
            ["useSummaryLayer"] = request.UseSummaryLayer.ToString(),
            ["page"] = request.Page.ToString(),
            ["pageSize"] = request.PageSize.ToString()
        };

        return QueryHelpers.AddQueryString("/api/memories", query);
    }

    private static string BuildJobsUrl(JobListRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["status"] = request.Status?.ToString(),
            ["jobType"] = request.JobType?.ToString(),
            ["page"] = request.Page.ToString(),
            ["pageSize"] = request.PageSize.ToString()
        };

        return QueryHelpers.AddQueryString("/api/jobs", query);
    }

    private static string BuildLogsUrl(LogQueryRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["serviceName"] = request.ServiceName,
            ["level"] = request.Level,
            ["traceId"] = request.TraceId,
            ["requestId"] = request.RequestId,
            ["from"] = request.From?.ToString("O"),
            ["to"] = request.To?.ToString("O"),
            ["limit"] = request.Limit.ToString()
        };

        return QueryHelpers.AddQueryString("/api/logs/search", query);
    }

    private static string BuildStorageRowsUrl(StorageRowsRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["column"] = request.Column,
            ["page"] = request.Page.ToString(),
            ["pageSize"] = request.PageSize.ToString()
        };

        return QueryHelpers.AddQueryString($"/api/storage/{request.Table}", query);
    }
}

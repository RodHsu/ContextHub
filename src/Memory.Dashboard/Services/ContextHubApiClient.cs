using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
    Task<MemoryGraphResult> GetMemoryGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken);
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
    Task<IReadOnlyList<SourceConnectionResult>> GetSourcesAsync(SourceListRequest request, CancellationToken cancellationToken);
    Task<SourceConnectionResult> CreateSourceAsync(SourceConnectionCreateRequest request, CancellationToken cancellationToken);
    Task<SourceConnectionResult> UpdateSourceAsync(SourceConnectionUpdateRequest request, CancellationToken cancellationToken);
    Task<EnqueueSourceSyncResult> SyncSourceAsync(Guid id, SourceSyncRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceSyncRunResult>> GetSourceRunsAsync(Guid id, string? projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<GovernanceFindingResult>> GetGovernanceFindingsAsync(GovernanceFindingListRequest request, CancellationToken cancellationToken);
    Task<GovernanceAnalyzeResult> AnalyzeGovernanceAsync(GovernanceAnalyzeRequest request, CancellationToken cancellationToken);
    Task<GovernanceFindingResult> AcceptGovernanceFindingAsync(Guid id, CancellationToken cancellationToken);
    Task<GovernanceFindingResult> DismissGovernanceFindingAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<EvaluationSuiteResult>> GetEvaluationSuitesAsync(string projectId, CancellationToken cancellationToken);
    Task<EvaluationSuiteResult> CreateEvaluationSuiteAsync(EvaluationSuiteCreateRequest request, CancellationToken cancellationToken);
    Task<EvaluationRunResult> RunEvaluationAsync(EvaluationRunRequest request, CancellationToken cancellationToken);
    Task<EvaluationRunResult?> GetEvaluationRunAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<SuggestedActionResult>> GetSuggestedActionsAsync(SuggestedActionListRequest request, CancellationToken cancellationToken);
    Task<SuggestedActionMutationResult> AcceptSuggestedActionAsync(Guid id, CancellationToken cancellationToken);
    Task<SuggestedActionResult> DismissSuggestedActionAsync(Guid id, CancellationToken cancellationToken);
    Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken);
    Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken);
    Task<PerformanceMeasureResult> MeasurePerformanceAsync(PerformanceMeasureRequest request, CancellationToken cancellationToken);
}

public sealed class ContextHubApiClient(HttpClient httpClient) : IContextHubApiClient
{
    private const int MaxGetAttempts = 3;

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

    public Task<MemoryGraphResult> GetMemoryGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<MemoryGraphResult>(BuildMemoryGraphUrl(request), cancellationToken);

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

    public Task<IReadOnlyList<SourceConnectionResult>> GetSourcesAsync(SourceListRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<SourceConnectionResult>>(BuildSourcesUrl(request), cancellationToken);

    public async Task<SourceConnectionResult> CreateSourceAsync(SourceConnectionCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/sources", request, cancellationToken);
        return await ReadRequiredAsync<SourceConnectionResult>(response, cancellationToken);
    }

    public async Task<SourceConnectionResult> UpdateSourceAsync(SourceConnectionUpdateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/api/sources/{request.Id}", new
        {
            request.Name,
            request.ConfigJson,
            request.SecretJson,
            request.Enabled,
            request.ProjectId
        }, cancellationToken);
        return await ReadRequiredAsync<SourceConnectionResult>(response, cancellationToken);
    }

    public async Task<EnqueueSourceSyncResult> SyncSourceAsync(Guid id, SourceSyncRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/sources/{id}/sync", new
        {
            request.Trigger,
            request.Force,
            request.ProjectId
        }, cancellationToken);
        return await ReadRequiredAsync<EnqueueSourceSyncResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<SourceSyncRunResult>> GetSourceRunsAsync(Guid id, string? projectId, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["projectId"] = projectId
        };

        return GetRequiredAsync<IReadOnlyList<SourceSyncRunResult>>(QueryHelpers.AddQueryString($"/api/sources/{id}/runs", query), cancellationToken);
    }

    public Task<IReadOnlyList<GovernanceFindingResult>> GetGovernanceFindingsAsync(GovernanceFindingListRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<GovernanceFindingResult>>(BuildGovernanceUrl(request), cancellationToken);

    public async Task<GovernanceAnalyzeResult> AnalyzeGovernanceAsync(GovernanceAnalyzeRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/governance/analyze", request, cancellationToken);
        return await ReadRequiredAsync<GovernanceAnalyzeResult>(response, cancellationToken);
    }

    public async Task<GovernanceFindingResult> AcceptGovernanceFindingAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/governance/findings/{id}/accept", null, cancellationToken);
        return await ReadRequiredAsync<GovernanceFindingResult>(response, cancellationToken);
    }

    public async Task<GovernanceFindingResult> DismissGovernanceFindingAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/governance/findings/{id}/dismiss", null, cancellationToken);
        return await ReadRequiredAsync<GovernanceFindingResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<EvaluationSuiteResult>> GetEvaluationSuitesAsync(string projectId, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<EvaluationSuiteResult>>(QueryHelpers.AddQueryString("/api/evaluation/suites", new Dictionary<string, string?> { ["projectId"] = projectId }), cancellationToken);

    public async Task<EvaluationSuiteResult> CreateEvaluationSuiteAsync(EvaluationSuiteCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/evaluation/suites", request, cancellationToken);
        return await ReadRequiredAsync<EvaluationSuiteResult>(response, cancellationToken);
    }

    public async Task<EvaluationRunResult> RunEvaluationAsync(EvaluationRunRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/evaluation/runs", request, cancellationToken);
        return await ReadRequiredAsync<EvaluationRunResult>(response, cancellationToken);
    }

    public async Task<EvaluationRunResult?> GetEvaluationRunAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/api/evaluation/runs/{id}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadRequiredAsync<EvaluationRunResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<SuggestedActionResult>> GetSuggestedActionsAsync(SuggestedActionListRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<SuggestedActionResult>>(BuildActionsUrl(request), cancellationToken);

    public async Task<SuggestedActionMutationResult> AcceptSuggestedActionAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/actions/{id}/accept", null, cancellationToken);
        return await ReadRequiredAsync<SuggestedActionMutationResult>(response, cancellationToken);
    }

    public async Task<SuggestedActionResult> DismissSuggestedActionAsync(Guid id, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/actions/{id}/dismiss", null, cancellationToken);
        return await ReadRequiredAsync<SuggestedActionResult>(response, cancellationToken);
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
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(url, cancellationToken);
                if (attempt < MaxGetAttempts && IsTransientStatusCode(response.StatusCode))
                {
                    await DelayBeforeRetryAsync(attempt, cancellationToken);
                    continue;
                }

                return await ReadRequiredAsync<T>(response, cancellationToken);
            }
            catch (Exception ex) when (ShouldRetryGet(ex, cancellationToken, attempt))
            {
                await DelayBeforeRetryAsync(attempt, cancellationToken);
            }
        }
    }

    private static bool ShouldRetryGet(Exception exception, CancellationToken cancellationToken, int attempt)
        => attempt < MaxGetAttempts &&
            !cancellationToken.IsCancellationRequested &&
            exception is HttpRequestException or TaskCanceledException;

    private static bool IsTransientStatusCode(HttpStatusCode statusCode)
        => statusCode is
            HttpStatusCode.RequestTimeout or
            HttpStatusCode.BadGateway or
            HttpStatusCode.ServiceUnavailable or
            HttpStatusCode.GatewayTimeout;

    private static Task DelayBeforeRetryAsync(int attempt, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(attempt * 150);
        return Task.Delay(delay, cancellationToken);
    }

    private static async Task<T> ReadRequiredAsync<T>(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(await BuildApiErrorMessageAsync(response, cancellationToken));
        }

        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException($"ContextHub API returned an empty payload for '{typeof(T).Name}'.");
    }

    private static async Task<string> BuildApiErrorMessageAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var prefix = $"API 回應 {(int)response.StatusCode} {response.StatusCode}";
        var payload = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return $"{prefix}。";
        }

        if (TryExtractProblemDetails(payload, out var detail))
        {
            return $"{prefix}：{detail}";
        }

        var singleLine = payload
            .ReplaceLineEndings(" ")
            .Trim();

        if (singleLine.Length > 280)
        {
            singleLine = $"{singleLine[..280].TrimEnd()}…";
        }

        return $"{prefix}：{singleLine}";
    }

    private static bool TryExtractProblemDetails(string payload, out string detail)
    {
        detail = string.Empty;

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            var parts = new List<string>();

            if (root.TryGetProperty("title", out var titleElement))
            {
                var title = titleElement.GetString();
                if (!string.IsNullOrWhiteSpace(title))
                {
                    parts.Add(title.Trim());
                }
            }

            if (root.TryGetProperty("detail", out var detailElement))
            {
                var problemDetail = detailElement.GetString();
                if (!string.IsNullOrWhiteSpace(problemDetail))
                {
                    parts.Add(problemDetail.Trim());
                }
            }

            if (root.TryGetProperty("errors", out var errorsElement) && errorsElement.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in errorsElement.EnumerateObject())
                {
                    var messages = property.Value.ValueKind == JsonValueKind.Array
                        ? property.Value.EnumerateArray()
                            .Select(item => item.GetString())
                            .Where(item => !string.IsNullOrWhiteSpace(item))
                            .Select(item => item!.Trim())
                            .ToArray()
                        : [];

                    if (messages.Length == 0)
                    {
                        continue;
                    }

                    parts.Add($"{property.Name}: {string.Join("、", messages)}");
                }
            }

            if (parts.Count == 0)
            {
                return false;
            }

            detail = string.Join(" ", parts);
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
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

    private static string BuildMemoryGraphUrl(MemoryGraphRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["tag"] = request.Tag,
            ["projectId"] = request.ProjectId,
            ["projectQuery"] = request.ProjectQuery,
            ["includedProjectIds"] = request.IncludedProjectIds is null ? null : string.Join(",", request.IncludedProjectIds),
            ["queryMode"] = request.QueryMode.ToString(),
            ["useSummaryLayer"] = request.UseSummaryLayer.ToString(),
            ["graphMode"] = request.GraphMode.ToString(),
            ["maxNodes"] = request.MaxNodes.ToString(),
            ["includeSimilarity"] = request.IncludeSimilarity.ToString(),
            ["scope"] = request.Scope?.ToString(),
            ["memoryType"] = request.MemoryType?.ToString(),
            ["status"] = request.Status?.ToString(),
            ["sourceType"] = request.SourceType
        };

        return QueryHelpers.AddQueryString("/api/memories/graph", query);
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

    private static string BuildSourcesUrl(SourceListRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["projectId"] = request.ProjectId,
            ["enabled"] = request.Enabled?.ToString(),
            ["sourceKind"] = request.SourceKind?.ToString()
        };

        return QueryHelpers.AddQueryString("/api/sources", query);
    }

    private static string BuildGovernanceUrl(GovernanceFindingListRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["projectId"] = request.ProjectId,
            ["type"] = request.Type?.ToString(),
            ["status"] = request.Status?.ToString(),
            ["limit"] = request.Limit.ToString()
        };

        return QueryHelpers.AddQueryString("/api/governance/findings", query);
    }

    private static string BuildActionsUrl(SuggestedActionListRequest request)
    {
        var query = new Dictionary<string, string?>
        {
            ["projectId"] = request.ProjectId,
            ["status"] = request.Status?.ToString(),
            ["type"] = request.Type?.ToString(),
            ["limit"] = request.Limit.ToString()
        };

        return QueryHelpers.AddQueryString("/api/actions", query);
    }
}

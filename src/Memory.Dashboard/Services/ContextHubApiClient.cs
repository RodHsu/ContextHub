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
    Task<IReadOnlyList<ConversationCheckpointSearchResult>> SearchConversationCheckpointsAsync(ConversationCheckpointSearchRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ChatGptProposalResult>> GetChatGptProposalsAsync(ChatGptProposalListRequest request, CancellationToken cancellationToken);
    Task<ChatGptProposalResult> ApproveChatGptProposalAsync(Guid id, string note, CancellationToken cancellationToken);
    Task<ChatGptProposalResult> RejectChatGptProposalAsync(Guid id, string note, CancellationToken cancellationToken);
    Task<MemoryGraphResult> GetMemoryGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectSuggestionResult>> GetMemoryProjectsAsync(string? query, int limit, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectInformationListItem>> GetProjectInformationProjectsAsync(bool includeInactive, CancellationToken cancellationToken);
    Task<ProjectInformationResult?> GetProjectInformationAsync(string projectId, CancellationToken cancellationToken);
    Task<ProjectInformationResult> UpsertProjectInformationAsync(ProjectInformationUpdateRequest request, CancellationToken cancellationToken);
    Task<ProjectInformationResult> UpdateProjectLifecycleAsync(ProjectLifecycleUpdateRequest request, CancellationToken cancellationToken);
    Task<ProjectHierarchyResult> GetProjectChildrenAsync(string parentProjectId, CancellationToken cancellationToken);
    Task<ProjectHierarchyResult> SetProjectChildrenAsync(ProjectHierarchySetChildrenRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<DiscussionThreadResult>> GetDiscussionThreadsAsync(DiscussionThreadListRequest request, CancellationToken cancellationToken);
    Task<DiscussionThreadDetailResult?> GetDiscussionThreadAsync(Guid threadId, string readerProjectId, CancellationToken cancellationToken);
    Task<DiscussionThreadResult?> CloseDiscussionThreadAsync(Guid threadId, CancellationToken cancellationToken);
    Task<DiscussionThreadResult?> SetDiscussionThreadArchivedAsync(Guid threadId, bool archived, CancellationToken cancellationToken);
    Task<DiscussionThreadResult?> AdvanceDiscussionThreadReadCursorAsync(Guid threadId, string readerProjectId, Guid lastReadMessageId, CancellationToken cancellationToken);
    Task<DiscussionThreadDetailResult> CreateDiscussionThreadAsync(DiscussionThreadCreateRequest request, CancellationToken cancellationToken);
    Task<DiscussionMessageResult> CreateDiscussionMessageAsync(DiscussionMessageCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ProjectWorkItemResult>> GetProjectWorkItemsAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> CreateProjectWorkItemAsync(ProjectWorkItemCreateRequest request, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> UpdateProjectWorkItemAsync(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> SetProjectWorkItemArchivedAsync(Guid workItemId, bool archived, CancellationToken cancellationToken);
    Task<ProjectWorkItemResult> SetProjectWorkItemChecklistCompletionAsync(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken);
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
    Task<MaintenanceStatusResult> GetMaintenanceStatusAsync(CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> ScheduleMaintenanceAsync(MaintenanceWindowRequest request, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> StartMaintenanceDrainAsync(Guid? runId, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> StartMaintenanceAsync(Guid? runId, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> CompleteMaintenanceAsync(Guid? runId, CancellationToken cancellationToken);
    Task<MaintenanceStatusResult> CancelMaintenanceAsync(Guid? runId, CancellationToken cancellationToken);
    Task<IReadOnlyList<MaintenanceRunResult>> GetMaintenanceRunsAsync(int limit, CancellationToken cancellationToken);
    Task<MemoryDataRetentionRunResult> RunMemoryDataRetentionAsync(MemoryDataRetentionRunRequest request, CancellationToken cancellationToken);
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
    Task<IReadOnlyList<TenantResult>> GetTenantsAsync(bool includeArchived, int limit, CancellationToken cancellationToken);
    Task<TenantResult> CreateTenantAsync(TenantCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantUserResult>> GetTenantUsersAsync(Guid tenantId, bool includeArchived, CancellationToken cancellationToken);
    Task<TenantUserResult> CreateTenantUserAsync(TenantUserCreateRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<TenantProjectGrantResult>> GetTenantProjectGrantsAsync(Guid tenantId, CancellationToken cancellationToken);
    Task<TenantProjectGrantResult> UpsertTenantProjectGrantAsync(TenantProjectGrantUpsertRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<ApiTokenResult>> GetApiTokensAsync(Guid tenantId, bool includeRevoked, CancellationToken cancellationToken);
    Task<ApiTokenCreatedResult> CreateApiTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken);
    Task<ApiTokenResult> UpdateApiTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken);
    Task<ApiTokenCreatedResult> RegenerateApiTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<ApiTokenResult> RevokeApiTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<IReadOnlyList<SecurityAuditEventResult>> GetSecurityAuditEventsAsync(Guid? tenantId, int limit, CancellationToken cancellationToken);
    Task<CurrentUserResult> GetCurrentUserAsync(CancellationToken cancellationToken);
    Task<IReadOnlyList<ApiTokenResult>> GetMyApiTokensAsync(bool includeRevoked, CancellationToken cancellationToken);
    Task<ApiTokenCreatedResult> CreateMyApiTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken);
    Task<ApiTokenResult> UpdateMyApiTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken);
    Task<ApiTokenCreatedResult> RegenerateMyApiTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<ApiTokenResult> RevokeMyApiTokenAsync(Guid tokenId, CancellationToken cancellationToken);
    Task<IReadOnlyList<StorageTableSummaryResult>> GetStorageTablesAsync(CancellationToken cancellationToken);
    Task<StorageTableRowsResult> GetStorageRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken);
    Task<PerformanceMeasureResult> MeasurePerformanceAsync(PerformanceMeasureRequest request, CancellationToken cancellationToken);
    Task<AgentConnectivitySettingsResult> GetAgentConnectivitySettingsAsync(CancellationToken cancellationToken);
    Task<AgentConnectivityStatusResult> GetAgentConnectivityStatusAsync(string? projectId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentConnectivitySummaryResult>> GetAgentConnectivitySummariesAsync(AgentConnectivitySummaryQuery request, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentConnectivityRecentObservationResult>> GetRecentAgentConnectivityObservationsAsync(string? projectId, string? agentId, int limit, CancellationToken cancellationToken);
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

    public Task<IReadOnlyList<ConversationCheckpointSearchResult>> SearchConversationCheckpointsAsync(ConversationCheckpointSearchRequest request, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["query"] = request.Query,
            ["projectId"] = request.ProjectId,
            ["conversationId"] = request.ConversationId,
            ["limit"] = request.Limit.ToString()
        };

        return GetRequiredAsync<IReadOnlyList<ConversationCheckpointSearchResult>>(
            QueryHelpers.AddQueryString("/api/conversations/checkpoints/search", query),
            cancellationToken);
    }

    public Task<IReadOnlyList<ChatGptProposalResult>> GetChatGptProposalsAsync(ChatGptProposalListRequest request, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["projectId"] = request.ProjectId,
            ["status"] = request.Status?.ToString(),
            ["limit"] = request.Limit.ToString()
        };

        return GetRequiredAsync<IReadOnlyList<ChatGptProposalResult>>(
            QueryHelpers.AddQueryString("/api/chatgpt/proposals", query),
            cancellationToken);
    }

    public async Task<ChatGptProposalResult> ApproveChatGptProposalAsync(Guid id, string note, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/chatgpt/proposals/{id:D}/approve", new { note }, cancellationToken);
        return await ReadRequiredAsync<ChatGptProposalResult>(response, cancellationToken);
    }

    public async Task<ChatGptProposalResult> RejectChatGptProposalAsync(Guid id, string note, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/chatgpt/proposals/{id:D}/reject", new { note }, cancellationToken);
        return await ReadRequiredAsync<ChatGptProposalResult>(response, cancellationToken);
    }

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

    public async Task<ProjectInformationResult?> GetProjectInformationAsync(string projectId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.GetAsync($"/api/projects/information/{Uri.EscapeDataString(projectId)}", cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        return await ReadRequiredAsync<ProjectInformationResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<ProjectInformationListItem>> GetProjectInformationProjectsAsync(bool includeInactive, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<ProjectInformationListItem>>(
            QueryHelpers.AddQueryString("/api/projects/information/", "includeInactive", includeInactive.ToString()),
            cancellationToken);

    public async Task<ProjectInformationResult> UpsertProjectInformationAsync(ProjectInformationUpdateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/projects/information/{Uri.EscapeDataString(request.ProjectId)}", request, cancellationToken);
        return await ReadRequiredAsync<ProjectInformationResult>(response, cancellationToken);
    }

    public async Task<ProjectInformationResult> UpdateProjectLifecycleAsync(ProjectLifecycleUpdateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"/api/projects/information/{Uri.EscapeDataString(request.ProjectId)}/lifecycle",
            request,
            cancellationToken);
        return await ReadRequiredAsync<ProjectInformationResult>(response, cancellationToken);
    }

    public Task<ProjectHierarchyResult> GetProjectChildrenAsync(string parentProjectId, CancellationToken cancellationToken)
        => GetRequiredAsync<ProjectHierarchyResult>($"/api/projects/hierarchy/{Uri.EscapeDataString(parentProjectId)}", cancellationToken);

    public async Task<ProjectHierarchyResult> SetProjectChildrenAsync(ProjectHierarchySetChildrenRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/projects/hierarchy/{Uri.EscapeDataString(request.ParentProjectId)}", new { childProjectIds = request.ChildProjectIds }, cancellationToken);
        return await ReadRequiredAsync<ProjectHierarchyResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<DiscussionThreadResult>> GetDiscussionThreadsAsync(DiscussionThreadListRequest request, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?> { ["projectId"] = request.ProjectId, ["hostProjectId"] = request.HostProjectId, ["status"] = request.Status, ["limit"] = request.Limit.ToString(), ["includeArchived"] = request.IncludeArchived.ToString() };
        return GetRequiredAsync<IReadOnlyList<DiscussionThreadResult>>(QueryHelpers.AddQueryString("/api/discussions/threads", query), cancellationToken);
    }

    public Task<DiscussionThreadDetailResult?> GetDiscussionThreadAsync(Guid threadId, string readerProjectId, CancellationToken cancellationToken)
        => GetRequiredAsync<DiscussionThreadDetailResult>($"/api/discussions/threads/{threadId:D}?readerProjectId={Uri.EscapeDataString(readerProjectId)}", cancellationToken)!;

    public async Task<DiscussionThreadResult?> CloseDiscussionThreadAsync(Guid threadId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/discussions/threads/{threadId:D}/close", content: null, cancellationToken);
        return await ReadRequiredAsync<DiscussionThreadResult>(response, cancellationToken);
    }

    public async Task<DiscussionThreadResult?> SetDiscussionThreadArchivedAsync(Guid threadId, bool archived, CancellationToken cancellationToken)
    {
        var action = archived ? "archive" : "restore";
        using var response = await httpClient.PostAsync($"/api/discussions/threads/{threadId:D}/{action}", content: null, cancellationToken);
        return await ReadRequiredAsync<DiscussionThreadResult>(response, cancellationToken);
    }

    public async Task<DiscussionThreadResult?> AdvanceDiscussionThreadReadCursorAsync(Guid threadId, string readerProjectId, Guid lastReadMessageId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/discussions/threads/{threadId:D}/read", new { ReaderProjectId = readerProjectId, LastReadMessageId = lastReadMessageId }, cancellationToken);
        return await ReadRequiredAsync<DiscussionThreadResult>(response, cancellationToken);
    }

    public async Task<DiscussionThreadDetailResult> CreateDiscussionThreadAsync(DiscussionThreadCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/discussions/threads", request, cancellationToken);
        return await ReadRequiredAsync<DiscussionThreadDetailResult>(response, cancellationToken);
    }

    public async Task<DiscussionMessageResult> CreateDiscussionMessageAsync(DiscussionMessageCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/discussions/threads/{request.ThreadId:D}/messages", new { request.SenderProjectId, request.Content }, cancellationToken);
        return await ReadRequiredAsync<DiscussionMessageResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<ProjectWorkItemResult>> GetProjectWorkItemsAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<ProjectWorkItemResult>>(QueryHelpers.AddQueryString("/api/work-items", new Dictionary<string, string?> { ["projectId"] = request.ProjectId, ["status"] = request.Status?.ToString(), ["limit"] = request.Limit.ToString(), ["includeArchived"] = request.IncludeArchived.ToString() }), cancellationToken);

    public async Task<ProjectWorkItemResult> CreateProjectWorkItemAsync(ProjectWorkItemCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/work-items", request, cancellationToken);
        return await ReadRequiredAsync<ProjectWorkItemResult>(response, cancellationToken);
    }

    public async Task<ProjectWorkItemResult> UpdateProjectWorkItemAsync(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/work-items/{request.Id:D}", request, cancellationToken);
        return await ReadRequiredAsync<ProjectWorkItemResult>(response, cancellationToken);
    }

    public async Task<ProjectWorkItemResult> SetProjectWorkItemArchivedAsync(Guid workItemId, bool archived, CancellationToken cancellationToken)
    {
        var action = archived ? "archive" : "restore";
        using var response = await httpClient.PostAsync($"/api/work-items/{workItemId:D}/{action}", content: null, cancellationToken);
        return await ReadRequiredAsync<ProjectWorkItemResult>(response, cancellationToken);
    }

    public async Task<ProjectWorkItemResult> SetProjectWorkItemChecklistCompletionAsync(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/work-items/{workItemId:D}/checklist/{checklistItemId:D}", new { isCompleted }, cancellationToken);
        return await ReadRequiredAsync<ProjectWorkItemResult>(response, cancellationToken);
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

    public Task<MaintenanceStatusResult> GetMaintenanceStatusAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<MaintenanceStatusResult>("/api/maintenance/status", cancellationToken);

    public async Task<MaintenanceStatusResult> ScheduleMaintenanceAsync(MaintenanceWindowRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/maintenance/windows", request, cancellationToken);
        return await ReadRequiredAsync<MaintenanceStatusResult>(response, cancellationToken);
    }

    public async Task<MaintenanceStatusResult> StartMaintenanceDrainAsync(Guid? runId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(BuildMaintenanceRunActionUrl(runId, "drain"), null, cancellationToken);
        return await ReadRequiredAsync<MaintenanceStatusResult>(response, cancellationToken);
    }

    public async Task<MaintenanceStatusResult> StartMaintenanceAsync(Guid? runId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(BuildMaintenanceRunActionUrl(runId, "start"), null, cancellationToken);
        return await ReadRequiredAsync<MaintenanceStatusResult>(response, cancellationToken);
    }

    public async Task<MaintenanceStatusResult> CompleteMaintenanceAsync(Guid? runId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(BuildMaintenanceRunActionUrl(runId, "complete"), null, cancellationToken);
        return await ReadRequiredAsync<MaintenanceStatusResult>(response, cancellationToken);
    }

    public async Task<MaintenanceStatusResult> CancelMaintenanceAsync(Guid? runId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync(BuildMaintenanceRunActionUrl(runId, "cancel"), null, cancellationToken);
        return await ReadRequiredAsync<MaintenanceStatusResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<MaintenanceRunResult>> GetMaintenanceRunsAsync(int limit, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<MaintenanceRunResult>>(
            QueryHelpers.AddQueryString("/api/maintenance/runs", new Dictionary<string, string?>
            {
                ["limit"] = limit.ToString()
            }),
            cancellationToken);

    public async Task<MemoryDataRetentionRunResult> RunMemoryDataRetentionAsync(MemoryDataRetentionRunRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/maintenance/memory-data-retention/run", request, cancellationToken);
        return await ReadRequiredAsync<MemoryDataRetentionRunResult>(response, cancellationToken);
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

    public Task<IReadOnlyList<TenantResult>> GetTenantsAsync(bool includeArchived, int limit, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<TenantResult>>(QueryHelpers.AddQueryString("/api/security/tenants", new Dictionary<string, string?>
        {
            ["includeArchived"] = includeArchived.ToString(),
            ["limit"] = limit.ToString()
        }), cancellationToken);

    public async Task<TenantResult> CreateTenantAsync(TenantCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/security/tenants", request, cancellationToken);
        return await ReadRequiredAsync<TenantResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<TenantUserResult>> GetTenantUsersAsync(Guid tenantId, bool includeArchived, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<TenantUserResult>>(QueryHelpers.AddQueryString($"/api/security/tenants/{tenantId}/users", new Dictionary<string, string?>
        {
            ["includeArchived"] = includeArchived.ToString()
        }), cancellationToken);

    public async Task<TenantUserResult> CreateTenantUserAsync(TenantUserCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/security/tenants/{request.TenantId}/users", new
        {
            request.Username,
            request.DisplayName,
            request.Email,
            request.Role,
            Password = request.PasswordHash
        }, cancellationToken);
        return await ReadRequiredAsync<TenantUserResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<TenantProjectGrantResult>> GetTenantProjectGrantsAsync(Guid tenantId, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<TenantProjectGrantResult>>($"/api/security/tenants/{tenantId}/project-grants", cancellationToken);

    public async Task<TenantProjectGrantResult> UpsertTenantProjectGrantAsync(TenantProjectGrantUpsertRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/security/tenants/{request.TenantId}/project-grants/{Uri.EscapeDataString(request.ProjectId)}", new
        {
            request.CanRead,
            request.CanWrite,
            request.CanManageTokens
        }, cancellationToken);
        return await ReadRequiredAsync<TenantProjectGrantResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<ApiTokenResult>> GetApiTokensAsync(Guid tenantId, bool includeRevoked, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<ApiTokenResult>>(QueryHelpers.AddQueryString($"/api/security/tenants/{tenantId}/tokens", new Dictionary<string, string?>
        {
            ["includeRevoked"] = includeRevoked.ToString()
        }), cancellationToken);

    public async Task<ApiTokenCreatedResult> CreateApiTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync($"/api/security/tenants/{request.TenantId}/tokens", new
        {
            request.OwnerUserId,
            request.Name,
            request.Notes,
            request.Scopes,
            request.AllowedProjectIds,
            request.ExpiresAt
        }, cancellationToken);
        return await ReadRequiredAsync<ApiTokenCreatedResult>(response, cancellationToken);
    }

    public async Task<ApiTokenResult> UpdateApiTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/api/security/tokens/{tokenId}", request, cancellationToken);
        return await ReadRequiredAsync<ApiTokenResult>(response, cancellationToken);
    }

    public async Task<ApiTokenResult> RevokeApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/security/tokens/{tokenId}/revoke", null, cancellationToken);
        return await ReadRequiredAsync<ApiTokenResult>(response, cancellationToken);
    }

    public async Task<ApiTokenCreatedResult> RegenerateApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/security/tokens/{tokenId}/regenerate", null, cancellationToken);
        return await ReadRequiredAsync<ApiTokenCreatedResult>(response, cancellationToken);
    }

    public Task<IReadOnlyList<SecurityAuditEventResult>> GetSecurityAuditEventsAsync(Guid? tenantId, int limit, CancellationToken cancellationToken)
    {
        var query = new Dictionary<string, string?>
        {
            ["limit"] = limit.ToString()
        };

        if (tenantId.HasValue)
        {
            query["tenantId"] = tenantId.Value.ToString();
        }

        return GetRequiredAsync<IReadOnlyList<SecurityAuditEventResult>>(QueryHelpers.AddQueryString("/api/security/audit-events", query), cancellationToken);
    }

    public Task<CurrentUserResult> GetCurrentUserAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<CurrentUserResult>("/api/me", cancellationToken);

    public Task<IReadOnlyList<ApiTokenResult>> GetMyApiTokensAsync(bool includeRevoked, CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<ApiTokenResult>>(QueryHelpers.AddQueryString("/api/me/tokens", new Dictionary<string, string?>
        {
            ["includeRevoked"] = includeRevoked.ToString()
        }), cancellationToken);

    public async Task<ApiTokenCreatedResult> CreateMyApiTokenAsync(ApiTokenCreateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync("/api/me/tokens", new
        {
            request.Name,
            request.Notes,
            request.Scopes,
            request.AllowedProjectIds,
            request.ExpiresAt
        }, cancellationToken);
        return await ReadRequiredAsync<ApiTokenCreatedResult>(response, cancellationToken);
    }

    public async Task<ApiTokenResult> UpdateMyApiTokenAsync(Guid tokenId, ApiTokenUpdateRequest request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PatchAsJsonAsync($"/api/me/tokens/{tokenId}", request, cancellationToken);
        return await ReadRequiredAsync<ApiTokenResult>(response, cancellationToken);
    }

    public async Task<ApiTokenResult> RevokeMyApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/me/tokens/{tokenId}/revoke", null, cancellationToken);
        return await ReadRequiredAsync<ApiTokenResult>(response, cancellationToken);
    }

    public async Task<ApiTokenCreatedResult> RegenerateMyApiTokenAsync(Guid tokenId, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"/api/me/tokens/{tokenId}/regenerate", null, cancellationToken);
        return await ReadRequiredAsync<ApiTokenCreatedResult>(response, cancellationToken);
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

    public Task<AgentConnectivitySettingsResult> GetAgentConnectivitySettingsAsync(CancellationToken cancellationToken)
        => GetRequiredAsync<AgentConnectivitySettingsResult>("/api/agent-connectivity/settings", cancellationToken);

    public Task<AgentConnectivityStatusResult> GetAgentConnectivityStatusAsync(string? projectId, CancellationToken cancellationToken)
        => GetRequiredAsync<AgentConnectivityStatusResult>(
            QueryHelpers.AddQueryString("/api/agent-connectivity/status", new Dictionary<string, string?>
            {
                ["projectId"] = projectId
            }),
            cancellationToken);

    public Task<IReadOnlyList<AgentConnectivitySummaryResult>> GetAgentConnectivitySummariesAsync(
        AgentConnectivitySummaryQuery request,
        CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<AgentConnectivitySummaryResult>>(BuildAgentConnectivitySummariesUrl(request), cancellationToken);

    public Task<IReadOnlyList<AgentConnectivityRecentObservationResult>> GetRecentAgentConnectivityObservationsAsync(
        string? projectId,
        string? agentId,
        int limit,
        CancellationToken cancellationToken)
        => GetRequiredAsync<IReadOnlyList<AgentConnectivityRecentObservationResult>>(
            QueryHelpers.AddQueryString("/api/agent-connectivity/recent", new Dictionary<string, string?>
            {
                ["projectId"] = projectId,
                ["agentId"] = agentId,
                ["limit"] = limit.ToString()
            }),
            cancellationToken);

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

    private static string BuildMaintenanceRunActionUrl(Guid? runId, string action)
        => runId.HasValue
            ? $"/api/maintenance/windows/{runId.Value:D}/{action}"
            : $"/api/maintenance/windows/current/{action}";

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

    private static string BuildAgentConnectivitySummariesUrl(AgentConnectivitySummaryQuery request)
    {
        var query = new Dictionary<string, string?>
        {
            ["projectId"] = request.ProjectId,
            ["agentId"] = request.AgentId,
            ["mcpMethod"] = request.McpMethod,
            ["fromUtc"] = request.FromUtc?.ToString("O"),
            ["toUtc"] = request.ToUtc?.ToString("O"),
            ["limit"] = request.Limit.ToString()
        };

        return QueryHelpers.AddQueryString("/api/agent-connectivity/summaries", query);
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

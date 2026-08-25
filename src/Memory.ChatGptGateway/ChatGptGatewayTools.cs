using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Memory.Application;
using ModelContextProtocol.Server;

namespace Memory.ChatGptGateway;

[McpServerToolType]
public sealed class ChatGptGatewayTools(
    IContextHubBootstrapService bootstrapService,
    IMemoryService memoryService,
    ILogQueryService logQueryService,
    IConversationAutomationService conversationAutomationService,
    IProjectInformationService projectInformationService,
    IProjectDiscussionService projectDiscussionService,
    IAccessibleProjectService accessibleProjectService,
    IDailyMemoryReviewService dailyMemoryReviewService,
    IKnowledgeReviewService knowledgeReviewService,
    ISuggestedActionService suggestedActionService,
    IMemoryDataRetentionService retentionService,
    IProjectArtifactExchangeService artifactExchangeService,
    IChatGptProposalService proposalService,
    IProjectWorkItemService projectWorkItemService,
    IRequestActorAccessor actorAccessor,
    IHttpContextAccessor httpContextAccessor)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool(UseStructuredContent = true), Description("Describe ContextHub purpose, capabilities, startup flow, and projectId guidance.")]
    public ContextHubBootstrapResult describe_context_hub(string? projectId = null)
        => bootstrapService.Describe(new ContextHubBootstrapRequest(projectId));

    [McpServerTool(UseStructuredContent = true), Description("List remote ContextHub ProjectIds accessible to the current actor. The default ProjectId is never returned.")]
    public Task<IReadOnlyList<AccessibleProjectResult>> projects_list(int limit = 100, CancellationToken cancellationToken = default)
        => accessibleProjectService.ListAsync(limit, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Return the current authorized account's daily remote memory review. This single read-only tool performs server-side project scoping and does not change memories, preferences, proposals, or actions.")]
    public Task<DailyMemoryReviewResult> daily_memory_review(CancellationToken cancellationToken = default)
        => dailyMemoryReviewService.ReviewAsync(cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Review all knowledge-governance surfaces. Follow Review -> Execute -> Re-review; Converged is returned only by an explicit re-review with zero actionable items and no additional pages.")]
    public Task<KnowledgeReviewResult> knowledge_review(KnowledgeReviewRequest request, CancellationToken cancellationToken = default)
        => knowledgeReviewService.ReviewAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List persisted global user preferences for remote knowledge-governance review.")]
    public Task<IReadOnlyList<UserPreferenceResult>> user_preferences_list(UserPreferenceListRequest request, CancellationToken cancellationToken = default)
        => memoryService.ListUserPreferencesAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List staged conversation insights for the current actor's authorized projects.")]
    public Task<IReadOnlyList<ConversationInsightResult>> conversation_insights_list(ConversationInsightListRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.ListInsightsAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read one conversation insight and its current governance/promotion status.")]
    public Task<ConversationInsightResult?> conversation_insight_status(Guid insightId, CancellationToken cancellationToken = default)
        => conversationAutomationService.GetInsightAsync(insightId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Idempotently return a failed conversation insight to Pending and enqueue promotion if no equivalent job is already pending.")]
    public Task<ConversationInsightResult> conversation_insight_retry(ConversationInsightGovernanceRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.RetryInsightAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Idempotently skip a pending or failed conversation insight with a governance reason.")]
    public Task<ConversationInsightResult> conversation_insight_skip(ConversationInsightGovernanceRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.SkipInsightAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List pending or historical governance suggested actions for one explicitly authorized ProjectId.")]
    public Task<IReadOnlyList<SuggestedActionResult>> suggested_actions_list(SuggestedActionListRequest request, CancellationToken cancellationToken = default)
        => suggestedActionService.ListAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Classify remote memory-retention candidates for the current actor's accessible ProjectIds. This is read-only and never deletes or archives memory.")]
    public async Task<MemoryDataRetentionRunResult> memory_retention_preview(CancellationToken cancellationToken = default)
    {
        var projects = await accessibleProjectService.ListAsync(200, cancellationToken);
        var projectIds = projects.Where(project => project.CanRead).Select(project => project.ProjectId).ToArray();
        if (projectIds.Length == 0)
        {
            throw new InvalidOperationException("No readable ProjectId is available for retention preview.");
        }

        var tenantId = actorAccessor.Current.TenantId
            ?? throw new InvalidOperationException("Retention preview requires an authenticated tenant actor.");

        return await retentionService.RunAsync(
            new MemoryDataRetentionRunRequest(
                TriggeredBy: "chatgpt-mcp-gateway:retention-preview",
                Mode: MemoryDataRetentionRunMode.Classify,
                PreviewOnly: false,
                ProjectIds: projectIds,
                TenantId: tenantId),
            "chatgpt-mcp-gateway:retention-preview",
            cancellationToken);
    }

    [McpServerTool(UseStructuredContent = true), Description("Preview safe project cleanup candidates for one explicitly authorized ProjectId. This is read-only and never deletes or archives memory.")]
    public Task<ProjectCleanupPreviewResult> project_cleanup_preview(ProjectCleanupPreviewRequest request, CancellationToken cancellationToken = default)
        => memoryService.PreviewProjectCleanupAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Build a structured ContextHub working context for the current task.")]
    public Task<WorkingContextResult> build_working_context(WorkingContextRequest request, CancellationToken cancellationToken = default)
        => memoryService.BuildWorkingContextAsync(
            request with
            {
                Telemetry = new RetrievalTelemetryContext("build_working_context", "chatgpt-mcp-gateway", "ChatGPT task context bootstrap")
            },
            cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Search ContextHub memory items using hybrid keyword and semantic retrieval.")]
    public Task<IReadOnlyList<MemorySearchHit>> memory_search(
        string query,
        int limit = 10,
        bool includeArchived = false,
        string projectId = ProjectContext.DefaultProjectId,
        IReadOnlyList<string>? includedProjectIds = null,
        MemoryQueryMode queryMode = MemoryQueryMode.CurrentOnly,
        bool useSummaryLayer = false,
        CancellationToken cancellationToken = default)
        => memoryService.SearchAsync(
            new MemorySearchRequest(
                query,
                limit,
                includeArchived,
                projectId,
                includedProjectIds,
                queryMode,
                useSummaryLayer,
                new RetrievalTelemetryContext("memory_search", "chatgpt-mcp-gateway", "ChatGPT memory search")),
            cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Get a single ContextHub memory item by id.")]
    public Task<MemoryDocument?> memory_get(Guid id, CancellationToken cancellationToken = default)
        => memoryService.GetAsync(id, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read durable project information before starting work in a ProjectId.")]
    public Task<ProjectInformationResult?> project_information_get(string projectId, CancellationToken cancellationToken = default)
        => projectInformationService.GetAsync(projectId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List cross-project discussion threads visible to an authorized participant ProjectId.")]
    public Task<IReadOnlyList<DiscussionThreadResult>> discussion_threads_list(DiscussionThreadListRequest request, CancellationToken cancellationToken = default)
        => projectDiscussionService.ListThreadsAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read one cross-project discussion thread and mark it read for the authorized participant ProjectId.")]
    public Task<DiscussionThreadDetailResult?> discussion_thread_get(Guid threadId, string readerProjectId, CancellationToken cancellationToken = default)
        => projectDiscussionService.GetThreadAsync(threadId, readerProjectId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Close a cross-project discussion when the authorized actor has write access to its HostProjectId. Closed discussions retain their history and reject new replies.")]
    public Task<DiscussionThreadResult?> discussion_thread_close(Guid threadId, CancellationToken cancellationToken = default)
        => projectDiscussionService.CloseThreadAsync(threadId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Archive a cross-project discussion. Archived threads are hidden from default lists and reject mutations until restored.")]
    public Task<DiscussionThreadResult?> discussion_thread_archive(Guid threadId, CancellationToken cancellationToken = default)
        => projectDiscussionService.SetThreadArchivedAsync(threadId, archived: true, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Restore an archived cross-project discussion without changing its Open or Closed status.")]
    public Task<DiscussionThreadResult?> discussion_thread_restore(Guid threadId, CancellationToken cancellationToken = default)
        => projectDiscussionService.SetThreadArchivedAsync(threadId, archived: false, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a persistent cross-project discussion. The host and every participant must be authorized to the OAuth actor.")]
    public Task<DiscussionThreadDetailResult> discussion_thread_create(DiscussionThreadCreateRequest request, CancellationToken cancellationToken = default)
        => projectDiscussionService.CreateThreadAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Post a message to an open cross-project discussion as an authorized participant ProjectId.")]
    public Task<DiscussionMessageResult> discussion_message_create(DiscussionMessageCreateRequest request, CancellationToken cancellationToken = default)
        => projectDiscussionService.AddMessageAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read configured child ProjectIds for an authorized parent ProjectId.")]
    public Task<ProjectHierarchyResult> project_hierarchy_get_children(string parentProjectId, CancellationToken cancellationToken = default)
        => projectDiscussionService.GetChildrenAsync(parentProjectId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Set child ProjectIds for an authorized parent ProjectId. This changes project-management hierarchy metadata only, not memory sharing.")]
    public Task<ProjectHierarchyResult> project_hierarchy_set_children(ProjectHierarchySetChildrenRequest request, CancellationToken cancellationToken = default)
        => projectDiscussionService.SetChildrenAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List user-managed project work items for one explicitly authorized ProjectId.")]
    public Task<IReadOnlyList<ProjectWorkItemResult>> project_work_items_list(ProjectWorkItemListRequest request, CancellationToken cancellationToken = default)
        => projectWorkItemService.ListAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a user-managed project work item for one explicitly authorized ProjectId.")]
    public Task<ProjectWorkItemResult> project_work_item_create(ProjectWorkItemCreateRequest request, CancellationToken cancellationToken = default)
        => projectWorkItemService.CreateAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Update project work item content, priority, due date, or lifecycle status.")]
    public Task<ProjectWorkItemResult> project_work_item_update(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken = default)
        => projectWorkItemService.UpdateAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Complete or reopen one project work item checklist entry.")]
    public Task<ProjectWorkItemResult> project_work_item_checklist_update(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken = default)
        => projectWorkItemService.SetChecklistItemCompletionAsync(workItemId, checklistItemId, isCompleted, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Archive a project work item. Archived work items are hidden from default lists and reject mutations until restored.")]
    public Task<ProjectWorkItemResult> project_work_item_archive(Guid workItemId, CancellationToken cancellationToken = default)
        => projectWorkItemService.SetArchivedAsync(workItemId, archived: true, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Restore an archived project work item without changing its business status.")]
    public Task<ProjectWorkItemResult> project_work_item_restore(Guid workItemId, CancellationToken cancellationToken = default)
        => projectWorkItemService.SetArchivedAsync(workItemId, archived: false, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Propose an update to the durable project description. DisplayName is UI-managed and cannot be changed by ChatGPT or MCP agents.")]
    public Task<ChatGptProposalResult> project_information_upsert(ProjectInformationAgentUpdateRequest request, string? governanceRunId = null, CancellationToken cancellationToken = default)
        => CreateProposalAsync("project_information_upsert", request.ProjectId, request.ProjectId, "Update project information.", request, governanceRunId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Propose hiding, unhiding, archiving, or restoring a project. Archiving excludes its memories from default search and build_working_context after approval.")]
    public Task<ChatGptProposalResult> project_information_update_lifecycle(ProjectLifecycleUpdateRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("project_information_update_lifecycle", request.ProjectId, request.Action.ToString(), "Update project lifecycle.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List project-scoped artifact exchange records shared by agents using the same ProjectId.")]
    public Task<IReadOnlyList<ProjectArtifactResult>> project_artifacts_list(ProjectArtifactListRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.ListAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Search project-scoped artifact summaries, snippets, file references, or external object pointers shared by agents using the same ProjectId.")]
    public Task<IReadOnlyList<ProjectArtifactResult>> project_artifacts_search(ProjectArtifactSearchRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.SearchAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Get one project-scoped artifact exchange record by memory id.")]
    public Task<ProjectArtifactResult?> project_artifact_get(Guid memoryId, CancellationToken cancellationToken = default)
        => artifactExchangeService.GetAsync(memoryId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Search runtime logs by text, service, level, or identifiers.")]
    public Task<IReadOnlyList<LogEntryResult>> log_search(LogQueryRequest request, CancellationToken cancellationToken = default)
        => logQueryService.SearchAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read raw runtime logs using filters such as service name, trace id, and time window.")]
    public Task<IReadOnlyList<LogEntryResult>> log_read(LogQueryRequest request, CancellationToken cancellationToken = default)
        => logQueryService.SearchAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Ingest a ChatGPT conversation checkpoint into ContextHub staging for later promotion.")]
    public Task<ConversationIngestResult> conversation_ingest(ConversationIngestRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.IngestAsync(
            request with
            {
                SourceSystem = string.IsNullOrWhiteSpace(request.SourceSystem)
                    ? ChatGptProposalService.SourceSystem
                    : request.SourceSystem
            },
            cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to upsert a durable ContextHub memory item. Approval is required before the memory is changed.")]
    public Task<ChatGptProposalResult> memory_upsert(MemoryUpsertRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("memory_upsert", request.ProjectId, request.Title, request.Summary, request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to update a durable ContextHub memory item. Approval is required before the memory is changed.")]
    public Task<ChatGptProposalResult> memory_update(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("memory_update", request.ProjectId ?? ProjectContext.DefaultProjectId, "Update memory item", $"Update memory item {request.Id:D}.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to archive or restore a durable ContextHub memory item. Approval is required before the memory is changed.")]
    public Task<ChatGptProposalResult> memory_archive(MemoryArchiveRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("memory_archive", request.ProjectId ?? ProjectContext.DefaultProjectId, "Archive memory item", $"Change archive state for memory item {request.Id:D}.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to move a durable ContextHub memory item to another ProjectId. Approval is required before the memory is changed.")]
    public Task<ChatGptProposalResult> memory_move(MemoryMoveRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("memory_move", request.SourceProjectId ?? request.TargetProjectId, "Move memory item", $"Move memory item {request.Id:D} to {request.TargetProjectId}.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to permanently delete one durable ContextHub memory item. Approval is required before the memory is changed.")]
    public Task<ChatGptProposalResult> memory_delete(MemoryDeleteRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("memory_delete", request.ProjectId ?? ProjectContext.DefaultProjectId, "Delete memory item", $"Delete memory item {request.Id:D}.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to archive or delete selected safe cleanup candidates in one ProjectId. Approval is required before memory is changed.")]
    public Task<ChatGptProposalResult> project_cleanup_apply(ProjectCleanupApplyRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("project_cleanup_apply", request.ProjectId, "Apply project cleanup", $"Apply {request.Action} cleanup to {request.MemoryIds?.Count ?? 0} memory item(s) in {request.ProjectId}.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to create or update a durable ContextHub user preference. Approval is required before the preference is changed.")]
    public Task<ChatGptProposalResult> user_preference_upsert(UserPreferenceUpsertRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("user_preference_upsert", ProjectContext.UserProjectId, request.Title, request.Rationale, request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to archive or restore a global user preference. Approval is required before it is changed.")]
    public Task<ChatGptProposalResult> user_preference_archive(UserPreferenceArchiveRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("user_preference_archive", ProjectContext.UserProjectId, "Archive user preference", $"Change archive state for preference {request.Id:D}.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to accept a governance suggested action. Approval is required before it is executed.")]
    public Task<ChatGptProposalResult> suggested_action_accept(HubActionRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("suggested_action_accept", ProjectContext.SharedProjectId, "Accept suggested action", $"Accept suggested action {request.Id:D}.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to dismiss a governance suggested action. Approval is required before it is changed.")]
    public Task<ChatGptProposalResult> suggested_action_dismiss(HubActionRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("suggested_action_dismiss", ProjectContext.SharedProjectId, "Dismiss suggested action", $"Dismiss suggested action {request.Id:D}.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to promote a selected log slice into durable memory. Approval is required before memory is changed.")]
    public Task<ChatGptProposalResult> promote_log_slice_to_memory(PromoteLogSliceRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("promote_log_slice_to_memory", request.ProjectId, request.Title, $"Promote logs matching '{request.Query ?? request.TraceId ?? request.ServiceName ?? "selected filters"}'.", request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to publish a project artifact summary, snippet, file reference, or external object pointer. Approval is required before shared project knowledge is changed.")]
    public Task<ChatGptProposalResult> project_artifact_publish(ProjectArtifactPublishRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("project_artifact_publish", request.ProjectId, request.Title, request.Summary, request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a pending proposal to upload artifact content to configured object storage, then publish only the expiring object pointer. Approval is required before external storage or shared project knowledge is changed.")]
    public Task<ChatGptProposalResult> project_artifact_upload_object(ProjectArtifactManagedObjectPublishRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("project_artifact_upload_object", request.ProjectId, request.Title, request.Summary, request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List pending, applied, rejected, or failed ChatGPT write proposals for review.")]
    public Task<IReadOnlyList<ChatGptProposalResult>> chatgpt_proposals_list(ChatGptProposalListRequest request, CancellationToken cancellationToken = default)
        => proposalService.ListAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create an idempotent proposal for a scheduled governance run. Reusing the same GovernanceRunId, tool, project, and payload returns the original proposal.")]
    public Task<ChatGptProposalResult> chatgpt_governance_proposal_create(ChatGptGovernanceProposalRequest request, CancellationToken cancellationToken = default)
    {
        var user = ResolveOAuthUser();
        return proposalService.CreateAsync(
            new ChatGptProposalCreateRequest(
                request.ToolName,
                request.ProjectId,
                request.PayloadJson,
                request.Title,
                request.Summary,
                user.Subject,
                user.Email,
                user.Name,
                request.GovernanceRunId),
            cancellationToken);
    }

    [McpServerTool(UseStructuredContent = true), Description("Approve a ChatGPT write proposal and apply it through ContextHub write use cases.")]
    public Task<ChatGptProposalResult> chatgpt_proposal_approve(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken = default)
        => proposalService.ApproveAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Reject a pending ChatGPT write proposal without changing durable ContextHub memory.")]
    public Task<ChatGptProposalResult> chatgpt_proposal_reject(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken = default)
        => proposalService.RejectAsync(request, cancellationToken);

    private Task<ChatGptProposalResult> CreateProposalAsync<T>(
        string toolName,
        string projectId,
        string title,
        string summary,
        T payload,
        CancellationToken cancellationToken)
        => CreateProposalAsync(toolName, projectId, title, summary, payload, null, cancellationToken);

    private Task<ChatGptProposalResult> CreateProposalAsync<T>(
        string toolName,
        string projectId,
        string title,
        string summary,
        T payload,
        string? governanceRunId,
        CancellationToken cancellationToken)
    {
        var user = ResolveOAuthUser();
        return proposalService.CreateAsync(
            new ChatGptProposalCreateRequest(
                toolName,
                projectId,
                JsonSerializer.Serialize(payload, JsonOptions),
                title,
                summary,
                user.Subject,
                user.Email,
                user.Name,
                governanceRunId),
            cancellationToken);
    }

    private OAuthUser ResolveOAuthUser()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user is null)
        {
            return new OAuthUser(string.Empty, string.Empty, string.Empty);
        }

        return new OAuthUser(
            ReadClaim(user, GatewayAuthentication.SubjectClaim, ClaimTypes.NameIdentifier, "sub"),
            ReadClaim(user, ClaimTypes.Email, "email"),
            ReadClaim(user, ClaimTypes.Name, "name"));
    }

    private static string ReadClaim(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirstValue(type);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private sealed record OAuthUser(string Subject, string Email, string Name);
}

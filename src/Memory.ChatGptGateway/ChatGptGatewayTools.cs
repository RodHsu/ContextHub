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
    IAccessibleProjectService accessibleProjectService,
    IDailyMemoryReviewService dailyMemoryReviewService,
    ISuggestedActionService suggestedActionService,
    IMemoryDataRetentionService retentionService,
    IProjectArtifactExchangeService artifactExchangeService,
    IChatGptProposalService proposalService,
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

    [McpServerTool(UseStructuredContent = true), Description("List persisted global user preferences for remote knowledge-governance review.")]
    public Task<IReadOnlyList<UserPreferenceResult>> user_preferences_list(UserPreferenceListRequest request, CancellationToken cancellationToken = default)
        => memoryService.ListUserPreferencesAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List staged conversation insights for the current actor's authorized projects.")]
    public Task<IReadOnlyList<ConversationInsightResult>> conversation_insights_list(ConversationInsightListRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.ListInsightsAsync(request, cancellationToken);

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

    [McpServerTool(UseStructuredContent = true), Description("Propose an update to durable project information. Approved data is included in build_working_context.")]
    public Task<ChatGptProposalResult> project_information_upsert(ProjectInformationUpdateRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("project_information_upsert", request.ProjectId, request.DisplayName ?? request.ProjectId, "Update project information.", request, cancellationToken);

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
                user.Name),
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

using System.ComponentModel;
using Memory.Application;
using ModelContextProtocol.Server;

namespace Memory.McpServer;

[McpServerToolType]
public sealed class MemoryMcpTools(
    IContextHubBootstrapService bootstrapService,
    IMemoryService memoryService,
    ILogQueryService logQueryService,
    IConversationAutomationService conversationAutomationService,
    IProjectArtifactExchangeService artifactExchangeService,
    IChatGptProposalService chatGptProposalService,
    IMaintenanceCoordinator maintenanceCoordinator)
{
    [McpServerTool, Description("Describe ContextHub purpose, capabilities, startup flow, and projectId guidance for first-time agent onboarding.")]
    public ContextHubBootstrapResult describe_context_hub(string? projectId = null)
        => bootstrapService.Describe(new ContextHubBootstrapRequest(projectId));

    [McpServerTool, Description("Search memory items using hybrid keyword and semantic retrieval.")]
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
                new RetrievalTelemetryContext("memory_search", "mcp", "ad hoc retrieval")),
            cancellationToken);

    [McpServerTool, Description("Get a single memory item by id.")]
    public Task<MemoryDocument?> memory_get(Guid id, CancellationToken cancellationToken = default)
        => memoryService.GetAsync(id, cancellationToken);

    [McpServerTool, Description("Publish a project-scoped artifact summary, snippet, file reference, or external object pointer for other agents using the same ProjectId.")]
    public Task<ProjectArtifactResult> project_artifact_publish(ProjectArtifactPublishRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.PublishAsync(request, cancellationToken);

    [McpServerTool, Description("Upload managed artifact content to configured object storage, then publish only the expiring object pointer for agents using the same ProjectId.")]
    public Task<ProjectArtifactResult> project_artifact_upload_object(ProjectArtifactManagedObjectPublishRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.UploadManagedObjectAsync(request, cancellationToken);

    [McpServerTool, Description("List project-scoped artifact exchange records for the same ProjectId.")]
    public Task<IReadOnlyList<ProjectArtifactResult>> project_artifacts_list(ProjectArtifactListRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.ListAsync(request, cancellationToken);

    [McpServerTool, Description("Search project-scoped artifact exchange records by content, title, summary, or source reference.")]
    public Task<IReadOnlyList<ProjectArtifactResult>> project_artifacts_search(ProjectArtifactSearchRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.SearchAsync(request, cancellationToken);

    [McpServerTool, Description("Get one project-scoped artifact exchange record by memory id.")]
    public Task<ProjectArtifactResult?> project_artifact_get(Guid memoryId, CancellationToken cancellationToken = default)
        => artifactExchangeService.GetAsync(memoryId, cancellationToken);

    [McpServerTool, Description("Delete expired managed project artifact objects from configured object storage and archive their artifact exchange records. Intended for Codex or agent maintenance, not ChatGPT direct use.")]
    public Task<ProjectArtifactExpiredObjectPruneResult> project_artifacts_prune_expired_objects(ProjectArtifactExpiredObjectPruneRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.PruneExpiredObjectsAsync(request, cancellationToken);

    [McpServerTool, Description("Create or replace a memory item using an external key.")]
    public Task<MemoryDocument> memory_upsert(MemoryUpsertRequest request, CancellationToken cancellationToken = default)
        => memoryService.UpsertAsync(request, cancellationToken);

    [McpServerTool, Description("Update an existing memory item by id.")]
    public Task<MemoryDocument> memory_update(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
        => memoryService.UpdateAsync(request, cancellationToken);

    [McpServerTool, Description("Build a structured working context for the current task.")]
    public Task<WorkingContextResult> build_working_context(WorkingContextRequest request, CancellationToken cancellationToken = default)
        => memoryService.BuildWorkingContextAsync(
            request with
            {
                Telemetry = new RetrievalTelemetryContext("build_working_context", "mcp", "task context bootstrap")
            },
            cancellationToken);

    [McpServerTool, Description("Read current ContextHub maintenance state, including scheduled/draining/running phase and active leases.")]
    public Task<MaintenanceStatusResult> maintenance_status(CancellationToken cancellationToken = default)
        => maintenanceCoordinator.GetStatusAsync(cancellationToken);

    [McpServerTool, Description("Heartbeat an agent lease so maintenance can wait for active work to finish before entering running maintenance.")]
    public Task<MaintenanceLeaseHeartbeatResult> maintenance_lease_heartbeat(MaintenanceLeaseHeartbeatRequest request, CancellationToken cancellationToken = default)
        => maintenanceCoordinator.HeartbeatLeaseAsync(request, cancellationToken);

    [McpServerTool, Description("Complete an agent lease after work finishes so scheduled maintenance can proceed.")]
    public Task<MaintenanceStatusResult> maintenance_lease_complete(MaintenanceLeaseCompleteRequest request, CancellationToken cancellationToken = default)
        => maintenanceCoordinator.CompleteLeaseAsync(request, cancellationToken);

    [McpServerTool, Description("Ingest a completed conversation turn or checkpoint into the conversation staging layer for automatic promotion.")]
    public Task<ConversationIngestResult> conversation_ingest(ConversationIngestRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.IngestAsync(request, cancellationToken);

    [McpServerTool, Description("List staged conversation sessions for audit or debugging.")]
    public Task<IReadOnlyList<ConversationSessionResult>> conversation_sessions_list(ConversationSessionListRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.ListSessionsAsync(request, cancellationToken);

    [McpServerTool, Description("List staged conversation insights and their promotion state.")]
    public Task<IReadOnlyList<ConversationInsightResult>> conversation_insights_list(ConversationInsightListRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.ListInsightsAsync(request, cancellationToken);

    [McpServerTool, Description("Enqueue a background reindex job for the current or target embedding model.")]
    public Task<EnqueueReindexResult> enqueue_reindex(EnqueueReindexRequest request, CancellationToken cancellationToken = default)
        => memoryService.EnqueueReindexAsync(request, cancellationToken);

    [McpServerTool, Description("Enqueue a background job to rebuild the read-only shared summary layer for a project and its referenced projects, or all projects when projectId is omitted.")]
    public Task<EnqueueSummaryRefreshResult> enqueue_summary_refresh(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken = default)
        => memoryService.EnqueueSummaryRefreshAsync(request, cancellationToken);

    [McpServerTool, Description("Read raw runtime logs using filters such as service name, trace id, and time window.")]
    public Task<IReadOnlyList<LogEntryResult>> log_read(LogQueryRequest request, CancellationToken cancellationToken = default)
        => logQueryService.SearchAsync(request, cancellationToken);

    [McpServerTool, Description("Search runtime logs by text, service, level, or identifiers.")]
    public Task<IReadOnlyList<LogEntryResult>> log_search(LogQueryRequest request, CancellationToken cancellationToken = default)
        => logQueryService.SearchAsync(request, cancellationToken);

    [McpServerTool, Description("Promote a selected log slice into a durable memory item for later retrieval.")]
    public Task<MemoryDocument> promote_log_slice_to_memory(PromoteLogSliceRequest request, CancellationToken cancellationToken = default)
        => memoryService.PromoteLogSliceAsync(request, cancellationToken);

    [McpServerTool, Description("Create or update an explicit user preference that should be reused across sessions and repositories.")]
    public Task<UserPreferenceResult> user_preference_upsert(UserPreferenceUpsertRequest request, CancellationToken cancellationToken = default)
        => memoryService.UpsertUserPreferenceAsync(request, cancellationToken);

    [McpServerTool, Description("List persisted user preferences that guide coding style, tooling choices, and constraints.")]
    public Task<IReadOnlyList<UserPreferenceResult>> user_preference_list(UserPreferenceListRequest request, CancellationToken cancellationToken = default)
        => memoryService.ListUserPreferencesAsync(request, cancellationToken);

    [McpServerTool, Description("Archive or restore a user preference by id.")]
    public Task<UserPreferenceResult> user_preference_archive(UserPreferenceArchiveRequest request, CancellationToken cancellationToken = default)
        => memoryService.ArchiveUserPreferenceAsync(request, cancellationToken);

    [McpServerTool, Description("List pending, applied, rejected, or failed ChatGPT write proposals for review.")]
    public Task<IReadOnlyList<ChatGptProposalResult>> chatgpt_proposals_list(ChatGptProposalListRequest request, CancellationToken cancellationToken = default)
        => chatGptProposalService.ListAsync(request, cancellationToken);

    [McpServerTool, Description("Approve a ChatGPT write proposal and apply it through ContextHub write use cases.")]
    public Task<ChatGptProposalResult> chatgpt_proposal_approve(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken = default)
        => chatGptProposalService.ApproveAsync(request, cancellationToken);

    [McpServerTool, Description("Reject a pending ChatGPT write proposal without changing durable ContextHub memory.")]
    public Task<ChatGptProposalResult> chatgpt_proposal_reject(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken = default)
        => chatGptProposalService.RejectAsync(request, cancellationToken);
}

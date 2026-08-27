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
    IProjectDiscussionService projectDiscussionService,
    IProjectWorkItemService projectWorkItemService,
    IKnowledgeReviewService knowledgeReviewService,
    IGovernanceBatchExecutor governanceBatchExecutor,
    IGovernanceService governanceService,
    IProjectInformationService projectInformationService,
    IProjectArtifactExchangeService artifactExchangeService,
    IChatGptProposalService chatGptProposalService,
    IMaintenanceCoordinator maintenanceCoordinator)
{
    [McpServerTool(UseStructuredContent = true), Description("Describe ContextHub purpose, capabilities, startup flow, and projectId guidance for first-time agent onboarding.")]
    public ContextHubBootstrapResult describe_context_hub(string? projectId = null)
        => bootstrapService.Describe(new ContextHubBootstrapRequest(projectId));

    [McpServerTool(UseStructuredContent = true), Description("Search memory items using hybrid keyword and semantic retrieval.")]
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

    [McpServerTool(UseStructuredContent = true), Description("Get a single memory item by id.")]
    public Task<MemoryDocument?> memory_get(Guid id, CancellationToken cancellationToken = default)
        => memoryService.GetAsync(id, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read the durable project information that agents must use as fixed background before task-specific memory retrieval.")]
    public Task<ProjectInformationResult?> project_information_get(string projectId, CancellationToken cancellationToken = default)
        => projectInformationService.GetAsync(projectId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create or update the durable name and description for one ProjectId. This information is included in build_working_context.")]
    public Task<ProjectInformationResult> project_information_upsert(ProjectInformationAgentUpdateRequest request, CancellationToken cancellationToken = default)
        => projectInformationService.UpdateFromAgentAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Hide, unhide, archive, or restore a project. Archived projects are excluded from default memory search and build_working_context. Safe deletion is only eligible seven days after archival and is not performed by this tool.")]
    public Task<ProjectInformationResult> project_information_update_lifecycle(ProjectLifecycleUpdateRequest request, CancellationToken cancellationToken = default)
        => projectInformationService.UpdateLifecycleAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Publish a project-scoped artifact summary, snippet, file reference, or external object pointer for other agents using the same ProjectId.")]
    public Task<ProjectArtifactResult> project_artifact_publish(ProjectArtifactPublishRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.PublishAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Upload managed artifact content to configured object storage, then publish only the expiring object pointer for agents using the same ProjectId.")]
    public Task<ProjectArtifactResult> project_artifact_upload_object(ProjectArtifactManagedObjectPublishRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.UploadManagedObjectAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List project-scoped artifact exchange records for the same ProjectId.")]
    public Task<IReadOnlyList<ProjectArtifactResult>> project_artifacts_list(ProjectArtifactListRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.ListAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Search project-scoped artifact exchange records by content, title, summary, or source reference.")]
    public Task<IReadOnlyList<ProjectArtifactResult>> project_artifacts_search(ProjectArtifactSearchRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.SearchAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Get one project-scoped artifact exchange record by memory id.")]
    public Task<ProjectArtifactResult?> project_artifact_get(Guid memoryId, CancellationToken cancellationToken = default)
        => artifactExchangeService.GetAsync(memoryId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Delete expired managed project artifact objects from configured object storage and archive their artifact exchange records. Intended for Codex or agent maintenance, not ChatGPT direct use.")]
    public Task<ProjectArtifactExpiredObjectPruneResult> project_artifacts_prune_expired_objects(ProjectArtifactExpiredObjectPruneRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.PruneExpiredObjectsAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create or replace a memory item using an external key.")]
    public Task<MemoryDocument> memory_upsert(MemoryUpsertRequest request, CancellationToken cancellationToken = default)
        => memoryService.UpsertAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Update an existing memory item by id.")]
    public Task<MemoryDocument> memory_update(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
        => memoryService.UpdateAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Archive or restore an existing memory item by id.")]
    public Task<MemoryDocument> memory_archive(MemoryArchiveRequest request, CancellationToken cancellationToken = default)
        => memoryService.ArchiveAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Restore an archived memory item by id.")]
    public Task<MemoryDocument> memory_restore(Guid id, string? projectId = null, string? reason = null, CancellationToken cancellationToken = default)
        => memoryService.ArchiveAsync(new MemoryArchiveRequest(id, projectId, Archived: false, reason), cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Move a memory item to another ProjectId after validating access and duplicate external keys.")]
    public Task<MemoryDocument> memory_move(MemoryMoveRequest request, CancellationToken cancellationToken = default)
        => memoryService.MoveAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Permanently delete one memory item by id. Use project_cleanup_preview first for bulk cleanup.")]
    public Task<MemoryDeleteResult> memory_delete(MemoryDeleteRequest request, CancellationToken cancellationToken = default)
        => memoryService.DeleteAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Preview safe cleanup candidates for one ProjectId, such as migrated tombstones, removed markers, archived items, or low-value remnants.")]
    public Task<ProjectCleanupPreviewResult> project_cleanup_preview(ProjectCleanupPreviewRequest request, CancellationToken cancellationToken = default)
        => memoryService.PreviewProjectCleanupAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Archive or delete selected safe cleanup candidates in one ProjectId. Unsafe active memories are skipped.")]
    public Task<ProjectCleanupApplyResult> project_cleanup_apply(ProjectCleanupApplyRequest request, CancellationToken cancellationToken = default)
        => memoryService.ApplyProjectCleanupAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Build a structured working context for the current task.")]
    public Task<WorkingContextResult> build_working_context(WorkingContextRequest request, CancellationToken cancellationToken = default)
        => memoryService.BuildWorkingContextAsync(
            request with
            {
                Telemetry = new RetrievalTelemetryContext("build_working_context", "mcp", "task context bootstrap")
            },
            cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read current ContextHub maintenance state, including scheduled/draining/running phase and active leases.")]
    public Task<MaintenanceStatusResult> maintenance_status(CancellationToken cancellationToken = default)
        => maintenanceCoordinator.GetStatusAsync(cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Heartbeat an agent lease so maintenance can wait for active work to finish before entering running maintenance.")]
    public Task<MaintenanceLeaseHeartbeatResult> maintenance_lease_heartbeat(MaintenanceLeaseHeartbeatRequest request, CancellationToken cancellationToken = default)
        => maintenanceCoordinator.HeartbeatLeaseAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Complete an agent lease after work finishes so scheduled maintenance can proceed.")]
    public Task<MaintenanceStatusResult> maintenance_lease_complete(MaintenanceLeaseCompleteRequest request, CancellationToken cancellationToken = default)
        => maintenanceCoordinator.CompleteLeaseAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Ingest a completed conversation turn or checkpoint into the conversation staging layer for automatic promotion.")]
    public Task<ConversationIngestResult> conversation_ingest(ConversationIngestRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.IngestAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List staged conversation sessions for audit or debugging.")]
    public Task<IReadOnlyList<ConversationSessionResult>> conversation_sessions_list(ConversationSessionListRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.ListSessionsAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List staged conversation insights and their promotion state.")]
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

    [McpServerTool(UseStructuredContent = true), Description("Idempotently mark an actionable conversation insight as Deferred, RequiresUserDecision, or HostBlocked with an audited reason. Exception states are excluded from automatic retry and may be manually retried later.")]
    public Task<ConversationInsightResult> conversation_insight_set_disposition(ConversationInsightDispositionRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.SetInsightDispositionAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Set the child repositories managed by a parent ProjectId. This controls project structure only; it does not copy memories or grant token access.")]
    public Task<ProjectHierarchyResult> project_hierarchy_set_children(ProjectHierarchySetChildrenRequest request, CancellationToken cancellationToken = default)
        => projectDiscussionService.SetChildrenAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List the explicitly configured child repositories for a parent ProjectId.")]
    public Task<ProjectHierarchyResult> project_hierarchy_get_children(string parentProjectId, CancellationToken cancellationToken = default)
        => projectDiscussionService.GetChildrenAsync(parentProjectId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a persistent cross-project discussion hosted by HostProjectId. Only the listed participant projects can read or reply; discussion messages never become memories or knowledge.")]
    public Task<DiscussionThreadDetailResult> discussion_thread_create(DiscussionThreadCreateRequest request, CancellationToken cancellationToken = default)
        => projectDiscussionService.CreateThreadAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List cross-project discussion threads visible to one participant ProjectId, optionally filtered by host project or status. Archived threads are excluded unless IncludeArchived is true.")]
    public Task<IReadOnlyList<DiscussionThreadResult>> discussion_threads_list(DiscussionThreadListRequest request, CancellationToken cancellationToken = default)
        => projectDiscussionService.ListThreadsAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read one participant-scoped cross-project discussion and mark it read for readerProjectId.")]
    public Task<DiscussionThreadDetailResult?> discussion_thread_get(Guid threadId, string readerProjectId, CancellationToken cancellationToken = default)
        => projectDiscussionService.GetThreadAsync(threadId, readerProjectId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Close a cross-project discussion. Only an actor with write access to the thread HostProjectId can close it; closed threads retain their history and reject new replies.")]
    public Task<DiscussionThreadResult?> discussion_thread_close(Guid threadId, CancellationToken cancellationToken = default)
        => projectDiscussionService.CloseThreadAsync(threadId, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Archive a cross-project discussion. Archived threads are hidden from default lists and reject mutations until restored.")]
    public Task<DiscussionThreadResult?> discussion_thread_archive(Guid threadId, CancellationToken cancellationToken = default)
        => projectDiscussionService.SetThreadArchivedAsync(threadId, archived: true, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Restore an archived cross-project discussion without changing its Open or Closed status.")]
    public Task<DiscussionThreadResult?> discussion_thread_restore(Guid threadId, CancellationToken cancellationToken = default)
        => projectDiscussionService.SetThreadArchivedAsync(threadId, archived: false, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Reply to a cross-project discussion as SenderProjectId. The sender must be a participant and writable for the current actor.")]
    public Task<DiscussionMessageResult> discussion_message_create(DiscussionMessageCreateRequest request, CancellationToken cancellationToken = default)
        => projectDiscussionService.AddMessageAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create a project work item. Work items are distinct from automated governance suggested actions.")]
    public Task<ProjectWorkItemResult> project_work_item_create(ProjectWorkItemCreateRequest request, CancellationToken cancellationToken = default)
        => projectWorkItemService.CreateAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Update a project work item status, content, priority, or due date.")]
    public Task<ProjectWorkItemResult> project_work_item_update(ProjectWorkItemUpdateRequest request, CancellationToken cancellationToken = default)
        => projectWorkItemService.UpdateAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Explicitly and auditably exclude or restore one governance acceptance tracker for one existing governanceRunId. Only tenant owners or administrators may change this project-scoped relationship; ordinary work items remain actionable.")]
    public Task<ProjectWorkItemResult> project_work_item_set_governance_exclusion(ProjectWorkItemGovernanceExclusionRequest request, CancellationToken cancellationToken = default)
        => projectWorkItemService.SetGovernanceExclusionAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Complete or reopen one checklist item on a project work item. Archived work items reject checklist mutations.")]
    public Task<ProjectWorkItemResult> project_work_item_checklist_update(Guid workItemId, Guid checklistItemId, bool isCompleted, CancellationToken cancellationToken = default)
        => projectWorkItemService.SetChecklistItemCompletionAsync(workItemId, checklistItemId, isCompleted, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Archive a project work item. Archived work items are hidden from default lists and reject mutations until restored.")]
    public Task<ProjectWorkItemResult> project_work_item_archive(Guid workItemId, CancellationToken cancellationToken = default)
        => projectWorkItemService.SetArchivedAsync(workItemId, archived: true, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Restore an archived project work item without changing its business status.")]
    public Task<ProjectWorkItemResult> project_work_item_restore(Guid workItemId, CancellationToken cancellationToken = default)
        => projectWorkItemService.SetArchivedAsync(workItemId, archived: false, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List project work items. Archived work items are excluded unless IncludeArchived is true. These are user-managed project tasks, not governance suggested actions.")]
    public Task<IReadOnlyList<ProjectWorkItemResult>> project_work_items_list(ProjectWorkItemListRequest request, CancellationToken cancellationToken = default)
        => projectWorkItemService.ListAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Run server-side full-coverage governance across every authorized active/archived Project and Shared durable memory, then return compact stable-snapshot candidate pages plus user preferences, discussions, work items, insights, actions, and proposals.")]
    public Task<KnowledgeReviewResult> knowledge_review(KnowledgeReviewRequest request, CancellationToken cancellationToken = default)
        => knowledgeReviewService.ReviewAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Execute one persisted, bounded governance batch from a full-review snapshot. Scheduled mode is low-risk, proposal-first, archive-first, replay-idempotent, and never hard-deletes. Returns compact per-item read-back evidence, audit ids, and a saved continuation cursor.")]
    public Task<GovernanceBatchExecuteResult> governance_batch_execute(GovernanceBatchExecuteRequest request, CancellationToken cancellationToken = default)
        => governanceBatchExecutor.ExecuteAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Idempotently classify a durable-memory governance finding as Deferred, RequiresUserDecision, or HostBlocked with an audited reason and governanceRunId.")]
    public Task<GovernanceFindingResult> governance_finding_set_disposition(GovernanceFindingDispositionRequest request, CancellationToken cancellationToken = default)
        => governanceService.SetDispositionAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Reopen a durable-memory governance finding exception for explicit retry and increment its audited retry count.")]
    public Task<GovernanceFindingResult> governance_finding_reopen(GovernanceFindingReopenRequest request, CancellationToken cancellationToken = default)
        => governanceService.ReopenAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Enqueue a background reindex job for the current or target embedding model.")]
    public Task<EnqueueReindexResult> enqueue_reindex(EnqueueReindexRequest request, CancellationToken cancellationToken = default)
        => memoryService.EnqueueReindexAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Enqueue a background job to rebuild the read-only shared summary layer for a project and its referenced projects, or all projects when projectId is omitted.")]
    public Task<EnqueueSummaryRefreshResult> enqueue_summary_refresh(EnqueueSummaryRefreshRequest request, CancellationToken cancellationToken = default)
        => memoryService.EnqueueSummaryRefreshAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Read raw runtime logs using filters such as service name, trace id, and time window.")]
    public Task<IReadOnlyList<LogEntryResult>> log_read(LogQueryRequest request, CancellationToken cancellationToken = default)
        => logQueryService.SearchAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Search runtime logs by text, service, level, or identifiers.")]
    public Task<IReadOnlyList<LogEntryResult>> log_search(LogQueryRequest request, CancellationToken cancellationToken = default)
        => logQueryService.SearchAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Promote a selected log slice into a durable memory item for later retrieval.")]
    public Task<MemoryDocument> promote_log_slice_to_memory(PromoteLogSliceRequest request, CancellationToken cancellationToken = default)
        => memoryService.PromoteLogSliceAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Create or update an explicit user preference that should be reused across sessions and repositories.")]
    public Task<UserPreferenceResult> user_preference_upsert(UserPreferenceUpsertRequest request, CancellationToken cancellationToken = default)
        => memoryService.UpsertUserPreferenceAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List persisted user preferences that guide coding style, tooling choices, and constraints.")]
    public Task<IReadOnlyList<UserPreferenceResult>> user_preference_list(UserPreferenceListRequest request, CancellationToken cancellationToken = default)
        => memoryService.ListUserPreferencesAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Archive or restore a user preference by id.")]
    public Task<UserPreferenceResult> user_preference_archive(UserPreferenceArchiveRequest request, CancellationToken cancellationToken = default)
        => memoryService.ArchiveUserPreferenceAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("List pending, applied, rejected, or failed ChatGPT write proposals for review.")]
    public Task<IReadOnlyList<ChatGptProposalResult>> chatgpt_proposals_list(ChatGptProposalListRequest request, CancellationToken cancellationToken = default)
        => chatGptProposalService.ListAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Approve a ChatGPT write proposal and apply it through ContextHub write use cases.")]
    public Task<ChatGptProposalResult> chatgpt_proposal_approve(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken = default)
        => chatGptProposalService.ApproveAsync(request, cancellationToken);

    [McpServerTool(UseStructuredContent = true), Description("Reject a pending ChatGPT write proposal without changing durable ContextHub memory.")]
    public Task<ChatGptProposalResult> chatgpt_proposal_reject(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken = default)
        => chatGptProposalService.RejectAsync(request, cancellationToken);
}

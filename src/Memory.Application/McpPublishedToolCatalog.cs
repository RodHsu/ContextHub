namespace Memory.Application;

public static class McpPublishedToolCatalog
{
    public const string AppFacingCatalogVersion = "2026-09-27-v8";

    public static IReadOnlySet<string> RestrictedToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "describe_context_hub",
        "projects_list",
        "daily_memory_review",
        "knowledge_review",
        "governance_contract_get",
        "governance_batch_execute",
        "governance_run_get",
        "governance_runs_list",
        "governance_tombstone_get",
        "governance_finding_set_disposition",
        "governance_finding_reopen",
        "user_preferences_list",
        "conversation_insights_list",
        "conversation_insight_status",
        "conversation_insight_retry",
        "conversation_insight_skip",
        "conversation_insight_set_disposition",
        "suggested_actions_list",
        "memory_retention_preview",
        "project_cleanup_preview",
        "build_working_context",
        "memory_search",
        "memory_get",
        "project_information_get",
        "discussion_threads_list",
        "discussion_thread_get",
        "discussion_thread_close",
        "discussion_thread_archive",
        "discussion_thread_restore",
        "discussion_thread_create",
        "discussion_message_create",
        "project_hierarchy_get_children",
        "project_hierarchy_set_children",
        "project_work_items_list",
        "project_work_item_create",
        "project_work_item_update",
        "project_work_item_set_governance_exclusion",
        "project_work_item_checklist_update",
        "project_work_item_archive",
        "project_work_item_restore",
        "agent_execution_prepare",
        "agent_execution_claim_next",
        "agent_execution_get",
        "agent_execution_heartbeat",
        "agent_execution_checkpoint",
        "agent_execution_block",
        "agent_execution_complete",
        "agent_execution_fail",
        "agent_execution_abandon",
        "project_information_upsert",
        "project_information_update_lifecycle",
        "project_artifacts_list",
        "project_artifacts_search",
        "project_artifact_get",
        "managed_files_list",
        "managed_files_search",
        "log_search",
        "log_read",
        "conversation_ingest",
        "memory_upsert",
        "memory_update",
        "memory_archive",
        "memory_move",
        "memory_delete",
        "project_cleanup_apply",
        "user_preference_upsert",
        "user_preference_archive",
        "suggested_action_accept",
        "suggested_action_dismiss",
        "promote_log_slice_to_memory",
        "project_artifact_publish",
        "managed_file_register",
        "chatgpt_proposals_list",
        "chatgpt_governance_proposal_create",
        "chatgpt_proposal_approve",
        "chatgpt_proposal_reject",
        "skills_search_for_execution",
        "skills_resolution_feedback",
        "skills_select_for_execution",
        "skill_version_get",
        "skill_version_materialize",
        "skills_materialization_cleanup",
        "skills_invocation_record"
    };

    public static IReadOnlySet<string> BackendOnlyToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "conversation_sessions_list",
        "enqueue_reindex",
        "enqueue_summary_refresh",
        "maintenance_lease_complete",
        "maintenance_lease_heartbeat",
        "maintenance_status",
        "memory_restore",
        "user_preference_list"
    };

    public static IReadOnlySet<string> GatewayOnlyToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "chatgpt_governance_proposal_create",
        "daily_memory_review",
        "memory_retention_preview",
        "projects_list",
        "suggested_action_accept",
        "suggested_action_dismiss",
        "suggested_actions_list",
        "user_preferences_list"
    };

    public static IReadOnlySet<string> QueryToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "describe_context_hub",
        "projects_list",
        "daily_memory_review",
        "governance_contract_get",
        "governance_run_get",
        "governance_runs_list",
        "governance_tombstone_get",
        "user_preferences_list",
        "conversation_insights_list",
        "conversation_insight_status",
        "suggested_actions_list",
        "memory_retention_preview",
        "project_cleanup_preview",
        "build_working_context",
        "memory_search",
        "memory_get",
        "project_information_get",
        "discussion_threads_list",
        "discussion_thread_get",
        "project_hierarchy_get_children",
        "project_work_items_list",
        "agent_execution_get",
        "project_artifacts_list",
        "project_artifacts_search",
        "project_artifact_get",
        "managed_files_list",
        "managed_files_search",
        "log_search",
        "log_read",
        "chatgpt_proposals_list",
        "skills_search_for_execution",
        "skill_version_get"
    };

    public static IReadOnlySet<string> ProposalWriteToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "memory_upsert",
        "memory_update",
        "memory_archive",
        "memory_move",
        "memory_delete",
        "project_cleanup_apply",
        "user_preference_upsert",
        "user_preference_archive",
        "suggested_action_accept",
        "suggested_action_dismiss",
        "promote_log_slice_to_memory",
        "project_artifact_publish",
        "managed_file_register"
    };

    public static IReadOnlySet<string> DeleteCapableToolNames { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "governance_batch_execute",
        "memory_delete",
        "project_cleanup_apply"
    };

    public static IReadOnlySet<string> DirectMutationToolNames { get; } = RestrictedToolNames
        .Except(QueryToolNames, StringComparer.Ordinal)
        .Except(ProposalWriteToolNames, StringComparer.Ordinal)
        .ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> BackendToolNames { get; } = RestrictedToolNames
        .Except(GatewayOnlyToolNames, StringComparer.Ordinal)
        .Concat(BackendOnlyToolNames)
        .ToHashSet(StringComparer.Ordinal);

    public static ContextHubBootstrapToolCatalogInfo Describe()
        => new(
            BackendToolNames.Count,
            RestrictedToolNames.Count,
            QueryToolNames.Count,
            RestrictedToolNames.Count - QueryToolNames.Count,
            DeleteCapableToolNames.Count,
            ProposalWriteToolNames.Count,
            AppFacingCatalogVersion);
}

namespace Memory.Application;

public static class ScheduledGovernanceContract
{
    public const string ReviewToolName = "scheduled_governance_review";
    public const string ExecuteToolName = "scheduled_governance_execute";
    public const string ReceiptToolName = "scheduled_governance_run_get";
    public const string ContractToolName = "scheduled_governance_contract_get";
    public const string ToolContractVersion = "1.0";
    public const string PublishedCatalogVersion = "2026-08-30-automation-v1";
    public const string SchemaHash = "3c9d010b230ae2366161a60658b273056f12fcd671ff4f70ffda8aad0ec41fcb";

    public static IReadOnlyList<GovernanceBatchActionType> FixedReversibleActions { get; } =
    [
        GovernanceBatchActionType.Merge,
        GovernanceBatchActionType.Update,
        GovernanceBatchActionType.Move,
        GovernanceBatchActionType.Archive,
        GovernanceBatchActionType.Reindex,
        GovernanceBatchActionType.SuggestedActionReconcile,
        GovernanceBatchActionType.ConversationInsightDisposition,
        GovernanceBatchActionType.Restore,
        GovernanceBatchActionType.LifecycleReconcile,
        GovernanceBatchActionType.HierarchyReconcile,
        GovernanceBatchActionType.PreferenceReconcile,
        GovernanceBatchActionType.ArtifactReconcile,
        GovernanceBatchActionType.DiscussionReconcile,
        GovernanceBatchActionType.WorkItemReconcile,
        GovernanceBatchActionType.Quarantine,
        GovernanceBatchActionType.SemanticReevaluate
    ];

    public const string ReviewDescription =
        "Create or replay an actor-scoped immutable full-governance snapshot, resolve the complete authorized durable scope server-side, and return only coverage/count invariants plus a fixed server decision. This tool does not mutate governed resources.";

    public const string ExecuteDescription =
        "Execute a bounded idempotent batch of fixed low-risk reversible governance actions from the supplied immutable snapshot. The input cannot select projects, actions, risk, deletion, retention maturity, or execution mode. Irreversible retention is unavailable on this surface.";

    public static ScheduledGovernanceContractResult Describe()
        => new(
            ReviewToolName,
            ExecuteToolName,
            ReceiptToolName,
            ToolContractVersion,
            SchemaHash,
            PublishedCatalogVersion,
            FixedReversibleActions.Select(x => x.ToString()).ToArray(),
            Enum.GetNames<ScheduledGovernanceDecision>(),
            "ContextHubInternalRetentionWorker");
}

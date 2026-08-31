namespace Memory.Application;

public static class ScheduledGovernanceContract
{
    public const string ReviewToolName = "scheduled_governance_review";
    public const string ExecuteToolName = "scheduled_governance_execute";
    public const string ReceiptToolName = "scheduled_governance_run_get";
    public const string ContractToolName = "scheduled_governance_contract_get";
    public const string ToolContractVersion = "1.2";
    public const string PublishedCatalogVersion = "2026-08-31-automation-v3";
    public const string SchemaHash = "de1a67e9a2d6f5160d975fc3f4414c220ebbd7f68c6b66bc86e4e506b6244ee8";
    public const string RuntimeServiceName = "Memory.ScheduledGovernanceGateway";

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
        "Read a full-governance snapshot without modifying, moving, archiving, or deleting governed resources. The server resolves the complete authorized durable scope and returns only coverage/count invariants plus a fixed decision; the same run can be replayed or re-reviewed.";

    public const string ExecuteDescription =
        "Execute a bounded idempotent batch of fixed low-risk reversible governance actions from the supplied immutable snapshot. The input cannot select projects, actions, risk, deletion, retention maturity, or execution mode. Irreversible retention is unavailable on this surface.";

    public static ScheduledGovernanceRuntimeIdentity RuntimeIdentity
    {
        get
        {
            var build = BuildMetadata.Current;
            return new(
                RuntimeServiceName,
                build.Version,
                build.TimestampUtc,
                $"{RuntimeServiceName}/{build.Version}+catalog.{PublishedCatalogVersion}.{SchemaHash[..12]}");
        }
    }

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
            "ContextHubInternalRetentionWorker",
            RuntimeIdentity);
}

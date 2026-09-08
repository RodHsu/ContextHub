namespace Memory.Application;

public static class GovernanceToolContract
{
    public const string ToolName = "governance_batch_execute";
    public const string ToolContractVersion = "2.1";
    public const string PublishedCatalogVersion = "2026-09-08-v5";
    public const string SchemaHash = "6a8f6c0abd5be5687621b73c7cb73e6e19646057e9984703dbc5ec399cd5b2fb";
    public const string ExecuteDescription =
        "Execute one server-side bounded governance batch from the saved full-review snapshot. " +
        "ContractVersion=2.1; SchemaHash=6a8f6c0abd5be5687621b73c7cb73e6e19646057e9984703dbc5ec399cd5b2fb; PublishedCatalogVersion=2026-09-08-v5. General execution defaults to Interactive; Scheduled execution is available only through the dedicated receipt-bound surface. Scheduled direct hard-delete is prohibited. " +
        "MaturedDelete is a compatibility capability observed through receipts; irreversible deletion is performed only by the policy-bound internal retention worker after immediate revalidation. " +
        "Returns replay-safe counters, tombstone/audit references, and continuation.";

    public static GovernanceToolContractResult Describe()
        => new(ToolName, ToolContractVersion, SchemaHash, PublishedCatalogVersion,
            Enum.GetNames<GovernanceBatchActionType>());
}

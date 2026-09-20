namespace Memory.Application;

public static class GovernanceToolContract
{
    public const string ToolName = "governance_batch_execute";
    public const string ToolContractVersion = "2.2";
    public const string PublishedCatalogVersion = McpPublishedToolCatalog.AppFacingCatalogVersion;
    public const string SchemaHash = "f2cffdc0359f7a8e31b844908e848de2515470e5d19a34560f253eccff96ca3e";
    public const string ExecuteDescription =
        "Execute one server-side bounded governance batch from the saved full-review snapshot. " +
        "ContractVersion=2.2; SchemaHash=f2cffdc0359f7a8e31b844908e848de2515470e5d19a34560f253eccff96ca3e; PublishedCatalogVersion=2026-09-20-v7. General execution defaults to Interactive; Scheduled execution is available only through the dedicated receipt-bound surface. Scheduled direct hard-delete is prohibited. " +
        "MaturedDelete is a compatibility capability observed through receipts; irreversible deletion is performed only by the policy-bound internal retention worker after immediate revalidation. " +
        "Returns replay-safe counters, tombstone/audit references, and continuation.";

    public static GovernanceToolContractResult Describe()
        => new(ToolName, ToolContractVersion, SchemaHash, PublishedCatalogVersion,
            Enum.GetNames<GovernanceBatchActionType>());
}

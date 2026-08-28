namespace Memory.Application;

public static class GovernanceToolContract
{
    public const string ToolName = "governance_batch_execute";
    public const string ToolContractVersion = "2.0";
    public const string PublishedCatalogVersion = "2026-08-28-v2";
    public const string SchemaHash = "6aea349c2ff0a10279603ae1d40d5c3f21c03e58d7a93d10186e6fcf19ebaa86";
    public const string ExecuteDescription =
        "Execute one server-side bounded governance batch from the saved full-review snapshot. " +
        "ContractVersion=2.0; SchemaHash=6aea349c2ff0a10279603ae1d40d5c3f21c03e58d7a93d10186e6fcf19ebaa86; PublishedCatalogVersion=2026-08-28-v2. Scheduled direct hard-delete is prohibited. " +
        "MaturedDelete is a compatibility capability observed through receipts; irreversible deletion is performed only by the policy-bound internal retention worker after immediate revalidation. " +
        "Returns replay-safe counters, tombstone/audit references, and continuation.";

    public static GovernanceToolContractResult Describe()
        => new(ToolName, ToolContractVersion, SchemaHash, PublishedCatalogVersion,
            Enum.GetNames<GovernanceBatchActionType>());
}

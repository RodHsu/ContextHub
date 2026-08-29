using Memory.Domain;

namespace Memory.Application;

public static class DurableMemoryGovernancePolicy
{
    public const string ProjectInformationExternalKey = "system:project-information";
    public const string ScopeContractVersion = "governance-durable-memory-v2";

    public static bool IsSystemProjectMetadata(MemoryItem memory)
        => string.Equals(memory.ExternalKey, ProjectInformationExternalKey, StringComparison.Ordinal);

    public static bool RequiresKnowledgeBody(MemoryItem memory)
        => !IsSystemProjectMetadata(memory);

    public static bool IsRetrievalEligible(MemoryItem memory)
        => !IsSystemProjectMetadata(memory) &&
           (!string.IsNullOrWhiteSpace(memory.Content) || !string.IsNullOrWhiteSpace(memory.Summary));
}

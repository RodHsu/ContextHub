using Memory.Domain;

namespace Memory.Application;

public static class DurableMemoryGovernancePolicy
{
    public const string ProjectInformationExternalKey = "system:project-information";
    public const string ScopeContractVersion = "governance-durable-memory-v2";

    public static IReadOnlyList<string> NormalizeGovernanceProjectIds(IEnumerable<string> projectIds)
        => projectIds
            .Append(ProjectContext.SharedProjectId)
            .Where(x => !string.IsNullOrWhiteSpace(x) && !ProjectContext.IsUser(x))
            .Select(x => ProjectContext.Normalize(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> ToExecutionProjectIds(IEnumerable<string> projectIds)
        => NormalizeGovernanceProjectIds(projectIds)
            .Where(x => !ProjectContext.IsShared(x) && !ProjectContext.IsUser(x))
            .ToArray();

    public static bool IsSystemProjectMetadata(MemoryItem memory)
        => string.Equals(memory.ExternalKey, ProjectInformationExternalKey, StringComparison.Ordinal);

    public static bool RequiresKnowledgeBody(MemoryItem memory)
        => !IsSystemProjectMetadata(memory);

    public static bool IsRetrievalEligible(MemoryItem memory)
        => !IsSystemProjectMetadata(memory) &&
           (!string.IsNullOrWhiteSpace(memory.Content) || !string.IsNullOrWhiteSpace(memory.Summary));
}

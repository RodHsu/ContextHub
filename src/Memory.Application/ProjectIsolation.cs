using Memory.Domain;

namespace Memory.Application;

public enum MemoryQueryMode
{
    CurrentOnly,
    CurrentPlusReferencedProjects,
    SummaryOnly
}

public static class ProjectContext
{
    public const string DefaultProjectId = "default";
    public const string SharedProjectId = "shared";
    public const string UserProjectId = "user";
    public const string AllProjectIdsSentinel = "*";
    public const int IdentityContractVersion = 1;
    public static IEqualityComparer<string> IdentityComparer { get; } = new ProjectIdentityComparer();

    public static string Normalize(string? projectId, string fallback = DefaultProjectId)
        => string.IsNullOrWhiteSpace(projectId) ? fallback : projectId.Trim();

    /// <summary>Identity is separate from the spelling stored and returned by the API.</summary>
    public static string IdentityKey(string projectId)
    {
        ArgumentNullException.ThrowIfNull(projectId);
        return ProjectIdentityCaseMap.Fold(projectId);
    }

    public static bool Matches(string? left, string? right)
        => left is not null && right is not null &&
           string.Equals(IdentityKey(left), IdentityKey(right), StringComparison.Ordinal);

    public static string[] IdentityKeys(IEnumerable<string> projectIds)
        => projectIds.Select(IdentityKey).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    private sealed class ProjectIdentityComparer : IEqualityComparer<string>
    {
        public bool Equals(string? left, string? right)
            => left is null ? right is null : Matches(left, right);

        public int GetHashCode(string value) => StringComparer.Ordinal.GetHashCode(IdentityKey(value));
    }

    public static bool IsShared(string? projectId)
        => Matches(Normalize(projectId), SharedProjectId);

    public static bool IsUser(string? projectId)
        => Matches(Normalize(projectId), UserProjectId);

    public static IReadOnlyList<string> ResolveSearchProjects(
        string? currentProjectId,
        IReadOnlyList<string>? includedProjectIds,
        MemoryQueryMode queryMode,
        bool useSummaryLayer)
    {
        var current = Normalize(currentProjectId);
        var values = new HashSet<string>(IdentityComparer);

        switch (queryMode)
        {
            case MemoryQueryMode.CurrentOnly:
                values.Add(current);
                break;
            case MemoryQueryMode.CurrentPlusReferencedProjects:
                values.Add(current);
                foreach (var projectId in includedProjectIds ?? [])
                {
                    var normalized = Normalize(projectId);
                    if (!IsShared(normalized) && !IsUser(normalized))
                    {
                        values.Add(normalized);
                    }
                }
                break;
            case MemoryQueryMode.SummaryOnly:
                values.Add(SharedProjectId);
                break;
            default:
                values.Add(current);
                break;
        }

        if (useSummaryLayer && queryMode != MemoryQueryMode.SummaryOnly)
        {
            values.Add(SharedProjectId);
        }

        return values.ToArray();
    }
}

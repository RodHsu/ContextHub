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

    public static string Normalize(string? projectId, string fallback = DefaultProjectId)
        => string.IsNullOrWhiteSpace(projectId) ? fallback : projectId.Trim();

    public static bool IsShared(string? projectId)
        => string.Equals(Normalize(projectId), SharedProjectId, StringComparison.OrdinalIgnoreCase);

    public static bool IsUser(string? projectId)
        => string.Equals(Normalize(projectId), UserProjectId, StringComparison.OrdinalIgnoreCase);

    public static IReadOnlyList<string> ResolveSearchProjects(
        string? currentProjectId,
        IReadOnlyList<string>? includedProjectIds,
        MemoryQueryMode queryMode,
        bool useSummaryLayer)
    {
        var current = Normalize(currentProjectId);
        var values = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

using Memory.Application;

namespace Memory.Dashboard.Services;

internal static class DashboardProjectSelection
{
    internal const string CurrentRepositoryProjectId = "ContextHub";

    internal static string ResolveCurrentProjectId(string? runtimeDefaultProjectId)
    {
        var normalized = string.IsNullOrWhiteSpace(runtimeDefaultProjectId)
            ? null
            : runtimeDefaultProjectId.Trim();

        return string.IsNullOrWhiteSpace(normalized) ||
               string.Equals(normalized, ProjectContext.DefaultProjectId, StringComparison.OrdinalIgnoreCase)
            ? CurrentRepositoryProjectId
            : normalized;
    }
}

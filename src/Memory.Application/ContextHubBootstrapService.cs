using Memory.Domain;

namespace Memory.Application;

public sealed class ContextHubBootstrapService : IContextHubBootstrapService
{
    public ContextHubBootstrapResult Describe(ContextHubBootstrapRequest request)
    {
        var projectIdProvided = !string.IsNullOrWhiteSpace(request.ProjectId);
        var projectId = projectIdProvided ? ProjectContext.Normalize(request.ProjectId) : null;

        return new ContextHubBootstrapResult(
            new ContextHubBootstrapServiceInfo(
                "ContextHub",
                "Long-term working context and memory for agents",
                "1.0"),
            new ContextHubBootstrapProjectInfo(
                projectId,
                projectIdProvided,
                ProjectIdRequiredForWork: true,
                "Resolve a repo-specific projectId before calling build_working_context, memory read/write, or conversation_ingest.",
                projectIdProvided
                    ? $"build_working_context(projectId=\"{projectId}\", query=\"...\")"
                    : null),
            new ContextHubBootstrapCapabilities(
                WorkingContext: true,
                MemorySearch: true,
                MemoryReadWrite: true,
                ConversationCheckpoint: true,
                UserPreferences: true,
                RuntimeLogs: true,
                MaintenanceStatus: true),
            McpPublishedToolCatalog.Describe(),
            [
                "Call describe_context_hub without projectId during first MCP onboarding.",
                "Resolve projectId from repo rules or repo root name.",
                "Call build_working_context with explicit projectId before task work.",
                "Call conversation_ingest for reusable checkpoints."
            ],
            new ContextHubBootstrapUserPreferencesInfo(
                IncludedByDefaultInWorkingContext: true,
                BootstrapDisclosure: "summary-and-policy",
                Enum.GetNames<UserPreferenceKind>()),
            [
                "Do not use ProjectContext.DefaultProjectId for repo work unless explicitly configured.",
                "Do not store secrets, tokens, private keys, or large raw logs."
            ]);
    }
}

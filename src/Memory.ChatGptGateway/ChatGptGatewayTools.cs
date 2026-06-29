using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Memory.Application;
using ModelContextProtocol.Server;

namespace Memory.ChatGptGateway;

[McpServerToolType]
public sealed class ChatGptGatewayTools(
    IContextHubBootstrapService bootstrapService,
    IMemoryService memoryService,
    ILogQueryService logQueryService,
    IConversationAutomationService conversationAutomationService,
    IProjectArtifactExchangeService artifactExchangeService,
    IChatGptProposalService proposalService,
    IHttpContextAccessor httpContextAccessor)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [McpServerTool, Description("Describe ContextHub purpose, capabilities, startup flow, and projectId guidance.")]
    public ContextHubBootstrapResult describe_context_hub(string? projectId = null)
        => bootstrapService.Describe(new ContextHubBootstrapRequest(projectId));

    [McpServerTool, Description("Build a structured ContextHub working context for the current task.")]
    public Task<WorkingContextResult> build_working_context(WorkingContextRequest request, CancellationToken cancellationToken = default)
        => memoryService.BuildWorkingContextAsync(
            request with
            {
                Telemetry = new RetrievalTelemetryContext("build_working_context", "chatgpt-mcp-gateway", "ChatGPT task context bootstrap")
            },
            cancellationToken);

    [McpServerTool, Description("Search ContextHub memory items using hybrid keyword and semantic retrieval.")]
    public Task<IReadOnlyList<MemorySearchHit>> memory_search(
        string query,
        int limit = 10,
        bool includeArchived = false,
        string projectId = ProjectContext.DefaultProjectId,
        IReadOnlyList<string>? includedProjectIds = null,
        MemoryQueryMode queryMode = MemoryQueryMode.CurrentOnly,
        bool useSummaryLayer = false,
        CancellationToken cancellationToken = default)
        => memoryService.SearchAsync(
            new MemorySearchRequest(
                query,
                limit,
                includeArchived,
                projectId,
                includedProjectIds,
                queryMode,
                useSummaryLayer,
                new RetrievalTelemetryContext("memory_search", "chatgpt-mcp-gateway", "ChatGPT memory search")),
            cancellationToken);

    [McpServerTool, Description("Get a single ContextHub memory item by id.")]
    public Task<MemoryDocument?> memory_get(Guid id, CancellationToken cancellationToken = default)
        => memoryService.GetAsync(id, cancellationToken);

    [McpServerTool, Description("List project-scoped artifact exchange records shared by agents using the same ProjectId.")]
    public Task<IReadOnlyList<ProjectArtifactResult>> project_artifacts_list(ProjectArtifactListRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.ListAsync(request, cancellationToken);

    [McpServerTool, Description("Search project-scoped artifact summaries, snippets, file references, or external object pointers shared by agents using the same ProjectId.")]
    public Task<IReadOnlyList<ProjectArtifactResult>> project_artifacts_search(ProjectArtifactSearchRequest request, CancellationToken cancellationToken = default)
        => artifactExchangeService.SearchAsync(request, cancellationToken);

    [McpServerTool, Description("Get one project-scoped artifact exchange record by memory id.")]
    public Task<ProjectArtifactResult?> project_artifact_get(Guid memoryId, CancellationToken cancellationToken = default)
        => artifactExchangeService.GetAsync(memoryId, cancellationToken);

    [McpServerTool, Description("Search runtime logs by text, service, level, or identifiers.")]
    public Task<IReadOnlyList<LogEntryResult>> log_search(LogQueryRequest request, CancellationToken cancellationToken = default)
        => logQueryService.SearchAsync(request, cancellationToken);

    [McpServerTool, Description("Read raw runtime logs using filters such as service name, trace id, and time window.")]
    public Task<IReadOnlyList<LogEntryResult>> log_read(LogQueryRequest request, CancellationToken cancellationToken = default)
        => logQueryService.SearchAsync(request, cancellationToken);

    [McpServerTool, Description("Ingest a ChatGPT conversation checkpoint into ContextHub staging for later promotion.")]
    public Task<ConversationIngestResult> conversation_ingest(ConversationIngestRequest request, CancellationToken cancellationToken = default)
        => conversationAutomationService.IngestAsync(
            request with
            {
                SourceSystem = string.IsNullOrWhiteSpace(request.SourceSystem)
                    ? ChatGptProposalService.SourceSystem
                    : request.SourceSystem
            },
            cancellationToken);

    [McpServerTool, Description("Create a pending proposal to upsert a durable ContextHub memory item. Approval is required before the memory is changed.")]
    public Task<ChatGptProposalResult> memory_upsert(MemoryUpsertRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("memory_upsert", request.ProjectId, request.Title, request.Summary, request, cancellationToken);

    [McpServerTool, Description("Create a pending proposal to update a durable ContextHub memory item. Approval is required before the memory is changed.")]
    public Task<ChatGptProposalResult> memory_update(MemoryUpdateRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("memory_update", request.ProjectId ?? ProjectContext.DefaultProjectId, "Update memory item", $"Update memory item {request.Id:D}.", request, cancellationToken);

    [McpServerTool, Description("Create a pending proposal to create or update a durable ContextHub user preference. Approval is required before the preference is changed.")]
    public Task<ChatGptProposalResult> user_preference_upsert(UserPreferenceUpsertRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("user_preference_upsert", ProjectContext.UserProjectId, request.Title, request.Rationale, request, cancellationToken);

    [McpServerTool, Description("Create a pending proposal to promote a selected log slice into durable memory. Approval is required before memory is changed.")]
    public Task<ChatGptProposalResult> promote_log_slice_to_memory(PromoteLogSliceRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("promote_log_slice_to_memory", request.ProjectId, request.Title, $"Promote logs matching '{request.Query ?? request.TraceId ?? request.ServiceName ?? "selected filters"}'.", request, cancellationToken);

    [McpServerTool, Description("Create a pending proposal to publish a project artifact summary, snippet, file reference, or external object pointer. Approval is required before shared project knowledge is changed.")]
    public Task<ChatGptProposalResult> project_artifact_publish(ProjectArtifactPublishRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("project_artifact_publish", request.ProjectId, request.Title, request.Summary, request, cancellationToken);

    [McpServerTool, Description("Create a pending proposal to upload artifact content to configured object storage, then publish only the expiring object pointer. Approval is required before external storage or shared project knowledge is changed.")]
    public Task<ChatGptProposalResult> project_artifact_upload_object(ProjectArtifactManagedObjectPublishRequest request, CancellationToken cancellationToken = default)
        => CreateProposalAsync("project_artifact_upload_object", request.ProjectId, request.Title, request.Summary, request, cancellationToken);

    [McpServerTool, Description("List pending, applied, rejected, or failed ChatGPT write proposals for review.")]
    public Task<IReadOnlyList<ChatGptProposalResult>> chatgpt_proposals_list(ChatGptProposalListRequest request, CancellationToken cancellationToken = default)
        => proposalService.ListAsync(request, cancellationToken);

    [McpServerTool, Description("Approve a ChatGPT write proposal and apply it through ContextHub write use cases.")]
    public Task<ChatGptProposalResult> chatgpt_proposal_approve(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken = default)
        => proposalService.ApproveAsync(request, cancellationToken);

    [McpServerTool, Description("Reject a pending ChatGPT write proposal without changing durable ContextHub memory.")]
    public Task<ChatGptProposalResult> chatgpt_proposal_reject(ChatGptProposalDecisionRequest request, CancellationToken cancellationToken = default)
        => proposalService.RejectAsync(request, cancellationToken);

    private Task<ChatGptProposalResult> CreateProposalAsync<T>(
        string toolName,
        string projectId,
        string title,
        string summary,
        T payload,
        CancellationToken cancellationToken)
    {
        var user = ResolveOAuthUser();
        return proposalService.CreateAsync(
            new ChatGptProposalCreateRequest(
                toolName,
                projectId,
                JsonSerializer.Serialize(payload, JsonOptions),
                title,
                summary,
                user.Subject,
                user.Email,
                user.Name),
            cancellationToken);
    }

    private OAuthUser ResolveOAuthUser()
    {
        var user = httpContextAccessor.HttpContext?.User;
        if (user is null)
        {
            return new OAuthUser(string.Empty, string.Empty, string.Empty);
        }

        return new OAuthUser(
            ReadClaim(user, GatewayAuthentication.SubjectClaim, ClaimTypes.NameIdentifier, "sub"),
            ReadClaim(user, ClaimTypes.Email, "email"),
            ReadClaim(user, ClaimTypes.Name, "name"));
    }

    private static string ReadClaim(ClaimsPrincipal principal, params string[] types)
    {
        foreach (var type in types)
        {
            var value = principal.FindFirstValue(type);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private sealed record OAuthUser(string Subject, string Email, string Name);
}

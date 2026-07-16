using Memory.Domain;

namespace Memory.Application;

public sealed class DailyMemoryReviewService(
    IAccessibleProjectService accessibleProjectService,
    IMemoryDataRetentionService retentionService,
    IConversationAutomationService conversationAutomationService,
    ISuggestedActionService suggestedActionService,
    IMemoryService memoryService,
    IChatGptProposalService proposalService,
    IRequestActorAccessor actorAccessor) : IDailyMemoryReviewService
{
    private const decimal HighSignalImportance = 0.70m;
    private const decimal HighSignalConfidence = 0.75m;

    public async Task<DailyMemoryReviewResult> ReviewAsync(CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);

        var projects = await accessibleProjectService.ListAsync(200, cancellationToken);
        var readableProjectIds = projects
            .Where(project => project.CanRead)
            .Select(project => project.ProjectId)
            .ToArray();
        if (readableProjectIds.Length == 0)
        {
            throw new InvalidOperationException("No readable ProjectId is available for the daily memory review.");
        }

        var tenantId = actor.TenantId
            ?? throw new InvalidOperationException("Daily memory review requires an authenticated tenant actor.");
        var retention = await retentionService.RunAsync(
            new MemoryDataRetentionRunRequest(
                TriggeredBy: "chatgpt-mcp-gateway:daily-memory-review",
                Mode: MemoryDataRetentionRunMode.Classify,
                PreviewOnly: false,
                ProjectIds: readableProjectIds,
                TenantId: tenantId),
            "chatgpt-mcp-gateway:daily-memory-review",
            cancellationToken);

        var insights = await conversationAutomationService.ListInsightsAsync(
            new ConversationInsightListRequest(
                PromotionStatus: ConversationPromotionStatus.Pending,
                Limit: 400),
            cancellationToken);
        var highSignalInsights = insights
            .Where(insight => insight.Importance >= HighSignalImportance && insight.Confidence >= HighSignalConfidence)
            .ToArray();

        var pendingSuggestedActions = new List<SuggestedActionResult>();
        foreach (var projectId in readableProjectIds)
        {
            var actions = await suggestedActionService.ListAsync(
                new SuggestedActionListRequest(projectId, SuggestedActionStatus.Pending, Limit: 100),
                cancellationToken);
            pendingSuggestedActions.AddRange(actions);
        }

        var userPreferences = await memoryService.ListUserPreferencesAsync(
            new UserPreferenceListRequest(IncludeArchived: true, Limit: 200),
            cancellationToken);
        var pendingProposals = await proposalService.ListAsync(
            new ChatGptProposalListRequest(Status: ChatGptProposalStatus.Pending, Limit: 200),
            cancellationToken);

        return new DailyMemoryReviewResult(
            projects,
            retention,
            highSignalInsights,
            pendingSuggestedActions,
            userPreferences,
            pendingProposals);
    }
}

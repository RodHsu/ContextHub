using Memory.Domain;

namespace Memory.Application;

public sealed class KnowledgeReviewService(
    IAccessibleProjectService accessibleProjects,
    IMemoryDataRetentionService retentionService,
    IConversationAutomationService conversationService,
    ISuggestedActionService suggestedActions,
    IMemoryService memoryService,
    IChatGptProposalService proposals,
    IProjectDiscussionService discussions,
    IProjectWorkItemService workItems,
    IRequestActorAccessor actorAccessor) : IKnowledgeReviewService
{
    public async Task<KnowledgeReviewResult> ReviewAsync(KnowledgeReviewRequest request, CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var available = await accessibleProjects.ListAsync(200, cancellationToken);
        var readable = available.Where(x => x.CanRead).ToArray();
        var requested = request.ProjectIds?.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => ProjectContext.Normalize(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var projects = requested is { Length: > 0 }
            ? requested.Select(projectId =>
            {
                ActorAuthorization.EnsureProjectAllowed(actor, projectId, write: false);
                return readable.FirstOrDefault(x => string.Equals(x.ProjectId, projectId, StringComparison.OrdinalIgnoreCase))
                    ?? new AccessibleProjectResult(projectId, CanRead: true, CanWrite: actor.AllowedProjectIds.Count == 0);
            }).ToArray()
            : readable;
        if (projects.Length == 0) throw new InvalidOperationException("No readable ProjectId is available for the knowledge review.");
        var tenantId = actor.TenantId ?? throw new InvalidOperationException("Knowledge review requires an authenticated tenant actor.");
        var limit = Math.Clamp(request.LimitPerSection, 1, 200);
        var ids = projects.Select(x => x.ProjectId).ToArray();
        var retention = await retentionService.RunAsync(new MemoryDataRetentionRunRequest(
            TriggeredBy: "knowledge-review",
            Mode: MemoryDataRetentionRunMode.Classify,
            PreviewOnly: false,
            ProjectIds: ids,
            TenantId: tenantId,
            IncludeCandidateDetails: true), "knowledge-review", cancellationToken);

        var insightResults = new List<ConversationInsightResult>();
        var actionResults = new List<SuggestedActionResult>();
        var proposalResults = new List<ChatGptProposalResult>();
        var discussionResults = new Dictionary<Guid, DiscussionThreadResult>();
        var workItemResults = new List<ProjectWorkItemResult>();
        foreach (var project in projects.Where(x => !ProjectContext.IsShared(x.ProjectId) && !ProjectContext.IsUser(x.ProjectId)))
        {
            var projectId = project.ProjectId;
            insightResults.AddRange(await conversationService.ListInsightsAsync(new ConversationInsightListRequest(projectId, PromotionStatus: ConversationPromotionStatus.Pending, Limit: limit), cancellationToken));
            actionResults.AddRange(await suggestedActions.ListAsync(new SuggestedActionListRequest(projectId, SuggestedActionStatus.Pending, Limit: limit), cancellationToken));
            proposalResults.AddRange(await proposals.ListAsync(new ChatGptProposalListRequest(projectId, ChatGptProposalStatus.Pending, limit), cancellationToken));
            foreach (var thread in await discussions.ListThreadsAsync(new DiscussionThreadListRequest(ProjectId: projectId, Status: "Open", Limit: limit), cancellationToken)) discussionResults[thread.Id] = thread;
            workItemResults.AddRange(await workItems.ListAsync(new ProjectWorkItemListRequest(projectId, Limit: limit), cancellationToken));
        }
        var preferences = await memoryService.ListUserPreferencesAsync(new UserPreferenceListRequest(IncludeArchived: false, Limit: limit), cancellationToken);
        var sharedCandidates = retention.AutoDeleteCandidates.Concat(retention.ReviewCandidates)
            .Where(x => ProjectContext.IsShared(x.ProjectId)).Take(limit).ToArray();
        return new KnowledgeReviewResult(
            projects, retention, sharedCandidates, preferences,
            discussionResults.Values.OrderByDescending(x => x.UpdatedAt).Take(limit).ToArray(),
            workItemResults.OrderBy(x => x.Status == ProjectWorkItemStatus.Completed || x.Status == ProjectWorkItemStatus.Cancelled).ThenByDescending(x => x.Priority).ThenBy(x => x.DueAt).Take(limit).ToArray(),
            insightResults.Where(x => x.Importance >= .70m && x.Confidence >= .75m).OrderByDescending(x => x.UpdatedAt).Take(limit).ToArray(),
            actionResults.OrderByDescending(x => x.UpdatedAt).Take(limit).ToArray(),
            proposalResults.OrderByDescending(x => x.UpdatedAt).Take(limit).ToArray());
    }
}

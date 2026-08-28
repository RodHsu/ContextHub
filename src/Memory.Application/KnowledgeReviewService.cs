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
    IDurableMemoryGovernanceService durableGovernance,
    IFullGovernancePlanService fullGovernance,
    IRequestActorAccessor actorAccessor,
    IGovernanceRunReceiptService runReceipts,
    IClock clock) : IKnowledgeReviewService
{
    private const int PageSize = 200;

    public async Task<KnowledgeReviewResult> ReviewAsync(KnowledgeReviewRequest request, CancellationToken cancellationToken)
    {
        var startedAt = clock.UtcNow;
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
        var offset = Math.Max(0, request.Offset);
        var governanceRunId = NormalizeGovernanceRunId(request.GovernanceRunId);
        var ids = projects.Select(x => x.ProjectId).ToArray();
        var retention = await retentionService.RunAsync(new MemoryDataRetentionRunRequest(
            TriggeredBy: $"knowledge-review:{governanceRunId}",
            Mode: MemoryDataRetentionRunMode.Classify,
            PreviewOnly: false,
            PreviewLimit: limit,
            PreviewOffset: offset,
            ProjectIds: ids,
            TenantId: tenantId,
            IncludeCandidateDetails: true), $"knowledge-review:{governanceRunId}", cancellationToken);

        var insightResults = new List<ConversationInsightResult>();
        var actionResults = new List<SuggestedActionResult>();
        var proposalResults = new List<ChatGptProposalResult>();
        var discussionResults = new Dictionary<Guid, DiscussionThreadResult>();
        var workItemResults = new List<ProjectWorkItemResult>();
        // Materialize semantic findings before reading execution queues so findings produced by
        // this review are executable in the same Review -> Execute cycle.
        var governanceSnapshot = await durableGovernance.GetOrCreateSnapshotAsync(ids, governanceRunId, request.IsReReview, cancellationToken);
        var fullGovernancePlan = await fullGovernance.BuildAsync(ids, governanceRunId, governanceSnapshot, cancellationToken);
        foreach (var project in projects.Where(x => !ProjectContext.IsShared(x.ProjectId) && !ProjectContext.IsUser(x.ProjectId)))
        {
            var projectId = project.ProjectId;
            insightResults.AddRange(await LoadAllAsync((pageOffset, pageLimit) => conversationService.ListInsightsAsync(
                new ConversationInsightListRequest(projectId, Limit: pageLimit, Offset: pageOffset), cancellationToken)));
            foreach (var thread in await LoadAllAsync((pageOffset, pageLimit) => discussions.ListThreadsAsync(
                         new DiscussionThreadListRequest(ProjectId: projectId, Status: "Open", Limit: Math.Min(pageLimit, 100), Offset: pageOffset), cancellationToken), 100))
            {
                discussionResults[thread.Id] = thread;
            }
            workItemResults.AddRange(await LoadAllAsync((pageOffset, pageLimit) => workItems.ListAsync(
                new ProjectWorkItemListRequest(projectId, Limit: pageLimit, Offset: pageOffset), cancellationToken)));
        }

        foreach (var projectId in ids.Append(ProjectContext.SharedProjectId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            actionResults.AddRange(await LoadAllAsync((pageOffset, pageLimit) => suggestedActions.ListAsync(
                new SuggestedActionListRequest(projectId, SuggestedActionStatus.Pending, Limit: pageLimit, Offset: pageOffset), cancellationToken)));
            proposalResults.AddRange(await LoadAllAsync((pageOffset, pageLimit) => proposals.ListAsync(
                new ChatGptProposalListRequest(projectId, ChatGptProposalStatus.Pending, pageLimit, pageOffset), cancellationToken)));
        }

        var preferences = await LoadAllAsync((pageOffset, pageLimit) => memoryService.ListUserPreferencesAsync(
            new UserPreferenceListRequest(IncludeArchived: false, Limit: pageLimit, Offset: pageOffset), cancellationToken));
        var sharedRetention = await retentionService.RunAsync(new MemoryDataRetentionRunRequest(
            TriggeredBy: $"knowledge-review-shared:{governanceRunId}",
            Mode: MemoryDataRetentionRunMode.Classify,
            PreviewOnly: false,
            PreviewLimit: limit,
            PreviewOffset: offset,
            ProjectIds: [ProjectContext.SharedProjectId],
            TenantId: tenantId,
            IncludeCandidateDetails: true), $"knowledge-review-shared:{governanceRunId}", cancellationToken);
        var sharedCandidates = sharedRetention.AutoDeleteCandidates.Concat(sharedRetention.ReviewCandidates)
            .OrderBy(x => x.UpdatedAtUtc)
            .ThenBy(x => x.MemoryId)
            .ToArray();
        var orderedDiscussions = discussionResults.Values.OrderByDescending(x => x.UpdatedAt).ToArray();
        var orderedWorkItems = workItemResults.OrderBy(x => x.Status == ProjectWorkItemStatus.Completed || x.Status == ProjectWorkItemStatus.Cancelled)
            .ThenByDescending(x => x.Priority).ThenBy(x => x.DueAt).ThenByDescending(x => x.UpdatedAt).ToArray();
        var orderedInsights = insightResults
            .Where(x => x.PromotionStatus == ConversationPromotionStatus.Pending)
            .Where(x => x.Importance >= .70m && x.Confidence >= .75m)
            .OrderByDescending(x => x.UpdatedAt)
            .ToArray();
        var orderedActions = actionResults.OrderByDescending(x => x.UpdatedAt).ToArray();
        var orderedProposals = proposalResults.OrderByDescending(x => x.UpdatedAt).ToArray();

        var sharedPage = sharedCandidates;
        var preferencePage = Page(preferences, offset, limit);
        var discussionPage = Page(orderedDiscussions, offset, limit);
        var workItemPage = Page(orderedWorkItems, offset, limit);
        var insightPage = Page(orderedInsights, offset, limit);
        var actionPage = Page(orderedActions, offset, limit);
        var proposalPage = Page(orderedProposals, offset, limit);
        var projectGovernancePage = Page(governanceSnapshot.ProjectCandidates, offset, limit);
        var sharedGovernancePage = Page(governanceSnapshot.SharedCandidates, offset, limit);
        var projectGovernancePageInfo = PageInfo(projectGovernancePage, governanceSnapshot.ProjectCandidates.Count, offset, limit) with
        {
            Continuation = BuildContinuation(governanceSnapshot.Coverage.SnapshotToken, "project", offset, projectGovernancePage.Length, governanceSnapshot.ProjectCandidates.Count)
        };
        var sharedGovernancePageInfo = PageInfo(sharedGovernancePage, governanceSnapshot.SharedCandidates.Count, offset, limit) with
        {
            Continuation = BuildContinuation(governanceSnapshot.Coverage.SnapshotToken, "shared", offset, sharedGovernancePage.Length, governanceSnapshot.SharedCandidates.Count)
        };
        var pagination = new KnowledgeReviewPaginationResult(
            projectGovernancePageInfo,
            sharedGovernancePageInfo,
            PageInfo(preferencePage, preferences.Count, offset, limit),
            PageInfo(discussionPage, orderedDiscussions.Length, offset, limit),
            PageInfo(workItemPage, orderedWorkItems.Length, offset, limit),
            PageInfo(insightPage, orderedInsights.Length, offset, limit),
            PageInfo(actionPage, orderedActions.Length, offset, limit),
            PageInfo(proposalPage, orderedProposals.Length, offset, limit));

        static bool IsWorkItemActionable(ProjectWorkItemResult workItem) =>
            workItem.Status is ProjectWorkItemStatus.Pending or ProjectWorkItemStatus.InProgress or ProjectWorkItemStatus.Blocked;
        var excludedGovernanceTrackers = orderedWorkItems
            .Where(IsWorkItemActionable)
            .Where(x => x.GovernanceExclusions.Any(exclusion =>
                exclusion.IsActive && string.Equals(exclusion.GovernanceRunId, governanceRunId, StringComparison.Ordinal)))
            .ToArray();
        var workItemGovernanceActionCount = orderedWorkItems.Count(x =>
            IsWorkItemActionable(x) && !excludedGovernanceTrackers.Any(excluded => excluded.Id == x.Id));
        var actionableCount = fullGovernancePlan.GovernanceActionableCount;
        var deferredCount = insightResults.Count(x => x.PromotionStatus == ConversationPromotionStatus.Deferred) + governanceSnapshot.DeferredCount;
        var userDecisionCount = insightResults.Count(x => x.PromotionStatus == ConversationPromotionStatus.RequiresUserDecision) +
                                governanceSnapshot.RequiresUserDecisionCount + fullGovernancePlan.GovernedExceptionCount;
        var hostBlockedCount = insightResults.Count(x => x.PromotionStatus == ConversationPromotionStatus.HostBlocked) + governanceSnapshot.HostBlockedCount;
        var coverageComplete = fullGovernancePlan.Coverage.CoverageComplete && !fullGovernancePlan.Coverage.HasMore;
        var convergence = BuildConvergence(
            request.IsReReview,
            coverageComplete,
            pagination.HasMore,
            actionableCount,
            deferredCount,
            userDecisionCount,
            hostBlockedCount,
            workItemGovernanceActionCount,
            excludedGovernanceTrackers.Length);

        var candidateCount = CountCandidates(fullGovernancePlan.Coverage);
        var result = new KnowledgeReviewResult(
            projects,
            retention,
            sharedPage,
            preferencePage,
            discussionPage,
            workItemPage,
            insightPage,
            actionPage,
            proposalPage,
            governanceRunId,
            request.IsReReview,
            pagination,
            convergence)
        {
            DurableMemoryCoverage = governanceSnapshot.Coverage,
            ProjectKnowledgeGovernance = new KnowledgeGovernanceSectionResult(projectGovernancePage, projectGovernancePageInfo),
            SharedKnowledgeGovernance = new KnowledgeGovernanceSectionResult(sharedGovernancePage, sharedGovernancePageInfo),
            GovernancePlan = fullGovernancePlan.Items,
            GovernanceCoverage = fullGovernancePlan.Coverage,
            QuarantinedCount = fullGovernancePlan.Retention.QuarantinedCount,
            DeleteEligibleCount = fullGovernancePlan.Retention.DeleteEligibleCount,
            DeleteMaturedCount = fullGovernancePlan.Retention.DeleteMaturedCount,
            AutoDeletedCount = 0,
            DeleteCancelledCount = fullGovernancePlan.Retention.DeleteCancelledCount,
            TombstonedCount = 0,
            SemanticAutoResolvedCount = 0,
            RemainingHumanDecisionCount = fullGovernancePlan.RemainingHumanDecisionCount,
            ProtectedRetentionCount = fullGovernancePlan.Retention.ProtectedRetentionCount,
            CandidateCount = candidateCount,
            ExecutionActionableCount = fullGovernancePlan.GovernanceActionableCount,
            GovernedExceptionCount = deferredCount + userDecisionCount + hostBlockedCount,
            Convergence = convergence with
            {
                GovernanceActionableCount = fullGovernancePlan.GovernanceActionableCount,
                BusinessWorkItemActionableCount = fullGovernancePlan.BusinessWorkItemActionableCount,
                GovernedExceptionCount = deferredCount + userDecisionCount + hostBlockedCount,
                CandidateCount = candidateCount,
                ExecutionActionableCount = fullGovernancePlan.GovernanceActionableCount
            }
        };
        await runReceipts.RecordReviewAsync(result, startedAt, cancellationToken);
        return result;
    }

    private static async Task<IReadOnlyList<T>> LoadAllAsync<T>(
        Func<int, int, Task<IReadOnlyList<T>>> loadPage,
        int pageSize = PageSize)
    {
        var results = new List<T>();
        for (var offset = 0; ; offset += pageSize)
        {
            var page = await loadPage(offset, pageSize);
            results.AddRange(page);
            if (page.Count < pageSize)
            {
                return results;
            }
        }
    }

    private static T[] Page<T>(IReadOnlyList<T> values, int offset, int limit)
        => values.Skip(offset).Take(limit).ToArray();

    private static KnowledgeReviewPageResult PageInfo<T>(IReadOnlyList<T> page, int totalCount, int offset, int limit)
        => new(offset, limit, page.Count, totalCount, offset + page.Count < totalCount);

    private static string? BuildContinuation(string snapshotToken, string section, int offset, int returnedCount, int totalCount)
        => offset + returnedCount < totalCount
            ? $"{snapshotToken}:{section}:{offset + returnedCount}"
            : null;

    private static int CountCandidates(FullGovernanceCoverageResult coverage)
        => coverage.ProjectCoverage.CandidateCount + coverage.HierarchyCoverage.CandidateCount +
           coverage.MemoryCoverage.CandidateCount + coverage.PreferenceCoverage.CandidateCount +
           coverage.ArtifactCoverage.CandidateCount + coverage.DiscussionCoverage.CandidateCount +
           coverage.WorkItemCoverage.CandidateCount + coverage.InsightCoverage.CandidateCount +
           coverage.SuggestedActionCoverage.CandidateCount + coverage.ProposalCoverage.CandidateCount +
           coverage.LogCoverage.CandidateCount;

    internal static KnowledgeReviewConvergenceResult BuildConvergence(
        bool isReReview,
        bool coverageComplete,
        bool hasMore,
        int actionableItemCount,
        int deferredCount,
        int requiresUserDecisionCount,
        int hostBlockedCount,
        int workItemActionableCount = 0,
        int excludedGovernanceTrackerCount = 0,
        int? governanceActionableCount = null)
    {
        var exceptionCount = deferredCount + requiresUserDecisionCount + hostBlockedCount;
        var convergenceActionableCount = governanceActionableCount ?? actionableItemCount;
        var converged = isReReview && coverageComplete && !hasMore && convergenceActionableCount == 0;
        var status = converged
            ? exceptionCount > 0 ? "ConvergedWithExceptions" : "Converged"
            : convergenceActionableCount > 0
                ? "ExecutionRequired"
                : !coverageComplete || hasMore
                    ? "CoverageIncomplete"
                    : "ReReviewRequired";
        return new KnowledgeReviewConvergenceResult(status, actionableItemCount, !converged, converged)
        {
            CoverageComplete = coverageComplete,
            DeferredCount = deferredCount,
            RequiresUserDecisionCount = requiresUserDecisionCount,
            HostBlockedCount = hostBlockedCount,
            WorkItemActionableCount = workItemActionableCount,
            ExecutionActionableCount = convergenceActionableCount,
            GovernedExceptionCount = exceptionCount,
            ExcludedGovernanceTrackerCount = excludedGovernanceTrackerCount
        };
    }

    private static string NormalizeGovernanceRunId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return Guid.NewGuid().ToString("D");
        }

        var normalized = value.Trim();
        if (normalized.Length > 128)
        {
            throw new InvalidOperationException("GovernanceRunId must not exceed 128 characters.");
        }

        return normalized;
    }
}

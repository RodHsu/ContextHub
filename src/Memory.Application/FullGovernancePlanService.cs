using System.Text.Json;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

public sealed class FullGovernancePlanService(
    IApplicationDbContext dbContext,
    IRequestActorAccessor actorAccessor,
    IClock clock) : IFullGovernancePlanService
{
    private const string ProjectInformationExternalKey = "system:project-information";
    private const string ProposalSourceSystem = ChatGptProposalService.SourceSystem;
    private const int MaximumLogPartitions = 10_000;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<FullGovernancePlanResult> BuildAsync(
        IReadOnlyList<string> projectIds,
        string governanceRunId,
        DurableMemoryGovernanceSnapshotResult memorySnapshot,
        CancellationToken cancellationToken)
    {
        var actor = actorAccessor.Current;
        ActorAuthorization.EnsureScopeAllowed(actor, SecurityScopes.MemoryRead);
        var tenantId = actor.TenantId ?? throw new InvalidOperationException("Full governance review requires a tenant actor.");
        var ownerUserId = actor.UserId ?? throw new InvalidOperationException("Full governance review requires a tenant user.");
        var projects = projectIds.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        ActorAuthorization.EnsureProjectsAllowed(actor, projects, write: false);
        var projectSet = projects.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var items = new List<GovernanceReviewItem>();

        foreach (var candidate in memorySnapshot.ProjectCandidates.Concat(memorySnapshot.SharedCandidates))
        {
            items.Add(new GovernanceReviewItem(
                $"finding:{candidate.FindingId:N}", GovernanceItemKind.Memory, candidate.ProjectId,
                candidate.Classification.ToString(), candidate.RecommendedAction,
                candidate.RequiresExplicitApproval ? GovernanceBatchRiskLevel.High : GovernanceBatchRiskLevel.Low,
                candidate.RequiresExplicitApproval, candidate.MemoryId,
                candidate.RelatedMemoryId.HasValue ? [candidate.RelatedMemoryId.Value] : [],
                candidate.ReasonCodes, governanceRunId));
        }

        var scopedMemories = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId)
            .Where(x =>
                (projects.Contains(x.ProjectId) &&
                 (x.MemoryType == MemoryType.Artifact || x.ExternalKey == ProjectInformationExternalKey)) ||
                (x.ProjectId == ProjectContext.UserProjectId && x.MemoryType == MemoryType.Preference))
            .Select(x => new
            {
                x.Id,
                x.ProjectId,
                x.ExternalKey,
                x.MemoryType,
                x.Status,
                x.Title,
                Content = x.MemoryType == MemoryType.Preference || x.ExternalKey == ProjectInformationExternalKey ? x.Content : string.Empty,
                x.Summary,
                x.SourceType,
                x.SourceRef,
                x.Tags,
                x.Importance,
                x.Confidence,
                x.MetadataJson,
                x.UpdatedAt
            })
            .ToListAsync(cancellationToken);

        var projectInformation = scopedMemories
            .Where(x => projectSet.Contains(x.ProjectId) && x.ExternalKey == ProjectInformationExternalKey)
            .ToArray();
        foreach (var projectId in projects)
        {
            var information = projectInformation.OrderByDescending(x => x.UpdatedAt)
                .FirstOrDefault(x => string.Equals(x.ProjectId, projectId, StringComparison.OrdinalIgnoreCase));
            if (information is null || string.IsNullOrWhiteSpace(information.Content) || !IsValidJson(information.MetadataJson))
            {
                items.Add(Item($"project:{projectId.ToLowerInvariant()}", GovernanceItemKind.Project, projectId,
                    information is null ? "MissingProjectInformation" : "InvalidProjectMetadata",
                    "LifecycleReconcile", GovernanceBatchRiskLevel.Medium, true,
                    information?.Id, information is null ? ["PROJECT_INFORMATION_MISSING"] : ["PROJECT_METADATA_INVALID"], governanceRunId));
            }
        }

        var hierarchyRows = await dbContext.ProjectHierarchies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId)
            .Where(x => projects.Contains(x.ParentProjectId) || projects.Contains(x.ChildProjectId))
            .ToListAsync(cancellationToken);
        var hierarchyReasons = FindHierarchyProblems(hierarchyRows, projectSet);
        foreach (var problem in hierarchyReasons)
        {
            items.Add(Item($"hierarchy:{problem.Row.Id:N}", GovernanceItemKind.ProjectHierarchy,
                problem.Row.ParentProjectId, problem.Classification, "HierarchyReconcile",
                GovernanceBatchRiskLevel.High, true, problem.Row.Id, problem.Reasons, governanceRunId));
        }

        var preferences = scopedMemories.Where(x => x.ProjectId == ProjectContext.UserProjectId && x.MemoryType == MemoryType.Preference).ToArray();
        foreach (var preference in preferences.Where(x => string.IsNullOrWhiteSpace(x.Content) || !IsValidJson(x.MetadataJson)))
        {
            items.Add(Item($"preference:{preference.Id:N}", GovernanceItemKind.UserPreference, ProjectContext.UserProjectId,
                "InvalidPreference", "PreferenceReconcile", GovernanceBatchRiskLevel.High, true,
                preference.Id, ["PREFERENCE_INVALID"], governanceRunId));
        }
        foreach (var group in preferences.Where(x => x.Status == MemoryStatus.Active)
                     .GroupBy(x => $"value:{NormalizeText(x.Title)}\n{NormalizeText(x.Content)}", StringComparer.OrdinalIgnoreCase)
                     .Where(x => x.Count() > 1))
        {
            var authority = group.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.Importance).ThenByDescending(x => x.UpdatedAt).First();
            foreach (var duplicate in group.Where(x => x.Id != authority.Id))
            {
                items.Add(new GovernanceReviewItem($"preference:{duplicate.Id:N}", GovernanceItemKind.UserPreference,
                    ProjectContext.UserProjectId, "DuplicatePreference", "PreferenceReconcile", GovernanceBatchRiskLevel.High,
                    true, authority.Id, [duplicate.Id], ["PREFERENCE_DUPLICATE_KEY", "PREFERENCE_HIGH_VALUE"], governanceRunId));
            }
        }

        var artifacts = scopedMemories.Where(x => projectSet.Contains(x.ProjectId) && x.MemoryType == MemoryType.Artifact &&
                                                   x.ExternalKey != ProjectInformationExternalKey).ToArray();
        foreach (var group in artifacts.Where(x => x.Status == MemoryStatus.Active)
                     .GroupBy(x => $"{x.ProjectId}\nvalue:{NormalizeText(x.Title)}\n{NormalizeText(x.Summary)}\n{NormalizeText(x.SourceType)}", StringComparer.OrdinalIgnoreCase)
                     .Where(x => x.Count() > 1))
        {
            var authority = group.OrderByDescending(x => x.Confidence).ThenByDescending(x => x.Importance).ThenByDescending(x => x.UpdatedAt).First();
            foreach (var duplicate in group.Where(x => x.Id != authority.Id))
            {
                items.Add(new GovernanceReviewItem($"artifact:{duplicate.Id:N}", GovernanceItemKind.Artifact,
                    duplicate.ProjectId, "DuplicateArtifact", "ArtifactReconcile", GovernanceBatchRiskLevel.Medium,
                    true, authority.Id, [duplicate.Id], ["ARTIFACT_DUPLICATE_KEY", "AUDIT_CHAIN_REQUIRED"], governanceRunId));
            }
        }
        foreach (var artifact in artifacts.Where(x => HasExpiredObject(x.MetadataJson, clock.UtcNow)))
        {
            items.Add(Item($"artifact:{artifact.Id:N}", GovernanceItemKind.Artifact, artifact.ProjectId,
                "ExpiredExternalObject", "ArtifactReconcile", GovernanceBatchRiskLevel.Medium, true,
                artifact.Id, ["ARTIFACT_OBJECT_EXPIRED", "AUDIT_CHAIN_REQUIRED"], governanceRunId));
        }

        var discussions = await dbContext.DiscussionThreads.AsNoTracking().Include(x => x.Participants).Include(x => x.Messages)
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && projects.Contains(x.HostProjectId))
            .ToListAsync(cancellationToken);
        foreach (var discussion in discussions)
        {
            var invalidParticipant = discussion.Participants.Any(x => !projectSet.Contains(x.ProjectId));
            var closedForRetention = discussion.Status.Equals("Closed", StringComparison.OrdinalIgnoreCase) &&
                                     discussion.ArchivedAt is null && discussion.UpdatedAt < clock.UtcNow.AddDays(-30);
            var staleOpen = discussion.Status.Equals("Open", StringComparison.OrdinalIgnoreCase) &&
                            discussion.UpdatedAt < clock.UtcNow.AddDays(-180);
            if (invalidParticipant || closedForRetention || staleOpen)
            {
                var classification = invalidParticipant ? "InvalidDiscussionParticipant" : closedForRetention ? "CompletedDiscussion" : "StaleOpenDiscussion";
                items.Add(Item($"discussion:{discussion.Id:N}", GovernanceItemKind.Discussion, discussion.HostProjectId,
                    classification, "DiscussionReconcile",
                    closedForRetention && !invalidParticipant ? GovernanceBatchRiskLevel.Low : GovernanceBatchRiskLevel.High,
                    !closedForRetention || invalidParticipant, discussion.Id,
                    invalidParticipant ? ["DISCUSSION_PARTICIPANT_INVALID"] : closedForRetention ? ["DISCUSSION_CLOSED_RETENTION"] : ["DISCUSSION_UNRESOLVED_REVIEW"], governanceRunId));
            }
        }

        var workItems = await dbContext.ProjectWorkItems.AsNoTracking().Include(x => x.ChecklistItems)
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && projects.Contains(x.ProjectId))
            .ToListAsync(cancellationToken);
        foreach (var workItem in workItems)
        {
            var inconsistent = workItem.Status == ProjectWorkItemStatus.Completed && workItem.ChecklistItems.Any(x => !x.IsCompleted);
            var terminalRetention = workItem.Status is ProjectWorkItemStatus.Completed or ProjectWorkItemStatus.Cancelled &&
                                    workItem.ArchivedAt is null && workItem.UpdatedAt < clock.UtcNow.AddDays(-90);
            if (inconsistent || terminalRetention)
            {
                items.Add(Item($"workitem:{workItem.Id:N}", GovernanceItemKind.WorkItem, workItem.ProjectId,
                    inconsistent ? "ChecklistStatusMismatch" : "CompletedHistoricalWorkItem",
                    "WorkItemReconcile", inconsistent ? GovernanceBatchRiskLevel.High : GovernanceBatchRiskLevel.Low,
                    inconsistent, workItem.Id,
                    inconsistent ? ["WORK_ITEM_CHECKLIST_MISMATCH"] : ["WORK_ITEM_TERMINAL_RETENTION"], governanceRunId));
            }
        }

        var insights = await dbContext.ConversationInsights.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && projects.Contains(x.ProjectId))
            .Where(x => x.PromotionStatus == ConversationPromotionStatus.Pending || x.PromotionStatus == ConversationPromotionStatus.Failed)
            .ToListAsync(cancellationToken);
        var proposalInsights = insights.Where(IsProposal).ToArray();
        foreach (var insight in insights.Where(x => !IsProposal(x)))
        {
            items.Add(Item($"insight:{insight.Id:N}", GovernanceItemKind.ConversationInsight, insight.ProjectId,
                "PendingConversationInsight", "ConversationInsightDisposition",
                IsProtectedInsight(insight) ? GovernanceBatchRiskLevel.High : GovernanceBatchRiskLevel.Low,
                IsProtectedInsight(insight), insight.Id,
                IsProtectedInsight(insight) ? ["INSIGHT_SEMANTIC_AUTHORITY_REQUIRED"] : ["INSIGHT_DISPOSITION_REQUIRED"], governanceRunId));
        }
        foreach (var proposal in proposalInsights)
        {
            items.Add(Item($"proposal:{proposal.Id:N}", GovernanceItemKind.Proposal, proposal.ProjectId,
                "PendingProposal", "ProposalApply", GovernanceBatchRiskLevel.High, true, proposal.Id,
                ["PROPOSAL_TARGET_READBACK_REQUIRED"], governanceRunId));
        }

        var actions = await dbContext.SuggestedActions.AsNoTracking()
            .Where(x => (projects.Contains(x.ProjectId) || x.ProjectId == ProjectContext.SharedProjectId) &&
                        x.Status == SuggestedActionStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var action in actions)
        {
            items.Add(Item($"action:{action.Id:N}", GovernanceItemKind.SuggestedAction, action.ProjectId,
                "PendingSuggestedAction", "SuggestedActionReconcile",
                action.Type == SuggestedActionType.ReindexProject ? GovernanceBatchRiskLevel.Low : GovernanceBatchRiskLevel.Medium,
                action.Type != SuggestedActionType.ReindexProject, action.Id,
                ["SUGGESTED_ACTION_RESOURCE_READBACK_REQUIRED"], governanceRunId));
        }

        var logTotal = await dbContext.RuntimeLogEntries.AsNoTracking().LongCountAsync(x => projects.Contains(x.ProjectId), cancellationToken);
        var logPartitions = await dbContext.RuntimeLogEntries.AsNoTracking()
            .Where(x => projects.Contains(x.ProjectId))
            .GroupBy(x => new { x.ProjectId, x.ServiceName, Day = x.CreatedAt.Date })
            .Select(x => new
            {
                x.Key.ProjectId,
                x.Key.ServiceName,
                x.Key.Day,
                Count = x.Count(),
                HasHighValue = x.Any(row => row.Level == "Error" || row.Level == "Critical" || row.Exception != ""),
                HasSensitive = x.Any(row =>
                    row.Message.ToLower().Contains("private key") || row.Message.ToLower().Contains("password") ||
                    row.Message.ToLower().Contains("secret") || row.Message.ToLower().Contains("token="))
            })
            .OrderBy(x => x.Day).ThenBy(x => x.ProjectId).ThenBy(x => x.ServiceName)
            .Take(MaximumLogPartitions + 1)
            .ToListAsync(cancellationToken);
        var logHasMore = logPartitions.Count > MaximumLogPartitions;
        foreach (var partition in logPartitions.Take(MaximumLogPartitions).Where(x => x.Day < clock.UtcNow.AddDays(-30).Date || x.HasHighValue || x.HasSensitive))
        {
            var key = $"log:{partition.ProjectId}:{partition.ServiceName}:{partition.Day:yyyyMMdd}";
            var classification = partition.HasSensitive ? "SecuritySensitiveLogPartition" : partition.HasHighValue ? "HighValueLogPartition" : "ExpiredLowValueLogPartition";
            var action = partition.HasSensitive ? "LogRetentionProposal" : partition.HasHighValue ? "LogPromote" : "LogRetentionProposal";
            items.Add(Item(key, GovernanceItemKind.LogPartition, partition.ProjectId, classification, action,
                partition.HasSensitive ? GovernanceBatchRiskLevel.Critical : partition.HasHighValue ? GovernanceBatchRiskLevel.Medium : GovernanceBatchRiskLevel.Low,
                true, DeterministicGuid(key),
                partition.HasSensitive ? ["LOG_SECRET_PII_REDACTION_REQUIRED", "LOG_PROMOTION_PROHIBITED"] :
                partition.HasHighValue ? ["LOG_PROMOTE_BEFORE_RETENTION"] : ["LOG_RETENTION_EXPIRED", "NO_SCHEDULED_HARD_DELETE"], governanceRunId));
        }

        var ordered = items.GroupBy(x => x.ItemKey, StringComparer.Ordinal).Select(x => x.First())
            .OrderBy(x => x.ItemKind).ThenBy(x => x.ProjectId, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.ItemKey, StringComparer.Ordinal).ToArray();
        var businessWorkItems = workItems.Count(x =>
            x.ArchivedAt is null &&
            x.Status is ProjectWorkItemStatus.Pending or ProjectWorkItemStatus.InProgress or ProjectWorkItemStatus.Blocked &&
            !HasActiveGovernanceExclusion(x.GovernanceExclusionsJson, governanceRunId));
        var governedExceptions = ordered.Count(x => x.RequiresExplicitApproval);
        var coverage = new FullGovernanceCoverageResult(
            Surface(projects.Length, projects.Length, ordered, GovernanceItemKind.Project),
            Surface(hierarchyRows.Count, hierarchyRows.Count, ordered, GovernanceItemKind.ProjectHierarchy),
            Surface(memorySnapshot.Coverage.TotalCount, memorySnapshot.Coverage.ScannedCount, ordered, GovernanceItemKind.Memory,
                memorySnapshot.Coverage.HasMore, memorySnapshot.Coverage.CoverageComplete),
            Surface(preferences.Length, preferences.Length, ordered, GovernanceItemKind.UserPreference),
            Surface(artifacts.Length, artifacts.Length, ordered, GovernanceItemKind.Artifact),
            Surface(discussions.Count, discussions.Count, ordered, GovernanceItemKind.Discussion),
            Surface(workItems.Count, workItems.Count, ordered, GovernanceItemKind.WorkItem),
            Surface(insights.Count(x => !IsProposal(x)), insights.Count(x => !IsProposal(x)), ordered, GovernanceItemKind.ConversationInsight),
            Surface(actions.Count, actions.Count, ordered, GovernanceItemKind.SuggestedAction),
            Surface(proposalInsights.Length, proposalInsights.Length, ordered, GovernanceItemKind.Proposal),
            Surface(checked((int)Math.Min(logTotal, int.MaxValue)), logPartitions.Take(MaximumLogPartitions).Sum(x => x.Count), ordered, GovernanceItemKind.LogPartition,
                logHasMore, !logHasMore));
        return new FullGovernancePlanResult(ordered, coverage, ordered.Count(x => !x.RequiresExplicitApproval), businessWorkItems, governedExceptions);
    }

    private static GovernanceReviewItem Item(string key, GovernanceItemKind kind, string projectId, string classification,
        string action, GovernanceBatchRiskLevel risk, bool approval, Guid? authority, IReadOnlyList<string> reasons, string runId)
        => new(key, kind, projectId, classification, action, risk, approval, authority, [], reasons, runId);

    private static GovernanceSurfaceCoverageResult Surface(int total, int scanned, IReadOnlyList<GovernanceReviewItem> items,
        GovernanceItemKind kind, bool hasMore = false, bool complete = true)
    {
        var candidates = items.Where(x => x.ItemKind == kind).ToArray();
        return new(total, scanned, candidates.Length, candidates.Length, 0, candidates.Count(x => x.RequiresExplicitApproval), 0, hasMore, complete);
    }

    private static bool IsProposal(ConversationInsight insight)
        => insight.SourceSystem == ProposalSourceSystem && insight.Tags.Contains("chatgpt-proposal");

    private static bool IsProtectedInsight(ConversationInsight insight)
        => insight.InsightType is ConversationInsightType.Decision or ConversationInsightType.Fact ||
           insight.Importance >= .8m || insight.Confidence >= .9m;

    private static bool IsValidJson(string value)
    {
        try { using var _ = JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value); return true; }
        catch (JsonException) { return false; }
    }

    private static bool HasActiveGovernanceExclusion(string json, string governanceRunId)
    {
        try
        {
            var exclusions = JsonSerializer.Deserialize<ProjectWorkItemGovernanceExclusionResult[]>(json, JsonOptions) ?? [];
            return exclusions.Any(x => x.IsActive && string.Equals(x.GovernanceRunId, governanceRunId, StringComparison.Ordinal));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string NormalizeText(string value)
        => string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).ToLowerInvariant();

    private static bool HasExpiredObject(string metadataJson, DateTimeOffset now)
    {
        try
        {
            using var document = JsonDocument.Parse(string.IsNullOrWhiteSpace(metadataJson) ? "{}" : metadataJson);
            return document.RootElement.TryGetProperty("expiresAt", out var value) &&
                   value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out var expiresAt) && expiresAt <= now;
        }
        catch (JsonException) { return true; }
    }

    private static IReadOnlyList<HierarchyProblem> FindHierarchyProblems(IReadOnlyList<ProjectHierarchy> rows, IReadOnlySet<string> projects)
    {
        var result = new List<HierarchyProblem>();
        var duplicates = rows.GroupBy(x => $"{x.ParentProjectId}\n{x.ChildProjectId}", StringComparer.OrdinalIgnoreCase)
            .Where(x => x.Count() > 1).SelectMany(x => x.Skip(1)).Select(x => x.Id).ToHashSet();
        var adjacency = rows.GroupBy(x => x.ParentProjectId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Key, x => x.Select(row => row.ChildProjectId).ToArray(), StringComparer.OrdinalIgnoreCase);
        foreach (var row in rows)
        {
            var reasons = new List<string>();
            if (string.Equals(row.ParentProjectId, row.ChildProjectId, StringComparison.OrdinalIgnoreCase)) reasons.Add("HIERARCHY_SELF_PARENT");
            if (!projects.Contains(row.ParentProjectId) || !projects.Contains(row.ChildProjectId)) reasons.Add("HIERARCHY_DANGLING_PROJECT");
            if (duplicates.Contains(row.Id)) reasons.Add("HIERARCHY_DUPLICATE_CHILD");
            if (HasPath(adjacency, row.ChildProjectId, row.ParentProjectId, new HashSet<string>(StringComparer.OrdinalIgnoreCase))) reasons.Add("HIERARCHY_CYCLE");
            if (reasons.Count > 0) result.Add(new HierarchyProblem(row, reasons[0], reasons));
        }
        return result;
    }

    private static bool HasPath(IReadOnlyDictionary<string, string[]> graph, string current, string target, HashSet<string> visited)
    {
        if (!visited.Add(current)) return false;
        if (!graph.TryGetValue(current, out var children))
        {
            visited.Remove(current);
            return false;
        }
        foreach (var child in children)
        {
            if (string.Equals(child, target, StringComparison.OrdinalIgnoreCase) || HasPath(graph, child, target, visited)) return true;
        }
        visited.Remove(current);
        return false;
    }

    private static Guid DeterministicGuid(string value)
    {
        var bytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value));
        return new Guid(bytes.AsSpan(0, 16));
    }

    private sealed record HierarchyProblem(ProjectHierarchy Row, string Classification, IReadOnlyList<string> Reasons);
}

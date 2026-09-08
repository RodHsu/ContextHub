using System.Security.Cryptography;
using System.Text;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

internal static class GovernanceEvidenceFingerprint
{
    public const string PolicyVersion = "autonomous-semantic-2026-09-01-v2";

    public static async Task<string> BuildAsync(
        IApplicationDbContext dbContext,
        string projectId,
        Guid tenantId,
        Guid ownerUserId,
        Guid? primaryMemoryId,
        Guid? secondaryMemoryId,
        Guid? excludedInsightId,
        string semanticPayload,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? referenceTexts = null,
        string? exactMemoryTitle = null,
        string? exactMemorySummary = null)
    {
        var memoryIds = new[] { primaryMemoryId, secondaryMemoryId }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var normalizedExactTitle = NormalizeExactText(exactMemoryTitle);
        var normalizedExactSummary = NormalizeExactText(exactMemorySummary);
        var hasExactMemoryIdentity = normalizedExactTitle.Length > 0 && normalizedExactSummary.Length > 0;
        var referenceTokens = memoryIds.Select(x => x.ToString("D"))
            .Concat(referenceTexts ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var referenceNeedle = excludedInsightId?.ToString("D") ?? referenceTokens.FirstOrDefault() ?? string.Empty;
        var memoryRows = memoryIds.Length == 0 && !hasExactMemoryIdentity
            ? []
            : await dbContext.MemoryItems.AsNoTracking().Where(x =>
                    x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                    (memoryIds.Contains(x.Id) ||
                     (hasExactMemoryIdentity && x.ProjectId == projectId &&
                      x.Title.Trim().ToLower() == normalizedExactTitle &&
                      x.Summary.Trim().ToLower() == normalizedExactSummary)))
                .OrderBy(x => x.Id)
                .Select(x => new
                {
                    x.Id,
                    x.ProjectId,
                    x.Status,
                    x.AuthorityState,
                    x.SupersedesId,
                    x.SupersededById,
                    x.ValidFrom,
                    x.ValidUntil,
                    x.SuccessorEvidenceId,
                    x.SuccessorEvidenceRef,
                    x.Version,
                    x.UpdatedAt,
                    x.MetadataJson
                })
                .ToArrayAsync(cancellationToken);
        var effectiveAtUtc = DateTimeOffset.UtcNow;
        var memoryEvidence = memoryRows.Select(x =>
            $"{x.Id:N}:{x.ProjectId}:{x.Status}:{x.AuthorityState}:{x.SupersedesId?.ToString("N")}:{x.SupersededById?.ToString("N")}:" +
            $"{x.ValidFrom?.UtcTicks}:{x.ValidUntil?.UtcTicks}:{x.SuccessorEvidenceId?.ToString("N")}:{x.SuccessorEvidenceRef}:" +
            $"{x.Version}:{x.UpdatedAt.UtcTicks}:{x.MetadataJson}:" +
            $"{x.Status == MemoryStatus.Active && x.AuthorityState == MemoryAuthorityState.Current && x.ValidFrom <= effectiveAtUtc && (!x.ValidUntil.HasValue || x.ValidUntil > effectiveAtUtc)}")
            .ToArray();
        var evidenceMemoryIds = memoryRows.Select(x => x.Id).ToArray();
        var linkRows = evidenceMemoryIds.Length == 0
            ? []
            : await dbContext.MemoryLinks.AsNoTracking()
                .Where(x => evidenceMemoryIds.Contains(x.FromId) || evidenceMemoryIds.Contains(x.ToId))
                .OrderBy(x => x.FromId)
                .ThenBy(x => x.ToId)
                .ThenBy(x => x.LinkType)
                .Select(x => new { x.FromId, x.ToId, x.LinkType, x.CreatedAt })
                .ToArrayAsync(cancellationToken);
        var linkEvidence = linkRows.Select(x =>
            $"{x.FromId:N}:{x.ToId:N}:{x.LinkType}:{x.CreatedAt.UtcTicks}").ToArray();
        var retrievalRows = evidenceMemoryIds.Length == 0
            ? []
            : await dbContext.RetrievalTelemetryDailyHitSummaries.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                            evidenceMemoryIds.Contains(x.MemoryId))
                .GroupBy(x => x.MemoryId)
                .Select(x => new { MemoryId = x.Key, LastSeenAt = x.Max(v => v.LastSeenAt) })
                .ToArrayAsync(cancellationToken);
        var rawRetrievalRows = evidenceMemoryIds.Length == 0
            ? []
            : await dbContext.RetrievalHits.AsNoTracking()
                .Where(x => x.MemoryId.HasValue && evidenceMemoryIds.Contains(x.MemoryId.Value) &&
                            x.RetrievalEvent != null &&
                            x.RetrievalEvent.TenantId == tenantId &&
                            x.RetrievalEvent.OwnerUserId == ownerUserId)
                .GroupBy(x => x.MemoryId!.Value)
                .Select(x => new { MemoryId = x.Key, LastSeenAt = x.Max(v => v.RetrievalEvent!.CreatedAt) })
                .ToArrayAsync(cancellationToken);
        var retrievalWatermarks = retrievalRows
            .Concat(rawRetrievalRows)
            .GroupBy(x => x.MemoryId)
            .OrderBy(x => x.Key)
            .Select(x => $"{x.Key:N}:{x.Max(v => v.LastSeenAt).UtcTicks}")
            .ToArray();
        var retentionRows = evidenceMemoryIds.Length == 0
            ? []
            : await dbContext.MemoryRetentionStates.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                            evidenceMemoryIds.Contains(x.ResourceId))
                .OrderBy(x => x.ResourceId)
                .Select(x => new { x.ResourceId, x.LifecycleStatus, x.PolicyVersion, x.EvidenceFingerprint, x.UpdatedAt })
                .ToArrayAsync(cancellationToken);
        var retentionEvidence = retentionRows.Select(x =>
            $"{x.ResourceId:N}:{x.LifecycleStatus}:{x.PolicyVersion}:{x.EvidenceFingerprint}:{x.UpdatedAt.UtcTicks}").ToArray();
        var projectInformationUpdated = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                        x.ProjectId == projectId && x.ExternalKey == "system:project-information")
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var workItemUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.ProjectWorkItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && x.ProjectId == projectId &&
                        (x.Title.Contains(referenceNeedle) || x.Description.Contains(referenceNeedle) ||
                         x.ChecklistItems.Any(item => item.Content.Contains(referenceNeedle))))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var workItemChecklistUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.ProjectWorkItemChecklistItems.AsNoTracking()
            .Where(x => x.WorkItem != null && x.WorkItem.TenantId == tenantId &&
                        x.WorkItem.OwnerUserId == ownerUserId && x.WorkItem.ProjectId == projectId &&
                        x.Content.Contains(referenceNeedle))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var discussionUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.DiscussionThreads.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && x.HostProjectId == projectId &&
                        (x.Title.Contains(referenceNeedle) || x.Messages.Any(m => m.Content.Contains(referenceNeedle))))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var discussionMessageUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.DiscussionMessages.AsNoTracking()
            .Where(x => x.Thread != null && x.Thread.TenantId == tenantId &&
                        x.Thread.OwnerUserId == ownerUserId && x.Thread.HostProjectId == projectId &&
                        x.Content.Contains(referenceNeedle))
            .Select(x => (DateTimeOffset?)x.CreatedAt).MaxAsync(cancellationToken);
        var actionUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.SuggestedActions.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                        x.ProjectId == projectId && x.DedupKey.Contains(referenceNeedle))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var insightUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.ConversationInsights.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && x.ProjectId == projectId &&
                        (!excludedInsightId.HasValue || x.Id != excludedInsightId.Value) &&
                        (x.Title.Contains(referenceNeedle) || x.Content.Contains(referenceNeedle) ||
                         x.Summary.Contains(referenceNeedle) || x.DedupKey.Contains(referenceNeedle)))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var hierarchyUpdated = await dbContext.ProjectHierarchies.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                        (x.ParentProjectId == projectId || x.ChildProjectId == projectId))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);

        var canonical = string.Join('\n', new[]
        {
            PolicyVersion,
            projectId.ToLowerInvariant(),
            semanticPayload,
            string.Join('\n', memoryEvidence),
            string.Join('\n', linkEvidence),
            string.Join('\n', retrievalWatermarks),
            string.Join('\n', retentionEvidence),
            projectInformationUpdated?.UtcTicks.ToString() ?? string.Empty,
            workItemUpdated?.UtcTicks.ToString() ?? string.Empty,
            workItemChecklistUpdated?.UtcTicks.ToString() ?? string.Empty,
            discussionUpdated?.UtcTicks.ToString() ?? string.Empty,
            discussionMessageUpdated?.UtcTicks.ToString() ?? string.Empty,
            actionUpdated?.UtcTicks.ToString() ?? string.Empty,
            insightUpdated?.UtcTicks.ToString() ?? string.Empty,
            hierarchyUpdated?.UtcTicks.ToString() ?? string.Empty
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    public static string FindingPayload(GovernanceFinding finding)
        => $"{finding.Type}|{finding.DedupKey}|{finding.DetailsJson}";

    public static string InsightPayload(ConversationInsight insight)
        => $"{insight.InsightType}|{insight.Title}|{insight.Content}|{insight.Summary}|{string.Join(',', insight.Tags.Order(StringComparer.OrdinalIgnoreCase))}|{insight.Importance}|{insight.Confidence}";

    internal static string NormalizeExactText(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

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
        string? exactMemorySummary = null,
        GovernanceInsightReferenceWatermarks? referenceWatermarks = null)
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
                     (hasExactMemoryIdentity && Memory.Application.ProjectContext.Matches(x.ProjectId, projectId) &&
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
        referenceWatermarks ??= await ReadReferenceWatermarksAsync(
            dbContext, projectId, tenantId, ownerUserId, excludedInsightId, referenceNeedle, cancellationToken);

        var canonical = string.Join('\n', new[]
        {
            PolicyVersion,
            projectId.ToLowerInvariant(),
            semanticPayload,
            string.Join('\n', memoryEvidence),
            string.Join('\n', linkEvidence),
            string.Join('\n', retrievalWatermarks),
            string.Join('\n', retentionEvidence),
            FormatWatermark(referenceWatermarks.ProjectInformation),
            FormatWatermark(referenceWatermarks.WorkItem),
            FormatWatermark(referenceWatermarks.Checklist),
            FormatWatermark(referenceWatermarks.Discussion),
            FormatWatermark(referenceWatermarks.Message),
            FormatWatermark(referenceWatermarks.Action),
            FormatWatermark(referenceWatermarks.Insight),
            FormatWatermark(referenceWatermarks.Hierarchy)
        });
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    private static async Task<GovernanceInsightReferenceWatermarks> ReadReferenceWatermarksAsync(
        IApplicationDbContext dbContext,
        string projectId,
        Guid tenantId,
        Guid ownerUserId,
        Guid? excludedInsightId,
        string referenceNeedle,
        CancellationToken cancellationToken)
    {
        var watermarks = dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                        ProjectContext.Matches(x.ProjectId, projectId) && x.ExternalKey == "system:project-information")
            .Select(x => new { Kind = WatermarkKind.ProjectInformation, At = x.UpdatedAt })
            .Concat(dbContext.ProjectHierarchies.AsNoTracking()
                .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                            (ProjectContext.Matches(x.ParentProjectId, projectId) || ProjectContext.Matches(x.ChildProjectId, projectId)))
                .Select(x => new { Kind = WatermarkKind.Hierarchy, At = x.UpdatedAt }));
        if (!string.IsNullOrEmpty(referenceNeedle))
        {
            watermarks = watermarks
                .Concat(dbContext.ProjectWorkItems.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && ProjectContext.Matches(x.ProjectId, projectId) &&
                                (x.Title.Contains(referenceNeedle) || x.Description.Contains(referenceNeedle) ||
                                 x.ChecklistItems.Any(item => item.Content.Contains(referenceNeedle))))
                    .Select(x => new { Kind = WatermarkKind.WorkItem, At = x.UpdatedAt }))
                .Concat(dbContext.ProjectWorkItemChecklistItems.AsNoTracking()
                    .Where(x => x.WorkItem != null && x.WorkItem.TenantId == tenantId &&
                                x.WorkItem.OwnerUserId == ownerUserId && ProjectContext.Matches(x.WorkItem.ProjectId, projectId) &&
                                x.Content.Contains(referenceNeedle))
                    .Select(x => new { Kind = WatermarkKind.Checklist, At = x.UpdatedAt }))
                .Concat(dbContext.DiscussionThreads.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && ProjectContext.Matches(x.HostProjectId, projectId) &&
                                (x.Title.Contains(referenceNeedle) || x.Messages.Any(m => m.Content.Contains(referenceNeedle))))
                    .Select(x => new { Kind = WatermarkKind.Discussion, At = x.UpdatedAt }))
                .Concat(dbContext.DiscussionMessages.AsNoTracking()
                    .Where(x => x.Thread != null && x.Thread.TenantId == tenantId &&
                                x.Thread.OwnerUserId == ownerUserId && ProjectContext.Matches(x.Thread.HostProjectId, projectId) &&
                                x.Content.Contains(referenceNeedle))
                    .Select(x => new { Kind = WatermarkKind.Message, At = x.CreatedAt }))
                .Concat(dbContext.SuggestedActions.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId &&
                                ProjectContext.Matches(x.ProjectId, projectId) && x.DedupKey.Contains(referenceNeedle))
                    .Select(x => new { Kind = WatermarkKind.Action, At = x.UpdatedAt }))
                .Concat(dbContext.ConversationInsights.AsNoTracking()
                    .Where(x => x.TenantId == tenantId && x.OwnerUserId == ownerUserId && ProjectContext.Matches(x.ProjectId, projectId) &&
                                (!excludedInsightId.HasValue || x.Id != excludedInsightId.Value) &&
                                (x.Title.Contains(referenceNeedle) || x.Content.Contains(referenceNeedle) ||
                                 x.Summary.Contains(referenceNeedle) || x.DedupKey.Contains(referenceNeedle)))
                    .Select(x => new { Kind = WatermarkKind.Insight, At = x.UpdatedAt }));
        }

        var latestWatermarks = await watermarks.GroupBy(x => x.Kind)
            .Select(group => new { Kind = group.Key, At = (DateTimeOffset?)group.Max(x => x.At) })
            .ToDictionaryAsync(x => x.Kind, x => x.At, cancellationToken);
        return new(
            excludedInsightId.GetValueOrDefault(),
            latestWatermarks.GetValueOrDefault(WatermarkKind.ProjectInformation),
            latestWatermarks.GetValueOrDefault(WatermarkKind.WorkItem),
            latestWatermarks.GetValueOrDefault(WatermarkKind.Checklist),
            latestWatermarks.GetValueOrDefault(WatermarkKind.Discussion),
            latestWatermarks.GetValueOrDefault(WatermarkKind.Message),
            latestWatermarks.GetValueOrDefault(WatermarkKind.Action),
            latestWatermarks.GetValueOrDefault(WatermarkKind.Insight),
            latestWatermarks.GetValueOrDefault(WatermarkKind.Hierarchy));
    }

    private static string FormatWatermark(DateTimeOffset? timestamp)
        => timestamp?.UtcTicks.ToString() ?? string.Empty;

    private enum WatermarkKind
    {
        ProjectInformation,
        WorkItem,
        Checklist,
        Discussion,
        Message,
        Action,
        Insight,
        Hierarchy
    }

    public static string FindingPayload(GovernanceFinding finding)
        => $"{finding.Type}|{finding.DedupKey}|{finding.DetailsJson}";

    public static string InsightPayload(ConversationInsight insight)
        => $"{insight.InsightType}|{insight.Title}|{insight.Content}|{insight.Summary}|{string.Join(',', insight.Tags.Order(StringComparer.OrdinalIgnoreCase))}|{insight.Importance}|{insight.Confidence}";

    internal static Task<Guid[]> FindCurrentExactMemoryIdsAsync(IApplicationDbContext dbContext,
        ContextHubRequestActor actor, ConversationInsight insight, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var title = NormalizeExactText(insight.Title);
        var summary = NormalizeExactText(insight.Summary);
        return dbContext.MemoryItems.AsNoTracking().Where(memory =>
                memory.TenantId == actor.TenantId && memory.OwnerUserId == actor.UserId &&
                ProjectContext.Matches(memory.ProjectId, insight.ProjectId) &&
                memory.Status == MemoryStatus.Active && memory.AuthorityState == MemoryAuthorityState.Current &&
                memory.ValidFrom <= now && (!memory.ValidUntil.HasValue || memory.ValidUntil > now) &&
                memory.Title.Trim().ToLower() == title && memory.Summary.Trim().ToLower() == summary)
            .Select(memory => memory.Id).Take(2).ToArrayAsync(cancellationToken);
    }

    internal static bool IsProtectedInsight(ConversationInsight insight)
        => insight.InsightType is ConversationInsightType.Decision or ConversationInsightType.Fact || insight.Importance >= 0.8m;

    internal static string NormalizeExactText(string? value)
        => value?.Trim().ToLowerInvariant() ?? string.Empty;
}

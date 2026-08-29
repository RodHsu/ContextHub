using System.Security.Cryptography;
using System.Text;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Application;

internal static class GovernanceEvidenceFingerprint
{
    public const string PolicyVersion = "autonomous-semantic-2026-08-28-v1";

    public static async Task<string> BuildAsync(
        IApplicationDbContext dbContext,
        string projectId,
        Guid? primaryMemoryId,
        Guid? secondaryMemoryId,
        Guid? excludedInsightId,
        string semanticPayload,
        CancellationToken cancellationToken,
        IReadOnlyList<string>? referenceTexts = null)
    {
        var memoryIds = new[] { primaryMemoryId, secondaryMemoryId }.Where(x => x.HasValue).Select(x => x!.Value).ToArray();
        var referenceTokens = memoryIds.Select(x => x.ToString("D"))
            .Concat(referenceTexts ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var referenceNeedle = excludedInsightId?.ToString("D") ?? referenceTokens.FirstOrDefault() ?? string.Empty;
        var memoryRows = memoryIds.Length == 0
            ? []
            : await dbContext.MemoryItems.AsNoTracking().Where(x => memoryIds.Contains(x.Id))
                .OrderBy(x => x.Id)
                .Select(x => new { x.Id, x.ProjectId, x.Status, x.Version, x.UpdatedAt, x.MetadataJson })
                .ToArrayAsync(cancellationToken);
        var memoryEvidence = memoryRows.Select(x =>
            $"{x.Id:N}:{x.ProjectId}:{x.Status}:{x.Version}:{x.UpdatedAt.UtcTicks}:{x.MetadataJson}").ToArray();
        var projectInformationUpdated = await dbContext.MemoryItems.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.ExternalKey == "system:project-information")
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var workItemUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.ProjectWorkItems.AsNoTracking()
            .Where(x => x.ProjectId == projectId && (x.Title.Contains(referenceNeedle) || x.Description.Contains(referenceNeedle)))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var discussionUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.DiscussionThreads.AsNoTracking()
            .Where(x => x.HostProjectId == projectId &&
                        (x.Title.Contains(referenceNeedle) || x.Messages.Any(m => m.Content.Contains(referenceNeedle))))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var actionUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.SuggestedActions.AsNoTracking()
            .Where(x => x.ProjectId == projectId && x.DedupKey.Contains(referenceNeedle))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var insightUpdated = string.IsNullOrEmpty(referenceNeedle) ? null : await dbContext.ConversationInsights.AsNoTracking()
            .Where(x => x.ProjectId == projectId && (!excludedInsightId.HasValue || x.Id != excludedInsightId.Value) &&
                        x.Content.Contains(referenceNeedle))
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);
        var hierarchyUpdated = await dbContext.ProjectHierarchies.AsNoTracking()
            .Where(x => x.ParentProjectId == projectId || x.ChildProjectId == projectId)
            .Select(x => (DateTimeOffset?)x.UpdatedAt).MaxAsync(cancellationToken);

        var canonical = string.Join('\n', new[]
        {
            PolicyVersion,
            projectId.ToLowerInvariant(),
            semanticPayload,
            string.Join('\n', memoryEvidence),
            projectInformationUpdated?.UtcTicks.ToString() ?? string.Empty,
            workItemUpdated?.UtcTicks.ToString() ?? string.Empty,
            discussionUpdated?.UtcTicks.ToString() ?? string.Empty,
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
}

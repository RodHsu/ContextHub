namespace Memory.Application;

public interface IGovernanceInsightReferenceReader
{
    Task<IReadOnlyDictionary<Guid, GovernanceInsightReferenceWatermarks>> ReadInsightReferenceWatermarksAsync(
        Guid tenantId, Guid ownerUserId, Guid[] insightIds, CancellationToken cancellationToken);
}

public sealed record GovernanceInsightReferenceWatermarks(
    Guid InsightId,
    DateTimeOffset? ProjectInformation,
    DateTimeOffset? WorkItem,
    DateTimeOffset? Checklist,
    DateTimeOffset? Discussion,
    DateTimeOffset? Message,
    DateTimeOffset? Action,
    DateTimeOffset? Insight,
    DateTimeOffset? Hierarchy);

using Memory.Domain;

namespace Memory.Application;

public sealed record MemoryLinkResult(
    Guid Id,
    Guid FromId,
    Guid ToId,
    string LinkType,
    DateTimeOffset CreatedAt);

public sealed record MemorySourceContextResult(
    Guid? SourceConnectionId,
    string? ConnectorName,
    string? Cursor,
    string? SourceVersion,
    string? OriginPathOrUrl,
    DateTimeOffset? SyncedAt,
    DateTimeOffset? LastSuccessfulSyncAt,
    IReadOnlyList<string> Lineage);

public sealed record MemoryGovernanceFindingSummaryResult(
    Guid Id,
    GovernanceFindingType Type,
    GovernanceFindingStatus Status,
    string Title,
    string Summary,
    DateTimeOffset UpdatedAt);

public sealed record DashboardEvaluationSummaryResult(
    Guid RunId,
    Guid SuiteId,
    string SuiteName,
    EvaluationRunStatus Status,
    decimal HitRate,
    decimal RecallAtK,
    decimal MeanReciprocalRank,
    double AverageLatencyMs,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

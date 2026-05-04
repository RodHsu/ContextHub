using Memory.Domain;

namespace Memory.Application;

public sealed record SourceConnectionCreateRequest(
    string Name,
    SourceKind SourceKind,
    string ConfigJson,
    string? SecretJson = null,
    bool Enabled = true,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record SourceConnectionUpdateRequest(
    Guid Id,
    string? Name = null,
    string? ConfigJson = null,
    string? SecretJson = null,
    bool? Enabled = null,
    string? ProjectId = null);

public sealed record SourceConnectionResult(
    Guid Id,
    string ProjectId,
    string Name,
    SourceKind SourceKind,
    bool Enabled,
    string ConfigJson,
    bool HasSecret,
    string LastCursor,
    DateTimeOffset? LastSuccessfulSyncAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record SourceSyncRunResult(
    Guid Id,
    Guid SourceConnectionId,
    string ProjectId,
    SourceSyncTrigger Trigger,
    SourceSyncStatus Status,
    int ScannedCount,
    int UpsertedCount,
    int ArchivedCount,
    int ErrorCount,
    string CursorBefore,
    string CursorAfter,
    string Error,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt);

public sealed record SourceListRequest(
    string ProjectId = ProjectContext.DefaultProjectId,
    bool? Enabled = null,
    SourceKind? SourceKind = null);

public sealed record SourceSyncRequest(
    Guid SourceConnectionId,
    SourceSyncTrigger Trigger = SourceSyncTrigger.Manual,
    bool Force = false,
    string? ProjectId = null);

public sealed record EnqueueSourceSyncResult(Guid JobId, MemoryJobStatus Status);

public sealed record GovernanceFindingListRequest(
    string ProjectId = ProjectContext.DefaultProjectId,
    GovernanceFindingType? Type = null,
    GovernanceFindingStatus? Status = null,
    int Limit = 100);

public sealed record GovernanceAnalyzeRequest(
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record GovernanceAnalyzeResult(
    string ProjectId,
    int OpenFindingCount,
    int PendingActionCount,
    DateTimeOffset AnalyzedAtUtc);

public sealed record GovernanceFindingResult(
    Guid Id,
    string ProjectId,
    Guid? SourceConnectionId,
    Guid? PrimaryMemoryId,
    Guid? SecondaryMemoryId,
    GovernanceFindingType Type,
    GovernanceFindingStatus Status,
    string Title,
    string Summary,
    string DetailsJson,
    string DedupKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EvaluateCaseUpsertRequest(
    string ScenarioLabel,
    string Query,
    IReadOnlyList<Guid>? ExpectedMemoryIds = null,
    IReadOnlyList<string>? ExpectedExternalKeys = null);

public sealed record EvaluationSuiteCreateRequest(
    string Name,
    string Description,
    IReadOnlyList<EvaluateCaseUpsertRequest> Cases,
    string ProjectId = ProjectContext.DefaultProjectId);

public sealed record EvaluationCaseResult(
    Guid Id,
    Guid SuiteId,
    string ProjectId,
    string ScenarioLabel,
    string Query,
    IReadOnlyList<Guid> ExpectedMemoryIds,
    IReadOnlyList<string> ExpectedExternalKeys,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record EvaluationSuiteResult(
    Guid Id,
    string ProjectId,
    string Name,
    string Description,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<EvaluationCaseResult> Cases);

public sealed record EvaluationRunRequest(
    Guid SuiteId,
    string? EmbeddingProfile = null,
    MemoryQueryMode QueryMode = MemoryQueryMode.CurrentOnly,
    bool UseSummaryLayer = false,
    int TopK = 5);

public sealed record EvaluationRunItemResult(
    Guid Id,
    Guid RunId,
    Guid CaseId,
    string Query,
    string ScenarioLabel,
    IReadOnlyList<Guid> ExpectedMemoryIds,
    IReadOnlyList<string> ExpectedExternalKeys,
    IReadOnlyList<Guid> HitMemoryIds,
    IReadOnlyList<string> HitExternalKeys,
    bool HitAtK,
    decimal RecallAtK,
    decimal ReciprocalRank,
    double LatencyMs,
    DateTimeOffset CreatedAt);

public sealed record EvaluationRunResult(
    Guid Id,
    Guid SuiteId,
    string ProjectId,
    EvaluationRunStatus Status,
    string EmbeddingProfile,
    MemoryQueryMode QueryMode,
    bool UseSummaryLayer,
    int TopK,
    decimal HitRate,
    decimal RecallAtK,
    decimal MeanReciprocalRank,
    double AverageLatencyMs,
    string Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<EvaluationRunItemResult> Items);

public sealed record SuggestedActionListRequest(
    string ProjectId = ProjectContext.DefaultProjectId,
    SuggestedActionStatus? Status = SuggestedActionStatus.Pending,
    SuggestedActionType? Type = null,
    int Limit = 100);

public sealed record SuggestedActionResult(
    Guid Id,
    string ProjectId,
    SuggestedActionType Type,
    SuggestedActionStatus Status,
    string Title,
    string Summary,
    string PayloadJson,
    string Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? ExecutedAt);

public sealed record SuggestedActionMutationResult(
    SuggestedActionResult Action,
    Guid? JobId = null);

public sealed record HubActionRequest(Guid Id);

public interface ISourceConnectionService
{
    Task<IReadOnlyList<SourceConnectionResult>> ListAsync(SourceListRequest request, CancellationToken cancellationToken);
    Task<SourceConnectionResult> CreateAsync(SourceConnectionCreateRequest request, CancellationToken cancellationToken);
    Task<SourceConnectionResult> UpdateAsync(SourceConnectionUpdateRequest request, CancellationToken cancellationToken);
    Task<EnqueueSourceSyncResult> EnqueueSyncAsync(SourceSyncRequest request, CancellationToken cancellationToken);
    Task<IReadOnlyList<SourceSyncRunResult>> ListRunsAsync(Guid sourceConnectionId, string? projectId, CancellationToken cancellationToken);
}

public interface ISourceSyncService
{
    Task ProcessSyncJobAsync(Guid jobId, CancellationToken cancellationToken);
}

public interface IGovernanceService
{
    Task<IReadOnlyList<GovernanceFindingResult>> ListAsync(GovernanceFindingListRequest request, CancellationToken cancellationToken);
    Task<GovernanceFindingResult> AcceptAsync(Guid id, CancellationToken cancellationToken);
    Task<GovernanceFindingResult> DismissAsync(Guid id, CancellationToken cancellationToken);
    Task AnalyzeAsync(string projectId, CancellationToken cancellationToken);
}

public interface IEvaluationService
{
    Task<IReadOnlyList<EvaluationSuiteResult>> ListSuitesAsync(string projectId, CancellationToken cancellationToken);
    Task<EvaluationSuiteResult> CreateSuiteAsync(EvaluationSuiteCreateRequest request, CancellationToken cancellationToken);
    Task<EvaluationRunResult> RunAsync(EvaluationRunRequest request, CancellationToken cancellationToken);
    Task<EvaluationRunResult?> GetRunAsync(Guid id, CancellationToken cancellationToken);
    Task<EvaluationRunResult?> GetLatestRunAsync(string projectId, CancellationToken cancellationToken);
}

public interface ISuggestedActionService
{
    Task<IReadOnlyList<SuggestedActionResult>> ListAsync(SuggestedActionListRequest request, CancellationToken cancellationToken);
    Task<SuggestedActionMutationResult> AcceptAsync(Guid id, CancellationToken cancellationToken);
    Task<SuggestedActionResult> DismissAsync(Guid id, CancellationToken cancellationToken);
}

public interface ISecretProtector
{
    string Protect(string plaintext);
    string Unprotect(string protectedText);
}

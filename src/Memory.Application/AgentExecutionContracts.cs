using Memory.Domain;

namespace Memory.Application;

public static class AgentExecutionContract
{
    public const string Version = "1.0";
    public const int DefaultLeaseSeconds = 300;
    public const int MaximumLeaseSeconds = 1800;
}

public sealed record AgentExecutionPrepareRequest(
    Guid WorkItemId,
    string ProjectId,
    string RepositoryId,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> AuthorityRefs,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<string> RequiredValidation,
    string AgentType,
    IReadOnlyList<string>? RequiredCapabilities,
    int Priority,
    int MaxAttempts,
    Guid? SkillResolutionId,
    string IdempotencyKey,
    Guid? ExecutionId = null);

public sealed record AgentExecutionClaimRequest(
    string ProjectId,
    string RepositoryId,
    string AgentId,
    string AgentType,
    IReadOnlyList<string>? Capabilities,
    int LeaseSeconds,
    string IdempotencyKey,
    IReadOnlyList<string>? AvailableTools = null);

public sealed record AgentExecutionLeaseRequest(
    Guid ExecutionId,
    string AgentId,
    string LeaseToken,
    long LeaseVersion,
    int LeaseSeconds,
    string IdempotencyKey,
    IReadOnlyList<string>? CurrentCapabilities = null,
    IReadOnlyList<string>? CurrentTools = null);

public sealed record AgentExecutionCheckpointRequest(
    Guid ExecutionId,
    string AgentId,
    string LeaseToken,
    long LeaseVersion,
    string Stage,
    string Summary,
    IReadOnlyList<string>? EvidenceRefs,
    int LeaseSeconds,
    string IdempotencyKey,
    IReadOnlyList<string>? CurrentCapabilities = null,
    IReadOnlyList<string>? CurrentTools = null);

public sealed record AgentExecutionTerminalRequest(
    Guid ExecutionId,
    string AgentId,
    string LeaseToken,
    long LeaseVersion,
    string ReasonClass,
    string Reason,
    IReadOnlyList<string>? EvidenceRefs,
    bool Retryable,
    string IdempotencyKey,
    IReadOnlyList<string>? CurrentCapabilities = null,
    IReadOnlyList<string>? CurrentTools = null);

public sealed record AgentExecutionCancelRequest(
    Guid ExecutionId,
    string ReasonClass,
    string Reason,
    IReadOnlyList<string>? EvidenceRefs,
    string IdempotencyKey);

public sealed record AgentExecutionPackage(
    string ContractVersion,
    Guid ExecutionId,
    Guid WorkItemId,
    string ProjectId,
    string RepositoryId,
    string Objective,
    IReadOnlyList<string> AcceptanceCriteria,
    IReadOnlyList<string> AuthorityRefs,
    IReadOnlyList<string> Constraints,
    IReadOnlyList<string> AllowedActions,
    IReadOnlyList<string> RequiredValidation,
    string AgentType,
    IReadOnlyList<string> RequiredCapabilities,
    string ContextVersion,
    SkillExecutionSnapshotResult? SkillSnapshot);

public sealed record AgentExecutionEventResult(
    Guid Id,
    AgentExecutionEventType EventType,
    long Sequence,
    string AgentId,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed record AgentExecutionResult(
    Guid Id,
    Guid WorkItemId,
    Guid? ParentExecutionId,
    string ProjectId,
    string RepositoryId,
    string AgentType,
    AgentExecutionStatus Status,
    int Priority,
    int Attempt,
    int MaxAttempts,
    string ClaimedByAgentId,
    long LeaseVersion,
    DateTimeOffset? LeaseExpiresAt,
    string FailureClass,
    string StructuredReasonJson,
    string PackageHash,
    AgentExecutionPackage Package,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? StartedAt,
    DateTimeOffset? CompletedAt,
    IReadOnlyList<AgentExecutionEventResult>? Events = null);

public sealed record AgentExecutionClaimResult(
    bool HasExecution,
    string Outcome,
    AgentExecutionResult? Execution,
    string? LeaseToken,
    bool Replayed);

public sealed record AgentExecutionMutationResult(
    AgentExecutionResult Execution,
    string Outcome,
    SkillExecutionSnapshotDecision? SkillDecision,
    IReadOnlyList<SkillExecutionSnapshotIssue> SkillIssues,
    bool Replayed);

public sealed record AgentExecutionListRequest(
    string ProjectId,
    AgentExecutionStatus? Status = null,
    int Limit = 100,
    int Offset = 0);

public sealed record AgentExecutionDashboardResult(
    string ProjectId,
    IReadOnlyDictionary<AgentExecutionStatus, int> Counts,
    int ActiveLeases,
    int ExpiredLeases,
    int RetryableFailures,
    IReadOnlyList<AgentExecutionResult> Recent);

public interface IAgentExecutionService
{
    Task<AgentExecutionResult> PrepareAsync(AgentExecutionPrepareRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionClaimResult> ClaimNextAsync(AgentExecutionClaimRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionResult?> GetAsync(Guid executionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AgentExecutionResult>> ListAsync(AgentExecutionListRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionDashboardResult> GetDashboardAsync(string projectId, CancellationToken cancellationToken);
    Task<AgentExecutionMutationResult> HeartbeatAsync(AgentExecutionLeaseRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionMutationResult> CheckpointAsync(AgentExecutionCheckpointRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionMutationResult> BlockAsync(AgentExecutionTerminalRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionMutationResult> CompleteAsync(AgentExecutionTerminalRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionMutationResult> FailAsync(AgentExecutionTerminalRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionMutationResult> AbandonAsync(AgentExecutionTerminalRequest request, CancellationToken cancellationToken);
    Task<AgentExecutionMutationResult> CancelAsync(AgentExecutionCancelRequest request, CancellationToken cancellationToken);
}

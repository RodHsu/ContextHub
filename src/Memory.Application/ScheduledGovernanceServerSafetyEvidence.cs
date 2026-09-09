namespace Memory.Application;

/// <summary>
/// Exact server-owned receipt identity used to recover safety evidence. The
/// query contains no caller assertions; tenant and owner come from the
/// authenticated request actor.
/// </summary>
public sealed record ScheduledGovernanceServerSafetyEvidenceQuery(
    Guid TenantId,
    Guid OwnerUserId,
    Guid ReceiptId,
    string GovernanceRunId);

/// <summary>
/// Tri-state safety evidence derived from immutable server records. A null
/// value means the current durable evidence cannot prove the condition.
/// </summary>
public sealed record ScheduledGovernanceServerSafetyEvidenceSnapshot(
    long ReceiptEventSequence,
    string? ReviewRequestIdentityHash,
    ScheduledGovernanceRuntimeIdentity? CapturedRuntimeIdentity,
    bool? InitialReviewReceived,
    bool? CountInvariantSatisfied,
    bool? DecisionObeyed,
    bool? NoGeneralConnectorFallback,
    bool? NoUnauthorizedMutation,
    bool? NoDuplicateMutation,
    bool? DisplayNameUnchanged,
    bool? BusinessWorkItemsUntouched,
    bool? HostDispatchCompleted,
    bool? ImmutableSnapshotBound,
    bool? FixedReversibleExecutorUsed);

public sealed record ScheduledGovernanceNaturalOriginAuthoritySnapshot(
    string PlatformIssuer,
    string ControlPlaneSourceSystem,
    string Environment,
    string TaskBindingHash,
    string AutomationBindingHash,
    string ScheduleDigest,
    string ConfigurationDigest,
    string AuthorityEpochDigest);

public interface IScheduledGovernanceNaturalOriginAuthorityProvider
{
    ScheduledGovernanceNaturalOriginAuthoritySnapshot? GetCurrent();
}

public interface IScheduledGovernanceServerSafetyEvidenceProvider
{
    Task<ScheduledGovernanceServerSafetyEvidenceSnapshot?> GetAsync(
        ScheduledGovernanceServerSafetyEvidenceQuery query,
        CancellationToken cancellationToken);
}

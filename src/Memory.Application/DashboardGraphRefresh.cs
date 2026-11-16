namespace Memory.Application;

public sealed record DashboardGraphRefreshStatus(
    string Scope,
    long Generation,
    string Mode,
    DateTimeOffset? LastSuccessAtUtc,
    DateTimeOffset? LastFullAtUtc,
    DateTimeOffset? LastCheckedAtUtc,
    int DirtyProjectCount,
    double? DirtyAgeSeconds,
    bool LeaseActive,
    bool Stale,
    long FullBuilds,
    long IncrementalBuilds,
    long Skipped,
    long Deduplicated,
    long Failures,
    string LastError);

public sealed record DashboardGraphRefreshLease(
    Guid Token,
    long Generation,
    bool Full,
    IReadOnlySet<string> DirtyProjects,
    IReadOnlyDictionary<string, long> Revisions,
    DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>? Previous);

public interface IDashboardGraphRefreshCoordinator
{
    Task<DashboardGraphRefreshLease?> TryAcquireAsync(bool forceFull, CancellationToken cancellationToken);
    Task<bool> PublishAsync(DashboardGraphRefreshLease lease, DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload> snapshot, CancellationToken cancellationToken);
    Task FailAsync(DashboardGraphRefreshLease lease, string errorCategory, CancellationToken cancellationToken);
    Task<DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>?> ReadSnapshotAsync(CancellationToken cancellationToken);
    Task<DashboardGraphRefreshStatus> GetStatusAsync(CancellationToken cancellationToken);
}

public interface IIncrementalDashboardMemoryGraphIndexBuilder : IDashboardMemoryGraphIndexBuilder
{
    Task<DashboardMemoryGraphIndexSnapshotPayload> BuildIncrementalAsync(
        DashboardMemoryGraphIndexSnapshotPayload previous,
        IReadOnlySet<string> dirtyProjects,
        CancellationToken cancellationToken);
}

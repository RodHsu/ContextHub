using Memory.Application;

namespace Memory.UnitTests;

public sealed class TestDashboardGraphRefreshCoordinator(IDashboardSnapshotStore snapshotStore) : IDashboardGraphRefreshCoordinator
{
    public bool Skip { get; set; }
    public bool Publish { get; set; } = true;
    public int Acquisitions { get; private set; }
    public Task<DashboardGraphRefreshLease?> TryAcquireAsync(bool forceFull, CancellationToken cancellationToken)
    {
        Acquisitions++;
        return Task.FromResult<DashboardGraphRefreshLease?>(Skip ? null : new(Guid.NewGuid(), 1, true, new HashSet<string>(), new Dictionary<string, long>(), null));
    }
    public async Task<bool> PublishAsync(DashboardGraphRefreshLease lease, DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload> snapshot, CancellationToken cancellationToken)
    {
        if (Publish) await snapshotStore.SetAsync(snapshot, cancellationToken);
        return Publish;
    }
    public Task FailAsync(DashboardGraphRefreshLease lease, string errorCategory, CancellationToken cancellationToken) => Task.CompletedTask;
    public Task<DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>?> ReadSnapshotAsync(CancellationToken cancellationToken) => snapshotStore.GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(DashboardSnapshotKeys.MemoryGraphIndex, cancellationToken);
    public Task<DashboardGraphRefreshStatus> GetStatusAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
}

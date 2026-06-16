namespace Memory.Application;

public sealed class DashboardMemoryGraphIndexRefreshService(
    IDashboardMemoryGraphIndexBuilder builder,
    IDashboardSnapshotStore snapshotStore,
    IInstanceBehaviorSettingsAccessor behaviorSettingsAccessor,
    TimeProvider timeProvider) : IDashboardMemoryGraphIndexRefreshService
{
    public async Task<DashboardMemoryGraphIndexRefreshResult> RefreshAsync(
        string trigger,
        int? refreshIntervalSeconds,
        CancellationToken cancellationToken)
    {
        var payload = await builder.BuildAsync(cancellationToken);
        var capturedAtUtc = timeProvider.GetUtcNow();
        var effectiveIntervalSeconds = refreshIntervalSeconds ?? await GetMemoryGraphIndexIntervalSecondsAsync(cancellationToken);

        await snapshotStore.SetAsync(
            new DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>(
                DashboardSnapshotKeys.MemoryGraphIndex,
                capturedAtUtc,
                effectiveIntervalSeconds,
                DashboardSnapshotStalenessPolicy.ComputeStaleAfter(capturedAtUtc, effectiveIntervalSeconds),
                string.Empty,
                payload),
            cancellationToken);

        return new DashboardMemoryGraphIndexRefreshResult(
            capturedAtUtc,
            effectiveIntervalSeconds,
            string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim(),
            payload.Graph.Nodes.Count,
            payload.Graph.Edges.Count,
            payload.Graph.Stats.Truncated);
    }

    private async Task<int> GetMemoryGraphIndexIntervalSecondsAsync(CancellationToken cancellationToken)
    {
        var settings = await behaviorSettingsAccessor.GetCurrentAsync(cancellationToken);
        return Math.Max(1, settings.SnapshotPolling.MemoryGraphIndexSeconds);
    }
}

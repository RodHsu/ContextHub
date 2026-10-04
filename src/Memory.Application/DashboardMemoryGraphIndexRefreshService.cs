namespace Memory.Application;

public sealed class DashboardMemoryGraphIndexRefreshService(
    IDashboardMemoryGraphIndexBuilder builder,
    IDashboardSnapshotStore snapshotStore,
    IInstanceBehaviorSettingsAccessor behaviorSettingsAccessor,
    TimeProvider timeProvider,
    IDashboardGraphRefreshCoordinator coordinator,
    IRequestActorAccessor actorAccessor) : IDashboardMemoryGraphIndexRefreshService
{
    public async Task<DashboardMemoryGraphIndexRefreshResult> RefreshAsync(
        string trigger,
        int? refreshIntervalSeconds,
        CancellationToken cancellationToken)
    {
        trigger = string.IsNullOrWhiteSpace(trigger) ? "manual" : trigger.Trim();
        var effectiveIntervalSeconds = refreshIntervalSeconds ?? await GetMemoryGraphIndexIntervalSecondsAsync(cancellationToken);
        var lease = await coordinator.TryAcquireAsync(string.Equals(trigger, "manual", StringComparison.OrdinalIgnoreCase), cancellationToken);
        if (lease is null)
        {
            var previous = await snapshotStore.GetAsync<DashboardMemoryGraphIndexSnapshotPayload>(DashboardSnapshotKeys.MemoryGraphIndex, cancellationToken);
            return Result(previous?.Payload, previous?.CapturedAtUtc ?? timeProvider.GetUtcNow(), effectiveIntervalSeconds, "skipped");
        }
        using var buildTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        buildTimeout.CancelAfter(TimeSpan.FromMinutes(4));
        var previousActor = actorAccessor.Current;
        try
        {
            // All publishers build the same global projection. Endpoints authorize the caller
            // before invoking this internal job; consumers still apply their actor graph filter.
            actorAccessor.Current = new ContextHubRequestActor(null, null, "dashboard-graph-projection", null,
                [SecurityScopes.MemoryRead], [], IsAuthenticated: true, IsServiceActor: true);
            using var traffic = CacheMetricsTrafficScope.Begin("graph-background");
            var payload = !lease.Full && lease.Previous is not null && builder is IIncrementalDashboardMemoryGraphIndexBuilder incremental
                ? await incremental.BuildIncrementalAsync(lease.Previous.Payload, lease.DirtyProjects, buildTimeout.Token)
                : await builder.BuildAsync(buildTimeout.Token);
            var capturedAtUtc = timeProvider.GetUtcNow();
            var envelope = new DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>(
                DashboardSnapshotKeys.MemoryGraphIndex, capturedAtUtc, effectiveIntervalSeconds,
                DashboardSnapshotStalenessPolicy.ComputeStaleAfter(capturedAtUtc, effectiveIntervalSeconds), string.Empty, payload);
            if (!await coordinator.PublishAsync(lease, envelope, cancellationToken))
            {
                return Result(lease.Previous?.Payload, lease.Previous?.CapturedAtUtc ?? capturedAtUtc, effectiveIntervalSeconds, "superseded");
            }
            return Result(payload, capturedAtUtc, effectiveIntervalSeconds, trigger);
        }
        catch (Exception ex)
        {
            using var cleanupTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await coordinator.FailAsync(lease, ex is OperationCanceledException ? cancellationToken.IsCancellationRequested ? "Cancelled" : "BuildTimeout" : "BuildFailed", cleanupTimeout.Token);
            }
            catch (Exception)
            {
                // Expiry permits recovery if DB connectivity is unavailable during cleanup.
            }
            throw;
        }
        finally
        {
            actorAccessor.Current = previousActor;
        }
    }

    private static DashboardMemoryGraphIndexRefreshResult Result(DashboardMemoryGraphIndexSnapshotPayload? payload, DateTimeOffset capturedAt, int interval, string trigger)
        => new(capturedAt, interval, trigger, payload?.Graph.Nodes.Count ?? 0, payload?.Graph.Edges.Count ?? 0, payload?.Graph.Stats.Truncated ?? false);

    private async Task<int> GetMemoryGraphIndexIntervalSecondsAsync(CancellationToken cancellationToken)
    {
        var settings = await behaviorSettingsAccessor.GetCurrentAsync(cancellationToken);
        return Math.Max(1, settings.SnapshotPolling.MemoryGraphIndexSeconds);
    }
}

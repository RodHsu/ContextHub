using System.Text.Json;
using System.Collections;
using Docker.DotNet;
using Docker.DotNet.Models;
using Memory.Application;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace Memory.Infrastructure;

public sealed class DockerRuntimeOptions
{
    public string ComposeProject { get; set; } = "contexthub";
    public string DockerEndpoint { get; set; } = "unix:///var/run/docker.sock";
    public int SnapshotCacheSeconds { get; set; } = 1;
    public int SnapshotTimeoutSeconds { get; set; } = 10;
}

public static class DashboardSnapshotRetentionPolicy
{
    public static TimeSpan RetentionAfterStale { get; } = TimeSpan.FromHours(2);

    public static TimeSpan ComputeExpiration<TPayload>(DashboardSnapshotEnvelope<TPayload> envelope, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(envelope);

        var expiration = envelope.StaleAfterUtc - now + RetentionAfterStale;
        return expiration > TimeSpan.Zero
            ? expiration
            : TimeSpan.FromSeconds(1);
    }
}

public sealed class RedisDashboardSnapshotStore(
    IConnectionMultiplexer redis,
    IOptions<MemoryOptions> options,
    TimeProvider timeProvider) : IDashboardSnapshotStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly IDatabase _database = redis.GetDatabase();
    private readonly string _prefix = $"memory:{options.Value.Namespace}:dashboard:snapshot:";
    private readonly TimeProvider _timeProvider = timeProvider;

    public async Task<DashboardSnapshotEnvelope<TPayload>?> GetAsync<TPayload>(string key, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var value = await _database.StringGetAsync($"{_prefix}{key}");
        return value.IsNullOrEmpty
            ? null
            : JsonSerializer.Deserialize<DashboardSnapshotEnvelope<TPayload>>(value.ToString(), JsonOptions);
    }

    public async Task SetAsync<TPayload>(DashboardSnapshotEnvelope<TPayload> envelope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Serialize(envelope, JsonOptions);
        var expiration = DashboardSnapshotRetentionPolicy.ComputeExpiration(envelope, _timeProvider.GetUtcNow());
        await _database.StringSetAsync($"{_prefix}{envelope.Key}", payload, expiration);
    }
}

internal sealed record DockerRuntimeSnapshot(
    string Status,
    string Error,
    DockerHostSummaryResult Host,
    IReadOnlyList<DockerContainerRuntimeSnapshot> Containers,
    IReadOnlyList<DockerVolumeSummaryResult> Volumes);

internal sealed record DockerContainerRuntimeSnapshot(
    DockerContainerMetricResult Metric,
    IReadOnlyList<DockerContainerMountSnapshot> Mounts);

internal sealed record DockerContainerMountSnapshot(
    string Type,
    string Name,
    string Source,
    string Destination,
    bool ReadWrite);

public sealed class DockerRuntimeMetricsService(IOptions<DockerRuntimeOptions> options) : IDisposable
{
    private readonly DockerRuntimeOptions _options = options.Value;
    private readonly Lazy<DockerClient> _client = new(() => CreateDockerClient(options.Value.DockerEndpoint));
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly TimeSpan _cacheLifetime = TimeSpan.FromSeconds(Math.Max(0, options.Value.SnapshotCacheSeconds));
    private readonly TimeSpan _snapshotTimeout = TimeSpan.FromSeconds(Math.Max(1, options.Value.SnapshotTimeoutSeconds));
    private DockerRuntimeSnapshot? _cachedSnapshot;
    private DateTimeOffset _cachedAtUtc;

    internal async Task<DockerRuntimeSnapshot> GetSnapshotAsync(CancellationToken cancellationToken)
    {
        var cached = GetFreshSnapshot();
        if (cached is not null)
        {
            return cached;
        }

        var entered = false;
        try
        {
            await _snapshotLock.WaitAsync(cancellationToken);
            entered = true;

            cached = GetFreshSnapshot();
            if (cached is not null)
            {
                return cached;
            }

            DockerRuntimeSnapshot snapshot;
            try
            {
                snapshot = await CollectSnapshotWithTimeoutAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return _cachedSnapshot is { } timedOut
                    ? timedOut with { Status = "Degraded", Error = $"Docker metrics timed out after {_snapshotTimeout.TotalSeconds:0.#} seconds. Showing last snapshot." }
                    : CreateUnavailableSnapshot($"Docker metrics timed out after {_snapshotTimeout.TotalSeconds:0.#} seconds.");
            }

            if (!string.Equals(snapshot.Status, "Healthy", StringComparison.OrdinalIgnoreCase) &&
                _cachedSnapshot is { } previous)
            {
                return previous with { Status = "Degraded", Error = snapshot.Error };
            }

            _cachedSnapshot = snapshot;
            _cachedAtUtc = snapshot.Host.CapturedAtUtc;
            return snapshot;
        }
        finally
        {
            if (entered)
            {
                _snapshotLock.Release();
            }
        }
    }

    public void Dispose()
    {
        _snapshotLock.Dispose();
        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }
    }

    private DockerRuntimeSnapshot? GetFreshSnapshot()
    {
        if (_cachedSnapshot is null || _cacheLifetime == TimeSpan.Zero)
        {
            return null;
        }

        return DateTimeOffset.UtcNow - _cachedAtUtc <= _cacheLifetime
            ? _cachedSnapshot
            : null;
    }

    private async Task<DockerRuntimeSnapshot> CollectSnapshotWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_snapshotTimeout);
        return await CollectSnapshotAsync(timeoutCts.Token);
    }

    private async Task<DockerRuntimeSnapshot> CollectSnapshotAsync(CancellationToken cancellationToken)
    {
        try
        {
            var client = _client.Value;
            var info = await client.System.GetSystemInfoAsync(cancellationToken);
            var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
            {
                All = true
            }, cancellationToken);

            var scopedContainers = containers
                .Where(IsInComposeProject)
                .OrderBy(ResolveServiceName)
                .ThenBy(ResolveContainerName)
                .ToArray();

            var containerMetrics = await Task.WhenAll(scopedContainers.Select(container => CollectContainerMetricAsync(container, cancellationToken)));
            var stackMemoryUsage = containerMetrics.Sum(x => x.Metric.MemoryUsageBytes);

            var images = await client.Images.ListImagesAsync(new ImagesListParameters { All = true }, cancellationToken);
            var relevantImageIds = scopedContainers
                .Select(x => x.ImageID)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var imageCount = images.Count(x => relevantImageIds.Contains(x.ID));
            var volumes = await client.Volumes.ListAsync(cancellationToken);
            var volumeSummaries = (volumes.Volumes ?? [])
                .Where(IsInComposeProject)
                .OrderBy(x => x.Name)
                .Select(x => new DockerVolumeSummaryResult(
                    x.Name,
                    x.Driver,
                    (long)(x.UsageData?.Size ?? 0),
                    x.Mountpoint))
                .ToArray();

            return new DockerRuntimeSnapshot(
                "Healthy",
                string.Empty,
                new DockerHostSummaryResult(
                    info.Name,
                    info.ServerVersion,
                    info.OperatingSystem,
                    info.KernelVersion,
                    (int)info.NCPU,
                    info.MemTotal,
                    Math.Max(0, info.MemTotal - stackMemoryUsage),
                    scopedContainers.LongLength,
                    imageCount,
                    volumeSummaries.LongLength,
                    DateTimeOffset.UtcNow),
                containerMetrics,
                volumeSummaries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return CreateUnavailableSnapshot(ex.Message);
        }
    }

    private async Task<DockerContainerRuntimeSnapshot> CollectContainerMetricAsync(ContainerListResponse container, CancellationToken cancellationToken)
    {
        var client = _client.Value;
        var inspectTask = client.Containers.InspectContainerAsync(container.ID, cancellationToken);
        var statsTask = ReadStatsAsync(container.ID, cancellationToken);
        await Task.WhenAll(inspectTask, statsTask);

        var inspect = await inspectTask;
        var stats = await statsTask;
        var (rxBytes, txBytes) = SumNetwork(stats);
        var (diskReadBytes, diskWriteBytes) = SumBlkIo(stats);

        return new DockerContainerRuntimeSnapshot(
            new DockerContainerMetricResult(
                ResolveContainerName(container),
                ResolveServiceName(container),
                container.Image,
                inspect.State?.Status ?? container.State ?? "unknown",
                inspect.State?.Health?.Status ?? "n/a",
                (int)inspect.RestartCount,
                CalculateCpuPercent(stats),
                (long)stats.MemoryStats.Usage,
                (long)stats.MemoryStats.Limit,
                rxBytes,
                txBytes,
                diskReadBytes,
                diskWriteBytes),
            ExtractMountSnapshots(inspect, container));
    }

    private bool IsInComposeProject(ContainerListResponse container)
        => container.Labels is not null &&
           container.Labels.TryGetValue("com.docker.compose.project", out var project) &&
           string.Equals(project, _options.ComposeProject, StringComparison.OrdinalIgnoreCase);

    private bool IsInComposeProject(VolumeResponse volume)
        => volume.Labels is not null &&
           volume.Labels.TryGetValue("com.docker.compose.project", out var project) &&
           string.Equals(project, _options.ComposeProject, StringComparison.OrdinalIgnoreCase);

    private static string ResolveContainerName(ContainerListResponse container)
        => container.Names?.FirstOrDefault()?.TrimStart('/') ?? container.ID[..12];

    private static string ResolveServiceName(ContainerListResponse container)
        => container.Labels is not null && container.Labels.TryGetValue("com.docker.compose.service", out var service)
            ? service
            : ResolveContainerName(container);

    private async Task<ContainerStatsResponse> ReadStatsAsync(string containerId, CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<ContainerStatsResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var progress = new Progress<ContainerStatsResponse>(response =>
        {
            if (completion.TrySetResult(response))
            {
                linkedCts.Cancel();
            }
        });

        try
        {
            await _client.Value.Containers.GetContainerStatsAsync(
                containerId,
                new ContainerStatsParameters { Stream = true },
                progress,
                linkedCts.Token);
        }
        catch (OperationCanceledException) when (completion.Task.IsCompleted)
        {
        }

        return await completion.Task.WaitAsync(cancellationToken);
    }

    private static DockerClient CreateDockerClient(string endpoint)
    {
        var configuration = new DockerClientConfiguration(new Uri(endpoint));
        const string createClientMethodName = "CreateClient";
        var configurationType = configuration.GetType();
        var withVersion = configurationType.GetMethod(createClientMethodName, [typeof(System.Version)]);
        if (withVersion is not null)
        {
            return (DockerClient)withVersion.Invoke(configuration, [null])!;
        }

        var withoutParameters = configurationType.GetMethod(createClientMethodName, Type.EmptyTypes)
            ?? throw new MissingMethodException(configurationType.FullName, createClientMethodName);
        return (DockerClient)withoutParameters.Invoke(configuration, null)!;
    }

    private static IReadOnlyList<DockerContainerMountSnapshot> ExtractMountSnapshots(
        ContainerInspectResponse inspect,
        ContainerListResponse container)
    {
        var mounts = ReadMountSnapshotsFromObject(inspect, "Mounts");
        if (mounts.Count > 0)
        {
            return mounts;
        }

        mounts = ReadMountSnapshotsFromObject(container, "Mounts");
        if (mounts.Count > 0)
        {
            return mounts;
        }

        return ReadBindSnapshotsFromHostConfig(inspect);
    }

    private static IReadOnlyList<DockerContainerMountSnapshot> ReadMountSnapshotsFromObject(object source, string propertyName)
    {
        var property = source.GetType().GetProperty(propertyName);
        if (property?.GetValue(source) is not IEnumerable enumerable)
        {
            return [];
        }

        var mounts = new List<DockerContainerMountSnapshot>();
        foreach (var item in enumerable)
        {
            if (item is null)
            {
                continue;
            }

            mounts.Add(new DockerContainerMountSnapshot(
                ReadPropertyValue(item, "Type"),
                ReadPropertyValue(item, "Name"),
                ReadPropertyValue(item, "Source"),
                ReadPropertyValue(item, "Destination"),
                bool.TryParse(ReadPropertyValue(item, "RW"), out var readWrite) && readWrite));
        }

        return mounts;
    }

    private static string ReadPropertyValue(object source, string propertyName)
        => source.GetType().GetProperty(propertyName)?.GetValue(source)?.ToString() ?? string.Empty;

    private static IReadOnlyList<DockerContainerMountSnapshot> ReadBindSnapshotsFromHostConfig(object source)
    {
        var hostConfig = source.GetType().GetProperty("HostConfig")?.GetValue(source);
        var binds = hostConfig?.GetType().GetProperty("Binds")?.GetValue(hostConfig) as IEnumerable;
        if (binds is null)
        {
            return [];
        }

        var mounts = new List<DockerContainerMountSnapshot>();
        foreach (var bind in binds)
        {
            if (bind is not string bindSpec || !TryParseBindSpec(bindSpec, out var mount))
            {
                continue;
            }

            mounts.Add(mount);
        }

        return mounts;
    }

    private static bool TryParseBindSpec(string bindSpec, out DockerContainerMountSnapshot mount)
    {
        mount = default!;
        if (string.IsNullOrWhiteSpace(bindSpec))
        {
            return false;
        }

        var lastSeparator = bindSpec.LastIndexOf(':');
        if (lastSeparator <= 0)
        {
            return false;
        }

        var modeCandidate = bindSpec[(lastSeparator + 1)..];
        var sourceAndDestination = bindSpec;
        var mode = "rw";
        if (!modeCandidate.Contains('/') && !modeCandidate.Contains('\\'))
        {
            sourceAndDestination = bindSpec[..lastSeparator];
            mode = modeCandidate;
        }

        var destinationSeparator = sourceAndDestination.LastIndexOf(':');
        if (destinationSeparator <= 0)
        {
            return false;
        }

        var source = sourceAndDestination[..destinationSeparator];
        var destination = sourceAndDestination[(destinationSeparator + 1)..];
        var isNamedVolume = !source.Contains('/') && !source.Contains('\\');

        mount = new DockerContainerMountSnapshot(
            isNamedVolume ? "volume" : "bind",
            isNamedVolume ? source : string.Empty,
            source,
            destination,
            !mode.Contains("ro", StringComparison.OrdinalIgnoreCase));
        return true;
    }

    private static DockerRuntimeSnapshot CreateUnavailableSnapshot(string error)
        => new(
            "Unavailable",
            error,
            new DockerHostSummaryResult(
                "unavailable",
                "unavailable",
                "unavailable",
                "unavailable",
                0,
                0,
                0,
                0,
                0,
                0,
                DateTimeOffset.UtcNow),
            [],
            []);

    private static double CalculateCpuPercent(ContainerStatsResponse stats)
    {
        var cpuDelta = (double)(stats.CPUStats.CPUUsage.TotalUsage - stats.PreCPUStats.CPUUsage.TotalUsage);
        var systemDelta = (double)(stats.CPUStats.SystemUsage - stats.PreCPUStats.SystemUsage);
        if (cpuDelta <= 0 || systemDelta <= 0)
        {
            return 0;
        }

        var cpuCount = stats.CPUStats.OnlineCPUs > 0
            ? stats.CPUStats.OnlineCPUs
            : (uint)Math.Max(stats.CPUStats.CPUUsage.PercpuUsage?.Count ?? 1, 1);
        return (cpuDelta / systemDelta) * cpuCount * 100d;
    }

    private static (long RxBytes, long TxBytes) SumNetwork(ContainerStatsResponse stats)
    {
        long rxBytes = 0;
        long txBytes = 0;
        if (stats.Networks is null)
        {
            return (0, 0);
        }

        foreach (var network in stats.Networks.Values)
        {
            rxBytes += (long)network.RxBytes;
            txBytes += (long)network.TxBytes;
        }

        return (rxBytes, txBytes);
    }

    private static (long ReadBytes, long WriteBytes) SumBlkIo(ContainerStatsResponse stats)
    {
        long readBytes = 0;
        long writeBytes = 0;
        foreach (var item in stats.BlkioStats.IoServiceBytesRecursive ?? [])
        {
            if (item.Op.Equals("read", StringComparison.OrdinalIgnoreCase))
            {
                readBytes += (long)item.Value;
            }
            else if (item.Op.Equals("write", StringComparison.OrdinalIgnoreCase))
            {
                writeBytes += (long)item.Value;
            }
        }

        return (readBytes, writeBytes);
    }
}

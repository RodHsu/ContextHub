using Docker.DotNet;
using Docker.DotNet.Models;
using Memory.Application;
using Microsoft.Extensions.Options;

namespace Memory.Dashboard.Services;

public sealed record DockerHostSummaryResult(
    string HostName,
    string ServerVersion,
    string OperatingSystem,
    string KernelVersion,
    int CpuCount,
    long TotalMemoryBytes,
    long EstimatedAvailableMemoryBytes,
    long ActiveContainerCount,
    long ImageCount,
    long VolumeCount,
    DateTimeOffset CapturedAtUtc);

public sealed record DockerContainerMetricResult(
    string Name,
    string Service,
    string Image,
    string State,
    string Health,
    int RestartCount,
    double CpuPercent,
    long MemoryUsageBytes,
    long MemoryLimitBytes,
    long NetworkRxBytes,
    long NetworkTxBytes,
    long DiskReadBytes,
    long DiskWriteBytes);

public sealed record DockerImageSummaryResult(
    string Id,
    string Tag,
    long SizeBytes,
    int Containers);

public sealed record DockerVolumeSummaryResult(
    string Name,
    string Driver,
    long SizeBytes,
    string Mountpoint);

public sealed record DockerStackSnapshotResult(
    string Status,
    string Error,
    DockerHostSummaryResult Host,
    IReadOnlyList<DockerContainerMetricResult> Containers,
    IReadOnlyList<DockerImageSummaryResult> Images,
    IReadOnlyList<DockerVolumeSummaryResult> Volumes);

public interface IDockerMetricsService
{
    Task<DockerStackSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken);
    Task<RestartAppContainersResult> RestartAppContainersAsync(RestartAppContainersRequest request, CancellationToken cancellationToken);
}

public sealed class DockerMetricsService : IDockerMetricsService, IDisposable
{
    private static readonly string[] RestartableServices = ["dashboard", "mcp-server", "worker", "embedding-service"];
    private readonly DashboardOptions _options;
    private readonly Lazy<DockerClient> _client;
    private readonly SemaphoreSlim _snapshotLock = new(1, 1);
    private readonly TimeSpan _cacheLifetime;
    private readonly TimeSpan _snapshotTimeout;
    private DockerStackSnapshotResult? _cachedSnapshot;
    private DateTimeOffset _cachedAtUtc;

    public DockerMetricsService(IOptions<DashboardOptions> options)
    {
        _options = options.Value;
        _client = new Lazy<DockerClient>(() => CreateDockerClient(_options.DockerEndpoint));
        _cacheLifetime = TimeSpan.FromSeconds(Math.Max(0, _options.DockerSnapshotCacheSeconds));
        _snapshotTimeout = TimeSpan.FromSeconds(Math.Max(1, _options.DockerSnapshotTimeoutSeconds));
    }

    public async Task<DockerStackSnapshotResult> GetSnapshotAsync(CancellationToken cancellationToken)
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

            DockerStackSnapshotResult snapshot;
            try
            {
                snapshot = await CollectSnapshotWithTimeoutAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return GetCachedSnapshot() is { } timedOutSnapshot
                    ? CreateStaleSnapshot(timedOutSnapshot, $"Docker metrics timed out after {_snapshotTimeout.TotalSeconds:0.#} seconds. Showing last snapshot.")
                    : CreateUnavailableSnapshot($"Docker metrics timed out after {_snapshotTimeout.TotalSeconds:0.#} seconds.");
            }

            if (!string.Equals(snapshot.Status, "Healthy", StringComparison.OrdinalIgnoreCase) &&
                GetCachedSnapshot() is { } previousSnapshot)
            {
                return CreateStaleSnapshot(previousSnapshot, snapshot.Error);
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

    public async Task<RestartAppContainersResult> RestartAppContainersAsync(RestartAppContainersRequest request, CancellationToken cancellationToken)
    {
        var requestedServices = (request.Services ?? RestartableServices)
            .Where(static service => !string.IsNullOrWhiteSpace(service))
            .Select(static service => service.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(service => RestartableServices.Contains(service, StringComparer.OrdinalIgnoreCase))
            .ToArray();

        var client = _client.Value;
        var containers = await client.Containers.ListContainersAsync(new ContainersListParameters
        {
            All = true
        }, cancellationToken);

        var containersByService = containers
            .Where(IsInComposeProject)
            .Where(container => container.Labels is not null &&
                                container.Labels.TryGetValue("com.docker.compose.service", out var service) &&
                                requestedServices.Contains(service, StringComparer.OrdinalIgnoreCase))
            .GroupBy(ResolveServiceName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);

        var restarted = new List<string>(requestedServices.Length);
        var skipped = new List<string>();

        foreach (var service in requestedServices)
        {
            if (!containersByService.TryGetValue(service, out var serviceContainers) || serviceContainers.Length == 0)
            {
                skipped.Add(service);
                continue;
            }

            foreach (var container in serviceContainers)
            {
                await client.Containers.RestartContainerAsync(container.ID, new ContainerRestartParameters(), cancellationToken);
            }

            restarted.Add(service);
        }

        _cachedSnapshot = null;

        return new RestartAppContainersResult(
            _options.InstanceId,
            _options.ComposeProject,
            restarted,
            skipped,
            DateTimeOffset.UtcNow);
    }

    private DockerStackSnapshotResult? GetFreshSnapshot()
    {
        if (_cachedSnapshot is null)
        {
            return null;
        }

        if (_cacheLifetime == TimeSpan.Zero)
        {
            return null;
        }

        return DateTimeOffset.UtcNow - _cachedAtUtc <= _cacheLifetime
            ? _cachedSnapshot
            : null;
    }

    private DockerStackSnapshotResult? GetCachedSnapshot()
        => _cachedSnapshot;

    private async Task<DockerStackSnapshotResult> CollectSnapshotWithTimeoutAsync(CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_snapshotTimeout);
        return await CollectSnapshotAsync(timeoutCts.Token);
    }

    private async Task<DockerStackSnapshotResult> CollectSnapshotAsync(CancellationToken cancellationToken)
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
                .OrderBy(x => ResolveServiceName(x))
                .ThenBy(x => ResolveContainerName(x))
                .ToArray();

            var containerMetrics = await Task.WhenAll(scopedContainers.Select(container => CollectContainerMetricAsync(container, cancellationToken)));
            var stackMemoryUsage = containerMetrics.Sum(x => x.MemoryUsageBytes);

            var images = await client.Images.ListImagesAsync(new ImagesListParameters { All = true }, cancellationToken);
            var relevantImageIds = scopedContainers
                .Select(x => x.ImageID)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var imageSummaries = images
                .Where(x => relevantImageIds.Contains(x.ID))
                .OrderByDescending(x => x.Size)
                .Select(x => new DockerImageSummaryResult(
                    x.ID,
                    x.RepoTags?.FirstOrDefault() ?? "<none>",
                    x.Size,
                    (int)x.Containers))
                .ToArray();

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

            var host = new DockerHostSummaryResult(
                info.Name,
                info.ServerVersion,
                info.OperatingSystem,
                info.KernelVersion,
                (int)info.NCPU,
                info.MemTotal,
                Math.Max(0, info.MemTotal - stackMemoryUsage),
                scopedContainers.LongLength,
                imageSummaries.LongLength,
                volumeSummaries.LongLength,
                DateTimeOffset.UtcNow);

            return new DockerStackSnapshotResult(
                "Healthy",
                string.Empty,
                host,
                containerMetrics,
                imageSummaries,
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

    private async Task<DockerContainerMetricResult> CollectContainerMetricAsync(ContainerListResponse container, CancellationToken cancellationToken)
    {
        var client = _client.Value;
        var inspectTask = client.Containers.InspectContainerAsync(container.ID, cancellationToken);
        var statsTask = ReadStatsAsync(container.ID, cancellationToken);
        await Task.WhenAll(inspectTask, statsTask);

        var inspect = await inspectTask;
        var stats = await statsTask;
        var (rxBytes, txBytes) = SumNetwork(stats);
        var (diskReadBytes, diskWriteBytes) = SumBlkIo(stats);

        return new DockerContainerMetricResult(
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
            diskWriteBytes);
    }

    private static DockerStackSnapshotResult CreateUnavailableSnapshot(string error)
        => new(
            "Degraded",
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
            [],
            []);

    private static DockerStackSnapshotResult CreateStaleSnapshot(DockerStackSnapshotResult snapshot, string error)
        => snapshot with
        {
            Status = "Degraded",
            Error = error
        };

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

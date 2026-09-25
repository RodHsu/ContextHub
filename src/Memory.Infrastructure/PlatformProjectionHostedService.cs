using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Memory.Infrastructure;

/// <summary>
/// Best-effort scheduler for rebuildable monitoring projections. Failures are isolated from
/// authority writes: immutable outbox rows remain the replay source for a later incremental or
/// scheduled full run.
/// </summary>
public sealed class PlatformProjectionHostedService(
    IServiceScopeFactory scopeFactory,
    ILogger<PlatformProjectionHostedService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan FullRebuildInterval = TimeSpan.FromHours(24);
    private const int ScopePageSize = 500;
    private readonly string ownerId = $"platform-projection:{Environment.MachineName}:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await ProjectAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Platform monitoring projection pass failed; authority commits remain available for replay.");
            }

            await Task.Delay(PollInterval, stoppingToken);
        }
    }

    private async Task ProjectAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var service = scope.ServiceProvider.GetRequiredService<IPlatformProjectionService>();
        var now = DateTimeOffset.UtcNow;
        for (var offset = 0; ; offset += ScopePageSize)
        {
            var projects = await dbContext.AuthorityOutboxEvents.AsNoTracking()
                .Select(x => new { x.TenantId, x.ProjectId })
                .Distinct()
                .OrderBy(x => x.TenantId)
                .ThenBy(x => x.ProjectId)
                .Skip(offset)
                .Take(ScopePageSize)
                .ToArrayAsync(cancellationToken);
            foreach (var project in projects)
            {
                var tenantScopeKey = project.TenantId?.ToString("D") ?? "system";
                var state = await dbContext.MonitoringProjectionStates.AsNoTracking().SingleOrDefaultAsync(
                    x => x.ProjectionName == PlatformProjectionContract.ProjectionName && x.TenantScopeKey == tenantScopeKey && x.ProjectId == project.ProjectId,
                    cancellationToken);
                var mode = state?.LastSuccessAt is null
                    ? PlatformBackgroundMode.Incremental
                    : state!.LastSuccessAt <= now - FullRebuildInterval
                        ? PlatformBackgroundMode.Full
                        : PlatformBackgroundMode.Incremental;
                var result = await service.RunAsync(new PlatformProjectionRunRequest(project.TenantId, project.ProjectId, mode, ownerId), cancellationToken);
                if (result.Status == PlatformBackgroundRunStatus.Running)
                {
                    // A bounded run deliberately yields at a checkpoint. The next poll resumes it
                    // using the persisted cursor and a fresh fenced lease.
                    logger.LogDebug("Platform projection checkpoint {RunId} for {ProjectId} at {Cursor}/{Boundary}.",
                        result.RunId, project.ProjectId, result.Cursor, result.AuthoritySequenceBoundary);
                }
            }

            if (projects.Length < ScopePageSize) break;
        }
    }
}

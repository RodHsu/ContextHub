using Memory.Application;

namespace Memory.Worker;

public sealed class JobWorker(
    IBackgroundJobProcessor processor,
    ICacheVersionStore cacheStore,
    ILogger<JobWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var job = await processor.ProcessNextAsync(stoppingToken);
                if (job is null)
                {
                    await cacheStore.WaitForJobSignalAsync(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }

                logger.LogInformation("Processed background job {JobId} with status {JobStatus}", job.Id, job.Status);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Background job loop failed.");
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
        }
    }
}

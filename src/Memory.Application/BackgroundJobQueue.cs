namespace Memory.Application;

public sealed class BackgroundJobQueue(
    IApplicationDbContext dbContext,
    ICacheVersionStore cacheStore) : IBackgroundJobQueue
{
    public async Task<Guid> EnqueueAsync(Memory.Domain.MemoryJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        await dbContext.MemoryJobs.AddAsync(job, cancellationToken);
        await dbContext.SaveChangesAsync(cancellationToken);
        await cacheStore.IncrementJobsAsync(cancellationToken);
        await cacheStore.PublishJobSignalAsync(job.Id, cancellationToken);
        return job.Id;
    }

    public async Task PublishSignalAsync(Guid jobId, CancellationToken cancellationToken)
    {
        await cacheStore.IncrementJobsAsync(cancellationToken);
        await cacheStore.PublishJobSignalAsync(jobId, cancellationToken);
    }
}

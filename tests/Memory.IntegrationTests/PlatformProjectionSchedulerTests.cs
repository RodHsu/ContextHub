using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Memory.IntegrationTests;

public sealed class PlatformProjectionSchedulerTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredTheory]
    [InlineData(false, 25, PlatformBackgroundMode.Full)]
    [InlineData(true, 25, PlatformBackgroundMode.Full)]
    [InlineData(true, 1, PlatformBackgroundMode.Incremental)]
    public async Task Recent_incremental_success_should_not_reset_the_full_reconciliation_clock(
        bool previousFull, int hoursSinceReconciliation, PlatformBackgroundMode expectedMode)
    {
        var project = $"scheduler-{Guid.NewGuid():N}";
        Guid previousRunId;
        await using (var setup = environment.GetFactory().Services.CreateAsyncScope())
        {
            var db = setup.ServiceProvider.GetRequiredService<MemoryDbContext>();
            db.AuthorityOutboxEvents.Add(Event(project));
            await db.SaveChangesAsync();
            var previous = await setup.ServiceProvider.GetRequiredService<IPlatformProjectionService>().RunAsync(
                new(null, project, previousFull ? PlatformBackgroundMode.Full : PlatformBackgroundMode.Incremental, "scheduler-test"),
                CancellationToken.None);
            previous.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
            previousRunId = previous.RunId;
            var history = await db.PlatformBackgroundRuns.SingleAsync(run => run.Id == previousRunId);
            history.CompletedAt = DateTimeOffset.UtcNow.AddHours(-hoursSinceReconciliation);
            await db.SaveChangesAsync();
            // The production projector updates this timestamp after every successful poll.
            var state = await db.MonitoringProjectionStates.SingleAsync(item => item.ProjectId == project);
            state.LastSuccessAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-1));
        }

        var scheduled = await RunScheduledPassAsync(project, previousRunId);
        scheduled.Mode.Should().Be(expectedMode);
        scheduled.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
        scheduled.CoverageComplete.Should().BeTrue();
    }

    [DockerRequiredFact]
    public async Task Scheduled_full_should_restore_a_late_committed_lower_sequence_despite_recent_incremental_success()
    {
        var project = $"scheduler-late-{Guid.NewGuid():N}";
        await using var earlierScope = environment.GetFactory().Services.CreateAsyncScope();
        var earlierDb = earlierScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        await using var transaction = await earlierDb.Database.BeginTransactionAsync();
        var earlier = Event(project);
        earlierDb.AuthorityOutboxEvents.Add(earlier);
        await earlierDb.SaveChangesAsync();
        Guid previousRunId;
        await using (var laterScope = environment.GetFactory().Services.CreateAsyncScope())
        {
            var db = laterScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var later = Event(project);
            db.AuthorityOutboxEvents.Add(later);
            await db.SaveChangesAsync();
            later.Sequence.Should().BeGreaterThan(earlier.Sequence);
            var previous = await laterScope.ServiceProvider.GetRequiredService<IPlatformProjectionService>().RunAsync(
                new(null, project, PlatformBackgroundMode.Incremental, "scheduler-test"), CancellationToken.None);
            previousRunId = previous.RunId;
            previous.CoverageComplete.Should().BeTrue();
            var history = await db.PlatformBackgroundRuns.SingleAsync(run => run.Id == previousRunId);
            history.CompletedAt = DateTimeOffset.UtcNow.AddHours(-25);
            await db.SaveChangesAsync();
        }
        await transaction.CommitAsync();

        await using (var check = environment.GetFactory().Services.CreateAsyncScope())
        {
            var db = check.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await db.AuthorityOutboxEvents.CountAsync(item => item.ProjectId == project)).Should().Be(2);
            (await db.MonitoringActivityProjections.CountAsync(item => item.ProjectId == project)).Should().Be(1);
        }

        var scheduled = await RunScheduledPassAsync(project, previousRunId);
        scheduled.Mode.Should().Be(PlatformBackgroundMode.Full);
        scheduled.ScannedCount.Should().Be(2);
        await using var verification = environment.GetFactory().Services.CreateAsyncScope();
        var verificationDb = verification.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var events = await verificationDb.AuthorityOutboxEvents.Where(item => item.ProjectId == project).Select(item => item.Id).ToArrayAsync();
        (await verificationDb.MonitoringActivityProjections.CountAsync(item => item.ProjectId == project)).Should().Be(2);
        (await verificationDb.PlatformOutboxDeliveries.CountAsync(item => events.Contains(item.OutboxEventId))).Should().Be(2);
    }

    private async Task<PlatformBackgroundRun> RunScheduledPassAsync(string project, Guid previousRunId)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var worker = new PlatformProjectionHostedService(
            environment.GetFactory().Services.GetRequiredService<IServiceScopeFactory>(),
            NullLogger<PlatformProjectionHostedService>.Instance);
        try
        {
            await worker.StartAsync(timeout.Token);
            while (true)
            {
                await using var scope = environment.GetFactory().Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
                var run = await db.PlatformBackgroundRuns.AsNoTracking().SingleOrDefaultAsync(
                    item => item.ProjectId == project && item.Id != previousRunId && item.Status == PlatformBackgroundRunStatus.Completed,
                    timeout.Token);
                if (run is not null) return run;
                await Task.Delay(50, timeout.Token);
            }
        }
        finally
        {
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await worker.StopAsync(stopTimeout.Token);
        }
    }

    private static AuthorityOutboxEvent Event(string project) => new()
    {
        ProjectId = project,
        Category = "Connections",
        AggregateType = "SourceConnection",
        AggregateId = Guid.NewGuid().ToString("D"),
        EventType = "Added",
        AuthorityRevision = 1,
        SecurityCritical = true,
        OccurredAt = DateTimeOffset.UtcNow
    };

    private sealed class DockerRequiredTheoryAttribute : TheoryAttribute
    {
        public DockerRequiredTheoryAttribute()
        {
            if (!DockerTestGate.Current.IsAvailable) Skip = DockerTestGate.Current.Reason;
        }
    }
}

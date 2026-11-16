using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class PlatformProjectionRecoveryTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredTheory]
    [InlineData(PlatformBackgroundMode.Incremental, false)]
    [InlineData(PlatformBackgroundMode.Incremental, true)]
    [InlineData(PlatformBackgroundMode.Full, false)]
    [InlineData(PlatformBackgroundMode.Full, true)]
    public async Task First_failed_transaction_should_persist_the_scope_boundary_and_resume_all_events(PlatformBackgroundMode mode, bool existingState)
    {
        var project = Project();
        var clock = new TestClock();
        long previousCursor = 0;
        long previousGeneration = 0;
        if (existingState)
        {
            await SeedAsync(project, 1);
            var previous = await RunAsync(project, PlatformBackgroundMode.Full, clock);
            previous.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
            previousCursor = previous.Cursor;
            previousGeneration = previous.Generation;
        }
        var events = await SeedAsync(project, 3);
        await using var fault = await MonitoringWriteFailure.CreateAsync(environment, project);
        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(project, mode, clock));
        var failed = await RetryRunAsync(project);
        failed.Status.Should().Be(PlatformBackgroundRunStatus.RetryScheduled);
        failed.AuthoritySequenceBoundary.Should().Be(events.Max(x => x.Sequence));
        failed.Cursor.Should().Be(mode == PlatformBackgroundMode.Incremental ? previousCursor : 0);
        failed.Generation.Should().Be(mode == PlatformBackgroundMode.Full ? previousGeneration + 1 : previousGeneration);
        failed.ExpectedCount.Should().Be(mode == PlatformBackgroundMode.Full && existingState ? 4 : 3);
        failed.ScannedCount.Should().Be(0);
        failed.CoverageComplete.Should().BeFalse();
        (await CountsAsync(project)).Should().Be((existingState ? 4L : 3L, existingState ? 1L : 0L, existingState ? 1L : 0L));
        await fault.RemoveAsync();
        clock.UtcNow = failed.EligibleAt.AddSeconds(1);
        PlatformProjectionRunResult result;
        var passes = 0;
        do
        {
            result = await RunAsync(project, mode, clock);
            result.RunId.Should().Be(failed.Id);
            result.AuthoritySequenceBoundary.Should().Be(failed.AuthoritySequenceBoundary);
            passes++;
        } while (result.Status == PlatformBackgroundRunStatus.Running && passes < 4);
        result.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
        result.Scanned.Should().Be(failed.ExpectedCount);
        result.CoverageComplete.Should().BeTrue();
        var count = existingState ? 4L : 3L;
        (await CountsAsync(project)).Should().Be((count, count, count));
    }

    [DockerRequiredTheory]
    [InlineData(PlatformBackgroundMode.Incremental)]
    [InlineData(PlatformBackgroundMode.Full)]
    public async Task Failure_after_checkpoint_should_preserve_original_boundary_cursor_and_generation(PlatformBackgroundMode mode)
    {
        var project = Project();
        var clock = new TestClock();
        await SeedAsync(project, 5);
        var first = await RunAsync(project, mode, clock);
        first.Status.Should().Be(PlatformBackgroundRunStatus.Running);
        first.Scanned.Should().Be(2);
        await using var fault = await MonitoringWriteFailure.CreateAsync(environment, project);
        await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(project, mode, clock));
        var failed = await RetryRunAsync(project);
        failed.Id.Should().Be(first.RunId);
        failed.Cursor.Should().Be(first.Cursor);
        failed.AuthoritySequenceBoundary.Should().Be(first.AuthoritySequenceBoundary);
        failed.Generation.Should().Be(first.Generation);
        failed.ExpectedCount.Should().Be(5);
        failed.ScannedCount.Should().Be(2);
        failed.OwnerId.Should().BeEmpty();
        failed.LeaseTokenHash.Should().BeEmpty();
        var deferred = await RunAsync(project, mode, clock);
        deferred.Status.Should().Be(PlatformBackgroundRunStatus.RetryScheduled);
        deferred.Cursor.Should().Be(first.Cursor);
        await SeedAsync(project, 1);
        await fault.RemoveAsync();
        clock.UtcNow = failed.EligibleAt.AddSeconds(1);
        var resumed = await RunAsync(project, mode, clock);
        resumed.RunId.Should().Be(first.RunId);
        resumed.Scanned.Should().Be(4);
        var final = await RunAsync(project, mode, clock);
        final.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
        final.AuthoritySequenceBoundary.Should().Be(first.AuthoritySequenceBoundary, "new events must not change a persisted checkpoint boundary");
        final.Scanned.Should().Be(5);
        (await CountsAsync(project)).Should().Be((6L, 5L, 5L));
        var next = await RunAsync(project, PlatformBackgroundMode.Incremental, clock);
        next.Status.Should().Be(PlatformBackgroundRunStatus.Completed);
        (await CountsAsync(project)).Should().Be((6L, 6L, 6L));
    }

    [DockerRequiredFact]
    public async Task Repeated_first_run_failures_should_retain_boundary_until_dead_letter_without_losing_authority()
    {
        var project = Project();
        var clock = new TestClock();
        var events = await SeedAsync(project, 3);
        await using var fault = await MonitoringWriteFailure.CreateAsync(environment, project);
        PlatformBackgroundRun? previous = null;
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            await Assert.ThrowsAsync<DbUpdateException>(() => RunAsync(project, PlatformBackgroundMode.Incremental, clock));
            await using var scope = environment.GetFactory().Services.CreateAsyncScope();
            var current = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>().PlatformBackgroundRuns.AsNoTracking().SingleAsync(x => x.ProjectId == project);
            if (previous is not null) current.Id.Should().Be(previous.Id);
            current.Attempt.Should().Be(attempt);
            current.AuthoritySequenceBoundary.Should().Be(events.Max(x => x.Sequence));
            current.ExpectedCount.Should().Be(3);
            current.Cursor.Should().Be(0);
            current.CoverageComplete.Should().BeFalse();
            current.Status.Should().Be(attempt == 5 ? PlatformBackgroundRunStatus.DeadLetter : PlatformBackgroundRunStatus.RetryScheduled);
            previous = current;
            clock.UtcNow = current.EligibleAt.AddSeconds(1);
        }
        previous!.LeaseTokenHash.Should().BeEmpty();
        (await CountsAsync(project)).Should().Be((3L, 0L, 0L));
    }

    private static string Project() => $"projection-recovery-{Guid.NewGuid():N}";

    private async Task<PlatformProjectionRunResult> RunAsync(string project, PlatformBackgroundMode mode, TestClock clock)
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var service = new PlatformProjectionService(scope.ServiceProvider.GetRequiredService<MemoryDbContext>(), clock);
        return await service.RunAsync(new(null, project, mode, "recovery-test-owner", BatchSize: 2), CancellationToken.None);
    }

    private async Task<AuthorityOutboxEvent[]> SeedAsync(string project, int count)
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var events = Enumerable.Range(0, count).Select(_ => new AuthorityOutboxEvent
        {
            ProjectId = project,
            Category = "Connections",
            AggregateType = "RecoveryFixture",
            AggregateId = Guid.NewGuid().ToString("N"),
            EventType = "Added",
            AuthorityRevision = 1,
            SecurityCritical = true,
            PayloadJson = "{}",
            OccurredAt = DateTimeOffset.UtcNow
        }).ToArray();
        db.AuthorityOutboxEvents.AddRange(events);
        await db.SaveChangesAsync();
        return events;
    }

    private async Task<PlatformBackgroundRun> RetryRunAsync(string project)
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<MemoryDbContext>().PlatformBackgroundRuns.AsNoTracking()
            .SingleAsync(x => x.ProjectId == project && x.Status == PlatformBackgroundRunStatus.RetryScheduled);
    }

    private async Task<(long Authority, long Projection, long Delivery)> CountsAsync(string project)
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var events = db.AuthorityOutboxEvents.Where(x => x.ProjectId == project);
        return (await events.LongCountAsync(), await db.MonitoringActivityProjections.LongCountAsync(x => x.ProjectId == project),
            await db.PlatformOutboxDeliveries.LongCountAsync(x => events.Any(e => e.Id == x.OutboxEventId)));
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
    }

    private sealed class DockerRequiredTheoryAttribute : TheoryAttribute
    {
        public DockerRequiredTheoryAttribute()
        {
            if (!DockerTestGate.Current.IsAvailable) Skip = DockerTestGate.Current.Reason;
        }
    }

    private sealed class MonitoringWriteFailure(NpgsqlDataSource source, string name) : IAsyncDisposable
    {
        public static async Task<MonitoringWriteFailure> CreateAsync(ContainerTestEnvironment environment, string project)
        {
            var source = environment.GetFactory().Services.GetRequiredService<NpgsqlDataSource>();
            var name = $"test_projection_failure_{Guid.NewGuid():N}";
            // All identifiers and project IDs are generated test values; only this fixture's rows fail.
            await using var command = source.CreateCommand($$"""
                CREATE FUNCTION monitoring.{{name}}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF NEW.project_id = '{{project}}' THEN RAISE EXCEPTION 'Synthetic monitoring write failure'; END IF;
                    RETURN NEW;
                END $body$;
                CREATE TRIGGER {{name}} BEFORE INSERT OR UPDATE ON monitoring.activity_projections
                FOR EACH ROW EXECUTE FUNCTION monitoring.{{name}}();
                """);
            await command.ExecuteNonQueryAsync();
            return new(source, name);
        }

        public async Task RemoveAsync()
        {
            await using var command = source.CreateCommand($"DROP TRIGGER IF EXISTS {name} ON monitoring.activity_projections; DROP FUNCTION IF EXISTS monitoring.{name}();");
            await command.ExecuteNonQueryAsync();
        }

        public async ValueTask DisposeAsync() => await RemoveAsync();
    }
}

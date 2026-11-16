using System.Data.Common;
using System.Security.Cryptography;
using FluentAssertions;
using FluentAssertions.Execution;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Pgvector;
using Pgvector.EntityFrameworkCore;
using Testcontainers.PostgreSql;

namespace Memory.IntegrationTests;

public sealed class PlatformProjectionBatchQueryTests(PlatformProjectionBatchQueryTests.Database database)
    : IClassFixture<PlatformProjectionBatchQueryTests.Database>
{
    private const string Owner = "batch-query-test-owner";

    [DockerRequiredTheory]
    [InlineData(1, 0)]
    [InlineData(1, 1000)]
    [InlineData(25, 0)]
    [InlineData(25, 1000)]
    [InlineData(250, 0)]
    [InlineData(250, 1000)]
    public async Task Resumed_batch_should_bound_reads_and_not_materialize_prior_history(int batchSize, int extraHistory)
    {
        var project = NewProject();
        var clock = new TestClock();
        var seeded = await SeedAsync(project, null, batchSize + 2, clock);
        var ordered = seeded.OrderBy(item => item.Sequence).ToArray();
        PlatformProjectionRunResult first;
        await using (var setup = database.Open())
        {
            first = await RunAsync(setup, project, null, PlatformBackgroundMode.Incremental, 1, clock);
            Assert.True(first.Status == PlatformBackgroundRunStatus.Running && first.Scanned == 1);
            Assert.True(first.Cursor == ordered[0].Sequence && first.AuthoritySequenceBoundary == ordered[^1].Sequence);
            var existing = await setup.PlatformBackgroundEvents.Where(item => item.RunId == first.RunId).ToArrayAsync();
            Assert.True(existing.Length == 3);
            var maximum = existing.Max(item => item.Sequence);
            setup.PlatformBackgroundEvents.AddRange(Enumerable.Range(1, extraHistory).Select(index => new PlatformBackgroundEvent
            {
                RunId = first.RunId,
                Sequence = maximum + 2L * index,
                EventType = PlatformBackgroundEventType.Checkpoint,
                CreatedAt = clock.UtcNow,
                PayloadJson = "{}"
            }));
            await setup.SaveChangesAsync();
        }

        PlatformBackgroundEvent[] history;
        MonitoringProjectionState state;
        await using (var before = database.Open())
        {
            history = await before.PlatformBackgroundEvents.AsNoTracking().Where(item => item.RunId == first.RunId)
                .OrderBy(item => item.Sequence).ToArrayAsync();
            state = await before.MonitoringProjectionStates.AsNoTracking().SingleAsync(item => item.ProjectId == project);
        }

        var counter = new ReadCounter();
        PlatformProjectionRunResult current;
        int trackedEvents;
        await using (var actual = database.Open(counter))
        {
            Assert.True(!actual.ChangeTracker.Entries().Any());
            current = await RunAsync(actual, project, null, PlatformBackgroundMode.Incremental, batchSize, clock);
            trackedEvents = actual.ChangeTracker.Entries<PlatformBackgroundEvent>().Count();
        }

        await using (var verify = database.Open())
        {
            var run = await verify.PlatformBackgroundRuns.AsNoTracking().SingleAsync(item => item.Id == first.RunId);
            var afterState = await verify.MonitoringProjectionStates.AsNoTracking().SingleAsync(item => item.ProjectId == project);
            var expectedIds = ordered.Take(batchSize + 1).Select(item => item.Id).ToHashSet();
            var allSeedIds = seeded.Select(item => item.Id).ToArray();
            var projections = await verify.MonitoringActivityProjections.AsNoTracking().Where(item => item.ProjectId == project)
                .Select(item => item.OutboxEventId).ToArrayAsync();
            var deliveries = await verify.PlatformOutboxDeliveries.AsNoTracking().Where(item => allSeedIds.Contains(item.OutboxEventId))
                .ToArrayAsync();
            var afterHistory = await verify.PlatformBackgroundEvents.AsNoTracking().Where(item => item.RunId == first.RunId)
                .OrderBy(item => item.Sequence).ToArrayAsync();
            Assert.True(current.RunId == first.RunId && run.Id == first.RunId);
            Assert.True(run.Status == PlatformBackgroundRunStatus.Running && !run.CoverageComplete);
            Assert.True(current.Cursor == ordered[batchSize].Sequence && run.Cursor == ordered[batchSize].Sequence);
            Assert.True(run.ScannedCount == batchSize + 1 && current.Scanned == batchSize + 1);
            Assert.True(run.AuthoritySequenceBoundary == ordered[^1].Sequence && run.ExpectedCount == batchSize + 2);
            Assert.True(run.Generation == first.Generation && run.Mode == PlatformBackgroundMode.Incremental);
            Assert.True(run.LeaseVersion == first.LeaseVersion + 1 && run.OwnerId == Owner);
            Assert.True(run.LeaseExpiresAt == clock.UtcNow.AddSeconds(PlatformProjectionContract.DefaultLeaseSeconds));
            Assert.True(run.LeaseTokenHash == Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(current.LeaseToken))).ToLowerInvariant());
            Assert.True(expectedIds.SetEquals(projections));
            Assert.True(deliveries.Length == expectedIds.Count && expectedIds.SetEquals(deliveries.Select(item => item.OutboxEventId)));
            Assert.True(deliveries.All(item => item.Consumer == PlatformProjectionContract.ProjectionName && item.Status == PlatformOutboxDeliveryStatus.Delivered));
            Assert.True(afterState.Cursor == state.Cursor && afterState.AuthoritySequence == state.AuthoritySequence &&
                afterState.Generation == state.Generation && afterState.LastSuccessAt == state.LastSuccessAt &&
                afterState.NextRunAt == state.NextRunAt && afterState.UpdatedAt == state.UpdatedAt);
            Assert.True(afterHistory.Length == history.Length + 2);
            Assert.True(history.Zip(afterHistory).All(pair => pair.First.Id == pair.Second.Id &&
                pair.First.Sequence == pair.Second.Sequence && pair.First.EventType == pair.Second.EventType &&
                pair.First.PayloadJson == pair.Second.PayloadJson && pair.First.CreatedAt == pair.Second.CreatedAt));
            Assert.True(afterHistory[^2].Sequence == history[^1].Sequence + 1 && afterHistory[^2].EventType == PlatformBackgroundEventType.Claimed);
            Assert.True(afterHistory[^1].Sequence == history[^1].Sequence + 2 && afterHistory[^1].EventType == PlatformBackgroundEventType.Checkpoint);
        }

        // Count actual EF LINQ commands, not wire round trips or SaveChanges batches.
        using var assertions = new AssertionScope();
        counter.Reads.Should().BeLessThanOrEqualTo(10, "reads must stay bounded independently of batch size");
        trackedEvents.Should().BeLessThanOrEqualTo(2, "a fresh context needs only the two new audit events");
    }

    [DockerRequiredTheory]
    [InlineData(PlatformBackgroundMode.Incremental)]
    [InlineData(PlatformBackgroundMode.Full)]
    public async Task Batch_prefetch_should_preserve_repair_stale_delivery_and_same_context_replay(PlatformBackgroundMode mode)
    {
        var project = NewProject();
        var clock = new TestClock();
        var events = (await SeedAsync(project, null, 3, clock)).OrderBy(item => item.Sequence).ToArray();
        await using (var seed = database.Open())
        {
            seed.MonitoringActivityProjections.AddRange(
                new MonitoringActivityProjection
                {
                    OutboxEventId = events[0].Id,
                    AuthoritySequence = events[0].Sequence,
                    AuthorityRevision = 0,
                    EventType = "Old",
                    TenantId = Guid.NewGuid(),
                    ProjectId = "drifted-identity",
                    Generation = 9
                },
                new MonitoringActivityProjection
                {
                    OutboxEventId = events[1].Id,
                    AuthoritySequence = events[1].Sequence + 1000,
                    AuthorityRevision = 99,
                    EventType = "Newer",
                    ProjectId = project,
                    Generation = 17,
                    RedactedPayloadJson = "{}"
                });
            seed.PlatformOutboxDeliveries.AddRange(
                new PlatformOutboxDelivery
                {
                    OutboxEventId = events[0].Id,
                    Consumer = PlatformProjectionContract.ProjectionName,
                    Status = PlatformOutboxDeliveryStatus.RetryScheduled,
                    Attempt = 3,
                    LastErrorCode = "Synthetic",
                    UpdatedAt = clock.UtcNow
                },
                new PlatformOutboxDelivery
                {
                    OutboxEventId = events[0].Id,
                    Consumer = "other-consumer",
                    Status = PlatformOutboxDeliveryStatus.Pending,
                    Attempt = 7,
                    LastErrorCode = "Untouched",
                    UpdatedAt = clock.UtcNow
                });
            await seed.SaveChangesAsync();
        }

        await using (var actual = database.Open())
        {
            var repaired = await RunAsync(actual, project, null, mode, 3, clock);
            Assert.True(repaired.Status == PlatformBackgroundRunStatus.Completed && repaired.CoverageComplete);
            Assert.True(repaired.Expected == 3 && repaired.Scanned == 3 && repaired.Repaired == 1 && repaired.Stale == 1 && repaired.Rebuilt == 1 && repaired.Drift == 1);
            var projection = await actual.MonitoringActivityProjections.SingleAsync(item => item.OutboxEventId == events[0].Id);
            Assert.True(projection.TenantId == events[0].TenantId && projection.ProjectId == events[0].ProjectId &&
                projection.AuthoritySequence == events[0].Sequence && projection.AuthorityRevision == events[0].AuthorityRevision &&
                projection.EventType == events[0].EventType && projection.Generation == 9 &&
                projection.Category == events[0].Category && projection.AggregateType == events[0].AggregateType &&
                projection.AggregateId == events[0].AggregateId && projection.OccurredAt == events[0].OccurredAt &&
                projection.SecurityCritical == events[0].SecurityCritical && projection.RedactedPayloadJson == "{}");
            var stale = await actual.MonitoringActivityProjections.SingleAsync(item => item.OutboxEventId == events[1].Id);
            Assert.True(stale.AuthoritySequence == events[1].Sequence + 1000 && stale.AuthorityRevision == 99 && stale.EventType == "Newer" && stale.Generation == 17);
            var target = await actual.PlatformOutboxDeliveries.SingleAsync(item => item.OutboxEventId == events[0].Id && item.Consumer == PlatformProjectionContract.ProjectionName);
            Assert.True(target.Attempt == 4 && target.Status == PlatformOutboxDeliveryStatus.Delivered && target.LastErrorCode == string.Empty);

            // The same DbContext retains new tracked events across both replay checkpoints.
            var replayFirst = await RunAsync(actual, project, null, PlatformBackgroundMode.Full, 1, clock);
            var replayLast = await RunAsync(actual, project, null, PlatformBackgroundMode.Full, 2, clock);
            Assert.True(replayFirst.RunId != repaired.RunId && replayFirst.RunId == replayLast.RunId);
            Assert.True(replayFirst.Status == PlatformBackgroundRunStatus.Running && replayLast.Status == PlatformBackgroundRunStatus.Completed);
            Assert.True(replayLast.AuthoritySequenceBoundary == events[^1].Sequence && replayLast.Scanned == 3 && replayLast.CoverageComplete);
            var history = await actual.PlatformBackgroundEvents.AsNoTracking().Where(item => item.RunId == replayLast.RunId)
                .OrderBy(item => item.Sequence).ToArrayAsync();
            Assert.True(history.Select(item => item.Sequence).SequenceEqual(new long[] { 1, 2, 3, 4, 5 }));
            Assert.True(history.Select(item => item.EventType).SequenceEqual(new[]
            {
                PlatformBackgroundEventType.Prepared, PlatformBackgroundEventType.Claimed, PlatformBackgroundEventType.Checkpoint,
                PlatformBackgroundEventType.Claimed, PlatformBackgroundEventType.Completed
            }));
        }

        await using var verify = database.Open();
        var ids = events.Select(item => item.Id).ToArray();
        var projected = await verify.MonitoringActivityProjections.AsNoTracking().Where(item => ids.Contains(item.OutboxEventId)).Select(item => item.OutboxEventId).ToArrayAsync();
        var delivered = await verify.PlatformOutboxDeliveries.AsNoTracking().Where(item => ids.Contains(item.OutboxEventId)).ToArrayAsync();
        Assert.True(ids.ToHashSet().SetEquals(projected) && projected.Length == 3);
        Assert.True(delivered.Length == 4 && delivered.Count(item => item.Consumer == PlatformProjectionContract.ProjectionName) == 3);
        var other = delivered.Single(item => item.Consumer == "other-consumer");
        Assert.True(other.Attempt == 7 && other.Status == PlatformOutboxDeliveryStatus.Pending && other.LastErrorCode == "Untouched" && other.UpdatedAt == clock.UtcNow);
        Assert.True(delivered.Where(item => item.Consumer == PlatformProjectionContract.ProjectionName).All(item => item.Status == PlatformOutboxDeliveryStatus.Delivered));
    }

    [DockerRequiredFact]
    public async Task Batch_should_use_tenant_and_project_identity_without_touching_foreign_events()
    {
        var project = NewProject() + "-Σ";
        var alias = project.ToUpperInvariant().Replace('Σ', 'ς');
        var tenant = Guid.NewGuid();
        var foreignTenant = Guid.NewGuid();
        var clock = new TestClock();
        var selected = await SeedAsync(project, tenant, 1, clock);
        var equivalent = await SeedAsync(alias, tenant, 1, clock);
        var wrongTenant = await SeedAsync(project, foreignTenant, 1, clock);
        var wrongProject = await SeedAsync(project + "-other", tenant, 1, clock);
        var wrongSystem = await SeedAsync(project, null, 1, clock);
        var expected = selected.Concat(equivalent).Select(item => item.Id).ToHashSet();
        var forbidden = wrongTenant.Concat(wrongProject).Concat(wrongSystem).Select(item => item.Id).ToArray();
        await using (var actual = database.Open())
        {
            var result = await RunAsync(actual, project, tenant, PlatformBackgroundMode.Incremental, 250, clock);
            Assert.True(result.Status == PlatformBackgroundRunStatus.Completed && result.Expected == 2 && result.Scanned == 2);
            Assert.True(result.AuthoritySequenceBoundary == Math.Max(selected[0].Sequence, equivalent[0].Sequence));
        }

        await using var verify = database.Open();
        var allIds = expected.Concat(forbidden).ToArray();
        var projections = await verify.MonitoringActivityProjections.AsNoTracking().Where(item => allIds.Contains(item.OutboxEventId)).ToArrayAsync();
        var deliveries = await verify.PlatformOutboxDeliveries.AsNoTracking().Where(item => allIds.Contains(item.OutboxEventId)).ToArrayAsync();
        Assert.True(projections.Length == 2 && expected.SetEquals(projections.Select(item => item.OutboxEventId)) && projections.All(item => item.TenantId == tenant));
        Assert.True(deliveries.Length == 2 && expected.SetEquals(deliveries.Select(item => item.OutboxEventId)));
    }

    private async Task<AuthorityOutboxEvent[]> SeedAsync(string project, Guid? tenant, int count, TestClock clock)
    {
        await using var context = database.Open();
        var events = Enumerable.Range(0, count).Select(_ => new AuthorityOutboxEvent
        {
            TenantId = tenant,
            ProjectId = project,
            Category = "Connections",
            AggregateType = "BatchQueryFixture",
            AggregateId = Guid.NewGuid().ToString("N"),
            EventType = "Added",
            AuthorityRevision = 2,
            SecurityCritical = true,
            PayloadJson = "{}",
            OccurredAt = clock.UtcNow
        }).ToArray();
        context.AuthorityOutboxEvents.AddRange(events);
        await context.SaveChangesAsync();
        return events;
    }

    private static Task<PlatformProjectionRunResult> RunAsync(MemoryDbContext context, string project, Guid? tenant,
        PlatformBackgroundMode mode, int batchSize, TestClock clock)
        => new PlatformProjectionService(context, clock).RunAsync(new(tenant, project, mode, Owner, BatchSize: batchSize), CancellationToken.None);

    private static string NewProject() => "batch-query-" + Guid.NewGuid().ToString("N");

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = new(2030, 1, 2, 3, 4, 5, TimeSpan.Zero);
    }

    private sealed class ReadCounter : DbCommandInterceptor
    {
        public int Reads { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData,
            DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (eventData.CommandSource == CommandSource.LinqQuery) Reads++;
            return ValueTask.FromResult(result);
        }
    }

    public sealed class Database : IAsyncLifetime
    {
        private PostgreSqlContainer? postgres;
        private NpgsqlDataSource? source;

        public async Task InitializeAsync()
        {
            if (!DockerTestGate.Current.IsAvailable) return;
            postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
                .WithDatabase("projection_batch_tests")
                .WithUsername("postgres")
                .WithPassword(Convert.ToHexString(RandomNumberGenerator.GetBytes(32)))
                .WithPortBinding(5432, true)
                .WithCleanUp(true)
                .WithLogger(NullLogger.Instance)
                .Build();
            await postgres.StartAsync();
            var builder = new NpgsqlDataSourceBuilder(postgres.GetConnectionString());
            builder.UseVector();
            source = builder.Build();
            await new DatabaseMigrationHostedService(source, NullLogger<DatabaseMigrationHostedService>.Instance).StartAsync(CancellationToken.None);
        }

        public MemoryDbContext Open(params IInterceptor[] interceptors)
            => new(new DbContextOptionsBuilder<MemoryDbContext>().UseNpgsql(source ?? throw new InvalidOperationException("Fixture unavailable"), options => options.UseVector())
                .EnableSensitiveDataLogging(false).EnableDetailedErrors(false).AddInterceptors(interceptors).Options);

        public async Task DisposeAsync()
        {
            try
            {
                if (source is not null) await source.DisposeAsync();
            }
            finally
            {
                if (postgres is not null) await postgres.DisposeAsync();
            }
        }
    }

    private sealed class DockerRequiredTheoryAttribute : TheoryAttribute
    {
        public DockerRequiredTheoryAttribute()
        {
            if (!DockerTestGate.Current.IsAvailable) Skip = DockerTestGate.Current.Reason;
        }
    }

    private sealed class DockerRequiredFactAttribute : FactAttribute
    {
        public DockerRequiredFactAttribute()
        {
            if (!DockerTestGate.Current.IsAvailable) Skip = DockerTestGate.Current.Reason;
        }
    }
}

using System.Data;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class ScheduledGovernanceReliabilityPersistenceTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [DockerRequiredFact]
    public async Task Concurrent_observation_should_converge_to_one_row_and_survive_a_fresh_scope_read()
    {
        var factory = environment.GetFactory();
        TenantUser actor;
        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var seed = await setupDb.TenantUsers.AsNoTracking()
                .SingleAsync(x => x.Username == "contract-test-admin");
            actor = await CreateOwnerAsync(setupDb, seed.TenantId, "reliability-concurrent-owner");
        }

        var runId = $"reliability-concurrent-{Guid.NewGuid():N}";
        var receipt = CreateReceipt(runId, "Scheduled");
        using var firstScope = factory.Services.CreateScope();
        using var secondScope = factory.Services.CreateScope();
        SetActor(firstScope.ServiceProvider, actor);
        SetActor(secondScope.ServiceProvider, actor);
        var observations = await Task.WhenAll(
            firstScope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>()
                .ObserveAsync(receipt, CancellationToken.None),
            secondScope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>()
                .ObserveAsync(receipt, CancellationToken.None));

        observations.Should().OnlyContain(result =>
            result.Runs.Count == 1 && result.Runs[0].GovernanceRunId == runId);

        using var readScope = factory.Services.CreateScope();
        SetActor(readScope.ServiceProvider, actor);
        var readService = readScope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>();
        var readBack = await readService.GetAsync(CancellationToken.None);
        var persistedRun = readBack.Runs.Should().ContainSingle(x => x.GovernanceRunId == runId).Subject;
        persistedRun.ReceiptId.Should().Be(receipt.ReceiptId);
        persistedRun.ExpectedAtUtc.Should().NotBeNull();
        persistedRun.SignedDrift.Should().Be(TimeSpan.Zero);
        persistedRun.DriftWithinTolerance.Should().BeTrue();
        persistedRun.Reasons.Should().Contain(
            ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);
        readBack.Schedule.IntendedTimeZoneId.Should().Be("Asia/Taipei");
        readBack.Schedule.SchedulerTimeZoneId.Should().Be("Asia/Tokyo");

        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await readDb.ScheduledGovernanceReliabilityRuns.SingleAsync(x =>
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.Id &&
            x.GovernanceRunId == runId);
        row.ReceiptId.Should().Be(receipt.ReceiptId);
        row.IntendedTimeZoneId.Should().Be("Asia/Taipei");
        row.SchedulerTimeZoneId.Should().Be("Asia/Tokyo");
        row.PlatformSignedNaturalOriginAttested.Should().BeFalse();
        row.NaturalOriginStatus.Should().Be("Unattested");
    }

    [DockerRequiredFact]
    public async Task Later_receipt_event_must_update_phase_data_without_relabeling_captured_identity()
    {
        var factory = environment.GetFactory();
        TenantUser actor;
        using (var setupScope = factory.Services.CreateScope())
        {
            var setupDb = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var seed = await setupDb.TenantUsers.AsNoTracking()
                .SingleAsync(x => x.Username == "contract-test-admin");
            actor = await CreateOwnerAsync(setupDb, seed.TenantId, "reliability-identity-owner");
        }

        var runId = $"reliability-identity-{Guid.NewGuid():N}";
        var firstReceipt = CreateReceipt(runId, "Scheduled", ReliabilityWindowStart);
        using (var firstScope = factory.Services.CreateScope())
        {
            SetActor(firstScope.ServiceProvider, actor);
            await firstScope.ServiceProvider
                .GetRequiredService<IScheduledGovernanceReliabilityService>()
                .ObserveAsync(firstReceipt, CancellationToken.None);
        }

        var capturedIdentity = new ScheduledGovernanceRuntimeIdentity(
            "Memory.ScheduledGovernanceGateway",
            "old-deployment",
            ReliabilityWindowStart.AddMinutes(-5),
            "old-derived-identity");
        var capturedBaseline = "old-contract|old-schema|old-catalog|" +
            "Memory.ScheduledGovernanceGateway|old-deployment|old-derived-identity";
        using (var mutateScope = factory.Services.CreateScope())
        {
            var db = mutateScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.ScheduledGovernanceReliabilityRuns.SingleAsync(x =>
                x.TenantId == actor.TenantId &&
                x.OwnerUserId == actor.Id &&
                x.GovernanceRunId == runId);
            var persisted = JsonSerializer.Deserialize<ScheduledGovernanceReliabilityReceiptProjection>(
                row.ProjectionJson,
                JsonOptions)!;
            row.ProjectionJson = JsonSerializer.Serialize(
                persisted with
                {
                    RuntimeIdentity = capturedIdentity,
                    BaselineIdentity = capturedBaseline
                },
                JsonOptions);
            await db.SaveChangesAsync();
        }

        var laterReceipt = firstReceipt with
        {
            ReceiptId = Guid.NewGuid(),
            LatestBatchReceived = true,
            Applied = 1,
            FinalConvergenceStatus = "ExecutionCompleted"
        };
        using (var laterScope = factory.Services.CreateScope())
        {
            SetActor(laterScope.ServiceProvider, actor);
            var observed = await laterScope.ServiceProvider
                .GetRequiredService<IScheduledGovernanceReliabilityService>()
                .ObserveAsync(laterReceipt, CancellationToken.None);

            observed.Runs.Should().ContainSingle(x =>
                x.GovernanceRunId == runId && x.ReceiptId == laterReceipt.ReceiptId);
            var db = laterScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var row = await db.ScheduledGovernanceReliabilityRuns.SingleAsync(x =>
                x.TenantId == actor.TenantId &&
                x.OwnerUserId == actor.Id &&
                x.GovernanceRunId == runId);
            row.ReceiptId.Should().Be(laterReceipt.ReceiptId);
            var persisted = JsonSerializer.Deserialize<ScheduledGovernanceReliabilityReceiptProjection>(
                row.ProjectionJson,
                JsonOptions)!;
            persisted.RuntimeIdentity.Should().Be(capturedIdentity);
            persisted.BaselineIdentity.Should().Be(capturedBaseline);
            persisted.FinalConvergenceStatus.Should().Be("ExecutionCompleted");
        }
    }

    [DockerRequiredFact]
    public async Task Unattested_dedicated_surface_observation_is_persisted_idempotently_and_owner_isolated()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using (var migration = connection.CreateCommand())
        {
            migration.CommandText = "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE name = '034_scheduled_governance_reliability.sql');";
            ((bool)(await migration.ExecuteScalarAsync())!).Should().BeTrue();
        }

        var seed = await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
        var primary = await CreateOwnerAsync(db, seed.TenantId, "reliability-primary-owner");
        var other = await CreateOwnerAsync(db, seed.TenantId, "reliability-other-owner");

        var runId = $"reliability-{Guid.NewGuid():N}";
        var receipt = CreateReceipt(runId, "Review");
        SetActor(scope.ServiceProvider, primary);
        var reliability = scope.ServiceProvider.GetRequiredService<IScheduledGovernanceReliabilityService>();

        var first = await reliability.ObserveAsync(receipt, CancellationToken.None);
        var replayedRead = await reliability.ObserveAsync(receipt, CancellationToken.None);

        first.ConsecutiveQualifyingRuns.Should().Be(0);
        first.GatePassed.Should().BeFalse();
        first.NonQualifyingRuns.Should().ContainSingle(x =>
            x.GovernanceRunId == runId &&
            x.Reasons.Contains(ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason));
        first.NaturalOriginEvidence.PlatformSignedAttestationAvailable.Should().BeFalse();
        replayedRead.Runs.Should().ContainSingle();
        var persisted = await db.ScheduledGovernanceReliabilityRuns.SingleAsync(x =>
            x.TenantId == primary.TenantId && x.OwnerUserId == primary.Id && x.GovernanceRunId == runId);
        using var persistedProjection = JsonDocument.Parse(persisted.ProjectionJson);
        var persistedRuntimeIdentity = persistedProjection.RootElement.GetProperty("runtimeIdentity");
        persistedRuntimeIdentity.GetProperty("buildVersion").GetString().Should()
            .Be(ScheduledGovernanceContract.RuntimeIdentity.BuildVersion);
        persistedRuntimeIdentity.GetProperty("derivedIdentity").GetString().Should()
            .Be(ScheduledGovernanceContract.RuntimeIdentity.DerivedIdentity);

        SetActor(scope.ServiceProvider, other);
        var isolated = await reliability.ObserveAsync(receipt with { ReceiptId = Guid.NewGuid() }, CancellationToken.None);
        isolated.Runs.Should().ContainSingle(x => x.GovernanceRunId == runId);
        (await db.ScheduledGovernanceReliabilityRuns.CountAsync(x => x.GovernanceRunId == runId)).Should().Be(2);
    }

    [DockerRequiredFact]
    public async Task Canonical_receipt_and_exact_replay_should_not_add_a_qualifying_natural_slot_or_count()
    {
        var factory = environment.GetFactory();
        using var setupScope = factory.Services.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var seed = await setupDb.TenantUsers.AsNoTracking()
            .SingleAsync(x => x.Username == "contract-test-admin");
        var actor = await CreateOwnerAsync(setupDb, seed.TenantId, "reliability-canonical-replay-owner");

        var runId = $"reliability-canonical-replay-{Guid.NewGuid():N}";
        var canonicalReceipt = CreateReceipt(runId, "Scheduled", ReliabilityWindowStart);
        var replayReceipt = canonicalReceipt with
        {
            ReceiptId = Guid.NewGuid(),
            IsReplay = true
        };

        using (var observeScope = factory.Services.CreateScope())
        {
            SetActor(observeScope.ServiceProvider, actor);
            var reliability = observeScope.ServiceProvider
                .GetRequiredService<IScheduledGovernanceReliabilityService>();

            var canonical = await reliability.ObserveAsync(canonicalReceipt, CancellationToken.None);
            var replay = await reliability.ObserveAsync(replayReceipt, CancellationToken.None);

            canonical.Runs.Should().ContainSingle(x => x.GovernanceRunId == runId);
            canonical.QualifyingRuns.Should().BeEmpty();
            canonical.ConsecutiveQualifyingRuns.Should().Be(0);
            canonical.GatePassed.Should().BeFalse();

            replay.Runs.Should().ContainSingle(x => x.GovernanceRunId == runId);
            replay.QualifyingRuns.Should().BeEmpty();
            replay.ConsecutiveQualifyingRuns.Should().Be(0);
            replay.GatePassed.Should().BeFalse();
            replay.IgnoredReplayProjectionCount.Should().Be(1);
            replay.Runs.Single().CountedTowardGate.Should().BeTrue();
            replay.Runs.Single().Qualifies.Should().BeFalse();
            replay.NonQualifyingRuns.Should().ContainSingle(x =>
                x.GovernanceRunId == runId &&
                x.Reasons.Contains(
                    ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason));
        }

        using var readScope = factory.Services.CreateScope();
        SetActor(readScope.ServiceProvider, actor);
        var readBack = await readScope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityService>()
            .GetAsync(CancellationToken.None);

        readBack.Runs.Should().ContainSingle(x =>
            x.GovernanceRunId == runId && x.ReceiptId == canonicalReceipt.ReceiptId);
        readBack.QualifyingRuns.Should().BeEmpty();
        readBack.ConsecutiveQualifyingRuns.Should().Be(0);
        readBack.IgnoredReplayProjectionCount.Should().Be(1);
        readBack.Runs.Single().Reasons.Should().Contain(
            ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);

        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await readDb.ScheduledGovernanceReliabilityRuns.SingleAsync(x =>
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.Id &&
            x.GovernanceRunId == runId);
        row.ReceiptId.Should().Be(canonicalReceipt.ReceiptId);
        row.IsReplay.Should().BeFalse();
        row.ReplayProjectionCount.Should().Be(1);
        DeserializeReplayReceiptIds(row.ReplayReceiptIdsJson).Should().Contain(replayReceipt.ReceiptId);
        row.CountedTowardGate.Should().BeTrue();
        row.Qualifies.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Replay_first_then_canonical_should_remain_fail_closed_until_canonical_is_read_back()
    {
        var factory = environment.GetFactory();
        using var setupScope = factory.Services.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var seed = await setupDb.TenantUsers.AsNoTracking()
            .SingleAsync(x => x.Username == "contract-test-admin");
        var actor = await CreateOwnerAsync(setupDb, seed.TenantId, "reliability-replay-first-owner");

        var runId = $"reliability-replay-first-{Guid.NewGuid():N}";
        var replayReceipt = CreateReceipt(runId, "Scheduled", ReliabilityWindowStart) with
        {
            ReceiptId = Guid.NewGuid(),
            IsReplay = true
        };
        var canonicalReceipt = replayReceipt with
        {
            ReceiptId = Guid.NewGuid(),
            IsReplay = false
        };

        using (var observeScope = factory.Services.CreateScope())
        {
            SetActor(observeScope.ServiceProvider, actor);
            var reliability = observeScope.ServiceProvider
                .GetRequiredService<IScheduledGovernanceReliabilityService>();

            var replayFirst = await reliability.ObserveAsync(replayReceipt, CancellationToken.None);

            replayFirst.Runs.Should().BeEmpty();
            replayFirst.QualifyingRuns.Should().BeEmpty();
            replayFirst.NonQualifyingRuns.Should().BeEmpty();
            replayFirst.ConsecutiveQualifyingRuns.Should().Be(0);
            replayFirst.GatePassed.Should().BeFalse();

            var replayRow = await observeScope.ServiceProvider
                .GetRequiredService<MemoryDbContext>()
                .ScheduledGovernanceReliabilityRuns
                .SingleAsync(x =>
                    x.TenantId == actor.TenantId &&
                    x.OwnerUserId == actor.Id &&
                    x.GovernanceRunId == runId);
            replayRow.IsReplay.Should().BeTrue();
            replayRow.ReplayProjectionCount.Should().Be(1);
            DeserializeReplayReceiptIds(replayRow.ReplayReceiptIdsJson)
                .Should().Contain(replayReceipt.ReceiptId);

            var afterCanonical = await reliability.ObserveAsync(canonicalReceipt, CancellationToken.None);

            afterCanonical.Runs.Should().ContainSingle(x =>
                x.GovernanceRunId == runId &&
                x.ReceiptId == canonicalReceipt.ReceiptId);
            afterCanonical.QualifyingRuns.Should().BeEmpty();
            afterCanonical.ConsecutiveQualifyingRuns.Should().Be(0);
            afterCanonical.GatePassed.Should().BeFalse();
            afterCanonical.IgnoredReplayProjectionCount.Should().Be(1);
            afterCanonical.Runs.Single().Reasons.Should().Contain(
                ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason);
        }

        using var readScope = factory.Services.CreateScope();
        SetActor(readScope.ServiceProvider, actor);
        var readBack = await readScope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityService>()
            .GetAsync(CancellationToken.None);

        readBack.Runs.Should().ContainSingle(x =>
            x.GovernanceRunId == runId && x.ReceiptId == canonicalReceipt.ReceiptId);
        readBack.QualifyingRuns.Should().BeEmpty();
        readBack.ConsecutiveQualifyingRuns.Should().Be(0);
        readBack.IgnoredReplayProjectionCount.Should().Be(1);

        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var row = await readDb.ScheduledGovernanceReliabilityRuns.SingleAsync(x =>
            x.TenantId == actor.TenantId &&
            x.OwnerUserId == actor.Id &&
            x.GovernanceRunId == runId);
        row.IsReplay.Should().BeFalse();
        row.ReceiptId.Should().Be(canonicalReceipt.ReceiptId);
        row.ReplayProjectionCount.Should().Be(1);
        DeserializeReplayReceiptIds(row.ReplayReceiptIdsJson).Should().Contain(replayReceipt.ReceiptId);
        row.CountedTowardGate.Should().BeTrue();
        row.Qualifies.Should().BeFalse();
    }

    [DockerRequiredFact]
    public async Task Six_persisted_scheduled_slots_should_fail_closed_when_the_public_surface_cannot_attest_natural_origin()
    {
        var factory = environment.GetFactory();
        using var setupScope = factory.Services.CreateScope();
        var setupDb = setupScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var seed = await setupDb.TenantUsers.AsNoTracking()
            .SingleAsync(x => x.Username == "contract-test-admin");
        var actor = await CreateOwnerAsync(setupDb, seed.TenantId, "reliability-six-slot-owner");

        var receipts = Enumerable.Range(0, 6)
            .Select(index => CreateReceipt(
                $"reliability-six-slot-{Guid.NewGuid():N}",
                "Scheduled",
                ReliabilityWindowStart.AddHours(index * 4)))
            .ToArray();

        using (var observeScope = factory.Services.CreateScope())
        {
            SetActor(observeScope.ServiceProvider, actor);
            var reliability = observeScope.ServiceProvider
                .GetRequiredService<IScheduledGovernanceReliabilityService>();
            foreach (var receipt in receipts)
            {
                var observed = await reliability.ObserveAsync(receipt, CancellationToken.None);
                observed.Runs.Should().Contain(x => x.GovernanceRunId == receipt.GovernanceRunId);
            }
        }

        using var readScope = factory.Services.CreateScope();
        SetActor(readScope.ServiceProvider, actor);
        var reliabilityReadBack = readScope.ServiceProvider
            .GetRequiredService<IScheduledGovernanceReliabilityService>();
        var readBack = await reliabilityReadBack.GetAsync(CancellationToken.None);

        readBack.Runs.Should().HaveCount(6);
        readBack.QualifyingRuns.Should().BeEmpty();
        readBack.NonQualifyingRuns.Should().HaveCount(6);
        readBack.ConsecutiveQualifyingRuns.Should().Be(0);
        readBack.GatePassed.Should().BeFalse();
        readBack.NaturalOriginEvidence.PlatformSignedAttestationAvailable.Should().BeFalse();
        readBack.NaturalOriginEvidence.Status.Should().Be("Unattested");
        readBack.NonQualifyingRuns.Should().OnlyContain(x =>
            x.Reasons.Contains(
                ScheduledGovernanceReliabilityService.NaturalOriginAttestationNotProvenReason));

        var readDb = readScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var rows = await readDb.ScheduledGovernanceReliabilityRuns.AsNoTracking()
            .Where(x => x.TenantId == actor.TenantId && x.OwnerUserId == actor.Id)
            .OrderBy(x => x.ObservedAtUtc)
            .ToArrayAsync();
        rows.Should().HaveCount(6);
        rows.Should().OnlyContain(x => !x.IsReplay && x.CountedTowardGate && !x.Qualifies);

        var persistedProjections = rows
            .Select(x => JsonSerializer.Deserialize<ScheduledGovernanceReliabilityReceiptProjection>(
                x.ProjectionJson,
                JsonOptions))
            .ToArray();
        persistedProjections.All(x => x is not null).Should().BeTrue();
        var serverCalculatorReadBack = new ScheduledGovernanceReliabilityWindowCalculator()
            .Calculate(persistedProjections!);
        serverCalculatorReadBack.Runs.Should().HaveCount(6);
        serverCalculatorReadBack.QualifyingRuns.Should().BeEmpty();
        serverCalculatorReadBack.ConsecutiveQualifyingRuns.Should().Be(0);
        serverCalculatorReadBack.ResetEvents.Should().HaveCount(6);
    }

    private static void SetActor(IServiceProvider services, TenantUser user)
        => services.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId,
            user.Id,
            user.Username,
            user.Role,
            [SecurityScopes.ScheduledGovernance],
            [],
            IsAuthenticated: true);

    private static async Task<TenantUser> CreateOwnerAsync(
        MemoryDbContext db,
        Guid tenantId,
        string prefix)
    {
        var now = DateTimeOffset.UtcNow;
        var owner = new TenantUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantId,
            Username = $"{prefix}-{Guid.NewGuid():N}",
            DisplayName = "Reliability persistence owner",
            Role = TenantUserRole.Admin,
            Status = TenantUserStatus.Active,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.TenantUsers.Add(owner);
        await db.SaveChangesAsync();
        return owner;
    }

    private static readonly DateTimeOffset ReliabilityWindowStart =
        new(2026, 9, 1, 0, 0, 0, TimeSpan.Zero);

    private static GovernanceRunReceiptResult CreateReceipt(
        string runId,
        string executionMode,
        DateTimeOffset? startedAt = null)
    {
        var started = startedAt ?? ReliabilityWindowStart;
        return new GovernanceRunReceiptResult(
            Guid.NewGuid(),
            runId,
            "scheduled-governance",
            executionMode,
            started,
            started.AddMinutes(1),
            ScheduledGovernanceContract.ToolContractVersion,
            ScheduledGovernanceContract.SchemaHash,
            ScheduledGovernanceContract.PublishedCatalogVersion,
            "snapshot-initial",
            "snapshot-final",
            true,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            "NoOpConverged",
            "ReviewCompleted",
            [],
            [],
            false,
            true,
            "Completed",
            false,
            string.Empty,
            null);
    }

    private static IReadOnlyList<Guid> DeserializeReplayReceiptIds(string value)
        => JsonSerializer.Deserialize<List<Guid>>(value) ?? [];
}

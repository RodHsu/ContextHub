using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class TelemetryRetentionPolicyTests
{
    [Fact]
    public void Create_Should_Use_Low_Impact_Defaults()
    {
        var policy = RetrievalTelemetryRetentionPolicy.Create(new TelemetryRetentionOptions(), new RetrievalTelemetryRetentionRunRequest());

        policy.HitsRetentionDays.Should().Be(3);
        policy.EventsRetentionDays.Should().Be(30);
        policy.SummaryRetentionDays.Should().Be(30);
        policy.SecurityAuditRetentionDays.Should().Be(180);
        policy.RuntimeLogRetentionDays.Should().Be(30);
        policy.MaintenanceRunRetentionDays.Should().Be(180);
        policy.HitSummaryTopPerBucket.Should().Be(100);
        policy.BatchSize.Should().Be(5_000);
        policy.EventBatchSize.Should().Be(1_000);
        policy.TimeWindowDays.Should().Be(3);
        policy.DelayBetweenBatchesMs.Should().Be(250);
        policy.CommandTimeoutSeconds.Should().Be(300);
        policy.MaxDuration.Should().Be(TimeSpan.FromMinutes(15));
        policy.RunVacuumAnalyzeAfterRetention.Should().BeTrue();
        policy.RunVacuumFullAutomatically.Should().BeFalse();
    }

    [Fact]
    public void Create_Should_Apply_Manual_Overrides()
    {
        var request = new RetrievalTelemetryRetentionRunRequest(
            TriggeredBy: "manual-test",
            BatchSize: 2,
            EventBatchSize: 3,
            TimeWindowDays: 1,
            DelayBetweenBatchesMs: 0,
            CommandTimeoutSeconds: 45,
            MaxDurationMinutes: 30,
            RunVacuumAnalyzeAfterRetention: false,
            RunVacuumFullAutomatically: true);

        var policy = RetrievalTelemetryRetentionPolicy.Create(new TelemetryRetentionOptions(), request);

        policy.BatchSize.Should().Be(2);
        policy.EventBatchSize.Should().Be(3);
        policy.TimeWindowDays.Should().Be(1);
        policy.DelayBetweenBatchesMs.Should().Be(0);
        policy.CommandTimeoutSeconds.Should().Be(45);
        policy.MaxDuration.Should().Be(TimeSpan.FromMinutes(30));
        policy.RunVacuumAnalyzeAfterRetention.Should().BeFalse();
        policy.RunVacuumFullAutomatically.Should().BeTrue();
    }

    [Fact]
    public void Create_Should_Use_Configured_Automatic_Vacuum_Full()
    {
        var options = new TelemetryRetentionOptions
        {
            RunVacuumFullAutomatically = true
        };

        var policy = RetrievalTelemetryRetentionPolicy.Create(options, new RetrievalTelemetryRetentionRunRequest());

        policy.RunVacuumFullAutomatically.Should().BeTrue();
    }

    [Fact]
    public void Create_Should_Normalize_Unsafe_Overrides()
    {
        var options = new TelemetryRetentionOptions
        {
            HitsRetentionDays = 0,
            EventsRetentionDays = -10,
            SummaryRetentionDays = 0,
            SecurityAuditRetentionDays = 0,
            RuntimeLogRetentionDays = 0,
            MaintenanceRunRetentionDays = 0,
            HitSummaryTopPerBucket = 5_000,
            BatchSize = 0,
            EventBatchSize = 250_000,
            TimeWindowDays = 0,
            DelayBetweenBatchesMs = -1,
            CommandTimeoutSeconds = 0,
            MaxDurationMinutes = 0
        };
        var request = new RetrievalTelemetryRetentionRunRequest(
            BatchSize: 250_000,
            EventBatchSize: 0,
            TimeWindowDays: 20,
            DelayBetweenBatchesMs: 120_000,
            CommandTimeoutSeconds: 10_000,
            MaxDurationMinutes: 10_000);

        var policy = RetrievalTelemetryRetentionPolicy.Create(options, request);

        policy.HitsRetentionDays.Should().Be(1);
        policy.EventsRetentionDays.Should().Be(1);
        policy.SummaryRetentionDays.Should().Be(1);
        policy.SecurityAuditRetentionDays.Should().Be(1);
        policy.RuntimeLogRetentionDays.Should().Be(1);
        policy.MaintenanceRunRetentionDays.Should().Be(1);
        policy.HitSummaryTopPerBucket.Should().Be(1_000);
        policy.BatchSize.Should().Be(100_000);
        policy.EventBatchSize.Should().Be(1);
        policy.TimeWindowDays.Should().Be(3);
        policy.DelayBetweenBatchesMs.Should().Be(60_000);
        policy.CommandTimeoutSeconds.Should().Be(3600);
        policy.MaxDuration.Should().Be(TimeSpan.FromMinutes(30));
    }
}

public sealed class MemoryDataRetentionPolicyTests
{
    [Fact]
    public void Create_Should_Use_Conservative_Defaults()
    {
        var policy = MemoryDataRetentionPolicy.Create(new MemoryDataRetentionOptions(), new MemoryDataRetentionRunRequest());

        policy.ArchivedItemsRetentionDays.Should().Be(365);
        policy.HitWindowDays.Should().Be(180);
        policy.MaxRecentHitCount.Should().Be(0);
        policy.MaxLinkDegree.Should().Be(0);
        policy.MaxImportance.Should().Be(0.55m);
        policy.MaxConfidence.Should().Be(0.70m);
        policy.PreviewLimit.Should().Be(50);
        policy.AutoApplyEnabled.Should().BeFalse();
        policy.BatchSize.Should().Be(1_000);
        policy.DelayBetweenBatchesMs.Should().Be(150);
        policy.CommandTimeoutSeconds.Should().Be(300);
        policy.MaxDuration.Should().Be(TimeSpan.FromMinutes(20));
    }

    [Fact]
    public void Create_Should_Apply_Manual_Overrides()
    {
        var request = new MemoryDataRetentionRunRequest(
            TriggeredBy: "manual-test",
            ArchivedItemsRetentionDays: 30,
            HitWindowDays: 14,
            MaxRecentHitCount: 1,
            MaxLinkDegree: 2,
            MaxImportance: 0.25m,
            MaxConfidence: 0.35m,
            PreviewLimit: 20,
            BatchSize: 5,
            DelayBetweenBatchesMs: 0,
            CommandTimeoutSeconds: 45,
            MaxDurationMinutes: 10,
            PreviewOnly: true);

        var policy = MemoryDataRetentionPolicy.Create(new MemoryDataRetentionOptions(), request);

        policy.ArchivedItemsRetentionDays.Should().Be(30);
        policy.HitWindowDays.Should().Be(14);
        policy.MaxRecentHitCount.Should().Be(1);
        policy.MaxLinkDegree.Should().Be(2);
        policy.MaxImportance.Should().Be(0.25m);
        policy.MaxConfidence.Should().Be(0.35m);
        policy.PreviewLimit.Should().Be(20);
        policy.BatchSize.Should().Be(5);
        policy.DelayBetweenBatchesMs.Should().Be(0);
        policy.CommandTimeoutSeconds.Should().Be(45);
        policy.MaxDuration.Should().Be(TimeSpan.FromMinutes(10));
    }

    [Fact]
    public void Create_Should_Normalize_Unsafe_Overrides()
    {
        var options = new MemoryDataRetentionOptions
        {
            ArchivedItemsRetentionDays = 0,
            HitWindowDays = 0,
            MaxRecentHitCount = -1,
            MaxLinkDegree = -1,
            MaxImportance = -1m,
            MaxConfidence = 2m,
            PreviewLimit = 0,
            BatchSize = 0,
            DelayBetweenBatchesMs = -1,
            CommandTimeoutSeconds = 0,
            MaxDurationMinutes = 0
        };
        var request = new MemoryDataRetentionRunRequest(
            ArchivedItemsRetentionDays: 0,
            HitWindowDays: 0,
            MaxRecentHitCount: -2,
            MaxLinkDegree: -3,
            MaxImportance: 2m,
            MaxConfidence: -1m,
            PreviewLimit: 2_000,
            BatchSize: 250_000,
            DelayBetweenBatchesMs: 120_000,
            CommandTimeoutSeconds: 10_000,
            MaxDurationMinutes: 10_000);

        var policy = MemoryDataRetentionPolicy.Create(options, request);

        policy.ArchivedItemsRetentionDays.Should().Be(1);
        policy.HitWindowDays.Should().Be(1);
        policy.MaxRecentHitCount.Should().Be(0);
        policy.MaxLinkDegree.Should().Be(0);
        policy.MaxImportance.Should().Be(1m);
        policy.MaxConfidence.Should().Be(0m);
        policy.PreviewLimit.Should().Be(500);
        policy.BatchSize.Should().Be(100_000);
        policy.DelayBetweenBatchesMs.Should().Be(60_000);
        policy.CommandTimeoutSeconds.Should().Be(3600);
        policy.MaxDuration.Should().Be(TimeSpan.FromMinutes(30));
    }
}

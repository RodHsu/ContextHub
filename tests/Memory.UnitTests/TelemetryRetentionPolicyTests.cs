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

        policy.HitsRetentionDays.Should().Be(15);
        policy.EventsRetentionDays.Should().Be(30);
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
            RunVacuumAnalyzeAfterRetention: false);

        var policy = RetrievalTelemetryRetentionPolicy.Create(new TelemetryRetentionOptions(), request);

        policy.BatchSize.Should().Be(2);
        policy.EventBatchSize.Should().Be(3);
        policy.TimeWindowDays.Should().Be(1);
        policy.DelayBetweenBatchesMs.Should().Be(0);
        policy.CommandTimeoutSeconds.Should().Be(45);
        policy.MaxDuration.Should().Be(TimeSpan.FromMinutes(30));
        policy.RunVacuumAnalyzeAfterRetention.Should().BeFalse();
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
        policy.BatchSize.Should().Be(100_000);
        policy.EventBatchSize.Should().Be(1);
        policy.TimeWindowDays.Should().Be(3);
        policy.DelayBetweenBatchesMs.Should().Be(60_000);
        policy.CommandTimeoutSeconds.Should().Be(3600);
        policy.MaxDuration.Should().Be(TimeSpan.FromMinutes(30));
    }
}

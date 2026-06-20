using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class DashboardContextSavingsAggregatorTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public void BuildContextSavings_Should_Calculate_Windows_From_Aggregated_Token_Totals()
    {
        var now = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero);

        var result = DashboardSnapshotCollectorHostedService.BuildContextSavings(
            now,
            now.AddDays(-7),
            [
                CreateEvent(now.AddHours(-2), baseline: 100, returned: 0, coveragePercent: 100d, cacheHit: true),
                CreateEvent(now.AddHours(-1), baseline: 1_000, returned: 800, coveragePercent: 80d, cacheHit: false),
                CreateEvent(now.AddDays(-2), baseline: 300, returned: 150, coveragePercent: 60d, cacheHit: true),
                CreateEvent(now.AddDays(-5), baseline: 700, returned: 350, coveragePercent: 40d, cacheHit: false)
            ]);

        result.WindowLabel.Should().Be("24H");
        result.Windows.Should().NotBeNull();
        result.Windows!.Select(x => x.Key).Should().Equal("24h", "3d", "7d");

        var twentyFourHours = result.Windows.Single(x => x.Key == "24h");
        twentyFourHours.SampleCount.Should().Be(2);
        twentyFourHours.BaselineTokenEstimate.Should().Be(1_100);
        twentyFourHours.ReturnedTokenEstimate.Should().Be(800);
        twentyFourHours.EstimatedSavedTokens.Should().Be(300);
        twentyFourHours.SavingPercent.Should().Be(27.27d);
        twentyFourHours.CacheHitPercent.Should().Be(50d);

        var threeDays = result.Windows.Single(x => x.Key == "3d");
        threeDays.SampleCount.Should().Be(3);
        threeDays.EstimatedSavedTokens.Should().Be(450);

        var sevenDays = result.Windows.Single(x => x.Key == "7d");
        sevenDays.SampleCount.Should().Be(4);
        sevenDays.EstimatedSavedTokens.Should().Be(800);
        sevenDays.SavingPercent.Should().Be(38.1d);
    }

    private static DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent CreateEvent(
        DateTimeOffset createdAt,
        int baseline,
        int returned,
        double coveragePercent,
        bool cacheHit)
    {
        var saved = Math.Max(0, baseline - returned);
        var savingPercent = baseline > 0
            ? Math.Round(saved / (double)baseline * 100d, 2)
            : 0d;
        var metadataJson = JsonSerializer.Serialize(
            new
            {
                savings = new ContextSavingsEstimateResult(
                    baseline,
                    returned,
                    saved,
                    savingPercent,
                    ContextSavingsEstimator.HighConfidence,
                    coveragePercent)
            },
            JsonOptions);
        return new DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent(createdAt, cacheHit, metadataJson);
    }
}

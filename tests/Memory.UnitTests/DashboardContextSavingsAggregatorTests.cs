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
            now.AddDays(-30),
            [
                CreateEvent(now.AddHours(-2), baseline: 100, returned: 0, coveragePercent: 100d, cacheHit: true),
                CreateEvent(now.AddHours(-1), baseline: 100, returned: 200, coveragePercent: 80d, cacheHit: false),
                CreateEvent(now.AddDays(-2), baseline: 300, returned: 150, coveragePercent: 60d, cacheHit: true),
                CreateEvent(now.AddDays(-5), baseline: 700, returned: 350, coveragePercent: 40d, cacheHit: false),
                CreateEvent(now.AddDays(-20), baseline: 600, returned: 100, coveragePercent: 100d, cacheHit: true),
                CreateEvent(now.AddDays(-45), baseline: 10_000, returned: 0, coveragePercent: 100d, cacheHit: true)
            ]);

        result.WindowLabel.Should().Be("24H");
        result.Windows.Should().NotBeNull();
        result.Windows!.Select(x => x.Key).Should().Equal("24h", "3d", "7d", "30d");

        var twentyFourHours = result.Windows.Single(x => x.Key == "24h");
        twentyFourHours.SampleCount.Should().Be(2);
        twentyFourHours.BaselineTokenEstimate.Should().Be(200);
        twentyFourHours.ReturnedTokenEstimate.Should().Be(200);
        twentyFourHours.EstimatedSavedTokens.Should().Be(100);
        twentyFourHours.SavingPercent.Should().Be(50d);
        twentyFourHours.CacheHitPercent.Should().Be(50d);

        var threeDays = result.Windows.Single(x => x.Key == "3d");
        threeDays.SampleCount.Should().Be(3);
        threeDays.EstimatedSavedTokens.Should().Be(250);

        var sevenDays = result.Windows.Single(x => x.Key == "7d");
        sevenDays.SampleCount.Should().Be(4);
        sevenDays.EstimatedSavedTokens.Should().Be(600);
        sevenDays.SavingPercent.Should().Be(50d);

        var thirtyDays = result.Windows.Single(x => x.Key == "30d");
        thirtyDays.SampleCount.Should().Be(5);
        thirtyDays.EstimatedSavedTokens.Should().Be(1_100);
        thirtyDays.SavingPercent.Should().Be(61.11d);
        thirtyDays.ExactCoveragePercent.Should().Be(0d);
        thirtyDays.TokenCountingMode.Should().Be(TokenCountingModes.Approximate);
    }

    [Fact]
    public void BuildContextSavings_Should_Aggregate_Exact_Coverage_And_Mode()
    {
        var now = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero);

        var result = DashboardSnapshotCollectorHostedService.BuildContextSavings(
            now,
            now.AddDays(-30),
            [
                CreateEvent(
                    now.AddHours(-2),
                    baseline: 100,
                    returned: 20,
                    coveragePercent: 100d,
                    cacheHit: true,
                    exactCoveragePercent: 100d,
                    tokenCountingMode: TokenCountingModes.Exact),
                CreateEvent(
                    now.AddHours(-1),
                    baseline: 100,
                    returned: 40,
                    coveragePercent: 100d,
                    cacheHit: false,
                    exactCoveragePercent: 60d,
                    tokenCountingMode: TokenCountingModes.Mixed)
            ]);

        var twentyFourHours = result.Windows!.Single(x => x.Key == "24h");
        twentyFourHours.ExactCoveragePercent.Should().Be(80d);
        twentyFourHours.TokenCountingMode.Should().Be(TokenCountingModes.Mixed);
    }

    private static DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent CreateEvent(
        DateTimeOffset createdAt,
        int baseline,
        int returned,
        double coveragePercent,
        bool cacheHit,
        double exactCoveragePercent = 0d,
        string tokenCountingMode = TokenCountingModes.Approximate)
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
                    coveragePercent,
                    baseline,
                    returned,
                    saved,
                    exactCoveragePercent > 0d ? baseline : null,
                    exactCoveragePercent > 0d ? returned : null,
                    exactCoveragePercent > 0d ? saved : null,
                    exactCoveragePercent,
                    tokenCountingMode)
            },
            JsonOptions);
        return new DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent(createdAt, cacheHit, metadataJson);
    }
}

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
            ],
            new DashboardSnapshotCollectorHostedService.McpToolCallWindowCounts(12, 34, 56, 78));

        result.WindowLabel.Should().Be("24H");
        result.Windows.Should().NotBeNull();
        result.Windows!.Select(x => x.Key).Should().Equal("24h", "3d", "7d", "30d");

        var twentyFourHours = result.Windows.Single(x => x.Key == "24h");
        twentyFourHours.SampleCount.Should().Be(2);
        twentyFourHours.ActualToolCallCount.Should().Be(12);
        twentyFourHours.BaselineTokenEstimate.Should().Be(200);
        twentyFourHours.ReturnedTokenEstimate.Should().Be(200);
        twentyFourHours.EstimatedSavedTokens.Should().Be(0);
        twentyFourHours.PositiveSavedTokens.Should().Be(100);
        twentyFourHours.IncreasedTokens.Should().Be(100);
        twentyFourHours.SavingPercent.Should().Be(0d);
        twentyFourHours.CacheHitPercent.Should().Be(50d);

        var threeDays = result.Windows.Single(x => x.Key == "3d");
        threeDays.SampleCount.Should().Be(3);
        threeDays.ActualToolCallCount.Should().Be(34);
        threeDays.EstimatedSavedTokens.Should().Be(150);

        var sevenDays = result.Windows.Single(x => x.Key == "7d");
        sevenDays.SampleCount.Should().Be(4);
        sevenDays.ActualToolCallCount.Should().Be(56);
        sevenDays.EstimatedSavedTokens.Should().Be(500);
        sevenDays.SavingPercent.Should().Be(41.67d);

        var thirtyDays = result.Windows.Single(x => x.Key == "30d");
        thirtyDays.SampleCount.Should().Be(5);
        thirtyDays.ActualToolCallCount.Should().Be(78);
        thirtyDays.EstimatedSavedTokens.Should().Be(1_000);
        thirtyDays.SavingPercent.Should().Be(55.56d);
        thirtyDays.ExactCoveragePercent.Should().Be(0d);
        thirtyDays.TokenCountingMode.Should().Be(TokenCountingModes.Approximate);
    }

    [Fact]
    public void BuildContextSavings_Should_Keep_Actual_Call_Counts_When_No_Savings_Samples_Exist()
    {
        var now = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero);

        var result = DashboardSnapshotCollectorHostedService.BuildContextSavings(
            now,
            now.AddDays(-30),
            [],
            new DashboardSnapshotCollectorHostedService.McpToolCallWindowCounts(7, 15, 31, 120));

        result.HasData.Should().BeFalse();
        result.ActualToolCallCount.Should().Be(7);
        result.Windows!.Select(x => x.ActualToolCallCount).Should().Equal(7, 15, 31, 120);
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

    [Fact]
    public void BuildContextSavings_Should_Keep_Inclusive_End_In_The_Last_Of_24_Trend_Buckets()
    {
        var now = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero);
        var events = Enumerable.Range(0, 25)
            .Select(hour => CreateEvent(now.AddHours(hour - 24), baseline: 100, returned: 200,
                coveragePercent: 100d, cacheHit: false))
            .Reverse()
            .ToArray();

        var result = DashboardSnapshotCollectorHostedService.BuildContextSavings(now, now.AddDays(-30), events);

        result.SampleCount.Should().Be(25);
        result.BaselineTokenEstimate.Should().Be(2_500);
        result.ReturnedTokenEstimate.Should().Be(5_000);
        result.EstimatedSavedTokens.Should().Be(-2_500);
        result.AverageSavingPercent.Should().Be(-100d);
        var window = result.Windows!.Single(x => x.Key == "24h");
        window.PositiveSavedTokens.Should().Be(0L);
        window.IncreasedTokens.Should().Be(2_500L);
        result.Trend.Should().HaveCount(24);
        result.Trend.Sum(x => x.BaselineTokenEstimate).Should().Be(result.BaselineTokenEstimate);
        result.Trend.Sum(x => x.ReturnedTokenEstimate).Should().Be(result.ReturnedTokenEstimate);
        result.Trend.Sum(x => x.EstimatedSavedTokens).Should().Be(result.EstimatedSavedTokens);
        result.Trend.Select(x => x.TimestampUtc).Should().BeInAscendingOrder();
        var last = result.Trend[^1];
        last.TimestampUtc.Should().Be(now);
        last.BaselineTokenEstimate.Should().Be(200);
        last.ReturnedTokenEstimate.Should().Be(400);
        last.EstimatedSavedTokens.Should().Be(-200);
        last.SavingPercent.Should().Be(-100d);
    }

    [Fact]
    public void BuildContextSavings_Should_Keep_A_Sole_End_Sample_Without_Padding_Empty_Trend_Buckets()
    {
        var now = new DateTimeOffset(2026, 6, 20, 8, 0, 0, TimeSpan.Zero);

        var result = DashboardSnapshotCollectorHostedService.BuildContextSavings(
            now, now.AddDays(-30), [CreateEvent(now, baseline: 100, returned: 200, coveragePercent: 100d, cacheHit: false)]);

        result.SampleCount.Should().Be(1);
        var point = result.Trend.Should().ContainSingle().Which;
        point.TimestampUtc.Should().Be(now);
        point.BaselineTokenEstimate.Should().Be(100);
        point.ReturnedTokenEstimate.Should().Be(200);
        point.EstimatedSavedTokens.Should().Be(-100);
        point.SavingPercent.Should().Be(-100d);
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

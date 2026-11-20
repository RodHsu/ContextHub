using System.Reflection;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Components.Pages;

namespace Memory.DashboardTests;

// Calls the real Monitoring component's pure presentation helpers; NOT a browser,
// render-tree, API, authentication or runtime-isolation acceptance test. NotExecuted.
public sealed class TokenSavingsMonitoringSourceTests
{
    [Theory]
    [InlineData(100, 20, "淨節省", "80")]
    [InlineData(100, 200, "淨增加", "100")]
    public void Main_label_uses_original_B_R_even_when_old_saved_field_is_clamped(int b, int r, string label, string amount)
    {
        var row = Window(b, r);
        Text("NetSavingsText", row).Should().Contain(label).And.Contain(amount);
        Text("SavingsPartText", row, true).Should().Contain("未知");
        Text("SavingsDataText", row).Should().Contain("舊快照");
    }

    [Fact]
    public void Zero_baseline_and_no_samples_do_not_look_like_zero_percent_savings()
    {
        Text("NetSavingsPercentText", Window(0, 30)).Should().Contain("不適用");
        var empty = Window(0, 0) with { HasData = false, SampleCount = 0, UnusableSampleCount = 0 };
        Text("NetSavingsText", empty).Should().Contain("無可用樣本");
        Text("NetSavingsPercentText", empty).Should().Be("未知");
        Text("NetSavingsText", empty with { UnusableSampleCount = 2 }).Should().Contain("未知");
    }

    [Fact]
    public void New_parts_require_version_complete_nonnegative_matching_evidence()
    {
        var row = Window(200, 200) with { PositiveSavedTokens = 100, IncreasedTokens = 100,
            SavingsCalculationVersion = "net-br-v1", UnusableSampleCount = 0 };
        Text("SavingsPartText", row, true).Should().Be("100 tokens");
        Text("SavingsPartText", row, false).Should().Be("100 tokens");
        Text("SavingsPartText", row with { IncreasedTokens = null }, true).Should().Contain("未知");
        Text("SavingsPartText", row with { IncreasedTokens = 99 }, true).Should().Contain("未知");
    }

    [Fact]
    public void Signed_chart_retains_negative_points_and_breaks_at_zero_denominator()
    {
        var now = DateTimeOffset.UtcNow;
        var points = new[] { Point(now, 100, 0), Point(now, 0, 30), Point(now, 100, 200) };
        var segments = Call<IReadOnlyList<string>>("BuildSavingsLineSegments", (object)points);
        segments.Should().HaveCount(2);
        segments[0].Should().NotBe(segments[1]);
    }

    [Fact]
    public void Sole_valid_point_retains_a_coordinate_for_the_pages_circle_marker()
    {
        var segments = Call<IReadOnlyList<string>>("BuildSavingsLineSegments", (object)new[] { Point(DateTimeOffset.UtcNow, 100, 200) });
        segments.Should().ContainSingle();
        segments[0].Split(' ').Should().ContainSingle();
        segments[0].Split(',').Should().HaveCount(2);
        // Static source review must also verify that the actual Razor circle consumes
        // each coordinate. This pure-helper case is not a render/browser acceptance.
    }

    [Fact]
    public void All_invalid_events_are_not_described_as_waiting_for_telemetry()
    {
        var window = Window(0, 0) with { HasData = false, SampleCount = 0, UnusableSampleCount = 1 };
        var now = DateTimeOffset.UtcNow;
        var result = new DashboardContextSavingsResult(false, 0, 0, 0, 0, 0, "Low", 0, 0,
            now.AddHours(-24), now, [], Windows: [window]);
        Text("ContextSavingsStateText", result).Should().Contain("已收到事件").And.Contain("未知");
    }

    private static DashboardContextSavingsTrendPointResult Point(DateTimeOffset at, int b, int r)
        => new(at, b, r, Math.Max(0, b - r), 99);
    private static DashboardContextSavingsWindowResult Window(int b, int r)
        => new("24h", "24H", true, 1, b, r, Math.Max(0, b - r), 99, "High", 100, 0,
            DateTimeOffset.UtcNow.AddHours(-24), DateTimeOffset.UtcNow, DateTimeOffset.UtcNow);
    private static string Text(string method, params object[] args) => Call<string>(method, args);
    private static T Call<T>(string method, params object[] args)
        => (T)typeof(Monitoring).GetMethod(method, BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, args)!;
}

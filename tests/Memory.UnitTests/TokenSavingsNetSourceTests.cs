using System.Text.Json;
using System.Text.Json.Nodes;
using System.Reflection;
using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;

namespace Memory.UnitTests;

// Source preparation only. These tests have NOT been executed.
public sealed class TokenSavingsNetSourceTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 2, 2, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(100, 20, 80, 80, 0)]
    [InlineData(100, 200, -100, 0, 100)]
    [InlineData(0, 30, -30, 0, 30)]
    [InlineData(0, 0, 0, 0, 0)]
    public void Actual_collector_recomputes_signed_net_from_original_pair_not_clamped_saved(
        int baseline, int returned, int net, long positive, long increased)
    {
        var source = Event(baseline, returned);
        var original = source.MetadataJson;
        var result = Build(source);
        var window = result.Windows!.Single(x => x.Key == "24h");
        window.EstimatedSavedTokens.Should().Be(net);
        window.PositiveSavedTokens.Should().Be(positive);
        window.IncreasedTokens.Should().Be(increased);
        window.SavingPercent.Should().Be(baseline == 0 ? 0d : Math.Round(net / (double)baseline * 100d, 2));
        window.UnusableSampleCount.Should().Be(0);
        window.SavingsCalculationVersion.Should().Be("net-br-v1");
        result.Trend.Should().ContainSingle().Which.EstimatedSavedTokens.Should().Be(net);
        source.MetadataJson.Should().Be(original);
    }

    [Fact]
    public void Two_distinct_pairs_cancel_net_without_erasing_positive_and_increase()
    {
        // These are TWO samples: (100,0) and (100,200), not one (200,200).
        var result = Build(Event(100, 0), Event(100, 200));
        var w = result.Windows!.Single(x => x.Key == "24h");
        w.SampleCount.Should().Be(2);
        w.BaselineTokenEstimate.Should().Be(200);
        w.ReturnedTokenEstimate.Should().Be(200);
        w.EstimatedSavedTokens.Should().Be(0);
        w.PositiveSavedTokens.Should().Be(100);
        w.IncreasedTokens.Should().Be(100);
        w.SavingPercent.Should().Be(0);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"savings\":{}}")]
    [InlineData("{\"savings\":{\"baselineTokenEstimate\":10}}")]
    [InlineData("{\"savings\":{\"baselineTokenEstimate\":-1,\"returnedTokenEstimate\":3}}")]
    [InlineData("{\"savings\":{\"baselineTokenEstimate\":1,\"returnedTokenEstimate\":-3}}")]
    [InlineData("{\"savings\":{\"baselineTokenEstimate\":1,\"BaselineTokenEstimate\":2,\"returnedTokenEstimate\":3}}")]
    [InlineData("broken json")]
    public void Missing_invalid_or_ambiguous_pairs_are_unknown_not_zero_samples(string metadata)
    {
        var result = Build(new DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent(Now.AddMinutes(-1), false, metadata));
        var w = result.Windows!.Single(x => x.Key == "24h");
        w.HasData.Should().BeFalse();
        w.SampleCount.Should().Be(0);
        w.UnusableSampleCount.Should().Be(1);
        w.ActualToolCallCount.Should().Be(9);
        result.Trend.Should().BeEmpty();
    }

    [Fact]
    public void No_samples_is_distinct_from_unusable_samples_and_tool_invocations()
    {
        var w = Build().Windows!.Single(x => x.Key == "24h");
        w.HasData.Should().BeFalse();
        w.UnusableSampleCount.Should().Be(0);
        w.SampleCount.Should().Be(0);
        w.ActualToolCallCount.Should().Be(9);
    }

    [Fact]
    public void Mixed_validity_uses_only_complete_pairs_for_both_numerator_and_denominator()
    {
        var result = Build(Event(100, 200), new(Now.AddMinutes(-1), false, "{\"savings\":{}}"));
        var w = result.Windows!.Single(x => x.Key == "24h");
        w.SampleCount.Should().Be(1);
        w.UnusableSampleCount.Should().Be(1);
        w.BaselineTokenEstimate.Should().Be(100);
        w.ReturnedTokenEstimate.Should().Be(200);
        w.SavingPercent.Should().Be(-100);
    }

    [Fact]
    public void Known_historical_totals_recompute_net_without_backfilling_events()
    {
        var w = Build(Event(9_440_639, 0), Event(792_906, 928_929)).Windows!.Single(x => x.Key == "24h");
        w.BaselineTokenEstimate.Should().Be(10_233_545);
        w.EstimatedSavedTokens.Should().Be(9_304_616);
        w.PositiveSavedTokens.Should().Be(9_440_639);
        w.IncreasedTokens.Should().Be(136_023);
        w.SavingPercent.Should().Be(90.92);
        // Synthetic decomposition of the diagnostic totals, not its actual 185 rows.
        w.SampleCount.Should().Be(2);
    }

    // Uses the actual RedisDashboardSnapshotStore serializer configuration and
    // actual public DTO envelope. No store instance, Redis, HTTP host or fixture is
    // created here. These are contract SOURCE cases, not executed acceptance.
    [Theory]
    [InlineData(100, 20, 80, 80, 0)]
    [InlineData(100, 200, -100, 0, 100)]
    [InlineData(0, 30, -30, 0, 30)]
    [InlineData(0, 0, 0, 0, 0)]
    public void Snapshot_contract_roundtrip_preserves_signed_net_parts_and_same_sample_denominator(
        int baseline, int returned, int net, long positive, long increased)
    {
        var roundtrip = RoundTrip(Build(Event(baseline, returned)), out var json);
        using var document = JsonDocument.Parse(json);
        var wire = document.RootElement.GetProperty("payload").GetProperty("savings").GetProperty("windows")[0];
        wire.GetProperty("estimatedSavedTokens").GetInt32().Should().Be(net);
        wire.GetProperty("positiveSavedTokens").GetInt64().Should().Be(positive);
        wire.GetProperty("increasedTokens").GetInt64().Should().Be(increased);
        var window = roundtrip.Payload.Savings.Windows![0];
        window.HasData.Should().BeTrue();
        window.SampleCount.Should().Be(1);
        window.BaselineTokenEstimate.Should().Be(baseline);
        window.ReturnedTokenEstimate.Should().Be(returned);
        window.EstimatedSavedTokens.Should().Be(net);
        (window.PositiveSavedTokens - window.IncreasedTokens).Should().Be((long)net);
        window.SavingPercent.Should().Be(baseline > 0 ? Math.Round(net / (double)baseline * 100d, 2) : 0d);
        // baseline==0 explicitly makes the ratio inapplicable; numeric legacy 0
        // alone is never an applicability claim. Monitoring tests cover its label.
        window.UnusableSampleCount.Should().Be(0);
        window.SavingsCalculationVersion.Should().Be("net-br-v1");
    }

    [Fact]
    public void Legacy_snapshot_omitted_optional_fields_remain_null_without_fabricating_parts_or_validity()
    {
        var original = Build(Event(100, 200));
        _ = RoundTrip(original, out var currentJson);
        var legacy = JsonNode.Parse(currentJson)!.AsObject();
        var legacySavings = legacy["payload"]!["savings"]!.AsObject();
        legacySavings["estimatedSavedTokens"] = 0;
        legacySavings["averageSavingPercent"] = 0;
        foreach (var point in legacySavings["trend"]!.AsArray())
        {
            point!["estimatedSavedTokens"] = 0;
            point["savingPercent"] = 0;
        }
        foreach (var node in legacy["payload"]!["savings"]!["windows"]!.AsArray())
        {
            var window = node!.AsObject();
            window.Remove("positiveSavedTokens");
            window.Remove("increasedTokens");
            window.Remove("unusableSampleCount");
            window.Remove("savingsCalculationVersion");
            window["estimatedSavedTokens"] = 0; // Actual old clamped representation.
            window["savingPercent"] = 0;
        }
        var decoded = JsonSerializer.Deserialize<DashboardSnapshotEnvelope<DashboardContextSavingsSnapshotPayload>>(
            legacy.ToJsonString(), ProductionSnapshotJson)!;
        var old = decoded.Payload.Savings.Windows![0];
        old.BaselineTokenEstimate.Should().Be(100);
        old.ReturnedTokenEstimate.Should().Be(200);
        old.EstimatedSavedTokens.Should().Be(0); // Deserialization is not backfill.
        old.PositiveSavedTokens.Should().BeNull();
        old.IncreasedTokens.Should().BeNull();
        old.UnusableSampleCount.Should().BeNull();
        old.SavingsCalculationVersion.Should().BeNull();
        using var encoded = JsonDocument.Parse(JsonSerializer.Serialize(decoded, ProductionSnapshotJson));
        var wire = encoded.RootElement.GetProperty("payload").GetProperty("savings").GetProperty("windows")[0];
        wire.GetProperty("positiveSavedTokens").ValueKind.Should().Be(JsonValueKind.Null);
        wire.GetProperty("increasedTokens").ValueKind.Should().Be(JsonValueKind.Null);
        wire.GetProperty("unusableSampleCount").ValueKind.Should().Be(JsonValueKind.Null);
        wire.GetProperty("savingsCalculationVersion").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [Theory]
    [InlineData(1_500_000_000, 100_000_000, 4_500_000_000L, 300_000_000L, 4_200_000_000L)]
    [InlineData(100_000_000, 1_500_000_000, 300_000_000L, 4_500_000_000L, -4_200_000_000L)]
    public void Wide_window_and_trend_keep_signed_totals_when_individually_valid_events_exceed_Int32(
        int baseline, int returned, long totalBaseline, long totalReturned, long net)
    {
        var snapshot = RoundTrip(Build(Event(baseline, returned), Event(baseline, returned), Event(baseline, returned)), out _);
        var result = snapshot.Payload.Savings;
        var day = result.Windows!.Single(window => window.Key == "24h");
        day.SampleCount.Should().Be(3);
        day.BaselineTokenEstimate.Should().Be(totalBaseline);
        day.ReturnedTokenEstimate.Should().Be(totalReturned);
        day.EstimatedSavedTokens.Should().Be(net);
        (day.PositiveSavedTokens - day.IncreasedTokens).Should().Be(net);
        day.SavingPercent.Should().Be(Math.Round(net / (double)totalBaseline * 100, 2));
        result.BaselineTokenEstimate.Should().Be(totalBaseline);
        result.ReturnedTokenEstimate.Should().Be(totalReturned);
        result.EstimatedSavedTokens.Should().Be(net);
        var point = result.Trend.Should().ContainSingle().Subject;
        point.BaselineTokenEstimate.Should().Be(totalBaseline);
        point.ReturnedTokenEstimate.Should().Be(totalReturned);
        point.EstimatedSavedTokens.Should().Be(net);
    }

    [Fact]
    public void Contract_roundtrip_distinguishes_no_samples_invalid_samples_and_valid_zero_baseline()
    {
        var none = RoundTrip(Build(), out _).Payload.Savings.Windows![0];
        var invalid = RoundTrip(Build(new DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent(
            Now.AddMinutes(-1), false, "{\"savings\":{\"baselineTokenEstimate\":100}}")), out _).Payload.Savings.Windows![0];
        var zero = RoundTrip(Build(Event(0, 30)), out _).Payload.Savings.Windows![0];
        none.HasData.Should().BeFalse();
        none.SampleCount.Should().Be(0);
        none.UnusableSampleCount.Should().Be(0);
        invalid.HasData.Should().BeFalse();
        invalid.SampleCount.Should().Be(0);
        invalid.UnusableSampleCount.Should().Be(1);
        zero.HasData.Should().BeTrue();
        zero.SampleCount.Should().Be(1);
        zero.UnusableSampleCount.Should().Be(0);
        zero.BaselineTokenEstimate.Should().Be(0);
        zero.EstimatedSavedTokens.Should().Be(-30);
    }

    [Fact]
    public void Contract_roundtrip_keeps_each_window_N_P_I_and_denominator_on_identical_valid_rows()
    {
        var positive = Event(100, 10);
        var increase = Event(100, 250);
        var older = new DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent(
            Now.AddDays(-2), false, Event(900, 0).MetadataJson);
        var invalid = new DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent(
            Now.AddMinutes(-1), false, "{\"savings\":{\"baselineTokenEstimate\":10000}}");
        var evidence = new[] { positive.MetadataJson, increase.MetadataJson, older.MetadataJson, invalid.MetadataJson };
        var result = RoundTrip(Build(positive, increase, older, invalid), out _).Payload.Savings;
        var day = result.Windows!.Single(x => x.Key == "24h");
        day.SampleCount.Should().Be(2);
        day.UnusableSampleCount.Should().Be(1);
        day.BaselineTokenEstimate.Should().Be(200);
        day.ReturnedTokenEstimate.Should().Be(260);
        day.PositiveSavedTokens.Should().Be(90);
        day.IncreasedTokens.Should().Be(150);
        day.EstimatedSavedTokens.Should().Be(-60);
        day.SavingPercent.Should().Be(-30);
        var threeDays = result.Windows!.Single(x => x.Key == "3d");
        threeDays.SampleCount.Should().Be(3);
        threeDays.UnusableSampleCount.Should().Be(1);
        threeDays.BaselineTokenEstimate.Should().Be(1100);
        threeDays.ReturnedTokenEstimate.Should().Be(260);
        threeDays.PositiveSavedTokens.Should().Be(990);
        threeDays.IncreasedTokens.Should().Be(150);
        threeDays.EstimatedSavedTokens.Should().Be(840);
        threeDays.SavingPercent.Should().Be(76.36);
        new[] { positive.MetadataJson, increase.MetadataJson, older.MetadataJson, invalid.MetadataJson }.Should().Equal(evidence);
    }

    [Theory]
    [InlineData(-100)]
    [InlineData(80)]
    public void Working_context_public_estimate_DTO_keeps_signed_values_in_snapshot_serializer(int net)
    {
        var estimate = new ContextSavingsEstimateResult(100, 100 - net, net, net, "High", 100,
            ApproxSavedTokens: net, ExactSavedTokens: net);
        var json = JsonSerializer.Serialize(estimate, ProductionSnapshotJson);
        using var document = JsonDocument.Parse(json);
        document.RootElement.GetProperty("estimatedSavedTokens").GetInt32().Should().Be(net);
        document.RootElement.GetProperty("exactSavedTokens").GetInt32().Should().Be(net);
        var decoded = JsonSerializer.Deserialize<ContextSavingsEstimateResult>(json, ProductionSnapshotJson)!;
        decoded.EstimatedSavedTokens.Should().Be(net);
        decoded.ApproxSavedTokens.Should().Be(net);
        decoded.ExactSavedTokens.Should().Be(net);
    }

    private static JsonSerializerOptions ProductionSnapshotJson
        => (JsonSerializerOptions)(typeof(RedisDashboardSnapshotStore)
            .GetField("JsonOptions", BindingFlags.Static | BindingFlags.NonPublic)?.GetValue(null)
            ?? throw new InvalidOperationException("Production snapshot serializer identity changed; review the contract instead of substituting options."));

    private static DashboardSnapshotEnvelope<DashboardContextSavingsSnapshotPayload> RoundTrip(
        DashboardContextSavingsResult result, out string json)
    {
        var envelope = new DashboardSnapshotEnvelope<DashboardContextSavingsSnapshotPayload>(
            DashboardSnapshotKeys.ContextSavings, Now, 60, Now.AddSeconds(90), string.Empty, new(result));
        json = JsonSerializer.Serialize(envelope, ProductionSnapshotJson);
        return JsonSerializer.Deserialize<DashboardSnapshotEnvelope<DashboardContextSavingsSnapshotPayload>>(json, ProductionSnapshotJson)
            ?? throw new InvalidDataException("Snapshot roundtrip returned null.");
    }

    private static DashboardContextSavingsResult Build(params DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent[] rows)
        => DashboardSnapshotCollectorHostedService.BuildContextSavings(Now, Now.AddDays(-30), rows,
            new DashboardSnapshotCollectorHostedService.McpToolCallWindowCounts(9, 10, 11, 12));

    private static DashboardSnapshotCollectorHostedService.ContextSavingsTelemetryEvent Event(int baseline, int returned)
        => new(Now.AddMinutes(-1), false, JsonSerializer.Serialize(new
        {
            savings = new ContextSavingsEstimateResult(baseline, returned, Math.Max(0, baseline - returned), 99,
                ContextSavingsEstimator.HighConfidence, 100)
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
}

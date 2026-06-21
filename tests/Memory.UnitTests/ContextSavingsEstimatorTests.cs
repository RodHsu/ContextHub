using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class ContextSavingsEstimatorTests
{
    [Fact]
    public void Estimate_Should_Use_Source_Token_Estimate_For_Baseline()
    {
        var memoryId = Guid.NewGuid();
        var hit = CreateHit(
            memoryId,
            string.Join(' ', Enumerable.Repeat("excerpt-token", 200)),
            sourceTokenEstimate: 40);

        var estimate = ContextSavingsEstimator.Estimate([hit], EmptyContext());

        estimate.BaselineTokenEstimate.Should().Be(40);
        estimate.ReturnedTokenEstimate.Should().Be(0);
        estimate.EstimatedSavedTokens.Should().Be(40);
        estimate.EstimatedSavingPercent.Should().Be(100d);
        estimate.Confidence.Should().Be(ContextSavingsEstimator.HighConfidence);
        estimate.SourceCoveragePercent.Should().Be(100d);
        estimate.ApproxBaselineTokens.Should().Be(40);
        estimate.ApproxReturnedTokens.Should().Be(0);
        estimate.ApproxSavedTokens.Should().Be(40);
        estimate.ExactBaselineTokens.Should().BeNull();
        estimate.ExactReturnedTokens.Should().BeNull();
        estimate.ExactSavedTokens.Should().BeNull();
        estimate.ExactCoveragePercent.Should().Be(0d);
        estimate.TokenCountingMode.Should().Be(TokenCountingModes.Approximate);
    }

    [Fact]
    public void Estimate_Should_Fall_Back_To_Excerpt_Tokens_When_Source_Estimate_Is_Missing()
    {
        var excerpt = string.Join(' ', Enumerable.Repeat("fallback", 16));
        var hit = CreateHit(Guid.NewGuid(), excerpt, sourceTokenEstimate: 0);

        var estimate = ContextSavingsEstimator.Estimate([hit], EmptyContext());

        estimate.BaselineTokenEstimate.Should().Be(ChunkingService.ApproximateTokenCount(excerpt));
        estimate.SourceCoveragePercent.Should().Be(0d);
        estimate.Confidence.Should().Be(ContextSavingsEstimator.LowConfidence);
    }

    [Fact]
    public void Estimate_Should_Clamp_Saved_Tokens_When_Returned_Exceeds_Baseline()
    {
        var hit = CreateHit(Guid.NewGuid(), "small", sourceTokenEstimate: 4);
        var context = EmptyContext() with
        {
            Facts =
            [
                new WorkingContextSection(
                    Guid.NewGuid(),
                    "Oversized returned context",
                    string.Join(' ', Enumerable.Repeat("returned", 80)),
                    string.Empty)
            ]
        };

        var estimate = ContextSavingsEstimator.Estimate([hit], context);

        estimate.ReturnedTokenEstimate.Should().BeGreaterThan(estimate.BaselineTokenEstimate);
        estimate.EstimatedSavedTokens.Should().Be(0);
        estimate.ApproxSavedTokens.Should().Be(0);
        estimate.EstimatedSavingPercent.Should().Be(0d);
    }

    [Fact]
    public void Estimate_Should_Return_Zero_Percent_When_Baseline_Is_Zero()
    {
        var estimate = ContextSavingsEstimator.Estimate([], EmptyContext());

        estimate.BaselineTokenEstimate.Should().Be(0);
        estimate.EstimatedSavedTokens.Should().Be(0);
        estimate.EstimatedSavingPercent.Should().Be(0d);
        estimate.Confidence.Should().Be(ContextSavingsEstimator.LowConfidence);
    }

    [Theory]
    [InlineData(4, 4, ContextSavingsEstimator.HighConfidence, 100d)]
    [InlineData(4, 2, ContextSavingsEstimator.MediumConfidence, 50d)]
    [InlineData(4, 1, ContextSavingsEstimator.LowConfidence, 25d)]
    public void Estimate_Should_Classify_Confidence_By_Source_Coverage(
        int totalHits,
        int hitsWithSourceEstimate,
        string expectedConfidence,
        double expectedCoverage)
    {
        var hits = Enumerable.Range(0, totalHits)
            .Select(index => CreateHit(
                Guid.NewGuid(),
                "fallback excerpt",
                index < hitsWithSourceEstimate ? 12 : 0))
            .ToArray();

        var estimate = ContextSavingsEstimator.Estimate(hits, EmptyContext());

        estimate.Confidence.Should().Be(expectedConfidence);
        estimate.SourceCoveragePercent.Should().Be(expectedCoverage);
    }

    [Fact]
    public void Estimate_Should_Count_Distinct_Memory_Hits_Only()
    {
        var memoryId = Guid.NewGuid();

        var estimate = ContextSavingsEstimator.Estimate(
            [
                CreateHit(memoryId, "first", 100),
                CreateHit(memoryId, "duplicate", 100),
                CreateHit(Guid.NewGuid(), "second", 50)
            ],
            EmptyContext());

        estimate.BaselineTokenEstimate.Should().Be(150);
    }

    private static MemorySearchHit CreateHit(Guid memoryId, string excerpt, int sourceTokenEstimate)
        => new(
            memoryId,
            "Test memory",
            MemoryType.Fact,
            MemoryScope.Project,
            1m,
            excerpt,
            "document",
            "tests",
            [],
            ProjectContext.DefaultProjectId,
            sourceTokenEstimate);

    private static WorkingContextResult EmptyContext()
        => new([], [], [], [], [], [], [], []);
}

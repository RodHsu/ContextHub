using System.Globalization;
using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class GovernanceRegressionMatrixTests
{
    [Theory]
    [InlineData("importance", "0")]
    [InlineData("importance", "0.95")]
    [InlineData("importance", "1")]
    [InlineData("confidence", "0")]
    [InlineData("confidence", "0.95")]
    [InlineData("confidence", "1")]
    public void Validate_accepts_each_canonical_score_field_value(string field, string rawValue)
    {
        var value = decimal.Parse(rawValue, CultureInfo.InvariantCulture);
        var importance = field == "importance" ? value : 0.5m;
        var confidence = field == "confidence" ? value : 0.5m;

        var act = () => MemoryScoreContract.Validate(importance, confidence);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("importance", "-1")]
    [InlineData("importance", "1.0001")]
    [InlineData("importance", "95")]
    [InlineData("importance", "100")]
    [InlineData("confidence", "-1")]
    [InlineData("confidence", "1.0001")]
    [InlineData("confidence", "95")]
    [InlineData("confidence", "100")]
    public void Validate_rejects_each_noncanonical_score_field_value(string field, string rawValue)
    {
        var value = decimal.Parse(rawValue, CultureInfo.InvariantCulture);
        var importance = field == "importance" ? value : 0.5m;
        var confidence = field == "confidence" ? value : 0.5m;

        var exception = ((Action)(() => MemoryScoreContract.Validate(importance, confidence)))
            .Should().Throw<MemoryScoreValidationException>()
            .Which;

        exception.Field.Should().Be(field);
        exception.NumericValue.Should().Be(value);
        exception.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
    }

    [Theory]
    [InlineData("importance")]
    [InlineData("confidence")]
    public void ValidateJsonPayload_rejects_noncanonical_score_in_either_field(string field)
    {
        var payload = field == "importance"
            ? "{\"importance\":95,\"confidence\":0.95}"
            : "{\"importance\":0.95,\"confidence\":95}";

        var exception = ((Action)(() => MemoryScoreContract.ValidateJsonPayload(payload)))
            .Should().Throw<MemoryScoreValidationException>()
            .Which;

        exception.Field.Should().Be(field);
        exception.NumericValue.Should().Be(95m);
        exception.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
    }

    [Theory]
    [InlineData("{\"Importance\":0.8,\"importance\":95}", "importance")]
    [InlineData("{\"Confidence\":0.8,\"confidence\":95}", "confidence")]
    public void ValidateJsonPayload_rejects_case_variant_duplicate_score_properties(string payload, string field)
    {
        var exception = ((Action)(() => MemoryScoreContract.ValidateJsonPayload(payload)))
            .Should().Throw<MemoryScoreValidationException>()
            .Which;

        exception.Field.Should().Be(field);
        exception.NumericValue.Should().Be(95m);
        exception.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
    }
}

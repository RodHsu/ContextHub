using System.Globalization;
using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class MemoryScoreContractTests
{
    [Theory]
    [InlineData("0")]
    [InlineData("0.95")]
    [InlineData("1")]
    public void Validate_accepts_canonical_inclusive_range(string rawValue)
    {
        var value = decimal.Parse(rawValue, CultureInfo.InvariantCulture);

        var act = () => MemoryScoreContract.Validate(value, value);

        act.Should().NotThrow();
        MemoryScoreContract.IsValid(value).Should().BeTrue();
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("1.01")]
    [InlineData("95")]
    [InlineData("100")]
    [InlineData("101")]
    public void Validate_rejects_percentage_or_out_of_range_values_without_normalization(string rawValue)
    {
        var value = decimal.Parse(rawValue, CultureInfo.InvariantCulture);

        var exception = ((Action)(() => MemoryScoreContract.Validate(value, 0.5m)))
            .Should().Throw<MemoryScoreValidationException>()
            .Which;

        exception.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
        exception.ValidationCode.Should().Be(MemoryScoreContract.InvalidRangeCode);
        exception.ReasonClass.Should().Be(MemoryScoreContract.InvalidRangeReasonClass);
        exception.Field.Should().Be("importance");
        exception.NumericValue.Should().Be(value);
        exception.ReceivedValue.Should().Be(rawValue);
        exception.Message.Should().Contain("expectedRange=[0,1]");
        exception.Message.Should().Contain("normalization=forbidden");
        exception.Message.Should().NotContain("received=0.95", "95 must never be silently normalized to 0.95");
    }

    [Fact]
    public void Validate_update_allows_omitted_scores()
    {
        var request = new MemoryUpdateRequest(Guid.NewGuid());

        var act = () => MemoryScoreContract.Validate(request);

        act.Should().NotThrow();
    }

    [Fact]
    public void ValidateJsonPayload_rejects_noncanonical_score_before_persistence()
    {
        var act = () => MemoryScoreContract.ValidateJsonPayload("""{"importance":95,"confidence":0.95}""");

        var exception = act.Should().Throw<MemoryScoreValidationException>().Which;
        exception.Field.Should().Be("importance");
        exception.NumericValue.Should().Be(95m);
        exception.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
    }

    [Fact]
    public void ValidateJsonPayload_rejects_string_scores_instead_of_coercing_them()
    {
        var act = () => MemoryScoreContract.ValidateJsonPayload("""{"importance":"0.95"}""");

        var exception = act.Should().Throw<MemoryScoreValidationException>().Which;
        exception.Field.Should().Be("importance");
        exception.Code.Should().Be(MemoryScoreContract.InvalidTypeCode);
        exception.ReasonClass.Should().Be(MemoryScoreContract.InvalidTypeReasonClass);
        exception.ReceivedValue.Should().Be("String");
        exception.Message.Should().NotContain("0.95");
    }
}

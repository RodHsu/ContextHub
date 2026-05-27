using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class BuildMetadataTests
{
    [Theory]
    [InlineData("1", "v1.0.0")]
    [InlineData("1.2", "v1.2.0")]
    [InlineData("1.2.3", "v1.2.3")]
    [InlineData("1.1.0", "v1.1.0")]
    [InlineData("1.2.3.4", "v1.2.3")]
    [InlineData("1.0.0.0", "v1.0.0")]
    [InlineData("1.260526.1530", "v1.260526.1530")]
    [InlineData("1.0.202605261530", "v1.0.202605261530")]
    [InlineData("1.0.202605261530.7", "v1.0.202605261530")]
    [InlineData("v1.0", "v1.0.0")]
    [InlineData("v1.2.3", "v1.2.3")]
    [InlineData("1.2.3+build.4", "v1.2.3")]
    [InlineData("unknown", "unknown")]
    public void NormalizeVersion_Should_Return_Three_Part_Display_Version(string input, string expected)
    {
        var actual = BuildMetadata.NormalizeVersion(input);

        actual.Should().Be(expected);
    }
}

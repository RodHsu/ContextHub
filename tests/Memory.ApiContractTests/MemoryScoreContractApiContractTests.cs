using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Reflection;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.McpServer;

namespace Memory.ApiContractTests;

public sealed class MemoryScoreContractApiContractTests
{
    [Fact]
    public void User_preference_api_request_publishes_inclusive_score_ranges()
    {
        AssertRange(nameof(UserPreferenceUpsertToolRequest.Importance));
        AssertRange(nameof(UserPreferenceUpsertToolRequest.Confidence));
    }

    [Fact]
    public void Api_request_mapping_preserves_percentage_values_for_rejection()
    {
        var request = new UserPreferenceUpsertToolRequest(
            "score-contract-api",
            UserPreferenceKind.CommunicationStyle,
            "Score contract API fixture",
            "Fixture",
            "Fixture",
            Importance: 95m,
            Confidence: 100m);

        var mapped = request.ToApplicationRequest();

        mapped.Importance.Should().Be(95m);
        mapped.Confidence.Should().Be(100m);
        ((Action)(() => MemoryScoreContract.Validate(mapped)))
            .Should().Throw<MemoryScoreValidationException>();
    }

    private static void AssertRange(string propertyName)
    {
        var property = typeof(UserPreferenceUpsertToolRequest).GetProperty(propertyName);
        property.Should().NotBeNull();

        var range = property!.GetCustomAttribute<RangeAttribute>();
        range.Should().NotBeNull();
        Convert.ToDecimal(range!.Minimum, CultureInfo.InvariantCulture).Should().Be(0m);
        Convert.ToDecimal(range.Maximum, CultureInfo.InvariantCulture).Should().Be(1m);
    }
}

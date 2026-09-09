using FluentAssertions;
using Memory.Infrastructure;
using Microsoft.Extensions.Configuration;

namespace Memory.UnitTests;

public sealed class ScheduledGovernanceNaturalOriginAuthorityProviderTests
{
    [Fact]
    public void Complete_Server_Config_Should_Produce_Current_Authority()
    {
        var values = ValidValues();
        var provider = new ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        var authority = provider.GetCurrent();

        authority.Should().NotBeNull();
        authority!.PlatformIssuer.Should().Be("https://scheduler.example.test");
        authority.Environment.Should().Be("production");
        authority.TaskBindingHash.Should().Be(Digest('a'));
        authority.AuthorityEpochDigest.Should().Be(Digest('e'));
    }

    [Theory]
    [InlineData("PlatformIssuer", "")]
    [InlineData("Environment", " production")]
    [InlineData("ControlPlaneSourceSystem", "scheduler\ncontrol")]
    [InlineData("TaskBindingHash", "not-a-digest")]
    [InlineData("ScheduleDigest", null)]
    [InlineData("AuthorityEpochDigest", null)]
    [InlineData("AuthorityEpochDigest", "not-a-digest")]
    public void Missing_Or_Malformed_Server_Config_Should_Fail_Closed(
        string key,
        string? value)
    {
        var values = ValidValues();
        values[$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:{key}"] = value;
        var provider = new ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider(
            new ConfigurationBuilder().AddInMemoryCollection(values).Build());

        provider.GetCurrent().Should().BeNull();
    }

    private static Dictionary<string, string?> ValidValues()
        => new()
        {
            [$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:PlatformIssuer"] =
                "https://scheduler.example.test",
            [$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:ControlPlaneSourceSystem"] =
                "scheduler-control-plane",
            [$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:Environment"] = "production",
            [$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:TaskBindingHash"] = Digest('a'),
            [$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:AutomationBindingHash"] = Digest('b'),
            [$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:ScheduleDigest"] =
                Memory.Application.ScheduledGovernanceReliabilityService.CurrentScheduleDigest,
            [$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:ConfigurationDigest"] = Digest('d'),
            [$"{ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider.SectionName}:AuthorityEpochDigest"] = Digest('e')
        };

    private static string Digest(char value) => new(value, 64);
}

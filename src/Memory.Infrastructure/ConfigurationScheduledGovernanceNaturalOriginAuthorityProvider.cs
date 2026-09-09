using Memory.Application;
using Microsoft.Extensions.Configuration;

namespace Memory.Infrastructure;

/// <summary>
/// Reads the current natural-schedule authority exclusively from trusted
/// server configuration. Missing, partial, or malformed authority remains
/// unavailable so platform evidence cannot qualify by agreeing only with
/// itself.
/// </summary>
public sealed class ConfigurationScheduledGovernanceNaturalOriginAuthorityProvider(
    IConfiguration configuration) : IScheduledGovernanceNaturalOriginAuthorityProvider
{
    internal const string SectionName = "ScheduledGovernance:NaturalOriginAuthority";

    public ScheduledGovernanceNaturalOriginAuthoritySnapshot? GetCurrent()
    {
        var platformIssuer = ReadIdentifier("PlatformIssuer");
        var controlPlaneSourceSystem = ReadIdentifier("ControlPlaneSourceSystem");
        var environment = ReadIdentifier("Environment");
        var taskBindingHash = ReadDigest("TaskBindingHash");
        var automationBindingHash = ReadDigest("AutomationBindingHash");
        var scheduleDigest = ReadDigest("ScheduleDigest");
        var configurationDigest = ReadDigest("ConfigurationDigest");
        var authorityEpochDigest = ReadDigest("AuthorityEpochDigest");
        if (platformIssuer is null ||
            controlPlaneSourceSystem is null ||
            environment is null ||
            taskBindingHash is null ||
            automationBindingHash is null ||
            scheduleDigest is null ||
            configurationDigest is null ||
            authorityEpochDigest is null)
        {
            return null;
        }

        return new ScheduledGovernanceNaturalOriginAuthoritySnapshot(
            platformIssuer,
            controlPlaneSourceSystem,
            environment,
            taskBindingHash,
            automationBindingHash,
            scheduleDigest,
            configurationDigest,
            authorityEpochDigest);
    }

    private string? ReadIdentifier(string key)
    {
        var value = configuration[$"{SectionName}:{key}"];
        if (string.IsNullOrWhiteSpace(value) ||
            value.Length > 128 ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Any(char.IsControl))
        {
            return null;
        }

        return value;
    }

    private string? ReadDigest(string key)
    {
        var value = configuration[$"{SectionName}:{key}"];
        return value is { Length: 64 } &&
               value.All(character => character is >= '0' and <= '9' or >= 'a' and <= 'f')
            ? value
            : null;
    }
}

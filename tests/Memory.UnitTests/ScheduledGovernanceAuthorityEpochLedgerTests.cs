using FluentAssertions;
using Memory.Domain;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class ScheduledGovernanceAuthorityEpochLedgerTests
{
    [Fact]
    public void Request_hash_must_bind_scope_configuration_digest_and_predecessor()
    {
        var request = CreateRequest();

        var same = ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(request);
        var replay = ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(request);
        var differentTenant = ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(
            request with { TenantId = Guid.NewGuid() });
        var differentEnvironment = ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(
            request with { Environment = "staging" });
        var differentConfiguration = ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(
            request with { ConfigurationDigest = Digest('b') });
        var differentPredecessor = ScheduledGovernanceAuthorityEpochContract.ComputeRequestHash(
            request with { ExpectedPreviousAuthorityEpochDigest = Digest('c') });

        same.Should().Be(replay);
        new[] { differentTenant, differentEnvironment, differentConfiguration, differentPredecessor }
            .Should().OnlyHaveUniqueItems()
            .And.NotContain(same);
        ScheduledGovernanceAuthorityEpochContract.IsSha256Digest(same).Should().BeTrue();
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData(" ABC", false)]
    [InlineData("ABC", false)]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", true)]
    public void Digest_validation_requires_lower_case_sha256_shape(string? value, bool expected)
    {
        ScheduledGovernanceAuthorityEpochContract.IsSha256Digest(value).Should().Be(expected);
    }

    [Theory]
    [InlineData("production", true)]
    [InlineData("staging-01", true)]
    [InlineData(" production", false)]
    [InlineData("production ", false)]
    [InlineData("", false)]
    [InlineData("line\nfeed", false)]
    public void Environment_validation_requires_a_trimmed_printable_identifier(
        string value,
        bool expected)
    {
        ScheduledGovernanceAuthorityEpochContract.IsValidEnvironment(value).Should().Be(expected);
    }

    [Fact]
    public void Historical_replay_is_not_accepted_current_authority()
    {
        var historical = new ScheduledGovernanceAuthorityEpochAdvanceResult(
            ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayHistorical,
            new ScheduledGovernanceAuthorityEpoch { Generation = 1 },
            "historical");
        var current = new ScheduledGovernanceAuthorityEpochAdvanceResult(
            ScheduledGovernanceAuthorityEpochAdvanceStatus.ReplayCurrent,
            new ScheduledGovernanceAuthorityEpoch { Generation = 2 },
            "current");

        historical.IsReplay.Should().BeTrue();
        historical.IsCurrent.Should().BeFalse();
        historical.Accepted.Should().BeFalse();
        current.IsReplay.Should().BeTrue();
        current.IsCurrent.Should().BeTrue();
        current.Accepted.Should().BeTrue();
    }

    [Theory]
    [InlineData(4095, true)]
    [InlineData(4096, false)]
    [InlineData(4097, false)]
    [InlineData(-1, false)]
    public void Chain_capacity_rejects_an_append_before_the_bounded_limit_is_exceeded(
        int chainLength,
        bool expected)
    {
        ScheduledGovernanceAuthorityEpochLedger.HasCapacityForAppend(chainLength)
            .Should().Be(expected);
    }

    [Fact]
    public void Authority_epoch_migration_fails_fast_and_enforces_identity_predecessor_and_generation_bounds()
    {
        var assembly = typeof(DatabaseMigrationHostedService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(
                ".041_scheduled_governance_authority_epochs.sql",
                StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        var migration = reader.ReadToEnd();

        migration.Should().Contain("CREATE TABLE scheduled_governance_authority_epochs");
        migration.Should().NotContain("CREATE TABLE IF NOT EXISTS scheduled_governance_authority_epochs");
        migration.Should().Contain("id <> '00000000-0000-0000-0000-000000000000'::uuid");
        migration.Should().Contain("CHECK (generation BETWEEN 1 AND 4096)");
        migration.Should().Contain("previous_generation IS NOT NULL");
        migration.Should().Contain("BEFORE TRUNCATE ON scheduled_governance_authority_epochs");
    }

    private static ScheduledGovernanceAuthorityEpochAdvanceRequest CreateRequest()
        => new(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "production",
            Digest('a'),
            Digest('a'),
            Digest('b'),
            3);

    private static string Digest(char value)
        => new(value, ScheduledGovernanceAuthorityEpochContract.DigestLength);
}

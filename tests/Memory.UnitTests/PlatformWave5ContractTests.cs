using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class PlatformWave5ContractTests
{
    [Fact]
    public void Migration_should_separate_authority_audit_monitoring_and_keep_evidence_immutable()
    {
        var sql = ReadMigration(".050_platform_foundation_c_agent_resources.sql");
        sql.Should().Contain("CREATE SCHEMA IF NOT EXISTS authority");
        sql.Should().Contain("CREATE SCHEMA IF NOT EXISTS audit");
        sql.Should().Contain("CREATE SCHEMA IF NOT EXISTS monitoring");
        sql.Should().Contain("trg_authority_outbox_immutable");
        sql.Should().Contain("trg_execution_resolution_snapshot_immutable");
        sql.Should().Contain("ux_background_runs_one_active_scope");
        sql.Should().Contain("tenant_scope_key");
        sql.Should().Contain("PRIMARY KEY(projection_name, tenant_scope_key, project_id)");
        sql.Should().Contain("tenant_id, project_id, sequence");
        sql.Should().Contain("Incremental','Full");
        sql.Should().Contain("coverage_complete");
        sql.Should().Contain("stale_count");
        sql.Should().Contain("drift_count");
        sql.Should().Contain("repaired_count");
        sql.Should().Contain("rebuilt_count");
        sql.Should().Contain("monitoring.activity_projections");
        sql.Should().Contain("never used as business or security authority");
    }

    [Fact]
    public void Execution_contract_should_store_only_logical_requirements_and_explicit_retry_policy()
    {
        typeof(AgentExecutionResourceRequirement).GetProperties().Select(x => x.Name).Should().BeEquivalentTo(
            "RequirementId", "Kind", "LogicalResourceId", "ResolutionMode", "RequestedVersionId",
            "ExpectedIntegrityIdentity", "Purpose", "AuthorityRevision", "PolicyRevision");
        typeof(AgentExecutionResourceRequirement).GetProperties().Select(x => x.Name).Should().NotContain(name =>
            name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Provider", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Kek", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Dek", StringComparison.OrdinalIgnoreCase));
        Enum.GetValues<AgentExecutionResourceRetryMode>().Should().Equal(
            [AgentExecutionResourceRetryMode.ReResolve, AgentExecutionResourceRetryMode.ReuseSnapshot]);
    }

    [Fact]
    public void Migration_should_not_persist_secret_material_or_provider_locator_in_execution_resolution()
    {
        var sql = ReadMigration(".050_platform_foundation_c_agent_resources.sql");
        var start = sql.IndexOf("CREATE TABLE IF NOT EXISTS authority.agent_execution_resolution_snapshots", StringComparison.Ordinal);
        var end = sql.IndexOf("CREATE OR REPLACE FUNCTION audit.reject_immutable_mutation", start, StringComparison.Ordinal);
        var resourceTables = sql[start..end];
        resourceTables.Should().NotContain("raw_secret");
        resourceTables.Should().NotContain("provider_locator");
        resourceTables.Should().NotContain("kek");
        resourceTables.Should().NotContain("dek");
        resourceTables.Should().NotContain("capability_token");
    }

    private static string ReadMigration(string suffix)
    {
        var assembly = typeof(MemoryDbContext).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

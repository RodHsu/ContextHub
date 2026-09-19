using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class AgentExecutionContractTests
{
    [Fact]
    public void Migration_should_enforce_single_owner_immutable_package_and_append_only_audit()
    {
        var sql = ReadMigration(".043_agent_execution.sql");
        sql.Should().Contain("ux_agent_executions_one_active_work_item");
        sql.Should().Contain("ux_agent_executions_one_open_work_item");
        sql.Should().Contain("WHERE status IN ('Claimed','Running')");
        sql.Should().Contain("trg_agent_execution_package_immutable");
        sql.Should().Contain("trg_agent_execution_events_append_only");
        sql.Should().Contain("trg_agent_execution_operations_append_only");
        sql.Should().Contain("ux_agent_execution_operations_idempotency");
        sql.Should().Contain("ck_agent_executions_lease");
        sql.Should().Contain("work_item_id uuid NOT NULL REFERENCES project_work_items(id) ON DELETE RESTRICT");
    }

    [Fact]
    public void Backend_catalog_should_publish_execution_tools_without_expanding_restricted_app_surface()
    {
        var names = new[]
        {
            "agent_execution_prepare",
            "agent_execution_claim_next",
            "agent_execution_get",
            "agent_execution_heartbeat",
            "agent_execution_checkpoint",
            "agent_execution_block",
            "agent_execution_complete",
            "agent_execution_fail",
            "agent_execution_abandon"
        };

        McpPublishedToolCatalog.BackendToolNames.Should().Contain(names);
        McpPublishedToolCatalog.RestrictedToolNames.Should().NotContain(names);
        McpPublishedToolCatalog.AppFacingCatalogVersion.Should().Be("2026-09-08-v6");
    }

    [Fact]
    public void Contract_should_keep_lease_and_retry_bounds_explicit()
    {
        AgentExecutionContract.Version.Should().Be("1.0");
        AgentExecutionContract.DefaultLeaseSeconds.Should().BePositive();
        AgentExecutionContract.MaximumLeaseSeconds.Should().BeGreaterThan(AgentExecutionContract.DefaultLeaseSeconds);
    }

    [Fact]
    public void Migration_manifest_should_apply_skills_before_agent_execution()
    {
        var migrations = typeof(MemoryDbContext).Assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Sql.Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var skills = Array.FindIndex(migrations, name => name.EndsWith(".040_agent_skills.sql", StringComparison.Ordinal));
        var executions = Array.FindIndex(migrations, name => name.EndsWith(".043_agent_execution.sql", StringComparison.Ordinal));
        skills.Should().BeGreaterThanOrEqualTo(0);
        executions.Should().BeGreaterThan(skills);
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

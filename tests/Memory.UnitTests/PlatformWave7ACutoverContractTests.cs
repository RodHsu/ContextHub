using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class PlatformWave7ACutoverContractTests
{
    [Fact]
    public void Artifact_contract_should_expose_only_logical_managed_file_references()
    {
        Enum.GetNames<ProjectArtifactKind>().Should().Equal("Summary", "Snippet", "FileReference");
        typeof(ProjectArtifactPublishRequest).GetProperties().Select(x => x.Name).Should().Contain(["FileId", "FileVersionId"]);
        typeof(ProjectArtifactPublishRequest).GetProperties().Select(x => x.Name).Should().NotContain(["ObjectRef", "ExpiresAt"]);
        typeof(ProjectArtifactResult).GetProperties().Select(x => x.Name).Should().Contain(["FileId", "FileVersionId"]);
        typeof(ProjectArtifactResult).GetProperties().Select(x => x.Name).Should().NotContain(["ObjectRef", "ExpiresAt", "IsExpired"]);
    }

    [Fact]
    public void General_catalog_should_replace_legacy_tools_without_changing_agent_execution_or_skills_surfaces()
    {
        McpPublishedToolCatalog.RestrictedToolNames.Should().Contain(
            ["managed_file_register", "managed_files_list", "managed_files_search"]);
        McpPublishedToolCatalog.BackendToolNames.Should().NotContain(
            ["project_artifact_upload_object", "project_artifacts_prune_expired_objects"]);
        McpPublishedToolCatalog.RestrictedToolNames.Should().NotContain(
            ["project_artifact_upload_object", "project_artifacts_prune_expired_objects"]);
        McpPublishedToolCatalog.RestrictedToolNames.Should().Contain(
            [
                "agent_execution_prepare", "agent_execution_claim_next", "agent_execution_get",
                "agent_execution_heartbeat", "agent_execution_checkpoint", "agent_execution_block",
                "agent_execution_complete", "agent_execution_fail", "agent_execution_abandon",
                "skills_search_for_execution", "skills_resolution_feedback", "skills_select_for_execution",
                "skill_version_get", "skill_version_materialize", "skills_materialization_cleanup",
                "skills_invocation_record"
            ]);
    }

    [Fact]
    public void Migration_051_should_be_ordered_after_foundation_c_and_fail_closed_on_ambiguous_mapping()
    {
        var migrations = typeof(MemoryDbContext).Assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Sql.Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();
        var foundation = Array.FindIndex(migrations, name => name.EndsWith(".050_platform_foundation_c_agent_resources.sql", StringComparison.Ordinal));
        var cutover = Array.FindIndex(migrations, name => name.EndsWith(".051_legacy_artifact_managed_file_cutover.sql", StringComparison.Ordinal));
        cutover.Should().BeGreaterThan(foundation);

        using var stream = typeof(MemoryDbContext).Assembly.GetManifestResourceStream(migrations[cutover])!;
        using var reader = new StreamReader(stream);
        var sql = reader.ReadToEnd();
        sql.Should().Contain("ambiguous FileReference mapping(s); migration is fail-closed");
        sql.Should().Contain("audit.legacy_artifact_cutover_mappings");
        sql.Should().Contain("ck_memory_items_public_artifact_contract");
        sql.Should().Contain("REVOKE ALL ON TABLE audit.legacy_artifact_cutover_mappings FROM PUBLIC");
    }
}

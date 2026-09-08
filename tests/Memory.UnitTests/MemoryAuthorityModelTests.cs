using FluentAssertions;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Memory.UnitTests;

public sealed class MemoryAuthorityModelTests
{
    [Fact]
    public void New_memory_keeps_legacy_lifecycle_defaults_and_starts_as_current_authority()
    {
        var memory = new MemoryItem();

        memory.Status.Should().Be(MemoryStatus.Active);
        memory.AuthorityState.Should().Be(MemoryAuthorityState.Current);
        memory.SupersedesId.Should().BeNull();
        memory.SupersededById.Should().BeNull();
        memory.ValidFrom.Should().BeNull();
        memory.ValidUntil.Should().BeNull();
        memory.SuccessorEvidenceId.Should().BeNull();
        memory.SuccessorEvidenceRef.Should().BeEmpty();
    }

    [Fact]
    public void Memory_item_maps_authority_window_chain_and_restricted_self_references()
    {
        using var db = CreateDbContext();
        var entity = db.Model.FindEntityType(typeof(MemoryItem));
        entity.Should().NotBeNull();
        var memoryEntity = entity!;
        var table = StoreObjectIdentifier.Table("memory_items", null);

        foreach (var property in new[]
                 {
                     nameof(MemoryItem.AuthorityState),
                     nameof(MemoryItem.SupersedesId),
                     nameof(MemoryItem.SupersededById),
                     nameof(MemoryItem.ValidFrom),
                     nameof(MemoryItem.ValidUntil),
                     nameof(MemoryItem.SuccessorEvidenceId),
                     nameof(MemoryItem.SuccessorEvidenceRef)
                 })
        {
            memoryEntity.FindProperty(property).Should().NotBeNull();
        }

        memoryEntity.FindProperty(nameof(MemoryItem.AuthorityState))!
            .GetColumnName(table)
            .Should().Be("authority_state");
        memoryEntity.FindProperty(nameof(MemoryItem.SupersedesId))!
            .GetColumnName(table)
            .Should().Be("supersedes_id");
        memoryEntity.FindProperty(nameof(MemoryItem.SupersededById))!
            .GetColumnName(table)
            .Should().Be("superseded_by_id");
        memoryEntity.FindProperty(nameof(MemoryItem.ValidFrom))!
            .GetColumnName(table)
            .Should().Be("valid_from");
        memoryEntity.FindProperty(nameof(MemoryItem.ValidUntil))!
            .GetColumnName(table)
            .Should().Be("valid_until");

        var selfForeignKeys = memoryEntity.GetForeignKeys()
            .Where(x => x.PrincipalEntityType.ClrType == typeof(MemoryItem))
            .ToArray();
        selfForeignKeys.Should().Contain(x => HasForeignKey(x, nameof(MemoryItem.SupersedesId)));
        selfForeignKeys.Should().Contain(x => HasForeignKey(x, nameof(MemoryItem.SupersededById)));
        selfForeignKeys.Should().Contain(x => HasForeignKey(x, nameof(MemoryItem.SuccessorEvidenceId)));
        selfForeignKeys.Should().OnlyContain(x => x.DeleteBehavior == DeleteBehavior.Restrict);

        memoryEntity.GetIndexes().Should().Contain(x =>
            x.GetDatabaseName() == "ux_memory_items_supersedes_id" &&
            x.IsUnique &&
            x.GetFilter() == "supersedes_id IS NOT NULL");
        memoryEntity.GetIndexes().Should().Contain(x =>
            x.GetDatabaseName() == "ux_memory_items_superseded_by_id" &&
            x.IsUnique &&
            x.GetFilter() == "superseded_by_id IS NOT NULL");
        memoryEntity.GetIndexes().Should().Contain(x =>
            x.GetDatabaseName() == "ix_memory_items_authority_window" &&
            x.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(MemoryItem.TenantId),
                nameof(MemoryItem.OwnerUserId),
                nameof(MemoryItem.ProjectId),
                nameof(MemoryItem.AuthorityState),
                nameof(MemoryItem.ValidFrom),
                nameof(MemoryItem.ValidUntil)
            }));
        memoryEntity.GetIndexes().Should().Contain(x =>
            x.GetDatabaseName() == "ix_memory_items_successor_evidence" &&
            x.Properties.Select(property => property.Name).SequenceEqual(new[]
            {
                nameof(MemoryItem.TenantId),
                nameof(MemoryItem.OwnerUserId),
                nameof(MemoryItem.ProjectId),
                nameof(MemoryItem.SuccessorEvidenceId)
            }));
    }

    [Fact]
    public void Authority_migration_is_idempotent_and_preserves_history()
    {
        var migration = ReadAuthorityMigration();

        migration.Should().Contain("ADD COLUMN IF NOT EXISTS authority_state");
        migration.Should().Contain("ADD COLUMN IF NOT EXISTS supersedes_id");
        migration.Should().Contain("ADD COLUMN IF NOT EXISTS superseded_by_id");
        migration.Should().Contain("ADD COLUMN IF NOT EXISTS valid_from");
        migration.Should().Contain("ADD COLUMN IF NOT EXISTS valid_until");
        migration.Should().Contain("ADD COLUMN IF NOT EXISTS successor_evidence_id");
        migration.Should().Contain("ADD COLUMN IF NOT EXISTS successor_evidence_ref");
        migration.Should().Contain("CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_items_supersedes_id");
        migration.Should().Contain("CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_items_superseded_by_id");
        migration.Should().Contain("memory authority predecessor fork preflight failed");
        migration.Should().Contain("memory authority successor fork preflight failed");
        migration.IndexOf("memory authority predecessor fork preflight failed", StringComparison.Ordinal)
            .Should().BeLessThan(migration.IndexOf(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_items_supersedes_id",
                StringComparison.Ordinal));
        migration.IndexOf("memory authority successor fork preflight failed", StringComparison.Ordinal)
            .Should().BeLessThan(migration.IndexOf(
                "CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_items_superseded_by_id",
                StringComparison.Ordinal));
        migration.Should().Contain("ON memory_items(tenant_id, owner_user_id, project_id, successor_evidence_id)");
        migration.Should().Contain("CREATE OR REPLACE FUNCTION memory_items_authority_scope_guard()");
        migration.Should().Contain("DROP TRIGGER IF EXISTS memory_items_authority_scope_guard");
        migration.Should().Contain("UPDATE OF tenant_id, owner_user_id, project_id, scope, supersedes_id");
        migration.Should().Contain("target.owner_user_id IS DISTINCT FROM NEW.owner_user_id");
        migration.Should().Contain("target.scope IS DISTINCT FROM NEW.scope");
        migration.Should().Contain("evidence.owner_user_id IS DISTINCT FROM NEW.owner_user_id");
        migration.Should().Contain("evidence.scope IS DISTINCT FROM NEW.scope");
        migration.Should().Contain("OR linked.successor_evidence_id = NEW.id");
        migration.Should().Contain("ON DELETE RESTRICT");
        migration.Should().Contain("authority_state IN ('Current', 'Superseded', 'Historical', 'Pending')");
        migration.Should().Contain("valid_until IS NULL OR valid_from IS NULL OR valid_until > valid_from");
        migration.Should().Contain("WHEN 'superseded' THEN 'Superseded'");
        migration.Should().Contain("WHEN 'pending' THEN 'Pending'");
        migration.Should().Contain("ELSE 'Current'");
        migration.Should().NotContain("WHEN 'stale' THEN 'Historical'");
        migration.Should().NotContain("WHEN 'archived' THEN 'Historical'");
        migration.Should().Contain("SET valid_from = created_at");
        migration.Should().NotContain("DELETE FROM memory_items");
    }

    [Fact]
    public void Authority_migration_keeps_stale_and_archived_lifecycle_rows_current_without_authority_evidence()
    {
        var migration = ReadAuthorityMigration();

        migration.Should().Contain("-- orthogonal to authority");
        migration.Should().Contain("Stale and archived rows therefore remain Current");
        migration.Should().Contain("ELSE 'Current'");
        migration.Should().NotContain("WHEN 'stale' THEN 'Historical'");
        migration.Should().NotContain("WHEN 'archived' THEN 'Historical'");
    }

    [Fact]
    public void Governance_evidence_aging_is_mapped_and_migrated()
    {
        using var db = CreateDbContext();
        var finding = db.Model.FindEntityType(typeof(GovernanceFinding));
        var insight = db.Model.FindEntityType(typeof(ConversationInsight));
        finding.Should().NotBeNull();
        insight.Should().NotBeNull();
        finding!.FindProperty(nameof(GovernanceFinding.GovernanceLastEvidenceChangedAt))!
            .GetColumnName(StoreObjectIdentifier.Table("governance_findings", null))
            .Should().Be("governance_last_evidence_changed_at");
        insight!.FindProperty(nameof(ConversationInsight.GovernanceLastEvidenceChangedAt))!
            .GetColumnName(StoreObjectIdentifier.Table("conversation_insights", null))
            .Should().Be("governance_last_evidence_changed_at");

        var migration = ReadMigration(".035_governance_evidence_aging.sql");
        migration.Should().Contain("ADD COLUMN IF NOT EXISTS governance_last_evidence_changed_at");
        migration.Should().Contain("ix_governance_findings_exception_aging");
        migration.Should().Contain("ix_conversation_insights_exception_aging");
    }

    [Fact]
    public void Conversation_scope_migration_fails_closed_before_replacing_unique_indexes()
    {
        var migration = ReadMigration(".036_conversation_automation_tenant_scope.sql");

        var preflightIndex = migration.IndexOf(
            "conversation session scoped deduplication preflight failed",
            StringComparison.Ordinal);
        var dropIndex = migration.IndexOf(
            "DROP INDEX IF EXISTS ix_conversation_sessions_source_conversation",
            StringComparison.Ordinal);

        preflightIndex.Should().BeGreaterThanOrEqualTo(0);
        preflightIndex.Should().BeLessThan(dropIndex);
        migration.Should().Contain("conversation checkpoint scoped deduplication preflight failed");
        migration.Should().Contain("conversation insight scoped deduplication preflight failed");
        migration.Should().Contain("ERRCODE = '23505'");
        migration.Should().NotContain("DELETE FROM conversation_sessions");
        migration.Should().NotContain("DELETE FROM conversation_checkpoints");
        migration.Should().NotContain("DELETE FROM conversation_insights");
    }

    private static MemoryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=contexthub;Username=test;Password=test")
            .Options;
        return new MemoryDbContext(options);
    }

    private static bool HasForeignKey(IForeignKey foreignKey, string propertyName)
        => foreignKey.Properties.Count == 1 &&
           foreignKey.Properties[0].Name == propertyName &&
           foreignKey.DeleteBehavior == DeleteBehavior.Restrict;

    private static string ReadAuthorityMigration()
    {
        return ReadMigration(".033_authority_supersession.sql");
    }

    private static string ReadMigration(string suffix)
    {
        var assembly = typeof(DatabaseMigrationHostedService).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(x => x.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        return reader.ReadToEnd();
    }
}

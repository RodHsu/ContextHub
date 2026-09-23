using FluentAssertions;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Npgsql;
using Testcontainers.PostgreSql;

namespace Memory.IntegrationTests;

public sealed class AgentExecutionMigrationRehearsalTests
{
    [DockerRequiredFact]
    public async Task Production_like_039_database_should_upgrade_through_definition_state_and_agent_execution_and_replay_cleanly()
    {
        await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithPortBinding(5432, true)
            .WithDatabase("contexthub")
            .WithUsername("contexthub")
            .WithPassword("contexthub")
            .Build();
        await postgres.StartAsync();

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();

        var migrations = ReadMigrations();
        await ApplyThroughAsync(connection, migrations, "039_scheduled_acceptance_receipt_evidence.sql");

        var workItemId = Guid.NewGuid();
        var discussingWorkItemId = Guid.NewGuid();
        var readyWorkItemId = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO project_work_items
                (id, project_id, title, description, tags, status, priority, created_at, updated_at)
            VALUES
                (@id, 'migration-rehearsal', 'Preserved in-progress work item', '', '{}', 'InProgress', 50, NOW(), NOW()),
                (@discussing_id, 'migration-rehearsal', 'Explicit discussing work item', '', ARRAY['discussion:active'], 'InProgress', 50, NOW(), NOW()),
                (@ready_id, 'migration-rehearsal', 'Explicit ready work item', '', ARRAY['definition:ready-for-development'], 'Pending', 50, NOW(), NOW());
            """,
            new NpgsqlParameter<Guid>("id", workItemId),
            new NpgsqlParameter<Guid>("discussing_id", discussingWorkItemId),
            new NpgsqlParameter<Guid>("ready_id", readyWorkItemId));
        await ExecuteAsync(connection, """
            INSERT INTO project_hierarchies
                (id, parent_project_id, child_project_id, created_at, updated_at)
            VALUES
                (@id, 'legacy-parent', 'legacy-child', NOW(), NOW());
            """, new NpgsqlParameter<Guid>("id", Guid.NewGuid()));

        await ApplyRemainingAsync(connection, migrations);

        (await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM project_work_items WHERE id = @id AND status = 'InProgress' AND definition_state = 'Draft';",
            new NpgsqlParameter<Guid>("id", workItemId))).Should().Be(1);
        (await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM project_work_items WHERE id = @id AND status = 'InProgress' AND definition_state = 'Discussing';",
            new NpgsqlParameter<Guid>("id", discussingWorkItemId))).Should().Be(1);
        (await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM project_work_items WHERE id = @id AND status = 'Pending' AND definition_state = 'ReadyForDevelopment';",
            new NpgsqlParameter<Guid>("id", readyWorkItemId))).Should().Be(1);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM schema_migrations
            WHERE name IN ('040_agent_skills.sql', '041_scheduled_governance_authority_epochs.sql',
                           '041a_memory_score_reconciliation.sql', '042_memory_score_contract.sql',
                           '043_agent_execution.sql', '044_project_work_item_definition_state.sql',
                           '045_platform_foundation_a.sql', '046_managed_storage_encryption_gateway.sql',
                           '047_managed_files_dlp_tag_governance.sql');
            """)).Should().Be(9);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM project_hierarchies
            WHERE parent_project_id = 'legacy-parent' AND child_project_id = 'legacy-child'
              AND dimension = 'discussion' AND authorization_inheritable = FALSE AND revision = 1;
            """)).Should().Be(1, "legacy hierarchy rows must remain non-authorization-inheritable");
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN
                ('project_security_revisions', 'project_authorization_policies', 'project_explicit_grants',
                 'canonical_tag_definitions', 'canonical_tag_aliases', 'canonical_tag_relations',
                 'canonical_tag_bindings', 'canonical_tag_suggestions');
            """)).Should().Be(8);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN
                ('managed_objects', 'managed_object_chunks', 'managed_transfer_sessions', 'managed_transfer_operations');
            """)).Should().Be(4);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN
                ('file_assets', 'file_versions', 'file_relations', 'file_profiles', 'file_representations',
                 'file_access_events', 'file_security_findings', 'file_search_projections', 'file_deletion_records',
                 'canonical_tag_telemetry_events', 'canonical_tag_daily_aggregates', 'canonical_tag_governance_proposals');
            """)).Should().Be(12);

        var tagDefinitionId = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO canonical_tag_definitions
                (id, project_id, canonical_name, normalized_name, description, created_at, updated_at)
            VALUES (@id, 'migration-rehearsal', 'Platform Security', 'platform-security', '', NOW(), NOW());
            INSERT INTO canonical_tag_aliases (id, definition_id, project_id, alias, normalized_alias)
            VALUES (@alias_id, @id, 'migration-rehearsal', 'security platform', 'security-platform');
            INSERT INTO project_explicit_grants
                (id, project_id, principal_id, right_key, effect, evidence_ref, created_at, updated_at)
            VALUES (@grant_id, 'migration-rehearsal', 'agent', 'read', 'Allow', 'migration-rehearsal', NOW(), NOW());
            """,
            new NpgsqlParameter<Guid>("id", tagDefinitionId),
            new NpgsqlParameter<Guid>("alias_id", Guid.NewGuid()),
            new NpgsqlParameter<Guid>("grant_id", Guid.NewGuid()));
        var aliasRace = () => ExecuteAsync(connection, """
            INSERT INTO canonical_tag_aliases (id, definition_id, project_id, alias, normalized_alias)
            VALUES (@id, @definition_id, 'migration-rehearsal', 'Security Platform duplicate', 'security-platform');
            """,
            new NpgsqlParameter<Guid>("id", Guid.NewGuid()),
            new NpgsqlParameter<Guid>("definition_id", tagDefinitionId));
        (await aliasRace.Should().ThrowAsync<PostgresException>()).Which.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        (await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM project_explicit_grants WHERE project_id = 'migration-rehearsal' AND evidence_ref = 'migration-rehearsal';"))
            .Should().Be(1, "explicit grants are persisted independently from dynamically evaluated policies");

        var skillId = Guid.NewGuid();
        var skillVersionId = Guid.NewGuid();
        var executionId = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO skills
                (id, stable_key, name, description, when_to_use, license, risk_level, created_at, updated_at)
            VALUES
                (@skill_id, 'migration-rehearsal', 'Migration rehearsal', '', '', 'MIT', 'Low', NOW(), NOW());
            INSERT INTO skill_versions
                (id, skill_id, version, status, content_hash, bundle_json, source_kind, source_ref,
                 trust_level, created_at, updated_at, published_at)
            VALUES
                (@version_id, @skill_id, '1.0.0', 'Published', repeat('a', 64), '{}'::jsonb,
                 'Repository', 'migration-rehearsal', 'SourceVerified', NOW(), NOW(), NOW());
            INSERT INTO agent_executions
                (id, work_item_id, project_id, repository_id, agent_type, package_json, package_hash,
                 package_context_version, skill_snapshot_json, status, eligible_at, created_at, updated_at)
            VALUES
                (@execution_id, @work_item_id, 'migration-rehearsal', 'ContextHub', 'Codex', '{}'::jsonb,
                 repeat('b', 64), '1.0', jsonb_build_object('skillVersionId', @version_id), 'Ready', NOW(), NOW(), NOW());
            """,
            new NpgsqlParameter<Guid>("skill_id", skillId),
            new NpgsqlParameter<Guid>("version_id", skillVersionId),
            new NpgsqlParameter<Guid>("execution_id", executionId),
            new NpgsqlParameter<Guid>("work_item_id", workItemId));

        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM agent_executions WHERE id = @id AND skill_snapshot_json->>'skillVersionId' = @version_id;",
            new NpgsqlParameter<Guid>("id", executionId),
            new NpgsqlParameter<string>("version_id", skillVersionId.ToString()))).Should().Be(1);

        await connection.CloseAsync();
        await connection.OpenAsync();
        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection,
            "SELECT COUNT(*) FROM schema_migrations WHERE name IN ('043_agent_execution.sql', '044_project_work_item_definition_state.sql', '045_platform_foundation_a.sql', '046_managed_storage_encryption_gateway.sql', '047_managed_files_dlp_tag_governance.sql');")).Should().Be(5);
    }

    [DockerRequiredFact]
    public async Task Migration_047_should_upgrade_a_046_shaped_database_and_replay_idempotently()
    {
        await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithPortBinding(5432, true)
            .WithDatabase("contexthub")
            .WithUsername("contexthub")
            .WithPassword("contexthub")
            .Build();
        await postgres.StartAsync();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var migrations = ReadMigrations();
        await ApplyThroughAsync(connection, migrations, "046_managed_storage_encryption_gateway.sql");

        var definitionId = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO canonical_tag_definitions
                (id, project_id, canonical_name, normalized_name, description, created_at, updated_at)
            VALUES (@id, 'wave-3-rehearsal', 'Existing Tag', 'existing-tag', '', NOW(), NOW());
            """, new NpgsqlParameter<Guid>("id", definitionId));

        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM canonical_tag_definitions WHERE id = @id AND status = 'Active' AND revision = 1;", new NpgsqlParameter<Guid>("id", definitionId))).Should().Be(1);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '047_managed_files_dlp_tag_governance.sql';")).Should().Be(1);

        await connection.CloseAsync();
        await connection.OpenAsync();
        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '047_managed_files_dlp_tag_governance.sql';")).Should().Be(1);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM canonical_tag_definitions WHERE id = @id;", new NpgsqlParameter<Guid>("id", definitionId))).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Migration_048_should_upgrade_a_047_shaped_database_and_replay_idempotently_without_plaintext_columns()
    {
        await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithPortBinding(5432, true)
            .WithDatabase("contexthub")
            .WithUsername("contexthub")
            .WithPassword("contexthub")
            .Build();
        await postgres.StartAsync();
        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var migrations = ReadMigrations();
        await ApplyThroughAsync(connection, migrations, "047_managed_files_dlp_tag_governance.sql");

        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '048_secrets_ssh_password_step_up.sql';")).Should().Be(1);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN
                ('secrets', 'secret_versions', 'secret_relations', 'secret_grants', 'secret_policies', 'secret_leases',
                 'secret_access_events', 'step_up_assertions', 'step_up_authentication_attempts', 'ssh_certificate_leases', 'ssh_revocation_records');
            """)).Should().Be(11);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name IN ('secrets', 'secret_versions', 'secret_leases', 'secret_access_events', 'step_up_assertions')
              AND column_name ~ '(plaintext$|material|password|private_key|kek|session_id)';
            """)).Should().Be(0, "secret material, passwords, KEKs, private keys, and raw session identifiers must not have persistence columns");

        var secretId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        await ExecuteAsync(connection, """
            INSERT INTO secrets
                (id, project_id, name, normalized_name, kind, state, revision, created_at, updated_at)
            VALUES (@secret_id, 'ContextHub', 'migration-secret', 'migration-secret', 'ApiToken', 'Active', 1, NOW(), NOW());
            INSERT INTO secret_versions
                (id, secret_id, version_number, state, envelope_schema_version, encryption_algorithm, key_id,
                 wrapped_dek, wrap_nonce, wrap_tag, ciphertext, ciphertext_nonce, ciphertext_tag, ciphertext_sha256,
                 plaintext_length, created_at)
            VALUES (@version_id, @secret_id, 1, 'Active', 1, 'AES-256-GCM', 'kek-v1',
                    decode(repeat('11', 32), 'hex'), decode(repeat('22', 12), 'hex'), decode(repeat('33', 16), 'hex'),
                    decode('aabbccdd', 'hex'), decode(repeat('44', 12), 'hex'), decode(repeat('55', 16), 'hex'),
                    repeat('a', 64), 4, NOW());
            UPDATE secrets SET current_version_id = @version_id, revision = 2 WHERE id = @secret_id;
            """,
            new NpgsqlParameter<Guid>("secret_id", secretId),
            new NpgsqlParameter<Guid>("version_id", versionId));
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM secrets WHERE id = @secret_id AND current_version_id = @version_id;",
            new NpgsqlParameter<Guid>("secret_id", secretId), new NpgsqlParameter<Guid>("version_id", versionId))).Should().Be(1);

        await connection.CloseAsync();
        await connection.OpenAsync();
        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '048_secrets_ssh_password_step_up.sql';")).Should().Be(1);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM secrets WHERE id = @secret_id;", new NpgsqlParameter<Guid>("secret_id", secretId))).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Definition_state_migration_should_fail_closed_on_conflicting_compatibility_tags()
    {
        await using var postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithPortBinding(5432, true)
            .WithDatabase("contexthub")
            .WithUsername("contexthub")
            .WithPassword("contexthub")
            .Build();
        await postgres.StartAsync();

        await using var connection = new NpgsqlConnection(postgres.GetConnectionString());
        await connection.OpenAsync();
        var migrations = ReadMigrations();
        await ApplyThroughAsync(connection, migrations, "043_agent_execution.sql");
        await ExecuteAsync(connection, """
            INSERT INTO project_work_items
                (id, project_id, title, description, tags, status, priority, created_at, updated_at)
            VALUES
                (@id, 'migration-rehearsal', 'Conflicting definition labels', '',
                 ARRAY['discussion:active', 'definition:ready-for-development'], 'Pending', 50, NOW(), NOW());
            """, new NpgsqlParameter<Guid>("id", Guid.NewGuid()));

        var migration = migrations.Single(x => x.Name == "044_project_work_item_definition_state.sql");
        var act = () => ApplyAsync(connection, migration);
        await act.Should().ThrowAsync<PostgresException>()
            .WithMessage("*conflicting explicit DefinitionState compatibility tags*");
        (await ScalarAsync<bool>(connection,
            "SELECT EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name = 'project_work_items' AND column_name = 'definition_state');"))
            .Should().BeFalse("the failed migration transaction must roll back the schema change");
    }

    private static IReadOnlyList<(string Name, string Sql)> ReadMigrations()
    {
        var assembly = typeof(MemoryDbContext).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.Contains(".Sql.Migrations.", StringComparison.Ordinal) && name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(name => name, StringComparer.Ordinal)
            .Select(name =>
            {
                using var stream = assembly.GetManifestResourceStream(name)
                    ?? throw new InvalidOperationException($"Embedded migration '{name}' was not found.");
                using var reader = new StreamReader(stream);
                var migrationName = name[(name.LastIndexOf(".Sql.Migrations.", StringComparison.Ordinal) + ".Sql.Migrations.".Length)..];
                return (migrationName, reader.ReadToEnd());
            })
            .ToArray();
    }

    private static async Task ApplyThroughAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(string Name, string Sql)> migrations,
        string terminalMigration)
    {
        foreach (var migration in migrations)
        {
            await ApplyAsync(connection, migration);
            if (migration.Name == terminalMigration)
            {
                return;
            }
        }

        throw new InvalidOperationException($"Migration '{terminalMigration}' was not found.");
    }

    private static async Task ApplyRemainingAsync(
        NpgsqlConnection connection,
        IReadOnlyList<(string Name, string Sql)> migrations)
    {
        foreach (var migration in migrations)
        {
            await ApplyAsync(connection, migration);
        }
    }

    private static async Task ApplyAsync(NpgsqlConnection connection, (string Name, string Sql) migration)
    {
        await ExecuteAsync(connection, """
            CREATE TABLE IF NOT EXISTS schema_migrations
            (
                name TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """);
        if (await ScalarAsync<bool>(connection,
                "SELECT EXISTS (SELECT 1 FROM schema_migrations WHERE name = @name);",
                new NpgsqlParameter<string>("name", migration.Name)))
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandTimeout = 300;
            command.CommandText = migration.Sql;
            await command.ExecuteNonQueryAsync();
            await using var record = connection.CreateCommand();
            record.Transaction = transaction;
            record.CommandText = "INSERT INTO schema_migrations (name) VALUES (@name);";
            record.Parameters.Add(new NpgsqlParameter<string>("name", migration.Name));
            await record.ExecuteNonQueryAsync();
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 300;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected a scalar database value.");
        return (T)value;
    }
}

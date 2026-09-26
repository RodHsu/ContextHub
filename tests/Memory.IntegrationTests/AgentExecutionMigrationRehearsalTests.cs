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
    public async Task Migration_049_should_upgrade_a_048_shaped_database_and_replay_idempotently_without_factor_secrets()
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
        await ApplyThroughAsync(connection, migrations, "048_secrets_ssh_password_step_up.sql");

        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '049_mfa_totp_webauthn.sql';")).Should().Be(1);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.tables
            WHERE table_schema = 'public' AND table_name IN
                ('mfa_authority_states', 'totp_factors', 'mfa_recovery_codes', 'webauthn_credentials', 'webauthn_ceremonies', 'mfa_security_events');
            """)).Should().Be(6);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = 'public'
              AND table_name IN ('totp_factors', 'mfa_recovery_codes', 'webauthn_credentials', 'webauthn_ceremonies', 'mfa_security_events')
              AND column_name ~ '(seed|secret|private_key|recovery_code$|raw_session|factor_material)';
            """)).Should().Be(2, "only opaque foreign-key identifiers seed_secret_id and seed_secret_version_id may mention seed; no factor plaintext column may exist");
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM pg_constraint
            WHERE conname IN ('ck_step_up_assertions_method', 'ck_step_up_assertions_assurance', 'ck_step_up_assertions_mfa_authority',
                'ck_mfa_authority_states_revision', 'ck_totp_factors_authority', 'ck_webauthn_ceremonies_authority',
                'ck_webauthn_ceremonies_hashes', 'ck_mfa_recovery_code_crypto');
            """)).Should().Be(8);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = 'public' AND
                ((table_name = 'step_up_assertions' AND column_name IN ('mfa_authority_revision', 'mfa_policy_revision')) OR
                 (table_name = 'totp_factors' AND column_name IN ('authority_revision_at_start', 'policy_revision_at_start', 'required_assurance_at_start', 'authorization_assertion_id', 'authorization_assertion_revision')) OR
                 (table_name = 'webauthn_ceremonies' AND column_name IN ('authority_revision_at_start', 'policy_revision_at_start', 'required_assurance_at_start', 'authorization_assertion_id', 'authorization_assertion_revision')));
            """)).Should().Be(12);

        await connection.CloseAsync();
        await connection.OpenAsync();
        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '049_mfa_totp_webauthn.sql';")).Should().Be(1);
    }

    [DockerRequiredFact]
    public async Task Migration_050_should_upgrade_a_049_shaped_database_with_isolated_authority_and_rebuildable_monitoring()
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
        await ApplyThroughAsync(connection, migrations, "049_mfa_totp_webauthn.sql");

        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '050_platform_foundation_c_agent_resources.sql';")).Should().Be(1);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.tables WHERE
                (table_schema = 'authority' AND table_name IN ('background_runs', 'agent_execution_resolution_snapshots', 'agent_execution_resolution_items', 'agent_execution_resource_approvals')) OR
                (table_schema = 'audit' AND table_name IN ('authority_outbox_events', 'background_events')) OR
                (table_schema = 'monitoring' AND table_name IN ('outbox_deliveries', 'activity_projections', 'projection_states'));
            """)).Should().Be(9);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM information_schema.columns
            WHERE table_schema = 'authority'
              AND table_name IN ('agent_execution_resolution_snapshots', 'agent_execution_resolution_items', 'agent_execution_resource_approvals')
              AND column_name ~ '(raw_secret|provider_locator|kek|dek|capability_token)';
            """)).Should().Be(0);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM pg_trigger WHERE NOT tgisinternal AND tgname IN
                ('trg_authority_outbox_immutable', 'trg_background_events_immutable',
                 'trg_execution_resolution_snapshot_immutable', 'trg_execution_resolution_item_immutable');
            """)).Should().Be(4);

        await connection.CloseAsync();
        await connection.OpenAsync();
        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '050_platform_foundation_c_agent_resources.sql';")).Should().Be(1);
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

    [DockerRequiredFact]
    public async Task Migration_051_should_cut_over_legacy_artifacts_replay_and_preserve_restricted_rollback_evidence()
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
        await ApplyThroughAsync(connection, migrations, "050_platform_foundation_c_agent_resources.sql");

        var managedObjectId = Guid.NewGuid();
        var fileId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var mappedArtifactId = Guid.NewGuid();
        var externalArtifactId = Guid.NewGuid();
        var summaryArtifactId = Guid.NewGuid();
        var contentHash = new string('a', 64);
        var mappedMetadata = $$$"""
            {"artifactExchange":true,"kind":"FileReference","sourceSystem":"codex","objectRef":{"provider":"legacy-provider","bucket":"private-bucket","key":"objects/legacy.bin","sha256":"{{{contentHash}}}"},"metadata":{"provider":"legacy-provider","safeLabel":"preserved"}}
            """;
        var externalMetadata = """{"artifactExchange":true,"kind":"ExternalObject","sourceSystem":"codex","objectRef":{"provider":"legacy-provider","bucket":"private-bucket","key":"objects/retired.bin"}}""";
        var summaryMetadata = """{"artifactExchange":true,"kind":"Summary","sourceSystem":"codex","metadata":{"provider":"legacy-provider","safeLabel":"preserved"}}""";

        await ExecuteAsync(connection, """
            INSERT INTO managed_objects
                (id, project_id, security_domain, state, storage_id, plaintext_length, chunk_size, chunk_count,
                 encryption_schema_version, encryption_generation, encryption_algorithm, key_id, wrapped_dek,
                 wrap_nonce, wrap_tag, plaintext_sha256, staged_until, created_at, updated_at)
            VALUES
                (@managed_object_id, 'wave-7a-rehearsal', 'ManagedFile', 'Ready', repeat('1', 64), 1, 65536, 1,
                 1, 1, 'AES-256-GCM', 'rehearsal-key', decode(repeat('11', 32), 'hex'), decode(repeat('22', 12), 'hex'),
                 decode(repeat('33', 16), 'hex'), @content_hash, NOW() + INTERVAL '1 hour', NOW(), NOW());
            INSERT INTO file_assets
                (id, project_id, logical_file_name, normalized_file_name, state, created_by_actor_id, created_at, updated_at)
            VALUES (@file_id, 'wave-7a-rehearsal', 'legacy.bin', 'legacy.bin', 'Active', 'migration-rehearsal', NOW(), NOW());
            INSERT INTO file_versions
                (id, file_asset_id, managed_object_id, version_number, content_sha256, content_type,
                 deduplication_scope_key, lifecycle, classification, created_at, updated_at)
            VALUES (@version_id, @file_id, @managed_object_id, 1, @content_hash, 'application/octet-stream',
                    repeat('2', 64), 'IntegrityVerified', 'Restricted', NOW(), NOW());
            INSERT INTO memory_items
                (id, external_key, scope, memory_type, title, content, summary, tags, source_type, source_ref,
                 importance, confidence, status, metadata_json, project_id, created_at, updated_at)
            VALUES
                (@mapped_id, 'wave7a-mapped', 'Project', 'Artifact', 'Mapped file reference', 'legacy', 'legacy', '{}',
                 'project-artifact-exchange', 'rehearsal', 0.5, 0.5, 'Active', @mapped_metadata, 'wave-7a-rehearsal', NOW(), NOW()),
                (@external_id, 'wave7a-external', 'Project', 'Artifact', 'Retired external reference', 'legacy', 'legacy', '{}',
                 'project-artifact-exchange', 'rehearsal', 0.5, 0.5, 'Active', @external_metadata, 'wave-7a-rehearsal', NOW(), NOW()),
                (@summary_id, 'wave7a-summary', 'Project', 'Artifact', 'Sanitized summary', 'summary', 'summary', '{}',
                 'project-artifact-exchange', 'rehearsal', 0.5, 0.5, 'Active', @summary_metadata, 'wave-7a-rehearsal', NOW(), NOW());
            """,
            new NpgsqlParameter<Guid>("managed_object_id", managedObjectId),
            new NpgsqlParameter<Guid>("file_id", fileId),
            new NpgsqlParameter<Guid>("version_id", versionId),
            new NpgsqlParameter<Guid>("mapped_id", mappedArtifactId),
            new NpgsqlParameter<Guid>("external_id", externalArtifactId),
            new NpgsqlParameter<Guid>("summary_id", summaryArtifactId),
            new NpgsqlParameter<string>("content_hash", contentHash),
            new NpgsqlParameter<string>("mapped_metadata", mappedMetadata),
            new NpgsqlParameter<string>("external_metadata", externalMetadata),
            new NpgsqlParameter<string>("summary_metadata", summaryMetadata));

        await ApplyRemainingAsync(connection, migrations);

        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '051_legacy_artifact_managed_file_cutover.sql';")).Should().Be(1);
        (await ScalarAsync<long>(connection, """
            SELECT COUNT(*) FROM memory_items
            WHERE id = @id AND status = 'Active'
              AND metadata_json::jsonb->>'kind' = 'FileReference'
              AND metadata_json::jsonb->>'fileId' = @file_id
              AND metadata_json::jsonb->>'fileVersionId' = @version_id
              AND metadata_json NOT ILIKE '%provider%' AND metadata_json NOT ILIKE '%bucket%';
            """,
            new NpgsqlParameter<Guid>("id", mappedArtifactId),
            new NpgsqlParameter<string>("file_id", fileId.ToString()),
            new NpgsqlParameter<string>("version_id", versionId.ToString()))).Should().Be(1);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM memory_items WHERE id = @id AND status = 'Archived';", new NpgsqlParameter<Guid>("id", externalArtifactId))).Should().Be(1);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM memory_items WHERE id = @id AND status = 'Active' AND metadata_json NOT ILIKE '%provider%';", new NpgsqlParameter<Guid>("id", summaryArtifactId))).Should().Be(1);
        (await ScalarAsync<string>(connection, "SELECT old_metadata_text FROM audit.legacy_artifact_cutover_mappings WHERE memory_id = @id;", new NpgsqlParameter<Guid>("id", mappedArtifactId))).Should().Be(mappedMetadata);

        await ExecuteAsync(connection, "BEGIN; ALTER TABLE memory_items DROP CONSTRAINT ck_memory_items_public_artifact_contract; UPDATE memory_items mi SET metadata_json = mapping.old_metadata_text, status = mapping.old_status FROM audit.legacy_artifact_cutover_mappings mapping WHERE mi.id = mapping.memory_id; ROLLBACK;");
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM memory_items WHERE id = @id AND metadata_json::jsonb->>'kind' = 'FileReference' AND metadata_json::jsonb ? 'objectRef';", new NpgsqlParameter<Guid>("id", mappedArtifactId))).Should().Be(0, "rollback rehearsal must be reversible without mutating the accepted cutover state");

        await ApplyRemainingAsync(connection, migrations);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '051_legacy_artifact_managed_file_cutover.sql';")).Should().Be(1);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM audit.legacy_artifact_cutover_mappings;")).Should().Be(2);
    }

    [DockerRequiredFact]
    public async Task Migration_051_should_fail_closed_when_a_legacy_file_reference_has_ambiguous_managed_file_matches()
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
        await ApplyThroughAsync(connection, migrations, "050_platform_foundation_c_agent_resources.sql");
        var hash = new string('b', 64);
        var artifactId = Guid.NewGuid();

        await ExecuteAsync(connection, """
            INSERT INTO managed_objects
                (id, project_id, security_domain, state, storage_id, plaintext_length, chunk_size, chunk_count,
                 encryption_schema_version, encryption_generation, encryption_algorithm, key_id, wrapped_dek,
                 wrap_nonce, wrap_tag, plaintext_sha256, staged_until, created_at, updated_at)
            SELECT id, 'wave-7a-ambiguous', 'ManagedFile', 'Ready', storage_id, 1, 65536, 1, 1, 1, 'AES-256-GCM',
                   'rehearsal-key', decode(repeat('11', 32), 'hex'), decode(repeat('22', 12), 'hex'), decode(repeat('33', 16), 'hex'),
                   @hash, NOW() + INTERVAL '1 hour', NOW(), NOW()
            FROM (VALUES (@object1::uuid, repeat('3', 64)), (@object2::uuid, repeat('4', 64))) seed(id, storage_id);
            INSERT INTO file_assets
                (id, project_id, logical_file_name, normalized_file_name, state, created_by_actor_id, created_at, updated_at)
            VALUES
                (@file1, 'wave-7a-ambiguous', 'one.bin', 'one.bin', 'Active', 'migration-rehearsal', NOW(), NOW()),
                (@file2, 'wave-7a-ambiguous', 'two.bin', 'two.bin', 'Active', 'migration-rehearsal', NOW(), NOW());
            INSERT INTO file_versions
                (id, file_asset_id, managed_object_id, version_number, content_sha256, content_type,
                 deduplication_scope_key, lifecycle, classification, created_at, updated_at)
            VALUES
                (@version1, @file1, @object1, 1, @hash, 'application/octet-stream', repeat('5', 64), 'IntegrityVerified', 'Restricted', NOW(), NOW()),
                (@version2, @file2, @object2, 1, @hash, 'application/octet-stream', repeat('6', 64), 'IntegrityVerified', 'Restricted', NOW(), NOW());
            INSERT INTO memory_items
                (id, external_key, scope, memory_type, title, content, summary, tags, source_type, source_ref,
                 importance, confidence, status, metadata_json, project_id, created_at, updated_at)
            VALUES
                (@artifact_id, 'wave7a-ambiguous', 'Project', 'Artifact', 'Ambiguous file reference', 'legacy', 'legacy', '{}',
                 'project-artifact-exchange', 'rehearsal', 0.5, 0.5, 'Active', @metadata, 'wave-7a-ambiguous', NOW(), NOW());
            """,
            new NpgsqlParameter<Guid>("object1", Guid.NewGuid()), new NpgsqlParameter<Guid>("object2", Guid.NewGuid()),
            new NpgsqlParameter<Guid>("file1", Guid.NewGuid()), new NpgsqlParameter<Guid>("file2", Guid.NewGuid()),
            new NpgsqlParameter<Guid>("version1", Guid.NewGuid()), new NpgsqlParameter<Guid>("version2", Guid.NewGuid()),
            new NpgsqlParameter<Guid>("artifact_id", artifactId), new NpgsqlParameter<string>("hash", hash),
            new NpgsqlParameter<string>("metadata", $$$"""{"artifactExchange":true,"kind":"FileReference","objectRef":{"sha256":"{{{hash}}}"}}"""));

        var apply = () => ApplyRemainingAsync(connection, migrations);
        (await apply.Should().ThrowAsync<PostgresException>()).Which.MessageText.Should().Contain("ambiguous FileReference mapping");
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM schema_migrations WHERE name = '051_legacy_artifact_managed_file_cutover.sql';")).Should().Be(0);
        (await ScalarAsync<long>(connection, "SELECT COUNT(*) FROM memory_items WHERE id = @id AND status = 'Active' AND metadata_json::jsonb ? 'objectRef';", new NpgsqlParameter<Guid>("id", artifactId))).Should().Be(1);
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

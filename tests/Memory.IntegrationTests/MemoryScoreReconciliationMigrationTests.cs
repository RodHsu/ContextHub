using System.Globalization;
using System.Text.RegularExpressions;
using FluentAssertions;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed partial class MemoryScoreReconciliationMigrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Exact_manifest_is_archived_read_back_removed_and_replay_safe_before_042()
    {
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var reconciliationTransaction = await connection.BeginTransactionAsync();
        await ResetReconciliationAsync(connection, reconciliationTransaction);

        var reconciliationSql = ReadMigration(".041a_memory_score_reconciliation.sql");
        var manifest = ParseManifest(reconciliationSql);
        await InsertManifestRowsAsync(connection, reconciliationTransaction, manifest);
        await InsertChildAggregateFixtureAsync(connection, reconciliationTransaction, manifest[0], manifest[1]);

        await ExecuteAsync(connection, reconciliationTransaction, reconciliationSql);

        (await ScalarAsync<long>(connection, reconciliationTransaction,
            "SELECT COUNT(*) FROM memory_score_reconciliation_quarantine;")).Should().Be(33);
        (await ScalarAsync<long>(connection, reconciliationTransaction,
            "SELECT COUNT(*) FROM memory_score_reconciliation_quarantine WHERE evidence_class = 'RequiresHumanDecision' AND replacement_memory_id IS NULL;")).Should().Be(33);
        (await ScalarAsync<long>(connection, reconciliationTransaction,
            "SELECT COUNT(*) FROM memory_items WHERE importance < 0 OR importance > 1 OR confidence < 0 OR confidence > 1;")).Should().Be(0);
        (await ScalarAsync<long>(connection, reconciliationTransaction,
            "SELECT jsonb_array_length(related_payload->'revisions') FROM memory_score_reconciliation_quarantine WHERE source_memory_id = @id;",
            new NpgsqlParameter<Guid>("id", manifest[0].Id))).Should().Be(1);
        (await ScalarAsync<long>(connection, reconciliationTransaction,
            "SELECT jsonb_array_length(related_payload->'chunks') FROM memory_score_reconciliation_quarantine WHERE source_memory_id = @id;",
            new NpgsqlParameter<Guid>("id", manifest[0].Id))).Should().Be(1);
        (await ScalarAsync<long>(connection, reconciliationTransaction,
            "SELECT jsonb_array_length(related_payload->'vectors') FROM memory_score_reconciliation_quarantine WHERE source_memory_id = @id;",
            new NpgsqlParameter<Guid>("id", manifest[0].Id))).Should().Be(1);
        (await ScalarAsync<long>(connection, reconciliationTransaction,
            "SELECT jsonb_array_length(related_payload->'links') FROM memory_score_reconciliation_quarantine WHERE source_memory_id = @id;",
            new NpgsqlParameter<Guid>("id", manifest[0].Id))).Should().Be(1);
        (await ScalarAsync<long>(connection, reconciliationTransaction,
            "SELECT jsonb_array_length(related_payload->'retentionStates') FROM memory_score_reconciliation_quarantine WHERE source_memory_id = @id;",
            new NpgsqlParameter<Guid>("id", manifest[0].Id))).Should().Be(1);

        await reconciliationTransaction.CommitAsync();

        await using var constraintTransaction = await connection.BeginTransactionAsync();
        await ExecuteAsync(connection, constraintTransaction, ReadMigration(".042_memory_score_contract.sql"));
        (await ScalarAsync<long>(connection, constraintTransaction, """
            SELECT COUNT(*)
            FROM pg_constraint
            WHERE conname IN (
                'ck_memory_items_importance_normalized',
                'ck_memory_items_confidence_normalized',
                'ck_conversation_insights_importance_normalized',
                'ck_conversation_insights_confidence_normalized');
            """)).Should().Be(4);

        await ExecuteAsync(connection, constraintTransaction, reconciliationSql);
        (await ScalarAsync<long>(connection, constraintTransaction,
            "SELECT COUNT(*) FROM memory_score_reconciliation_quarantine;")).Should().Be(33);

        await AssertRejectedAtSavepointAsync(connection, constraintTransaction,
            "UPDATE memory_score_reconciliation_quarantine SET evidence_class = 'Changed';");
        await AssertRejectedAtSavepointAsync(connection, constraintTransaction,
            "DELETE FROM memory_score_reconciliation_quarantine;");
        await AssertRejectedAtSavepointAsync(connection, constraintTransaction,
            "TRUNCATE memory_score_reconciliation_quarantine;");
        await AssertRejectedAtSavepointAsync(connection, constraintTransaction,
            "UPDATE memory_score_reconciliation_runs SET archived_source_count = 0;");
        await AssertRejectedAtSavepointAsync(connection, constraintTransaction,
            "DELETE FROM memory_score_reconciliation_runs;");
        await AssertRejectedAtSavepointAsync(connection, constraintTransaction,
            "TRUNCATE memory_score_reconciliation_runs;");
        await AssertRejectedAtSavepointAsync(connection, constraintTransaction, """
            INSERT INTO memory_score_reconciliation_quarantine
                (reconciliation_key, source_memory_id, source_external_key,
                 stored_importance, stored_confidence, evidence_class,
                 replacement_memory_id, source_payload, related_payload, payload_hash)
            VALUES
                ('memory-score-contract-2026-09-13-v1', gen_random_uuid(), 'invalid-replacement',
                 4, 0.95, 'RequiresHumanDecision', gen_random_uuid(), '{}'::jsonb, '{}'::jsonb,
                 md5('{}'::jsonb::text || '{}'::jsonb::text));
            """);

        await constraintTransaction.RollbackAsync();
    }

    [DockerRequiredFact]
    public async Task Clean_nonempty_database_is_an_explicit_no_op()
    {
        var sql = ReadMigration(".041a_memory_score_reconciliation.sql");
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetReconciliationAsync(connection, transaction);

        var canonical = new ManifestRow(Guid.NewGuid(), $"canonical-{Guid.NewGuid():N}", 0.7m);
        await InsertMemoryAsync(connection, transaction, canonical);

        await ExecuteAsync(connection, transaction, sql);

        (await ScalarAsync<string>(connection, transaction,
            "SELECT outcome FROM memory_score_reconciliation_runs;")).Should().Be("CleanDatabaseNoOp");
        (await ScalarAsync<long>(connection, transaction,
            "SELECT COUNT(*) FROM memory_items WHERE id = @id;",
            new NpgsqlParameter<Guid>("id", canonical.Id))).Should().Be(1);
        (await ScalarAsync<long>(connection, transaction,
            "SELECT COUNT(*) FROM memory_score_reconciliation_quarantine;")).Should().Be(0);

        await transaction.RollbackAsync();
    }

    [DockerRequiredFact]
    public async Task Missing_manifest_row_fails_closed_without_archival()
    {
        var sql = ReadMigration(".041a_memory_score_reconciliation.sql");
        var manifest = ParseManifest(sql);
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetReconciliationAsync(connection, transaction);
        await InsertManifestRowsAsync(connection, transaction, manifest[..^1]);

        var exception = (await ((Func<Task>)(() => ExecuteAsync(connection, transaction, sql)))
                .Should().ThrowAsync<PostgresException>())
            .Which;
        exception.MessageText.Should().Contain("differs from the exact 33-row reconciliation manifest");
        await transaction.RollbackAsync();
    }

    [DockerRequiredFact]
    public async Task Thirty_fourth_malformed_row_fails_closed_without_archival()
    {
        var sql = ReadMigration(".041a_memory_score_reconciliation.sql");
        var manifest = ParseManifest(sql);
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetReconciliationAsync(connection, transaction);
        await InsertManifestRowsAsync(connection, transaction, manifest);
        await InsertMemoryAsync(connection, transaction,
            new ManifestRow(Guid.NewGuid(), $"unexpected-{Guid.NewGuid():N}", 4m));

        var exception = (await ((Func<Task>)(() => ExecuteAsync(connection, transaction, sql)))
                .Should().ThrowAsync<PostgresException>())
            .Which;
        exception.MessageText.Should().Contain("differs from the exact 33-row reconciliation manifest");
        await transaction.RollbackAsync();
    }

    [DockerRequiredFact]
    public async Task Malformed_conversation_insight_fails_closed_without_archival()
    {
        var sql = ReadMigration(".041a_memory_score_reconciliation.sql");
        var manifest = ParseManifest(sql);
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetReconciliationAsync(connection, transaction);
        await InsertManifestRowsAsync(connection, transaction, manifest);
        await InsertMalformedInsightAsync(connection, transaction);

        var exception = (await ((Func<Task>)(() => ExecuteAsync(connection, transaction, sql)))
                .Should().ThrowAsync<PostgresException>())
            .Which;
        exception.MessageText.Should().Contain("refuses malformed conversation insight scores");
        await transaction.RollbackAsync();
    }

    [DockerRequiredFact]
    public async Task Partial_replay_payload_fails_closed()
    {
        var sql = ReadMigration(".041a_memory_score_reconciliation.sql");
        var reconciliationBlock = sql.IndexOf("\nDO $$", StringComparison.Ordinal);
        reconciliationBlock.Should().BeGreaterThan(0);

        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await ResetReconciliationAsync(connection, transaction);
        await ExecuteAsync(connection, transaction, sql[..reconciliationBlock]);
        await ExecuteAsync(connection, transaction, """
            INSERT INTO memory_score_reconciliation_runs
                (reconciliation_key, implementation_plan_id, authority_artifact_id,
                 authority_fact_id, master_work_item_id, successor_target_id,
                 source_commit, manifest_hash, outcome, expected_source_count,
                 archived_source_count, replacement_count, aggregate_counts)
            SELECT
                'memory-score-contract-2026-09-13-v1',
                '3bbcc307-50b9-4f18-ad01-439f166feed3',
                'edf09285-cc6b-482b-8bac-f4568b62728e',
                'e311bfc7-08f8-42e9-84ec-1a97f85258dc',
                'e1eac3fb-b506-418c-9408-61ae68b522d8',
                '789bd766-5623-4137-a566-e8531b6b08af',
                '773878ce0aacdb55d6fff3c7e9a74977f5df2675',
                md5(string_agg(source_memory_id::text || '|' || source_external_key || '|' || stored_importance::text,
                    E'\n' ORDER BY source_memory_id)),
                'ArchivedRequiresHumanDecision', 33, 33, 0, '{}'::jsonb
            FROM expected_memory_score_reconciliation;

            INSERT INTO memory_score_reconciliation_quarantine
                (reconciliation_key, source_memory_id, source_external_key,
                 stored_importance, stored_confidence, evidence_class,
                 replacement_memory_id, source_payload, related_payload, payload_hash)
            SELECT
                'memory-score-contract-2026-09-13-v1', source_memory_id, source_external_key,
                stored_importance, 0.95, 'RequiresHumanDecision', NULL,
                jsonb_build_object('id', source_memory_id, 'external_key', source_external_key,
                    'importance', stored_importance, 'confidence', 0.95),
                '{}'::jsonb,
                md5(jsonb_build_object('id', source_memory_id, 'external_key', source_external_key,
                    'importance', stored_importance, 'confidence', 0.95)::text || '{}'::jsonb::text)
            FROM expected_memory_score_reconciliation
            ORDER BY source_memory_id
            LIMIT 1;
            """);

        var exception = (await ((Func<Task>)(() => ExecuteAsync(connection, transaction, sql)))
                .Should().ThrowAsync<PostgresException>())
            .Which;
        exception.MessageText.Should().Contain("replay evidence is incomplete or altered");
        await transaction.RollbackAsync();
    }

    private static async Task ResetReconciliationAsync(NpgsqlConnection connection, NpgsqlTransaction transaction)
    {
        await ExecuteAsync(connection, transaction, """
            ALTER TABLE memory_items
                DROP CONSTRAINT IF EXISTS ck_memory_items_importance_normalized,
                DROP CONSTRAINT IF EXISTS ck_memory_items_confidence_normalized;
            ALTER TABLE conversation_insights
                DROP CONSTRAINT IF EXISTS ck_conversation_insights_importance_normalized,
                DROP CONSTRAINT IF EXISTS ck_conversation_insights_confidence_normalized;
            DROP TABLE IF EXISTS memory_score_reconciliation_quarantine;
            DROP TABLE IF EXISTS memory_score_reconciliation_runs;
            DROP FUNCTION IF EXISTS reject_memory_score_reconciliation_mutation();
            """);
    }

    private static async Task InsertManifestRowsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        IReadOnlyList<ManifestRow> rows)
    {
        foreach (var row in rows)
        {
            await InsertMemoryAsync(connection, transaction, row);
        }
    }

    private static async Task InsertMemoryAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ManifestRow row)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO memory_items
                (id, external_key, scope, memory_type, title, content, summary, tags,
                 source_type, source_ref, importance, confidence, version, status,
                 metadata_json, created_at, updated_at, project_id, is_read_only)
            VALUES
                (@id, @key, 'Project', 'Fact', @key, 'legacy payload', 'legacy summary', '{}',
                 'legacy-import', @key, @importance, 0.95, 1, 'Active', '{}', NOW(), NOW(),
                 'score-reconciliation-test', FALSE);
            """,
            new NpgsqlParameter<Guid>("id", row.Id),
            new NpgsqlParameter<string>("key", row.ExternalKey),
            new NpgsqlParameter<decimal>("importance", row.Importance));
    }

    private static async Task InsertChildAggregateFixtureAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        ManifestRow first,
        ManifestRow second)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO memory_item_revisions
                (id, memory_item_id, version, title, content, summary, metadata_json, changed_by, created_at)
            VALUES (gen_random_uuid(), @first, 1, 'legacy', 'legacy', 'legacy', '{}', 'integration-test', NOW());

            INSERT INTO memory_item_chunks
                (id, memory_item_id, chunk_kind, chunk_index, chunk_text, metadata_json, created_at)
            VALUES ('80000000-0000-0000-0000-000000000001', @first, 'Document', 0, 'legacy', '{}', NOW());

            INSERT INTO memory_chunk_vectors
                (id, chunk_id, model_key, dimension, status, embedding, created_at)
            VALUES (gen_random_uuid(), '80000000-0000-0000-0000-000000000001', 'integration-test', 2, 'Active', '[0.1,0.2]'::vector, NOW());

            INSERT INTO memory_links (id, from_id, to_id, link_type, created_at)
            VALUES (gen_random_uuid(), @first, @second, 'Related', NOW());

            INSERT INTO memory_retention_states
                (resource_id, tenant_id, owner_user_id, project_id, resource_type,
                 classification, policy_kind, policy_version, grace_period_days,
                 lifecycle_status, evidence_fingerprint, reason_codes_json,
                 blocked_reasons_json, governance_run_id, created_at, updated_at)
            VALUES
                (@first, '71000000-0000-0000-0000-000000000001',
                 '72000000-0000-0000-0000-000000000001', 'score-reconciliation-test',
                 'Memory', 'Review', 'Test', 'test-v1', 7, 'Active', '', '[]', '[]', '', NOW(), NOW());
            """,
            new NpgsqlParameter<Guid>("first", first.Id),
            new NpgsqlParameter<Guid>("second", second.Id));
    }

    private static async Task InsertMalformedInsightAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction)
    {
        await ExecuteAsync(connection, transaction, """
            INSERT INTO conversation_sessions
                (id, conversation_id, project_id, source_system, started_at,
                 last_checkpoint_at, updated_at)
            VALUES
                ('81000000-0000-0000-0000-000000000001', 'score-reconciliation-insight',
                 'score-reconciliation-test', 'integration-test', NOW(), NOW(), NOW());

            INSERT INTO conversation_checkpoints
                (id, session_id, conversation_id, turn_id, project_id, source_system,
                 event_type, source_kind, source_ref, dedup_key, created_at)
            VALUES
                ('82000000-0000-0000-0000-000000000001',
                 '81000000-0000-0000-0000-000000000001', 'score-reconciliation-insight',
                 'turn-1', 'score-reconciliation-test', 'integration-test', 'Completed',
                 'HostEvent', 'integration-test', 'score-reconciliation-checkpoint', NOW());

            INSERT INTO conversation_insights
                (id, session_id, checkpoint_id, conversation_id, turn_id, project_id,
                 source_system, source_kind, insight_type, title, content, summary,
                 source_ref, importance, confidence, dedup_key, created_at, updated_at)
            VALUES
                ('83000000-0000-0000-0000-000000000001',
                 '81000000-0000-0000-0000-000000000001',
                 '82000000-0000-0000-0000-000000000001', 'score-reconciliation-insight',
                 'turn-1', 'score-reconciliation-test', 'integration-test', 'HostEvent',
                 'Fact', 'Malformed insight', 'Malformed insight', 'Malformed insight',
                 'integration-test', 4, 0.95, 'score-reconciliation-insight', NOW(), NOW());
            """);
    }

    private static async Task AssertRejectedAtSavepointAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql)
    {
        var savepoint = $"before_rejection_{Guid.NewGuid():N}";
        await transaction.SaveAsync(savepoint);
        await ((Func<Task>)(() => ExecuteAsync(connection, transaction, sql)))
            .Should().ThrowAsync<PostgresException>();
        await transaction.RollbackAsync(savepoint);
    }

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        string sql,
        params NpgsqlParameter[] parameters)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.Parameters.AddRange(parameters);
        var value = await command.ExecuteScalarAsync()
            ?? throw new InvalidOperationException("Expected a scalar database value.");
        return (T)Convert.ChangeType(value, typeof(T), CultureInfo.InvariantCulture);
    }

    private static ManifestRow[] ParseManifest(string sql)
        => ManifestRowRegex().Matches(sql)
            .Select(match => new ManifestRow(
                Guid.Parse(match.Groups["id"].Value),
                match.Groups["key"].Value.Replace("''", "'", StringComparison.Ordinal),
                decimal.Parse(match.Groups["importance"].Value, CultureInfo.InvariantCulture)))
            .ToArray();

    private static string ReadMigration(string suffix)
    {
        var assembly = typeof(MemoryDbContext).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(suffix, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private sealed record ManifestRow(Guid Id, string ExternalKey, decimal Importance);

    [GeneratedRegex(@"\('(?<id>[0-9a-f-]{36})',\s*'(?<key>(?:''|[^'])+)',\s*(?<importance>[0-9]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex ManifestRowRegex();
}

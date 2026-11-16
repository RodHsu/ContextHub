using FluentAssertions;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class CacheReleaseMigrationTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Adjudication_before_cache_migrations_must_preserve_both_receipts_and_append_only_ledger()
        => await RehearseAsync(adjudicationFirst: true);

    [DockerRequiredFact]
    public async Task Adjudication_after_cache_migrations_must_preserve_both_receipts_and_append_only_ledger()
        => await RehearseAsync(adjudicationFirst: false);

    private async Task RehearseAsync(bool adjudicationFirst)
    {
        // A database owned by this isolated testcontainer, never a configured Production target.
        var database = "cache_p5_" + Guid.NewGuid().ToString("N");
        await using var admin = NpgsqlDataSource.Create(environment.PostgresConnectionString!);
        await using (var create = admin.CreateCommand($"CREATE DATABASE {database}"))
            await create.ExecuteNonQueryAsync();
        var connection = new NpgsqlConnectionStringBuilder(environment.PostgresConnectionString!) { Database = database };
        try
        {
            await using var source = NpgsqlDataSource.Create(connection.ConnectionString);
            await using (var ledger = source.CreateCommand("CREATE TABLE schema_migrations(name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())"))
                await ledger.ExecuteNonQueryAsync();
            var assembly = typeof(DatabaseMigrationHostedService).Assembly;
            foreach (var resource in assembly.GetManifestResourceNames()
                         .Where(x => x.Contains(".Sql.Migrations.", StringComparison.Ordinal) && x.EndsWith(".sql", StringComparison.Ordinal))
                         .Order(StringComparer.Ordinal))
            {
                var name = resource[(resource.LastIndexOf(".Sql.Migrations.", StringComparison.Ordinal) + ".Sql.Migrations.".Length)..];
                if (string.CompareOrdinal(name[..3], "051") > 0) continue;
                using var reader = new StreamReader(assembly.GetManifestResourceStream(resource)!);
                await ApplyAsync(source, name, await reader.ReadToEndAsync());
            }
            var migrator = new DatabaseMigrationHostedService(source, NullLogger<DatabaseMigrationHostedService>.Instance);
            if (adjudicationFirst) await AdjudicationAsync(source);
            await migrator.StartAsync(default);
            if (!adjudicationFirst) await AdjudicationAsync(source);
            await using var beforeCommand = source.CreateCommand("SELECT revision FROM cache_scope_revisions WHERE scope = 'global'");
            var before = (long)(await beforeCommand.ExecuteScalarAsync())!;
            await migrator.StartAsync(default);
            await migrator.StartAsync(default);
            (await beforeCommand.ExecuteScalarAsync()).Should().Be(before);
            await using var receipts = source.CreateCommand("SELECT count(*) FROM schema_migrations WHERE name ~ '^05[2345]_'");
            (await receipts.ExecuteScalarAsync()).Should().Be(4L);
            await using var guards = source.CreateCommand("""
                SELECT count(*) FROM pg_trigger
                WHERE tgname IN ('conversation_adjudications_append_only', 'capture_cache_revision')
                  AND NOT tgisinternal;
                """);
            (await guards.ExecuteScalarAsync()).Should().Be(8L);
            // Empty databases have no business insights. Verify the trigger definition and
            // immutable function instead of manufacturing unrelated business governance rows.
            await using var definition = source.CreateCommand("SELECT pg_get_functiondef('reject_conversation_adjudication_change()'::regprocedure)");
            ((string)(await definition.ExecuteScalarAsync())!).Should().Contain("append-only");
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP DATABASE {database} WITH (FORCE)");
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task AdjudicationAsync(NpgsqlDataSource source)
    {
        var assembly = typeof(CacheReleaseMigrationTests).Assembly;
        using var reader = new StreamReader(assembly.GetManifestResourceStream("Memory.IntegrationTests.Fixtures.052_conversation_adjudication.sql")!);
        await ApplyAsync(source, "052_conversation_adjudication.sql", await reader.ReadToEndAsync());
    }

    private static async Task ApplyAsync(NpgsqlDataSource source, string name, string sql)
    {
        await using var connection = await source.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var apply = new NpgsqlCommand(sql, connection, transaction) { CommandTimeout = 300 };
        await apply.ExecuteNonQueryAsync();
        await using var receipt = new NpgsqlCommand("INSERT INTO schema_migrations(name) VALUES (@name)", connection, transaction);
        receipt.Parameters.AddWithValue("name", name);
        await receipt.ExecuteNonQueryAsync();
        await transaction.CommitAsync();
    }
}

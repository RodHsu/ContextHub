using FluentAssertions;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Identity;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class DashboardSeedCredentialMigrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Fresh_database_disables_the_seed_password_and_replay_preserves_a_rotated_password()
    {
        const string seedId = "73000000-0000-0000-0000-000000000001";
        const string migration = "061_clear_legacy_dashboard_seed_credential.sql";

        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var seed = new NpgsqlCommand("""
            SELECT u.password_hash = '', u.password_updated_at IS NULL, u.role, u.status, t.slug
            FROM tenant_users u
            JOIN tenants t ON t.id = u.tenant_id
            WHERE u.id = @id;
            """, connection, transaction))
        {
            seed.Parameters.AddWithValue("id", Guid.Parse(seedId));
            await using var reader = await seed.ExecuteReaderAsync();
            (await reader.ReadAsync()).Should().BeTrue();
            reader.GetBoolean(0).Should().BeTrue("the historical owner requires configured credentials on a fresh database");
            reader.GetBoolean(1).Should().BeTrue();
            reader.GetString(2).Should().Be("Owner");
            reader.GetString(3).Should().Be("Active");
            reader.GetString(4).Should().Be("context-team");
        }

        await using (var applied = new NpgsqlCommand(
            "SELECT count(*) FROM schema_migrations WHERE name = @name;", connection, transaction))
        {
            applied.Parameters.AddWithValue("name", migration);
            (await applied.ExecuteScalarAsync()).Should().Be(1L);
        }

        var rotatedHash = new PasswordHasher<object>().HashPassword(new object(), Guid.NewGuid().ToString("N"));
        await using (var rotate = new NpgsqlCommand("""
            UPDATE tenant_users
            SET password_hash = @hash, password_updated_at = now()
            WHERE id = @id;
            """, connection, transaction))
        {
            rotate.Parameters.AddWithValue("hash", rotatedHash);
            rotate.Parameters.AddWithValue("id", Guid.Parse(seedId));
            (await rotate.ExecuteNonQueryAsync()).Should().Be(1);
        }

        var assembly = typeof(DatabaseMigrationHostedService).Assembly;
        var migrationResource = assembly.GetManifestResourceNames().Single(name => name.EndsWith("." + migration, StringComparison.Ordinal));
        using var migrationReader = new StreamReader(assembly.GetManifestResourceStream(migrationResource)!);
        await using (var replay = new NpgsqlCommand(await migrationReader.ReadToEndAsync(), connection, transaction))
        {
            (await replay.ExecuteNonQueryAsync()).Should().Be(0);
        }
    }
}

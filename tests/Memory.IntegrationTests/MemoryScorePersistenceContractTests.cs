using System.Data;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class MemoryScorePersistenceContractTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    private static readonly decimal[] InvalidValues = [-1m, 1.01m, 95m, 100m, 101m];

    [DockerRequiredFact]
    public async Task Persistence_enforces_canonical_scores_before_SQL_and_with_four_database_checks()
    {
        using var scope = environment.GetFactory().Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var expectedConstraints = new[]
        {
            "ck_memory_items_importance_normalized",
            "ck_memory_items_confidence_normalized",
            "ck_conversation_insights_importance_normalized",
            "ck_conversation_insights_confidence_normalized"
        };

        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync();
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT conname
                FROM pg_constraint
                WHERE contype = 'c'
                  AND conname = ANY (@constraint_names)
                ORDER BY conname;
                """;
            command.Parameters.Add(new NpgsqlParameter<string[]>("constraint_names", expectedConstraints));
            var actualConstraints = new List<string>();
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actualConstraints.Add(reader.GetString(0));
            }

            actualConstraints.Should().BeEquivalentTo(expectedConstraints);
        }

        var valid = CreateMemoryItem(0.95m, 0.95m);
        db.MemoryItems.Add(valid);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        (await db.MemoryItems.AsNoTracking().SingleAsync(item => item.Id == valid.Id))
            .Should().Match<MemoryItem>(item => item.Importance == 0.95m && item.Confidence == 0.95m);

        foreach (var invalidValue in InvalidValues)
        {
            foreach (var field in new[] { "importance", "confidence" })
            {
                await AssertTrackedMemoryItemRejectedWithoutRowAsync(db, field, invalidValue);
                await AssertTrackedConversationInsightRejectedWithoutRowAsync(db, field, invalidValue);
            }
        }

        await AssertDatabaseCheckRejectsRepresentableInvalidValueAsync(valid.Id);
        await AssertMigrationRejectsHistoricalMalformedRowWithoutChangingItAsync(valid.Id);
    }

    private async Task AssertDatabaseCheckRejectsRepresentableInvalidValueAsync(Guid memoryItemId)
    {
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE memory_items SET importance = 1.01 WHERE id = @id;";
        command.Parameters.Add(new NpgsqlParameter<Guid>("id", memoryItemId));

        var exception = (await ((Func<Task>)(async () => await command.ExecuteNonQueryAsync()))
                .Should().ThrowAsync<PostgresException>())
            .Which;

        exception.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        exception.SqlState.Should().NotBe(PostgresErrorCodes.NumericValueOutOfRange);
        exception.ConstraintName.Should().Be("ck_memory_items_importance_normalized");
        await transaction.RollbackAsync();
    }

    private async Task AssertMigrationRejectsHistoricalMalformedRowWithoutChangingItAsync(Guid memoryItemId)
    {
        await using var connection = new NpgsqlConnection(environment.PostgresConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using (var arrange = connection.CreateCommand())
        {
            arrange.Transaction = transaction;
            arrange.CommandText = """
                ALTER TABLE memory_items
                    DROP CONSTRAINT ck_memory_items_importance_normalized;
                UPDATE memory_items
                SET importance = 1.01
                WHERE id = @id;
                """;
            arrange.Parameters.Add(new NpgsqlParameter<Guid>("id", memoryItemId));
            await arrange.ExecuteNonQueryAsync();
        }

        await transaction.SaveAsync("before_migration_preflight");
        var migrationSql = ReadMigrationSql();
        await using (var migrate = connection.CreateCommand())
        {
            migrate.Transaction = transaction;
            migrate.CommandText = migrationSql;
            var exception = (await ((Func<Task>)(async () => await migrate.ExecuteNonQueryAsync()))
                    .Should().ThrowAsync<PostgresException>())
                .Which;

            exception.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
            exception.MessageText.Should().Contain("historical memory scores are outside canonical [0,1]");
        }

        await transaction.RollbackAsync("before_migration_preflight");
        await using (var verify = connection.CreateCommand())
        {
            verify.Transaction = transaction;
            verify.CommandText = "SELECT importance FROM memory_items WHERE id = @id;";
            verify.Parameters.Add(new NpgsqlParameter<Guid>("id", memoryItemId));
            ((decimal)(await verify.ExecuteScalarAsync())!).Should().Be(1.01m);
        }

        await transaction.RollbackAsync();
    }

    private static string ReadMigrationSql()
    {
        var assembly = typeof(MemoryDbContext).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".042_memory_score_contract.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration resource '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static async Task AssertTrackedMemoryItemRejectedWithoutRowAsync(
        MemoryDbContext db,
        string field,
        decimal value)
    {
        var item = CreateMemoryItem(0.5m, 0.5m);
        if (field == "importance")
        {
            item.Importance = value;
        }
        else
        {
            item.Confidence = value;
        }

        db.MemoryItems.Add(item);
        var exception = (await ((Func<Task>)(() => db.SaveChangesAsync()))
                .Should().ThrowAsync<MemoryScoreValidationException>())
            .Which;
        exception.Field.Should().Be(field);
        exception.NumericValue.Should().Be(value);
        db.ChangeTracker.Clear();
        (await db.MemoryItems.AsNoTracking().AnyAsync(candidate => candidate.Id == item.Id)).Should().BeFalse();
    }

    private static async Task AssertTrackedConversationInsightRejectedWithoutRowAsync(
        MemoryDbContext db,
        string field,
        decimal value)
    {
        var insight = CreateConversationInsight(0.5m, 0.5m);
        if (field == "importance")
        {
            insight.Importance = value;
        }
        else
        {
            insight.Confidence = value;
        }

        db.ConversationInsights.Add(insight);
        var exception = (await ((Func<Task>)(() => db.SaveChangesAsync()))
                .Should().ThrowAsync<MemoryScoreValidationException>())
            .Which;
        exception.Field.Should().Be(field);
        exception.NumericValue.Should().Be(value);
        db.ChangeTracker.Clear();
        (await db.ConversationInsights.AsNoTracking().AnyAsync(candidate => candidate.Id == insight.Id)).Should().BeFalse();
    }

    private static MemoryItem CreateMemoryItem(decimal importance, decimal confidence)
        => new()
        {
            ProjectId = "numeric-contract-integration",
            ExternalKey = $"numeric-contract:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = "Numeric contract integration",
            Content = "Persistence contract integration fixture.",
            Summary = "Persistence contract integration fixture.",
            Importance = importance,
            Confidence = confidence,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ConversationInsight CreateConversationInsight(decimal importance, decimal confidence)
        => new()
        {
            SessionId = Guid.NewGuid(),
            CheckpointId = Guid.NewGuid(),
            ConversationId = $"numeric-contract-{Guid.NewGuid():N}",
            TurnId = "turn-1",
            ProjectId = "numeric-contract-integration",
            SourceSystem = "integration-test",
            SourceKind = ConversationSourceKind.HostEvent,
            InsightType = ConversationInsightType.Fact,
            Title = "Numeric contract integration",
            Content = "Persistence contract integration fixture.",
            Summary = "Persistence contract integration fixture.",
            SourceRef = "integration-test",
            Importance = importance,
            Confidence = confidence,
            DedupKey = $"numeric-contract:{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
}

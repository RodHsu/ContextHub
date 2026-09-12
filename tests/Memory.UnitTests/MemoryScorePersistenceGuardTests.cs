using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Memory.UnitTests;

public sealed class MemoryScorePersistenceGuardTests
{
    public static TheoryData<string, decimal> InvalidScoreMatrix => new()
    {
        { "importance", -1m },
        { "importance", 1.01m },
        { "importance", 95m },
        { "importance", 100m },
        { "importance", 101m },
        { "confidence", -1m },
        { "confidence", 1.01m },
        { "confidence", 95m },
        { "confidence", 100m },
        { "confidence", 101m }
    };

    [Theory]
    [MemberData(nameof(InvalidScoreMatrix))]
    public async Task SaveChangesAsync_rejects_invalid_tracked_memory_item_before_provider_execution(
        string field,
        decimal value)
    {
        await using var db = CreateDbContext();
        var item = CreateMemoryItem();
        SetScore(item, field, value);
        db.MemoryItems.Add(item);

        var exception = (await ((Func<Task>)(() => db.SaveChangesAsync()))
                .Should().ThrowAsync<MemoryScoreValidationException>())
            .Which;

        exception.Field.Should().Be(field);
        exception.NumericValue.Should().Be(value);
        exception.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
        exception.Message.Should().Contain("expectedRange=[0,1]");
        exception.Message.Should().Contain("normalization=forbidden");
    }

    [Theory]
    [MemberData(nameof(InvalidScoreMatrix))]
    public async Task SaveChangesAsync_rejects_invalid_tracked_conversation_insight_before_provider_execution(
        string field,
        decimal value)
    {
        await using var db = CreateDbContext();
        var insight = CreateConversationInsight();
        SetScore(insight, field, value);
        db.ConversationInsights.Add(insight);

        var exception = (await ((Func<Task>)(() => db.SaveChangesAsync()))
                .Should().ThrowAsync<MemoryScoreValidationException>())
            .Which;

        exception.Field.Should().Be(field);
        exception.NumericValue.Should().Be(value);
        exception.Code.Should().Be(MemoryScoreContract.InvalidRangeCode);
    }

    [Fact]
    public void SaveChanges_rejects_invalid_tracked_score_before_provider_execution()
    {
        using var db = CreateDbContext();
        var item = CreateMemoryItem();
        item.Importance = 95m;
        db.MemoryItems.Add(item);

        var exception = ((Action)(() => db.SaveChanges()))
            .Should().Throw<MemoryScoreValidationException>()
            .Which;

        exception.Field.Should().Be("importance");
        exception.NumericValue.Should().Be(95m);
    }

    [Fact]
    public void Migration_preflights_historical_rows_without_normalizing_or_deleting_them()
    {
        var assembly = typeof(MemoryDbContext).Assembly;
        var resourceName = assembly.GetManifestResourceNames()
            .Single(name => name.EndsWith(".042_memory_score_contract.sql", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resourceName);
        stream.Should().NotBeNull();
        using var reader = new StreamReader(stream!);
        var sql = reader.ReadToEnd();

        sql.IndexOf("IF malformed_memory_items > 0", StringComparison.Ordinal)
            .Should().BeLessThan(sql.IndexOf("ALTER TABLE memory_items", StringComparison.Ordinal));
        sql.Should().Contain("RAISE EXCEPTION");
        sql.Should().Contain("never normalizes or deletes stored values");
        var normalizedSql = sql.ToUpperInvariant();
        normalizedSql.Should().NotContain("UPDATE MEMORY_ITEMS");
        normalizedSql.Should().NotContain("UPDATE CONVERSATION_INSIGHTS");
        normalizedSql.Should().NotContain("DELETE FROM");
    }

    private static MemoryDbContext CreateDbContext()
    {
        var options = new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unused;Username=unused;Password=unused;Timeout=1")
            .Options;
        return new MemoryDbContext(options);
    }

    private static MemoryItem CreateMemoryItem()
        => new()
        {
            ProjectId = "numeric-contract-test",
            ExternalKey = $"numeric-contract:{Guid.NewGuid():N}",
            Scope = MemoryScope.Project,
            MemoryType = MemoryType.Fact,
            Title = "Numeric contract test",
            Content = "Persistence guard test.",
            Summary = "Persistence guard test.",
            Importance = 0.5m,
            Confidence = 0.5m,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static ConversationInsight CreateConversationInsight()
        => new()
        {
            SessionId = Guid.NewGuid(),
            CheckpointId = Guid.NewGuid(),
            ConversationId = $"numeric-contract-{Guid.NewGuid():N}",
            TurnId = "turn-1",
            ProjectId = "numeric-contract-test",
            SourceSystem = "unit-test",
            SourceKind = ConversationSourceKind.HostEvent,
            InsightType = ConversationInsightType.Fact,
            Title = "Numeric contract test",
            Content = "Persistence guard test.",
            Summary = "Persistence guard test.",
            SourceRef = "unit-test",
            Importance = 0.5m,
            Confidence = 0.5m,
            DedupKey = $"numeric-contract:{Guid.NewGuid():N}",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };

    private static void SetScore(MemoryItem item, string field, decimal value)
    {
        if (field == "importance")
        {
            item.Importance = value;
            return;
        }

        item.Confidence = value;
    }

    private static void SetScore(ConversationInsight insight, string field, decimal value)
    {
        if (field == "importance")
        {
            insight.Importance = value;
            return;
        }

        insight.Confidence = value;
    }
}

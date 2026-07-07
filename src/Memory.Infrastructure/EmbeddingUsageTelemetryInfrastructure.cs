using System.Globalization;
using System.Text.Json;
using Memory.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Memory.Infrastructure;

public sealed class DatabaseEmbeddingUsageTelemetry(
    NpgsqlDataSource dataSource,
    IOptions<DatabaseLoggingOptions> loggingOptions,
    TimeProvider timeProvider,
    ILogger<DatabaseEmbeddingUsageTelemetry> logger) : IEmbeddingUsageTelemetry
{
    private const int TopGroupLimit = 8;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly HistogramBucket[] Buckets =
    [
        new("lte128", 128),
        new("lte256", 256),
        new("lte384", 384),
        new("lte512", 512),
        new("lte768", 768),
        new("lte1024", 1024),
        new("lte1536", 1536),
        new("gt1536", int.MaxValue)
    ];

    private readonly string _serviceName = NormalizeDimension(loggingOptions.Value.ServiceName, "memory-service");
    private DateTimeOffset _lastWriteWarningAtUtc = DateTimeOffset.MinValue;
    private DateTimeOffset _lastReadWarningAtUtc = DateTimeOffset.MinValue;

    public async Task RecordAsync(IReadOnlyList<EmbeddingUsageTelemetryItem> items, CancellationToken cancellationToken)
    {
        if (items.Count == 0)
        {
            return;
        }

        try
        {
            var groups = items
                .GroupBy(item => new EmbeddingUsageGroupKey(
                    TruncateToHour(item.CreatedAtUtc),
                    _serviceName,
                    NormalizeDimension(item.Provider, "unknown"),
                    NormalizeDimension(item.Profile, "unknown"),
                    item.Purpose.ToString(),
                    NormalizeDimension(item.SourceKind, "unknown"),
                    Math.Max(0, item.MaxTokens)))
                .ToArray();

            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            foreach (var group in groups)
            {
                await UpsertGroupAsync(connection, group.Key, group.ToArray(), cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            var now = timeProvider.GetUtcNow();
            if (now - _lastWriteWarningAtUtc >= TimeSpan.FromMinutes(5))
            {
                _lastWriteWarningAtUtc = now;
                logger.LogWarning(ex, "Embedding usage telemetry write failed; embedding request will continue.");
            }
            else
            {
                logger.LogDebug(ex, "Embedding usage telemetry write failure suppressed.");
            }
        }
    }

    public async Task<IReadOnlyList<EmbeddingUsageWindowResult>> GetWindowsAsync(DateTimeOffset observedAtUtc, CancellationToken cancellationToken)
    {
        var rows = Array.Empty<EmbeddingUsageAggregateRow>();
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 20;
            command.CommandText = """
                SELECT
                    bucket_start_utc,
                    service_name,
                    provider,
                    profile,
                    purpose,
                    source_kind,
                    max_tokens,
                    total_inputs,
                    truncated_inputs,
                    total_token_count,
                    total_truncated_tokens,
                    max_token_count,
                    histogram_json::text
                FROM embedding_usage_hourly
                WHERE bucket_start_utc >= @started_at
                  AND bucket_start_utc <= @ended_at;
                """;
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("started_at", observedAtUtc.AddDays(-7)));
            command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("ended_at", observedAtUtc));

            var collected = new List<EmbeddingUsageAggregateRow>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                collected.Add(new EmbeddingUsageAggregateRow(
                    reader.GetFieldValue<DateTimeOffset>(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    reader.GetString(5),
                    reader.GetInt32(6),
                    reader.GetInt64(7),
                    reader.GetInt64(8),
                    reader.GetInt64(9),
                    reader.GetInt64(10),
                    reader.GetInt32(11),
                    ParseHistogram(reader.GetString(12))));
            }

            rows = collected.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            LogReadFailure(ex);
        }
        catch (Exception ex)
        {
            LogReadFailure(ex);
        }

        return
        [
            BuildWindow("24h", "24H", observedAtUtc.AddHours(-24), observedAtUtc, rows),
            BuildWindow("3d", "3D", observedAtUtc.AddDays(-3), observedAtUtc, rows),
            BuildWindow("7d", "7D", observedAtUtc.AddDays(-7), observedAtUtc, rows)
        ];
    }

    private static async Task UpsertGroupAsync(
        NpgsqlConnection connection,
        EmbeddingUsageGroupKey key,
        IReadOnlyList<EmbeddingUsageTelemetryItem> items,
        CancellationToken cancellationToken)
    {
        var histogram = CreateHistogram(items);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 10;
        command.CommandText = """
            INSERT INTO embedding_usage_hourly (
                bucket_start_utc,
                service_name,
                provider,
                profile,
                purpose,
                source_kind,
                max_tokens,
                total_inputs,
                truncated_inputs,
                total_token_count,
                total_truncated_tokens,
                max_token_count,
                histogram_json,
                first_seen_at,
                last_seen_at,
                updated_at)
            VALUES (
                @bucket_start_utc,
                @service_name,
                @provider,
                @profile,
                @purpose,
                @source_kind,
                @max_tokens,
                @total_inputs,
                @truncated_inputs,
                @total_token_count,
                @total_truncated_tokens,
                @max_token_count,
                @histogram_json,
                @first_seen_at,
                @last_seen_at,
                NOW())
            ON CONFLICT (bucket_start_utc, service_name, provider, profile, purpose, source_kind, max_tokens)
            DO UPDATE SET
                total_inputs = embedding_usage_hourly.total_inputs + EXCLUDED.total_inputs,
                truncated_inputs = embedding_usage_hourly.truncated_inputs + EXCLUDED.truncated_inputs,
                total_token_count = embedding_usage_hourly.total_token_count + EXCLUDED.total_token_count,
                total_truncated_tokens = embedding_usage_hourly.total_truncated_tokens + EXCLUDED.total_truncated_tokens,
                max_token_count = GREATEST(embedding_usage_hourly.max_token_count, EXCLUDED.max_token_count),
                histogram_json = jsonb_build_object(
                    'lte128', COALESCE((embedding_usage_hourly.histogram_json->>'lte128')::bigint, 0) + COALESCE((EXCLUDED.histogram_json->>'lte128')::bigint, 0),
                    'lte256', COALESCE((embedding_usage_hourly.histogram_json->>'lte256')::bigint, 0) + COALESCE((EXCLUDED.histogram_json->>'lte256')::bigint, 0),
                    'lte384', COALESCE((embedding_usage_hourly.histogram_json->>'lte384')::bigint, 0) + COALESCE((EXCLUDED.histogram_json->>'lte384')::bigint, 0),
                    'lte512', COALESCE((embedding_usage_hourly.histogram_json->>'lte512')::bigint, 0) + COALESCE((EXCLUDED.histogram_json->>'lte512')::bigint, 0),
                    'lte768', COALESCE((embedding_usage_hourly.histogram_json->>'lte768')::bigint, 0) + COALESCE((EXCLUDED.histogram_json->>'lte768')::bigint, 0),
                    'lte1024', COALESCE((embedding_usage_hourly.histogram_json->>'lte1024')::bigint, 0) + COALESCE((EXCLUDED.histogram_json->>'lte1024')::bigint, 0),
                    'lte1536', COALESCE((embedding_usage_hourly.histogram_json->>'lte1536')::bigint, 0) + COALESCE((EXCLUDED.histogram_json->>'lte1536')::bigint, 0),
                    'gt1536', COALESCE((embedding_usage_hourly.histogram_json->>'gt1536')::bigint, 0) + COALESCE((EXCLUDED.histogram_json->>'gt1536')::bigint, 0)),
                first_seen_at = LEAST(embedding_usage_hourly.first_seen_at, EXCLUDED.first_seen_at),
                last_seen_at = GREATEST(embedding_usage_hourly.last_seen_at, EXCLUDED.last_seen_at),
                updated_at = NOW();
            """;
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("bucket_start_utc", key.BucketStartUtc));
        command.Parameters.Add(new NpgsqlParameter<string>("service_name", key.ServiceName));
        command.Parameters.Add(new NpgsqlParameter<string>("provider", key.Provider));
        command.Parameters.Add(new NpgsqlParameter<string>("profile", key.Profile));
        command.Parameters.Add(new NpgsqlParameter<string>("purpose", key.Purpose));
        command.Parameters.Add(new NpgsqlParameter<string>("source_kind", key.SourceKind));
        command.Parameters.Add(new NpgsqlParameter<int>("max_tokens", key.MaxTokens));
        command.Parameters.Add(new NpgsqlParameter<long>("total_inputs", items.Count));
        command.Parameters.Add(new NpgsqlParameter<long>("truncated_inputs", items.LongCount(static x => x.Truncated)));
        command.Parameters.Add(new NpgsqlParameter<long>("total_token_count", items.Sum(static x => (long)Math.Max(0, x.TokenCount))));
        command.Parameters.Add(new NpgsqlParameter<long>("total_truncated_tokens", items.Sum(x => x.Truncated ? Math.Max(0, x.TokenCount - key.MaxTokens) : 0)));
        command.Parameters.Add(new NpgsqlParameter<int>("max_token_count", items.Max(static x => Math.Max(0, x.TokenCount))));
        command.Parameters.Add(new NpgsqlParameter<string>("histogram_json", JsonSerializer.Serialize(histogram, JsonOptions))
        {
            NpgsqlDbType = NpgsqlDbType.Jsonb
        });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("first_seen_at", items.Min(static x => x.CreatedAtUtc)));
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("last_seen_at", items.Max(static x => x.CreatedAtUtc)));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static EmbeddingUsageWindowResult BuildWindow(
        string key,
        string label,
        DateTimeOffset startedAtUtc,
        DateTimeOffset endedAtUtc,
        IReadOnlyList<EmbeddingUsageAggregateRow> rows)
    {
        var windowRows = rows
            .Where(row => row.BucketStartUtc >= startedAtUtc && row.BucketStartUtc <= endedAtUtc)
            .ToArray();
        var histogram = SumHistogram(windowRows);
        var totalInputs = windowRows.Sum(static x => x.TotalInputs);
        var truncatedInputs = windowRows.Sum(static x => x.TruncatedInputs);
        var maxTokenCount = windowRows.Length == 0 ? 0 : windowRows.Max(static x => x.MaxTokenCount);

        var topGroups = windowRows
            .GroupBy(static x => new { x.ServiceName, x.Provider, x.Profile, x.Purpose, x.SourceKind, x.MaxTokens })
            .Select(group =>
            {
                var groupRows = group.ToArray();
                var groupTotalInputs = groupRows.Sum(static x => x.TotalInputs);
                var groupTruncatedInputs = groupRows.Sum(static x => x.TruncatedInputs);
                var groupHistogram = SumHistogram(groupRows);
                var groupMaxTokenCount = groupRows.Length == 0 ? 0 : groupRows.Max(static x => x.MaxTokenCount);
                return new EmbeddingUsageGroupResult(
                    group.Key.ServiceName,
                    group.Key.Provider,
                    group.Key.Profile,
                    group.Key.Purpose,
                    group.Key.SourceKind,
                    group.Key.MaxTokens,
                    groupTotalInputs,
                    groupTruncatedInputs,
                    CalculateRate(groupTruncatedInputs, groupTotalInputs),
                    ApproximateP95(groupHistogram, groupTotalInputs, groupMaxTokenCount),
                    groupMaxTokenCount);
            })
            .OrderByDescending(group => group.TruncatedInputs)
            .ThenByDescending(group => group.TotalInputs)
            .ThenBy(group => group.ServiceName, StringComparer.Ordinal)
            .Take(TopGroupLimit)
            .ToArray();

        return new EmbeddingUsageWindowResult(
            key,
            label,
            startedAtUtc,
            endedAtUtc,
            totalInputs,
            truncatedInputs,
            CalculateRate(truncatedInputs, totalInputs),
            ApproximateP95(histogram, totalInputs, maxTokenCount),
            maxTokenCount,
            topGroups);
    }

    internal static int ApproximateP95(IReadOnlyDictionary<string, long> histogram, long totalInputs, int maxTokenCount)
    {
        if (totalInputs <= 0)
        {
            return 0;
        }

        var target = Math.Max(1L, (long)Math.Ceiling(totalInputs * 0.95d));
        var cumulative = 0L;
        foreach (var bucket in Buckets)
        {
            cumulative += histogram.GetValueOrDefault(bucket.Key);
            if (cumulative >= target)
            {
                return bucket.UpperBound == int.MaxValue ? maxTokenCount : bucket.UpperBound;
            }
        }

        return maxTokenCount;
    }

    private void LogReadFailure(Exception ex)
    {
        var now = timeProvider.GetUtcNow();
        if (now - _lastReadWarningAtUtc >= TimeSpan.FromMinutes(5))
        {
            _lastReadWarningAtUtc = now;
            logger.LogWarning(ex, "Embedding usage telemetry read failed; dashboard will return empty usage windows.");
        }
        else
        {
            logger.LogDebug(ex, "Embedding usage telemetry read failure suppressed.");
        }
    }

    private static IReadOnlyDictionary<string, long> CreateHistogram(IReadOnlyList<EmbeddingUsageTelemetryItem> items)
    {
        var histogram = EmptyHistogram();
        foreach (var item in items)
        {
            var tokenCount = Math.Max(0, item.TokenCount);
            foreach (var bucket in Buckets)
            {
                if (tokenCount <= bucket.UpperBound)
                {
                    histogram[bucket.Key]++;
                    break;
                }
            }
        }

        return histogram;
    }

    private static Dictionary<string, long> SumHistogram(IEnumerable<EmbeddingUsageAggregateRow> rows)
    {
        var histogram = EmptyHistogram();
        foreach (var row in rows)
        {
            foreach (var bucket in Buckets)
            {
                histogram[bucket.Key] += row.Histogram.GetValueOrDefault(bucket.Key);
            }
        }

        return histogram;
    }

    private static Dictionary<string, long> EmptyHistogram()
        => Buckets.ToDictionary(static x => x.Key, static _ => 0L, StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, long> ParseHistogram(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return EmptyHistogram();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, long>>(value, JsonOptions) ?? [];
            var histogram = EmptyHistogram();
            foreach (var bucket in Buckets)
            {
                histogram[bucket.Key] = Math.Max(0L, parsed.GetValueOrDefault(bucket.Key));
            }

            return histogram;
        }
        catch (JsonException)
        {
            return EmptyHistogram();
        }
    }

    private static DateTimeOffset TruncateToHour(DateTimeOffset value)
    {
        var utc = value.ToUniversalTime();
        return new DateTimeOffset(utc.Year, utc.Month, utc.Day, utc.Hour, 0, 0, TimeSpan.Zero);
    }

    private static double CalculateRate(long numerator, long denominator)
        => denominator <= 0 ? 0d : Math.Round(numerator / (double)denominator * 100d, 2);

    private static string NormalizeDimension(string? value, string fallback)
    {
        var normalized = value?.Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? fallback
            : normalized.Length <= 128
                ? normalized
                : normalized[..128];
    }

    private sealed record HistogramBucket(string Key, int UpperBound);

    private sealed record EmbeddingUsageGroupKey(
        DateTimeOffset BucketStartUtc,
        string ServiceName,
        string Provider,
        string Profile,
        string Purpose,
        string SourceKind,
        int MaxTokens);

    internal sealed record EmbeddingUsageAggregateRow(
        DateTimeOffset BucketStartUtc,
        string ServiceName,
        string Provider,
        string Profile,
        string Purpose,
        string SourceKind,
        int MaxTokens,
        long TotalInputs,
        long TruncatedInputs,
        long TotalTokenCount,
        long TotalTruncatedTokens,
        int MaxTokenCount,
        IReadOnlyDictionary<string, long> Histogram);
}

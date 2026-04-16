using System.Text.Json;
using Memory.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Npgsql;
using Pgvector;

namespace Memory.Infrastructure;

public sealed class RuntimeConfigurationAccessor(
    IOptions<MemoryOptions> memoryOptions,
    IEmbeddingProvider embeddingProvider,
    IResolvedEmbeddingProfileAccessor profileAccessor) : IRuntimeConfigurationAccessor
{
    public RuntimeConfigurationResult Current => new(
        memoryOptions.Value.Namespace,
        embeddingProvider.ProviderName,
        embeddingProvider.ExecutionProvider,
        embeddingProvider.EmbeddingProfile,
        embeddingProvider.ModelKey,
        embeddingProvider.Dimensions,
        embeddingProvider.MaxTokens,
        profileAccessor.Current.InferenceThreads,
        profileAccessor.Current.BatchSize,
        embeddingProvider.BatchingEnabled);
}

public sealed class ServiceHealthAccessor(HealthCheckService healthCheckService) : IServiceHealthAccessor
{
    public async Task<IReadOnlyList<DashboardServiceHealthResult>> GetServicesAsync(CancellationToken cancellationToken)
    {
        var report = await healthCheckService.CheckHealthAsync(registration => registration.Tags.Contains("ready"), cancellationToken);
        return report.Entries
            .OrderBy(x => x.Key)
            .Select(x => new DashboardServiceHealthResult(
                x.Key,
                x.Value.Status.ToString(),
                string.IsNullOrWhiteSpace(x.Value.Description)
                    ? (x.Value.Exception?.Message ?? string.Empty)
                    : x.Value.Description))
            .ToArray();
    }
}

public sealed class NpgsqlStorageExplorerStore(NpgsqlDataSource dataSource) : IStorageExplorerStore
{
    private static readonly IReadOnlyDictionary<string, StorageTableDefinition> TableDefinitions =
        new Dictionary<string, StorageTableDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["memory_items"] = new(
                "memory_items",
                "記憶主體與 metadata",
                "updated_at DESC, id DESC",
                ["id", "project_id", "external_key", "scope", "memory_type", "title", "content", "summary", "tags", "source_type", "source_ref", "importance", "confidence", "version", "status", "is_read_only", "metadata_json", "created_at", "updated_at"],
                ["project_id", "external_key", "scope", "memory_type", "title", "content", "summary", "tags", "source_type", "source_ref", "status", "metadata_json"]),
            ["memory_item_revisions"] = new(
                "memory_item_revisions",
                "記憶版本快照",
                "created_at DESC, id DESC",
                ["id", "memory_item_id", "version", "title", "content", "summary", "metadata_json", "changed_by", "created_at"],
                ["memory_item_id", "version", "title", "content", "summary", "metadata_json", "changed_by"]),
            ["memory_item_chunks"] = new(
                "memory_item_chunks",
                "檢索 chunk 與全文索引來源",
                "created_at DESC, id DESC",
                ["id", "memory_item_id", "chunk_kind", "chunk_index", "chunk_text", "metadata_json", "created_at"],
                ["memory_item_id", "chunk_kind", "chunk_index", "chunk_text", "metadata_json"]),
            ["memory_chunk_vectors"] = new(
                "memory_chunk_vectors",
                "向量版本與模型資訊",
                "created_at DESC, id DESC",
                ["id", "chunk_id", "model_key", "dimension", "status", "embedding", "created_at"],
                ["chunk_id", "model_key", "dimension", "status"]),
            ["memory_links"] = new(
                "memory_links",
                "記憶之間的關聯",
                "created_at DESC, id DESC",
                ["id", "from_id", "to_id", "link_type", "created_at"],
                ["from_id", "to_id", "link_type"]),
            ["memory_jobs"] = new(
                "memory_jobs",
                "背景工作與 reindex 狀態",
                "created_at DESC, id DESC",
                ["id", "project_id", "job_type", "status", "payload_json", "error", "created_at", "started_at", "completed_at"],
                ["project_id", "job_type", "status", "payload_json", "error"]),
            ["runtime_log_entries"] = new(
                "runtime_log_entries",
                "DB-first runtime logs",
                "created_at DESC, id DESC",
                ["id", "project_id", "service_name", "category", "level", "message", "exception", "trace_id", "request_id", "payload_json", "created_at"],
                ["project_id", "service_name", "category", "level", "message", "exception", "trace_id", "request_id", "payload_json"]),
            ["log_ingestion_checkpoints"] = new(
                "log_ingestion_checkpoints",
                "log 擷取檢查點",
                "last_seen_at DESC, id DESC",
                ["id", "service_name", "last_seen_at"],
                ["service_name"]),
            ["instance_settings"] = new(
                "instance_settings",
                "Instance 級個人化設定覆寫",
                "updated_at DESC, instance_id ASC, setting_key ASC",
                ["instance_id", "setting_key", "value_json", "revision", "updated_at", "updated_by"],
                ["instance_id", "setting_key", "value_json", "updated_by"]),
            ["conversation_sessions"] = new(
                "conversation_sessions",
                "對話自動整理 session 狀態",
                "updated_at DESC, id DESC",
                ["id", "conversation_id", "project_id", "project_name", "task_id", "source_system", "status", "last_turn_id", "started_at", "last_checkpoint_at", "updated_at"],
                ["conversation_id", "project_id", "project_name", "task_id", "source_system", "status", "last_turn_id"]),
            ["conversation_checkpoints"] = new(
                "conversation_checkpoints",
                "對話整理 checkpoint 與摘要輸入",
                "created_at DESC, id DESC",
                ["id", "session_id", "conversation_id", "turn_id", "project_id", "project_name", "task_id", "source_system", "event_type", "source_kind", "source_ref", "user_message_summary", "agent_message_summary", "tool_calls_json", "session_summary", "short_excerpt", "dedup_key", "metadata_json", "created_at"],
                ["conversation_id", "turn_id", "project_id", "project_name", "task_id", "source_system", "event_type", "source_kind", "source_ref", "user_message_summary", "agent_message_summary", "session_summary", "short_excerpt", "dedup_key", "metadata_json"]),
            ["conversation_insights"] = new(
                "conversation_insights",
                "對話萃取出的 staging insights 與 promotion 狀態",
                "updated_at DESC, id DESC",
                ["id", "session_id", "checkpoint_id", "conversation_id", "turn_id", "project_id", "project_name", "task_id", "source_system", "source_kind", "insight_type", "title", "content", "summary", "source_ref", "tags", "importance", "confidence", "dedup_key", "promotion_status", "promoted_memory_id", "error", "metadata_json", "created_at", "updated_at"],
                ["conversation_id", "turn_id", "project_id", "project_name", "task_id", "source_system", "source_kind", "insight_type", "title", "content", "summary", "source_ref", "tags", "dedup_key", "promotion_status", "error", "metadata_json"])
        };

    public async Task<IReadOnlyList<StorageTableSummaryResult>> ListTablesAsync(CancellationToken cancellationToken)
    {
        var summaries = new List<StorageTableSummaryResult>(TableDefinitions.Count);
        foreach (var definition in TableDefinitions.Values.OrderBy(x => x.Name))
        {
            var count = await CountAsync(definition.Name, cancellationToken);
            summaries.Add(new StorageTableSummaryResult(definition.Name, definition.Description, count, definition.Columns));
        }

        return summaries;
    }

    public async Task<StorageTableRowsResult> GetRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
    {
        var definition = Resolve(request.Table);
        var appliedQuery = NormalizeQuery(request.Query);
        var appliedColumn = ResolveSearchColumn(definition, request.Column);
        var (whereClause, configureParameters) = BuildFilter(definition, appliedQuery, appliedColumn);
        var totalCount = await CountAsync(definition.Name, whereClause, configureParameters, cancellationToken);
        var offset = (request.Page - 1) * request.PageSize;

        var sql = $"""
            SELECT *
            FROM {definition.Name}
            {whereClause}
            ORDER BY {definition.OrderBy}
            LIMIT @limit OFFSET @offset;
            """;

        var rows = new List<StorageRowResult>();
        await using var command = dataSource.CreateCommand(sql);
        configureParameters(command);
        command.Parameters.Add(new NpgsqlParameter<int>("limit", request.PageSize));
        command.Parameters.Add(new NpgsqlParameter<int>("offset", offset));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[reader.GetName(i)] = SerializeValue(reader.GetValue(i));
            }

            rows.Add(new StorageRowResult(values));
        }

        return new StorageTableRowsResult(
            definition.Name,
            definition.Description,
            definition.Columns,
            definition.SearchableColumns,
            appliedQuery,
            appliedColumn,
            new PagedResult<StorageRowResult>(rows, request.Page, request.PageSize, totalCount));
    }

    private async Task<int> CountAsync(string table, CancellationToken cancellationToken)
        => await CountAsync(table, string.Empty, static _ => { }, cancellationToken);

    private async Task<int> CountAsync(string table, string whereClause, Action<NpgsqlCommand> configureParameters, CancellationToken cancellationToken)
    {
        var sql = $"SELECT COUNT(*) FROM {table} {whereClause};";
        await using var command = dataSource.CreateCommand(sql);
        configureParameters(command);
        var scalar = await command.ExecuteScalarAsync(cancellationToken);
        return Convert.ToInt32(scalar);
    }

    private static (string WhereClause, Action<NpgsqlCommand> ConfigureParameters) BuildFilter(
        StorageTableDefinition definition,
        string? query,
        string? column)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return (string.Empty, static _ => { });
        }

        var columns = string.IsNullOrWhiteSpace(column)
            ? definition.SearchableColumns
            : new[] { column };
        var predicates = columns
            .Select(static columnName => $"COALESCE({columnName}::text, '') ILIKE @query");

        return (
            $"WHERE {string.Join(" OR ", predicates)}",
            command => command.Parameters.Add(new NpgsqlParameter<string>("query", $"%{query}%")));
    }

    private static StorageTableDefinition Resolve(string table)
    {
        if (!TableDefinitions.TryGetValue(table, out var definition))
        {
            throw new InvalidOperationException($"Storage table '{table}' is not available in the dashboard explorer.");
        }

        return definition;
    }

    private static string? ResolveSearchColumn(StorageTableDefinition definition, string? column)
    {
        if (string.IsNullOrWhiteSpace(column))
        {
            return null;
        }

        var trimmed = column.Trim();
        var resolved = definition.SearchableColumns.FirstOrDefault(x => string.Equals(x, trimmed, StringComparison.OrdinalIgnoreCase));
        if (resolved is null)
        {
            throw new ArgumentException($"Storage column '{trimmed}' is not available for querying in table '{definition.Name}'.", nameof(column));
        }

        return resolved;
    }

    private static string? NormalizeQuery(string? query)
        => string.IsNullOrWhiteSpace(query) ? null : query.Trim();

    private static string? SerializeValue(object value)
        => value switch
        {
            DBNull => null,
            null => null,
            DateTimeOffset dto => dto.ToString("O"),
            DateTime dateTime => dateTime.ToString("O"),
            string text => text,
            string[] texts => JsonSerializer.Serialize(texts),
            Guid guid => guid.ToString(),
            Vector vector => Truncate(vector.ToString(), 256),
            _ when value.GetType().IsArray => JsonSerializer.Serialize(value),
            _ => Convert.ToString(value)
        };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : $"{value[..maxLength]}...";

    private sealed record StorageTableDefinition(
        string Name,
        string Description,
        string OrderBy,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> SearchableColumns);
}

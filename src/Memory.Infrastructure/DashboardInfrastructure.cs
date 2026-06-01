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
    private const int MaxCellTextLength = 4096;

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
            ["maintenance_runs"] = new(
                "maintenance_runs",
                "維護歷程與資料保留執行紀錄",
                "started_at DESC, id DESC",
                ["id", "maintenance_type", "status", "started_at", "completed_at", "triggered_by", "policy_json", "result_json", "error"],
                ["id", "maintenance_type", "status", "triggered_by", "policy_json", "result_json", "error"]),
            ["runtime_log_entries"] = new(
                "runtime_log_entries",
                "DB-first runtime logs",
                "created_at DESC, id DESC",
                ["id", "project_id", "service_name", "category", "level", "message", "exception", "trace_id", "request_id", "payload_json", "created_at"],
                ["project_id", "service_name", "category", "level", "message", "exception", "trace_id", "request_id", "payload_json"]),
            ["retrieval_events"] = new(
                "retrieval_events",
                "檢索事件摘要與查詢條件",
                "created_at DESC, id DESC",
                ["id", "tenant_id", "owner_user_id", "project_id", "channel", "entry_point", "purpose", "query_text", "query_hash", "query_mode", "included_project_ids", "use_summary_layer", "result_limit", "cache_hit", "result_count", "duration_ms", "success", "error", "trace_id", "request_id", "metadata_json", "created_at"],
                ["id", "tenant_id", "owner_user_id", "project_id", "channel", "entry_point", "purpose", "query_text", "query_hash", "query_mode", "included_project_ids", "error", "trace_id", "request_id", "metadata_json"]),
            ["retrieval_hits"] = new(
                "retrieval_hits",
                "檢索命中快照",
                "retrieval_event_id DESC, rank ASC",
                ["id", "retrieval_event_id", "rank", "memory_id", "title", "memory_type", "source_type", "source_ref", "score", "excerpt", "project_id"],
                ["retrieval_event_id", "memory_id", "title", "memory_type", "source_type", "source_ref", "excerpt", "project_id"]),
            ["retrieval_telemetry_daily_summaries"] = new(
                "retrieval_telemetry_daily_summaries",
                "檢索 telemetry 每日彙總",
                "summary_date DESC, project_id ASC, entry_point ASC",
                ["summary_date", "tenant_id", "owner_user_id", "project_id", "channel", "entry_point", "purpose", "query_mode", "request_count", "success_count", "error_count", "zero_result_count", "cache_hit_count", "duration_sum_ms", "duration_max_ms", "duration_p95_ms", "result_count_sum", "created_at", "updated_at"],
                ["tenant_id", "owner_user_id", "project_id", "channel", "entry_point", "purpose", "query_mode"]),
            ["retrieval_telemetry_daily_hit_summaries"] = new(
                "retrieval_telemetry_daily_hit_summaries",
                "檢索命中每日 Top memory 彙總",
                "summary_date DESC, project_id ASC, entry_point ASC, hit_count DESC",
                ["summary_date", "tenant_id", "owner_user_id", "project_id", "entry_point", "memory_id", "title", "memory_type", "source_type", "source_ref", "hit_count", "best_rank", "score_sum", "score_max", "created_at", "updated_at"],
                ["tenant_id", "owner_user_id", "project_id", "entry_point", "memory_id", "title", "memory_type", "source_type", "source_ref"]),
            ["log_ingestion_checkpoints"] = new(
                "log_ingestion_checkpoints",
                "log 擷取檢查點",
                "last_seen_at DESC, id DESC",
                ["id", "service_name", "last_seen_at"],
                ["service_name"]),
            ["source_connections"] = new(
                "source_connections",
                "來源設定與同步狀態",
                "updated_at DESC, id DESC",
                ["id", "project_id", "name", "source_kind", "enabled", "config_json", "secret_json_protected", "last_cursor", "last_successful_sync_at", "created_at", "updated_at"],
                ["project_id", "name", "source_kind", "config_json", "last_cursor"]),
            ["source_sync_runs"] = new(
                "source_sync_runs",
                "來源同步執行紀錄",
                "started_at DESC, id DESC",
                ["id", "source_connection_id", "project_id", "trigger", "status", "scanned_count", "upserted_count", "archived_count", "error_count", "cursor_before", "cursor_after", "error", "started_at", "completed_at"],
                ["project_id", "trigger", "status", "cursor_before", "cursor_after", "error"]),
            ["governance_findings"] = new(
                "governance_findings",
                "治理檢查結果與處理狀態",
                "updated_at DESC, id DESC",
                ["id", "project_id", "source_connection_id", "primary_memory_id", "secondary_memory_id", "type", "status", "title", "summary", "details_json", "dedup_key", "created_at", "updated_at"],
                ["project_id", "type", "status", "title", "summary", "dedup_key", "details_json"]),
            ["evaluation_suites"] = new(
                "evaluation_suites",
                "檢索評測資料集",
                "updated_at DESC, id DESC",
                ["id", "project_id", "name", "description", "created_at", "updated_at"],
                ["project_id", "name", "description"]),
            ["evaluation_cases"] = new(
                "evaluation_cases",
                "評測案例與期待結果",
                "updated_at DESC, id DESC",
                ["id", "suite_id", "project_id", "scenario_label", "query", "expected_memory_ids", "expected_external_keys", "created_at", "updated_at"],
                ["project_id", "scenario_label", "query", "expected_memory_ids", "expected_external_keys"]),
            ["evaluation_runs"] = new(
                "evaluation_runs",
                "評測執行結果摘要",
                "started_at DESC, id DESC",
                ["id", "suite_id", "project_id", "status", "embedding_profile", "query_mode", "use_summary_layer", "top_k", "hit_rate", "recall_at_k", "mean_reciprocal_rank", "average_latency_ms", "error", "created_at", "started_at", "completed_at"],
                ["project_id", "status", "embedding_profile", "query_mode", "error"]),
            ["evaluation_run_items"] = new(
                "evaluation_run_items",
                "逐案例評測細節",
                "created_at DESC, id DESC",
                ["id", "run_id", "case_id", "query", "scenario_label", "expected_memory_ids", "expected_external_keys", "hit_memory_ids", "hit_external_keys", "hit_at_k", "recall_at_k", "reciprocal_rank", "latency_ms", "created_at"],
                ["query", "scenario_label", "expected_external_keys", "hit_external_keys"]),
            ["suggested_actions"] = new(
                "suggested_actions",
                "待處理建議與執行結果",
                "updated_at DESC, id DESC",
                ["id", "project_id", "type", "status", "title", "summary", "payload_json", "error", "created_at", "updated_at", "executed_at"],
                ["project_id", "type", "status", "title", "summary", "payload_json", "error"]),
            ["instance_settings"] = new(
                "instance_settings",
                "Instance 級個人化設定覆寫",
                "updated_at DESC, instance_id ASC, setting_key ASC",
                ["instance_id", "setting_key", "value_json", "revision", "updated_at", "updated_by"],
                ["instance_id", "setting_key", "value_json", "updated_by"]),
            ["tenants"] = new(
                "tenants",
                "租戶與組織帳號範圍",
                "updated_at DESC, slug ASC",
                ["id", "slug", "display_name", "status", "created_at", "updated_at"],
                ["id", "slug", "display_name", "status"]),
            ["tenant_users"] = new(
                "tenant_users",
                "租戶帳戶與角色",
                "updated_at DESC, username ASC",
                ["id", "tenant_id", "username", "display_name", "email", "role", "status", "created_at", "updated_at"],
                ["id", "tenant_id", "username", "display_name", "email", "role", "status"]),
            ["tenant_project_grants"] = new(
                "tenant_project_grants",
                "租戶專案授權範圍",
                "updated_at DESC, project_id ASC",
                ["id", "tenant_id", "project_id", "can_read", "can_write", "can_manage_tokens", "created_at", "updated_at"],
                ["id", "tenant_id", "project_id"]),
            ["api_tokens"] = new(
                "api_tokens",
                "API Token metadata 與最後使用資訊",
                "updated_at DESC, created_at DESC",
                ["id", "tenant_id", "owner_user_id", "name", "notes", "token_prefix", "token_last_four", "scopes", "allowed_project_ids", "expires_at", "revoked_at", "last_used_at", "last_used_ip", "last_used_user_agent", "created_at", "updated_at"],
                ["id", "tenant_id", "owner_user_id", "name", "notes", "token_prefix", "token_last_four", "scopes", "allowed_project_ids", "last_used_ip", "last_used_user_agent"]),
            ["security_audit_events"] = new(
                "security_audit_events",
                "租戶、帳戶與 Token 稽核事件",
                "created_at DESC, id DESC",
                ["id", "tenant_id", "actor_user_id", "api_token_id", "event_type", "outcome", "ip_address", "user_agent", "details_json", "created_at"],
                ["id", "tenant_id", "actor_user_id", "api_token_id", "event_type", "outcome", "ip_address", "user_agent", "details_json"]),
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
        var rowCounts = await LoadTableRowCountEstimatesAsync(cancellationToken);
        var summaries = new List<StorageTableSummaryResult>(TableDefinitions.Count);
        foreach (var definition in TableDefinitions.Values.OrderBy(x => x.Name))
        {
            var count = rowCounts.GetValueOrDefault(definition.Name);
            summaries.Add(new StorageTableSummaryResult(
                definition.Name,
                definition.Description,
                count,
                definition.Columns,
                DashboardStoragePolicy.IsLargeTable(definition.Name)));
        }

        return summaries;
    }

    public async Task<StorageTableRowsResult> GetRowsAsync(StorageRowsRequest request, CancellationToken cancellationToken)
    {
        var definition = Resolve(request.Table);
        if (DashboardStoragePolicy.IsBlockedUnfilteredLargeTablePage(request))
        {
            throw new StorageExplorerQueryRejectedException(DashboardStoragePolicy.LargeTableDeepPageWarning);
        }

        var appliedQuery = NormalizeQuery(request.Query);
        var appliedColumn = ResolveSearchColumn(definition, request.Column);
        if (DashboardStoragePolicy.IsLargeTablePreviewRequest(request))
        {
            return await GetLargeTablePreviewRowsAsync(definition, request, cancellationToken);
        }

        var (whereClause, configureParameters) = BuildFilter(definition, appliedQuery, appliedColumn);
        var isLargeTable = DashboardStoragePolicy.IsLargeTable(definition.Name);
        int? totalCount = isLargeTable && !string.IsNullOrWhiteSpace(appliedQuery)
            ? null
            : string.IsNullOrWhiteSpace(appliedQuery)
            ? await EstimateRowCountAsync(definition.Name, cancellationToken)
            : await CountAsync(definition.Name, whereClause, configureParameters, cancellationToken);
        var offset = (request.Page - 1) * request.PageSize;

        var sql = $"""
            SELECT {string.Join(", ", definition.Columns)}
            FROM {definition.Name}
            {whereClause}
            ORDER BY {definition.OrderBy}
            LIMIT @limit OFFSET @offset;
            """;

        var rows = new List<StorageRowResult>();
        await using var command = dataSource.CreateCommand(sql);
        if (isLargeTable)
        {
            command.CommandTimeout = 10;
        }

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
            new PagedResult<StorageRowResult>(
                rows,
                request.Page,
                request.PageSize,
                totalCount ?? ((request.Page - 1) * request.PageSize + rows.Count)),
            isLargeTable ? "Large table live query is guarded and count is estimated only for unfiltered preview." : string.Empty);
    }

    private async Task<StorageTableRowsResult> GetLargeTablePreviewRowsAsync(
        StorageTableDefinition definition,
        StorageRowsRequest request,
        CancellationToken cancellationToken)
    {
        var omittedColumns = GetLargeTablePreviewOmittedColumns(definition.Name);
        var selectedColumns = definition.Columns.Where(x => !omittedColumns.Contains(x)).ToArray();
        var sql = $"""
            SELECT {string.Join(", ", selectedColumns)}
            FROM {definition.Name}
            ORDER BY {definition.OrderBy}
            LIMIT @limit;
            """;

        var rows = new List<StorageRowResult>();
        await using var command = dataSource.CreateCommand(sql);
        command.CommandTimeout = 10;
        command.Parameters.Add(new NpgsqlParameter<int>("limit", Math.Min(request.PageSize, DashboardStoragePolicy.LargeTablePreviewPageSize)));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < reader.FieldCount; i++)
            {
                values[reader.GetName(i)] = SerializeValue(reader.GetValue(i));
            }

            foreach (var omittedColumn in omittedColumns)
            {
                values[omittedColumn] = "[omitted in large table preview]";
            }

            rows.Add(new StorageRowResult(values));
        }

        return new StorageTableRowsResult(
            definition.Name,
            definition.Description,
            definition.Columns,
            definition.SearchableColumns,
            null,
            null,
            new PagedResult<StorageRowResult>(
                rows,
                1,
                Math.Min(request.PageSize, DashboardStoragePolicy.LargeTablePreviewPageSize),
                await EstimateRowCountAsync(definition.Name, cancellationToken)),
            DashboardStoragePolicy.LargeTablePreviewWarning,
            "origin");
    }

    private async Task<IReadOnlyDictionary<string, int>> LoadTableRowCountEstimatesAsync(CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT relname, GREATEST(n_live_tup, 0)::bigint
            FROM pg_stat_user_tables
            WHERE schemaname = current_schema()
              AND relname = ANY(@tables);
            """;

        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string[]>("tables", TableDefinitions.Keys.ToArray()));

        var estimates = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var table = reader.GetString(0);
            var count = reader.GetInt64(1);
            estimates[table] = count > int.MaxValue ? int.MaxValue : (int)count;
        }

        return estimates;
    }

    private async Task<int> EstimateRowCountAsync(string table, CancellationToken cancellationToken)
    {
        var estimates = await LoadTableRowCountEstimatesAsync(cancellationToken);
        return estimates.GetValueOrDefault(table);
    }

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
            string text => Truncate(text, MaxCellTextLength),
            string[] texts => Truncate(JsonSerializer.Serialize(texts), MaxCellTextLength),
            Guid guid => guid.ToString(),
            Vector vector => Truncate(vector.ToString(), 256),
            _ when value.GetType().IsArray => Truncate(JsonSerializer.Serialize(value), MaxCellTextLength),
            _ => Truncate(Convert.ToString(value) ?? string.Empty, MaxCellTextLength)
        };

    private static string Truncate(string value, int maxLength)
        => value.Length <= maxLength ? value : $"{value[..maxLength]}...";

    private static IReadOnlySet<string> GetLargeTablePreviewOmittedColumns(string table)
        => table switch
        {
            "retrieval_events" => new HashSet<string>(["query_text", "metadata_json"], StringComparer.OrdinalIgnoreCase),
            "retrieval_hits" => new HashSet<string>(["excerpt"], StringComparer.OrdinalIgnoreCase),
            _ => new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        };

    private sealed record StorageTableDefinition(
        string Name,
        string Description,
        string OrderBy,
        IReadOnlyList<string> Columns,
        IReadOnlyList<string> SearchableColumns);
}

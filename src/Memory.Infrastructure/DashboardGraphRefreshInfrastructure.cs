using System.Text.Json;
using Memory.Application;
using Microsoft.Extensions.Options;
using Npgsql;
using NpgsqlTypes;

namespace Memory.Infrastructure;

/// <summary>A rebuildable DB projection. Publication, revision checkpoint and lease fencing share one transaction.</summary>
public sealed class NpgsqlDashboardGraphRefreshCoordinator(NpgsqlDataSource dataSource, IOptions<MemoryOptions> options) : IDashboardGraphRefreshCoordinator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly string _scope = options.Value.Namespace + ":global";

    public async Task<DashboardGraphRefreshLease?> TryAcquireAsync(bool forceFull, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using (var ensure = Command(connection, "INSERT INTO dashboard_graph_projection(scope) VALUES (@scope) ON CONFLICT DO NOTHING"))
        {
            await ensure.ExecuteNonQueryAsync(cancellationToken);
        }
        var state = await ReadStateAsync(connection, true, cancellationToken);
        var current = await ReadRevisionsAsync(connection, false, cancellationToken);
        var now = await ReadNowAsync(connection, cancellationToken);
        if (state.LeaseExpiresAt > now)
        {
            await ExecuteAsync(connection, "UPDATE dashboard_graph_projection SET deduplicated=deduplicated+1 WHERE scope=@scope", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var full = forceFull || state.Snapshot is null || state.LastFullAt is null || state.LastError.Length > 0 || now - state.LastFullAt >= TimeSpan.FromHours(24)
            || ChangedNonProjectScope(state.Revisions, current.Revisions);
        var dirty = ChangedProjects(state.Revisions, current.Revisions);
        if (!full && dirty.Count == 0)
        {
            await ExecuteAsync(connection, "UPDATE dashboard_graph_projection SET skipped=skipped+1,last_checked_at=clock_timestamp() WHERE scope=@scope", cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return null;
        }
        var token = Guid.NewGuid();
        await using var acquire = Command(connection, "UPDATE dashboard_graph_projection SET generation=generation+1,lease_token=@token,lease_expires_at=clock_timestamp()+interval '5 minutes',last_checked_at=clock_timestamp(),dirty_since=COALESCE(dirty_since,clock_timestamp()) WHERE scope=@scope RETURNING generation");
        acquire.Parameters.AddWithValue("token", token);
        var generation = (long)(await acquire.ExecuteScalarAsync(cancellationToken))!;
        await transaction.CommitAsync(cancellationToken);
        return new(token, generation, full, dirty, current.Revisions, state.Snapshot);
    }

    public async Task<bool> PublishAsync(DashboardGraphRefreshLease lease, DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload> snapshot, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(System.Data.IsolationLevel.Serializable, cancellationToken);
        var current = await ReadRevisionsAsync(connection, true, cancellationToken);
        if (!SameRevisions(current.Revisions, lease.Revisions))
        {
            await transaction.RollbackAsync(cancellationToken);
            await FailAsync(lease, "RevisionChanged", cancellationToken);
            return false;
        }
        await using var publish = Command(connection, """
            UPDATE dashboard_graph_projection SET snapshot=@snapshot,revisions=@revisions,
              lease_token=NULL,lease_expires_at=NULL,last_success_at=clock_timestamp(),last_checked_at=clock_timestamp(),
              last_full_at=CASE WHEN @full THEN clock_timestamp() ELSE last_full_at END,
              mode=CASE WHEN @full THEN 'Full' ELSE 'Incremental' END,
              full_builds=full_builds+CASE WHEN @full THEN 1 ELSE 0 END,
              incremental_builds=incremental_builds+CASE WHEN @full THEN 0 ELSE 1 END,last_error='',dirty_since=NULL
            WHERE scope=@scope AND generation=@generation AND lease_token=@token AND lease_expires_at>clock_timestamp()
            """);
        publish.Parameters.AddWithValue("snapshot", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(snapshot, JsonOptions));
        publish.Parameters.AddWithValue("revisions", NpgsqlDbType.Jsonb, JsonSerializer.Serialize(lease.Revisions, JsonOptions));
        publish.Parameters.AddWithValue("full", lease.Full);
        publish.Parameters.AddWithValue("generation", lease.Generation);
        publish.Parameters.AddWithValue("token", lease.Token);
        var published = await publish.ExecuteNonQueryAsync(cancellationToken) == 1;
        await transaction.CommitAsync(cancellationToken);
        return published;
    }

    public async Task FailAsync(DashboardGraphRefreshLease lease, string errorCategory, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = Command(connection, "UPDATE dashboard_graph_projection SET lease_token=NULL,lease_expires_at=NULL,failures=failures+1,last_error=@error WHERE scope=@scope AND generation=@generation AND lease_token=@token");
        command.Parameters.AddWithValue("generation", lease.Generation);
        command.Parameters.AddWithValue("token", lease.Token);
        command.Parameters.AddWithValue("error", errorCategory is "RevisionChanged" or "Cancelled" or "BuildTimeout" ? errorCategory : "BuildFailed");
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>?> ReadSnapshotAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var state = await ReadStateAsync(connection, false, cancellationToken);
        var current = await ReadRevisionsAsync(connection, false, cancellationToken);
        var changed = !SameRevisions(state.Revisions, current.Revisions);
        // An unchanged revision check validates freshness without changing the content capture time.
        return state.Snapshot is null ? null : state.Snapshot with
        {
            StaleAfterUtc = (changed ? state.LastSuccessAt ?? state.Snapshot.CapturedAtUtc : state.LastCheckedAt ?? state.Snapshot.CapturedAtUtc).AddSeconds(60),
            LastError = state.LastError.Length > 0 ? state.LastError : changed ? "Dirty" : ""
        };
    }

    public async Task<DashboardGraphRefreshStatus> GetStatusAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        var state = await ReadStateAsync(connection, false, cancellationToken);
        var current = await ReadRevisionsAsync(connection, false, cancellationToken);
        var now = await ReadNowAsync(connection, cancellationToken);
        var dirty = ChangedProjects(state.Revisions, current.Revisions);
        var changed = current.UpdatedAt.Where(pair => !state.Revisions.TryGetValue(pair.Key, out var prior) || prior != current.Revisions[pair.Key]).Select(pair => pair.Value).ToArray();
        double? age = changed.Length == 0 ? null : Math.Max(0, (now - (state.DirtySince ?? changed.Min())).TotalSeconds);
        return new("global", state.Generation, state.Mode, state.LastSuccessAt, state.LastFullAt, state.LastCheckedAt,
            dirty.Count, age, state.LeaseExpiresAt > now,
            state.Snapshot is null || age > 60 || state.LastCheckedAt is null || now - state.LastCheckedAt > TimeSpan.FromSeconds(60) || state.LastError.Length > 0,
            state.FullBuilds, state.IncrementalBuilds, state.Skipped, state.Deduplicated, state.Failures, state.LastError);
    }

    private NpgsqlCommand Command(NpgsqlConnection connection, string sql)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("scope", _scope);
        return command;
    }

    private async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, sql);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<DateTimeOffset> ReadNowAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT clock_timestamp()", connection);
        return new DateTimeOffset((DateTime)(await command.ExecuteScalarAsync(cancellationToken))!);
    }

    private async Task<State> ReadStateAsync(NpgsqlConnection connection, bool forUpdate, CancellationToken cancellationToken)
    {
        await using var command = Command(connection, "SELECT generation,lease_expires_at,revisions::text,snapshot::text,mode,last_success_at,last_full_at,last_checked_at,full_builds,incremental_builds,skipped,deduplicated,failures,last_error,dirty_since FROM dashboard_graph_projection WHERE scope=@scope" + (forUpdate ? " FOR UPDATE" : ""));
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) return new();
        return new State
        {
            Generation = reader.GetInt64(0),
            LeaseExpiresAt = Date(reader, 1),
            Revisions = JsonSerializer.Deserialize<Dictionary<string, long>>(reader.GetString(2), JsonOptions)!,
            Snapshot = reader.IsDBNull(3) ? null : JsonSerializer.Deserialize<DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>>(reader.GetString(3), JsonOptions),
            Mode = reader.GetString(4),
            LastSuccessAt = Date(reader, 5),
            LastFullAt = Date(reader, 6),
            LastCheckedAt = Date(reader, 7),
            FullBuilds = reader.GetInt64(8),
            IncrementalBuilds = reader.GetInt64(9),
            Skipped = reader.GetInt64(10),
            Deduplicated = reader.GetInt64(11),
            Failures = reader.GetInt64(12),
            LastError = reader.GetString(13),
            DirtySince = Date(reader, 14)
        };
    }

    private static DateTimeOffset? Date(NpgsqlDataReader reader, int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<DateTimeOffset>(ordinal);

    private static async Task<RevisionState> ReadRevisionsAsync(NpgsqlConnection connection, bool lockRows, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand("SELECT scope,revision,updated_at FROM cache_scope_revisions WHERE scope LIKE 'project:%' OR scope IN ('global','security') ORDER BY scope" + (lockRows ? " FOR SHARE" : ""), connection);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var result = new RevisionState();
        while (await reader.ReadAsync(cancellationToken))
        {
            result.Revisions.Add(reader.GetString(0), reader.GetInt64(1));
            result.UpdatedAt.Add(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(2));
        }
        return result;
    }

    private static bool SameRevisions(IReadOnlyDictionary<string, long> left, IReadOnlyDictionary<string, long> right) => left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var revision) && revision == pair.Value);
    private static bool ChangedNonProjectScope(IReadOnlyDictionary<string, long> previous, IReadOnlyDictionary<string, long> current) => previous.Keys.Concat(current.Keys).Where(key => !key.StartsWith("project:", StringComparison.Ordinal)).Distinct().Any(key => previous.GetValueOrDefault(key) != current.GetValueOrDefault(key));
    private static HashSet<string> ChangedProjects(IReadOnlyDictionary<string, long> previous, IReadOnlyDictionary<string, long> current) => previous.Keys.Concat(current.Keys).Where(key => key.StartsWith("project:", StringComparison.Ordinal)).Distinct().Where(key => previous.GetValueOrDefault(key) != current.GetValueOrDefault(key)).Select(key => key[8..]).ToHashSet(StringComparer.OrdinalIgnoreCase);
    private sealed class RevisionState
    {
        public Dictionary<string, long> Revisions { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, DateTimeOffset> UpdatedAt { get; } = new(StringComparer.Ordinal);
    }
    private sealed class State
    {
        public long Generation { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
        public Dictionary<string, long> Revisions { get; init; } = new();
        public DashboardSnapshotEnvelope<DashboardMemoryGraphIndexSnapshotPayload>? Snapshot { get; init; }
        public string Mode { get; init; } = "Unknown";
        public DateTimeOffset? LastSuccessAt { get; init; }
        public DateTimeOffset? LastFullAt { get; init; }
        public DateTimeOffset? LastCheckedAt { get; init; }
        public DateTimeOffset? DirtySince { get; init; }
        public long FullBuilds { get; init; }
        public long IncrementalBuilds { get; init; }
        public long Skipped { get; init; }
        public long Deduplicated { get; init; }
        public long Failures { get; init; }
        public string LastError { get; init; } = "";
    }
}

public sealed class CoordinatedDashboardSnapshotStore(RedisDashboardSnapshotStore inner, IDashboardGraphRefreshCoordinator coordinator) : IDashboardSnapshotStore
{
    public async Task<DashboardSnapshotEnvelope<TPayload>?> GetAsync<TPayload>(string key, CancellationToken cancellationToken)
    {
        if (key != DashboardSnapshotKeys.MemoryGraphIndex) return await inner.GetAsync<TPayload>(key, cancellationToken);
        if (typeof(TPayload) != typeof(DashboardMemoryGraphIndexSnapshotPayload)) throw new InvalidOperationException("Invalid graph snapshot payload type.");
        return (DashboardSnapshotEnvelope<TPayload>?)(object?)await coordinator.ReadSnapshotAsync(cancellationToken);
    }
    public Task SetAsync<TPayload>(DashboardSnapshotEnvelope<TPayload> envelope, CancellationToken cancellationToken)
    {
        if (envelope.Key == DashboardSnapshotKeys.MemoryGraphIndex) throw new InvalidOperationException("Graph publication requires a valid generation lease.");
        return inner.SetAsync(envelope, cancellationToken);
    }
}

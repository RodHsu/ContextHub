using System.Data;
using Microsoft.Extensions.Logging;
using Npgsql;
using Pgvector;
using Memory.Application;
using Memory.Domain;

namespace Memory.Infrastructure;

public sealed class NpgsqlSearchStore(NpgsqlDataSource dataSource, ILogger<NpgsqlSearchStore> logger) : IHybridSearchStore, IVectorStore
{
    public async Task<IReadOnlyList<ChunkSearchHit>> SearchKeywordChunksAsync(string query, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.memory_item_id,
                   c.id,
                   ts_rank_cd(c.content_tsv, websearch_to_tsquery('simple', @query))::numeric AS score,
                   LEFT(c.chunk_text, 240) AS excerpt
            FROM memory_item_chunks c
            WHERE c.content_tsv @@ websearch_to_tsquery('simple', @query)
            ORDER BY score DESC
            LIMIT @limit;
            """;

        var results = new List<ChunkSearchHit>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<string>("query", query));
        command.Parameters.Add(new NpgsqlParameter<int>("limit", limit));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new ChunkSearchHit(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetDecimal(2),
                reader.GetString(3)));
        }

        return results;
    }

    public async Task<IReadOnlyList<ChunkSearchHit>> SearchVectorChunksAsync(EmbeddingVector vector, int limit, CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT c.memory_item_id,
                   c.id,
                   (1 - (v.embedding <=> @embedding))::numeric AS score,
                   LEFT(c.chunk_text, 240) AS excerpt
            FROM memory_chunk_vectors v
            JOIN memory_item_chunks c ON c.id = v.chunk_id
            WHERE v.model_key = @model_key
              AND v.status = 'Active'
            ORDER BY v.embedding <=> @embedding
            LIMIT @limit;
            """;

        var results = new List<ChunkSearchHit>();
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<Vector>("embedding", new Vector(vector.Values))
        {
            DataTypeName = "vector"
        });
        command.Parameters.Add(new NpgsqlParameter<string>("model_key", vector.ModelKey));
        command.Parameters.Add(new NpgsqlParameter<int>("limit", limit));

        try
        {
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                results.Add(new ChunkSearchHit(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    reader.GetDecimal(2),
                    reader.GetString(3)));
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UndefinedTable)
        {
            logger.LogWarning(ex, "Vector search skipped because database tables are not ready.");
        }

        return results;
    }

    public async Task ReplaceChunkVectorAsync(Guid chunkId, EmbeddingVector vector, CancellationToken cancellationToken)
    {
        const string supersedeSql = """
            UPDATE memory_chunk_vectors
            SET status = 'Superseded'
            WHERE chunk_id = @chunk_id
              AND model_key = @model_key
              AND status = 'Active';
            """;

        const string insertSql = """
            INSERT INTO memory_chunk_vectors (id, chunk_id, model_key, dimension, status, embedding, created_at)
            VALUES (@id, @chunk_id, @model_key, @dimension, @status, @embedding, @created_at);
            """;

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var supersede = connection.CreateCommand())
        {
            supersede.CommandText = supersedeSql;
            supersede.Transaction = transaction;
            supersede.Parameters.Add(new NpgsqlParameter<Guid>("chunk_id", chunkId));
            supersede.Parameters.Add(new NpgsqlParameter<string>("model_key", vector.ModelKey));
            await supersede.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = connection.CreateCommand())
        {
            insert.CommandText = insertSql;
            insert.Transaction = transaction;
            insert.Parameters.Add(new NpgsqlParameter<Guid>("id", Guid.NewGuid()));
            insert.Parameters.Add(new NpgsqlParameter<Guid>("chunk_id", chunkId));
            insert.Parameters.Add(new NpgsqlParameter<string>("model_key", vector.ModelKey));
            insert.Parameters.Add(new NpgsqlParameter<int>("dimension", vector.Dimensions));
            insert.Parameters.Add(new NpgsqlParameter<string>("status", VectorStatus.Active.ToString()));
            insert.Parameters.Add(new NpgsqlParameter<Vector>("embedding", new Vector(vector.Values))
            {
                DataTypeName = "vector"
            });
            insert.Parameters.Add(new NpgsqlParameter<DateTimeOffset>("created_at", DateTimeOffset.UtcNow));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }
}

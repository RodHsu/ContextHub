using System.Reflection;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Memory.Infrastructure;

public sealed class DatabaseMigrationHostedService(NpgsqlDataSource dataSource, ILogger<DatabaseMigrationHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var lockCommand = connection.CreateCommand();
        lockCommand.CommandText = "SELECT pg_advisory_lock(941221);";
        await lockCommand.ExecuteNonQueryAsync(cancellationToken);

        try
        {
            await EnsureMigrationTableAsync(connection, cancellationToken);
            var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken);
            var assembly = typeof(DatabaseMigrationHostedService).Assembly;
            var scripts = assembly.GetManifestResourceNames()
                .Where(x => x.Contains(".Sql.Migrations.", StringComparison.Ordinal) && x.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                .OrderBy(x => x, StringComparer.Ordinal);

            foreach (var script in scripts)
            {
                var name = script[(script.LastIndexOf(".Sql.Migrations.", StringComparison.Ordinal) + ".Sql.Migrations.".Length)..];
                if (applied.Contains(name))
                {
                    continue;
                }

                await using var stream = assembly.GetManifestResourceStream(script)
                    ?? throw new InvalidOperationException($"Unable to open embedded migration '{script}'.");
                using var reader = new StreamReader(stream);
                var sql = await reader.ReadToEndAsync(cancellationToken);

                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using (var applyCommand = connection.CreateCommand())
                {
                    applyCommand.Transaction = transaction;
                    applyCommand.CommandText = sql;
                    await applyCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await using (var insertCommand = connection.CreateCommand())
                {
                    insertCommand.Transaction = transaction;
                    insertCommand.CommandText = "INSERT INTO schema_migrations (name) VALUES (@name);";
                    insertCommand.Parameters.Add(new NpgsqlParameter<string>("name", name));
                    await insertCommand.ExecuteNonQueryAsync(cancellationToken);
                }

                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation("Applied database migration {MigrationName}", name);
            }

            await connection.ReloadTypesAsync(cancellationToken);
        }
        finally
        {
            await using var unlockCommand = connection.CreateCommand();
            unlockCommand.CommandText = "SELECT pg_advisory_unlock(941221);";
            await unlockCommand.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static async Task EnsureMigrationTableAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS schema_migrations
            (
                name TEXT PRIMARY KEY,
                applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
            );
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<HashSet<string>> ReadAppliedMigrationsAsync(NpgsqlConnection connection, CancellationToken cancellationToken)
    {
        var applied = new HashSet<string>(StringComparer.Ordinal);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM schema_migrations;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }
}

public sealed record BufferedLogEntry(
    string ProjectId,
    string ServiceName,
    string Category,
    string Level,
    string Message,
    string Exception,
    string TraceId,
    string RequestId,
    string PayloadJson,
    DateTimeOffset CreatedAt);

public sealed class DatabaseLogQueue
{
    private readonly Channel<BufferedLogEntry> _channel = Channel.CreateBounded<BufferedLogEntry>(new BoundedChannelOptions(2048)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true
    });

    public ValueTask EnqueueAsync(BufferedLogEntry entry, CancellationToken cancellationToken)
        => _channel.Writer.WriteAsync(entry, cancellationToken);

    public ValueTask<BufferedLogEntry> ReadAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAsync(cancellationToken);

    public bool TryRead(out BufferedLogEntry entry)
    {
        if (_channel.Reader.TryRead(out var candidate))
        {
            entry = candidate;
            return true;
        }

        entry = default!;
        return false;
    }
}

public sealed class DatabaseLogWriterService(
    DatabaseLogQueue queue,
    IDbContextFactory<MemoryDbContext> dbContextFactory,
    Microsoft.Extensions.Options.IOptions<DatabaseLoggingOptions> options,
    ILogger<DatabaseLogWriterService> logger) : BackgroundService
{
    private readonly DatabaseLoggingOptions _options = options.Value;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var batch = new List<BufferedLogEntry>(_options.BatchSize)
                {
                    await queue.ReadAsync(stoppingToken)
                };

                while (batch.Count < _options.BatchSize && queue.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                await using var dbContext = await dbContextFactory.CreateDbContextAsync(stoppingToken);
                dbContext.RuntimeLogEntries.AddRange(batch.Select(x => new Memory.Domain.RuntimeLogEntry
                {
                    ProjectId = x.ProjectId,
                    ServiceName = x.ServiceName,
                    Category = x.Category,
                    Level = x.Level,
                    Message = x.Message,
                    Exception = x.Exception,
                    TraceId = x.TraceId,
                    RequestId = x.RequestId,
                    PayloadJson = x.PayloadJson,
                    CreatedAt = x.CreatedAt
                }));

                await dbContext.SaveChangesAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Database log flush failed; dropping buffered log batch.");
                await Task.Delay(TimeSpan.FromSeconds(_options.FlushIntervalSeconds), stoppingToken);
            }
        }
    }
}

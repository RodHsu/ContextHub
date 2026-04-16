using System.Diagnostics;
using Memory.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using StackExchange.Redis;

namespace Memory.Infrastructure;

public sealed class DatabaseLoggerProvider(DatabaseLogQueue queue, IOptions<DatabaseLoggingOptions> options) : ILoggerProvider
{
    private readonly DatabaseLoggingOptions _options = options.Value;

    public ILogger CreateLogger(string categoryName) => new DatabaseLogger(categoryName, queue, _options);

    public void Dispose()
    {
    }
}

internal sealed class DatabaseLogger(string categoryName, DatabaseLogQueue queue, DatabaseLoggingOptions options) : ILogger
{
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public bool IsEnabled(LogLevel logLevel)
        => logLevel >= options.MinimumLevel &&
           !categoryName.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal);

    public void Log<TState>(
        LogLevel logLevel,
        EventId eventId,
        TState state,
        Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel))
        {
            return;
        }

        var activity = Activity.Current;
        _ = queue.EnqueueAsync(
            new BufferedLogEntry(
                ProjectContext.DefaultProjectId,
                options.ServiceName,
                categoryName,
                logLevel.ToString(),
                formatter(state, exception),
                exception?.ToString() ?? string.Empty,
                activity?.TraceId.ToString() ?? string.Empty,
                activity?.SpanId.ToString() ?? string.Empty,
                "{}",
                DateTimeOffset.UtcNow),
            CancellationToken.None);
    }
}

public sealed class PostgresHealthCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var command = dataSource.CreateCommand("SELECT 1;");
            await command.ExecuteScalarAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(exception: ex);
        }
    }
}

public sealed class RedisHealthCheck(IConnectionMultiplexer multiplexer) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await multiplexer.GetDatabase().PingAsync();
            return HealthCheckResult.Healthy();
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(exception: ex);
        }
    }
}

public sealed class EmbeddingProviderHealthCheck(IHttpClientFactory httpClientFactory, IOptions<EmbeddingOptions> options) : IHealthCheck
{
    private readonly EmbeddingOptions _options = options.Value;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (!_options.Provider.Equals("Http", StringComparison.OrdinalIgnoreCase))
        {
            return HealthCheckResult.Healthy();
        }

        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return HealthCheckResult.Unhealthy("Embeddings:BaseUrl is required when using the Http provider.");
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpEmbeddingProviderClient.Name);
            using var response = await client.GetAsync("/health/ready", cancellationToken);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy($"Embedding service returned HTTP {(int)response.StatusCode}.");
        }
        catch (Exception ex)
        {
            return HealthCheckResult.Unhealthy(exception: ex);
        }
    }
}

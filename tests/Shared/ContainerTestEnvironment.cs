using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Npgsql;
using StackExchange.Redis;
using Testcontainers.PostgreSql;
using Testcontainers.Redis;

namespace Memory.Tests.Shared;

public sealed class ContainerTestEnvironment : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;
    private RedisContainer? _redis;

    public MemoryApplicationFactory? Factory { get; private set; }

    public async Task InitializeAsync()
    {
        if (!DockerTestGate.Current.IsAvailable)
        {
            return;
        }

        _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg17")
            .WithPortBinding(5432, true)
            .WithDatabase("contexthub")
            .WithUsername("contexthub")
            .WithPassword("contexthub")
            .Build();

        _redis = new RedisBuilder("redis:7.4-alpine")
            .WithPortBinding(6379, true)
            .Build();

        await _postgres.StartAsync();
        await _redis.StartAsync();
        var postgresConnectionString = _postgres.GetConnectionString();
        var redisConnectionString = _redis.GetConnectionString();
        await WaitForDependenciesAsync(postgresConnectionString, redisConnectionString);
        Factory = new MemoryApplicationFactory(postgresConnectionString, redisConnectionString);
        await WaitForReadinessAsync();
    }

    public async Task DisposeAsync()
    {
        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }

        if (_postgres is not null)
        {
            await _postgres.DisposeAsync();
        }
    }

    private async Task WaitForReadinessAsync()
    {
        var factory = GetFactory();
        using var client = factory.CreateClient();
        var startedAt = DateTimeOffset.UtcNow;
        while (DateTimeOffset.UtcNow - startedAt < TimeSpan.FromSeconds(30))
        {
            try
            {
                using var response = await client.GetAsync("/health/ready");
                if (response.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
            }

            await Task.Delay(500);
        }

        throw new TimeoutException("Timed out waiting for the test server readiness endpoint.");
    }

    public MemoryApplicationFactory GetFactory()
        => Factory ?? throw new InvalidOperationException(DockerTestGate.Current.Reason);

    private static async Task WaitForDependenciesAsync(string postgresConnectionString, string redisConnectionString)
    {
        var postgresDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
        while (DateTimeOffset.UtcNow < postgresDeadline)
        {
            try
            {
                await using var connection = new NpgsqlConnection(postgresConnectionString);
                await connection.OpenAsync();
                await using var command = new NpgsqlCommand("SELECT 1;", connection);
                await command.ExecuteScalarAsync();
                break;
            }
            catch
            {
                await Task.Delay(500);
            }
        }

        var redisDeadline = DateTimeOffset.UtcNow.AddSeconds(20);
        while (DateTimeOffset.UtcNow < redisDeadline)
        {
            try
            {
                using var multiplexer = await ConnectionMultiplexer.ConnectAsync(redisConnectionString);
                await multiplexer.GetDatabase().PingAsync();
                return;
            }
            catch
            {
                await Task.Delay(250);
            }
        }

        throw new TimeoutException("Timed out waiting for test dependencies to become ready.");
    }
}

public sealed class MemoryApplicationFactory(string postgresConnectionString, string redisConnectionString) : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:Postgres", postgresConnectionString);
        builder.UseSetting("ConnectionStrings:Redis", redisConnectionString);
        builder.UseSetting("Embeddings:Provider", "Deterministic");
        builder.UseSetting("Embeddings:Profile", "compact");
        builder.UseSetting("Embeddings:ModelKey", "deterministic-384");
        builder.UseSetting("Embeddings:Dimensions", "384");
        builder.UseSetting("Embeddings:MaxTokens", "512");
        builder.UseSetting("Memory:Namespace", "test");
        builder.UseSetting("DatabaseLogging:MinimumLevel", "Error");
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Postgres"] = postgresConnectionString,
                ["ConnectionStrings:Redis"] = redisConnectionString,
                ["Embeddings:Provider"] = "Deterministic",
                ["Embeddings:Profile"] = "compact",
                ["Embeddings:ModelKey"] = "deterministic-384",
                ["Embeddings:Dimensions"] = "384",
                ["Embeddings:MaxTokens"] = "512",
                ["Memory:Namespace"] = "test",
                ["DatabaseLogging:MinimumLevel"] = "Error"
            });
        });
    }
}

public sealed class DockerRequiredFactAttribute : FactAttribute
{
    public DockerRequiredFactAttribute()
    {
        if (!DockerTestGate.Current.IsAvailable)
        {
            Skip = DockerTestGate.Current.Reason;
        }
    }
}

public sealed record DockerAvailabilityResult(bool IsAvailable, string Reason);

public static class DockerTestGate
{
    private static readonly Lazy<DockerAvailabilityResult> LazyCurrent = new(ProbeDocker);

    public static DockerAvailabilityResult Current => LazyCurrent.Value;

    private static DockerAvailabilityResult ProbeDocker()
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo("docker", "version --format {{.Server.Version}}")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.Start();
            if (!process.WaitForExit((int)TimeSpan.FromSeconds(5).TotalMilliseconds))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                }

                return new DockerAvailabilityResult(false, "Docker daemon is not reachable; container-backed integration tests were skipped.");
            }

            if (process.ExitCode == 0)
            {
                return new DockerAvailabilityResult(true, "Docker daemon is available.");
            }

            var error = process.StandardError.ReadToEnd().Trim();
            if (string.IsNullOrWhiteSpace(error))
            {
                error = process.StandardOutput.ReadToEnd().Trim();
            }

            return new DockerAvailabilityResult(false, $"Docker daemon is not reachable; container-backed integration tests were skipped. {error}".Trim());
        }
        catch (Exception ex)
        {
            return new DockerAvailabilityResult(false, $"Docker CLI is unavailable; container-backed integration tests were skipped. {ex.Message}");
        }
    }
}

using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class RequestArrivalObservationStoreTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Enabled_ingress_records_auth_denial_and_tool_parent_without_headers_or_arguments()
    {
        await using var factory = environment.GetFactory().WithWebHostBuilder(builder =>
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["RequestArrivalObservations:Enabled"] = "true",
                ["RequestArrivalObservations:UntilUtc"] = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")
            })));
        using var client = factory.CreateClient();
        var buffer = factory.Services.GetRequiredService<RequestArrivalObservations>();
        Assert.True(buffer.Enabled);
        client.DefaultRequestHeaders.Authorization = null;
        using var denied = await client.GetAsync("/api/me?private=PRIVATE_QUERY_SENTINEL");
        Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MemoryApplicationFactory.TestBootstrapToken);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/event-stream");
        using var called = await client.PostAsJsonAsync("/mcp", new
        {
            jsonrpc = "2.0",
            id = 1,
            method = "tools/call",
            @params = new { name = "memory_search", arguments = new { query = "PRIVATE_QUERY_SENTINEL", projectId = "PRIVATE_PROJECT_SENTINEL", limit = 1 } }
        });
        Assert.Equal(HttpStatusCode.OK, called.StatusCode);
        var flush = factory.Services.GetServices<IHostedService>().OfType<RequestArrivalObservationFlushService>().Single();
        await flush.FlushAsync(false, default);
        await using var command = factory.Services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            SELECT count(*) FILTER(WHERE layer='http'),count(*) FILTER(WHERE layer='mcp-tool'),
                count(*) FILTER(WHERE layer='http' AND outcome='error'),
                string_agg(row_to_json(s)::text,''),
                (SELECT count(*) FROM monitoring.request_arrival_samples t
                 JOIN monitoring.request_arrival_samples h ON h.boot_id=t.boot_id AND h.sequence=t.parent_sequence
                 WHERE t.boot_id=@boot AND t.layer='mcp-tool' AND h.layer='http' AND h.started_at_utc<=t.started_at_utc)
            FROM monitoring.request_arrival_samples s WHERE boot_id=@boot;
            """);
        command.Parameters.AddWithValue("boot", buffer.BootId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2L, reader.GetInt64(0));
        Assert.Equal(1L, reader.GetInt64(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.DoesNotContain("PRIVATE_QUERY_SENTINEL", reader.GetString(3));
        Assert.DoesNotContain("PRIVATE_PROJECT_SENTINEL", reader.GetString(3));
        Assert.DoesNotContain(MemoryApplicationFactory.TestBootstrapToken, reader.GetString(3));
        Assert.Equal(1L, reader.GetInt64(4));
    }

    [DockerRequiredFact]
    public async Task Replaying_old_start_cannot_replace_completed_sample_or_coverage()
    {
        _ = environment.GetFactory();
        await using var source = NpgsqlDataSource.Create(environment.PostgresConnectionString!);
        var buffer = Buffer();
        var store = new RequestArrivalObservationStore(source);
        var lease = buffer.Begin("http", "memory_search")!;
        var started = buffer.Snapshot();
        await store.WriteAsync(started.Coverage, started.Samples, DateTimeOffset.UtcNow, false, default);
        lease.Complete("success");
        var completed = buffer.Snapshot();
        await store.WriteAsync(completed.Coverage, completed.Samples, DateTimeOffset.UtcNow, false, default);
        await store.WriteAsync(completed.Coverage, completed.Samples, DateTimeOffset.UtcNow, false, default);
        await store.WriteAsync(started.Coverage, started.Samples, DateTimeOffset.UtcNow, false, default);
        await using var command = source.CreateCommand("""
            SELECT s.revision,s.outcome,b.http_started,b.http_completed,b.http_active,
                (SELECT count(*) FROM monitoring.request_arrival_samples WHERE boot_id=@boot)
            FROM monitoring.request_arrival_samples s JOIN monitoring.request_arrival_boots b USING(boot_id)
            WHERE s.boot_id=@boot;
            """);
        command.Parameters.AddWithValue("boot", buffer.BootId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(2, reader.GetInt32(0));
        Assert.Equal("success", reader.GetString(1));
        Assert.Equal(1L, reader.GetInt64(2));
        Assert.Equal(1L, reader.GetInt64(3));
        Assert.Equal(0, reader.GetInt32(4));
        Assert.Equal(1L, reader.GetInt64(5));
    }

    [DockerRequiredFact]
    public async Task Failed_database_flush_is_atomic_keeps_staging_and_can_retry()
    {
        _ = environment.GetFactory();
        await using var source = NpgsqlDataSource.Create(environment.PostgresConnectionString!);
        var buffer = Buffer();
        buffer.Begin("mcp-tool", "build_working_context")!.Complete("success");
        var store = new RequestArrivalObservationStore(source);
        var flush = new RequestArrivalObservationFlushService(buffer, store, TimeProvider.System,
            NullLogger<RequestArrivalObservationFlushService>.Instance);
        var suffix = buffer.BootId.ToString("N");
        await using var fault = source.CreateCommand($"""
            CREATE FUNCTION monitoring.arrival_fault_{suffix}() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN IF NEW.boot_id = '{buffer.BootId:D}' THEN RAISE EXCEPTION 'OWNED_ARRIVAL_WRITE_FAULT'; END IF; RETURN NEW; END $$;
            CREATE TRIGGER arrival_fault_{suffix} BEFORE INSERT ON monitoring.request_arrival_samples
            FOR EACH ROW EXECUTE FUNCTION monitoring.arrival_fault_{suffix}();
            """);
        await fault.ExecuteNonQueryAsync();
        try
        {
            await Assert.ThrowsAsync<PostgresException>(() => flush.FlushAsync(false, default));
            Assert.Single(buffer.Snapshot().Samples);
            await using var count = source.CreateCommand("SELECT count(*) FROM monitoring.request_arrival_boots WHERE boot_id=@boot");
            count.Parameters.AddWithValue("boot", buffer.BootId);
            Assert.Equal(0L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await using var remove = source.CreateCommand($"DROP TRIGGER arrival_fault_{suffix} ON monitoring.request_arrival_samples; DROP FUNCTION monitoring.arrival_fault_{suffix}();");
            await remove.ExecuteNonQueryAsync();
        }
        await flush.FlushAsync(false, default);
        Assert.Empty(buffer.Snapshot().Samples);
        await using var saved = source.CreateCommand("SELECT count(*) FROM monitoring.request_arrival_samples WHERE boot_id=@boot AND revision=2");
        saved.Parameters.AddWithValue("boot", buffer.BootId);
        Assert.Equal(1L, await saved.ExecuteScalarAsync());
    }

    [Fact]
    public async Task Disabled_capture_never_touches_a_disposed_database_source()
    {
        var buffer = new RequestArrivalObservations(TimeProvider.System, Options.Create(new RequestArrivalObservationOptions()));
        var source = NpgsqlDataSource.Create("Host=127.0.0.1;Database=owned-unused;Username=owned-unused");
        await source.DisposeAsync();
        var flush = new RequestArrivalObservationFlushService(buffer, new RequestArrivalObservationStore(source),
            TimeProvider.System, NullLogger<RequestArrivalObservationFlushService>.Instance);
        await flush.FlushAsync(false, default);
        Assert.Empty(buffer.Snapshot().Samples);
    }

    private static RequestArrivalObservations Buffer()
        => new(TimeProvider.System, Options.Create(new RequestArrivalObservationOptions
        {
            Enabled = true,
            UntilUtc = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")
        }));
}

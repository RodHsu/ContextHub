using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Npgsql;

namespace Memory.ChatGptGatewayTests;

public sealed class GatewayRequestArrivalObservationTests(ChatGptGatewayTestEnvironment environment)
    : IClassFixture<ChatGptGatewayTestEnvironment>
{
    private const string HeaderSentinel = "GW_ARRIVAL_HEADER_SENTINEL";
    private const string QuerySentinel = "GW_ARRIVAL_QUERY_SENTINEL";
    private const string ProjectSentinel = "GW_ARRIVAL_PROJECT_SENTINEL";
    private const string TestToken = ChatGptGatewayTestConstants.TestToken;

    [DockerRequiredFact]
    public async Task Default_disabled_gateway_capture_does_not_sample_or_write()
    {
        var factory = environment.GetFactory();
        using var client = CreateAuthorizedClient(factory.CreateClient());
        var buffer = factory.Services.GetRequiredService<RequestArrivalObservations>();
        buffer.Enabled.Should().BeFalse();

        using var response = await SendToolCallAsync(client, 1, "projects_list", new { limit = 5 });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ExtractPayload(await response.Content.ReadAsStringAsync())
            .GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();

        var snapshot = await FlushAndReadAsync(factory.Services, buffer.BootId);
        snapshot.HttpRows.Should().Be(0);
        snapshot.ToolRows.Should().Be(0);
        snapshot.HttpStarted.Should().Be(0);
        snapshot.ToolStarted.Should().Be(0);
    }

    [DockerRequiredFact]
    public async Task Enabled_capture_links_gateway_envelopes_and_tools_and_keeps_rows_redacted_and_bounded()
    {
        await using var factory = environment.GetFactory().WithWebHostBuilder(builder => ConfigureCapture(
            builder, enabled: true, DateTimeOffset.UtcNow.AddMinutes(5), maximumSamples: 6));
        using var client = CreateAuthorizedClient(factory.CreateClient());
        var buffer = factory.Services.GetRequiredService<RequestArrivalObservations>();
        buffer.Enabled.Should().BeTrue();

        using var search = await SendToolCallAsync(client, 1, "memory_search", new
        {
            query = QuerySentinel,
            projectId = ProjectSentinel,
            limit = 1
        }, HeaderSentinel);
        search.StatusCode.Should().Be(HttpStatusCode.OK);
        ExtractPayload(await search.Content.ReadAsStringAsync())
            .GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();

        using var failedSearch = await SendToolCallAsync(client, 2, "memory_search", new
        {
            query = "GW_ARRIVAL_ERROR_QUERY_SENTINEL",
            projectId = ProjectSentinel,
            limit = -1
        }, HeaderSentinel);
        failedSearch.StatusCode.Should().Be(HttpStatusCode.OK);
        ExtractPayload(await failedSearch.Content.ReadAsStringAsync())
            .GetProperty("result").GetProperty("isError").GetBoolean().Should().BeTrue();

        using var projects = await SendToolCallAsync(client, 3, "projects_list", new { limit = 7 }, HeaderSentinel);
        projects.StatusCode.Should().Be(HttpStatusCode.OK);
        ExtractPayload(await projects.Content.ReadAsStringAsync())
            .GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();

        using var boundedCall = await SendToolCallAsync(client, 4, "projects_list", new { limit = 8 }, HeaderSentinel);
        boundedCall.StatusCode.Should().Be(HttpStatusCode.OK);
        ExtractPayload(await boundedCall.Content.ReadAsStringAsync())
            .GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();

        var snapshot = await FlushAndReadAsync(factory.Services, buffer.BootId);
        snapshot.HttpStarted.Should().Be(4);
        snapshot.HttpCompleted.Should().Be(4);
        snapshot.HttpActive.Should().Be(0);
        snapshot.ToolStarted.Should().Be(4);
        snapshot.ToolCompleted.Should().Be(4);
        snapshot.ToolActive.Should().Be(0);
        snapshot.Admitted.Should().Be(6);
        snapshot.DroppedStarts.Should().Be(2);
        snapshot.HttpRows.Should().Be(3);
        snapshot.ToolRows.Should().Be(3);
        snapshot.HttpSuccessRows.Should().Be(3);
        snapshot.ToolErrorRows.Should().Be(1);
        snapshot.MemorySearchRows.Should().Be(2);
        snapshot.OtherRows.Should().Be(1);
        snapshot.ParentLinks.Should().Be(3);
        snapshot.RowsJson.Should().NotContain(QuerySentinel)
            .And.NotContain("GW_ARRIVAL_ERROR_QUERY_SENTINEL")
            .And.NotContain(ProjectSentinel)
            .And.NotContain(HeaderSentinel)
            .And.NotContain(TestToken);
    }

    [DockerRequiredFact]
    public async Task Gateway_auth_and_origin_denials_capture_only_http_error_rows()
    {
        await using var factory = environment.GetFactory().WithWebHostBuilder(builder => ConfigureCapture(
            builder, enabled: true, DateTimeOffset.UtcNow.AddMinutes(5)));
        var buffer = factory.Services.GetRequiredService<RequestArrivalObservations>();

        using var anonymousClient = factory.CreateClient();
        using var unauthenticated = await SendToolCallAsync(
            anonymousClient, 1, "projects_list", new { limit = 1 }, HeaderSentinel);
        unauthenticated.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var authorizedClient = CreateAuthorizedClient(factory.CreateClient());
        using var deniedOrigin = await SendToolCallAsync(
            authorizedClient, 2, "projects_list", new { limit = 1 }, HeaderSentinel, "https://untrusted.example.test");
        deniedOrigin.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var snapshot = await FlushAndReadAsync(factory.Services, buffer.BootId);
        snapshot.HttpStarted.Should().Be(2);
        snapshot.HttpCompleted.Should().Be(2);
        snapshot.HttpActive.Should().Be(0);
        snapshot.HttpRows.Should().Be(2);
        snapshot.HttpErrorRows.Should().Be(2);
        snapshot.ToolStarted.Should().Be(0);
        snapshot.ToolRows.Should().Be(0);
        snapshot.RowsJson.Should().NotContain(HeaderSentinel)
            .And.NotContain(TestToken);
    }

    [DockerRequiredFact]
    public async Task Expired_capture_window_keeps_gateway_behavior_and_writes_no_rows()
    {
        await using var factory = environment.GetFactory().WithWebHostBuilder(builder => ConfigureCapture(
            builder, enabled: true, DateTimeOffset.UtcNow.AddMinutes(-1)));
        using var client = CreateAuthorizedClient(factory.CreateClient());
        var buffer = factory.Services.GetRequiredService<RequestArrivalObservations>();
        buffer.Enabled.Should().BeFalse();

        using var response = await SendToolCallAsync(client, 1, "projects_list", new { limit = 2 });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        ExtractPayload(await response.Content.ReadAsStringAsync())
            .GetProperty("result").TryGetProperty("isError", out _).Should().BeFalse();

        var snapshot = await FlushAndReadAsync(factory.Services, buffer.BootId);
        snapshot.HttpRows.Should().Be(0);
        snapshot.ToolRows.Should().Be(0);
        snapshot.HttpStarted.Should().Be(0);
        snapshot.ToolStarted.Should().Be(0);
    }

    private static HttpClient CreateAuthorizedClient(HttpClient client)
    {
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", TestToken);
        return client;
    }

    private static void ConfigureCapture(
        IWebHostBuilder builder,
        bool enabled,
        DateTimeOffset untilUtc,
        int maximumSamples = 100_000)
        => builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RequestArrivalObservations:Enabled"] = enabled ? "true" : "false",
            ["RequestArrivalObservations:UntilUtc"] = untilUtc.ToString("O", System.Globalization.CultureInfo.InvariantCulture),
            ["RequestArrivalObservations:Capacity"] = "8",
            ["RequestArrivalObservations:MaximumSamples"] = maximumSamples.ToString(System.Globalization.CultureInfo.InvariantCulture)
        }));

    private static async Task<HttpResponseMessage> SendToolCallAsync(
        HttpClient client,
        int id,
        string toolName,
        object arguments,
        string? headerSentinel = null,
        string? origin = null)
    {
        var parameters = new JsonObject
        {
            ["name"] = toolName,
            ["arguments"] = JsonSerializer.SerializeToNode(arguments),
            ["_meta"] = new JsonObject
            {
                ["io.modelcontextprotocol/protocolVersion"] = "2026-07-28",
                ["io.modelcontextprotocol/clientInfo"] = new JsonObject { ["name"] = "GatewayArrivalTests", ["version"] = "1" },
                ["io.modelcontextprotocol/clientCapabilities"] = new JsonObject()
            }
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["id"] = id,
                ["method"] = "tools/call",
                ["params"] = parameters
            })
        };
        request.Headers.Add("MCP-Protocol-Version", "2026-07-28");
        request.Headers.Add("Mcp-Method", "tools/call");
        request.Headers.Add("Mcp-Name", toolName);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");
        if (headerSentinel is not null) request.Headers.Add("X-Arrival-Canary", headerSentinel);
        if (origin is not null) request.Headers.Add("Origin", origin);

        return await client.SendAsync(request);
    }

    private static JsonElement ExtractPayload(string payload)
    {
        if (payload.TrimStart().StartsWith('{'))
        {
            using var jsonDocument = JsonDocument.Parse(payload);
            return jsonDocument.RootElement.Clone();
        }

        var dataLine = payload.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Expected an MCP SSE data line.");
        using var document = JsonDocument.Parse(dataLine["data: ".Length..]);
        return document.RootElement.Clone();
    }

    private static async Task<ObservationSnapshot> FlushAndReadAsync(IServiceProvider services, Guid bootId)
    {
        var flush = services.GetServices<IHostedService>()
            .OfType<RequestArrivalObservationFlushService>()
            .Single();
        await flush.FlushAsync(false, CancellationToken.None);

        await using var command = services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            SELECT
                COALESCE((SELECT http_started FROM monitoring.request_arrival_boots WHERE boot_id=@boot),0)::bigint,
                COALESCE((SELECT http_completed FROM monitoring.request_arrival_boots WHERE boot_id=@boot),0)::bigint,
                COALESCE((SELECT http_active FROM monitoring.request_arrival_boots WHERE boot_id=@boot),0),
                COALESCE((SELECT tool_started FROM monitoring.request_arrival_boots WHERE boot_id=@boot),0)::bigint,
                COALESCE((SELECT tool_completed FROM monitoring.request_arrival_boots WHERE boot_id=@boot),0)::bigint,
                COALESCE((SELECT tool_active FROM monitoring.request_arrival_boots WHERE boot_id=@boot),0),
                COALESCE((SELECT admitted FROM monitoring.request_arrival_boots WHERE boot_id=@boot),0)::bigint,
                COALESCE((SELECT dropped_starts FROM monitoring.request_arrival_boots WHERE boot_id=@boot),0)::bigint,
                (SELECT count(*)::bigint FROM monitoring.request_arrival_samples WHERE boot_id=@boot AND layer='http'),
                (SELECT count(*)::bigint FROM monitoring.request_arrival_samples WHERE boot_id=@boot AND layer='mcp-tool'),
                (SELECT count(*)::bigint FROM monitoring.request_arrival_samples WHERE boot_id=@boot AND layer='http' AND outcome='success'),
                (SELECT count(*)::bigint FROM monitoring.request_arrival_samples WHERE boot_id=@boot AND layer='http' AND outcome='error'),
                (SELECT count(*)::bigint FROM monitoring.request_arrival_samples WHERE boot_id=@boot AND layer='mcp-tool' AND outcome='error'),
                (SELECT count(*)::bigint FROM monitoring.request_arrival_samples WHERE boot_id=@boot AND layer='mcp-tool' AND operation='memory_search'),
                (SELECT count(*)::bigint FROM monitoring.request_arrival_samples WHERE boot_id=@boot AND layer='mcp-tool' AND operation='other'),
                (SELECT count(*)::bigint FROM monitoring.request_arrival_samples t
                    JOIN monitoring.request_arrival_samples h ON h.boot_id=t.boot_id AND h.sequence=t.parent_sequence AND h.layer='http'
                    WHERE t.boot_id=@boot AND t.layer='mcp-tool'),
                COALESCE((SELECT string_agg(row_to_json(s)::text, E'\n')
                    FROM monitoring.request_arrival_samples s WHERE s.boot_id=@boot),'');
            """);
        command.Parameters.AddWithValue("boot", bootId);
        await using var reader = await command.ExecuteReaderAsync();
        if (!await reader.ReadAsync()) throw new InvalidOperationException("Expected arrival observation snapshot.");

        return new ObservationSnapshot(
            reader.GetInt64(0), reader.GetInt64(1), reader.GetInt32(2),
            reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5),
            reader.GetInt64(6), reader.GetInt64(7),
            reader.GetInt64(8), reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11),
            reader.GetInt64(12), reader.GetInt64(13), reader.GetInt64(14), reader.GetInt64(15),
            reader.GetString(16));
    }

    private sealed record ObservationSnapshot(
        long HttpStarted,
        long HttpCompleted,
        int HttpActive,
        long ToolStarted,
        long ToolCompleted,
        int ToolActive,
        long Admitted,
        long DroppedStarts,
        long HttpRows,
        long ToolRows,
        long HttpSuccessRows,
        long HttpErrorRows,
        long ToolErrorRows,
        long MemorySearchRows,
        long OtherRows,
        long ParentLinks,
        string RowsJson);
}

using System.Text.Json;
using Memory.Infrastructure;
using Memory.McpServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Memory.ApiContractTests;

public sealed class ArrivalCaptureClassificationTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Matched_api_route_uses_safe_template_without_recording_project_query_header_or_body(int schema)
    {
        var time = TimeProvider.System;
        var observations = new RequestArrivalObservations(time, Options.Create(new RequestArrivalObservationOptions
        {
            Enabled = true,
            CaptureSchemaVersion = schema,
            UntilUtc = time.GetUtcNow().AddMinutes(5).ToString("O")
        }), new TestClock());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(observations);
        await using var app = builder.Build();
        app.UseRequestArrivalObservations();
        app.MapGet("/api/projects/information/{projectId}", () => Results.StatusCode(202));
        await app.StartAsync();
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Canary", "SECRET_HEADER");
        using var response = await client.GetAsync("/api/projects/information/SECRET_PROJECT?query=SECRET_QUERY");
        Assert.Equal(202, (int)response.StatusCode);
        var row = Assert.Single(observations.Snapshot().Samples);
        Assert.Equal("other", row.Operation); // Migration 057's operation contract is unchanged.
        Assert.Equal(schema == 2 ? "project-read" : null, row.RequestFamily);
        Assert.Equal(schema == 2 ? "GET" : null, row.RequestMethod);
        Assert.Equal(schema == 2 ? "unknown" : null, row.PayloadSizeBucket);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(row));
        Assert.Equal("success", row.Outcome);
    }

    [Theory]
    [InlineData("/api/future/SECRET_IDENTIFIER")]
    [InlineData("/mcp/future/SECRET_IDENTIFIER")]
    public async Task Unknown_api_or_mcp_route_is_explicit_unknown_while_health_is_excluded(string path)
    {
        var observations = new RequestArrivalObservations(TimeProvider.System, Options.Create(new RequestArrivalObservationOptions
        {
            Enabled = true,
            CaptureSchemaVersion = 2,
            UntilUtc = DateTimeOffset.UtcNow.AddMinutes(5).ToString("O")
        }), new TestClock());
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(observations);
        await using var app = builder.Build();
        app.UseRequestArrivalObservations();
        app.MapGet("/api/future/{privateId}", () => Results.Ok());
        app.MapGet("/mcp/future/{privateId}", () => Results.Ok());
        app.MapGet("/health/live", () => Results.Ok());
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var unknown = await client.GetAsync(path);
        using var health = await client.GetAsync("/health/live");
        Assert.True(unknown.IsSuccessStatusCode);
        Assert.True(health.IsSuccessStatusCode);
        var row = Assert.Single(observations.Snapshot().Samples);
        Assert.Equal("unknown", row.RequestFamily);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(row));
    }

    private sealed class TestClock : IRequestArrivalClock
    {
        private long _tick = 1_000_000_000;
        public bool TryReadDomain(out RequestArrivalClockDomain? domain)
        {
            domain = new(Guid.Parse("cd05aad2-d32c-4fb8-8f96-b52722987b0b"), "time:[123]", 0, 0, "tsc");
            return true;
        }
        public bool TryRead(out RequestArrivalClockReading? reading)
        {
            var stamp = Interlocked.Add(ref _tick, 100_000);
            reading = new(stamp, stamp + 1_000, stamp, stamp, stamp);
            return true;
        }
    }
}

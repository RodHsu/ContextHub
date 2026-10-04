using System.Diagnostics;
using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;
using Xunit.Abstractions;

namespace Memory.IntegrationTests;

public sealed class CacheReleaseAcceptanceTests(ContainerTestEnvironment environment, ITestOutputHelper output) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Disabled_cache_rollback_must_ignore_existing_payloads_and_preserve_authorization()
    {
        var provider = environment.GetFactory().Services;
        using var scope = provider.CreateScope();
        var services = scope.ServiceProvider;
        var options = provider.GetRequiredService<IOptions<MemoryOptions>>().Value.RedisCache;
        var cache = services.GetRequiredService<IRedisObjectCache>();
        var key = "rollback-fixture-" + Guid.NewGuid().ToString("N");
        await cache.SetAsync(key, "search-final", new[] { "old-payload" }, TimeSpan.FromMinutes(1), default);
        (await cache.GetAsync<string[]>(key, "search-final", default)).Hit.Should().BeTrue();
        var enabled = options.Enabled;
        try
        {
            options.Enabled = false;
            (await cache.GetAsync<string[]>(key, "search-final", default)).Hit.Should().BeFalse();
            await cache.SetAsync(key, "search-final", new[] { "replacement" }, TimeSpan.FromMinutes(1), default);
            var db = services.GetRequiredService<MemoryDbContext>();
            var owner = await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
            var actor = services.GetRequiredService<IRequestActorAccessor>();
            actor.Current = new(owner.TenantId, owner.Id, owner.Username, TenantUserRole.Member,
                [SecurityScopes.MemoryRead], ["rollback-allowed"], true);
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => services.GetRequiredService<IMemoryService>()
                .SearchAsync(new("probe", ProjectId: "rollback-denied", UseSummaryLayer: false), default));
            var stamp = await services.GetRequiredService<ICacheVersionStore>()
                .GetVersionStampAsync(["rollback-allowed"], actor.Current, false, default);
            stamp.Value.Should().Contain("durable-v1");
        }
        finally { options.Enabled = enabled; }
        (await cache.GetAsync<string[]>(key, "search-final", default)).Value.Should().Equal("old-payload");
    }

    [DockerRequiredFact]
    public async Task Migration_replay_must_not_bump_revision_or_replace_graph_and_metrics_data()
    {
        var provider = environment.GetFactory().Services;
        var source = provider.GetRequiredService<NpgsqlDataSource>();
        async Task<string> StateAsync()
        {
            await using var command = source.CreateCommand("""
                SELECT json_build_object(
                    'receipts', (SELECT json_agg(name ORDER BY name) FROM schema_migrations WHERE name ~ '^05[345]_'),
                    'revisions', (SELECT json_agg(row(scope, revision) ORDER BY scope) FROM cache_scope_revisions),
                    'graph', (SELECT count(*) FROM dashboard_graph_projection),
                    'metrics', (SELECT count(*) FROM monitoring.cache_metric_minutes))::text;
                """);
            return (string)(await command.ExecuteScalarAsync())!;
        }
        var before = await StateAsync();
        var migrator = new DatabaseMigrationHostedService(source, NullLogger<DatabaseMigrationHostedService>.Instance);
        await migrator.StartAsync(default);
        await migrator.StartAsync(default);
        (await StateAsync()).Should().Be(before);
    }

    [DockerRequiredFact]
    public async Task Equal_workload_cold_and_warm_http_runs_must_return_same_scoped_documents()
    {
        var recorder = new RequestRecorder();
        await using var factory = environment.GetFactory().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
        {
            services.RemoveAll<ICacheMetricsRecorder>();
            services.AddSingleton<ICacheMetricsRecorder>(provider =>
            {
                recorder.Inner = provider.GetRequiredService<CacheMetricsBuffer>();
                return recorder;
            });
            services.AddSingleton<IStartupFilter>(new RequestTimingFilter(recorder));
        }));
        using var client = factory.CreateClient();
        using var setup = factory.Services.CreateScope();
        var db = setup.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await db.TenantUsers.AsNoTracking().SingleAsync(x => x.Username == "contract-test-admin");
        var project = "p5-workload-" + Guid.NewGuid().ToString("N");
        var item = new MemoryItem
        {
            TenantId = owner.TenantId,
            OwnerUserId = owner.Id,
            ProjectId = project,
            ExternalKey = Guid.NewGuid().ToString("N"),
            Title = "cache acceptance alpha",
            Summary = "cache acceptance alpha",
            Content = "cache acceptance alpha",
            Importance = 1m,
            Confidence = 1m
        };
        db.MemoryItems.Add(item);
        db.MemoryItemChunks.Add(new MemoryItemChunk
        {
            MemoryItem = item,
            ChunkText = item.Content,
            ChunkIndex = 0,
            ChunkKind = ChunkKind.Document,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        var samples = Setting("CONTEXTHUB_CACHE_BENCHMARK_SAMPLES", 32, 10_000);
        var runs = Setting("CONTEXTHUB_CACHE_BENCHMARK_RUNS", 1, 5);
        var cells = new List<object>();
        for (var run = 0; run < runs; run++)
        {
            // Identical request sequences within each cold/warm pair; namespaces are never cleared.
            var urls = Enumerable.Range(0, samples).Select(i =>
                $"/api/memories/search?query={Uri.EscapeDataString($"cache acceptance alpha OR r{run}q{i}")}&projectId={project}&queryMode=CurrentOnly&useSummaryLayer=false&limit=3").ToArray();
            foreach (var state in new[] { "cold", "warm" })
            {
                var timings = new double[samples];
                var serverTimings = new double[samples];
                long hitsCount = 0, missesCount = 0, originCount = 0;
                for (var i = 0; i < samples; i++)
                {
                    var id = Guid.NewGuid().ToString("N");
                    var frame = new RequestFrame();
                    recorder.Frames.TryAdd(id, frame).Should().BeTrue();
                    using var request = new HttpRequestMessage(HttpMethod.Get, urls[i]);
                    request.Headers.Add("X-Cache-Benchmark", id);
                    var started = Stopwatch.GetTimestamp();
                    using var response = await client.SendAsync(request);
                    response.EnsureSuccessStatusCode();
                    var hits = (await response.Content.ReadFromJsonAsync<MemorySearchHit[]>())!;
                    hits.Should().Contain(x => x.MemoryId == item.Id);
                    hits.Should().OnlyContain(x => x.ProjectId == project);
                    timings[i] = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                    serverTimings[i] = await frame.Completed.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    hitsCount += frame.Hits;
                    missesCount += frame.Misses;
                    originCount += frame.Origins;
                    recorder.Frames.TryRemove(id, out _).Should().BeTrue();
                }
                if (state == "warm") { hitsCount.Should().Be(samples); missesCount.Should().Be(0); originCount.Should().Be(0); }
                else { missesCount.Should().Be(samples); hitsCount.Should().Be(0); originCount.Should().Be(samples); }
                cells.Add(new
                {
                    Run = run,
                    State = state,
                    Samples = samples,
                    Concurrency = 1,
                    Hits = hitsCount,
                    Misses = missesCount,
                    OriginAttempts = originCount,
                    P50Ms = Percentile(timings, .50),
                    P95Ms = Percentile(timings, .95),
                    P99Ms = Percentile(timings, .99),
                    P99Status = samples < 10_000 ? "Exploratory" : "Measured; precision not guaranteed",
                    ElapsedMs = timings.Sum(),
                    SamplesMs = timings,
                    ServerP50Ms = Percentile(serverTimings, .50),
                    ServerP95Ms = Percentile(serverTimings, .95),
                    ServerP99Ms = Percentile(serverTimings, .99),
                    ServerSamplesMs = serverTimings
                });
            }
        }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            EvidenceKind = "IsolatedAuthenticatedTestServerHTTP",
            Dataset = "one real nonempty document; deterministic embedding",
            Comparison = "same-sequence cold/warm; not legacy version comparison or representative Production traffic",
            Transport = "in-process HTTP; server response-completed samples include authentication, serialization and preceding OnCompleted callbacks; no network RTT",
            Background = "application test-host defaults; not controlled Graph on/off",
            Cells = cells
        }));
    }

    private static int Setting(string name, int fallback, int maximum)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (value is null) return fallback;
        if (!int.TryParse(value, out var result) || result < 1 || result > maximum)
            throw new InvalidOperationException($"{name} must be between 1 and {maximum}.");
        return result;
    }

    private static double Percentile(double[] values, double percentile)
        => values.Order().ElementAt((int)Math.Ceiling(values.Length * percentile) - 1);

    private sealed class RequestFrame
    {
        public long Hits, Misses, Origins;
        public TaskCompletionSource<double> Completed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class RequestRecorder : ICacheMetricsRecorder
    {
        public readonly AsyncLocal<RequestFrame?> Current = new();
        public ConcurrentDictionary<string, RequestFrame> Frames { get; } = new();
        public ICacheMetricsRecorder? Inner { get; set; }
        public void Record(string kind, CacheMetricOutcome outcome, string? trafficClass = null, double durationMs = 0)
        {
            var frame = Current.Value;
            if (frame is not null)
            {
                if (kind == "search-final" && outcome == CacheMetricOutcome.Hit) Interlocked.Increment(ref frame.Hits);
                if (kind == "search-final" && outcome == CacheMetricOutcome.Miss) Interlocked.Increment(ref frame.Misses);
                if (kind == CacheMetricKinds.OriginSearch && outcome == CacheMetricOutcome.Observation) Interlocked.Increment(ref frame.Origins);
            }
            Inner?.Record(kind, outcome, trafficClass, durationMs);
        }
    }

    private sealed class RequestTimingFilter(RequestRecorder recorder) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, continuation) =>
            {
                var id = context.Request.Headers["X-Cache-Benchmark"].ToString();
                if (!recorder.Frames.TryGetValue(id, out var frame)) { await continuation(context); return; }
                var previous = recorder.Current.Value;
                recorder.Current.Value = frame;
                var started = Stopwatch.GetTimestamp();
                context.Response.OnCompleted(() =>
                {
                    frame.Completed.TrySetResult(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
                    return Task.CompletedTask;
                });
                try { await continuation(context); }
                finally { recorder.Current.Value = previous; }
            });
            next(app);
        };
    }

    [DockerRequiredFact]
    public async Task Capacity_inventory_must_complete_in_read_only_transaction_and_find_all_cache_relations()
    {
        using var stream = typeof(CacheReleaseAcceptanceTests).Assembly.GetManifestResourceStream("cache-capacity.sql")!;
        using var sql = new StreamReader(stream);
        var source = environment.GetFactory().Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = source.CreateCommand(await sql.ReadToEndAsync());
        await using var reader = await command.ExecuteReaderAsync();
        var relations = new List<object>();
        var metricTables = 0;
        while (await reader.ReadAsync())
        {
            var schema = reader.GetString(0);
            var table = reader.GetString(1);
            reader.GetBoolean(2).Should().BeTrue($"{schema}.{table} must be present after migration");
            if (schema == "monitoring") metricTables++;
            relations.Add(new { Schema = schema, Table = table, TotalBytes = reader.GetInt64(7) });
        }
        metricTables.Should().Be(3);
        relations.Should().HaveCount(8);
        while (await reader.NextResultAsync())
            while (await reader.ReadAsync()) { }
        output.WriteLine(JsonSerializer.Serialize(new
        {
            EvidenceKind = "ReadOnlyCapacityInventory",
            Environment = "isolated PostgreSQL testcontainer",
            ThirtyDayPlanOnly = true,
            RetentionPolicyChanged = false,
            Relations = relations
        }));
    }
}

using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

// This gate is explicit: an ordinary test run must not silently substitute a same-version restart.
public sealed class ManifestRollbackFactAttribute : FactAttribute
{
    public ManifestRollbackFactAttribute()
    {
        if (!DockerTestGate.Current.IsAvailable)
            Skip = DockerTestGate.Current.Reason;
        else if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTEXTHUB_MANIFEST_ROLLBACK_DLL")) ||
                 string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTEXTHUB_MANIFEST_ROLLBACK_SHA256")) ||
                 string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTEXTHUB_MANIFEST_ROLLBACK_RECEIPT")) ||
                 string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("CONTEXTHUB_MANIFEST_ROLLBACK_RECEIPT_SHA256")))
            Skip = "A separately built, pinned compatible app is required for actual cross-version manifest acceptance.";
    }
}

public sealed class MemoryManifestRollbackIntegrationTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [ManifestRollbackFact]
    public async Task Compatible_app_rejects_manifest_execution_and_roll_forward_replays_every_frozen_page()
    {
        var dll = Path.GetFullPath(Environment.GetEnvironmentVariable("CONTEXTHUB_MANIFEST_ROLLBACK_DLL")!);
        var expectedHash = Environment.GetEnvironmentVariable("CONTEXTHUB_MANIFEST_ROLLBACK_SHA256")!;
        await using var binary = new FileStream(dll, FileMode.Open, FileAccess.Read, FileShare.Read);
        Convert.ToHexString(await SHA256.HashDataAsync(binary)).Should().Be(expectedHash.ToUpperInvariant());
        using var compatibleBundle = await HoldCompatibleBundle(dll);
        var factory = environment.GetFactory();
        var configuration = factory.Services.GetRequiredService<IConfiguration>();
        var request = new MemoryManifestReadRequest("ContextHub", Guid.NewGuid(), PageSize: 71, AllAuthorizedProjects: false);
        using var client = factory.CreateClient();
        Dictionary<Guid, int> seededVersions;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var user = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
            var seeded = Enumerable.Range(0, 237).Select(i => new MemoryItem
            {
                TenantId = user.TenantId,
                OwnerUserId = user.Id,
                ProjectId = request.ProjectId,
                ExternalKey = "rollback-fixture-" + request.RequestId.ToString("N") + "-" + i,
                Version = i % 4 + 1,
                Content = "fixture-body-must-not-be-returned"
            }).ToArray();
            db.MemoryItems.AddRange(seeded);
            await db.SaveChangesAsync();
            seededVersions = seeded.ToDictionary(x => x.Id, x => x.Version);
        }
        var beforePages = await ReadAll(client, request);
        beforePages.Count.Should().BeGreaterThan(1);
        var frozenEntries = beforePages.SelectMany(x => x.Items).ToDictionary(x => x.MemoryId);
        foreach (var entry in seededVersions)
        {
            frozenEntries.Should().ContainKey(entry.Key);
            frozenEntries[entry.Key].Version.Should().Be(entry.Value);
            frozenEntries[entry.Key].ProjectId.Should().Be(request.ProjectId);
        }
        var first = beforePages[0];
        first.NextCursor.Should().NotBeNull();
        string opaqueResult;
        string runId;
        int total;
        Counts before;
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var stored = await db.KnowledgeGovernanceSnapshots.AsNoTracking().SingleAsync(x => x.Id == first.SnapshotId);
            opaqueResult = stored.ResultJson; // Includes signing state: compare only in RAM, never log or hash it.
            runId = stored.GovernanceRunId;
            total = stored.TotalCount;
            before = await BusinessCounts(db);
        }
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = Path.GetDirectoryName(dll)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(dll);
        // Reuse only this test fixture's existing authority; no production configuration is read.
        foreach (var key in new[]
        {
            "ConnectionStrings:Postgres", "ConnectionStrings:Redis", "Embeddings:Provider", "Embeddings:Profile",
            "Embeddings:ModelKey", "Embeddings:Dimensions", "Embeddings:MaxTokens", "Memory:Namespace",
            "ContextHub:Security:RequireAuthentication", "ContextHub:Security:BootstrapToken",
            "ContextHub:Security:BootstrapTenantSlug", "ContextHub:Security:BootstrapUsername",
            "ContextHub:Security:BootstrapAllowedProjectIds", "ContextHub:Security:AllowedMcpOrigins:0"
        })
            start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = configuration[key];
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["Logging__LogLevel__Default"] = "None";
        start.Environment["DatabaseLogging__MinimumLevel"] = "Critical";
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        start.Environment["ASPNETCORE_URLS"] = "http://127.0.0.1:" + port;
        await factory.DisposeAsync();
        using var process = new Process { StartInfo = start };
        process.Start().Should().BeTrue();
        var stdout = Drain(process.StandardOutput);
        var stderr = Drain(process.StandardError);
        try
        {
            using var oldClient = new HttpClient { BaseAddress = new Uri("http://127.0.0.1:" + port), Timeout = TimeSpan.FromSeconds(10) };
            oldClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", MemoryApplicationFactory.TestBootstrapToken);
            await WaitForReady(oldClient, process);
            using var unsupported = await oldClient.PostAsJsonAsync("/api/memories/manifest/read", request);
            unsupported.StatusCode.Should().Be(HttpStatusCode.NotFound);
            using var rejected = await oldClient.PostAsJsonAsync("/api/knowledge-reviews/execute",
                new GovernanceBatchExecuteRequest(runId, [request.ProjectId], "manifest:" + first.SnapshotId.ToString("N")));
            rejected.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            (await rejected.Content.ReadAsStringAsync()).Should().Contain("must not exceed 128 characters");
            await using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
                .UseNpgsql(environment.PostgresConnectionString, x => x.UseVector()).Options);
            var retained = await db.KnowledgeGovernanceSnapshots.AsNoTracking().SingleAsync(x => x.Id == first.SnapshotId);
            // Avoid assertion failure output containing CursorKey: compare booleans, not the opaque strings.
            (retained.ResultJson == opaqueResult).Should().BeTrue();
            retained.TotalCount.Should().Be(total);
            (await BusinessCounts(db)).Should().Be(before);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
        await environment.RestartApplicationAsync();
        using var resumed = environment.GetFactory().CreateClient();
        var afterPages = await ReadAll(resumed, request);
        (JsonSerializer.Serialize(afterPages) == JsonSerializer.Serialize(beforePages)).Should().BeTrue();
        var expectedIds = beforePages.SelectMany(x => x.Items).Select(x => x.MemoryId).ToArray();
        expectedIds.Distinct().Count().Should().Be(first.AuthorizedTotal);
        using var replay = await resumed.PostAsJsonAsync("/api/memories/manifest/read",
            request with { SnapshotId = first.SnapshotId, Cursor = first.NextCursor });
        replay.EnsureSuccessStatusCode();
        var replayPage = (await replay.Content.ReadFromJsonAsync<MemoryManifestPage>())!;
        (JsonSerializer.Serialize(replayPage) == JsonSerializer.Serialize(beforePages[1])).Should().BeTrue();
        using var verify = await resumed.PostAsJsonAsync("/api/memories/manifest/verify",
            new MemoryManifestVerifyRequest(request.ProjectId, first.SnapshotId, expectedIds.Take(100).ToArray()));
        verify.EnsureSuccessStatusCode();
        (await verify.Content.ReadFromJsonAsync<List<MemoryManifestVerification>>())!.Should().OnlyContain(x => x.State == "Unchanged");
        using var finalScope = environment.GetFactory().Services.CreateScope();
        var finalDb = finalScope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var final = await finalDb.KnowledgeGovernanceSnapshots.AsNoTracking().SingleAsync(x => x.Id == first.SnapshotId);
        (final.ResultJson == opaqueResult).Should().BeTrue();
        final.TotalCount.Should().Be(total);
        (await BusinessCounts(finalDb)).Should().Be(before);
        using var fresh = await resumed.PostAsJsonAsync("/api/memories/manifest/read", request with { RequestId = Guid.NewGuid() });
        fresh.EnsureSuccessStatusCode();
        var freshPage = (await fresh.Content.ReadFromJsonAsync<MemoryManifestPage>())!;
        freshPage.SnapshotId.Should().NotBe(first.SnapshotId);
        freshPage.CountInvariantSatisfied.Should().BeTrue();
        freshPage.AuthorizedTotal.Should().Be(first.AuthorizedTotal);
    }

    private static async Task<HeldBundle> HoldCompatibleBundle(string dll)
    {
        var receiptPath = Environment.GetEnvironmentVariable("CONTEXTHUB_MANIFEST_ROLLBACK_RECEIPT")!;
        var receiptHash = Environment.GetEnvironmentVariable("CONTEXTHUB_MANIFEST_ROLLBACK_RECEIPT_SHA256")!;
        var held = new HeldBundle();
        try
        {
            var receipt = held.Open(receiptPath);
            Convert.ToHexString(await SHA256.HashDataAsync(receipt)).Should().Be(receiptHash.ToUpperInvariant());
            receipt.Position = 0;
            using var document = await JsonDocument.ParseAsync(receipt);
            var root = document.RootElement;
            root.GetProperty("CompatibleCommit").GetString().Should().Be("b348ddc35bea2d3e8d2fb07dfa3430f9eb98f617");
            root.GetProperty("BuildExit").GetInt32().Should().Be(0);
            var directory = Path.GetDirectoryName(dll)!;
            var productNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("Assemblies").EnumerateArray())
            {
                var name = item.GetProperty("Name").GetString()!;
                Path.IsPathFullyQualified(name).Should().BeFalse();
                name.Split('/').Should().NotContain(x => x == "" || x == "." || x == "..");
                name.Should().NotContain("\\").And.NotContain(":");
                name.EndsWith(".dll", StringComparison.Ordinal).Should().BeTrue();
                productNames.Add(name).Should().BeTrue();
                var path = Path.GetFullPath(Path.Combine(directory, name));
                path.StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    .Should().BeTrue();
                var file = held.Open(path);
                Convert.ToHexString(await SHA256.HashDataAsync(file))
                    .Should().Be(item.GetProperty("Sha256").GetString()!.ToUpperInvariant());
            }
            foreach (var required in new[] { "Memory.McpServer.dll", "Memory.Application.dll", "Memory.Infrastructure.dll", "Memory.Domain.dll" })
                productNames.Should().Contain(required);
            productNames.Should().BeEquivalentTo(Directory.GetFiles(directory, "*.dll", SearchOption.AllDirectories)
                .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/')));
            var supportNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var item in root.GetProperty("RuntimeSupport").EnumerateArray())
            {
                var name = item.GetProperty("Name").GetString()!;
                name.Should().BeOneOf("Memory.McpServer.deps.json", "Memory.McpServer.runtimeconfig.json");
                supportNames.Add(name).Should().BeTrue();
                var file = held.Open(Path.Combine(directory, name));
                Convert.ToHexString(await SHA256.HashDataAsync(file))
                    .Should().Be(item.GetProperty("Sha256").GetString()!.ToUpperInvariant());
            }
            supportNames.Should().HaveCount(2);
            return held;
        }
        catch
        {
            held.Dispose();
            throw;
        }
    }

    private sealed class HeldBundle : IDisposable
    {
        private readonly List<FileStream> files = [];
        public FileStream Open(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(stream);
            return stream;
        }
        public void Dispose()
        {
            foreach (var file in files) file.Dispose();
        }
    }

    private static async Task<List<MemoryManifestPage>> ReadAll(HttpClient client, MemoryManifestReadRequest request)
    {
        var pages = new List<MemoryManifestPage>();
        MemoryManifestReadRequest next = request;
        do
        {
            using var response = await client.PostAsJsonAsync("/api/memories/manifest/read", next);
            response.EnsureSuccessStatusCode();
            var page = (await response.Content.ReadFromJsonAsync<MemoryManifestPage>())!;
            page.CountInvariantSatisfied.Should().BeTrue();
            pages.Add(page);
            next = request with { SnapshotId = page.SnapshotId, Cursor = page.NextCursor };
        } while (!pages[^1].EndOfSnapshot && pages.Count < 100);
        pages[^1].EndOfSnapshot.Should().BeTrue();
        return pages;
    }

    private static async Task WaitForReady(HttpClient client, Process process)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!timeout.IsCancellationRequested)
        {
            if (process.HasExited) throw new InvalidOperationException("COMPATIBLE_TEST_APP_EXITED_BEFORE_READY");
            try
            {
                using var response = await client.GetAsync("/health/ready", timeout.Token);
                if (response.IsSuccessStatusCode) return;
            }
            catch (HttpRequestException) { }
            await Task.Delay(200, timeout.Token);
        }
        throw new TimeoutException("COMPATIBLE_TEST_APP_READY_TIMEOUT");
    }

    private static async Task Drain(StreamReader reader)
    {
        var buffer = new char[4096];
        while (await reader.ReadAsync(buffer) > 0) { }
    }

    private sealed record Counts(int Memories, int Findings, int Actions, int Jobs, int Receipts, int Snapshots);
    private static async Task<Counts> BusinessCounts(MemoryDbContext db)
        => new(await db.MemoryItems.CountAsync(), await db.GovernanceFindings.CountAsync(),
            await db.SuggestedActions.CountAsync(), await db.MemoryJobs.CountAsync(),
            await db.GovernanceRunReceipts.CountAsync(), await db.KnowledgeGovernanceSnapshots.CountAsync());
}

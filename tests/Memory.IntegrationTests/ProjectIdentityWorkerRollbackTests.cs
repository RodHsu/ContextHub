using System.Diagnostics;
using System.Net;
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
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class WorkerRollbackFactAttribute : FactAttribute
{
    public WorkerRollbackFactAttribute()
    {
        if (!DockerTestGate.Current.IsAvailable)
            Skip = DockerTestGate.Current.Reason;
        else if (new[] { "COMPATIBLE", "CANDIDATE" }.Any(version =>
                     new[] { "DLL", "RECEIPT", "RECEIPT_SHA256" }.Any(field =>
                         string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable($"CONTEXTHUB_WORKER_{version}_{field}")))))
            Skip = "Separately built and pinned compatible and candidate Worker bundles are required.";
    }
}

public sealed class ProjectIdentityWorkerRollbackTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [WorkerRollbackFact]
    public async Task Actual_workers_preserve_alias_data_and_owner_tenant_model_boundaries_across_rollback_restart_and_roll_forward()
    {
        using var compatible = await WorkerBundle.Open("COMPATIBLE", "b348ddc35bea2d3e8d2fb07dfa3430f9eb98f617");
        using var candidate = await WorkerBundle.Open("CANDIDATE", "900b4fefecf2fda3e449b99c244c9e580e10f8bc");
        var factory = environment.GetFactory();
        using var seed = factory.Services.CreateScope();
        var db = seed.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var config = seed.ServiceProvider.GetRequiredService<IConfiguration>();
        var owner = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var tenant = new Tenant { Slug = "worker-" + Guid.NewGuid().ToString("N"), DisplayName = "Worker isolation fixture" };
        var peer = new TenantUser { TenantId = owner.TenantId, Username = "worker-peer-" + Guid.NewGuid().ToString("N") };
        var foreign = new TenantUser { TenantId = tenant.Id, Username = "worker-foreign-" + Guid.NewGuid().ToString("N") };
        db.Tenants.Add(tenant);
        db.TenantUsers.AddRange(peer, foreign);
        await db.SaveChangesAsync();
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var chunks = new List<MemoryItemChunk>();
        var owners = new[] { owner, owner, owner, peer, foreign };
        var spellings = new[] { "TT", "Tt", "tT", "tt", "TT" };
        for (var index = 0; index < owners.Length; index++)
        {
            var item = new MemoryItem
            {
                TenantId = owners[index].TenantId,
                OwnerUserId = owners[index].Id,
                ProjectId = spellings[index] + suffix,
                ExternalKey = "worker-fixture-" + index,
                Title = "Worker fixture",
                Content = "Deterministic Worker scope fixture " + index,
                Summary = "Worker fixture",
                Status = MemoryStatus.Active
            };
            var chunk = new MemoryItemChunk { MemoryItem = item, ChunkText = item.Content, ChunkKind = ChunkKind.Document };
            db.MemoryItems.Add(item);
            db.MemoryItemChunks.Add(chunk);
            chunks.Add(chunk);
        }
        await db.SaveChangesAsync();
        var provider = seed.ServiceProvider.GetRequiredService<IEmbeddingProvider>();
        var vectors = seed.ServiceProvider.GetRequiredService<IVectorStore>();
        foreach (var chunk in chunks)
            await vectors.ReplaceChunkVectorAsync(chunk.Id,
                await provider.EmbedAsync(chunk.ChunkText, EmbeddingPurpose.Document, default), default);
        var ownedIds = chunks.Take(3).Select(x => x.Id).ToArray();
        var foreignIds = chunks.Skip(3).Select(x => x.Id).ToArray();
        var foreignBefore = await Snapshot(seed.ServiceProvider, foreignIds);
        var recordsBefore = await db.MemoryItems.AsNoTracking().Where(x => x.ProjectId.EndsWith(suffix))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.ProjectId, x.TenantId, x.OwnerUserId, x.ExternalKey, x.Version }).ToListAsync();
        var completed = new List<Guid>();
        var settled = new List<Guid>();
        var stages = new[] { (candidate, "TT"), (compatible, "tt"), (compatible, "tT"), (candidate, "Tt"), (candidate, "tt") };
        foreach (var (bundle, spelling) in stages)
        {
            var beforeRestartVectors = await Snapshot(seed.ServiceProvider, ownedIds);
            var beforeRestartLedger = await JobLedger(seed.ServiceProvider, settled);
            await using var worker = await RunningWorker.Start(bundle.Dll, config);
            (await Snapshot(seed.ServiceProvider, ownedIds)).SequenceEqual(beforeRestartVectors).Should().BeTrue();
            (await JobLedger(seed.ServiceProvider, settled) == beforeRestartLedger).Should().BeTrue();
            var job = NewJob(owner, spelling + suffix, provider.ModelKey);
            db.MemoryJobs.Add(job);
            await db.SaveChangesAsync();
            await WaitForJob(factory.Services, job.Id, MemoryJobStatus.Completed);
            completed.Add(job.Id);
            settled.Add(job.Id);
            using var read = factory.Services.CreateScope();
            var current = read.ServiceProvider.GetRequiredService<MemoryDbContext>();
            (await Snapshot(read.ServiceProvider, foreignIds)).SequenceEqual(foreignBefore).Should().BeTrue();
            foreach (var id in ownedIds)
                (await current.MemoryChunkVectors.CountAsync(x => x.ChunkId == id && x.Status == "Active" && x.ModelKey == provider.ModelKey))
                    .Should().Be(1);
            (await current.MemoryJobs.CountAsync(x => completed.Contains(x.Id) && x.Status == MemoryJobStatus.Completed))
                .Should().Be(completed.Count);
            var beforeFailure = await Snapshot(read.ServiceProvider, ownedIds);
            var rejected = NewJob(owner, spelling + suffix, "unconfigured-worker-test-model");
            db.MemoryJobs.Add(rejected);
            await db.SaveChangesAsync();
            await WaitForJob(factory.Services, rejected.Id, MemoryJobStatus.Failed);
            settled.Add(rejected.Id);
            (await Snapshot(read.ServiceProvider, ownedIds)).SequenceEqual(beforeFailure).Should().BeTrue();
            (await Snapshot(read.ServiceProvider, foreignIds)).SequenceEqual(foreignBefore).Should().BeTrue();
        }
        using var final = factory.Services.CreateScope();
        var finalDb = final.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var recordsAfter = await finalDb.MemoryItems.AsNoTracking().Where(x => x.ProjectId.EndsWith(suffix))
            .OrderBy(x => x.Id).Select(x => new { x.Id, x.ProjectId, x.TenantId, x.OwnerUserId, x.ExternalKey, x.Version }).ToListAsync();
        (JsonSerializer.Serialize(recordsBefore) == JsonSerializer.Serialize(recordsAfter)).Should().BeTrue();
    }

    private static MemoryJob NewJob(TenantUser owner, string projectId, string modelKey) => new()
    {
        TenantId = owner.TenantId,
        OwnerUserId = owner.Id,
        ProjectId = projectId,
        JobType = MemoryJobType.Reindex,
        Status = MemoryJobStatus.Pending,
        PayloadJson = JsonSerializer.Serialize(new { ModelKey = modelKey, MemoryItemId = (Guid?)null, ProjectId = projectId }),
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-1)
    };

    private static async Task WaitForJob(IServiceProvider services, Guid id, MemoryJobStatus expected)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            using var scope = services.CreateScope();
            var status = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>().MemoryJobs
                .AsNoTracking().Where(x => x.Id == id).Select(x => x.Status).SingleAsync(deadline.Token);
            if (status is MemoryJobStatus.Completed or MemoryJobStatus.Failed)
            {
                status.Should().Be(expected);
                return;
            }
            await Task.Delay(100, deadline.Token);
        }
        throw new TimeoutException("WORKER_TEST_JOB_TIMEOUT");
    }

    private static async Task<string[]> Snapshot(IServiceProvider services, Guid[] ids)
    {
        await using var command = services.GetRequiredService<NpgsqlDataSource>().CreateCommand("""
            SELECT id::text, chunk_id::text, model_key, dimension::text, status, embedding::text, created_at::text
            FROM memory_chunk_vectors WHERE chunk_id = ANY(@ids) ORDER BY id;
            """);
        command.Parameters.AddWithValue("ids", ids);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<string>();
        while (await reader.ReadAsync())
            rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(reader.GetString).ToArray()));
        return rows.ToArray(); // Fixture vectors only; never publish raw snapshots or process configuration.
    }

    private static async Task<string> JobLedger(IServiceProvider services, List<Guid> ids)
    {
        using var scope = services.CreateScope();
        var rows = await scope.ServiceProvider.GetRequiredService<MemoryDbContext>().MemoryJobs.AsNoTracking()
            .Where(x => ids.Contains(x.Id)).OrderBy(x => x.Id)
            .Select(x => new { x.Id, x.TenantId, x.OwnerUserId, x.ProjectId, x.Status, x.StartedAt, x.CompletedAt, x.PayloadJson })
            .ToListAsync();
        return JsonSerializer.Serialize(rows); // Compare only in RAM; no error text or process configuration.
    }

    private sealed class WorkerBundle : IDisposable
    {
        private readonly List<FileStream> files = [];
        public required string Dll { get; init; }
        private FileStream Hold(string path)
        {
            for (var current = new FileInfo(Path.GetFullPath(path)).Directory; current is not null; current = current.Parent)
                current.Attributes.HasFlag(FileAttributes.ReparsePoint).Should().BeFalse();
            File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint).Should().BeFalse();
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            files.Add(stream);
            return stream;
        }
        public static async Task<WorkerBundle> Open(string version, string commit)
        {
            var bundle = new WorkerBundle { Dll = Environment.GetEnvironmentVariable($"CONTEXTHUB_WORKER_{version}_DLL")! };
            try
            {
                var receipt = bundle.Hold(Environment.GetEnvironmentVariable($"CONTEXTHUB_WORKER_{version}_RECEIPT")!);
                Convert.ToHexString(await SHA256.HashDataAsync(receipt)).Should()
                    .Be(Environment.GetEnvironmentVariable($"CONTEXTHUB_WORKER_{version}_RECEIPT_SHA256")!.ToUpperInvariant());
                receipt.Position = 0;
                using var doc = await JsonDocument.ParseAsync(receipt);
                doc.RootElement.GetProperty("commit").GetString().Should().Be(commit);
                doc.RootElement.GetProperty("buildExitCode").GetInt32().Should().Be(0);
                var directory = Path.GetFullPath(Path.GetDirectoryName(bundle.Dll)!);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var item in doc.RootElement.GetProperty("files").EnumerateArray())
                {
                    var name = item.GetProperty("name").GetString()!;
                    Path.IsPathFullyQualified(name).Should().BeFalse();
                    name.Split('/').Should().NotContain(x => x == "" || x == "." || x == "..");
                    name.Should().NotContain("\\").And.NotContain(":");
                    names.Add(name).Should().BeTrue();
                    var path = Path.GetFullPath(Path.Combine(directory, name));
                    path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase).Should().BeTrue();
                    var stream = bundle.Hold(path);
                    Convert.ToHexString(await SHA256.HashDataAsync(stream)).Should().Be(item.GetProperty("sha256").GetString()!.ToUpperInvariant());
                }
                names.Should().Contain(["Memory.Worker.dll", "Memory.Application.dll", "Memory.Infrastructure.dll", "Memory.Domain.dll",
                    "Memory.Worker.deps.json", "Memory.Worker.runtimeconfig.json"]);
                names.Should().BeEquivalentTo(Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    .Where(path => path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".deps.json", StringComparison.OrdinalIgnoreCase)
                        || path.EndsWith(".runtimeconfig.json", StringComparison.OrdinalIgnoreCase))
                    .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/')));
                return bundle;
            }
            catch { bundle.Dispose(); throw; }
        }
        public void Dispose() { foreach (var file in files) file.Dispose(); }
    }

    private sealed class RunningWorker : IAsyncDisposable
    {
        private readonly Process process;
        private readonly Task stdout;
        private readonly Task stderr;
        private RunningWorker(Process process)
        {
            this.process = process;
            stdout = Drain(process.StandardOutput);
            stderr = Drain(process.StandardError);
        }
        public static async Task<RunningWorker> Start(string dll, IConfiguration config)
        {
            var start = new ProcessStartInfo("dotnet")
            {
                WorkingDirectory = Path.GetDirectoryName(dll)!,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            start.ArgumentList.Add(dll);
            foreach (var key in new[] { "ConnectionStrings:Postgres", "ConnectionStrings:Redis", "Embeddings:Provider", "Embeddings:Profile",
                         "Embeddings:ModelKey", "Embeddings:Dimensions", "Embeddings:MaxTokens", "Memory:Namespace",
                         "ContextHub:Security:RequireAuthentication", "ContextHub:Security:BootstrapToken", "ContextHub:Security:BootstrapTenantSlug",
                         "ContextHub:Security:BootstrapUsername", "ContextHub:Security:BootstrapAllowedProjectIds", "ContextHub:Security:AllowedMcpOrigins:0" })
                start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = config[key];
            foreach (var key in new[] { "ASPNETCORE_ENVIRONMENT", "DOTNET_ENVIRONMENT" }) start.Environment[key] = "Testing";
            start.Environment["Logging__LogLevel__Default"] = "None";
            start.Environment["DatabaseLogging__MinimumLevel"] = "Critical";
            start.Environment["TelemetryRetention__Enabled"] = "false";
            start.Environment["MemoryDataRetention__Enabled"] = "false";
            start.Environment["AutonomousGovernance__InternalMaturedDeleteEnabled"] = "false";
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            start.Environment["ASPNETCORE_URLS"] = $"http://127.0.0.1:{port}";
            var worker = new RunningWorker(Process.Start(start)!);
            try
            {
                using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(5) };
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                while (!deadline.IsCancellationRequested)
                {
                    if (worker.process.HasExited) throw new InvalidOperationException("OWNED_TEST_WORKER_EXITED_BEFORE_READY");
                    try { using var response = await client.GetAsync("/health/ready", deadline.Token); if (response.IsSuccessStatusCode) return worker; }
                    catch (HttpRequestException) { }
                    catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                    await Task.Delay(100, deadline.Token);
                }
                throw new TimeoutException("OWNED_TEST_WORKER_READY_TIMEOUT");
            }
            catch { await worker.DisposeAsync(); throw; }
        }
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
                await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(15));
            }
            finally { process.Dispose(); }
        }
        private static async Task Drain(StreamReader reader)
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory()) != 0) { }
        }
    }
}

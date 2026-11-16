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

public sealed class WorkerRollbackTheoryAttribute : TheoryAttribute
{
    public WorkerRollbackTheoryAttribute()
    {
        Skip = new WorkerRollbackFactAttribute().Skip;
    }
}

public sealed class ProjectIdentityWorkerRollbackTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [WorkerRollbackFact]
    public async Task Actual_workers_preserve_alias_data_and_owner_tenant_model_boundaries_across_rollback_restart_and_roll_forward()
    {
        using var compatible = await WorkerBundle.Open("COMPATIBLE", "16db49d08e9ee64fd7b7ee7893ad46276f1d91a0");
        using var candidate = await WorkerBundle.Open("CANDIDATE", "80ff15d2481762003b65cca13a359f6c3f8f58ca");
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
            var activeVectorIdsBefore = await db.MemoryChunkVectors.AsNoTracking()
                .Where(x => ownedIds.Contains(x.ChunkId) && x.Status == "Active" && x.ModelKey == provider.ModelKey)
                .ToDictionaryAsync(x => x.ChunkId, x => x.Id);
            activeVectorIdsBefore.Keys.Should().BeEquivalentTo(ownedIds);
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
            {
                (await current.MemoryChunkVectors.CountAsync(x => x.ChunkId == id && x.Status == "Active" && x.ModelKey == provider.ModelKey))
                    .Should().Be(1);
                (await current.MemoryChunkVectors.AsNoTracking()
                    .Where(x => x.ChunkId == id && x.Status == "Active" && x.ModelKey == provider.ModelKey)
                    .Select(x => x.Id).SingleAsync()).Should().NotBe(activeVectorIdsBefore[id]);
                (await current.MemoryChunkVectors.CountAsync(x => x.Id == activeVectorIdsBefore[id] && x.ChunkId == id
                    && x.Status == "Superseded" && x.ModelKey == provider.ModelKey)).Should().Be(1);
            }
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

    [WorkerRollbackTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_candidate_vector_transaction_recovers_the_same_stale_job_in_a_compatible_worker(bool afterSupersede)
    {
        using var compatible = await WorkerBundle.Open("COMPATIBLE", "16db49d08e9ee64fd7b7ee7893ad46276f1d91a0");
        using var candidate = await WorkerBundle.Open("CANDIDATE", "80ff15d2481762003b65cca13a359f6c3f8f58ca");
        var factory = environment.GetFactory();
        using var seed = factory.Services.CreateScope();
        var db = seed.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var services = seed.ServiceProvider;
        var owner = await db.TenantUsers.SingleAsync(x => x.Username == "contract-test-admin");
        var peer = new TenantUser { TenantId = owner.TenantId, Username = "interruption-peer-" + Guid.NewGuid().ToString("N") };
        db.TenantUsers.Add(peer);
        await db.SaveChangesAsync();
        var suffix = "-" + Guid.NewGuid().ToString("N");
        var chunks = new List<MemoryItemChunk>();
        foreach (var (actor, spelling) in new[] { (owner, "TT"), (owner, "Tt"), (peer, "tt") })
        {
            var item = new MemoryItem
            {
                TenantId = actor.TenantId,
                OwnerUserId = actor.Id,
                ProjectId = spelling + suffix,
                ExternalKey = "interruption-fixture-" + chunks.Count,
                Title = "Interruption fixture",
                Content = "Deterministic interruption fixture " + chunks.Count,
                Summary = "Interruption fixture",
                Status = MemoryStatus.Active
            };
            var chunk = new MemoryItemChunk { MemoryItem = item, ChunkText = item.Content, ChunkKind = ChunkKind.Document };
            db.MemoryItems.Add(item);
            db.MemoryItemChunks.Add(chunk);
            chunks.Add(chunk);
        }
        await db.SaveChangesAsync();
        var provider = services.GetRequiredService<IEmbeddingProvider>();
        foreach (var chunk in chunks)
            await services.GetRequiredService<IVectorStore>().ReplaceChunkVectorAsync(chunk.Id,
                await provider.EmbedAsync(chunk.ChunkText, EmbeddingPurpose.Document, default), default);
        var ownedIds = chunks.Take(2).Select(x => x.Id).ToArray();
        var activeVectorIdsBefore = await db.MemoryChunkVectors.AsNoTracking()
            .Where(x => ownedIds.Contains(x.ChunkId) && x.Status == "Active" && x.ModelKey == provider.ModelKey)
            .ToDictionaryAsync(x => x.ChunkId, x => x.Id);
        activeVectorIdsBefore.Keys.Should().BeEquivalentTo(ownedIds);
        var allIds = chunks.Select(x => x.Id).ToArray();
        var before = await Snapshot(services, allIds);
        var foreignBefore = await Snapshot(services, [chunks[2].Id]);
        var config = services.GetRequiredService<IConfiguration>();
        var source = services.GetRequiredService<NpgsqlDataSource>();
        var applicationName = "owned-worker-interruption-" + Guid.NewGuid().ToString("N");
        var job = NewJob(owner, "tT" + suffix, provider.ModelKey);

        await using (var worker = await RunningWorker.Start(candidate.Dll, config, applicationName))
        {
            var advisoryKey = Random.Shared.NextInt64(1, long.MaxValue);
            await using var insertionFault = afterSupersede ? await InsertFault.Create(source, applicationName, advisoryKey) : null;
            await using var blocker = await source.OpenConnectionAsync();
            await using var transaction = await blocker.BeginTransactionAsync();
            await using var command = new NpgsqlCommand(afterSupersede
                ? "SELECT pg_advisory_xact_lock(@key);"
                : "LOCK TABLE memory_chunk_vectors IN SHARE MODE;", blocker, transaction);
            if (afterSupersede) command.Parameters.AddWithValue("key", advisoryKey);
            await command.ExecuteNonQueryAsync();
            db.MemoryJobs.Add(job);
            await db.SaveChangesAsync();
            await WaitForVectorLock(source, applicationName, afterSupersede);
            (await db.MemoryJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id)).Status.Should().Be(MemoryJobStatus.Running);
            await worker.DisposeAsync(); // Only this test's exact child process is interrupted.
            (await Snapshot(services, allIds)).SequenceEqual(before).Should().BeTrue();
            await transaction.CommitAsync();
        }
        await WaitForDisconnectedWorker(source, applicationName);
        (await Snapshot(services, allIds)).SequenceEqual(before).Should().BeTrue();
        using (var recovery = factory.Services.CreateScope())
        {
            var recoveryDb = recovery.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var interrupted = await recoveryDb.MemoryJobs.SingleAsync(x => x.Id == job.Id);
            interrupted.Status.Should().Be(MemoryJobStatus.Running);
            // Simulate elapsed time in this isolated fixture. The production 30-minute rule and clocks are unchanged.
            interrupted.StartedAt = DateTimeOffset.UtcNow.AddMinutes(-31);
            await recoveryDb.SaveChangesAsync();
        }
        await using (var rollback = await RunningWorker.Start(compatible.Dll, config))
        {
            await WaitForJob(factory.Services, job.Id, MemoryJobStatus.Completed);
            using var read = factory.Services.CreateScope();
            var finalDb = read.ServiceProvider.GetRequiredService<MemoryDbContext>();
            var recovered = await finalDb.MemoryJobs.AsNoTracking().SingleAsync(x => x.Id == job.Id);
            recovered.ProjectId.Should().Be(job.ProjectId);
            recovered.TenantId.Should().Be(owner.TenantId);
            recovered.OwnerUserId.Should().Be(owner.Id);
            recovered.StartedAt.Should().BeAfter(DateTimeOffset.UtcNow.AddMinutes(-5));
            (await Snapshot(read.ServiceProvider, [chunks[2].Id])).SequenceEqual(foreignBefore).Should().BeTrue();
            foreach (var chunk in chunks.Take(2))
            {
                (await finalDb.MemoryChunkVectors.CountAsync(x => x.ChunkId == chunk.Id && x.Status == "Active" && x.ModelKey == provider.ModelKey))
                    .Should().Be(1);
                (await finalDb.MemoryChunkVectors.AsNoTracking()
                    .Where(x => x.ChunkId == chunk.Id && x.Status == "Active" && x.ModelKey == provider.ModelKey)
                    .Select(x => x.Id).SingleAsync()).Should().NotBe(activeVectorIdsBefore[chunk.Id]);
                (await finalDb.MemoryChunkVectors.CountAsync(x => x.Id == activeVectorIdsBefore[chunk.Id] && x.ChunkId == chunk.Id
                    && x.Status == "Superseded" && x.ModelKey == provider.ModelKey)).Should().Be(1);
            }
        }
        var ledger = await JobLedger(services, [job.Id]);
        var vectorsAfter = await Snapshot(services, allIds);
        await using (var forward = await RunningWorker.Start(candidate.Dll, config))
        {
            (await JobLedger(services, [job.Id]) == ledger).Should().BeTrue();
            (await Snapshot(services, allIds)).SequenceEqual(vectorsAfter).Should().BeTrue();
        }
    }

    private static async Task WaitForVectorLock(NpgsqlDataSource source, string applicationName, bool afterSupersede)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        while (!deadline.IsCancellationRequested)
        {
            await using var command = source.CreateCommand("""
                SELECT EXISTS (
                    SELECT 1 FROM pg_stat_activity AS activity
                    JOIN pg_locks AS waiting ON waiting.pid = activity.pid
                    WHERE activity.application_name = @name AND activity.wait_event_type = 'Lock'
                      AND NOT waiting.granted
                      AND ((@after_supersede AND waiting.locktype = 'advisory')
                           OR (NOT @after_supersede AND waiting.relation = 'memory_chunk_vectors'::regclass)));
                """);
            command.Parameters.AddWithValue("name", applicationName);
            command.Parameters.AddWithValue("after_supersede", afterSupersede);
            if ((bool)(await command.ExecuteScalarAsync(deadline.Token))!) return;
            await Task.Delay(100, deadline.Token);
        }
        throw new TimeoutException("OWNED_WORKER_VECTOR_LOCK_NOT_OBSERVED");
    }

    private static async Task WaitForDisconnectedWorker(NpgsqlDataSource source, string applicationName)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (!deadline.IsCancellationRequested)
        {
            await using var command = source.CreateCommand("SELECT count(*) FROM pg_stat_activity WHERE application_name = @name;");
            command.Parameters.AddWithValue("name", applicationName);
            if ((long)(await command.ExecuteScalarAsync(deadline.Token))! == 0) return;
            await Task.Delay(100, deadline.Token);
        }
        throw new TimeoutException("OWNED_WORKER_CONNECTION_NOT_RELEASED");
    }

    private sealed class InsertFault(NpgsqlDataSource source, string name) : IAsyncDisposable
    {
        public static async Task<InsertFault> Create(NpgsqlDataSource source, string applicationName, long key)
        {
            var name = "owned_vector_insert_fault_" + Guid.NewGuid().ToString("N");
            // Identifiers, application name and lock key are generated fixture values; no production schema is touched.
            await using var command = source.CreateCommand(FormattableString.Invariant($$"""
                CREATE FUNCTION public.{{name}}() RETURNS trigger LANGUAGE plpgsql AS $body$
                BEGIN
                    IF current_setting('application_name') = '{{applicationName}}' THEN
                        PERFORM pg_advisory_xact_lock({{key}});
                    END IF;
                    RETURN NEW;
                END $body$;
                CREATE TRIGGER {{name}} BEFORE INSERT ON memory_chunk_vectors
                FOR EACH ROW EXECUTE FUNCTION public.{{name}}();
                """));
            await command.ExecuteNonQueryAsync();
            return new(source, name);
        }
        public async ValueTask DisposeAsync()
        {
            await using var command = source.CreateCommand($"DROP TRIGGER IF EXISTS {name} ON memory_chunk_vectors; DROP FUNCTION IF EXISTS public.{name}();");
            await command.ExecuteNonQueryAsync();
        }
    }

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
        private bool disposed;
        private RunningWorker(Process process)
        {
            this.process = process;
            stdout = Drain(process.StandardOutput);
            stderr = Drain(process.StandardError);
        }
        public static async Task<RunningWorker> Start(string dll, IConfiguration config, string? applicationName = null)
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
            if (applicationName is not null)
                start.Environment["ConnectionStrings__Postgres"] = new NpgsqlConnectionStringBuilder(config.GetConnectionString("Postgres"))
                { ApplicationName = applicationName }.ConnectionString;
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
            if (disposed) return;
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(15));
            process.Dispose();
            disposed = true;
        }
        private static async Task Drain(StreamReader reader)
        {
            var buffer = new char[4096];
            while (await reader.ReadAsync(buffer.AsMemory()) != 0) { }
        }
    }
}

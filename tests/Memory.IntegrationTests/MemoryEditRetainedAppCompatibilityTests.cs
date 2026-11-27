using System.Diagnostics;
using System.Globalization;
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
using Npgsql;

namespace Memory.IntegrationTests;

public sealed class MemoryEditRetainedAppFactAttribute : FactAttribute
{
    public MemoryEditRetainedAppFactAttribute()
    {
        if (Environment.GetEnvironmentVariable(MemoryEditRetainedAppCompatibilityTests.ReceiptVariable) is null)
            Skip = "The exact retained v1.1.125 app bundle and fixture receipt are required.";
        else if (!DockerTestGate.Current.IsAvailable)
            Skip = DockerTestGate.Current.Reason;
    }
}

public sealed class MemoryEditRetainedAppCompatibilityTests
{
    public const string ReceiptVariable = "CONTEXTHUB_MEMORY_EDIT_RETAINED_RECEIPT";
    private const string ReceiptHashVariable = "CONTEXTHUB_MEMORY_EDIT_RETAINED_RECEIPT_SHA256";
    private const string DotnetVariable = "CONTEXTHUB_MEMORY_EDIT_RETAINED_DOTNET";
    private const string RetainedSource = "5001d45b7ff52377e15cd5d70ad75c9680d52df2";
    private const string RetainedTree = "73005f420415857b7b478667bc7170ea8f800774";
    private const string EntryAssembly = "Memory.McpServer.dll";
    private const string EditAuditMarker = "versioned-update";

    [MemoryEditRetainedAppFact]
    public async Task Retained_v125_reads_committed_memory_and_roll_forward_recovers_exact_edit_once()
    {
        using var retained = await RetainedBundle.OpenAsync();
        var environment = new ContainerTestEnvironment();
        try
        {
            await environment.InitializeAsync();
            var factory = environment.GetFactory();
            var seed = await SeedAsync(factory);
            var connectionString = environment.PostgresConnectionString!;
            var initialLedger = await ReadLedgerAsync(connectionString);
            initialLedger.Should().HaveCount(67).And.Contain(row => row.Name == "064_memory_edit_receipts.sql");
            var baseline = await ReadStateAsync(connectionString, seed);
            baseline.Receipts.Should().BeEmpty();
            baseline.EditRevisions.Should().BeEmpty();

            const string editedBody = "synthetic body after guarded edit";
            var command = new MemoryEditCommand(seed.Memory.ProjectId, seed.Memory.Id, seed.Memory.Version,
                Guid.NewGuid().ToString("N"), new(Content: editedBody));
            var editRoute = $"/api/memories/{seed.Memory.Id:D}/edits";
            MemoryEditReceipt original;
            using (var client = factory.CreateClient())
            {
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.EditToken);
                original = await PostReceiptAsync(client, editRoute, command);
            }
            var recovery = new MemoryEditRecoveryRequest(command.ProjectId, command.MemoryId, command.RequestId,
                MemoryEditContract.Hash(command));
            original.Replayed.Should().BeFalse();
            original.PreviousVersion.Should().Be(seed.Memory.Version);
            original.Version.Should().Be(seed.Memory.Version + 1);
            original.RequestHash.Should().Be(recovery.ExpectedRequestHash);
            var committed = await ReadStateAsync(connectionString, seed);
            committed.Version.Should().Be(original.Version);
            committed.Content.Should().Be(editedBody);
            committed.Revisions.Should().HaveCount(baseline.Revisions.Length + 1);
            committed.EditRevisions.Should().Equal(original.RevisionId);
            committed.Receipts.Should().Equal(original.ReceiptId);
            committed.ReindexJobs.Should().Be(baseline.ReindexJobs + 1);

            var oldStart = CreateRetainedStart(retained.Directory, factory.Services.GetRequiredService<IConfiguration>(), out var address);
            await factory.DisposeAsync();
            await ReadRetainedAsync(oldStart, address, seed, original, editedBody);
            (await ReadLedgerAsync(connectionString)).Should().Equal(initialLedger);
            (await ReadStateAsync(connectionString, seed)).Should().BeEquivalentTo(committed);

            await environment.RestartApplicationAsync();
            using (var resumed = environment.GetFactory().CreateClient())
            {
                resumed.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.EditToken);
                (await PostReceiptAsync(resumed, editRoute + "/receipt", recovery)).Should().Be(original with { Replayed = true });
                (await PostReceiptAsync(resumed, editRoute, command)).Should().Be(original with { Replayed = true });
            }
            (await ReadStateAsync(connectionString, seed)).Should().BeEquivalentTo(committed);
            (await ReadLedgerAsync(connectionString)).Should().Equal(initialLedger);
        }
        finally
        {
            await environment.DisposeAsync();
        }
    }

    private static async Task<Seed> SeedAsync(MemoryApplicationFactory factory)
    {
        using var scope = factory.Services.CreateScope();
        var services = scope.ServiceProvider;
        var db = services.GetRequiredService<MemoryDbContext>();
        var user = await db.TenantUsers.SingleAsync(row => row.Username == "contract-test-admin");
        var project = $"MemoryEditCompat-{Guid.NewGuid():N}";
        services.GetRequiredService<IRequestActorAccessor>().Current = new ContextHubRequestActor(
            user.TenantId, user.Id, user.Username, user.Role, [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [project], IsAuthenticated: true);
        var memory = await services.GetRequiredService<IMemoryService>().UpsertAsync(new(
            "compat-seed", MemoryScope.Project, MemoryType.Episode, "synthetic original title", "synthetic original body", "synthetic summary",
            "document", "synthetic-fixture", [], 0.5m, 0.5m, ProjectId: project), default);
        var security = services.GetRequiredService<ITenantSecurityService>();
        var readToken = await security.CreateTokenAsync(new(user.TenantId, user.Id, "retained read-only fixture",
            Scopes: [SecurityScopes.MemoryRead], AllowedProjectIds: [project]), default);
        var editToken = await security.CreateTokenAsync(new(user.TenantId, user.Id, "candidate guarded edit fixture",
            Scopes: [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], AllowedProjectIds: [project]), default);
        readToken.Token.Scopes.Should().Equal(SecurityScopes.MemoryRead);
        readToken.Token.AllowedProjectIds.Should().Equal(project);
        return new(memory, user.TenantId, user.Id, readToken.PlainToken, editToken.PlainToken);
    }

    private static async Task<MemoryEditReceipt> PostReceiptAsync<T>(HttpClient client, string path, T body)
    {
        using var response = await client.PostAsJsonAsync(path, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        return await response.Content.ReadFromJsonAsync<MemoryEditReceipt>()
            ?? throw new InvalidOperationException("Expected a typed memory edit receipt.");
    }

    private static async Task<LedgerEntry[]> ReadLedgerAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("SELECT name, applied_at FROM schema_migrations ORDER BY name", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var ledger = new List<LedgerEntry>();
        while (await reader.ReadAsync()) ledger.Add(new(reader.GetString(0), reader.GetDateTime(1)));
        return ledger.ToArray();
    }

    private static async Task<EditState> ReadStateAsync(string connectionString, Seed seed)
    {
        await using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql(connectionString, options => options.UseVector()).Options);
        var memory = await db.MemoryItems.AsNoTracking().SingleAsync(row => row.Id == seed.Memory.Id);
        var revisions = await db.MemoryItemRevisions.AsNoTracking().Where(row => row.MemoryItemId == memory.Id)
            .OrderBy(row => row.Version).Select(row => new { row.Id, row.ChangedBy }).ToArrayAsync();
        var receipts = await db.MemoryEditReceipts.AsNoTracking().Where(row => row.MemoryItemId == memory.Id)
            .Select(row => row.Id).ToArrayAsync();
        var reindex = await db.MemoryJobs.CountAsync(row => row.TenantId == seed.TenantId && row.OwnerUserId == seed.OwnerId &&
            row.ProjectId == seed.Memory.ProjectId && row.JobType == MemoryJobType.Reindex);

        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM security_audit_events
            WHERE tenant_id = @tenant AND strpos(details_json::text, @memory) > 0
            """, connection);
        command.Parameters.AddWithValue("tenant", seed.TenantId);
        command.Parameters.AddWithValue("memory", memory.Id.ToString("D"));
        var resourceAudits = (long)(await command.ExecuteScalarAsync())!;
        return new(memory.Version, memory.Content, revisions.Select(row => row.Id).ToArray(),
            revisions.Where(row => row.ChangedBy == EditAuditMarker).Select(row => row.Id).ToArray(), receipts, reindex, resourceAudits);
    }

    private static ProcessStartInfo CreateRetainedStart(string bundle, IConfiguration fixture, out Uri address)
    {
        var dotnet = RequiredAbsolutePath(DotnetVariable);
        var start = new ProcessStartInfo(dotnet)
        {
            WorkingDirectory = bundle,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add(Path.Combine(bundle, EntryAssembly));
        start.Environment.Clear();
        foreach (var key in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP", "TMPDIR" })
            if (Environment.GetEnvironmentVariable(key) is { } value) start.Environment[key] = value;
        foreach (var key in new[]
                 {
                     "ConnectionStrings:Postgres", "ConnectionStrings:Redis", "Embeddings:Provider", "Embeddings:Profile",
                     "Embeddings:ModelKey", "Embeddings:Dimensions", "Embeddings:MaxTokens", "Memory:Namespace",
                     "ContextHub:Security:RequireAuthentication", "ContextHub:Security:BootstrapToken",
                     "ContextHub:Security:BootstrapTenantSlug", "ContextHub:Security:BootstrapUsername",
                     "ContextHub:Security:BootstrapAllowedProjectIds", "ContextHub:Security:AllowedMcpOrigins:0"
                 })
            start.Environment[key.Replace(":", "__", StringComparison.Ordinal)] = fixture[key]
                ?? throw new InvalidOperationException($"Missing isolated fixture configuration key '{key}'.");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Testing";
        start.Environment["DOTNET_ENVIRONMENT"] = "Testing";
        start.Environment["Logging__LogLevel__Default"] = "None";
        start.Environment["DatabaseLogging__MinimumLevel"] = "Critical";
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        address = new Uri($"http://127.0.0.1:{port}");
        start.Environment["ASPNETCORE_URLS"] = address.AbsoluteUri;
        return start;
    }

    private static async Task ReadRetainedAsync(ProcessStartInfo start, Uri address, Seed seed, MemoryEditReceipt receipt, string content)
    {
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start retained fixture app.");
        var stdout = process.StandardOutput.BaseStream.CopyToAsync(Stream.Null);
        var stderr = process.StandardError.BaseStream.CopyToAsync(Stream.Null);
        try
        {
            using var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(10) };
            await WaitForReadyAsync(client, process);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", seed.ReadToken);
            using var response = await client.GetAsync($"/api/memories/{seed.Memory.Id:D}");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            var oldView = await response.Content.ReadFromJsonAsync<MemoryDocument>();
            oldView.Should().NotBeNull();
            oldView!.Id.Should().Be(seed.Memory.Id);
            oldView.ProjectId.Should().Be(seed.Memory.ProjectId);
            oldView.Version.Should().Be(receipt.Version);
            oldView.Content.Should().Be(content);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAll(stdout, stderr).WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    private static async Task WaitForReadyAsync(HttpClient client, Process process)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        try
        {
            while (!deadline.IsCancellationRequested)
            {
                if (process.HasExited) throw new InvalidOperationException($"Retained test app exited with code {process.ExitCode} before readiness.");
                try
                {
                    using var response = await client.GetAsync("/health/ready", deadline.Token);
                    if (response.StatusCode == HttpStatusCode.OK) return;
                }
                catch (HttpRequestException) { }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { }
                await Task.Delay(200, deadline.Token);
            }
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        throw new TimeoutException("Retained test app was not ready before its deadline.");
    }

    private static string RequiredAbsolutePath(string name)
    {
        var path = Environment.GetEnvironmentVariable(name);
        if (path is null || !Path.IsPathFullyQualified(path))
            throw new InvalidOperationException($"{name} must be an absolute fixture path.");
        return Path.GetFullPath(path);
    }

    private sealed record Seed(MemoryDocument Memory, Guid TenantId, Guid OwnerId, string ReadToken, string EditToken);
    private sealed record LedgerEntry(string Name, DateTime AppliedAt);
    private sealed record EditState(int Version, string Content, Guid[] Revisions, Guid[] EditRevisions, Guid[] Receipts,
        int ReindexJobs, long ResourceAudits);

    private sealed class RetainedBundle(string directory) : IDisposable
    {
        private readonly List<FileStream> _held = [];
        public string Directory { get; } = directory;

        public static async Task<RetainedBundle> OpenAsync()
        {
            var receiptPath = RequiredAbsolutePath(ReceiptVariable);
            var expectedHash = Environment.GetEnvironmentVariable(ReceiptHashVariable)
                ?? throw new InvalidOperationException($"{ReceiptHashVariable} is required.");
            var directory = Path.Combine(Path.GetDirectoryName(receiptPath)!, "app");
            var retained = new RetainedBundle(directory);
            try
            {
                var receipt = await retained.HoldAsync(receiptPath, expectedHash);
                using var manifest = await JsonDocument.ParseAsync(receipt);
                manifest.RootElement.GetProperty("source").GetString().Should().Be(RetainedSource);
                manifest.RootElement.GetProperty("tree").GetString().Should().Be(RetainedTree);
                manifest.RootElement.GetProperty("buildExit").GetInt32().Should().Be(0);
                var names = new HashSet<string>(StringComparer.Ordinal);
                foreach (var entry in manifest.RootElement.GetProperty("files").EnumerateArray())
                {
                    var name = entry.GetProperty("path").GetString()!;
                    name.Split('/').Should().NotContain(part => part == "" || part == "." || part == "..");
                    name.Should().NotContain("\\").And.NotContain(":");
                    names.Add(name).Should().BeTrue();
                    var file = Path.GetFullPath(Path.Combine(directory, name));
                    await retained.HoldAsync(file, entry.GetProperty("sha256").GetString()!);
                }

                foreach (var required in new[] { EntryAssembly, "Memory.Application.dll", "Memory.Infrastructure.dll",
                             "Memory.Domain.dll", "Memory.McpServer.deps.json", "Memory.McpServer.runtimeconfig.json" })
                    names.Should().Contain(required);
                names.Should().BeEquivalentTo(System.IO.Directory.GetFiles(directory, "*", SearchOption.AllDirectories)
                    .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/')));
                return retained;
            }
            catch
            {
                retained.Dispose();
                throw;
            }
        }

        private async Task<FileStream> HoldAsync(string path, string expectedHash)
        {
            var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            _held.Add(file);
            Convert.ToHexString(await SHA256.HashDataAsync(file)).Should().Be(expectedHash.ToUpperInvariant());
            file.Position = 0;
            return file;
        }

        public void Dispose()
        {
            foreach (var file in _held) file.Dispose();
        }
    }
}

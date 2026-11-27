using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.McpServer;
using Memory.Tests.Shared;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pgvector.EntityFrameworkCore;
using Xunit.Abstractions;

namespace Memory.ApiContractTests;

public sealed class ManagedFileReadEndpointTests(ITestOutputHelper output)
{
    [Fact]
    public async Task Inventory_binds_explicit_project_and_visible_pagination_with_existing_defaults()
    {
        var reads = new Reads();
        await using var app = await Host(reads);
        using var client = app.GetTestClient();
        (await client.GetAsync("/api/files/inventory?projectId=ContextHub&limit=12&offset=24")).StatusCode.Should().Be(HttpStatusCode.OK);
        reads.PageRequest.Should().Be(("ContextHub", 12, 24));
        var page = await client.GetFromJsonAsync<ManagedFileMetadataPage>("/api/files/inventory?projectId=ContextHub");
        reads.PageRequest.Should().Be(("ContextHub", 50, 0));
        page!.HasMore.Should().BeFalse();
        (await client.GetAsync("/api/files/inventory?projectId=ContextHub&offset=invalid")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/files/inventory")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Missing_detail_history_and_foreign_version_pair_share_404_without_response_reason()
    {
        var reads = new Reads();
        await using var app = await Host(reads);
        using var client = app.GetTestClient();
        var file = Guid.NewGuid(); var version = Guid.NewGuid();
        foreach (var path in new[] { $"/{file}", $"/{file}/versions", $"/{file}/versions/{version}" })
        {
            using var response = await client.GetAsync("/api/files" + path + "?projectId=ContextHub");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        }
        reads.VersionRequest.Should().Be(("ContextHub", file, version));
        reads.Result = Row(file, version);
        using var detail = await client.GetAsync($"/api/files/{file}/versions/{version}?projectId=ContextHub");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await detail.Content.ReadFromJsonAsync<ManagedFileInventoryResult>())!.FileVersionId.Should().Be(version);
        var json = await detail.Content.ReadAsStringAsync();
        foreach (var forbidden in new[] { "managedObjectId", "contentSha256", "deduplicationScopeKey", "wrapped", "actorId", "purpose", "targetId" })
            json.Should().NotContain(forbidden);
    }

    [Fact]
    public async Task All_reads_require_memory_read_before_invoking_service()
    {
        var reads = new Reads();
        await using var app = await Host(reads, false);
        using var client = app.GetTestClient();
        var file = Guid.NewGuid(); var version = Guid.NewGuid();
        foreach (var path in new[] { "/inventory", "/dlp-summary", $"/{file}", $"/{file}/versions", $"/{file}/versions/{version}" })
            (await client.GetAsync("/api/files" + path + "?projectId=ContextHub")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        reads.Calls.Should().Be(0);
    }

    [Theory]
    [InlineData("inventory")]
    [InlineData("dlp-summary")]
    public async Task Read_budget_expiry_is_unavailable_not_an_empty_complete_result(string path)
    {
        var reads = new Reads { CancelRead = true };
        await using var app = await Host(reads);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/files/{path}?projectId=ContextHub");
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("MetadataReadIncomplete").And.NotContain("hasMore").And.NotContain("candidate").And.NotContain("initialPending");
        if (path == "dlp-summary") response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Dlp_summary_returns_only_fixed_counters_and_disables_response_caching()
    {
        var reads = new Reads { Summary = new(7, 2, 3, 1) };
        await using var app = await Host(reads);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync("/api/files/dlp-summary?projectId=ContextHub");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        reads.SummaryProject.Should().Be("ContextHub");
        (await response.Content.ReadFromJsonAsync<ManagedFileDlpSummary>()).Should().Be(reads.Summary);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.EnumerateObject().Select(property => property.Name).Should()
            .BeEquivalentTo(new[] { "visibleLatestVersions", "quarantined", "needsRescan", "initialPending" });
    }

    [Fact]
    public void Dlp_summary_second_batch_translates_to_bounded_postgres_sql_with_only_needed_columns()
    {
        using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=127.0.0.1;Database=dlp_translation_fixture", options => options.UseVector()).Options);
        var caller = new Actor(true);
        var service = new ManagedFileReadService(db, caller, null!);
        var batch = typeof(ManagedFileReadService).GetMethod("DlpSummaryBatch", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var query = (IQueryable)batch.Invoke(service,
            [caller.Current, "ContextHub", caller.Current.TenantId!.Value, Guid.NewGuid()])!;
        var sql = query.ToQueryString();

        sql.Should().Contain("ORDER BY").And.Contain("LIMIT").And.Contain(" > @").And.Contain("max(")
            .And.Contain("tenant_id").And.Contain("owner_user_id");
        var projection = sql[sql.IndexOf("SELECT", StringComparison.Ordinal)..sql.IndexOf("FROM", StringComparison.Ordinal)];
        projection.Should().Contain("file_asset_id").And.Contain("lifecycle").And.Contain("classification")
            .And.Contain("needs_rescan").And.Contain("last_scan_at")
            .And.NotContain("managed_object_id").And.NotContain("content_sha256").And.NotContain("scanner");
        db.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
        output.WriteLine(sql);
    }

    private static ManagedFileInventoryResult Row(Guid file, Guid version) => new(file, version, "Synthetic", "ContextHub", FileAssetState.Active,
        1, FileVersionLifecycle.Ready, FileClassification.Normal, 1, false, null, 0, null, 0, DateTimeOffset.UnixEpoch);
    internal static async Task<WebApplication> Host(IManagedFileReadService reads, bool readScope = true, IRequestActorAccessor? actor = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IManagedFileReadService>(reads);
        builder.Services.AddSingleton<IRequestActorAccessor>(actor ?? new Actor(readScope));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("synthetic", _ => { });
        var app = builder.Build();
        app.MapGroup("/api/files").MapFileMetadataReadEndpoints(true);
        await app.StartAsync();
        return app;
    }
    private sealed class Actor(bool readScope) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member,
            readScope ? [SecurityScopes.MemoryRead] : [], ["ContextHub"], true);
    }
    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() => Task.FromResult(AuthenticateResult.Success(
            new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("synthetic")), "synthetic")));
    }
    private sealed class Reads : IManagedFileReadService
    {
        public bool CancelRead { get; set; }
        public int Calls { get; private set; }
        public (string, int, int) PageRequest { get; private set; }
        public (string, Guid, Guid) VersionRequest { get; private set; }
        public string? SummaryProject { get; private set; }
        public ManagedFileDlpSummary Summary { get; set; } = new(0, 0, 0, 0);
        public ManagedFileInventoryResult? Result { get; set; }
        public Task<ManagedFileMetadataPage> ListAsync(string projectId, int limit, int offset, CancellationToken ct)
        { Calls++; if (CancelRead) throw new OperationCanceledException(); PageRequest = (projectId, limit, offset); return Task.FromResult(new ManagedFileMetadataPage([], limit, offset, false)); }
        public Task<ManagedFileDlpSummary> GetDlpSummaryAsync(string? projectId, CancellationToken ct)
        { Calls++; if (CancelRead) throw new OperationCanceledException(); SummaryProject = projectId; return Task.FromResult(Summary); }
        public Task<ManagedFileInventoryResult?> GetAsync(string projectId, Guid fileId, CancellationToken ct)
        { Calls++; return Task.FromResult(Result); }
        public Task<ManagedFileMetadataPage?> ListVersionsAsync(string projectId, Guid fileId, int limit, int offset, CancellationToken ct)
        { Calls++; return Task.FromResult<ManagedFileMetadataPage?>(null); }
        public Task<ManagedFileInventoryResult?> GetVersionAsync(string projectId, Guid fileId, Guid versionId, CancellationToken ct)
        { Calls++; VersionRequest = (projectId, fileId, versionId); return Task.FromResult(Result); }
    }
}

public sealed class ManagedFileDlpSummaryPostgresTests(ContainerTestEnvironment environment)
    : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Dlp_summary_counts_only_authorized_latest_versions_across_postgres_batches()
    {
        await using var scope = environment.GetFactory().Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var suffix = Guid.NewGuid().ToString("N");
        var projectId = "dlp-summary-" + suffix;
        var otherProject = "other-dlp-" + suffix;
        var tenant = new Tenant { Slug = "dlp-" + suffix, DisplayName = "DLP monitor fixture" };
        var foreignTenant = new Tenant { Slug = "dlp-foreign-" + suffix, DisplayName = "Foreign tenant fixture" };
        var owner = new TenantUser { TenantId = tenant.Id, Username = "owner" };
        var anotherOwner = new TenantUser { TenantId = tenant.Id, Username = "another-owner" };
        var foreignOwner = new TenantUser { TenantId = foreignTenant.Id, Username = "foreign-owner" };
        db.Tenants.AddRange(tenant, foreignTenant);
        await db.SaveChangesAsync();
        db.TenantUsers.AddRange(owner, anotherOwner, foreignOwner);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var storage = new ManagedObject
        {
            TenantId = tenant.Id,
            OwnerUserId = owner.Id,
            ProjectId = projectId,
            StorageId = suffix + suffix,
            State = ManagedObjectState.Ready,
            PlaintextLength = 1,
            ChunkSize = 65536,
            ChunkCount = 1,
            KeyId = "fixture-metadata-only",
            WrappedDek = new byte[32],
            WrapNonce = new byte[12],
            WrapTag = new byte[16],
            CreatedAt = now,
            UpdatedAt = now,
            StagedUntil = now
        };
        db.ManagedObjects.Add(storage);

        FileAsset AddFile(TenantUser fileOwner, string project, bool deleted = false)
        {
            var name = $"synthetic-{Guid.NewGuid():N}";
            var file = new FileAsset
            {
                TenantId = fileOwner.TenantId,
                OwnerUserId = fileOwner.Id,
                ProjectId = project,
                LogicalFileName = name,
                NormalizedFileName = name,
                State = deleted ? FileAssetState.LogicalDeleted : FileAssetState.Active,
                DeletedAt = deleted ? now : null,
                CreatedByActorId = "dlp-fixture",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.FileAssets.Add(file);
            return file;
        }

        void AddVersion(FileAsset file, FileVersionLifecycle life, bool rescan = false, bool assessed = false, int number = 1)
        {
            db.FileVersions.Add(new FileVersion
            {
                FileAsset = file,
                ManagedObject = storage,
                VersionNumber = number,
                ContentSha256 = new string('a', 64),
                DeduplicationScopeKey = $"{file.Id:D}:{number}",
                Lifecycle = life,
                Classification = life == FileVersionLifecycle.Quarantined ? FileClassification.Quarantined : FileClassification.Restricted,
                NeedsRescan = rescan,
                LastScanAt = assessed ? now : null,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        for (var index = 0; index < 101; index++)
        {
            AddVersion(AddFile(owner, projectId), FileVersionLifecycle.Ready);
        }
        AddVersion(AddFile(owner, projectId), FileVersionLifecycle.IntegrityVerified, rescan: true);
        AddVersion(AddFile(owner, projectId), FileVersionLifecycle.NeedsRescan, rescan: true, assessed: true);
        AddVersion(AddFile(owner, projectId), FileVersionLifecycle.Quarantined, rescan: true, assessed: true);
        var history = AddFile(owner, projectId);
        AddVersion(history, FileVersionLifecycle.Quarantined);
        AddVersion(history, FileVersionLifecycle.Ready, number: 2);
        AddVersion(history, FileVersionLifecycle.LogicalDeleted, rescan: true, number: 3);
        AddVersion(AddFile(owner, projectId), FileVersionLifecycle.LogicalDeleted, rescan: true);
        AddVersion(AddFile(owner, projectId, deleted: true), FileVersionLifecycle.Quarantined);
        var denied = AddFile(owner, projectId);
        AddVersion(denied, FileVersionLifecycle.Quarantined);
        AddVersion(AddFile(anotherOwner, projectId), FileVersionLifecycle.Quarantined);
        AddVersion(AddFile(foreignOwner, projectId), FileVersionLifecycle.Quarantined);
        AddVersion(AddFile(owner, otherProject), FileVersionLifecycle.Quarantined);
        await db.SaveChangesAsync();

        var actor = new SummaryActor(new(tenant.Id, owner.Id, owner.Username, TenantUserRole.Member,
            [SecurityScopes.MemoryRead], [projectId], true));
        var rights = new MetadataRights(denied.Id);
        await using var app = await ManagedFileReadEndpointTests.Host(new ManagedFileReadService(db, actor, rights), actor: actor);
        using var client = app.GetTestClient();
        using var response = await client.GetAsync($"/api/files/dlp-summary?projectId={projectId.ToUpperInvariant()}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadFromJsonAsync<ManagedFileDlpSummary>()).Should().Be(new ManagedFileDlpSummary(105, 1, 3, 1));
        rights.EvaluatedFileIds.Should().OnlyHaveUniqueItems().And.HaveCount(106).And.Contain(denied.Id);

        foreach (var invalidScope in new[] { "", "?projectId=%20", "?projectId=%2A", "?projectId=DeFaUlT" })
        {
            using var invalid = await client.GetAsync("/api/files/dlp-summary" + invalidScope);
            invalid.StatusCode.Should().Be(HttpStatusCode.BadRequest);
            invalid.Headers.CacheControl!.NoStore.Should().BeTrue();
        }

        rights.CancelAfter = rights.EvaluatedFileIds.Count + 2;
        using var incomplete = await client.GetAsync($"/api/files/dlp-summary?projectId={projectId}");
        incomplete.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await incomplete.Content.ReadAsStringAsync()).Should().Contain("MetadataReadIncomplete").And.NotContain("visibleLatestVersions");
    }

    private sealed class SummaryActor(ContextHubRequestActor actor) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = actor;
    }

    private sealed class MetadataRights(Guid denied) : IPlatformFoundationStore
    {
        public List<Guid> EvaluatedFileIds { get; } = [];
        public int? CancelAfter { get; set; }

        public Task<EffectiveRightsResult> EvaluateAsync(string projectId, string principalId, IReadOnlyList<string> rights,
            string? resourceType, string? resourceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (EvaluatedFileIds.Count == CancelAfter) throw new OperationCanceledException();
            rights.Should().Equal("metadata");
            resourceType.Should().Be("File");
            var fileId = Guid.Parse(resourceId!);
            EvaluatedFileIds.Add(fileId);
            return Task.FromResult(new EffectiveRightsResult(projectId, principalId, new SecurityRevisionVector(1, 1, 1, 1),
                [new("metadata", fileId != denied, "Resource", [], "fixture")]));
        }

        public Task<AuthorizationTopologyEdge> UpsertTopologyEdgeAsync(TopologyEdgeUpsertRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<AuthorizationRule> UpsertPolicyAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<AuthorizationRule> UpsertExplicitGrantAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
        public Task<CanonicalTagSuggestion> SuggestTagAsync(CanonicalTagSuggestionRequest request, CancellationToken cancellationToken)
            => throw new NotSupportedException();
    }
}

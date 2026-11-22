using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.Options;

namespace Memory.UnitTests;

public sealed class ManagedFileDownloadTests
{
    [Fact]
    public async Task Exact_old_pin_resolves_internally_and_reuses_download_authority_without_object_disclosure()
    {
        var setup = new Fixture();
        var old = setup.Version;
        setup.Versions.Add(new FileVersion
        {
            FileAssetId = setup.Asset.Id,
            FileAsset = setup.Asset,
            ManagedObjectId = Guid.NewGuid(),
            VersionNumber = 2,
            Lifecycle = FileVersionLifecycle.Ready
        });
        var result = await setup.Create();
        result!.FileId.Should().Be(setup.Asset.Id);
        result.FileVersionId.Should().Be(old.Id);
        result.Operation.Should().Be(ManagedTransferOperation.Download);
        var session = setup.Sessions.Single();
        session.ManagedObjectId.Should().Be(old.ManagedObjectId);
        session.OwnerUserId.Should().Be(setup.Actor.Current.UserId);
        session.Purpose.Should().Be("synthetic-review");
        session.AgentId.Should().Be("synthetic-agent");
        session.ExecutionId.Should().Be(setup.ExecutionId);
        session.CapabilityHash.Should().NotBe(result.Capability);
        session.Operations.Single().RequestId.Should().Be("synthetic-request");
        setup.Foundation.Requests.Should().ContainSingle().Which.Should().Be(("ContextHub", "download", setup.Asset.Id.ToString("D")));
        setup.Proxy.Saves.Should().Be(1);
        var json = JsonSerializer.Serialize(result);
        foreach (var forbidden in new[] { setup.Object.Id.ToString(), "ObjectId", "ManagedObject", "StorageId", "ContentSha256", "Wrapped", "Provider", "Purpose", "ActorId" })
            json.Should().NotContain(forbidden);
        result.ResourcePath.Should().Be($"/api/transfers/{session.Id:D}");
    }

    [Theory]
    [InlineData("foreign-asset-tenant")]
    [InlineData("foreign-asset-owner")]
    [InlineData("foreign-object-tenant")]
    [InlineData("foreign-object-owner")]
    [InlineData("wrong-asset-project")]
    [InlineData("wrong-object-project")]
    [InlineData("wrong-file-pin")]
    [InlineData("missing-version")]
    [InlineData("deleted-file")]
    [InlineData("deleted-version")]
    [InlineData("rescan")]
    [InlineData("not-ready")]
    [InlineData("object-not-ready")]
    [InlineData("quarantined")]
    [InlineData("download-denied")]
    public async Task Inaccessible_or_unavailable_pins_are_indistinguishable_and_create_no_session(string scenario)
    {
        var setup = new Fixture();
        switch (scenario)
        {
            case "foreign-asset-tenant": setup.Asset.TenantId = Guid.NewGuid(); break;
            case "foreign-asset-owner": setup.Asset.OwnerUserId = Guid.NewGuid(); break;
            case "foreign-object-tenant": setup.Object.TenantId = Guid.NewGuid(); break;
            case "foreign-object-owner": setup.Object.OwnerUserId = Guid.NewGuid(); break;
            case "wrong-asset-project": setup.Asset.ProjectId = "Other"; break;
            case "wrong-object-project": setup.Object.ProjectId = "Other"; break;
            case "wrong-file-pin": setup.Version.FileAssetId = Guid.NewGuid(); break;
            case "missing-version": setup.Versions.Clear(); break;
            case "deleted-file": setup.Asset.State = FileAssetState.LogicalDeleted; break;
            case "deleted-version": setup.Version.Lifecycle = FileVersionLifecycle.LogicalDeleted; break;
            case "rescan": setup.Version.NeedsRescan = true; break;
            case "not-ready": setup.Version.Lifecycle = FileVersionLifecycle.SecurityScanning; break;
            case "object-not-ready": setup.Object.State = ManagedObjectState.Missing; break;
            case "quarantined": setup.Version.Classification = FileClassification.Quarantined; break;
            case "download-denied": setup.Foundation.Allowed = false; break;
        }
        (await setup.Create()).Should().BeNull();
        setup.Sessions.Should().BeEmpty();
        setup.Proxy.Saves.Should().Be(0);
    }

    [Theory]
    [InlineData(false, false, false, false)]
    [InlineData(true, false, false, true)]
    [InlineData(false, true, false, true)]
    [InlineData(false, true, true, false)]
    [InlineData(true, false, true, true)]
    public async Task Admin_service_and_unassigned_scope_preserve_existing_download_rules(bool admin, bool service, bool unassigned, bool allowed)
    {
        var setup = new Fixture();
        setup.Actor.Current = setup.Actor.Current with { Role = admin ? TenantUserRole.Admin : TenantUserRole.Member, IsServiceActor = service };
        setup.Asset.OwnerUserId = Guid.NewGuid();
        setup.Object.OwnerUserId = setup.Asset.OwnerUserId;
        if (unassigned) { setup.Asset.ProjectId = null; setup.Asset.State = FileAssetState.Unassigned; }
        ((await setup.Create()) is not null).Should().Be(allowed);
        setup.Sessions.Count.Should().Be(allowed ? 1 : 0);
    }

    [Theory]
    [InlineData(FileClassification.Normal)]
    [InlineData(FileClassification.Sensitive)]
    [InlineData(FileClassification.Restricted)]
    public async Task Frozen_classifications_use_existing_download_right_and_explicit_purpose(FileClassification classification)
    {
        var setup = new Fixture();
        setup.Version.Classification = classification;
        (await setup.Create()).Should().NotBeNull();
        setup.Foundation.Requests.Should().ContainSingle().Which.Item2.Should().Be("download");
    }

    [Fact]
    public async Task Uploader_can_download_unassigned_file_and_service_cannot_cross_tenant()
    {
        var setup = new Fixture();
        setup.Asset.ProjectId = null;
        setup.Asset.State = FileAssetState.Unassigned;
        (await setup.Create()).Should().NotBeNull();
        setup.Foundation.Requests.Should().BeEmpty();
        var foreign = new Fixture();
        foreign.Actor.Current = foreign.Actor.Current with { IsServiceActor = true, Role = TenantUserRole.Admin, TenantId = Guid.NewGuid() };
        (await foreign.Create()).Should().BeNull();
        foreign.Proxy.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Initial_repeated_request_retains_existing_new_session_semantics_without_bearer_recovery()
    {
        var setup = new Fixture();
        var first = await setup.Create();
        var second = await setup.Create();
        second!.SessionId.Should().NotBe(first!.SessionId);
        second.Capability.Should().NotBe(first.Capability);
        setup.Sessions.Should().HaveCount(2);
        setup.Sessions.Should().OnlyContain(session => session.Operations.Single().RequestId == "synthetic-request");
    }

    [Fact]
    public async Task Scope_project_validation_and_cancellation_do_not_create_sessions()
    {
        var setup = new Fixture();
        setup.Actor.Current = setup.Actor.Current with { Scopes = [] };
        await FluentActions.Awaiting(() => setup.Create()).Should().ThrowAsync<UnauthorizedAccessException>();
        setup.Actor.Current = setup.Actor.Current with { Scopes = [SecurityScopes.MemoryRead], AllowedProjectIds = ["Other"] };
        await FluentActions.Awaiting(() => setup.Create()).Should().ThrowAsync<UnauthorizedAccessException>();
        setup.Actor.Current = setup.Actor.Current with { AllowedProjectIds = ["ContextHub"] };
        foreach (var request in new[] { setup.Request with { ProjectId = "" }, setup.Request with { Purpose = "" },
            setup.Request with { IdempotencyKey = "" }, setup.Request with { Purpose = new string('x', 201) }, setup.Request with { IdempotencyKey = new string('x', 201) } })
            await FluentActions.Awaiting(() => setup.Service.CreateAsync(setup.Asset.Id, setup.Version.Id, request, default)).Should().ThrowAsync<ArgumentException>();
        using var source = new CancellationTokenSource(); source.Cancel();
        await FluentActions.Awaiting(() => setup.Service.CreateAsync(setup.Asset.Id, setup.Version.Id, setup.Request, source.Token)).Should().ThrowAsync<OperationCanceledException>();
        setup.Proxy.Saves.Should().Be(0);
        setup.Sessions.Should().BeEmpty();
    }

    [Fact]
    public void Logical_pin_resolution_translates_on_actual_model_without_opening_a_database()
    {
        var setup = new Fixture();
        using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var service = new ManagedFileDownloadService(db, setup.Actor, null!);
        var query = (IQueryable<Guid?>)typeof(ManagedFileDownloadService).GetMethod("ResolveObject", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(service, [setup.Actor.Current, "ContextHub", setup.Asset.Id, setup.Version.Id])!;
        var sql = query.ToQueryString();
        sql.Should().Contain("JOIN").And.Contain("tenant_id").And.Contain("owner_user_id").And.Contain("needs_rescan");
        sql.Should().NotContain("content_sha256").And.NotContain("storage_id").And.NotContain("wrapped_dek");
        db.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public async Task Cancellation_during_download_authority_and_technical_failure_are_not_reported_as_absence()
    {
        var setup = new Fixture();
        using var source = new CancellationTokenSource();
        setup.Foundation.BeforeEvaluate = () => source.Cancel();
        await FluentActions.Awaiting(() => setup.Service.CreateAsync(setup.Asset.Id, setup.Version.Id, setup.Request, source.Token))
            .Should().ThrowAsync<OperationCanceledException>();
        setup.Sessions.Should().BeEmpty(); setup.Proxy.Saves.Should().Be(0);
        setup.Foundation.BeforeEvaluate = () => throw new InvalidOperationException("synthetic-backend-unavailable");
        await FluentActions.Awaiting(() => setup.Create()).Should().ThrowAsync<InvalidOperationException>().WithMessage("synthetic-backend-unavailable");
        setup.Sessions.Should().BeEmpty(); setup.Proxy.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Content_metadata_requires_download_authority_and_never_creates_a_session_or_audit_mutation()
    {
        var setup = new Fixture();
        setup.Version.ContentSha256 = new string('C', 64);
        var result = await setup.Disclosure.ReadContentMetadataAsync("ContextHub", setup.Asset.Id, setup.Version.Id, "synthetic-review", default);
        result!.SizeBytes.Should().Be(100); result.ContentSha256.Should().Be(new string('c', 64));
        result.FileVersionId.Should().Be(setup.Version.Id);
        setup.Foundation.Requests.Should().ContainSingle().Which.Item2.Should().Be("download");
        setup.Sessions.Should().BeEmpty(); setup.Proxy.Saves.Should().Be(0);
        setup.Foundation.DeniedRights.Add("download");
        (await setup.Disclosure.ReadContentMetadataAsync("ContextHub", setup.Asset.Id, setup.Version.Id, "synthetic", default)).Should().BeNull();
        setup.Proxy.Saves.Should().Be(0);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("owner")]
    [InlineData("project")]
    [InlineData("object-project")]
    [InlineData("pin")]
    [InlineData("deleted")]
    [InlineData("rescan")]
    [InlineData("object-state")]
    [InlineData("quarantine")]
    public async Task Hash_and_size_do_not_escape_existing_logical_download_boundaries(string denial)
    {
        var s = new Fixture();
        switch (denial)
        {
            case "tenant": s.Asset.TenantId = Guid.NewGuid(); break;
            case "owner": s.Asset.OwnerUserId = Guid.NewGuid(); break;
            case "project": s.Asset.ProjectId = "Other"; break;
            case "object-project": s.Object.ProjectId = "Other"; break;
            case "pin": s.Version.FileAssetId = Guid.NewGuid(); break;
            case "deleted": s.Asset.State = FileAssetState.LogicalDeleted; break;
            case "rescan": s.Version.NeedsRescan = true; break;
            case "object-state": s.Object.State = ManagedObjectState.Corrupt; break;
            case "quarantine": s.Actor.Current = s.Actor.Current with { Role = TenantUserRole.Admin }; s.Version.Classification = FileClassification.Quarantined; break;
        }
        (await s.Disclosure.ReadContentMetadataAsync("ContextHub", s.Asset.Id, s.Version.Id, "synthetic", default)).Should().BeNull();
        s.Proxy.Saves.Should().Be(0); s.Sessions.Should().BeEmpty();
    }

    [Theory]
    [InlineData(FileClassification.Normal)]
    [InlineData(FileClassification.Sensitive)]
    [InlineData(FileClassification.Restricted)]
    public async Task Purpose_is_required_for_all_content_metadata_and_dlp_keeps_existing_download_behavior(FileClassification classification)
    {
        var s = new Fixture(); s.Version.Classification = classification;
        await FluentActions.Awaiting(() => s.Disclosure.ReadContentMetadataAsync("ContextHub", s.Asset.Id, s.Version.Id, " ", default)).Should().ThrowAsync<ArgumentException>();
        (await s.Disclosure.ReadContentMetadataAsync("ContextHub", s.Asset.Id, s.Version.Id, "synthetic", default)).Should().NotBeNull();
        s.Version.ContentSha256 = "not-a-hash-sensitive-value";
        (await s.Disclosure.ReadContentMetadataAsync("ContextHub", s.Asset.Id, s.Version.Id, "synthetic", default))!.ContentSha256.Should().BeNull();
        s.Proxy.Saves.Should().Be(0);
    }

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(false, false, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public async Task Audit_uses_exact_existing_admin_reader_predicate_and_masks_before_paging(bool admin, bool securityScope, bool service)
    {
        var s = new Fixture();
        s.Actor.Current = s.Actor.Current with
        {
            Role = admin ? TenantUserRole.Admin : TenantUserRole.Member,
            Scopes = securityScope ? [SecurityScopes.MemoryRead, SecurityScopes.SecurityManage] : [SecurityScopes.MemoryRead],
            IsServiceActor = service
        };
        s.Events.Add(s.Event());
        for (var i = 0; i < 105; i++) s.Events.Add(s.Event(allowed: false));
        var hidden = s.Event(); hidden.TenantId = Guid.NewGuid(); s.Events.Add(hidden);
        var otherVersion = s.Event(); otherVersion.FileVersionId = Guid.NewGuid(); s.Events.Add(otherVersion);
        if (!admin && !securityScope)
        {
            await FluentActions.Awaiting(() => s.Disclosure.ReadAuditAsync("ContextHub", s.Asset.Id, s.Version.Id, 1, 0, default)).Should().ThrowAsync<UnauthorizedAccessException>();
            s.Proxy.AuditReads.Should().Be(0); return;
        }
        var page = await s.Disclosure.ReadAuditAsync("ContextHub", s.Asset.Id, s.Version.Id, 1, 0, default);
        page!.Items.Should().ContainSingle(); page.HasMore.Should().BeFalse();
        page.ActorMasked.Should().BeTrue(); page.PurposeMasked.Should().BeTrue(); page.DeniedDetailsMasked.Should().BeTrue();
        var json = JsonSerializer.Serialize(page);
        json.Should().NotContain("raw-actor").And.NotContain("raw-purpose").And.NotContain("raw-reason");
        typeof(ManagedFileAuditEntry).GetProperties().Select(p => p.Name).Should().Equal("AuditId", "Operation", "CreatedAt");
        s.Proxy.Saves.Should().Be(0);
        s.Foundation.DeniedRights.Add("metadata"); var before = s.Proxy.AuditReads;
        (await s.Disclosure.ReadAuditAsync("ContextHub", s.Asset.Id, s.Version.Id, 1, 0, default)).Should().BeNull();
        s.Proxy.AuditReads.Should().Be(before);
    }

    [Fact]
    public async Task Audit_pages_all_visible_successes_with_stable_order_without_denied_lookahead_or_totals()
    {
        var s = new Fixture(); s.Actor.Current = s.Actor.Current with { Role = TenantUserRole.Admin };
        for (var i = 0; i < 102; i++) { var e = s.Event(); e.CreatedAt = DateTimeOffset.UnixEpoch.AddSeconds(i); s.Events.Add(e); }
        for (var i = 0; i < 250; i++) s.Events.Add(s.Event(false));
        var first = await s.Disclosure.ReadAuditAsync("ContextHub", s.Asset.Id, s.Version.Id, 100, 0, default);
        var last = await s.Disclosure.ReadAuditAsync("ContextHub", s.Asset.Id, s.Version.Id, 100, 100, default);
        first!.Items.Should().HaveCount(100); first.HasMore.Should().BeTrue();
        last!.Items.Should().HaveCount(2); last.HasMore.Should().BeFalse();
        first.Items.Concat(last.Items).Select(e => e.AuditId).Should().OnlyHaveUniqueItems();
        s.Proxy.Saves.Should().Be(0);
    }

    [Fact]
    public void Audit_sql_projection_excludes_raw_actor_purpose_and_reason_without_opening_database()
    {
        var s = new Fixture();
        using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>().UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var service = new ManagedFileDisclosureService(db, s.Actor, s.Foundation, null!);
        var query = (IQueryable<ManagedFileAuditEntry>)typeof(ManagedFileDisclosureService)
            .GetMethod("VisibleAuditEntries", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(service, [s.Actor.Current, "ContextHub", s.Asset.Id, s.Version.Id])!;
        var sql = query.ToQueryString();
        sql.Should().Contain("tenant_id").And.Contain("file_version_id").And.Contain("allowed");
        sql.Should().NotContain("actor_id").And.NotContain("purpose").And.NotContain("reason_code");
        db.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
    }

    private sealed class Fixture
    {
        public RequestActorAccessor Actor { get; } = new();
        public FileAsset Asset { get; }
        public FileVersion Version { get; }
        public ManagedObject Object { get; }
        public List<FileVersion> Versions { get; } = [];
        public List<ManagedTransferSession> Sessions { get; } = [];
        public Foundation Foundation { get; } = new();
        public DbProxy Proxy { get; }
        public ManagedFileDownloadService Service { get; }
        public ManagedFileDisclosureService Disclosure { get; }
        public List<FileAccessEvent> Events { get; } = [];
        public FileAccessEvent Event(bool allowed = true) => new()
        {
            FileAssetId = Asset.Id,
            FileVersionId = Version.Id,
            TenantId = Actor.Current.TenantId,
            OwnerUserId = Actor.Current.UserId,
            ProjectId = "ContextHub",
            Operation = FileOperation.Download,
            ActorId = "raw-actor",
            Purpose = "raw-purpose",
            ReasonCode = "raw-reason",
            Allowed = allowed,
            CreatedAt = DateTimeOffset.UnixEpoch
        };
        public Guid ExecutionId { get; } = Guid.NewGuid();
        public CreateLogicalFileDownloadRequest Request => new("ContextHub", "synthetic-review", "synthetic-request", "synthetic-agent", ExecutionId);
        public Fixture()
        {
            Actor.Current = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member,
                [SecurityScopes.MemoryRead], ["ContextHub"], true);
            Asset = new() { TenantId = Actor.Current.TenantId, OwnerUserId = Actor.Current.UserId, ProjectId = "ContextHub" };
            Object = new()
            {
                TenantId = Actor.Current.TenantId,
                OwnerUserId = Actor.Current.UserId,
                ProjectId = "ContextHub",
                State = ManagedObjectState.Ready,
                PlaintextLength = 100,
                ChunkSize = 65536,
                EncryptionGeneration = 1
            };
            Version = new()
            {
                FileAsset = Asset,
                FileAssetId = Asset.Id,
                ManagedObjectId = Object.Id,
                VersionNumber = 1,
                Classification = FileClassification.Normal,
                Lifecycle = FileVersionLifecycle.Ready
            };
            Versions.Add(Version);
            Asset.Versions.Add(Version);
            var db = DispatchProxy.Create<IApplicationDbContext, DbProxy>();
            Proxy = (DbProxy)(object)db;
            Proxy.Sets["get_FileAssets"] = new TestSet<FileAsset>([Asset]);
            Proxy.Sets["get_FileVersions"] = new TestSet<FileVersion>(Versions);
            Proxy.Sets["get_ManagedObjects"] = new TestSet<ManagedObject>([Object]);
            Proxy.Sets["get_ManagedTransferSessions"] = new TestSet<ManagedTransferSession>(Sessions);
            Proxy.Sets["get_FileAccessEvents"] = new TestSet<FileAccessEvent>(Events);
            var transfers = new ManagedTransferService(db, Actor, new Clock(), new NoContentStore(), new NoKeys(), Foundation, Options.Create(new ManagedTransferOptions()));
            Service = new(db, Actor, transfers);
            Disclosure = new(db, Actor, Foundation, new ManagedFileReadService(db, Actor, Foundation));
        }
        public Task<LogicalFileDownloadSessionResult?> Create() => Service.CreateAsync(Asset.Id, Version.Id, Request, default);
    }

    public class DbProxy : DispatchProxy
    {
        public Dictionary<string, object> Sets { get; } = [];
        public int Saves { get; private set; }
        public int AuditReads { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (Sets.TryGetValue(targetMethod!.Name, out var set)) { if (targetMethod.Name == "get_FileAccessEvents") AuditReads++; return set; }
            if (targetMethod.Name == "SaveChangesAsync") { ((CancellationToken)args![0]!).ThrowIfCancellationRequested(); Saves++; return Task.FromResult(1); }
            throw new NotSupportedException(targetMethod.Name);
        }
    }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
    private sealed class NoContentStore : IManagedObjectStore
    {
        public Task PutChunkAsync(string id, int index, ReadOnlyMemory<byte> bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<int> ReadChunkAsync(string id, int index, Memory<byte> bytes, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> ChunkExistsAsync(string id, int index, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteObjectAsync(string id, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class NoKeys : IManagedFileKeyAuthority
    {
        public WrappedManagedKey Wrap(Guid id, int generation, ReadOnlySpan<byte> key) => throw new NotSupportedException();
        public byte[] Unwrap(Guid id, int generation, WrappedManagedKey key) => throw new NotSupportedException();
        public WrappedManagedKey Rewrap(Guid id, int generation, WrappedManagedKey key) => throw new NotSupportedException();
    }
    private sealed class Foundation : IPlatformFoundationStore
    {
        public bool Allowed { get; set; } = true;
        public HashSet<string> DeniedRights { get; } = [];
        public Action? BeforeEvaluate { get; set; }
        public List<(string, string, string)> Requests { get; } = [];
        public Task<EffectiveRightsResult> EvaluateAsync(string projectId, string principalId, IReadOnlyList<string> rights, string? resourceType, string? resourceId, CancellationToken ct)
        {
            BeforeEvaluate?.Invoke();
            ct.ThrowIfCancellationRequested();
            Requests.Add((projectId, rights.Single(), resourceId!));
            return Task.FromResult(new EffectiveRightsResult(projectId, principalId, new(1, 1, 1, 1), [new(rights.Single(), Allowed && !DeniedRights.Contains(rights.Single()), "Resource", [], "synthetic")]));
        }
        public Task<AuthorizationTopologyEdge> UpsertTopologyEdgeAsync(TopologyEdgeUpsertRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<AuthorizationRule> UpsertPolicyAsync(AuthorizationRuleUpsertRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<AuthorizationRule> UpsertExplicitGrantAsync(AuthorizationRuleUpsertRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<CanonicalTagSuggestion> SuggestTagAsync(CanonicalTagSuggestionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class TestSet<T>(List<T> values) : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T> where T : class
    {
        private readonly AsyncQuery<T> query = new(values);
        public override Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType => throw new NotSupportedException();
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => ((IQueryable<T>)query).Expression;
        IQueryProvider IQueryable.Provider => new AsyncProvider(query);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => values.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => values.GetEnumerator();
        public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default) => new AsyncEnumerator<T>(values.GetEnumerator(), ct);
        public override ValueTask<EntityEntry<T>> AddAsync(T entity, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); values.Add(entity); return ValueTask.FromResult<EntityEntry<T>>(null!); }
    }
    private sealed class AsyncQuery<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        public AsyncQuery(IEnumerable<T> values) : base(values) { }
        public AsyncQuery(Expression expression) : base(expression) { }
        IQueryProvider IQueryable.Provider => new AsyncProvider(this);
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken ct = default) => new AsyncEnumerator<T>(this.AsEnumerable().GetEnumerator(), ct);
    }
    private sealed class Properties : ExpressionVisitor
    {
        protected override Expression VisitMethodCall(MethodCallExpression node)
        {
            if (node.Method.DeclaringType != typeof(EF) || node.Method.Name != nameof(EF.Property)) return base.VisitMethodCall(node);
            var target = Visit(node.Arguments[0]);
            while (target is UnaryExpression { NodeType: ExpressionType.Convert } conversion) target = conversion.Operand;
            return Expression.Property(target, (string)((ConstantExpression)node.Arguments[1]).Value!);
        }
    }
    private sealed class AsyncProvider(IQueryProvider inner) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => (IQueryable)Activator.CreateInstance(typeof(AsyncQuery<>).MakeGenericType(expression.Type.GetGenericArguments()[0]), new Properties().Visit(expression))!;
        public IQueryable<T> CreateQuery<T>(Expression expression) => new AsyncQuery<T>(new Properties().Visit(expression));
        public object? Execute(Expression expression) => inner.Execute(new Properties().Visit(expression));
        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(new Properties().Visit(expression));
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested();
            var resultType = typeof(TResult).GetGenericArguments()[0];
            var execute = typeof(IQueryProvider).GetMethods().Single(method => method.Name == nameof(Execute) && method.IsGenericMethod);
            var result = execute.MakeGenericMethod(resultType).Invoke(inner, [new Properties().Visit(expression)]);
            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result])!;
        }
    }
    private sealed class AsyncEnumerator<T>(IEnumerator<T> inner, CancellationToken ct) : IAsyncEnumerator<T>
    {
        public T Current => inner.Current;
        public ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
        public ValueTask<bool> MoveNextAsync() { ct.ThrowIfCancellationRequested(); return ValueTask.FromResult(inner.MoveNext()); }
    }
}

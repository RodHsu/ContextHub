using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Memory.UnitTests;

public sealed class ManagedFileReadTests
{
    [Fact]
    public async Task Denied_prefix_does_not_truncate_visible_paging_or_leak_into_has_more()
    {
        var setup = new Fixture();
        for (var i = 0; i < 351; i++) setup.AddFile(allowed: false);
        var expected = Enumerable.Range(0, 4).Select(_ => setup.AddFile()).ToArray();
        var first = await setup.Service.ListAsync("ContextHub", 2, 0, CancellationToken.None);
        first.Items.Select(row => row.FileId).Should().Equal(expected.Take(2));
        first.HasMore.Should().BeTrue();
        var second = await setup.Service.ListAsync("ContextHub", 2, 2, CancellationToken.None);
        second.Items.Select(row => row.FileId).Should().Equal(expected.Skip(2));
        second.HasMore.Should().BeFalse();
        var empty = await setup.Service.ListAsync("ContextHub", 2, 4, CancellationToken.None);
        empty.Items.Should().BeEmpty();
        empty.HasMore.Should().BeFalse();
        setup.Foundation.RequestedRights.Should().OnlyContain(right => right == "metadata");
        setup.Proxy.Mutations.Should().Be(0);
    }

    [Theory]
    [InlineData(false, false, 1)]
    [InlineData(true, false, 2)]
    [InlineData(false, true, 2)]
    public async Task Tenant_owner_admin_and_service_scope_match_inventory(bool admin, bool service, int expected)
    {
        var setup = new Fixture(admin, service);
        setup.AddFile();
        setup.AddFile(owner: Guid.NewGuid());
        setup.AddFile(tenant: Guid.NewGuid());
        setup.AddFile(project: "OtherProject");
        var page = await setup.Service.ListAsync("ContextHub", 100, 0, CancellationToken.None);
        page.Items.Should().HaveCount(expected);
        setup.Accessor.Current = setup.Accessor.Current with { AllowedProjectIds = ["OtherProject"] };
        Func<Task> denied = () => setup.Service.ListAsync("ContextHub", 1, 0, CancellationToken.None);
        await denied.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task Old_pins_are_bound_to_file_and_deleted_or_hidden_resources_are_absent()
    {
        var setup = new Fixture();
        var file = setup.AddFile();
        var oldVersion = setup.Versions.Single();
        var latest = setup.AddVersion(file, 2);
        var deleted = setup.AddVersion(file, 3, FileVersionLifecycle.LogicalDeleted);
        var otherFile = setup.AddFile();
        (await setup.Service.GetAsync("ContextHub", file, CancellationToken.None))!.FileVersionId.Should().Be(latest.Id);
        (await setup.Service.GetVersionAsync("ContextHub", file, oldVersion.Id, CancellationToken.None))!.VersionNumber.Should().Be(1);
        (await setup.Service.GetVersionAsync("ContextHub", otherFile, oldVersion.Id, CancellationToken.None)).Should().BeNull();
        (await setup.Service.GetVersionAsync("ContextHub", file, deleted.Id, CancellationToken.None)).Should().BeNull();
        var page = await setup.Service.ListVersionsAsync("ContextHub", file, 1, 0, CancellationToken.None);
        page!.Items.Single().FileVersionId.Should().Be(latest.Id);
        page.HasMore.Should().BeTrue();
        var tail = await setup.Service.ListVersionsAsync("ContextHub", file, 1, 1, CancellationToken.None);
        tail!.Items.Single().FileVersionId.Should().Be(oldVersion.Id);
        tail.HasMore.Should().BeFalse();
        setup.Foundation.Denied.Add(file);
        (await setup.Service.GetAsync("ContextHub", file, CancellationToken.None)).Should().BeNull();
        (await setup.Service.ListVersionsAsync("ContextHub", file, 1, 0, CancellationToken.None)).Should().BeNull();
        setup.Assets.Single(asset => asset.Id == otherFile).State = FileAssetState.LogicalDeleted;
        (await setup.Service.GetAsync("ContextHub", otherFile, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task An_asset_without_active_versions_is_absent_from_all_metadata_reads()
    {
        var setup = new Fixture();
        var file = setup.AddFile();
        var version = setup.Versions.Single();
        version.Lifecycle = FileVersionLifecycle.LogicalDeleted;
        (await setup.Service.ListAsync("ContextHub", 10, 0, default)).Items.Should().BeEmpty();
        (await setup.Service.GetAsync("ContextHub", file, default)).Should().BeNull();
        (await setup.Service.ListVersionsAsync("ContextHub", file, 10, 0, default)).Should().BeNull();
        (await setup.Service.GetVersionAsync("ContextHub", file, version.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Status_metadata_preserves_quarantine_rescan_and_hold_without_exposing_content_or_mutating()
    {
        var setup = new Fixture();
        var id = setup.AddFile();
        var asset = setup.Assets.Single();
        asset.Relations.Add(new FileRelation { Kind = FileRelationKind.LegalHold, TargetId = "private-hold-target" });
        var version = setup.Versions.Single();
        version.Classification = FileClassification.Quarantined;
        version.Lifecycle = FileVersionLifecycle.Quarantined;
        version.NeedsRescan = true;
        version.Findings.Add(new FileSecurityFinding { Severity = SecurityFindingSeverity.High, Disposition = SecurityFindingDisposition.Confirmed });
        version.Findings.Add(new FileSecurityFinding { Severity = SecurityFindingSeverity.Critical, Disposition = SecurityFindingDisposition.Resolved });
        var row = await setup.Service.GetVersionAsync("ContextHub", id, version.Id, CancellationToken.None);
        row!.Classification.Should().Be(FileClassification.Quarantined);
        row.NeedsRescan.Should().BeTrue();
        row.RelationCount.Should().Be(1);
        row.OpenSecurityFindings.Should().Be(1);
        row.HighestOpenSeverity.Should().Be(SecurityFindingSeverity.High);
        var properties = typeof(ManagedFileInventoryResult).GetProperties().Select(property => property.Name).ToArray();
        properties.Should().NotContain(["ManagedObjectId", "ContentSha256", "SizeBytes", "ActorId", "Purpose", "Relations", "Audit", "WrappedDek", "StorageId"]);
        typeof(ManagedFileMetadataPage).GetProperties().Select(property => property.Name).Should().Equal("Items", "Limit", "Offset", "HasMore");
        setup.Proxy.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task Cancellation_stops_read_scanning_without_saving()
    {
        var setup = new Fixture();
        setup.AddFile();
        using var source = new CancellationTokenSource();
        source.Cancel();
        Func<Task> operation = () => setup.Service.ListAsync("ContextHub", 1, 0, source.Token);
        await operation.Should().ThrowAsync<OperationCanceledException>();
        setup.Proxy.Mutations.Should().Be(0);
    }

    [Fact]
    public async Task Read_deadline_cancels_instead_of_reporting_a_false_complete_page()
    {
        var clock = new DeadlineClock();
        var setup = new Fixture(timeProvider: clock);
        setup.AddFile();
        setup.Foundation.BeforeEvaluate = () => clock.Fire!();
        Func<Task> operation = () => setup.Service.ListAsync("ContextHub", 1, 0, default);
        await operation.Should().ThrowAsync<OperationCanceledException>();
        clock.Due.Should().Be(TimeSpan.FromSeconds(15));
        setup.Proxy.Mutations.Should().Be(0);
    }

    private sealed class DeadlineClock : TimeProvider
    {
        public Action? Fire { get; private set; }
        public TimeSpan Due { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        { Due = dueTime; Fire = () => callback(state); return new Timer(); }
        private sealed class Timer : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period) => true;
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    [Fact]
    public void Latest_scalar_projection_translates_without_opening_a_database()
    {
        var setup = new Fixture();
        using var db = new MemoryDbContext(new DbContextOptionsBuilder<MemoryDbContext>()
            .UseNpgsql("Host=localhost;Database=unused;Username=unused;Password=unused").Options);
        var service = new ManagedFileReadService(db, setup.Accessor, setup.Foundation);
        var query = (IQueryable<ManagedFileInventoryResult>)typeof(ManagedFileReadService)
            .GetMethod("LatestMetadata", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(service, [setup.Accessor.Current, "ContextHub", new[] { Guid.NewGuid() }])!;
        var sql = query.ToQueryString();
        sql.ToUpperInvariant().Should().Contain("MAX").And.Contain("COUNT");
        sql.Should().NotContain("content_sha256").And.NotContain("managed_object_id").And.NotContain("redacted_evidence");
        db.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
    }

    private sealed class Fixture
    {
        public List<FileAsset> Assets { get; } = [];
        public List<FileVersion> Versions { get; } = [];
        public RequestActorAccessor Accessor { get; } = new();
        public Foundation Foundation { get; } = new();
        public DbProxy Proxy { get; }
        public ManagedFileReadService Service { get; }
        private int sequence;

        public Fixture(bool admin = false, bool service = false, TimeProvider? timeProvider = null)
        {
            Accessor.Current = new(Guid.NewGuid(), Guid.NewGuid(), "reader", admin ? TenantUserRole.Admin : TenantUserRole.Member,
                [SecurityScopes.MemoryRead], ["ContextHub"], true, IsServiceActor: service);
            var db = DispatchProxy.Create<IApplicationDbContext, DbProxy>();
            Proxy = (DbProxy)(object)db;
            Proxy.Assets = new TestSet<FileAsset>(Assets);
            Proxy.Versions = new TestSet<FileVersion>(Versions);
            Service = new(db, Accessor, Foundation, timeProvider);
        }

        public Guid AddFile(bool allowed = true, Guid? tenant = null, Guid? owner = null, string project = "ContextHub")
        {
            var asset = new FileAsset
            {
                TenantId = tenant ?? Accessor.Current.TenantId,
                OwnerUserId = owner ?? Accessor.Current.UserId,
                ProjectId = project,
                LogicalFileName = "safe-name",
                UpdatedAt = DateTimeOffset.UnixEpoch.AddSeconds(-sequence++)
            };
            Assets.Add(asset);
            if (!allowed) Foundation.Denied.Add(asset.Id);
            AddVersion(asset.Id, 1);
            return asset.Id;
        }

        public FileVersion AddVersion(Guid id, int number, FileVersionLifecycle lifecycle = FileVersionLifecycle.Ready)
        {
            var asset = Assets.Single(item => item.Id == id);
            var version = new FileVersion { FileAssetId = id, FileAsset = asset, VersionNumber = number, Lifecycle = lifecycle };
            Versions.Add(version);
            asset.Versions.Add(version);
            return version;
        }
    }

    public class DbProxy : DispatchProxy
    {
        public DbSet<FileAsset> Assets { get; set; } = null!;
        public DbSet<FileVersion> Versions { get; set; } = null!;
        public int Mutations { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_FileAssets") return Assets;
            if (targetMethod.Name == "get_FileVersions") return Versions;
            Mutations++;
            throw new InvalidOperationException("Read service attempted a mutation or unrelated storage access.");
        }
    }

    private sealed class Foundation : IPlatformFoundationStore
    {
        public Action? BeforeEvaluate { get; set; }
        public HashSet<Guid> Denied { get; } = [];
        public List<string> RequestedRights { get; } = [];
        public Task<EffectiveRightsResult> EvaluateAsync(string projectId, string principalId, IReadOnlyList<string> rights, string? resourceType, string? resourceId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            BeforeEvaluate?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            RequestedRights.AddRange(rights);
            return Task.FromResult(new EffectiveRightsResult(projectId, principalId, new SecurityRevisionVector(1, 1, 1, 1),
                [new("metadata", !Denied.Contains(Guid.Parse(resourceId!)), "Resource", [], "test")]));
        }
        public Task<AuthorizationTopologyEdge> UpsertTopologyEdgeAsync(TopologyEdgeUpsertRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuthorizationRule> UpsertPolicyAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<AuthorizationRule> UpsertExplicitGrantAsync(AuthorizationRuleUpsertRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<CanonicalTagSuggestion> SuggestTagAsync(CanonicalTagSuggestionRequest request, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestSet<T>(IEnumerable<T> values) : DbSet<T>, IQueryable<T>, IAsyncEnumerable<T> where T : class
    {
        public override Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType => throw new NotSupportedException();
        private readonly AsyncQuery<T> query = new(values);
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => ((IQueryable<T>)query).Expression;
        IQueryProvider IQueryable.Provider => new AsyncProvider(query);
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => ((IEnumerable<T>)query).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => ((IEnumerable<T>)query).GetEnumerator();
        public override IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new AsyncEnumerator<T>(((IEnumerable<T>)query).GetEnumerator(), cancellationToken);
    }

    private sealed class AsyncQuery<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        public AsyncQuery(IEnumerable<T> values) : base(values) { }
        public AsyncQuery(Expression expression) : base(expression) { }
        IQueryProvider IQueryable.Provider => new AsyncProvider(this);
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new AsyncEnumerator<T>(this.AsEnumerable().GetEnumerator(), cancellationToken);
    }

    private sealed class AsyncProvider(IQueryProvider inner) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression)
            => (IQueryable)Activator.CreateInstance(typeof(AsyncQuery<>).MakeGenericType(expression.Type.GetGenericArguments()[0]), expression)!;
        public IQueryable<TElement> CreateQuery<TElement>(Expression expression) => new AsyncQuery<TElement>(expression);
        public object? Execute(Expression expression) => inner.Execute(expression);
        public TResult Execute<TResult>(Expression expression) => inner.Execute<TResult>(expression);
        public TResult ExecuteAsync<TResult>(Expression expression, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var resultType = typeof(TResult).GetGenericArguments()[0];
            var execute = typeof(IQueryProvider).GetMethods().Single(method => method.Name == nameof(Execute) && method.IsGenericMethod);
            var result = execute.MakeGenericMethod(resultType).Invoke(inner, [expression]);
            return (TResult)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(resultType).Invoke(null, [result])!;
        }
    }

    private sealed class AsyncEnumerator<T>(IEnumerator<T> inner, CancellationToken cancellationToken) : IAsyncEnumerator<T>
    {
        public T Current => inner.Current;
        public ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
        public ValueTask<bool> MoveNextAsync() { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult(inner.MoveNext()); }
    }
}

using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Query;

namespace Memory.UnitTests;

public sealed class SecretManagementReadTests
{
    [Theory]
    [InlineData("anonymous")]
    [InlineData("member")]
    [InlineData("no-identity")]
    [InlineData("no-read")]
    [InlineData("no-manage")]
    [InlineData("project")]
    public async Task Management_identity_and_read_manage_project_gates_run_before_resource_query(string scenario)
    {
        var f = new Fixture();
        f.Actor.Current = scenario switch
        {
            "anonymous" => f.Actor.Current with { IsAuthenticated = false },
            "member" => f.Actor.Current with { Role = TenantUserRole.Member },
            "no-identity" => f.Actor.Current with { UserId = null },
            "no-read" => f.Actor.Current with { Scopes = [SecurityScopes.SecretsManage] },
            "no-manage" => f.Actor.Current with { Scopes = [SecurityScopes.SecretsRead] },
            "project" => f.Actor.Current with { AllowedProjectIds = ["Other"] },
            _ => throw new InvalidOperationException()
        };
        await FluentActions.Awaiting(() => f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, default)).Should().ThrowAsync<UnauthorizedAccessException>();
        f.Proxy.SecretReads.Should().Be(0);
        f.Proxy.Saves.Should().Be(0);
    }

    [Theory]
    [InlineData("tenant")]
    [InlineData("owner")]
    [InlineData("project")]
    [InlineData("missing")]
    [InlineData("metadata")]
    [InlineData("manage")]
    [InlineData("no-policy")]
    [InlineData("deny")]
    [InlineData("expired-grant")]
    public async Task Hidden_or_denied_secret_returns_no_expiry_lease_or_history_payload(string scenario)
    {
        var f = new Fixture();
        switch (scenario)
        {
            case "tenant": f.Secret.TenantId = Guid.NewGuid(); break;
            case "owner": f.Secret.OwnerUserId = Guid.NewGuid(); break;
            case "project": f.Secret.ProjectId = "Other"; break;
            case "missing": f.Secrets.Clear(); break;
            case "metadata": f.Foundation.DeniedRights.Add("secret.metadata"); break;
            case "manage": f.Foundation.DeniedRights.Add("secret.manage"); break;
            case "no-policy": f.Policies.Clear(); break;
            case "deny": f.Policies[0].Effect = AuthorizationEffect.Deny; break;
            case "expired-grant": f.Policies.Clear(); f.Grants.Add(f.Grant(AuthorizationEffect.Allow, DateTimeOffset.UnixEpoch)); break;
        }
        (await f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, default)).Should().BeNull();
        (await f.Service.ReadLeasesAsync("ContextHub", f.Secret.Id, 25, 0, default)).Should().BeNull();
        (await f.Service.ReadHistoryAsync("ContextHub", f.Secret.Id, 25, 0, default)).Should().BeNull();
        f.Proxy.LeaseReads.Should().Be(0); f.Proxy.HistoryReads.Should().Be(0); f.Proxy.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Resource_allow_tier_overrides_project_deny_but_same_tier_deny_wins()
    {
        var f = new Fixture(); f.Policies[0].Effect = AuthorizationEffect.Deny;
        f.Grants.Add(f.Grant(AuthorizationEffect.Allow));
        (await f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, default)).Should().NotBeNull();
        f.Grants.Add(f.Grant(AuthorizationEffect.Deny));
        (await f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task Safe_current_expiry_has_exact_version_and_missing_pin_remains_unknown_without_material()
    {
        var f = new Fixture();
        var result = await f.Service.ReadExpiryAsync("contexthub", f.Secret.Id, default);
        result!.CurrentVersion.Should().Be(3); result.VersionState.Should().Be(SecretVersionState.Retired);
        result.ExpiresAt.Should().Be(DateTimeOffset.UnixEpoch.AddDays(1)); result.Revision.Should().Be(7);
        f.Foundation.Requests.Should().Contain(("contexthub", "secret.metadata", f.Secret.Id.ToString("D")));
        f.Foundation.Requests.Should().Contain(("contexthub", "secret.manage", f.Secret.Id.ToString("D")));
        AssertSafe(JsonSerializer.Serialize(result));
        f.Version.SecretId = Guid.NewGuid();
        var missing = await f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, default);
        missing!.CurrentVersion.Should().BeNull(); missing.ExpiresAt.Should().BeNull(); missing.VersionState.Should().BeNull();
        f.Proxy.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Service_actor_preserves_tenant_scope_without_broadening_an_admin_to_other_owners()
    {
        var f = new Fixture(); f.Secret.OwnerUserId = Guid.NewGuid();
        (await f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, default)).Should().BeNull();
        f.Actor.Current = f.Actor.Current with { IsServiceActor = true };
        (await f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, default)).Should().NotBeNull();
        f.Secret.TenantId = Guid.NewGuid();
        (await f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, default)).Should().BeNull();
    }

    [Fact]
    public async Task History_excludes_denied_and_foreign_rows_before_offset_lookahead_and_never_selects_sensitive_fields()
    {
        var f = new Fixture();
        for (var i = 0; i < 151; i++) f.Events.Add(f.Event(false, DateTimeOffset.UnixEpoch.AddMinutes(i)));
        f.Events.Add(f.Event(true, DateTimeOffset.UnixEpoch.AddSeconds(1)));
        f.Events.Add(f.Event(true, DateTimeOffset.UnixEpoch.AddSeconds(2)));
        f.Events.Add(f.Event(true, DateTimeOffset.UnixEpoch.AddSeconds(3)));
        var foreign = f.Event(true, DateTimeOffset.UnixEpoch.AddDays(2)); foreign.OwnerUserId = Guid.NewGuid(); f.Events.Add(foreign);
        var other = f.Event(true, DateTimeOffset.UnixEpoch.AddDays(2)); other.ProjectId = "Other"; f.Events.Add(other);
        var first = await f.Service.ReadHistoryAsync("ContextHub", f.Secret.Id, 1, 0, default);
        first!.Items.Should().ContainSingle().Which.CreatedAt.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(3));
        first.HasMore.Should().BeTrue();
        var last = await f.Service.ReadHistoryAsync("ContextHub", f.Secret.Id, 1, 2, default);
        last!.Items.Should().ContainSingle().Which.CreatedAt.Should().Be(DateTimeOffset.UnixEpoch.AddSeconds(1));
        last.HasMore.Should().BeFalse();
        first.ActorMasked.Should().BeTrue(); first.PurposeMasked.Should().BeTrue(); first.TargetMasked.Should().BeTrue(); first.DeniedDetailsMasked.Should().BeTrue();
        AssertSafe(JsonSerializer.Serialize(first)); f.Proxy.Saves.Should().Be(0);
    }

    [Fact]
    public async Task Leases_have_safe_usage_expiry_without_capabilities_or_identity_and_keep_stored_state_separate()
    {
        var f = new Fixture();
        var older = f.Lease(DateTimeOffset.UnixEpoch.AddSeconds(-1)); older.ExpiresAt = DateTimeOffset.UnixEpoch.AddSeconds(-1);
        older.State = SecretLeaseState.Active; older.MaxUses = 2; older.UsedCount = 5; f.Leases.Add(older);
        f.Leases.Add(f.Lease(DateTimeOffset.UnixEpoch.AddSeconds(1)));
        var foreign = f.Lease(DateTimeOffset.UnixEpoch.AddDays(1)); foreign.TenantId = Guid.NewGuid(); f.Leases.Add(foreign);
        var page = await f.Service.ReadLeasesAsync("ContextHub", f.Secret.Id, 1, 1, default);
        page!.HasMore.Should().BeFalse(); page.Items.Should().ContainSingle();
        page.Items[0].State.Should().Be(SecretLeaseState.Active); page.Items[0].IsExpired.Should().BeTrue(); page.Items[0].RemainingUses.Should().Be(0);
        page.SensitiveDetailsMasked.Should().BeTrue(); AssertSafe(JsonSerializer.Serialize(page)); f.Proxy.Saves.Should().Be(0);
    }

    [Fact]
    public async Task All_reads_honor_cancellation_and_page_bounds()
    {
        var f = new Fixture(); using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await FluentActions.Awaiting(() => f.Service.ReadExpiryAsync("ContextHub", f.Secret.Id, cancelled.Token)).Should().ThrowAsync<OperationCanceledException>();
        var page = await f.Service.ReadLeasesAsync("ContextHub", f.Secret.Id, int.MaxValue, -1, default);
        page!.Limit.Should().Be(100); page.Offset.Should().Be(0); page.Items.Should().BeEmpty();
        await FluentActions.Awaiting(() => f.Service.ReadHistoryAsync(" ", f.Secret.Id, 1, 0, default)).Should().ThrowAsync<ArgumentException>();
    }

    private static void AssertSafe(string json)
    {
        foreach (var text in new[] { "synthetic-private", "Capability", "ActorId", "Purpose\"", "TargetHash", "RequestId", "ReasonCode", "Ciphertext", "WrappedDek", "Plaintext", "KeyId", "LeaseId", "Total" }) json.Should().NotContain(text);
    }

    private sealed class Fixture
    {
        public Actor Actor { get; } = new();
        public Foundation Foundation { get; } = new();
        public Secret Secret { get; }
        public SecretVersion Version { get; }
        public List<Secret> Secrets { get; } = [];
        public List<SecretVersion> Versions { get; } = [];
        public List<SecretGrant> Grants { get; } = [];
        public List<SecretPolicy> Policies { get; } = [];
        public List<SecretLease> Leases { get; } = [];
        public List<SecretAccessEvent> Events { get; } = [];
        public DbProxy Proxy { get; }
        public SecretManagementReadService Service { get; }
        public Fixture()
        {
            Secret = new() { TenantId = Actor.Current.TenantId, OwnerUserId = Actor.Current.UserId, ProjectId = "ContextHub", Revision = 7 };
            Version = new() { SecretId = Secret.Id, VersionNumber = 3, State = SecretVersionState.Retired, ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1), KeyId = "synthetic-private", Ciphertext = [1, 2, 3] };
            Secret.CurrentVersionId = Version.Id; Secrets.Add(Secret); Versions.Add(Version);
            Policies.Add(new() { TenantId = Actor.Current.TenantId, OwnerUserId = Actor.Current.UserId, ProjectId = "ContextHub", PrincipalId = "*", Right = SecretRight.Manage, Effect = AuthorizationEffect.Allow });
            var db = DispatchProxy.Create<IApplicationDbContext, DbProxy>(); Proxy = (DbProxy)(object)db;
            Proxy.Sets["get_Secrets"] = new TestSet<Secret>(Secrets); Proxy.Sets["get_SecretVersions"] = new TestSet<SecretVersion>(Versions);
            Proxy.Sets["get_SecretGrants"] = new TestSet<SecretGrant>(Grants); Proxy.Sets["get_SecretPolicies"] = new TestSet<SecretPolicy>(Policies);
            Proxy.Sets["get_SecretLeases"] = new TestSet<SecretLease>(Leases); Proxy.Sets["get_SecretAccessEvents"] = new TestSet<SecretAccessEvent>(Events);
            Service = new(db, Actor, Foundation, new Clock());
        }
        public SecretGrant Grant(AuthorizationEffect effect, DateTimeOffset? expiresAt = null) => new() { TenantId = Actor.Current.TenantId, OwnerUserId = Actor.Current.UserId, SecretId = Secret.Id, ProjectId = "ContextHub", PrincipalId = "*", Right = SecretRight.Manage, Effect = effect, ExpiresAt = expiresAt };
        public SecretAccessEvent Event(bool allowed, DateTimeOffset created) => new() { TenantId = Actor.Current.TenantId, OwnerUserId = Actor.Current.UserId, SecretId = Secret.Id, ProjectId = "ContextHub", Allowed = allowed, Operation = SecretAccessOperation.Use, CreatedAt = created, ActorId = "synthetic-private", Purpose = "synthetic-private", TargetHash = "synthetic-private", ReasonCode = "synthetic-private", RequestId = "synthetic-private" };
        public SecretLease Lease(DateTimeOffset created) => new() { TenantId = Actor.Current.TenantId, OwnerUserId = Actor.Current.UserId, SecretId = Secret.Id, SecretVersionId = Version.Id, ProjectId = "ContextHub", Kind = SecretLeaseKind.Use, CreatedAt = created, ExpiresAt = DateTimeOffset.UnixEpoch.AddDays(1), CapabilityHash = "synthetic-private", Purpose = "synthetic-private", Target = "synthetic-private", ActorId = "synthetic-private", MaxUses = 10, UsedCount = 2, ActiveUses = 1 };
    }
    private sealed class Actor : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Admin,
            [SecurityScopes.SecretsRead, SecurityScopes.SecretsManage], ["ContextHub"], true);
    }
    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
    public class DbProxy : DispatchProxy
    {
        public Dictionary<string, object> Sets { get; } = [];
        public int Saves { get; private set; }
        public int SecretReads { get; private set; }
        public int LeaseReads { get; private set; }
        public int HistoryReads { get; private set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod!.Name == "get_Secrets") SecretReads++;
            if (targetMethod.Name == "get_SecretLeases") LeaseReads++;
            if (targetMethod.Name == "get_SecretAccessEvents") HistoryReads++;
            if (Sets.TryGetValue(targetMethod.Name, out var set)) return set;
            if (targetMethod.Name == "SaveChangesAsync") { Saves++; return Task.FromResult(1); }
            throw new NotSupportedException(targetMethod.Name);
        }
    }
    private sealed class Foundation : IPlatformFoundationStore
    {
        public HashSet<string> DeniedRights { get; } = [];
        public List<(string, string, string)> Requests { get; } = [];
        public Task<EffectiveRightsResult> EvaluateAsync(string projectId, string principalId, IReadOnlyList<string> rights, string? resourceType, string? resourceId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested(); Requests.Add((projectId, rights.Single(), resourceId!));
            return Task.FromResult(new EffectiveRightsResult(projectId, principalId, new(1, 1, 1, 1), [new(rights.Single(), !DeniedRights.Contains(rights.Single()), "Resource", [], "synthetic")]));
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

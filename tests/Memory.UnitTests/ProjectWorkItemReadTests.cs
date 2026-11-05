using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;

namespace Memory.UnitTests;

public sealed class ProjectWorkItemReadTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), Owner = Guid.NewGuid();
    private static ContextHubRequestActor Actor(bool service = false) => new(Tenant, Owner, "reader", TenantUserRole.Member,
        [SecurityScopes.MemoryRead], ["ContextHub"], true, service);
    private static ProjectWorkItem Item(Guid? owner = null, Guid? tenant = null) => new()
    {
        TenantId = tenant ?? Tenant,
        OwnerUserId = owner ?? Owner,
        ProjectId = "ContextHub",
        Title = "read",
        UpdatedAt = DateTimeOffset.UnixEpoch,
        ChecklistItems = []
    };
    private static ProjectWorkItemService Service(ContextHubRequestActor actor, params ProjectWorkItem[] items)
    {
        var db = DispatchProxy.Create<IApplicationDbContext, ReadContext>();
        ((ReadContext)db).Items = new ReadSet<ProjectWorkItem>(items);
        return new(db, new RequestActorAccessor { Current = actor }, new Clock());
    }

    [Fact]
    public async Task Get_respects_owner_tenant_and_service_actor_boundaries()
    {
        var own = Item(); var otherOwner = Item(Guid.NewGuid()); var otherTenant = Item(tenant: Guid.NewGuid());
        var service = Service(Actor(), own, otherOwner, otherTenant);
        (await service.GetAsync(own.Id, false, default))!.Id.Should().Be(own.Id);
        (await service.GetAsync(otherOwner.Id, false, default)).Should().BeNull();
        (await service.GetAsync(otherTenant.Id, false, default)).Should().BeNull();
        (await service.GetAsync(Guid.NewGuid(), false, default)).Should().BeNull();
        var machine = Service(Actor(service: true), otherOwner, otherTenant);
        (await machine.GetAsync(otherOwner.Id, false, default))!.Id.Should().Be(otherOwner.Id);
        (await machine.GetAsync(otherTenant.Id, false, default)).Should().BeNull();
    }

    [Fact]
    public async Task Get_excludes_archived_unless_requested_and_returns_ordered_checklist()
    {
        var item = Item(); item.ArchivedAt = DateTimeOffset.UtcNow;
        item.ChecklistItems = [new() { Content = "last", SortOrder = 2 }, new() { Content = "first", SortOrder = 0 }];
        var service = Service(Actor(), item);
        (await service.GetAsync(item.Id, false, default)).Should().BeNull();
        var result = await service.GetAsync(item.Id, true, default);
        result!.ChecklistItems.Select(x => x.Content).Should().Equal("first", "last");
        result.IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task Get_requires_read_scope_before_resolving_item_visibility()
    {
        var item = Item();
        Func<Task> missingScope = () => Service(Actor() with { Scopes = [] }, item).GetAsync(item.Id, false, default);
        await missingScope.Should().ThrowAsync<UnauthorizedAccessException>();
        item.ProjectId = "Other";
        Func<Task> hiddenMissingScope = () => Service(Actor() with { Scopes = [] }, item).GetAsync(item.Id, true, default);
        await hiddenMissingScope.Should().ThrowAsync<UnauthorizedAccessException>();
        Func<Task> unknownMissingScope = () => Service(Actor() with { Scopes = [] }).GetAsync(Guid.NewGuid(), false, default);
        await unknownMissingScope.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Get_hides_disallowed_project_like_missing_item(bool serviceActor, bool archived)
    {
        var hidden = Item(serviceActor ? Guid.NewGuid() : Owner);
        hidden.ProjectId = "PrivateProject";
        hidden.Title = "Private title";
        if (archived) hidden.ArchivedAt = DateTimeOffset.UnixEpoch;
        var service = Service(Actor(serviceActor), hidden);

        (await service.GetAsync(hidden.Id, false, default)).Should().BeNull();
        (await service.GetAsync(hidden.Id, true, default)).Should().BeNull();
        (await service.GetAsync(Guid.NewGuid(), true, default)).Should().BeNull();
    }

    [Theory]
    [InlineData("contexthub", false)]
    [InlineData("SHARED", false)]
    [InlineData("user", false)]
    [InlineData("Other", true)]
    public async Task Get_preserves_existing_project_identity_and_unrestricted_grants(string projectId, bool unrestricted)
    {
        var item = Item();
        item.ProjectId = projectId;
        var actor = unrestricted ? Actor() with { AllowedProjectIds = [] } : Actor();

        (await Service(actor, item).GetAsync(item.Id, false, default))!.Id.Should().Be(item.Id);
    }

    [Fact]
    public async Task List_offset_is_after_actor_project_filters_and_has_a_stable_tie_break()
    {
        var a = Item(); a.Id = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var b = Item(); b.Id = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var hidden = Item(Guid.NewGuid()); var archived = Item(); archived.ArchivedAt = DateTimeOffset.UtcNow;
        var other = Item(); other.ProjectId = "Other";
        var service = Service(Actor(), b, hidden, archived, other, a);
        var first = await service.ListAsync(new("ContextHub", Limit: 1), default);
        var second = await service.ListAsync(new("ContextHub", Limit: 1, Offset: 1), default);
        first.Select(x => x.Id).Should().Equal(a.Id);
        second.Select(x => x.Id).Should().Equal(b.Id);
        (await service.ListAsync(new("ContextHub", Offset: 2), default)).Should().BeEmpty();
        (await service.ListAsync(new("ContextHub", Limit: 1, Offset: -1), default)).Single().Id.Should().Be(a.Id);
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }
    public class ReadContext : DispatchProxy
    {
        public DbSet<ProjectWorkItem> Items { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method!.Name == "get_ProjectWorkItems" ? Items : throw new InvalidOperationException("Read path must not save or access unrelated sets.");
    }
    private sealed class ReadSet<T>(IEnumerable<T> items) : DbSet<T>, IQueryable<T> where T : class
    {
        public override Microsoft.EntityFrameworkCore.Metadata.IEntityType EntityType => throw new NotSupportedException();
        private readonly IQueryable<T> query = new ReadQuery<T>(items);
        Type IQueryable.ElementType => typeof(T);
        Expression IQueryable.Expression => query.Expression;
        IQueryProvider IQueryable.Provider => query.Provider;
        IEnumerator<T> IEnumerable<T>.GetEnumerator() => query.GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => query.GetEnumerator();
    }
    private sealed class ReadQuery<T> : EnumerableQuery<T>, IAsyncEnumerable<T>, IQueryable<T>
    {
        public ReadQuery(IEnumerable<T> items) : base(items) { }
        public ReadQuery(Expression expression) : base(expression) { }
        IQueryProvider IQueryable.Provider => new ReadProvider(this);
        public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default)
            => new AsyncEnumerator<T>(this.AsEnumerable().GetEnumerator(), cancellationToken);
    }
    private sealed class ReadProvider(IQueryProvider inner) : IAsyncQueryProvider
    {
        public IQueryable CreateQuery(Expression expression) => throw new NotSupportedException();
        public IQueryable<T> CreateQuery<T>(Expression expression) => new ReadQuery<T>(expression);
        public object? Execute(Expression expression) => inner.Execute(expression);
        public T Execute<T>(Expression expression) => inner.Execute<T>(expression);
        public T ExecuteAsync<T>(Expression expression, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = typeof(T).GenericTypeArguments.Single();
            return (T)typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(type)
                .Invoke(null, [inner.Execute(expression)])!;
        }
    }
    private sealed class AsyncEnumerator<T>(IEnumerator<T> inner, CancellationToken token) : IAsyncEnumerator<T>
    {
        public T Current => inner.Current;
        public ValueTask DisposeAsync() { inner.Dispose(); return ValueTask.CompletedTask; }
        public ValueTask<bool> MoveNextAsync() { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(inner.MoveNext()); }
    }
}

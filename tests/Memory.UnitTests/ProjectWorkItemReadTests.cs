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

    private static ProjectWorkItemService EditingService(ContextHubRequestActor actor, Editing editing, params ProjectWorkItem[] items)
    {
        var db = DispatchProxy.Create<IApplicationDbContext, ReadContext>();
        ((ReadContext)db).Items = new ReadSet<ProjectWorkItem>(items);
        return new(db, new RequestActorAccessor { Current = actor }, new Clock(), () => editing);
    }

    [Theory]
    [InlineData("update")]
    [InlineData("archive")]
    [InlineData("checklist")]
    public async Task Legacy_writers_without_original_revision_reject_instead_of_filling_the_stored_revision(string operation)
    {
        var item = Item(); item.Revision = 900;
        var editing = new Editing(); var service = EditingService(Actor() with { Scopes = [SecurityScopes.MemoryWrite] }, editing, item);
        Func<Task> call = operation switch
        {
            "archive" => () => service.SetArchivedAsync(item.Id, true, default),
            "checklist" => () => service.SetChecklistItemCompletionAsync(item.Id, Guid.NewGuid(), false, default),
            _ => () => service.UpdateAsync(new(item.Id, Title: "draft"), default)
        };
        (await call.Should().ThrowAsync<SafeEditConflictException>()).Which.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        editing.Command.Should().BeNull(); item.Revision.Should().Be(900);
    }

    [Fact]
    public async Task Typed_writer_resolves_only_authorized_project_and_preserves_the_callers_stale_revision()
    {
        var item = Item(); item.Revision = 900;
        var editing = new Editing(); var service = EditingService(Actor() with { Scopes = [SecurityScopes.MemoryWrite] }, editing, item);
        var result = await service.UpdateAsync(new(item.Id, ExpectedRevision: 7, RequestId: "original-key",
            Edit: new(DueAt: new(EditDueAtMode.Clear))), default);
        editing.Command!.ExpectedRevision.Should().Be(7); editing.Command.ProjectId.Should().Be("ContextHub");
        editing.Command.RequestId.Should().Be("original-key"); editing.Command.Patch.DueAt!.Mode.Should().Be(EditDueAtMode.Clear);
        result.EditReceipt!.RequestHash.Should().Be(WorkItemEditContract.Hash(editing.Command));
        // The fake only observes adapter dispatch. It does not accept a stale write or simulate persistence.
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("tenant")]
    [InlineData("project")]
    public async Task Unauthorized_target_never_reaches_the_versioned_editor(string boundary)
    {
        var item = boundary == "owner" ? Item(Guid.NewGuid()) : boundary == "tenant" ? Item(tenant: Guid.NewGuid()) : Item();
        if (boundary == "project") item.ProjectId = "Other";
        var editing = new Editing(); var service = EditingService(Actor() with { Scopes = [SecurityScopes.MemoryWrite] }, editing, item);
        var error = await Record.ExceptionAsync(() => service.UpdateAsync(new(item.Id, ExpectedRevision: 1, RequestId: "key"), default));
        error.Should().BeAssignableTo<Exception>();
        (error is KeyNotFoundException or UnauthorizedAccessException).Should().BeTrue();
        editing.Command.Should().BeNull();
    }

    private sealed class Editing : IWorkItemEditingService
    {
        public WorkItemEditCommand? Command { get; private set; }
        public Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditCommand command, CancellationToken ct)
        {
            Command = command;
            var result = WorkItemEditProjection.ToResult(new ProjectWorkItem { Id = command.WorkItemId, ProjectId = command.ProjectId, Revision = command.ExpectedRevision!.Value });
            return Task.FromResult(new WorkItemEditReceipt(Guid.NewGuid(), null, command.ProjectId, command.WorkItemId, command.RequestId,
                WorkItemEditContract.Hash(command), WorkItemEditContract.IntentHashVersion, command.Operation, command.ExpectedRevision.Value,
                command.ExpectedRevision.Value, false, DateTimeOffset.UnixEpoch, result));
        }
        public Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditRecoveryRequest request, CancellationToken ct) => throw new NotSupportedException();
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
    public async Task Get_requires_read_scope_and_allowed_project()
    {
        var item = Item();
        Func<Task> missingScope = () => Service(Actor() with { Scopes = [] }, item).GetAsync(item.Id, false, default);
        await missingScope.Should().ThrowAsync<UnauthorizedAccessException>();
        item.ProjectId = "Other";
        Func<Task> forbiddenProject = () => Service(Actor(), item).GetAsync(item.Id, false, default);
        await forbiddenProject.Should().ThrowAsync<UnauthorizedAccessException>();
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

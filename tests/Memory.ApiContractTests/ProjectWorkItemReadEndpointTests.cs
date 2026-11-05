using System.Security.Claims;
using System.Collections;
using System.Linq.Expressions;
using System.Reflection;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.McpServer;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Query;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.ApiContractTests;

public sealed class ProjectWorkItemReadEndpointTests
{
    [Fact]
    public async Task List_binds_offset_and_existing_filters_without_changing_defaults()
    {
        var service = new Reads();
        await using var app = await Host(service);
        using var client = app.GetTestClient();
        (await client.GetAsync("/api/work-items?projectId=ContextHub&offset=23&limit=4&status=Blocked&definitionState=Frozen&includeArchived=true")).StatusCode.Should().Be(HttpStatusCode.OK);
        service.ListRequest.Should().Be(new ProjectWorkItemListRequest("ContextHub", ProjectWorkItemStatus.Blocked, 4, true, 23, ProjectWorkItemDefinitionState.Frozen));
        (await client.GetAsync("/api/work-items?projectId=ContextHub")).StatusCode.Should().Be(HttpStatusCode.OK);
        service.ListRequest.Should().Be(new ProjectWorkItemListRequest("ContextHub"));
        (await client.GetAsync("/api/work-items?projectId=ContextHub&status=unknown")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await client.GetAsync("/api/work-items?projectId=ContextHub&offset=not-a-number")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Detail_returns_resource_or_404_and_binds_archive_option()
    {
        var service = new Reads();
        await using var app = await Host(service);
        using var client = app.GetTestClient();
        var id = Guid.NewGuid();
        (await client.GetAsync($"/api/work-items/{id}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        service.GetRequest.Should().Be((id, false));
        service.Result = new(id, "ContextHub", "Synthetic", "", [], [], ProjectWorkItemStatus.Pending, 0, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null);
        var response = await client.GetAsync($"/api/work-items/{id}?includeArchived=true");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await response.Content.ReadFromJsonAsync<ProjectWorkItemResult>())!.Id.Should().Be(id);
        service.GetRequest.Should().Be((id, true));
    }

    [Fact]
    public async Task Both_reads_require_the_existing_memory_read_scope()
    {
        var service = new Reads();
        await using var app = await Host(service, false);
        using var client = app.GetTestClient();
        (await client.GetAsync("/api/work-items?projectId=ContextHub")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await client.GetAsync($"/api/work-items/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        service.ListRequest.Should().BeNull();
        service.GetRequest.Should().Be(default((Guid, bool)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detail_actual_service_hides_disallowed_project_like_missing_item(bool archived)
    {
        var actor = new FixedActor();
        var hidden = Item(actor.Current, "PrivateProject");
        hidden.Title = "Private title";
        hidden.Description = "Private description";
        if (archived) hidden.ArchivedAt = DateTimeOffset.UnixEpoch;
        await using var app = await Host(ActualService(actor, hidden), actorAccessor: actor);
        using var client = app.GetTestClient();

        foreach (var includeArchived in new[] { false, true })
        {
            using var hiddenResponse = await client.GetAsync($"/api/work-items/{hidden.Id}?includeArchived={includeArchived}");
            using var missingResponse = await client.GetAsync($"/api/work-items/{Guid.NewGuid()}?includeArchived={includeArchived}");
            hiddenResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            missingResponse.StatusCode.Should().Be(HttpStatusCode.NotFound);
            var body = await hiddenResponse.Content.ReadAsStringAsync();
            body.Should().Be(await missingResponse.Content.ReadAsStringAsync()).And.BeEmpty();
            body.Should().NotContain(hidden.ProjectId).And.NotContain(hidden.Id.ToString())
                .And.NotContain(hidden.Title).And.NotContain(hidden.Description);
            hiddenResponse.Content.Headers.ContentType.Should().Be(missingResponse.Content.Headers.ContentType);
        }
    }

    [Fact]
    public async Task Detail_actual_service_preserves_visible_owner_tenant_and_archive_boundaries()
    {
        var actor = new FixedActor();
        var visible = Item(actor.Current);
        visible.ChecklistItems = [new() { Content = "last", SortOrder = 2 }, new() { Content = "first", SortOrder = 0 }];
        var otherOwner = Item(actor.Current); otherOwner.OwnerUserId = Guid.NewGuid();
        var otherTenant = Item(actor.Current); otherTenant.TenantId = Guid.NewGuid();
        var archived = Item(actor.Current); archived.ArchivedAt = DateTimeOffset.UnixEpoch;
        await using var app = await Host(ActualService(actor, visible, otherOwner, otherTenant, archived), actorAccessor: actor);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync($"/api/work-items/{visible.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = (await response.Content.ReadFromJsonAsync<ProjectWorkItemResult>())!;
        result.Id.Should().Be(visible.Id);
        result.ProjectId.Should().Be(visible.ProjectId);
        result.ChecklistItems.Select(x => x.Content).Should().Equal("first", "last");
        foreach (var hidden in new[] { otherOwner, otherTenant })
        {
            using var denied = await client.GetAsync($"/api/work-items/{hidden.Id}?includeArchived=true");
            denied.StatusCode.Should().Be(HttpStatusCode.NotFound);
            (await denied.Content.ReadAsStringAsync()).Should().BeEmpty();
        }
        using var defaultArchived = await client.GetAsync($"/api/work-items/{archived.Id}");
        defaultArchived.StatusCode.Should().Be(HttpStatusCode.NotFound);
        using var requestedArchived = await client.GetAsync($"/api/work-items/{archived.Id}?includeArchived=true");
        requestedArchived.StatusCode.Should().Be(HttpStatusCode.OK);
        (await requestedArchived.Content.ReadFromJsonAsync<ProjectWorkItemResult>())!.IsArchived.Should().BeTrue();
    }

    [Fact]
    public async Task Detail_actual_service_keeps_missing_read_scope_forbidden_without_item_details()
    {
        var actor = new FixedActor();
        actor.Current = actor.Current with { Scopes = [] };
        var hidden = Item(actor.Current, "PrivateProject");
        await using var app = await Host(ActualService(actor, hidden), actorAccessor: actor);
        using var client = app.GetTestClient();

        foreach (var id in new[] { hidden.Id, Guid.NewGuid() })
        {
            using var response = await client.GetAsync($"/api/work-items/{id}?includeArchived=true");
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().NotContain(hidden.ProjectId).And.NotContain(hidden.Id.ToString())
                .And.NotContain(hidden.Title);
        }
    }

    private static async Task<WebApplication> Host(IProjectWorkItemService service, bool readScope = true, IRequestActorAccessor? actorAccessor = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IProjectWorkItemService>(service);
        builder.Services.AddSingleton<IRequestActorAccessor>(actorAccessor ?? new Actor(readScope));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, SyntheticAuthentication>("synthetic", _ => { });
        var app = builder.Build();
        // Retain the production error shape so a service regression exposes the old 403 detail.
        // This is a synthetic host, not the full Program startup or a database integration test.
        app.Use(async (context, next) =>
        {
            try { await next(context); }
            catch (UnauthorizedAccessException ex) when (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await Results.Problem(title: "Forbidden", detail: ex.Message, statusCode: StatusCodes.Status403Forbidden).ExecuteAsync(context);
            }
        });
        app.MapGroup("/api/work-items").MapWorkItemReadEndpoints(true);
        await app.StartAsync();
        return app;
    }
    private sealed class Actor(bool readScope) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member, readScope ? [SecurityScopes.MemoryRead] : [], ["ContextHub"], true);
    }

    private sealed class FixedActor : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member,
            [SecurityScopes.MemoryRead], ["ContextHub"], true);
    }

    private static ProjectWorkItem Item(ContextHubRequestActor actor, string project = "ContextHub") => new()
    {
        TenantId = actor.TenantId,
        OwnerUserId = actor.UserId,
        ProjectId = project,
        Title = "Synthetic",
        UpdatedAt = DateTimeOffset.UnixEpoch,
        ChecklistItems = []
    };

    private static ProjectWorkItemService ActualService(IRequestActorAccessor actor, params ProjectWorkItem[] items)
    {
        var db = DispatchProxy.Create<IApplicationDbContext, ReadContext>();
        ((ReadContext)db).Items = new ReadSet<ProjectWorkItem>(items);
        return new(db, actor, new Clock());
    }

    private sealed class Clock : IClock { public DateTimeOffset UtcNow => DateTimeOffset.UnixEpoch; }

    public class ReadContext : DispatchProxy
    {
        public DbSet<ProjectWorkItem> Items { get; set; } = null!;
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => method!.Name == "get_ProjectWorkItems" ? Items : throw new InvalidOperationException("Read path must not save or access unrelated sets.");
    }

    // Same async query strategy as the existing service unit tests; no real DbContext/provider/connection.
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
    private sealed class SyntheticAuthentication(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("synthetic")), "synthetic")));
    }
    private sealed class Reads : IProjectWorkItemService
    {
        public ProjectWorkItemListRequest? ListRequest { get; private set; }
        public (Guid, bool) GetRequest { get; private set; }
        public ProjectWorkItemResult? Result { get; set; }
        public Task<ProjectWorkItemResult?> GetAsync(Guid id, bool includeArchived, CancellationToken cancellationToken) { GetRequest = (id, includeArchived); return Task.FromResult(Result); }
        public Task<IReadOnlyList<ProjectWorkItemResult>> ListAsync(ProjectWorkItemListRequest request, CancellationToken cancellationToken) { ListRequest = request; return Task.FromResult<IReadOnlyList<ProjectWorkItemResult>>([]); }
        public Task<ProjectWorkItemResult> CreateAsync(ProjectWorkItemCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> UpdateAsync(ProjectWorkItemUpdateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> SetArchivedAsync(Guid id, bool archived, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid id, Guid checklist, bool completed, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> SetGovernanceExclusionAsync(ProjectWorkItemGovernanceExclusionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}

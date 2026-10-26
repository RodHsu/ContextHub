using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.McpServer;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.ApiContractTests;

/// <summary>Real HTTP mapping with synthetic use cases; not persistence, transaction or authorization acceptance.</summary>
public sealed class ProjectWorkItemEditingEndpointTests
{
    private static readonly Guid Id = Guid.Parse("b56a17a7-8fe0-4b03-867a-d57407ef47b1");
    private static readonly Guid Leaf = Guid.Parse("e243d1d6-3577-424a-8e69-6c968dfdddf8");
    private const string Project = "ContextHub";
    private const string Key = "synthetic-edit-key";
    private static string Path => $"/api/work-items/{Id:D}";

    [Fact]
    public async Task Canonical_edit_binds_the_complete_original_intent_and_returns_no_store()
    {
        var fixture = new Fixture(); await using var app = await Host(fixture); using var client = app.GetTestClient();
        var command = Command(new(Title: "Synthetic title", Description: "Synthetic description", Tags: ["one", "two"],
            Priority: 15, Status: ProjectWorkItemStatus.InProgress, DefinitionState: ProjectWorkItemDefinitionState.ReadyForDevelopment,
            DueAt: new(EditDueAtMode.Set, DateTimeOffset.UnixEpoch)));
        using var response = await client.PostAsJsonAsync(Path + "/edits", command);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Edits.ApplyRequest.Should().BeEquivalentTo(command);
        fixture.Edits.ApplyCalls.Should().Be(1);
        var receipt = await response.Content.ReadFromJsonAsync<WorkItemEditReceipt>();
        receipt!.WorkItemId.Should().Be(Id); receipt.RequestId.Should().Be(Key); receipt.Revision.Should().Be(8);
        fixture.Legacy.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Canonical_route_and_body_target_mismatch_is_rejected_before_the_use_case()
    {
        var fixture = new Fixture(); await using var app = await Host(fixture); using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(Path + "/edits", Command() with { WorkItemId = Leaf });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("TargetMismatch");
        fixture.Edits.ApplyCalls.Should().Be(0);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Receipt_recovery_binds_the_exact_project_key_fingerprint_and_hash_version()
    {
        var fixture = new Fixture(); await using var app = await Host(fixture); using var client = app.GetTestClient();
        var hash = new string('b', 64);
        var key = "synthetic key & recovery";
        using var response = await client.GetAsync(Path + "/edit-receipts?projectId=ContextHub&requestId=" + Uri.EscapeDataString(key)
            + "&expectedRequestHash=" + hash + "&hashVersion=" + Uri.EscapeDataString(WorkItemEditContract.IntentHashVersion));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        fixture.Edits.RecoveryRequest.Should().Be(new WorkItemEditRecoveryRequest(Project, Id, key, hash, WorkItemEditContract.IntentHashVersion));
        (await response.Content.ReadFromJsonAsync<WorkItemEditReceipt>())!.RequestId.Should().Be(key);

        fixture.Edits.ReceiptMissing = true;
        using var missing = await client.GetAsync(Path + "/edit-receipts?projectId=ContextHub&requestId=" + Key + "&expectedRequestHash=" + hash);
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsStringAsync()).Should().BeEmpty();
        fixture.Edits.RecoveryRequest!.HashVersion.Should().Be(WorkItemEditContract.IntentHashVersion);
        fixture.Edits.ApplyCalls.Should().Be(0, "recovery must not substitute an edit operation");
    }

    [Fact]
    public async Task Unconfirmed_recovery_returns_unknown_without_replaying_the_edit()
    {
        var fixture = new Fixture(); fixture.Edits.Failure = new Exception("synthetic-private");
        await using var app = await Host(fixture); using var client = app.GetTestClient();
        using var response = await client.GetAsync(Path + "/edit-receipts?projectId=ContextHub&requestId=" + Key + "&expectedRequestHash=" + new string('a', 64));
        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await response.Content.ReadAsStringAsync()).Should().Contain("RequestOutcomeUnknown").And.NotContain("synthetic-private");
        fixture.Edits.RecoveryCalls.Should().Be(1); fixture.Edits.ApplyCalls.Should().Be(0);
    }

    [Theory]
    [InlineData("omitted", EditDueAtMode.Keep)]
    [InlineData("null", EditDueAtMode.Clear)]
    [InlineData("value", EditDueAtMode.Set)]
    public async Task Legacy_put_preserves_due_at_presence_and_original_preconditions(string representation, EditDueAtMode mode)
    {
        var fixture = new Fixture(); await using var app = await Host(fixture); using var client = app.GetTestClient();
        var body = new Dictionary<string, object?> { ["expectedRevision"] = 7, ["requestId"] = Key, ["title"] = "Synthetic" };
        if (representation == "null") body["dueAt"] = null;
        if (representation == "value") body["dueAt"] = DateTimeOffset.UnixEpoch;
        using var response = await client.PutAsJsonAsync(Path, body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var request = fixture.Legacy.UpdateRequest!;
        request.Id.Should().Be(Id); request.ExpectedRevision.Should().Be(7); request.RequestId.Should().Be(Key);
        request.Operation.Should().Be(WorkItemEditOperation.Update);
        request.Edit!.Title.Should().Be("Synthetic");
        request.Edit.DueAt!.Mode.Should().Be(mode);
        request.Edit.DueAt.Value.Should().Be(mode == EditDueAtMode.Set ? DateTimeOffset.UnixEpoch : null);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
    }

    [Fact]
    public async Task Legacy_put_does_not_merge_flat_and_nested_edit_representations()
    {
        var fixture = new Fixture(); await using var app = await Host(fixture); using var client = app.GetTestClient();
        using var response = await client.PutAsJsonAsync(Path, new
        {
            expectedRevision = 7,
            requestId = Key,
            title = "Flat",
            edit = new { description = "Nested" }
        });
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain("InvalidEditIntent").And.NotContain("Use one edit representation");
        fixture.Legacy.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Nested_legacy_put_binds_operation_and_typed_patch_without_flattening_it()
    {
        var fixture = new Fixture(); await using var app = await Host(fixture); using var client = app.GetTestClient();
        using var response = await client.PutAsJsonAsync(Path, new
        {
            expectedRevision = 7,
            requestId = Key,
            operation = WorkItemEditOperation.ChecklistText,
            edit = new WorkItemEditPatch(ChecklistItemId: Leaf, ChecklistContent: "Retained original leaf")
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Legacy.UpdateRequest!.Operation.Should().Be(WorkItemEditOperation.ChecklistText);
        fixture.Legacy.UpdateRequest.Edit!.ChecklistItemId.Should().Be(Leaf);
        fixture.Legacy.UpdateRequest.Edit.ChecklistContent.Should().Be("Retained original leaf");
    }

    [Theory]
    [InlineData(SafeEditConflictCodes.MissingReadVersion, null)]
    [InlineData(SafeEditConflictCodes.StaleReadVersion, 9L)]
    public async Task Known_edit_conflicts_have_typed_409_details(string code, long? current)
    {
        var fixture = new Fixture(); fixture.Edits.Failure = new SafeEditConflictException(code, current);
        await using var app = await Host(fixture); using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(Path + "/edits", Command());
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("code").GetString().Should().Be(code);
        json.RootElement.GetProperty("currentRevision").ValueKind.Should().Be(current.HasValue ? JsonValueKind.Number : JsonValueKind.Null);
        if (current.HasValue) json.RootElement.GetProperty("currentRevision").GetInt64().Should().Be(current.Value);
    }

    [Theory]
    [InlineData("forbidden", HttpStatusCode.Forbidden)]
    [InlineData("hidden", HttpStatusCode.NotFound)]
    [InlineData("argument", HttpStatusCode.BadRequest)]
    [InlineData("state", HttpStatusCode.BadRequest)]
    [InlineData("unexpected", HttpStatusCode.ServiceUnavailable)]
    [InlineData("cancelled", HttpStatusCode.ServiceUnavailable)]
    public async Task Private_failures_are_not_returned_as_success_or_raw_details(string failure, HttpStatusCode expected)
    {
        var fixture = new Fixture(); fixture.Edits.Failure = Error(failure);
        await using var app = await Host(fixture); using var client = app.GetTestClient();
        using var response = await client.PostAsJsonAsync(Path + "/edits", Command());
        response.StatusCode.Should().Be(expected);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var json = await response.Content.ReadAsStringAsync();
        json.Should().NotContain("synthetic-private").And.NotContain("stack").And.NotContain("Exception");
        if (expected == HttpStatusCode.ServiceUnavailable) json.Should().Contain("RequestOutcomeUnknown").And.Contain("requestId");
    }

    [Fact]
    public async Task Empty_legacy_archive_body_reaches_the_typed_service_with_missing_preconditions()
    {
        var fixture = new Fixture(); fixture.Legacy.Failure = new SafeEditConflictException(SafeEditConflictCodes.MissingReadVersion);
        await using var app = await Host(fixture); using var client = app.GetTestClient();
        using var response = await client.PostAsync(Path + "/archive", null);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        fixture.Legacy.ArchiveRequest.Should().Be(new ProjectWorkItemArchiveRequest(Id, true, null, null));
        fixture.Legacy.OriginalScalarCalls.Should().Be(0);
        // The fake only dispatches a known failure; actual missing-version enforcement is an Application test.
    }

    [Fact]
    public async Task Legacy_lifecycle_checklist_and_governance_routes_pass_typed_original_preconditions()
    {
        var fixture = new Fixture(); await using var app = await Host(fixture); using var client = app.GetTestClient();
        var body = new { expectedRevision = 7, requestId = Key };
        (await client.PostAsJsonAsync(Path + "/archive", body)).StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Legacy.ArchiveRequest.Should().Be(new ProjectWorkItemArchiveRequest(Id, true, 7, Key));
        (await client.PostAsJsonAsync(Path + "/restore", body)).StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Legacy.ArchiveRequest.Should().Be(new ProjectWorkItemArchiveRequest(Id, false, 7, Key));
        (await client.PutAsJsonAsync(Path + $"/checklist/{Leaf:D}", new { isCompleted = false, expectedRevision = 7, requestId = Key })).StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Legacy.ChecklistRequest.Should().Be(new ProjectWorkItemChecklistUpdateRequest(Id, Leaf, false, 7, Key));
        (await client.PutAsJsonAsync(Path + "/governance-exclusion", new
        {
            projectId = Project,
            governanceRunId = "synthetic-run",
            reason = "Synthetic reason",
            excluded = false,
            expectedRevision = 7,
            requestId = Key
        })).StatusCode.Should().Be(HttpStatusCode.OK);
        fixture.Legacy.ExclusionRequest.Should().Be(new ProjectWorkItemGovernanceExclusionRequest(Id, Project, "synthetic-run", "Synthetic reason", false, 7, Key));
        fixture.Legacy.OriginalScalarCalls.Should().Be(0);
        // These fakes do not assert the extra governance-management resource authorization.
    }

    [Fact]
    public async Task Recovery_requires_memory_read_before_the_use_case()
    {
        var fixture = new Fixture(); await using var app = await Host(fixture, read: false); using var client = app.GetTestClient();
        using var response = await client.GetAsync(Path + "/edit-receipts?projectId=ContextHub&requestId=" + Key + "&expectedRequestHash=" + new string('a', 64));
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        fixture.Edits.RecoveryCalls.Should().Be(0);
    }

    [Fact]
    public async Task Every_edit_route_requires_memory_write_before_either_service()
    {
        var fixture = new Fixture(); await using var app = await Host(fixture, write: false); using var client = app.GetTestClient();
        var writes = new (HttpMethod Method, string Url, object Body)[]
        {
            (HttpMethod.Post, Path + "/edits", Command()),
            (HttpMethod.Put, Path, new { expectedRevision = 7, requestId = Key, title = "Synthetic" }),
            (HttpMethod.Post, Path + "/archive", new { expectedRevision = 7, requestId = Key }),
            (HttpMethod.Post, Path + "/restore", new { expectedRevision = 7, requestId = Key }),
            (HttpMethod.Put, Path + $"/checklist/{Leaf:D}", new { expectedRevision = 7, requestId = Key, isCompleted = false }),
            (HttpMethod.Put, Path + "/governance-exclusion", new { projectId = Project, governanceRunId = "synthetic-run", reason = "Synthetic", expectedRevision = 7, requestId = Key })
        };
        foreach (var write in writes)
        {
            using var request = new HttpRequestMessage(write.Method, write.Url) { Content = JsonContent.Create(write.Body) };
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        }
        fixture.Edits.ApplyCalls.Should().Be(0); fixture.Legacy.Calls.Should().Be(0);
    }

    private static WorkItemEditCommand Command(WorkItemEditPatch? patch = null)
        => new(Project, Id, 7, Key, WorkItemEditOperation.Update, patch ?? new(Title: "Synthetic"));

    private static Exception Error(string kind) => kind switch
    {
        "forbidden" => new UnauthorizedAccessException("synthetic-private"),
        "hidden" => new KeyNotFoundException("synthetic-private"),
        "argument" => new ArgumentException("synthetic-private"),
        "state" => new InvalidOperationException("synthetic-private"),
        "cancelled" => new OperationCanceledException("synthetic-private"),
        _ => new Exception("synthetic-private")
    };

    private static async Task<WebApplication> Host(Fixture fixture, bool read = true, bool write = true)
    {
        var builder = WebApplication.CreateBuilder(); builder.WebHost.UseTestServer();
        builder.Services.AddSingleton<IWorkItemEditingService>(fixture.Edits);
        builder.Services.AddSingleton<IProjectWorkItemService>(fixture.Legacy);
        builder.Services.AddSingleton<IRequestActorAccessor>(new Actor(read, write));
        builder.Services.AddAuthentication("synthetic").AddScheme<AuthenticationSchemeOptions, Auth>("synthetic", _ => { });
        var app = builder.Build();
        app.MapGroup("/api/work-items").MapWorkItemEditingEndpoints(true);
        await app.StartAsync(); return app;
    }

    private sealed class Actor(bool read, bool write) : IRequestActorAccessor
    {
        public ContextHubRequestActor Current { get; set; } = new(Guid.NewGuid(), Guid.NewGuid(), "synthetic", TenantUserRole.Member,
            (read ? new[] { SecurityScopes.MemoryRead } : []).Concat(write ? [SecurityScopes.MemoryWrite] : []).ToArray(), [Project], true);
    }

    private sealed class Auth(IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity("synthetic")), "synthetic")));
    }

    private sealed class Fixture
    {
        public Edits Edits { get; } = new();
        public Legacy Legacy { get; } = new();
    }

    private sealed class Edits : IWorkItemEditingService
    {
        public Exception? Failure { get; set; }
        public int ApplyCalls { get; private set; }
        public int RecoveryCalls { get; private set; }
        public bool ReceiptMissing { get; set; }
        public WorkItemEditCommand? ApplyRequest { get; private set; }
        public WorkItemEditRecoveryRequest? RecoveryRequest { get; private set; }
        public Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditCommand command, CancellationToken ct)
        {
            ApplyCalls++; ApplyRequest = command; if (Failure is not null) throw Failure;
            return Task.FromResult(Receipt(command.WorkItemId, command.RequestId));
        }
        public Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditRecoveryRequest request, CancellationToken ct)
        {
            RecoveryCalls++; RecoveryRequest = request; if (Failure is not null) throw Failure;
            return Task.FromResult(ReceiptMissing ? null : Receipt(request.WorkItemId, request.RequestId));
        }
    }

    private sealed class Legacy : IProjectWorkItemService
    {
        public int Calls { get; private set; }
        public int OriginalScalarCalls { get; private set; }
        public Exception? Failure { get; set; }
        public ProjectWorkItemUpdateRequest? UpdateRequest { get; private set; }
        public ProjectWorkItemArchiveRequest? ArchiveRequest { get; private set; }
        public ProjectWorkItemChecklistUpdateRequest? ChecklistRequest { get; private set; }
        public ProjectWorkItemGovernanceExclusionRequest? ExclusionRequest { get; private set; }
        private Task<ProjectWorkItemResult> Result(Guid id) { Calls++; if (Failure is not null) throw Failure; return Task.FromResult(Row(id)); }
        public Task<ProjectWorkItemResult> UpdateAsync(ProjectWorkItemUpdateRequest request, CancellationToken ct)
        { UpdateRequest = request; return Result(request.Id); }
        public Task<ProjectWorkItemResult> SetArchivedAsync(ProjectWorkItemArchiveRequest request, CancellationToken ct)
        { ArchiveRequest = request; return Result(request.WorkItemId); }
        public Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(ProjectWorkItemChecklistUpdateRequest request, CancellationToken ct)
        { ChecklistRequest = request; return Result(request.WorkItemId); }
        public Task<ProjectWorkItemResult> SetGovernanceExclusionAsync(ProjectWorkItemGovernanceExclusionRequest request, CancellationToken ct)
        { ExclusionRequest = request; return Result(request.WorkItemId); }
        public Task<ProjectWorkItemResult> SetArchivedAsync(Guid id, bool archived, CancellationToken ct)
        { OriginalScalarCalls++; throw new NotSupportedException("The endpoint must call the versioned overload."); }
        public Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid id, Guid leaf, bool completed, CancellationToken ct)
        { OriginalScalarCalls++; throw new NotSupportedException("The endpoint must call the versioned overload."); }
        public Task<ProjectWorkItemResult?> GetAsync(Guid id, bool includeArchived, CancellationToken ct) => throw new NotSupportedException();
        public Task<IReadOnlyList<ProjectWorkItemResult>> ListAsync(ProjectWorkItemListRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> CreateAsync(ProjectWorkItemCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
    }

    private static ProjectWorkItemResult Row(Guid id) => new(id, Project, "Synthetic result", "", [], [],
        ProjectWorkItemStatus.InProgress, 0, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null)
    { Revision = 8 };

    private static WorkItemEditReceipt Receipt(Guid id, string key) => new(Guid.Parse("3669d973-1088-478b-bb57-fb830d26ecd2"),
        Guid.Parse("ba5c7433-2e86-4d2b-bfd4-bb21d15b1a8c"), Project, id, key, new string('a', 64), WorkItemEditContract.IntentHashVersion,
        WorkItemEditOperation.Update, 7, 8, true, DateTimeOffset.UnixEpoch, Row(id));
}

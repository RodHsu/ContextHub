using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Services;
using Memory.Domain;
using Memory.Dashboard.Components.Pages;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.DashboardTests;

public sealed class FileMetadataReadTests
{
    [Fact]
    public async Task Client_should_use_only_scoped_gets_for_page_latest_history_and_pinned_version()
    {
        var file = File();
        using var handler = new MetadataHandler(request => request.RequestUri!.AbsolutePath.EndsWith("inventory", StringComparison.Ordinal) || request.RequestUri.AbsolutePath.EndsWith("versions", StringComparison.Ordinal)
            ? JsonContent.Create(new ManagedFileMetadataPage([file], 25, 25, true)) : JsonContent.Create(file));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://context-hub.example.test") };
        var api = new ContextHubApiClient(http);
        (await api.GetManagedFilePageAsync("team/one", 25, 25, CancellationToken.None))!.HasMore.Should().BeTrue();
        (await api.GetManagedFileMetadataAsync("team/one", file.FileId, CancellationToken.None))!.FileId.Should().Be(file.FileId);
        (await api.GetManagedFileVersionsAsync("team/one", file.FileId, 25, 50, CancellationToken.None))!.Items.Should().ContainSingle();
        (await api.GetManagedFileVersionAsync("team/one", file.FileId, file.FileVersionId, CancellationToken.None))!.FileVersionId.Should().Be(file.FileVersionId);
        handler.Requests.Should().HaveCount(4).And.OnlyContain(x => x.Method == HttpMethod.Get);
        handler.Requests.Should().OnlyContain(x => x.Uri.Query.Contains("projectId=team%2Fone", StringComparison.OrdinalIgnoreCase));
        handler.Requests[0].Uri.Query.Should().Contain("limit=25").And.Contain("offset=25");
        handler.Requests[2].Uri.Query.Should().Contain("offset=50");
        handler.Requests[3].Uri.AbsolutePath.Should().EndWith($"/versions/{file.FileVersionId:D}");
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.NotImplemented)]
    public async Task Missing_read_surface_should_be_unknown(HttpStatusCode status)
    {
        using var http = new HttpClient(new MetadataHandler(_ => new StringContent(""), status)) { BaseAddress = new Uri("https://context-hub.example.test") };
        var api = new ContextHubApiClient(http);
        (await api.GetManagedFilePageAsync("p", 25, 0, CancellationToken.None)).Should().BeNull();
        (await api.GetManagedFileMetadataAsync("p", Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
        (await api.GetManagedFileVersionsAsync("p", Guid.NewGuid(), 25, 0, CancellationToken.None)).Should().BeNull();
        (await api.GetManagedFileVersionAsync("p", Guid.NewGuid(), Guid.NewGuid(), CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Forbidden_page_should_fail_without_legacy_fallback()
    {
        var legacyCalls = 0;
        var api = Api((method, _) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromException<ManagedFileMetadataPage?>(new UnauthorizedAccessException("private")),
            nameof(IContextHubApiClient.GetManagedFilesAsync) => CountLegacy(),
            _ => throw new InvalidOperationException(method)
        });
        Task<IReadOnlyList<ManagedFileInventoryResult>> CountLegacy() { legacyCalls++; return Task.FromResult<IReadOnlyList<ManagedFileInventoryResult>>([File()]); }
        using var state = new FileMetadataReadState(api);
        await state.LoadAsync("p");
        state.Error.Should().NotBeNull().And.NotContain("private");
        state.Files.Should().BeEmpty();
        legacyCalls.Should().Be(0);
        state.Legacy.Should().BeFalse();
    }

    [Fact]
    public async Task Unsupported_page_should_fallback_explicitly_without_claiming_pagination()
    {
        var file = File();
        using var state = new FileMetadataReadState(Api((method, _) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromResult<ManagedFileMetadataPage?>(null),
            nameof(IContextHubApiClient.GetManagedFilesAsync) => Task.FromResult<IReadOnlyList<ManagedFileInventoryResult>>([file]),
            _ => null
        }));
        await state.LoadAsync("p");
        state.Legacy.Should().BeTrue();
        state.HasMore.Should().BeFalse();
        state.Offset.Should().Be(0);
        state.Files.Should().ContainSingle();
        state.Versions.Should().BeNull();
    }

    [Fact]
    public async Task New_scope_page_should_cancel_and_reject_late_old_page()
    {
        var old = new TaskCompletionSource<ManagedFileMetadataPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var api = Api((method, args) => method == nameof(IContextHubApiClient.GetManagedFilePageAsync)
            ? (string)args[0]! == "old" ? Capture() : Task.FromResult<ManagedFileMetadataPage?>(new([], 25, 25, false)) : null);
        Task<ManagedFileMetadataPage?> Capture() { return old.Task; }
        // Capture cancellation through the real request arguments.
        ((FileReadApiProxy)(object)api).Observe = (method, args) => { if (method == nameof(IContextHubApiClient.GetManagedFilePageAsync) && (string)args[0]! == "old") token = (CancellationToken)args[3]!; };
        using var state = new FileMetadataReadState(api);
        var first = state.LoadAsync("old");
        await state.LoadAsync("new", 25);
        token.IsCancellationRequested.Should().BeTrue();
        old.SetResult(new([File()], 25, 0, true));
        await first;
        state.ProjectId.Should().Be("new");
        state.Offset.Should().Be(25);
        state.Files.Should().BeEmpty();
        state.Rights.Should().BeNull();
    }

    [Fact]
    public async Task New_selection_should_clear_old_rights_history_and_ignore_late_metadata()
    {
        var first = File();
        var second = File(Guid.Parse("aaaabbbb-0000-0000-0000-000000000002"));
        var late = new TaskCompletionSource<ManagedFileInventoryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var delayFirst = false;
        var api = Api((method, args) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([first, second], 25, 0, false)),
            nameof(IContextHubApiClient.GetManagedFileMetadataAsync) => (Guid)args[1]! == first.FileId && delayFirst ? late.Task : Task.FromResult<ManagedFileInventoryResult?>((Guid)args[1]! == first.FileId ? first : second),
            nameof(IContextHubApiClient.GetManagedFileVersionsAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([], 25, 0, false)),
            nameof(IContextHubApiClient.GetEffectiveRightsAsync) => Task.FromResult(new EffectiveRightsResult("p", "reader", new(1, 1, 1, 1), [])),
            _ => null
        });
        using var state = new FileMetadataReadState(api);
        await state.LoadAsync("p");
        state.Rights.Should().NotBeNull();
        state.Versions.Should().NotBeNull();
        delayFirst = true;
        var old = state.SelectAsync(first);
        state.Rights.Should().BeNull();
        state.Versions.Should().BeNull();
        await state.SelectAsync(second);
        late.SetResult(first);
        await old;
        state.Selected!.FileId.Should().Be(second.FileId);
        state.Version!.FileId.Should().Be(second.FileId);
    }

    [Fact]
    public async Task Late_unsupported_page_cannot_start_legacy_read_for_a_new_scope()
    {
        var pending = new TaskCompletionSource<ManagedFileMetadataPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var legacyCalls = 0;
        using var state = new FileMetadataReadState(Api((method, args) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => (string)args[0]! == "old" ? pending.Task : Task.FromResult<ManagedFileMetadataPage?>(new([], 25, 0, false)),
            nameof(IContextHubApiClient.GetManagedFilesAsync) => Legacy(),
            _ => null
        }));
        Task<IReadOnlyList<ManagedFileInventoryResult>> Legacy() { legacyCalls++; return Task.FromResult<IReadOnlyList<ManagedFileInventoryResult>>([]); }
        var old = state.LoadAsync("old");
        await state.LoadAsync("new");
        pending.SetResult(null);
        await old;
        legacyCalls.Should().Be(0);
        state.ProjectId.Should().Be("new");
        state.Files.Should().BeEmpty();
        state.Legacy.Should().BeFalse();
    }

    [Fact]
    public async Task Late_pinned_version_should_not_replace_newer_latest_selection()
    {
        var file = File();
        var pin = new TaskCompletionSource<ManagedFileInventoryResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken pinToken = default;
        var api = Api((method, args) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([file], 25, 0, false)),
            nameof(IContextHubApiClient.GetManagedFileMetadataAsync) => Task.FromResult<ManagedFileInventoryResult?>(file),
            nameof(IContextHubApiClient.GetManagedFileVersionAsync) => pin.Task,
            _ => null
        });
        ((FileReadApiProxy)(object)api).Observe = (method, args) =>
        {
            if (method == nameof(IContextHubApiClient.GetManagedFileVersionAsync)) pinToken = (CancellationToken)args[3]!;
        };
        using var state = new FileMetadataReadState(api);
        await state.LoadAsync("p");
        var oldId = Guid.NewGuid();
        var old = state.SelectAsync(file, oldId);
        await state.SelectAsync(file);
        pinToken.IsCancellationRequested.Should().BeTrue();
        pin.SetResult(file with { FileVersionId = oldId, VersionNumber = 1 });
        await old;
        state.PinnedVersion.Should().BeFalse();
        state.Version!.FileVersionId.Should().Be(file.FileVersionId);
    }

    [Fact]
    public async Task Pinned_version_should_not_silently_fall_back_to_latest_and_dispose_should_clear_data()
    {
        var file = File();
        using var state = new FileMetadataReadState(Api((method, _) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([file], 25, 0, false)),
            nameof(IContextHubApiClient.GetManagedFileMetadataAsync) => Task.FromResult<ManagedFileInventoryResult?>(file),
            nameof(IContextHubApiClient.GetManagedFileVersionAsync) => Task.FromResult<ManagedFileInventoryResult?>(null),
            _ => null
        }));
        await state.LoadAsync("p");
        await state.SelectAsync(file, Guid.NewGuid());
        state.PinnedVersion.Should().BeTrue();
        state.Version.Should().BeNull();
        state.ReleaseRequirement.Should().BeNull();
        state.Dispose();
        state.Files.Should().BeEmpty();
        state.Selected.Should().BeNull();
        state.Rights.Should().BeNull();
    }

    [Fact]
    public async Task Missing_pin_survives_actual_history_next_and_previous_handlers()
    {
        var file = File(); var pin = Guid.NewGuid(); var requested = new List<Guid>();
        using var state = new FileMetadataReadState(Api((method, args) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([file], 25, 0, false)),
            nameof(IContextHubApiClient.GetManagedFileMetadataAsync) => Task.FromResult<ManagedFileInventoryResult?>(file),
            nameof(IContextHubApiClient.GetManagedFileVersionsAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([], 25, (int)args[3]!, true)),
            nameof(IContextHubApiClient.GetManagedFileVersionAsync) => Missing((Guid)args[2]!),
            _ => null
        }));
        Task<ManagedFileInventoryResult?> Missing(Guid id) { requested.Add(id); return Task.FromResult<ManagedFileInventoryResult?>(null); }
        await state.LoadAsync("p");
        await state.SelectAsync(file, pin);
        var page = new Files();
        typeof(Files).GetField("_state", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, state);
        foreach (var handler in new[] { "NextVersionsAsync", "PreviousVersionsAsync" })
        {
            await (Task)typeof(Files).GetMethod(handler, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, null)!;
            state.PinnedVersion.Should().BeTrue();
            state.RequestedVersionId.Should().Be(pin);
            state.Version.Should().BeNull();
        }
        requested.Should().HaveCount(3).And.OnlyContain(id => id == pin);
        await page.DisposeAsync();
    }

    [Fact]
    public async Task Rendered_files_page_should_show_authorized_page_and_history_without_total_or_active_release()
    {
        var file = File();
        var api = Api((method, _) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([file], 25, 0, true)),
            nameof(IContextHubApiClient.GetManagedFileMetadataAsync) => Task.FromResult<ManagedFileInventoryResult?>(file),
            nameof(IContextHubApiClient.GetManagedFileVersionsAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([file], 25, 0, false)),
            nameof(IContextHubApiClient.GetProjectInformationProjectsAsync) => Task.FromResult<IReadOnlyList<ProjectInformationListItem>>([]),
            _ => null
        });
        var html = WebUtility.HtmlDecode(await RenderAsync(api));
        html.Should().Contain("本頁可見檔案").And.Contain("本頁搜尋符合 1 筆");
        html.Should().Contain("版本 metadata（唯讀）").And.Contain(file.FileVersionId.ToString());
        html.Should().Contain("aria-label=\"版本歷史分頁\"");
        html.Should().MatchRegex(@"<button[^>]*disabled[^>]*>\s*要求解除 quarantine");
        html.Should().NotContain("pre-signed").And.NotContain("s3://");
    }

    [Fact]
    public async Task Rendered_legacy_page_should_explain_unknown_paging_and_not_enable_next()
    {
        var api = Api((method, _) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromResult<ManagedFileMetadataPage?>(null),
            nameof(IContextHubApiClient.GetManagedFilesAsync) => Task.FromResult<IReadOnlyList<ManagedFileInventoryResult>>([]),
            nameof(IContextHubApiClient.GetProjectInformationProjectsAsync) => Task.FromResult<IReadOnlyList<ProjectInformationListItem>>([]),
            _ => null
        });
        var html = WebUtility.HtmlDecode(await RenderAsync(api));
        html.Should().Contain("範圍可能不完整，分頁與總數未知");
        html.Should().MatchRegex(@"<button[^>]*disabled[^>]*>\s*下一頁檔案");
    }

    [Fact]
    public async Task Disclosure_client_keeps_purpose_in_post_body_and_audit_in_scoped_get()
    {
        var row = File(); using var handler = new DisclosureHandler(row);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://context-hub.example.test") };
        var client = new ContextHubApiClient(http);
        (await client.ReadFileContentMetadataAsync("p", row.FileId, row.FileVersionId, "synthetic-use", default))!.FileVersionId.Should().Be(row.FileVersionId);
        (await client.ReadFileAuditAsync("p", row.FileId, row.FileVersionId, 25, 50, default))!.PurposeMasked.Should().BeTrue();
        handler.Requests[0].Method.Should().Be(HttpMethod.Post); handler.Requests[0].Uri.Query.Should().BeEmpty();
        handler.Requests[0].Body.Should().Contain("synthetic-use").And.Contain("projectId");
        handler.Requests[1].Method.Should().Be(HttpMethod.Get); handler.Requests[1].Uri.Query.Should().Contain("offset=50").And.Contain("projectId=p");
    }

    [Theory]
    [InlineData(false, false, "scope")]
    [InlineData(false, true, "scope")]
    [InlineData(true, false, "scope")]
    [InlineData(true, true, "scope")]
    [InlineData(false, false, "version")]
    [InlineData(false, true, "version")]
    [InlineData(true, false, "version")]
    [InlineData(true, true, "version")]
    [InlineData(false, false, "dispose")]
    [InlineData(false, true, "dispose")]
    [InlineData(true, false, "dispose")]
    [InlineData(true, true, "dispose")]
    public async Task Disclosure_success_or_error_cannot_outlive_scope_pin_or_disposal(bool audit, bool error, string change)
    {
        var row = File(); var oldEntry = Guid.NewGuid(); var newEntry = Guid.NewGuid();
        var contentGate = new TaskCompletionSource<ManagedFileContentMetadata?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var auditGate = new TaskCompletionSource<ManagedFileAuditPage?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingOld = true; CancellationToken token = default;
        var api = DisclosureApi(row, (method, args) =>
        {
            token = (CancellationToken)args[^1]!;
            if (method == nameof(IContextHubApiClient.ReadFileContentMetadataAsync))
                return pendingOld ? contentGate.Task : Task.FromResult<ManagedFileContentMetadata?>(new((Guid)args[1]!, (Guid)args[2]!, 42, new string('b', 64)));
            return pendingOld ? auditGate.Task : Task.FromResult<ManagedFileAuditPage?>(new((Guid)args[1]!, (Guid)args[2]!, [new(newEntry, FileOperation.Download, DateTimeOffset.UnixEpoch)], 25, 0, false));
        });
        using var state = new FileMetadataReadState(api); await state.LoadAsync("p");
        var old = audit ? state.ReadAuditAsync() : state.ReadContentMetadataAsync("synthetic");
        var oldToken = token; pendingOld = false;
        if (change == "scope") await state.LoadAsync("other");
        else if (change == "version") await state.SelectAsync(state.Selected!, Guid.NewGuid());
        else state.Dispose();
        oldToken.IsCancellationRequested.Should().BeTrue(); state.ContentMetadata.Should().BeNull(); state.Audit.Should().BeNull();
        if (change != "dispose")
        {
            if (audit) await state.ReadAuditAsync(); else await state.ReadContentMetadataAsync("synthetic");
        }
        if (audit)
        {
            if (error) auditGate.SetException(new HttpRequestException("private-marker"));
            else auditGate.SetResult(new(row.FileId, row.FileVersionId, [new(oldEntry, FileOperation.Read, DateTimeOffset.UnixEpoch)], 25, 0, false));
        }
        else
        {
            if (error) contentGate.SetException(new HttpRequestException("private-marker"));
            else contentGate.SetResult(new(row.FileId, row.FileVersionId, 999, new string('a', 64)));
        }
        await old;
        state.ContentError.Should().BeNull(); state.AuditError.Should().BeNull();
        if (change == "dispose") { state.ContentMetadata.Should().BeNull(); state.Audit.Should().BeNull(); }
        else if (audit) state.Audit!.Items.Single().AuditId.Should().Be(newEntry);
        else state.ContentMetadata!.ContentSha256.Should().Be(new string('b', 64));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Wrong_pin_disclosure_is_unavailable_not_accepted_metadata_or_an_empty_audit(bool audit)
    {
        var row = File();
        var api = DisclosureApi(row, (method, _) => method == nameof(IContextHubApiClient.ReadFileContentMetadataAsync)
            ? (object)Task.FromResult<ManagedFileContentMetadata?>(new(Guid.NewGuid(), row.FileVersionId, 123, new string('a', 64)))
            : Task.FromResult<ManagedFileAuditPage?>(new(Guid.NewGuid(), row.FileVersionId, [], 25, 0, false)));
        using var state = new FileMetadataReadState(api); await state.LoadAsync("p");
        if (audit) { await state.ReadAuditAsync(); state.Audit.Should().BeNull(); state.AuditError.Should().NotBeNull(); }
        else { await state.ReadContentMetadataAsync("synthetic"); state.ContentMetadata.Should().BeNull(); state.ContentError.Should().NotBeNull(); }
    }

    [Fact]
    public async Task Inventory_rendering_does_not_prefetch_or_embed_hash_size_or_raw_audit_values()
    {
        var row = File(); var calls = 0;
        var api = DisclosureApi(row, (_, _) => { calls++; throw new InvalidOperationException("Disclosure needs an explicit action."); });
        var html = WebUtility.HtmlDecode(await RenderAsync(api));
        calls.Should().Be(0);
        html.Should().Contain("基本資訊權限不會取得這些值").And.Contain("操作者、用途與拒絕詳細資訊預設遮罩");
        html.Should().NotContain(new string('a', 64)).And.NotContain("raw-actor").And.NotContain("raw-purpose");
    }

    private static IContextHubApiClient DisclosureApi(ManagedFileInventoryResult row, Func<string, object?[], object?> disclosure)
        => Api((method, args) => method switch
        {
            nameof(IContextHubApiClient.GetManagedFilePageAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([row with { ProjectId = (string)args[0]! }], 25, 0, false)),
            nameof(IContextHubApiClient.GetManagedFileMetadataAsync) => Task.FromResult<ManagedFileInventoryResult?>(row with { ProjectId = (string)args[0]! }),
            nameof(IContextHubApiClient.GetManagedFileVersionAsync) => Task.FromResult<ManagedFileInventoryResult?>(row with { ProjectId = (string)args[0]!, FileVersionId = (Guid)args[2]! }),
            nameof(IContextHubApiClient.GetManagedFileVersionsAsync) => Task.FromResult<ManagedFileMetadataPage?>(new([], 25, 0, false)),
            nameof(IContextHubApiClient.GetProjectInformationProjectsAsync) => Task.FromResult<IReadOnlyList<ProjectInformationListItem>>([]),
            nameof(IContextHubApiClient.ReadFileContentMetadataAsync) or nameof(IContextHubApiClient.ReadFileAuditAsync) => disclosure(method, args),
            _ => null
        });

    private sealed class DisclosureHandler(ManagedFileInventoryResult row) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri, string Body)> Requests { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add((request.Method, request.RequestUri!, request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct)));
            return new(HttpStatusCode.OK)
            {
                Content = request.Method == HttpMethod.Post
                ? JsonContent.Create(new ManagedFileContentMetadata(row.FileId, row.FileVersionId, 42, new string('a', 64)))
                : JsonContent.Create(new ManagedFileAuditPage(row.FileId, row.FileVersionId, [], 25, 50, false))
            };
        }
    }

    private static async Task<string> RenderAsync(IContextHubApiClient api)
    {
        var fakeSettings = new FakeInstanceSettingsService(Options.Create(new DashboardOptions()));
        var snapshot = await fakeSettings.GetSnapshotAsync(CancellationToken.None);
        snapshot = snapshot with { Behavior = snapshot.Behavior with { DefaultProjectId = "p" } };
        using var services = new ServiceCollection().AddLogging().AddSingleton(api)
            .AddSingleton<IDashboardRuntimeSettingsAccessor>(new TestRuntimeSettings(snapshot)).BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<Files>(ParameterView.Empty);
            return rendered.ToHtmlString();
        });
    }

    private sealed class TestRuntimeSettings(InstanceSettingsSnapshot snapshot) : IDashboardRuntimeSettingsAccessor
    {
        public Task<InstanceSettingsSnapshot> GetCurrentAsync(CancellationToken cancellationToken) => Task.FromResult(snapshot);
        public Task RefreshAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private static IContextHubApiClient Api(Func<string, object?[], object?> handler)
    {
        var api = DispatchProxy.Create<IContextHubApiClient, FileReadApiProxy>();
        ((FileReadApiProxy)(object)api).Handler = handler;
        return api;
    }

    public class FileReadApiProxy : DispatchProxy
    {
        public Func<string, object?[], object?> Handler { get; set; } = default!;
        public Action<string, object?[]>? Observe { get; set; }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
        {
            Observe?.Invoke(method!.Name, args!);
            return Handler(method!.Name, args!) ?? typeof(Task).GetMethod(nameof(Task.FromResult))!
                .MakeGenericMethod(method.ReturnType.GenericTypeArguments[0]).Invoke(null, [null]);
        }
    }

    private static ManagedFileInventoryResult File(Guid? id = null) => new(
        id ?? Guid.Parse("aaaabbbb-0000-0000-0000-000000000001"), Guid.Parse("ccccdddd-0000-0000-0000-000000000001"),
        "Synthetic.txt", "p", FileAssetState.Active, 2, FileVersionLifecycle.Ready, FileClassification.Normal, 1, false,
        null, 0, null, 0, DateTimeOffset.UtcNow);

    private sealed class MetadataHandler(Func<HttpRequestMessage, HttpContent> content, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public List<(HttpMethod Method, Uri Uri)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.Method, request.RequestUri!));
            return Task.FromResult(new HttpResponseMessage(status) { Content = content(request) });
        }
    }
}

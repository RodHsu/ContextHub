using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Services;
using Memory.Domain;

namespace Memory.DashboardTests;

public sealed class WorkItemEditingClientTests
{
    private static readonly Guid Id = Guid.NewGuid();
    private static WorkItemEditCommand Command() => new("ContextHub", Id, 7, "original-key", WorkItemEditOperation.Update, new(Title: "draft"));
    private static ProjectWorkItemResult Item() => new(Id, "ContextHub", "title", "", [], [], ProjectWorkItemStatus.Pending,
        0, null, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null)
    { Revision = 7 };
    private static ContextHubApiClient Client(Handler handler) => new(new HttpClient(handler) { BaseAddress = new("https://synthetic.invalid") });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Legacy_update_omits_unspecified_dueAt_and_explicit_clear_is_typed(bool clear)
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(Item()) });
        var request = new ProjectWorkItemUpdateRequest(Id, Title: clear ? null : "draft", ExpectedRevision: 7, RequestId: "key",
            Edit: clear ? new(DueAt: new(EditDueAtMode.Clear)) : null);
        await Client(handler).UpdateProjectWorkItemAsync(request, default);
        using var json = JsonDocument.Parse(handler.Body!);
        json.RootElement.TryGetProperty("dueAt", out _).Should().BeFalse();
        if (clear) json.RootElement.GetProperty("edit").GetProperty("dueAt").GetProperty("mode").GetInt32().Should().Be((int)EditDueAtMode.Clear);
        json.RootElement.GetProperty("expectedRevision").GetInt64().Should().Be(7);
    }

    [Fact]
    public async Task Read_response_must_match_the_original_project_and_target()
    {
        var handler = new Handler(_ => new(HttpStatusCode.OK) { Content = JsonContent.Create(Item() with { ProjectId = "Other" }) });
        await FluentActions.Awaiting(() => Client(handler).GetWorkItemForEditAsync("ContextHub", Id, default))
            .Should().ThrowAsync<InvalidDataException>();
        handler.Path.Should().Contain("projectId=ContextHub").And.Contain("includeArchived=true");
    }

    [Theory]
    [InlineData("{\"code\":\"MissingReadVersion\",\"currentRevision\":null}", true)]
    [InlineData("{\"code\":\"StaleReadVersion\",\"currentRevision\":9}", true)]
    [InlineData("{\"code\":\"unrecognized\"}", false)]
    public async Task Known_conflicts_are_definite_but_unknown_payloads_are_not_discarded_as_known_rejections(string body, bool known)
    {
        var handler = new Handler(_ => new(HttpStatusCode.Conflict) { Content = new StringContent(body) });
        var error = await Record.ExceptionAsync(() => Client(handler).ApplyWorkItemEditAsync(Command(), default));
        if (known) error.Should().BeOfType<SafeEditConflictException>();
        else error.Should().BeOfType<InvalidDataException>();
    }

    [Fact]
    public async Task Unsupported_response_after_submission_remains_unknown_in_the_real_draft_controller()
    {
        var handler = new Handler(request => request.Method == HttpMethod.Get
            ? new(HttpStatusCode.OK) { Content = JsonContent.Create(Item()) }
            : throw new NotSupportedException("synthetic encoding failure after dispatch"));
        using var state = new WorkItemEditDraftState(Client(handler), () => "retained-key");
        state.Bind("ContextHub", Id, "subject"); await state.LoadAsync(); state.SetDraft(new(Title: "draft"));
        await state.ApplyAsync();
        state.OutcomeUnknown.Should().BeTrue(); state.CaptureCheckpoint()!.RequestId.Should().Be("retained-key");
        state.HasDraft.Should().BeTrue(); handler.Calls.Should().Be(2);
    }

    [Fact]
    public async Task Recovery_404_is_null_not_an_authority_to_resubmit_and_keeps_the_exact_fingerprint()
    {
        var handler = new Handler(_ => new(HttpStatusCode.NotFound));
        var command = Command(); var request = new WorkItemEditRecoveryRequest(command.ProjectId, Id, command.RequestId, WorkItemEditContract.Hash(command));
        (await Client(handler).RecoverWorkItemEditAsync(request, default)).Should().BeNull();
        handler.Method.Should().Be(HttpMethod.Get); handler.Path.Should().Contain("edit-receipts").And.Contain("requestId=original-key")
            .And.Contain("expectedRequestHash=" + request.ExpectedRequestHash).And.Contain("hashVersion=WorkItemEditIntent%2Fv2");
        handler.Calls.Should().Be(1);
    }

    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public string? Body { get; private set; }
        public string Path { get; private set; } = "";
        public HttpMethod? Method { get; private set; }
        public int Calls { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++; Path = request.RequestUri!.PathAndQuery; Method = request.Method;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return response(request);
        }
    }
}

using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Services;
using Memory.Domain;

namespace Memory.DashboardTests;

public sealed class MemoryEditDraftStateTests
{
    [Fact]
    public async Task Stale_edit_keeps_the_draft_and_requires_explicit_adoption_of_a_fresh_version()
    {
        var original = Document();
        var latest = original with { Version = 5, Content = "changed elsewhere" };
        var api = new EditingApi { Document = original };
        var request = 0;
        using var state = new MemoryEditDraftState(api, () => $"intent-{++request}");
        state.Bind("subject", original);
        await state.OpenAsync();
        state.SetDraft(new(Title: "local draft"));
        api.Apply = _ => throw new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, 5);
        await state.ApplyAsync();

        state.Draft.Title.Should().Be("local draft");
        state.Base!.Version.Should().Be(4);
        state.ServerVersion.Should().Be(5);
        state.CanApply.Should().BeFalse();
        api.Commands.Should().ContainSingle().Which.Should().Be(
            new MemoryEditCommand(original.ProjectId, original.Id, 4, "intent-1", new(Title: "local draft")));
        await state.ReloadForComparisonAsync(); // The actual GET still returned the stale version.
        state.Comparison.Should().BeNull();
        api.Document = latest;
        await state.ReloadForComparisonAsync();
        state.Comparison.Should().Be(latest);
        state.CanApply.Should().BeFalse();
        api.Commands.Should().ContainSingle();

        state.AdoptComparison();
        state.Draft.Title.Should().Be("local draft");
        state.Base.Should().Be(latest);
        state.CanApply.Should().BeTrue();
        api.Apply = command => Task.FromResult(Receipt(command));
        await state.ApplyAsync();
        api.Commands[1].Should().Be(new MemoryEditCommand(original.ProjectId, original.Id, 5, "intent-2", new(Title: "local draft")));
        state.LastReceipt!.Version.Should().Be(6);
        state.Base.Should().BeNull("a receipt does not replace a fresh read of the current document");
        state.HasDraft.Should().BeFalse();
    }

    [Fact]
    public async Task Unknown_result_allows_only_the_exact_read_only_recovery_and_rejects_a_foreign_receipt()
    {
        var original = Document();
        var api = new EditingApi
        {
            Document = original,
            Apply = _ => throw new HttpRequestException("fixture response lost")
        };
        using var state = new MemoryEditDraftState(api, () => "the-original-request");
        state.Bind("subject", original);
        await state.OpenAsync();
        state.SetDraft(new(Content: "local draft"));
        await state.ApplyAsync();
        var originalCommand = api.Commands.Single();
        var expected = Receipt(originalCommand);
        state.OutcomeUnknown.Should().BeTrue();
        state.SetDraft(new(Content: "replacement"));
        state.Draft.Content.Should().Be("local draft");
        await state.ApplyAsync();

        await state.RecoverAsync(); // 404/null is still an unknown outcome.
        api.Recover = _ => Task.FromResult<MemoryEditReceipt?>(expected with { PreviousVersion = 3 });
        await state.RecoverAsync();
        state.OutcomeUnknown.Should().BeTrue();
        state.Draft.Content.Should().Be("local draft");
        api.Recover = _ => Task.FromResult<MemoryEditReceipt?>(expected);
        await state.RecoverAsync();

        api.Commands.Should().ContainSingle();
        api.Recoveries.Should().HaveCount(3).And.OnlyContain(request => request ==
            new MemoryEditRecoveryRequest(original.ProjectId, original.Id, originalCommand.RequestId, expected.RequestHash));
        state.LastReceipt.Should().Be(expected);
        state.OutcomeUnknown.Should().BeFalse();
        state.HasDraft.Should().BeFalse();
    }

    [Fact]
    public async Task Same_intent_keeps_its_request_identity_after_a_definite_rejection()
    {
        var original = Document();
        var api = new EditingApi
        {
            Document = original,
            Apply = _ => throw new HttpRequestException("fixture rejected", null, HttpStatusCode.BadRequest)
        };
        var sequence = 0;
        using var state = new MemoryEditDraftState(api, () => $"intent-{++sequence}");
        state.Bind("subject", original);
        await state.OpenAsync();
        state.SetDraft(new(Title: "original draft", Tags: ["new tag"]));
        await state.ApplyAsync();
        state.SetDraft(new(Title: "original draft", Tags: ["new tag"]));
        await state.ApplyAsync();
        state.SetDraft(new(Title: "changed draft", Tags: ["new tag"]));
        await state.ApplyAsync();

        api.Commands.Select(command => command.RequestId).Should().Equal("intent-1", "intent-1", "intent-2");
        api.Commands.Select(MemoryEditContract.Hash).Take(2).Distinct().Should().ContainSingle();
    }

    [Fact]
    public async Task Changed_selection_rejects_a_late_receipt_and_retains_only_scoped_recovery_metadata()
    {
        var original = Document();
        var other = Document() with { ProjectId = "another-project" };
        var lateResult = new TaskCompletionSource<MemoryEditReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new EditingApi { Document = original, Apply = _ => lateResult.Task };
        using var state = new MemoryEditDraftState(api);
        state.Bind("subject", original);
        await state.OpenAsync();
        state.SetDraft(new(Content: "first selection private draft"));
        var apply = state.ApplyAsync();
        var committed = Receipt(api.Commands.Single());

        state.Bind("subject", other);
        api.Document = other;
        await state.OpenAsync();
        state.SetDraft(new(Title: "other draft"));
        lateResult.SetResult(committed);
        await apply;
        state.Draft.Title.Should().Be("other draft");
        state.LastReceipt.Should().BeNull();
        state.OutcomeUnknown.Should().BeFalse();

        state.Bind("subject", original);
        state.OutcomeUnknown.Should().BeTrue();
        state.HasDraft.Should().BeFalse();
        state.Draft.Content.Should().BeNull();
        api.Recover = _ => Task.FromResult<MemoryEditReceipt?>(committed);
        await state.RecoverAsync();
        api.Recoveries.Should().ContainSingle().Which.ExpectedRequestHash.Should().Be(committed.RequestHash);
        state.LastReceipt.Should().Be(committed);
        state.Bind("different-subject", original);
        state.LastReceipt.Should().BeNull();
        state.OutcomeUnknown.Should().BeFalse();
    }

    [Fact]
    public async Task Read_only_shared_preferences_and_missing_identity_do_not_open_an_edit()
    {
        var document = Document();
        var api = new EditingApi { Document = document };
        using var state = new MemoryEditDraftState(api);
        foreach (var ineligible in new[]
        {
            document with { IsReadOnly = true },
            document with { ProjectId = "shared" },
            document with { MemoryType = MemoryType.Preference },
            document with { Version = 0 }
        })
        {
            state.Bind("subject", ineligible);
            state.IsEditable.Should().BeFalse();
            await state.OpenAsync();
        }
        state.Bind("", document);
        await state.OpenAsync();
        api.ReadCount.Should().Be(0);
    }

    [Fact]
    public async Task Dashboard_http_binding_uses_post_receipt_and_never_accepts_a_read_from_another_project()
    {
        var requested = Document();
        var command = new MemoryEditCommand(requested.ProjectId, requested.Id, 4, "fixture",
            new(Title: "draft"));
        var receipt = Receipt(command);
        var recovery = new MemoryEditRecoveryRequest(command.ProjectId, command.MemoryId, command.RequestId, receipt.RequestHash);
        using var transport = new TestTransport(requested with { ProjectId = "foreign" }, receipt);
        using var http = new HttpClient(transport) { BaseAddress = new Uri("https://fixture.example.test") };
        var client = new ContextHubApiClient(http);

        (await client.GetMemoryForEditAsync(requested.ProjectId, requested.Id, default)).Should().BeNull();
        var conflict = () => client.ApplyMemoryEditAsync(command, default);
        (await conflict.Should().ThrowAsync<SafeEditConflictException>()).Which.CurrentRevision.Should().Be(5);
        (await client.RecoverMemoryEditAsync(recovery, default)).Should().Be(receipt);
        transport.Recovery.Should().Be(recovery);
    }

    private static MemoryDocument Document() => new(Guid.NewGuid(), "fixture", MemoryScope.Project, MemoryType.Fact,
        "original title", "original body", "original summary", "fixture", "fixture", ["tag"], 0.5m, 0.5m, 4,
        MemoryStatus.Active, "{}", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, "ContextHub");

    private static MemoryEditReceipt Receipt(MemoryEditCommand command)
        => new(Guid.NewGuid(), command.ProjectId, command.MemoryId, command.RequestId, MemoryEditContract.Hash(command),
            MemoryEditContract.IntentHashVersion, command.ExpectedVersion, command.ExpectedVersion + 1,
            Guid.NewGuid(), DateTimeOffset.UnixEpoch);

    private sealed class EditingApi : IMemoryEditingApi
    {
        public MemoryDocument? Document { get; set; }
        public int ReadCount { get; private set; }
        public Func<MemoryEditCommand, Task<MemoryEditReceipt>> Apply { get; set; } = command => Task.FromResult(Receipt(command));
        public Func<MemoryEditRecoveryRequest, Task<MemoryEditReceipt?>> Recover { get; set; } = _ => Task.FromResult<MemoryEditReceipt?>(null);
        public List<MemoryEditCommand> Commands { get; } = [];
        public List<MemoryEditRecoveryRequest> Recoveries { get; } = [];

        public Task<MemoryDocument?> GetMemoryForEditAsync(string projectId, Guid memoryId, CancellationToken cancellationToken)
        {
            ReadCount++;
            return Task.FromResult(Document);
        }

        public Task<MemoryEditReceipt> ApplyMemoryEditAsync(MemoryEditCommand command, CancellationToken cancellationToken)
        {
            Commands.Add(command);
            return Apply(command);
        }

        public Task<MemoryEditReceipt?> RecoverMemoryEditAsync(MemoryEditRecoveryRequest request, CancellationToken cancellationToken)
        {
            Recoveries.Add(request);
            return Recover(request);
        }
    }

    private sealed class TestTransport(MemoryDocument document, MemoryEditReceipt receipt) : HttpMessageHandler
    {
        public MemoryEditRecoveryRequest? Recovery { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(document) };
            request.Method.Should().Be(HttpMethod.Post);
            request.RequestUri!.Query.Should().BeEmpty();
            if (request.RequestUri.AbsolutePath.EndsWith("/edits/receipt", StringComparison.Ordinal))
            {
                Recovery = await request.Content!.ReadFromJsonAsync<MemoryEditRecoveryRequest>(cancellationToken);
                return new(HttpStatusCode.OK) { Content = JsonContent.Create(receipt) };
            }
            request.RequestUri.AbsolutePath.Should().EndWith("/edits");
            return new(HttpStatusCode.Conflict)
            {
                Content = JsonContent.Create(new { code = SafeEditConflictCodes.StaleReadVersion, currentVersion = 5 })
            };
        }
    }
}

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Dashboard.Services;
using Memory.Domain;

namespace Memory.DashboardTests;

public sealed class WorkItemEditDraftTests
{
    private static readonly Guid Id = Guid.Parse("6944f2a2-c4d0-4e0c-bd06-8d388723dc72");
    private const string Identity = "synthetic-tenant:synthetic-user";

    [Fact]
    public async Task Missing_read_revision_is_never_replaced_with_baseline_one()
    {
        var requests = 0; var api = new Api { Read = (_, id, _) => Task.FromResult<ProjectWorkItemResult?>(Row(id, revision: null)) };
        using var state = new WorkItemEditDraftState(api, () => (++requests).ToString());
        state.Bind("ContextHub", Id, Identity); await state.LoadAsync(); state.SetDraft(new(Title: "kept"));
        await state.ApplyAsync();
        state.Base!.Revision.Should().BeNull(); state.CanApply.Should().BeFalse();
        state.ConflictCode.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        state.Draft.Title.Should().Be("kept"); api.Commands.Should().BeEmpty(); requests.Should().Be(0);
    }

    [Fact]
    public async Task Exact_pending_identity_and_real_intent_hash_are_captured_before_dispatch()
    {
        WorkItemEditDraftState? state = null;
        var api = new Api();
        api.Apply = (command, _) =>
        {
            var checkpoint = state!.CaptureCheckpoint();
            checkpoint.Should().NotBeNull(); checkpoint!.ExpectedRequestHash.Should().Be(WorkItemEditContract.Hash(command));
            checkpoint.RequestId.Should().Be("request-a"); command.ExpectedRevision.Should().Be(7);
            command.Patch.Title.Should().Be("trimmed"); state.Saving.Should().BeTrue();
            return Task.FromResult(Receipt(command));
        };
        using (state = new WorkItemEditDraftState(api, () => "request-a"))
        {
            state.Bind("ContextHub", Id, Identity); await state.LoadAsync(); state.SetDraft(new(Title: " trimmed "));
            await state.ApplyAsync();
            state.Base!.Revision.Should().Be(8); state.LastReceipt.Should().NotBeNull();
            state.HasDraft.Should().BeFalse(); state.CaptureCheckpoint().Should().BeNull();
            state.OutcomeUnknown.Should().BeFalse(); state.Saving.Should().BeFalse();
        }
    }

    [Fact]
    public async Task Conflict_keeps_draft_and_original_version_until_explicit_comparison_adoption()
    {
        var next = 0; var api = new Api();
        api.Read = (_, id, _) => Task.FromResult<ProjectWorkItemResult?>(Row(id, revision: api.ReadCalls == 1 ? 7 : 12));
        api.Apply = (command, _) => api.Commands.Count == 1
            ? Task.FromException<WorkItemEditReceipt>(new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, 12))
            : Task.FromResult(Receipt(command));
        using var state = new WorkItemEditDraftState(api, () => $"request-{++next}");
        state.Bind("ContextHub", Id, Identity); await state.LoadAsync(); state.SetDraft(new(Description: "keep my draft"));
        await state.ApplyAsync();
        state.Base!.Revision.Should().Be(7); state.ServerRevision.Should().Be(12);
        state.Draft.Description.Should().Be("keep my draft"); state.CaptureCheckpoint().Should().BeNull();
        await state.ApplyAsync(); api.Commands.Should().HaveCount(1);
        await state.ReloadForComparisonAsync();
        state.Comparison!.Revision.Should().Be(12); state.Base.Revision.Should().Be(7);
        state.Draft.Description.Should().Be("keep my draft");
        state.AcceptComparison().Should().BeTrue(); state.Base.Revision.Should().Be(12);
        await state.ApplyAsync();
        api.Commands.Should().HaveCount(2); api.Commands[1].ExpectedRevision.Should().Be(12);
        api.Commands[1].RequestId.Should().NotBe(api.Commands[0].RequestId);
        state.Base.Revision.Should().Be(13); state.HasDraft.Should().BeFalse();
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("cancel")]
    [InlineData("server")]
    public async Task Unknown_outcome_blocks_new_intent_and_null_recovery_is_not_nonexecution(string failure)
    {
        var next = 0; var api = new Api
        {
            Apply = (_, _) => Task.FromException<WorkItemEditReceipt>(failure switch
            {
                "cancel" => new OperationCanceledException(),
                "server" => new HttpRequestException("synthetic-private", null, HttpStatusCode.InternalServerError),
                _ => new HttpRequestException("synthetic-private")
            })
        };
        using var state = new WorkItemEditDraftState(api, () => $"request-{++next}");
        state.Bind("ContextHub", Id, Identity); await state.LoadAsync(); state.SetDraft(new(Title: "pending draft"));
        await state.ApplyAsync();
        var checkpoint = state.CaptureCheckpoint(); checkpoint.Should().NotBeNull();
        state.OutcomeUnknown.Should().BeTrue(); state.Draft.Title.Should().Be("pending draft");
        FluentActions.Invoking(() => state.SetDraft(new(Title: "different"))).Should().Throw<InvalidOperationException>();
        await state.ApplyAsync(); api.Commands.Should().ContainSingle(); next.Should().Be(1);
        await state.RecoverAsync();
        state.OutcomeUnknown.Should().BeTrue(); state.CaptureCheckpoint().Should().Be(checkpoint);
        api.Recoveries.Should().ContainSingle().Which.ExpectedRequestHash.Should().Be(checkpoint!.ExpectedRequestHash);
        api.Recover = (_, _) => Task.FromResult<WorkItemEditReceipt?>(Receipt(api.Commands[0]));
        await state.RecoverAsync();
        state.OutcomeUnknown.Should().BeFalse(); state.Base!.Revision.Should().Be(8); state.HasDraft.Should().BeFalse();
        api.Commands.Should().ContainSingle(); next.Should().Be(1);
    }

    [Theory]
    [InlineData("hash")]
    [InlineData("hash-version")]
    [InlineData("request")]
    [InlineData("target")]
    [InlineData("project")]
    [InlineData("result-target")]
    [InlineData("result-revision")]
    [InlineData("operation")]
    [InlineData("original-revision")]
    [InlineData("revision-step")]
    [InlineData("history")]
    public async Task A_mismatched_success_receipt_is_unknown_and_never_publishes_its_data(string mismatch)
    {
        var api = new Api();
        api.Apply = (command, _) => Task.FromResult(Corrupt(Receipt(command), mismatch));
        using var state = await Loaded(api);
        state.SetDraft(new(Title: "private-kept")); await state.ApplyAsync();
        state.OutcomeUnknown.Should().BeTrue(); state.LastReceipt.Should().BeNull(); state.Base!.Revision.Should().Be(7);
        state.Draft.Title.Should().Be("private-kept"); state.CaptureCheckpoint().Should().NotBeNull();
        api.Recover = (_, _) => Task.FromResult<WorkItemEditReceipt?>(Corrupt(Receipt(api.Commands[0]), mismatch));
        await state.RecoverAsync();
        state.OutcomeUnknown.Should().BeTrue(); state.LastReceipt.Should().BeNull();
        state.Base.Revision.Should().Be(7); api.Commands.Should().ContainSingle();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scope_change_rejects_late_write_success_or_error_and_retains_only_old_recovery_metadata(bool error)
    {
        var gate = new TaskCompletionSource<WorkItemEditReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        var api = new Api { Apply = (_, ct) => { token = ct; return gate.Task; } };
        using var state = await Loaded(api); state.SetDraft(new(Title: "old private"));
        var saving = state.ApplyAsync(); var checkpoint = state.CaptureCheckpoint();
        var other = Guid.NewGuid(); state.Bind("Other", other, "other-identity"); token.IsCancellationRequested.Should().BeTrue();
        await state.LoadAsync();
        if (error) gate.SetException(new HttpRequestException("old-private-error")); else gate.SetResult(Receipt(api.Commands[0]));
        await saving;
        state.Base!.Id.Should().Be(other); state.Base.ProjectId.Should().Be("Other");
        state.HasDraft.Should().BeFalse(); state.LastReceipt.Should().BeNull(); state.Error.Should().BeNull();
        state.CaptureCheckpoint().Should().BeNull();
        state.Bind("ContextHub", Id, Identity);
        state.OutcomeUnknown.Should().BeTrue(); state.CaptureCheckpoint().Should().Be(checkpoint);
        state.Base.Should().BeNull(); state.Draft.Title.Should().BeNull();
    }

    [Fact]
    public async Task Checkpoint_roundtrip_contains_no_draft_and_restores_only_exact_current_identity_and_scope()
    {
        var api = new Api { Apply = (_, _) => Task.FromException<WorkItemEditReceipt>(new HttpRequestException()) };
        using var first = await Loaded(api); first.SetDraft(new(Title: "do-not-checkpoint-text", Description: "private draft"));
        await first.ApplyAsync(); var checkpoint = first.CaptureCheckpoint()!;
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var serialized = JsonSerializer.Serialize(checkpoint, options);
        serialized.Should().NotContain("do-not-checkpoint-text").And.NotContain("private draft").And.NotContain("patch");
        var restored = JsonSerializer.Deserialize<WorkItemEditRecoveryCheckpoint>(serialized, options)!;
        using var second = new WorkItemEditDraftState(api);
        second.Bind("ContextHub", Id, "different-identity"); second.RestoreCheckpoint(restored).Should().BeFalse();
        second.Bind("ContextHub", Id, Identity);
        second.RestoreCheckpoint(restored with { ExpectedRequestHash = "bad" }).Should().BeFalse();
        second.RestoreCheckpoint(restored with { HashVersion = "unknown" }).Should().BeFalse();
        second.RestoreCheckpoint(restored with { ExpectedRequestHash = restored.ExpectedRequestHash.ToUpperInvariant() }).Should().BeTrue();
        second.HasDraft.Should().BeFalse(); second.OutcomeUnknown.Should().BeTrue();
        await second.ApplyAsync(); api.Commands.Should().ContainSingle();
        api.Recover = (_, _) => Task.FromResult<WorkItemEditReceipt?>(Receipt(api.Commands[0]));
        await second.RecoverAsync(); second.OutcomeUnknown.Should().BeFalse(); second.LastReceipt.Should().NotBeNull();
    }

    [Fact]
    public async Task Pending_edit_allows_no_second_dispatch_and_dispose_ignores_late_result()
    {
        var gate = new TaskCompletionSource<WorkItemEditReceipt>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default; var ids = 0;
        var api = new Api { Apply = (_, ct) => { token = ct; return gate.Task; } };
        var state = new WorkItemEditDraftState(api, () => $"request-{++ids}");
        state.Bind("ContextHub", Id, Identity); await state.LoadAsync(); state.SetDraft(new(Title: "pending"));
        var first = state.ApplyAsync(); var checkpoint = state.CaptureCheckpoint();
        await state.ApplyAsync(); await state.RecoverAsync();
        api.Commands.Should().ContainSingle(); api.Recoveries.Should().BeEmpty(); ids.Should().Be(1);
        checkpoint.Should().NotBeNull(); state.Dispose(); token.IsCancellationRequested.Should().BeTrue();
        gate.SetResult(Receipt(api.Commands[0])); await first;
        state.Base.Should().BeNull(); state.LastReceipt.Should().BeNull(); state.CaptureCheckpoint().Should().BeNull();
        await state.ApplyAsync(); api.Commands.Should().ContainSingle();
    }

    [Fact]
    public async Task Recovery_cancels_an_older_read_so_it_cannot_replace_the_recovered_snapshot()
    {
        var api = new Api { Apply = (_, _) => Task.FromException<WorkItemEditReceipt>(new HttpRequestException()) };
        using var first = await Loaded(api); first.SetDraft(new(Title: "pending")); await first.ApplyAsync();
        var checkpoint = first.CaptureCheckpoint()!;
        var gate = new TaskCompletionSource<ProjectWorkItemResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken token = default;
        api.Read = (_, _, ct) => { token = ct; return gate.Task; };
        api.Recover = (_, _) => Task.FromResult<WorkItemEditReceipt?>(Receipt(api.Commands[0]));
        using var restored = new WorkItemEditDraftState(api); restored.Bind("ContextHub", Id, Identity);
        restored.RestoreCheckpoint(checkpoint).Should().BeTrue(); var read = restored.LoadAsync();
        await restored.RecoverAsync(); token.IsCancellationRequested.Should().BeTrue();
        restored.Base!.Revision.Should().Be(8);
        gate.SetResult(Row(Id, revision: 7)); await read;
        restored.Base.Revision.Should().Be(8); restored.OutcomeUnknown.Should().BeFalse(); restored.Loading.Should().BeFalse();
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.UnprocessableEntity)]
    public async Task Definitive_request_rejections_keep_draft_without_claiming_unknown_commit(HttpStatusCode code)
    {
        var api = new Api { Apply = (_, _) => Task.FromException<WorkItemEditReceipt>(new HttpRequestException("private", null, code)) };
        using var state = await Loaded(api); state.SetDraft(new(Title: "kept")); await state.ApplyAsync();
        state.HasDraft.Should().BeTrue(); state.OutcomeUnknown.Should().BeFalse(); state.LastReceipt.Should().BeNull();
        state.CaptureCheckpoint().Should().BeNull(); state.Error.Should().NotContain("private");
    }

    [Fact]
    public async Task A_nonupdate_operation_preserves_an_unsent_ordinary_draft()
    {
        var api = new Api { Apply = (command, _) => Task.FromResult(Receipt(command)) };
        using var state = await Loaded(api); state.SetDraft(new(Title: "not sent by reopen"));
        await state.ApplyAsync(WorkItemEditOperation.Reopen);
        api.Commands.Should().ContainSingle().Which.Patch.Title.Should().BeNull();
        state.HasDraft.Should().BeTrue(); state.Draft.Title.Should().Be("not sent by reopen"); state.Base!.Revision.Should().Be(8);
    }

    [Fact]
    public async Task Default_unsupported_write_surface_keeps_draft_without_claiming_a_commit()
    {
        using var state = new WorkItemEditDraftState(new ReadOnlyApi());
        state.Bind("ContextHub", Id, Identity); await state.LoadAsync(); state.SetDraft(new(Title: "kept"));
        await state.ApplyAsync();
        state.HasDraft.Should().BeTrue(); state.OutcomeUnknown.Should().BeFalse(); state.LastReceipt.Should().BeNull();
        state.CaptureCheckpoint().Should().BeNull(); state.Error.Should().NotBeNull();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_reads_cannot_overwrite_new_scope_or_its_loading_state(bool error)
    {
        var gate = new TaskCompletionSource<ProjectWorkItemResult?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var api = new Api { Read = (project, id, _) => project == "old" ? gate.Task : Task.FromResult<ProjectWorkItemResult?>(Row(id, project)) };
        using var state = new WorkItemEditDraftState(api); state.Bind("old", Id, Identity); var first = state.LoadAsync();
        var other = Guid.NewGuid(); state.Bind("new", other, Identity); await state.LoadAsync();
        if (error) gate.SetException(new HttpRequestException("private")); else gate.SetResult(Row(Id, "old"));
        await first; state.Base!.Id.Should().Be(other); state.Base.ProjectId.Should().Be("new");
        state.Loading.Should().BeFalse(); state.Error.Should().BeNull();
    }

    [Fact]
    public async Task Draft_copies_caller_lists_and_never_silently_changes_pending_payload()
    {
        var tags = new List<string> { "one" };
        var api = new Api(); using var state = await Loaded(api);
        state.SetDraft(new(Tags: tags)); tags.Add("outside"); state.Draft.Tags.Should().Equal("one");
        FluentActions.Invoking(() => ((IList<string>)state.Draft.Tags!)[0] = "mutated").Should().Throw<NotSupportedException>();
    }

    private static async Task<WorkItemEditDraftState> Loaded(Api api)
    {
        var state = new WorkItemEditDraftState(api, () => "request-a");
        state.Bind("ContextHub", Id, Identity); await state.LoadAsync(); return state;
    }

    private static ProjectWorkItemResult Row(Guid id, string project = "ContextHub", long? revision = 7)
        => new(id, project, "synthetic", "", [], [], ProjectWorkItemStatus.InProgress, 1, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null)
        { Revision = revision };

    private static WorkItemEditReceipt Receipt(WorkItemEditCommand command)
    {
        var normalized = WorkItemEditContract.Normalize(command); var previous = normalized.ExpectedRevision!.Value;
        return new(Guid.NewGuid(), Guid.NewGuid(), normalized.ProjectId, normalized.WorkItemId, normalized.RequestId,
            WorkItemEditContract.Hash(normalized), WorkItemEditContract.IntentHashVersion, normalized.Operation,
            previous, previous + 1, true, DateTimeOffset.UnixEpoch, Row(normalized.WorkItemId, normalized.ProjectId, previous + 1));
    }

    private static WorkItemEditReceipt Corrupt(WorkItemEditReceipt receipt, string field) => field switch
    {
        "hash" => receipt with { RequestHash = new string('0', 64) },
        "hash-version" => receipt with { HashVersion = "unknown" },
        "request" => receipt with { RequestId = "different" },
        "target" => receipt with { WorkItemId = Guid.NewGuid() },
        "project" => receipt with { ProjectId = "Other" },
        "result-target" => receipt with { Result = receipt.Result with { Id = Guid.NewGuid() } },
        "result-revision" => receipt with { Result = receipt.Result with { Revision = 100 } },
        "operation" => receipt with { Operation = WorkItemEditOperation.Archive },
        "original-revision" => receipt with { PreviousRevision = 1, Revision = 2, Result = receipt.Result with { Revision = 2 } },
        "revision-step" => receipt with { Revision = 10, Result = receipt.Result with { Revision = 10 } },
        _ => receipt with { HistoryId = null }
    };

    private sealed class Api : IWorkItemEditingApi
    {
        public int ReadCalls { get; private set; }
        public List<WorkItemEditCommand> Commands { get; } = [];
        public List<WorkItemEditRecoveryRequest> Recoveries { get; } = [];
        public Func<string, Guid, CancellationToken, Task<ProjectWorkItemResult?>> Read { get; set; }
            = (project, id, _) => Task.FromResult<ProjectWorkItemResult?>(Row(id, project));
        public Func<WorkItemEditCommand, CancellationToken, Task<WorkItemEditReceipt>> Apply { get; set; }
            = (_, _) => Task.FromException<WorkItemEditReceipt>(new NotSupportedException());
        public Func<WorkItemEditRecoveryRequest, CancellationToken, Task<WorkItemEditReceipt?>> Recover { get; set; }
            = (_, _) => Task.FromResult<WorkItemEditReceipt?>(null);
        public Task<ProjectWorkItemResult?> GetWorkItemForEditAsync(string project, Guid id, CancellationToken ct)
        { ReadCalls++; return Read(project, id, ct); }
        public Task<WorkItemEditReceipt> ApplyWorkItemEditAsync(WorkItemEditCommand command, CancellationToken ct)
        { Commands.Add(command); return Apply(command, ct); }
        public Task<WorkItemEditReceipt?> RecoverWorkItemEditAsync(WorkItemEditRecoveryRequest request, CancellationToken ct)
        { Recoveries.Add(request); return Recover(request, ct); }
    }

    private sealed class ReadOnlyApi : IWorkItemEditingApi
    {
        public Task<ProjectWorkItemResult?> GetWorkItemForEditAsync(string projectId, Guid workItemId, CancellationToken ct)
            => Task.FromResult<ProjectWorkItemResult?>(Row(workItemId, projectId));
    }
}

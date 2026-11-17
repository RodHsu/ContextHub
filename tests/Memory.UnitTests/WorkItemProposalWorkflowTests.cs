using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Microsoft.Extensions.Logging.Abstractions;

namespace Memory.UnitTests;

public sealed class WorkItemProposalWorkflowTests
{
    private static readonly Guid Tenant = Guid.NewGuid(), ActorId = Guid.NewGuid(), ProposalId = Guid.NewGuid(), ItemId = Guid.NewGuid();
    private static ContextHubRequestActor Actor() => new(Tenant, ActorId, "synthetic", TenantUserRole.Member,
        [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], ["ContextHub"], true);
    private static WorkItemProposalPreparation Prepared(WorkItemProposalPreparationMode mode)
    {
        var command = WorkItemProposalContract.Command("ContextHub", new(ItemId, ExpectedRevision: 7, RequestId: "persisted-proposal-key"));
        return new(ProposalId, Tenant, ActorId, command, WorkItemEditContract.Hash(command), mode);
    }
    private static ChatGptProposalResult Result() => new(ProposalId, WorkItemProposalContract.ToolName, ChatGptProposalStatus.Applied,
        "ContextHub", "ContextHub", "synthetic", "", "{}", "", "", "", ItemId, "", DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);
    private static WorkItemEditReceipt Receipt(WorkItemProposalPreparation original)
    {
        var item = new ProjectWorkItem { Id = ItemId, ProjectId = "ContextHub", Revision = 7 };
        WorkItemEditAggregate.Apply(item, original.Command, DateTimeOffset.UnixEpoch);
        return new(Guid.NewGuid(), Guid.NewGuid(), "ContextHub", ItemId, original.Command.RequestId, original.RequestHash,
            WorkItemEditContract.IntentHashVersion, original.Command.Operation, 7, 8, true, DateTimeOffset.UnixEpoch, WorkItemEditProjection.ToResult(item));
    }

    [Fact]
    public async Task First_dispatch_passes_the_persisted_intent_and_only_finalizes_its_bound_receipt()
    {
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Dispatch)); var edit = new Editing { Result = Receipt(store.Original) };
        (await Service(store, edit).ApproveAsync(new(ProposalId, "approval note"), default)).Status.Should().Be(ChatGptProposalStatus.Applied);
        edit.ApplyCalls.Should().Be(1); edit.Command.Should().Be(store.Original.Command); edit.ReadCalls.Should().Be(0);
        store.Finalized.Should().BeSameAs(edit.Result); store.Note.Should().Be("approval note");
    }

    [Fact]
    public async Task Recovery_absence_stays_unknown_and_cannot_dispatch_or_finalize()
    {
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Recover)); var edit = new Editing();
        await FluentActions.Awaiting(() => Service(store, edit).ApproveAsync(new(ProposalId), default))
            .Should().ThrowAsync<WorkItemProposalOutcomeUnknownException>();
        edit.ApplyCalls.Should().Be(0); edit.ReadCalls.Should().Be(1); store.Finalized.Should().BeNull();
        edit.Recovery.Should().Be(new WorkItemEditRecoveryRequest("ContextHub", ItemId, store.Original.Command.RequestId, store.Original.RequestHash));
    }

    [Fact]
    public async Task Failed_finalize_recovers_the_original_receipt_on_next_approval_without_another_apply()
    {
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Dispatch)) { FinalizeFailure = new IOException("synthetic save failure") };
        var edit = new Editing { Result = Receipt(store.Original) }; var workflow = Service(store, edit);
        await FluentActions.Awaiting(() => workflow.ApproveAsync(new(ProposalId), default)).Should().ThrowAsync<WorkItemProposalOutcomeUnknownException>();
        store.Original = store.Original with { Mode = WorkItemProposalPreparationMode.Recover }; store.FinalizeFailure = null;
        (await workflow.ApproveAsync(new(ProposalId), default)).Status.Should().Be(ChatGptProposalStatus.Applied);
        edit.ApplyCalls.Should().Be(1); edit.ReadCalls.Should().Be(1); store.Finalized.Should().BeSameAs(edit.Result);
        // This is actual orchestration with a store boundary fake, not persisted crash/transaction proof.
    }

    [Fact]
    public async Task Only_a_definite_core_conflict_is_recorded_and_returned_as_a_conflict()
    {
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Dispatch));
        var edit = new Editing { ApplyFailure = new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, 9) };
        var error = await FluentActions.Awaiting(() => Service(store, edit).ApproveAsync(new(ProposalId), default)).Should().ThrowAsync<SafeEditConflictException>();
        error.Which.Code.Should().Be(SafeEditConflictCodes.StaleReadVersion); store.Conflict.Should().Be(SafeEditConflictCodes.StaleReadVersion);
        edit.ApplyCalls.Should().Be(1); store.Finalized.Should().BeNull();
        store.Original = store.Original with { Mode = WorkItemProposalPreparationMode.Conflict, ConflictCode = store.Conflict };
        await FluentActions.Awaiting(() => Service(store, edit).ApproveAsync(new(ProposalId), default)).Should().ThrowAsync<SafeEditConflictException>();
        edit.ApplyCalls.Should().Be(1); edit.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task A_finalize_conflict_after_the_core_returns_cannot_be_reported_as_a_definite_rejected_edit()
    {
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Dispatch)) { FinalizeFailure = new SafeEditConflictException(SafeEditConflictCodes.RequestIdentityMismatch) };
        var edit = new Editing { Result = Receipt(store.Original) };
        await FluentActions.Awaiting(() => Service(store, edit).ApproveAsync(new(ProposalId), default)).Should().ThrowAsync<WorkItemProposalOutcomeUnknownException>();
        edit.ApplyCalls.Should().Be(1); store.Conflict.Should().BeNull();
    }

    [Fact]
    public async Task A_mismatched_recovery_receipt_never_reaches_finalize_or_generates_another_request()
    {
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Recover));
        var edit = new Editing { Result = Receipt(store.Original) with { RequestHash = new string('a', 64) } };
        await FluentActions.Awaiting(() => Service(store, edit).ApproveAsync(new(ProposalId), default)).Should().ThrowAsync<WorkItemProposalOutcomeUnknownException>();
        edit.ApplyCalls.Should().Be(0); store.Finalized.Should().BeNull();
    }

    [Fact]
    public async Task Actor_change_after_prepare_stops_dispatch_without_revealing_the_result()
    {
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Dispatch)); var edit = new Editing();
        var actors = new RequestActorAccessor { Current = Actor() with { UserId = Guid.NewGuid() } };
        var workflow = new WorkItemProposalWorkflow(store, edit, actors, NullLogger<WorkItemProposalWorkflow>.Instance);
        await FluentActions.Awaiting(() => workflow.ApproveAsync(new(ProposalId), default)).Should().ThrowAsync<WorkItemProposalOutcomeUnknownException>();
        edit.ApplyCalls.Should().Be(0); edit.ReadCalls.Should().Be(0);
    }

    [Fact]
    public async Task Caller_cancellation_does_not_spawn_uncancellable_tail_work_or_replay()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Dispatch)); var edit = new Editing
        { OnApply = cancellation.Cancel, ApplyFailure = new OperationCanceledException(cancellation.Token) };
        await FluentActions.Awaiting(() => Service(store, edit).ApproveAsync(new(ProposalId), cancellation.Token)).Should().ThrowAsync<OperationCanceledException>();
        edit.Token.Should().Be(cancellation.Token); edit.ApplyCalls.Should().Be(1); store.Finalized.Should().BeNull();
    }

    [Fact]
    public async Task Recovery_does_not_borrow_write_scope_as_read_authority()
    {
        var store = new Store(Prepared(WorkItemProposalPreparationMode.Recover)); var edit = new Editing { Result = Receipt(store.Original) };
        var workflow = new WorkItemProposalWorkflow(store, edit, new RequestActorAccessor { Current = Actor() with { Scopes = [SecurityScopes.MemoryWrite] } }, NullLogger<WorkItemProposalWorkflow>.Instance);
        await FluentActions.Awaiting(() => workflow.ApproveAsync(new(ProposalId), default)).Should().ThrowAsync<WorkItemProposalOutcomeUnknownException>();
        edit.ReadCalls.Should().Be(0); edit.ApplyCalls.Should().Be(0); store.Finalized.Should().BeNull();
    }

    private static WorkItemProposalWorkflow Service(Store store, Editing edit) => new(store, edit,
        new RequestActorAccessor { Current = Actor() }, NullLogger<WorkItemProposalWorkflow>.Instance);
    private sealed class Store(WorkItemProposalPreparation original) : IWorkItemProposalStore
    {
        public WorkItemProposalPreparation Original { get; set; } = original;
        public Exception? FinalizeFailure { get; set; }
        public WorkItemEditReceipt? Finalized { get; private set; }
        public string? Note { get; private set; }
        public string? Conflict { get; private set; }
        public Task<WorkItemProposalPreparation> PrepareAsync(Guid id, CancellationToken ct) => Task.FromResult(Original);
        public Task<ChatGptProposalResult> FinalizeAsync(WorkItemProposalPreparation value, WorkItemEditReceipt receipt, string note, CancellationToken ct)
        { if (FinalizeFailure is not null) return Task.FromException<ChatGptProposalResult>(FinalizeFailure); Finalized = receipt; Note = note; return Task.FromResult(Result()); }
        public Task RecordConflictAsync(WorkItemProposalPreparation value, string code, CancellationToken ct) { Conflict = code; return Task.CompletedTask; }
        public Task<ChatGptProposalResult> RejectAsync(ChatGptProposalDecisionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
    private sealed class Editing : IWorkItemEditingService
    {
        public WorkItemEditReceipt? Result { get; init; }
        public Exception? ApplyFailure { get; init; }
        public Action? OnApply { get; init; }
        public int ApplyCalls { get; private set; }
        public int ReadCalls { get; private set; }
        public WorkItemEditCommand? Command { get; private set; }
        public WorkItemEditRecoveryRequest? Recovery { get; private set; }
        public CancellationToken Token { get; private set; }
        public Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditCommand command, CancellationToken ct)
        { ApplyCalls++; Command = command; Token = ct; OnApply?.Invoke(); return ApplyFailure is null ? Task.FromResult(Result!) : Task.FromException<WorkItemEditReceipt>(ApplyFailure); }
        public Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditRecoveryRequest request, CancellationToken ct)
        { ReadCalls++; Recovery = request; return Task.FromResult(Result); }
    }
}

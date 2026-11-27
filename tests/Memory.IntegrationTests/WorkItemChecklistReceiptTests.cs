using FluentAssertions;
using Memory.Application;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace Memory.IntegrationTests;

public sealed class WorkItemChecklistReceiptTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Checklist_replay_commits_once_and_original_receipt_requires_current_project_read_access()
    {
        var services = environment.GetFactory().Services;
        using var verification = services.CreateScope();
        var db = verification.ServiceProvider.GetRequiredService<MemoryDbContext>();
        var owner = await db.TenantUsers.AsNoTracking().SingleAsync(user => user.Username == "contract-test-admin");
        var projectId = $"checklist-receipt-{Guid.NewGuid():N}";
        var actor = new ContextHubRequestActor(owner.TenantId, owner.Id, owner.Username, owner.Role,
            [SecurityScopes.MemoryRead, SecurityScopes.MemoryWrite], [projectId], IsAuthenticated: true);
        var item = await AsActor(provider => provider.GetRequiredService<IProjectWorkItemService>().CreateAsync(
            new ProjectWorkItemCreateRequest(projectId, "Receipt fixture", ChecklistItems: ["First check", "Second check"]),
            CancellationToken.None));
        var firstLeaf = item.ChecklistItems[0].Id;
        var secondLeaf = item.ChecklistItems[1].Id;
        var baseline = item.Revision!.Value;
        var request = new ProjectWorkItemChecklistUpdateRequest(item.Id, firstLeaf, true, baseline, Guid.NewGuid().ToString("N"));
        var expectedHash = WorkItemEditContract.Hash(new WorkItemEditCommand(projectId, item.Id, baseline, request.RequestId!,
            WorkItemEditOperation.ChecklistComplete, new WorkItemEditPatch(ChecklistItemId: firstLeaf, IsCompleted: true)));

        var first = await Complete(request);
        first.EditReceipt.Should().NotBeNull();
        var receipt = first.EditReceipt!;
        receipt.Changed.Should().BeTrue();
        receipt.Replayed.Should().BeFalse();
        receipt.HistoryId.Should().NotBeNull();
        receipt.RequestId.Should().Be(request.RequestId);
        receipt.RequestHash.Should().Be(expectedHash);
        receipt.HashVersion.Should().Be(WorkItemEditContract.IntentHashVersion);
        receipt.Operation.Should().Be(WorkItemEditOperation.ChecklistComplete);
        receipt.PreviousRevision.Should().Be(baseline);
        receipt.Revision.Should().Be(baseline + 1);
        first.Revision.Should().Be(receipt.Revision);
        first.ChecklistItems.Select(row => row.Id).Should().Equal(firstLeaf, secondLeaf);
        first.ChecklistItems.Select(row => row.IsCompleted).Should().Equal(true, false);

        var replay = await Complete(request);
        replay.Should().BeEquivalentTo(first with { EditReceipt = receipt with { Replayed = true } });

        var conflict = await FluentActions.Awaiting(() => Complete(request with { ChecklistItemId = secondLeaf }))
            .Should().ThrowAsync<SafeEditConflictException>();
        conflict.Which.Code.Should().Be(SafeEditConflictCodes.RequestIdentityMismatch);

        var laterRequest = request with
        {
            ChecklistItemId = secondLeaf,
            ExpectedRevision = receipt.Revision,
            RequestId = Guid.NewGuid().ToString("N")
        };
        var latest = await Complete(laterRequest);
        latest.Revision.Should().Be(baseline + 2);
        latest.ChecklistItems.Select(row => row.Id).Should().Equal(firstLeaf, secondLeaf);
        latest.ChecklistItems.Select(row => row.IsCompleted).Should().Equal(true, true);

        var recovery = new WorkItemEditRecoveryRequest(projectId, item.Id, receipt.RequestId, expectedHash, receipt.HashVersion);
        var recovered = await Recover(recovery);
        recovered.Should().NotBeNull();
        WorkItemEditReceiptMetadata.From(recovered!).Should().BeEquivalentTo(receipt);
        recovered!.Result.Should().BeEquivalentTo(first with { EditReceipt = null });

        var wrongHash = new string(expectedHash[0] == 'a' ? 'b' : 'a', 64);
        var mismatchedRecovery = await FluentActions.Awaiting(() => Recover(recovery with { ExpectedRequestHash = wrongHash }))
            .Should().ThrowAsync<SafeEditConflictException>();
        mismatchedRecovery.Which.Code.Should().Be(SafeEditConflictCodes.RequestIdentityMismatch);

        var narrowedActor = actor with { AllowedProjectIds = [$"{projectId}-other"] };
        await FluentActions.Awaiting(() => Recover(recovery, narrowedActor)).Should().ThrowAsync<UnauthorizedAccessException>();

        var receiptRows = await db.WorkItemEditReceipts.AsNoTracking().Where(row => row.WorkItemId == item.Id).ToArrayAsync();
        receiptRows.Select(row => row.RequestId).Should().BeEquivalentTo(new[] { request.RequestId, laterRequest.RequestId });
        var receiptRow = receiptRows.Should().ContainSingle(row => row.RequestId == request.RequestId).Which;
        receiptRow.Id.Should().Be(receipt.ReceiptId);
        receiptRow.RequestHash.Should().Be(expectedHash);

        var history = await db.WorkItemEditHistories.AsNoTracking().Where(row => row.WorkItemId == item.Id)
            .OrderBy(row => row.Revision).ToArrayAsync();
        history.Select(row => row.Revision).Should().Equal(baseline + 1, baseline + 2);
        history.Select(row => row.RequestId).Should().Equal(request.RequestId, laterRequest.RequestId);
        history[0].Id.Should().Be(receipt.HistoryId!.Value);
        history[0].PreviousRevision.Should().Be(baseline);
        history[0].Operation.Should().Be(nameof(WorkItemEditOperation.ChecklistComplete));

        var outboxRevisions = await db.AuthorityOutboxEvents.AsNoTracking()
            .Where(row => row.Category == "WorkItems" && row.AggregateId == item.Id.ToString("D")
                && row.EventType == nameof(WorkItemEditOperation.ChecklistComplete))
            .OrderBy(row => row.AuthorityRevision).Select(row => row.AuthorityRevision).ToArrayAsync();
        outboxRevisions.Should().Equal(baseline + 1, baseline + 2);
        (await db.ProjectWorkItems.AsNoTracking().SingleAsync(row => row.Id == item.Id)).Revision.Should().Be(baseline + 2);
        (await db.ProjectWorkItemChecklistItems.AsNoTracking().Where(row => row.WorkItemId == item.Id)
            .OrderBy(row => row.SortOrder).Select(row => row.Id).ToArrayAsync()).Should().Equal(firstLeaf, secondLeaf);

        Task<ProjectWorkItemResult> Complete(ProjectWorkItemChecklistUpdateRequest update)
            => AsActor(provider => provider.GetRequiredService<IProjectWorkItemService>()
                .SetChecklistItemCompletionAsync(update, CancellationToken.None));

        Task<WorkItemEditReceipt?> Recover(WorkItemEditRecoveryRequest lookup, ContextHubRequestActor? current = null)
            => AsActor(provider => provider.GetRequiredService<IWorkItemEditingService>().ReadReceiptAsync(lookup, CancellationToken.None), current);

        async Task<T> AsActor<T>(Func<IServiceProvider, Task<T>> action, ContextHubRequestActor? current = null)
        {
            using var scope = services.CreateScope();
            scope.ServiceProvider.GetRequiredService<IRequestActorAccessor>().Current = current ?? actor;
            return await action(scope.ServiceProvider);
        }
    }
}

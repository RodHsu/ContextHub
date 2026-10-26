using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class WorkItemEditAggregateTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(10);

    [Theory]
    [InlineData(null, SafeEditConflictCodes.MissingReadVersion)]
    [InlineData(0L, SafeEditConflictCodes.MissingReadVersion)]
    [InlineData(-1L, SafeEditConflictCodes.MissingReadVersion)]
    [InlineData(6L, SafeEditConflictCodes.StaleReadVersion)]
    public void Missing_or_stale_original_revision_does_not_mutate_the_aggregate(long? expected, string code)
    {
        var item = Item(); var before = Snapshot(item);
        var command = Command(item, WorkItemEditOperation.Update, new(Title: "changed")) with { ExpectedRevision = expected };
        var error = FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, command, Now)).Should().Throw<SafeEditConflictException>().Which;
        error.Code.Should().Be(code);
        Snapshot(item).Should().Be(before);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_command_for_a_different_work_item_or_project_does_not_mutate(bool otherProject)
    {
        var item = Item(); var before = Snapshot(item);
        var command = Command(item, WorkItemEditOperation.Update, new(Title: "changed"));
        command = otherProject ? command with { ProjectId = "Other" } : command with { WorkItemId = Guid.NewGuid() };
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, command, Now)).Should().Throw<InvalidOperationException>();
        Snapshot(item).Should().Be(before);
    }

    [Fact]
    public void A_late_validation_error_does_not_leave_earlier_fields_changed()
    {
        var item = Item(); item.DefinitionState = ProjectWorkItemDefinitionState.Superseded; var before = Snapshot(item);
        var command = Command(item, WorkItemEditOperation.Update, new(Title: "changed", Description: "changed",
            DefinitionState: ProjectWorkItemDefinitionState.Draft));
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, command, Now)).Should().Throw<InvalidOperationException>();
        Snapshot(item).Should().Be(before);
    }

    [Fact]
    public void Revision_overflow_is_detected_before_any_caller_owned_state_changes()
    {
        var item = Item(); item.Revision = long.MaxValue; var before = Snapshot(item);
        var command = Command(item, WorkItemEditOperation.Update, new(Title: "changed"));
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, command, Now)).Should().Throw<OverflowException>();
        Snapshot(item).Should().Be(before);
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.Update, new()), Now).Should().BeFalse();
        Snapshot(item).Should().Be(before);
    }

    [Fact]
    public void Multiple_changed_fields_advance_one_revision_and_preserve_identity_and_unrelated_metadata()
    {
        var item = Item(); var rows = item.ChecklistItems.ToArray();
        var originalCreated = item.CreatedAt; var originalTenant = item.TenantId; var originalOwner = item.OwnerUserId;
        var command = Command(item, WorkItemEditOperation.Update, new(Title: " new title ", Description: " new description ",
            Tags: ["one", "one", "two"], Priority: 200, Status: ProjectWorkItemStatus.InProgress,
            DefinitionState: ProjectWorkItemDefinitionState.ReadyForDevelopment,
            DueAt: new(EditDueAtMode.Set, Now.AddHours(3).ToOffset(TimeSpan.FromHours(2)))));
        WorkItemEditAggregate.Apply(item, command, Now).Should().BeTrue();
        item.Revision.Should().Be(8); item.UpdatedAt.Should().Be(Now);
        item.Title.Should().Be("new title"); item.Description.Should().Be("new description"); item.Tags.Should().Equal("one", "two");
        item.Priority.Should().Be(100); item.Status.Should().Be(ProjectWorkItemStatus.InProgress);
        item.DefinitionState.Should().Be(ProjectWorkItemDefinitionState.ReadyForDevelopment);
        item.DueAt.Should().Be(Now.AddHours(3)); item.DueAt!.Value.Offset.Should().Be(TimeSpan.Zero);
        item.CreatedAt.Should().Be(originalCreated); item.TenantId.Should().Be(originalTenant); item.OwnerUserId.Should().Be(originalOwner);
        item.GovernanceExclusionsJson.Should().Be("[\"synthetic-untouched-evidence\"]");
        item.ChecklistItems.Should().Equal(rows); rows.Should().OnlyContain(row => row.UpdatedAt == DateTimeOffset.UnixEpoch);
    }

    [Theory]
    [InlineData(EditDueAtMode.Keep)]
    [InlineData(EditDueAtMode.Clear)]
    [InlineData(EditDueAtMode.Set)]
    public void Due_date_keep_clear_and_set_have_distinct_effects(EditDueAtMode mode)
    {
        var item = Item(); var existing = item.DueAt;
        var command = Command(item, WorkItemEditOperation.Update, new(DueAt: new(mode, mode == EditDueAtMode.Set ? Now : null)));
        var changed = WorkItemEditAggregate.Apply(item, command, Now);
        item.DueAt.Should().Be(mode switch { EditDueAtMode.Keep => existing, EditDueAtMode.Clear => null, _ => Now });
        changed.Should().Be(mode != EditDueAtMode.Keep);
        item.Revision.Should().Be(mode == EditDueAtMode.Keep ? 7 : 8);
    }

    [Theory]
    [InlineData(ProjectWorkItemStatus.Pending)]
    [InlineData(ProjectWorkItemStatus.InProgress)]
    [InlineData(ProjectWorkItemStatus.Cancelled)]
    public void Completed_status_cannot_be_implicitly_reopened_by_update(ProjectWorkItemStatus status)
    {
        var item = Item(completed: true); var before = Snapshot(item);
        var error = FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item,
            Command(item, WorkItemEditOperation.Update, new(Status: status)), Now)).Should().Throw<SafeEditConflictException>().Which;
        error.Code.Should().Be(SafeEditConflictCodes.RequiresReopen); Snapshot(item).Should().Be(before);
    }

    [Theory]
    [InlineData(WorkItemEditOperation.ChecklistComplete)]
    [InlineData(WorkItemEditOperation.ChecklistAdd)]
    public void Completed_uncheck_or_new_incomplete_leaf_requires_explicit_reopen(WorkItemEditOperation operation)
    {
        var item = Item(completed: true); var first = item.ChecklistItems.First().Id; var before = Snapshot(item);
        var patch = operation switch
        {
            WorkItemEditOperation.ChecklistComplete => new WorkItemEditPatch(ChecklistItemId: first, IsCompleted: false),
            _ => new(ChecklistItemId: Guid.NewGuid(), ChecklistContent: "new")
        };
        var error = FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, Command(item, operation, patch), Now))
            .Should().Throw<SafeEditConflictException>().Which;
        error.Code.Should().Be(SafeEditConflictCodes.RequiresReopen); Snapshot(item).Should().Be(before);
    }

    [Theory]
    [InlineData(WorkItemEditOperation.ChecklistText)]
    [InlineData(WorkItemEditOperation.ChecklistRemove)]
    [InlineData(WorkItemEditOperation.ChecklistReorder)]
    public void Completed_text_remove_and_reorder_preserve_completion_without_reopening(WorkItemEditOperation operation)
    {
        var item = Item(completed: true); var first = item.ChecklistItems.First().Id; var completedAt = item.CompletedAt;
        var patch = operation switch
        {
            WorkItemEditOperation.ChecklistText => new WorkItemEditPatch(ChecklistItemId: first, ChecklistContent: "corrected text"),
            WorkItemEditOperation.ChecklistRemove => new(ChecklistItemId: first),
            _ => new(ChecklistOrder: item.ChecklistItems.Reverse().Select(row => row.Id).ToArray())
        };
        WorkItemEditAggregate.Apply(item, Command(item, operation, patch), Now).Should().BeTrue();
        item.Status.Should().Be(ProjectWorkItemStatus.Completed); item.CompletedAt.Should().Be(completedAt);
        item.ChecklistItems.Should().OnlyContain(row => row.IsCompleted); item.Revision.Should().Be(8);
    }

    [Theory]
    [InlineData(WorkItemEditOperation.ChecklistComplete)]
    [InlineData(WorkItemEditOperation.ChecklistText)]
    [InlineData(WorkItemEditOperation.ChecklistReorder)]
    public void Completed_unchanged_completion_text_and_order_are_no_ops(WorkItemEditOperation operation)
    {
        var item = Item(completed: true); var first = item.ChecklistItems.First(); var before = Snapshot(item);
        var patch = operation switch
        {
            WorkItemEditOperation.ChecklistComplete => new WorkItemEditPatch(ChecklistItemId: first.Id, IsCompleted: true),
            WorkItemEditOperation.ChecklistText => new(ChecklistItemId: first.Id, ChecklistContent: first.Content),
            _ => new(ChecklistOrder: item.ChecklistItems.OrderBy(row => row.SortOrder).Select(row => row.Id).ToArray())
        };
        WorkItemEditAggregate.Apply(item, Command(item, operation, patch), Now).Should().BeFalse();
        Snapshot(item).Should().Be(before);
    }

    [Theory]
    [InlineData(WorkItemEditOperation.ChecklistText)]
    [InlineData(WorkItemEditOperation.ChecklistReorder)]
    public void Completed_edits_that_leave_incomplete_leaves_fail_without_changing_state(WorkItemEditOperation operation)
    {
        var item = Item(completed: true); item.ChecklistItems.First().IsCompleted = false; var before = Snapshot(item);
        var patch = operation == WorkItemEditOperation.ChecklistText
            ? new WorkItemEditPatch(ChecklistItemId: item.ChecklistItems.First().Id, ChecklistContent: "corrected")
            : new(ChecklistOrder: item.ChecklistItems.Reverse().Select(row => row.Id).ToArray());
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, Command(item, operation, patch), Now))
            .Should().Throw<InvalidOperationException>().Which.Should().NotBeOfType<SafeEditConflictException>();
        Snapshot(item).Should().Be(before);
    }

    [Fact]
    public void Removing_an_incomplete_historical_leaf_can_retain_completed_state_without_reopening()
    {
        var item = Item(completed: true); var incomplete = item.ChecklistItems.First(); incomplete.IsCompleted = false;
        var completedAt = item.CompletedAt; var remaining = item.ChecklistItems.Last();
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistRemove,
            new(ChecklistItemId: incomplete.Id)), Now).Should().BeTrue();
        item.Status.Should().Be(ProjectWorkItemStatus.Completed); item.CompletedAt.Should().Be(completedAt);
        item.ChecklistItems.Should().ContainSingle().Which.Should().BeSameAs(remaining); item.Revision.Should().Be(8);
    }

    [Fact]
    public void Explicit_reopen_then_uncheck_preserves_leaf_identity_and_requires_the_new_revision()
    {
        var item = Item(completed: true); var priorCompletion = item.CompletedAt; var leaf = item.ChecklistItems.First();
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.Reopen, new()), Now).Should().BeTrue();
        item.Status.Should().Be(ProjectWorkItemStatus.InProgress); item.CompletedAt.Should().BeNull(); item.Revision.Should().Be(8);
        priorCompletion.Should().Be(DateTimeOffset.UnixEpoch.AddDays(1), "the caller captures this prior state for durable history before applying");
        var uncheck = Command(item, WorkItemEditOperation.ChecklistComplete, new(ChecklistItemId: leaf.Id, IsCompleted: false));
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, uncheck with { ExpectedRevision = 7 }, Now))
            .Should().Throw<SafeEditConflictException>();
        WorkItemEditAggregate.Apply(item, uncheck, Now.AddMinutes(1)).Should().BeTrue();
        item.Revision.Should().Be(9); item.ChecklistItems.First().Should().BeSameAs(leaf); leaf.IsCompleted.Should().BeFalse();
        leaf.UpdatedAt.Should().Be(Now.AddMinutes(1));
    }

    [Fact]
    public void Completing_requires_every_leaf_and_repeated_completed_status_does_not_rewrite_time()
    {
        var item = Item(); var before = Snapshot(item);
        var complete = Command(item, WorkItemEditOperation.Update, new(Status: ProjectWorkItemStatus.Completed));
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, complete, Now)).Should().Throw<InvalidOperationException>();
        Snapshot(item).Should().Be(before);
        foreach (var row in item.ChecklistItems) row.IsCompleted = true;
        WorkItemEditAggregate.Apply(item, complete, Now).Should().BeTrue();
        item.CompletedAt.Should().Be(Now); item.Revision.Should().Be(8);
        var completed = Snapshot(item);
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.Update, new(Status: ProjectWorkItemStatus.Completed)), Now.AddDays(1)).Should().BeFalse();
        Snapshot(item).Should().Be(completed);
    }

    [Fact]
    public void Archive_and_restore_retain_historical_status_even_when_legacy_completed_rows_are_inconsistent()
    {
        var item = Item(completed: true); item.ChecklistItems.First().IsCompleted = false;
        var completedAt = item.CompletedAt;
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.Archive, new()), Now).Should().BeTrue();
        item.Status.Should().Be(ProjectWorkItemStatus.Completed); item.CompletedAt.Should().Be(completedAt); item.ArchivedAt.Should().Be(Now);
        var archived = Snapshot(item);
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.Archive, new()), Now.AddDays(1)).Should().BeFalse();
        Snapshot(item).Should().Be(archived);
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.Restore, new()), Now.AddDays(2)).Should().BeTrue();
        item.Status.Should().Be(ProjectWorkItemStatus.Completed); item.CompletedAt.Should().Be(completedAt); item.ArchivedAt.Should().BeNull();
        item.Revision.Should().Be(9);
    }

    [Fact]
    public void Archived_items_reject_ordinary_edits_without_partial_mutation()
    {
        var item = Item(); item.ArchivedAt = DateTimeOffset.UnixEpoch; var before = Snapshot(item);
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.Update, new(Title: "changed")), Now))
            .Should().Throw<InvalidOperationException>();
        Snapshot(item).Should().Be(before);
    }

    [Fact]
    public void Metadata_only_edits_do_not_rewrite_a_legacy_completion_inconsistency()
    {
        var item = Item(completed: true); item.ChecklistItems.First().IsCompleted = false;
        var completedAt = item.CompletedAt;
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.Update, new(Title: "corrected title")), Now).Should().BeTrue();
        item.Title.Should().Be("corrected title"); item.Status.Should().Be(ProjectWorkItemStatus.Completed);
        item.CompletedAt.Should().Be(completedAt); item.ChecklistItems.First().IsCompleted.Should().BeFalse();
        item.Revision.Should().Be(8);
    }

    [Fact]
    public void Add_text_reorder_and_remove_preserve_surviving_ids_completion_and_creation_metadata()
    {
        var item = Item(); var original = item.ChecklistItems.ToArray(); var addedId = Guid.NewGuid();
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistAdd,
            new(ChecklistItemId: addedId, ChecklistContent: " new leaf ")), Now).Should().BeTrue();
        var added = item.ChecklistItems.Single(row => row.Id == addedId);
        added.Content.Should().Be("new leaf"); added.WorkItemId.Should().Be(item.Id); added.WorkItem.Should().BeSameAs(item);
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistText,
            new(ChecklistItemId: original[0].Id, ChecklistContent: " edited ")), Now.AddMinutes(1)).Should().BeTrue();
        original[0].Content.Should().Be("edited"); original[0].IsCompleted.Should().BeTrue();
        original[0].CreatedAt.Should().Be(DateTimeOffset.UnixEpoch);
        var order = new[] { addedId, original[1].Id, original[0].Id };
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistReorder, new(ChecklistOrder: order)), Now.AddMinutes(2)).Should().BeTrue();
        item.ChecklistItems.OrderBy(row => row.SortOrder).Select(row => row.Id).Should().Equal(order);
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistRemove, new(ChecklistItemId: original[1].Id)), Now.AddMinutes(3)).Should().BeTrue();
        item.ChecklistItems.OrderBy(row => row.SortOrder).Select(row => row.Id).Should().Equal(addedId, original[0].Id);
        item.ChecklistItems.OrderBy(row => row.SortOrder).Select(row => row.SortOrder).Should().Equal(0, 1);
        item.ChecklistItems.Single(row => row.Id == original[0].Id).Should().BeSameAs(original[0]);
        item.Revision.Should().Be(11);
    }

    [Fact]
    public void A_legacy_array_navigation_is_made_mutable_without_replacing_tracked_child_instances()
    {
        var item = Item(); var existing = item.ChecklistItems.ToArray(); item.ChecklistItems = existing;
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistAdd,
            new(ChecklistItemId: Guid.NewGuid(), ChecklistContent: "new")), Now).Should().BeTrue();
        item.ChecklistItems.Should().HaveCount(3);
        item.ChecklistItems.Single(row => row.Id == existing[0].Id).Should().BeSameAs(existing[0]);
        item.ChecklistItems.Single(row => row.Id == existing[1].Id).Should().BeSameAs(existing[1]);
        item.Revision.Should().Be(8);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("extra")]
    [InlineData("duplicate")]
    [InlineData("empty")]
    public void Invalid_complete_reorder_is_rejected_before_mutation(string scenario)
    {
        var item = Item(); var ids = item.ChecklistItems.Select(row => row.Id).ToArray(); var before = Snapshot(item);
        IReadOnlyList<Guid> order = scenario switch
        {
            "missing" => [ids[0]],
            "extra" => [.. ids, Guid.NewGuid()],
            "duplicate" => [ids[0], ids[0]],
            _ => []
        };
        Action apply = () => WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistReorder, new(ChecklistOrder: order)), Now);
        if (scenario == "duplicate") apply.Should().Throw<ArgumentException>();
        else apply.Should().Throw<InvalidOperationException>();
        Snapshot(item).Should().Be(before);
    }

    [Fact]
    public void Empty_checklist_reorder_and_unchanged_completion_are_no_ops()
    {
        var item = Item(); var before = Snapshot(item); var leaf = item.ChecklistItems.First();
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistComplete,
            new(ChecklistItemId: leaf.Id, IsCompleted: leaf.IsCompleted)), Now).Should().BeFalse();
        Snapshot(item).Should().Be(before);
        item.ChecklistItems.Clear(); before = Snapshot(item);
        WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistReorder, new(ChecklistOrder: [])), Now).Should().BeFalse();
        Snapshot(item).Should().Be(before);
    }

    [Fact]
    public void Missing_duplicate_and_over_capacity_leaf_edits_leave_state_unchanged()
    {
        var item = Item(); var first = item.ChecklistItems.First().Id; var before = Snapshot(item);
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistText,
            new(ChecklistItemId: Guid.NewGuid(), ChecklistContent: "missing")), Now)).Should().Throw<InvalidOperationException>();
        Snapshot(item).Should().Be(before);
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistAdd,
            new(ChecklistItemId: first, ChecklistContent: "duplicate")), Now)).Should().Throw<InvalidOperationException>();
        Snapshot(item).Should().Be(before);
        while (item.ChecklistItems.Count < 100) item.ChecklistItems.Add(new() { WorkItemId = item.Id, Content = "synthetic", SortOrder = item.ChecklistItems.Count });
        before = Snapshot(item);
        FluentActions.Invoking(() => WorkItemEditAggregate.Apply(item, Command(item, WorkItemEditOperation.ChecklistAdd,
            new(ChecklistItemId: Guid.NewGuid(), ChecklistContent: "overflow")), Now)).Should().Throw<InvalidOperationException>();
        Snapshot(item).Should().Be(before);
    }

    private static WorkItemEditCommand Command(ProjectWorkItem item, WorkItemEditOperation operation, WorkItemEditPatch patch)
        => new(item.ProjectId, item.Id, item.Revision, "synthetic-request", operation, patch);

    private static ProjectWorkItem Item(bool completed = false)
    {
        var item = new ProjectWorkItem
        {
            TenantId = Guid.NewGuid(),
            OwnerUserId = Guid.NewGuid(),
            ProjectId = "ContextHub",
            Title = "original",
            Description = "description",
            Tags = ["original"],
            Priority = 8,
            Revision = 7,
            Status = completed ? ProjectWorkItemStatus.Completed : ProjectWorkItemStatus.Pending,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch,
            DueAt = DateTimeOffset.UnixEpoch.AddDays(20),
            CompletedAt = completed ? DateTimeOffset.UnixEpoch.AddDays(1) : null,
            GovernanceExclusionsJson = "[\"synthetic-untouched-evidence\"]"
        };
        item.ChecklistItems = new[] { true, completed }.Select((value, index) => new ProjectWorkItemChecklistItem
        {
            WorkItemId = item.Id,
            Content = "leaf" + index,
            IsCompleted = value,
            SortOrder = index,
            CreatedAt = DateTimeOffset.UnixEpoch,
            UpdatedAt = DateTimeOffset.UnixEpoch
        }).ToList();
        return item;
    }

    private static string Snapshot(ProjectWorkItem item) => JsonSerializer.Serialize(new
    {
        item.Id,
        item.TenantId,
        item.OwnerUserId,
        item.ProjectId,
        item.Title,
        item.Description,
        item.Tags,
        item.Priority,
        item.Status,
        item.DefinitionState,
        item.DueAt,
        item.Revision,
        item.CreatedAt,
        item.UpdatedAt,
        item.CompletedAt,
        item.ArchivedAt,
        item.GovernanceExclusionsJson,
        rows = item.ChecklistItems.OrderBy(row => row.SortOrder).ThenBy(row => row.Id).Select(row => new
        {
            row.Id,
            row.WorkItemId,
            row.Content,
            row.IsCompleted,
            row.SortOrder,
            row.CreatedAt,
            row.UpdatedAt
        })
    });
}

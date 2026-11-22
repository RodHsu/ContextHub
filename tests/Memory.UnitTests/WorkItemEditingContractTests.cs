using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class WorkItemEditingContractTests
{
    private static readonly Guid WorkItemId = Guid.Parse("9058acf2-03a2-444c-94b8-c09c295e882d");
    private static readonly Guid FirstLeaf = Guid.Parse("3e5bc6df-2ad4-45e9-90d6-9d92b290fbf5");
    private static readonly Guid SecondLeaf = Guid.Parse("5f5201db-6e40-4f50-ac1e-36fa6f947c76");

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Missing_original_revision_is_rejected_without_substituting_the_current_revision(long? revision)
    {
        var command = Command() with { ExpectedRevision = revision };
        var normalized = FluentActions.Invoking(() => WorkItemEditContract.Normalize(command))
            .Should().Throw<SafeEditConflictException>().Which;
        normalized.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        normalized.CurrentRevision.Should().BeNull();
        var compared = FluentActions.Invoking(() => WorkItemEditContract.RequireRevision(revision, 7))
            .Should().Throw<SafeEditConflictException>().Which;
        compared.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        compared.CurrentRevision.Should().BeNull();
    }

    [Fact]
    public void Comparison_accepts_only_the_original_current_revision()
    {
        FluentActions.Invoking(() => WorkItemEditContract.RequireRevision(7, 7)).Should().NotThrow();
        var exception = FluentActions.Invoking(() => WorkItemEditContract.RequireRevision(6, 7))
            .Should().Throw<SafeEditConflictException>().Which;
        exception.Code.Should().Be(SafeEditConflictCodes.StaleReadVersion);
        exception.CurrentRevision.Should().Be(7);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(-1L)]
    public void Invalid_persisted_revision_is_not_treated_as_a_valid_baseline(long actual)
        => FluentActions.Invoking(() => WorkItemEditContract.RequireRevision(7, actual)).Should().Throw<ArgumentOutOfRangeException>();

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("shared")]
    [InlineData("USER")]
    [InlineData("*")]
    public void Scope_must_be_explicit_and_regular(string project)
        => FluentActions.Invoking(() => WorkItemEditContract.Normalize(Command() with { ProjectId = project })).Should().Throw<ArgumentException>();

    [Fact]
    public void Identity_and_text_normalization_follow_the_edit_bounds_without_mutating_the_input()
    {
        var tags = new List<string> { " Alpha ", "alpha", "", " Beta " };
        var command = Command(new(Title: " Title ", Description: " Description ", Tags: tags, Priority: -1))
            with
        { ProjectId = " ContextHub ", RequestId = " request-1 " };
        var normalized = WorkItemEditContract.Normalize(command);
        normalized.ProjectId.Should().Be("ContextHub");
        normalized.RequestId.Should().Be("request-1");
        normalized.Patch.Title.Should().Be("Title");
        normalized.Patch.Description.Should().Be("Description");
        normalized.Patch.Tags.Should().Equal("Alpha", "Beta");
        normalized.Patch.Priority.Should().Be(0);
        normalized.Patch.DueAt.Should().Be(new EditDueAt());
        tags[0] = "changed";
        normalized.Patch.Tags.Should().Equal("Alpha", "Beta");
        command.Patch.Title.Should().Be(" Title ");
    }

    [Fact]
    public void Mutable_checklist_order_is_copied_before_the_command_can_be_applied()
    {
        var order = new List<Guid> { FirstLeaf, SecondLeaf };
        var normalized = WorkItemEditContract.Normalize(Command(new(ChecklistOrder: order), WorkItemEditOperation.ChecklistReorder));
        order.Reverse();
        normalized.Patch.ChecklistOrder.Should().Equal(FirstLeaf, SecondLeaf);
    }

    [Theory]
    [InlineData("title")]
    [InlineData("description")]
    [InlineData("tag")]
    [InlineData("tags")]
    [InlineData("request")]
    [InlineData("content")]
    public void Oversized_inputs_are_rejected_rather_than_silently_truncated(string field)
    {
        var command = field switch
        {
            "title" => Command(new(Title: new string('x', 201))),
            "description" => Command(new(Description: new string('x', 12001))),
            "tag" => Command(new(Tags: [new string('x', 51)])),
            "tags" => Command(new(Tags: Enumerable.Range(1, 13).Select(index => index.ToString()).ToArray())),
            "request" => Command() with { RequestId = new string('x', 201) },
            _ => Command(new(ChecklistItemId: FirstLeaf, ChecklistContent: new string('x', 501)), WorkItemEditOperation.ChecklistText)
        };
        FluentActions.Invoking(() => WorkItemEditContract.Normalize(command)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("title")]
    [InlineData("content")]
    [InlineData("request")]
    public void Required_text_cannot_be_blank(string field)
    {
        var command = field switch
        {
            "title" => Command(new(Title: " ")),
            "content" => Command(new(ChecklistItemId: FirstLeaf, ChecklistContent: " "), WorkItemEditOperation.ChecklistText),
            _ => Command() with { RequestId = " " }
        };
        FluentActions.Invoking(() => WorkItemEditContract.Normalize(command)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Maximum_supported_text_and_tag_bounds_remain_usable()
    {
        var command = Command(new(Title: new string('t', 200), Description: new string('d', 12000),
            Tags: Enumerable.Range(1, 12).Select(index => index.ToString()).ToArray(), Priority: int.MaxValue))
            with
        { RequestId = new string('r', 200) };
        var normalized = WorkItemEditContract.Normalize(command);
        normalized.Patch.Priority.Should().Be(100);
        normalized.Patch.Tags.Should().HaveCount(12);
        FluentActions.Invoking(() => WorkItemEditContract.Normalize(Command(new(ChecklistItemId: FirstLeaf,
            ChecklistContent: new string('c', 500)), WorkItemEditOperation.ChecklistAdd))).Should().NotThrow();
    }

    [Fact]
    public void Unknown_operations_enums_and_empty_identities_fail_closed()
    {
        var commands = new[]
        {
            Command() with { Operation = (WorkItemEditOperation)999 },
            Command() with { WorkItemId = Guid.Empty },
            Command(new(Status: (ProjectWorkItemStatus)999)),
            Command(new(DefinitionState: (ProjectWorkItemDefinitionState)999)),
            Command(new(DueAt: new((EditDueAtMode)999))),
            Command() with { Patch = null! }
        };
        foreach (var command in commands)
            FluentActions.Invoking(() => WorkItemEditContract.Normalize(command)).Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(EditDueAtMode.Keep, true)]
    [InlineData(EditDueAtMode.Clear, true)]
    [InlineData(EditDueAtMode.Set, false)]
    public void Due_at_modes_require_an_unambiguous_value_combination(EditDueAtMode mode, bool hasValue)
    {
        var patch = new WorkItemEditPatch(DueAt: new(mode, hasValue ? DateTimeOffset.UnixEpoch : null));
        FluentActions.Invoking(() => WorkItemEditContract.Normalize(Command(patch))).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Keep_clear_and_set_have_distinct_intents_and_equivalent_instants_share_the_set_hash()
    {
        var keep = Command();
        var clear = Command(new(DueAt: new(EditDueAtMode.Clear)));
        var time = new DateTimeOffset(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);
        var set = Command(new(DueAt: new(EditDueAtMode.Set, time)));
        var equivalent = Command(new(DueAt: new(EditDueAtMode.Set, time.ToOffset(TimeSpan.FromHours(8)))));
        new[] { WorkItemEditContract.Hash(keep), WorkItemEditContract.Hash(clear), WorkItemEditContract.Hash(set) }
            .Distinct().Should().HaveCount(3);
        WorkItemEditContract.Hash(set).Should().Be(WorkItemEditContract.Hash(equivalent));
        WorkItemEditContract.Normalize(equivalent).Patch.DueAt!.Value!.Value.Offset.Should().Be(TimeSpan.Zero);
        WorkItemEditContract.Hash(Command(new(DueAt: new(EditDueAtMode.Keep)))).Should().Be(WorkItemEditContract.Hash(keep));
    }

    [Fact]
    public void Hash_is_canonical_for_normalized_intent_and_request_identity_is_a_separate_store_key()
    {
        var command = Command(new(Title: " Title ", Tags: ["Tag", "tag", " Other "]));
        var equivalent = Command(new(Title: "Title", Tags: ["Tag", "Other"]))
            with
        { ProjectId = " contexthub ", RequestId = "different-request" };
        var hash = WorkItemEditContract.Hash(command);
        hash.Should().MatchRegex("^[0-9a-f]{64}$");
        hash.Should().Be(WorkItemEditContract.Hash(equivalent));
        // Hash equality is not replay permission; the real store must compare the server-scoped identity key.
    }

    [Fact]
    public void Revision_scope_target_and_meaningful_field_presence_change_the_hash()
    {
        var command = Command();
        var variants = new[]
        {
            command,
            command with { ExpectedRevision = 8 },
            command with { ProjectId = "Other" },
            command with { WorkItemId = SecondLeaf },
            command with { Patch = new(Description: string.Empty) },
            command with { Patch = new(Tags: []) },
            command with { Operation = WorkItemEditOperation.Archive }
        };
        variants.Select(WorkItemEditContract.Hash).Distinct().Should().HaveCount(variants.Length);
    }

    [Fact]
    public void Checklist_order_identity_completion_and_text_are_bound_to_the_hash()
    {
        var first = Command(new(ChecklistOrder: [FirstLeaf, SecondLeaf]), WorkItemEditOperation.ChecklistReorder);
        var reverse = first with { Patch = new(ChecklistOrder: [SecondLeaf, FirstLeaf]) };
        WorkItemEditContract.Hash(first).Should().NotBe(WorkItemEditContract.Hash(reverse));
        var complete = Command(new(ChecklistItemId: FirstLeaf, IsCompleted: true), WorkItemEditOperation.ChecklistComplete);
        WorkItemEditContract.Hash(complete).Should().NotBe(WorkItemEditContract.Hash(complete with { Patch = new(ChecklistItemId: FirstLeaf, IsCompleted: false) }));
        WorkItemEditContract.Hash(complete).Should().NotBe(WorkItemEditContract.Hash(complete with { Patch = new(ChecklistItemId: SecondLeaf, IsCompleted: true) }));
        var text = Command(new(ChecklistItemId: FirstLeaf, ChecklistContent: "new text"), WorkItemEditOperation.ChecklistText);
        WorkItemEditContract.Hash(text).Should().NotBe(WorkItemEditContract.Hash(text with { Patch = text.Patch with { ChecklistContent = "other text" } }));
    }

    [Fact]
    public void Operation_specific_shapes_reject_mixed_or_missing_fields()
    {
        var commands = new[]
        {
            Command(new(ChecklistItemId: FirstLeaf)),
            Command(new(Title: "ignored"), WorkItemEditOperation.Archive),
            Command(new(IsCompleted: true), WorkItemEditOperation.Reopen),
            Command(new(ChecklistItemId: Guid.Empty, IsCompleted: true), WorkItemEditOperation.ChecklistComplete),
            Command(new(ChecklistItemId: FirstLeaf), WorkItemEditOperation.ChecklistComplete),
            Command(new(ChecklistItemId: FirstLeaf, IsCompleted: true, ChecklistContent: "ignored"), WorkItemEditOperation.ChecklistComplete),
            Command(new(ChecklistItemId: FirstLeaf), WorkItemEditOperation.ChecklistAdd),
            Command(new(ChecklistItemId: FirstLeaf, ChecklistContent: "ignored"), WorkItemEditOperation.ChecklistRemove),
            Command(new(), WorkItemEditOperation.ChecklistReorder),
            Command(new(ChecklistOrder: [FirstLeaf], ChecklistItemId: FirstLeaf), WorkItemEditOperation.ChecklistReorder)
        };
        foreach (var command in commands)
            FluentActions.Invoking(() => WorkItemEditContract.Normalize(command)).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Reorder_shape_accepts_empty_and_distinct_lists_but_not_duplicates_empty_ids_or_overflow()
    {
        FluentActions.Invoking(() => WorkItemEditContract.Normalize(Command(new(ChecklistOrder: []), WorkItemEditOperation.ChecklistReorder))).Should().NotThrow();
        FluentActions.Invoking(() => WorkItemEditContract.Normalize(Command(new(ChecklistOrder: [FirstLeaf, SecondLeaf]), WorkItemEditOperation.ChecklistReorder))).Should().NotThrow();
        foreach (var order in new[] { new[] { FirstLeaf, FirstLeaf }, new[] { Guid.Empty }, Enumerable.Range(0, 101).Select(_ => Guid.NewGuid()).ToArray() })
            FluentActions.Invoking(() => WorkItemEditContract.Normalize(Command(new(ChecklistOrder: order), WorkItemEditOperation.ChecklistReorder))).Should().Throw<ArgumentException>();
    }

    private static WorkItemEditCommand Command(WorkItemEditPatch? patch = null, WorkItemEditOperation operation = WorkItemEditOperation.Update)
        => new("ContextHub", WorkItemId, 7, "request-1", operation, patch ?? new());
}

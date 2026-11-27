using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class MemoryEditContractTests
{
    [Fact]
    public void Fingerprint_keeps_edit_fields_and_original_version_but_folds_project_aliases()
    {
        var command = Command(new(Title: "after", Importance: 0.50m));
        var same = command with
        {
            ProjectId = " CONTEXTHUB ",
            RequestId = "different receipt lookup key",
            Patch = new(Title: "after", Importance: 0.5m)
        };
        var hash = MemoryEditContract.Hash(command);
        MemoryEditContract.Hash(same).Should().Be(hash);
        MemoryEditContract.Hash(command with { ExpectedVersion = 2 }).Should().NotBe(hash);
        MemoryEditContract.Hash(command with { ProjectId = "OtherProject" }).Should().NotBe(hash);
        MemoryEditContract.Hash(command with { Patch = command.Patch with { Content = "" } }).Should().NotBe(hash);
        MemoryEditContract.Hash(command with { Patch = command.Patch with { Tags = [] } }).Should().NotBe(hash);
    }

    [Fact]
    public void Invalid_text_and_empty_intent_fail_with_safe_fixed_messages()
    {
        var invalid = Command(new(Content: "fixture-" + '\uD800'));
        var failure = FluentActions.Invoking(() => MemoryEditContract.Hash(invalid))
            .Should().Throw<ArgumentException>().Which;
        failure.Message.Should().Be("Memory edit text is invalid.");
        failure.InnerException.Should().BeNull();
        FluentActions.Invoking(() => MemoryEditContract.Normalize(Command(new())))
            .Should().Throw<ArgumentException>().WithMessage("At least one editable memory field is required.");
    }

    [Fact]
    public void Original_read_version_and_scope_are_explicit()
    {
        var command = Command(new(Content: "candidate"));
        FluentActions.Invoking(() => MemoryEditContract.Normalize(command with { ExpectedVersion = 0 }))
            .Should().Throw<SafeEditConflictException>().Which.Code.Should().Be(SafeEditConflictCodes.MissingReadVersion);
        FluentActions.Invoking(() => MemoryEditContract.Normalize(command with { ProjectId = "" }))
            .Should().Throw<ArgumentException>().WithMessage("An explicit ProjectId is required.");
        FluentActions.Invoking(() => MemoryEditContract.Normalize(command with { ProjectId = ProjectContext.UserProjectId }))
            .Should().Throw<UnauthorizedAccessException>();
        FluentActions.Invoking(() => MemoryEditContract.Normalize(command with { Patch = new(Confidence: 90m) }))
            .Should().Throw<MemoryScoreValidationException>();
    }

    private static MemoryEditCommand Command(MemoryEditPatch patch)
        => new("ContextHub", Guid.NewGuid(), 1, "original-key", patch);
}

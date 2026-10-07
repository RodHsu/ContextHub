using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class ProjectIdentityTests
{
    [Fact]
    public void Project_grants_accept_case_aliases_and_keep_other_identifiers_separate()
    {
        var actor = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "member", Memory.Domain.TenantUserRole.Member,
            [SecurityScopes.MemoryRead], ["Tt", "Å"], true);
        foreach (var project in new[] { "TT", "Tt", "tT", "tt" })
            ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
        foreach (var project in new[] { "TT-1", "T_T", "A\u030a" })
        {
            var denied = () => ActorAuthorization.EnsureProjectAllowed(actor, project, write: false);
            denied.Should().Throw<UnauthorizedAccessException>();
        }
        ProjectContext.IsShared(" SHARED ").Should().BeTrue();
        ProjectContext.IsUser(" UsEr ").Should().BeTrue();
    }

    [Fact]
    public void Project_spelling_is_preserved_while_all_case_variants_share_one_identity()
    {
        var variants = new[] { "TT", "Tt", "tT", "tt" };
        foreach (var left in variants)
        {
            ProjectContext.Normalize(" " + left + " ").Should().Be(left);
            foreach (var right in variants)
                ProjectContext.Matches(left, right).Should().BeTrue();
        }
        ProjectContext.IdentityKeys(["TT", "Tt", "tT", "tt", " Other "])
            .Should().Equal("OTHER", "TT");
    }

    [Theory]
    [InlineData("TT", "TT-1")]
    [InlineData("TT", "T_T")]
    [InlineData("café", "cafe")]
    [InlineData("Å", "A\u030a")]
    [InlineData("ff", "ﬀ")]
    [InlineData("ß", "SS")]
    public void Case_equivalence_does_not_merge_other_identifiers(string left, string right)
        => ProjectContext.Matches(left, right).Should().BeFalse();

    [Theory]
    [InlineData(" É-Proj ", "é-proj")]
    [InlineData("專案TT", "專案tt")]
    [InlineData("Σ", "ς")]
    [InlineData("\u2003TT\u2003", "tt")]
    [InlineData("\U00010400", "\U00010428")]
    public void Identity_is_invariant_and_trims_the_same_whitespace_as_project_input(string left, string right)
        => ProjectContext.Matches(left, right).Should().BeTrue();

    [Fact]
    public void Missing_reference_is_not_a_project_identity()
    {
        ProjectContext.Matches(null, "default").Should().BeFalse();
        ProjectContext.Matches("default", null).Should().BeFalse();
        ProjectContext.Matches(null, null).Should().BeFalse();
    }

    [Fact]
    public void Identity_trims_every_supported_project_input_whitespace_character()
    {
        foreach (var whitespace in "\t\n\u000b\f\r \u0085\u00a0\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200a\u2028\u2029\u202f\u205f\u3000")
        {
            var value = $"{whitespace}Tt{whitespace}";
            ProjectContext.Normalize(value).Should().Be("Tt");
            ProjectContext.IdentityKey(value).Should().Be("TT", "input normalization and identity must trim U+{0:X4}", (int)whitespace);
        }
    }

    [Fact]
    public void Invalid_utf16_cannot_alias_a_valid_project_through_replacement_characters()
    {
        var fold = () => ProjectContext.IdentityKey("TT\ud800");
        fold.Should().Throw<ArgumentException>();
        var revisions = new Dictionary<string, long>(ProjectContext.IdentityComparer) { ["TT"] = 3 };
        revisions["tt"].Should().Be(3);
    }
}

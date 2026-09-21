using FluentAssertions;
using Memory.Application;
using Memory.Domain;

namespace Memory.UnitTests;

public sealed class PlatformFoundationTests
{
    [Fact]
    public void Effective_rights_follow_multi_parent_nearest_union_and_deny_precedence()
    {
        var evaluator = new EffectiveRightsEvaluator();
        var edges = new[]
        {
            Edge("root-a", "middle-a"), Edge("middle-a", "target"),
            Edge("root-b", "target")
        };
        var rules = new[]
        {
            Rule("root-a", "read", AuthorizationEffect.Deny, "root-a-deny"),
            Rule("middle-a", "read", AuthorizationEffect.Allow, "middle-a-allow"),
            Rule("root-b", "read", AuthorizationEffect.Deny, "root-b-deny"),
            Rule("root-a", "write", AuthorizationEffect.Allow, "root-a-write"),
            Rule("root-b", "write", AuthorizationEffect.Allow, "root-b-write")
        };

        var result = evaluator.Evaluate(Request(["read", "write"], edges, rules));

        result.Decisions.Single(x => x.Right == "read").Allowed.Should().BeFalse();
        result.Decisions.Single(x => x.Right == "read").Evidence.Should().BeEquivalentTo("middle-a-allow", "root-b-deny");
        result.Decisions.Single(x => x.Right == "write").Allowed.Should().BeTrue();
    }

    [Fact]
    public void Resource_overrides_project_and_project_overrides_inherited_per_right()
    {
        var evaluator = new EffectiveRightsEvaluator();
        var rules = new[]
        {
            Rule("parent", "read", AuthorizationEffect.Deny, "parent"),
            Rule("target", "read", AuthorizationEffect.Allow, "project"),
            Rule("target", "read", AuthorizationEffect.Deny, "resource", "File", "42")
        };

        var result = evaluator.Evaluate(Request(["read"], [Edge("parent", "target")], rules, "File", "42"));

        result.Decisions.Single().Allowed.Should().BeFalse();
        result.Decisions.Single().Tier.Should().Be("Resource");
        result.Decisions.Single().Evidence.Should().Equal("resource");
    }

    [Fact]
    public void Policy_derived_rules_are_evaluated_from_each_snapshot_and_revision_changes_cache_key()
    {
        var evaluator = new EffectiveRightsEvaluator();
        var denied = evaluator.Evaluate(Request(["read"], [], [Rule("target", "read", AuthorizationEffect.Deny, "policy-v1")], revisions: new(0, 1, 0, 0)));
        var allowed = evaluator.Evaluate(Request(["read"], [], [Rule("target", "read", AuthorizationEffect.Allow, "policy-v2")], revisions: new(0, 2, 0, 0)));

        denied.Decisions.Single().Allowed.Should().BeFalse();
        allowed.Decisions.Single().Allowed.Should().BeTrue();
        allowed.Revisions.Policy.Should().Be(2);
    }

    [Fact]
    public void Precise_invalidation_refreshes_only_named_project_cache_keys()
    {
        var evaluator = new EffectiveRightsEvaluator();
        var revision = new SecurityRevisionVector(1, 1, 1, 1);
        var deniedRequest = Request(["read"], [], [Rule("target", "read", AuthorizationEffect.Deny, "old")], revisions: revision);
        var allowedRequest = Request(["read"], [], [Rule("target", "read", AuthorizationEffect.Allow, "new")], revisions: revision);

        evaluator.Evaluate(deniedRequest).Decisions.Single().Allowed.Should().BeFalse();
        evaluator.Evaluate(allowedRequest).Decisions.Single().Allowed.Should().BeFalse("an unchanged revision must not silently replace cached authority");
        evaluator.InvalidateProjects(["target"]);
        evaluator.Evaluate(allowedRequest).Decisions.Single().Allowed.Should().BeTrue();
    }

    [Fact]
    public void Authorization_cycle_fails_closed_and_stale_mutation_revision_is_rejected()
    {
        var evaluator = new EffectiveRightsEvaluator();
        var act = () => evaluator.Evaluate(Request(["read"], [Edge("a", "target"), Edge("target", "a")], []));
        act.Should().Throw<InvalidOperationException>().WithMessage("*failed closed*");

        var stale = () => AuthorizationTopologyValidator.ValidateMutation([], Edge("a", "target", revision: 4), expectedRevision: 1);
        stale.Should().Throw<InvalidOperationException>().WithMessage("*revision conflict*");
    }

    [Fact]
    public void Canonical_tag_resolution_filters_acl_before_ranking_and_does_not_grant_rights()
    {
        var definition = new CanonicalTagDefinition { Id = Guid.NewGuid(), ProjectId = "target", CanonicalName = "Platform Security", NormalizedName = "platform-security" };
        var resolver = new CanonicalTagResolver();
        var visibleKey = CanonicalTagResolver.ResourceKey("target", "Memory", "visible");
        var result = resolver.Resolve(
            "security-platform",
            [definition],
            [new CanonicalTagAlias { DefinitionId = definition.Id, Alias = "security platform", NormalizedAlias = "security-platform" }],
            [
                new CanonicalTagBinding { DefinitionId = definition.Id, ProjectId = "target", ResourceType = "Memory", ResourceId = "secret" },
                new CanonicalTagBinding { DefinitionId = definition.Id, ProjectId = "target", ResourceType = "Memory", ResourceId = "visible" }
            ],
            new HashSet<string>([visibleKey], StringComparer.Ordinal));

        result.Candidates.Single().MatchKind.Should().Be("Alias");
        result.Resources.Should().ContainSingle(x => x.ResourceId == "visible");
        result.Resources.Should().NotContain(x => x.ResourceId == "secret");
        typeof(CanonicalTagResolution).GetProperties().Select(x => x.Name).Should().NotContain("Rights");
    }

    private static AuthorizationTopologyEdge Edge(string parent, string child, long revision = 1)
        => new(parent, child, "authorization", true, revision);

    private static AuthorizationRule Rule(string project, string right, AuthorizationEffect effect, string evidence, string? resourceType = null, string? resourceId = null)
        => new(project, "agent", right, effect, AuthorizationRuleSource.PolicyDerived, evidence, resourceType, resourceId);

    private static EffectiveRightsRequest Request(
        IReadOnlyList<string> rights,
        IReadOnlyList<AuthorizationTopologyEdge> edges,
        IReadOnlyList<AuthorizationRule> rules,
        string? resourceType = null,
        string? resourceId = null,
        SecurityRevisionVector? revisions = null)
        => new("target", "agent", rights, edges, rules, revisions ?? new(1, 1, 1, 1), resourceType, resourceId);
}

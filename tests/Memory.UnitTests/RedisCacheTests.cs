using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class RedisCacheTests
{
    private static readonly CacheVersionStamp Version = new("test", 1, 1, 1, 1, new Dictionary<string, long>());

    [Theory]
    [InlineData(MemoryQueryMode.CurrentPlusReferencedProjects)]
    [InlineData(MemoryQueryMode.SummaryOnly)]
    public void WorkingContextKey_Should_Separate_Primary_Project_Even_When_Retrieval_Projects_Match(MemoryQueryMode mode)
    {
        var request = new WorkingContextRequest("query", ProjectId: "A", QueryMode: mode);
        var a = RedisCacheKeyBuilder.WorkingContext(Version, request, ContextHubRequestActor.Unrestricted, ["A", "B"], "model");
        var b = RedisCacheKeyBuilder.WorkingContext(Version, request with { ProjectId = "B" }, ContextHubRequestActor.Unrestricted, ["B", "A"], "model");
        a.Should().StartWith("cache:v2:context:").And.NotBe(b);
    }

    [Fact]
    public void ResultKeys_Should_Separate_Service_Mode_Role_And_Anonymous_Scope()
    {
        var user = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "user", TenantUserRole.Member,
            [SecurityScopes.MemoryRead], ["A"], true);
        var request = new MemorySearchRequest("query", ProjectId: "A");
        string Key(ContextHubRequestActor actor) => RedisCacheKeyBuilder.Search(Version, request, actor, ["A"], "model");
        Key(user).Should().NotBe(Key(user with { IsServiceActor = true }));
        Key(user).Should().NotBe(Key(user with { Role = TenantUserRole.Admin }));
        Key(ContextHubRequestActor.Unrestricted).Should().NotBe(Key(ContextHubRequestActor.Unrestricted with { IsServiceActor = true }));
        RedisCacheKeyBuilder.SemanticHits(Version, "model", "query", 10, user, ["A"])
            .Should().NotBe(RedisCacheKeyBuilder.SemanticHits(Version, "model", "query", 10, user with { IsServiceActor = true }, ["A"]));
    }

    [Fact]
    public void ActorIdentity_Should_Canonicalize_Order_But_Not_Collide_On_Separators()
    {
        var actor = new ContextHubRequestActor(Guid.NewGuid(), Guid.NewGuid(), "user", TenantUserRole.Member,
            ["b", "a", "A"], ["B", "A"], true);
        RedisCacheKeyBuilder.Actor(actor).Should().Be(RedisCacheKeyBuilder.Actor(actor with { Scopes = ["a", "b"], AllowedProjectIds = ["A", "B"] }));
        RedisCacheKeyBuilder.ProjectSet(["A|B"]).Should().NotBe(RedisCacheKeyBuilder.ProjectSet(["A", "B"]));
        RedisCacheKeyBuilder.Actor(actor with { Scopes = ["a,b"] }).Should().NotBe(RedisCacheKeyBuilder.Actor(actor with { Scopes = ["a", "b"] }));
    }

    [Fact]
    public void DashboardKeys_Should_Use_Structured_Field_Boundaries()
    {
        var actor = ContextHubRequestActor.Unrestricted;
        var first = new MemoryListRequest(SourceType: "a:b", Tag: "c");
        var second = first with { SourceType = "a", Tag = "b:c" };
        RedisCacheKeyBuilder.DashboardMemories(Version, first, actor)
            .Should().NotBe(RedisCacheKeyBuilder.DashboardMemories(Version, second, actor));
        RedisCacheKeyBuilder.DashboardLogs(new LogQueryRequest(Query: "a:b", ServiceName: "c"), actor)
            .Should().NotBe(RedisCacheKeyBuilder.DashboardLogs(new LogQueryRequest(Query: "a", ServiceName: "b:c"), actor));
    }

    [Fact]
    public void SearchKey_Should_Separate_Actor_Project_Query_And_Model_Scope()
    {
        var version = new CacheVersionStamp("g=1;s=1;u=1;p=ContextHub:1", 1, 1, 0, 1, new Dictionary<string, long>
        {
            ["ContextHub"] = 1
        });
        var actor = new ContextHubRequestActor(
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb"),
            "alice",
            TenantUserRole.Member,
            [SecurityScopes.MemoryRead],
            ["ContextHub"],
            true);
        var request = new MemorySearchRequest(
            "redis cache",
            10,
            false,
            "ContextHub",
            null,
            MemoryQueryMode.CurrentPlusReferencedProjects,
            false);

        var baseline = RedisCacheKeyBuilder.Search(version, request, actor, ["ContextHub"], "model-a");
        var otherModel = RedisCacheKeyBuilder.Search(version, request, actor, ["ContextHub"], "model-b");
        var otherProject = RedisCacheKeyBuilder.Search(version, request, actor, ["OtherProject"], "model-a");
        var otherQuery = RedisCacheKeyBuilder.Search(version, request with { Query = "redis cache miss" }, actor, ["ContextHub"], "model-a");
        var otherActor = RedisCacheKeyBuilder.Search(version, request, actor with { UserId = Guid.Parse("cccccccc-cccc-cccc-cccc-cccccccccccc") }, ["ContextHub"], "model-a");

        baseline.Should().NotBe(otherModel);
        baseline.Should().NotBe(otherProject);
        baseline.Should().NotBe(otherQuery);
        baseline.Should().NotBe(otherActor);
        baseline.Should().NotBe(RedisCacheKeyBuilder.Search(version, request, actor, ["contexthub"], "model-a"),
            "case-sensitive database selectors must not share final results");
    }

    [Fact]
    public void ProjectSet_Should_Normalize_Deduplicate_By_Key_Inputs()
    {
        var first = RedisCacheKeyBuilder.ProjectSet([" contextHub ", "Shared", "ContextHub"]);
        var second = RedisCacheKeyBuilder.ProjectSet(["shared", "ContextHub"]);

        first.Should().Be(second);
    }

    [Fact]
    public void RedisCacheTelemetry_Should_Track_Total_And_Per_Kind_Counters()
    {
        var telemetry = new RedisCacheTelemetry();

        telemetry.RecordHit("search-final");
        telemetry.RecordMiss("search-final");
        telemetry.RecordSet("semantic-hits");
        telemetry.RecordBypass("semantic-hits");
        telemetry.RecordError("semantic-hits");
        telemetry.RecordInvalidPayload("search-final");

        var snapshot = telemetry.GetSnapshot();

        snapshot.Hits.Should().Be(1);
        snapshot.Misses.Should().Be(1);
        snapshot.Sets.Should().Be(1);
        snapshot.Bypasses.Should().Be(1);
        snapshot.Errors.Should().Be(1);
        snapshot.InvalidPayloads.Should().Be(1);
        snapshot.Kinds["search-final"].InvalidPayloads.Should().Be(1);
        snapshot.Kinds["search-final"].Hits.Should().Be(1);
        snapshot.Kinds["search-final"].Misses.Should().Be(1);
        snapshot.Kinds["semantic-hits"].Sets.Should().Be(1);
        snapshot.Kinds["semantic-hits"].Bypasses.Should().Be(1);
        snapshot.Kinds["semantic-hits"].Errors.Should().Be(1);
    }
}

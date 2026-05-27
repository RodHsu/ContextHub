using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class RedisCacheTests
{
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

        var snapshot = telemetry.GetSnapshot();

        snapshot.Hits.Should().Be(1);
        snapshot.Misses.Should().Be(1);
        snapshot.Sets.Should().Be(1);
        snapshot.Bypasses.Should().Be(1);
        snapshot.Errors.Should().Be(1);
        snapshot.Kinds["search-final"].Hits.Should().Be(1);
        snapshot.Kinds["search-final"].Misses.Should().Be(1);
        snapshot.Kinds["semantic-hits"].Sets.Should().Be(1);
        snapshot.Kinds["semantic-hits"].Bypasses.Should().Be(1);
        snapshot.Kinds["semantic-hits"].Errors.Should().Be(1);
    }
}

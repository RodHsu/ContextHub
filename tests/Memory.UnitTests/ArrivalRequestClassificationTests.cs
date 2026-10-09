using System.Text.Json;
using Memory.Infrastructure;

namespace Memory.UnitTests;

public sealed class ArrivalRequestClassificationTests
{
    [Theory]
    [InlineData(null, "unknown")]
    [InlineData(-1L, "unknown")]
    [InlineData(0L, "empty")]
    [InlineData(1L, "le1k")]
    [InlineData(1024L, "le1k")]
    [InlineData(1025L, "le16k")]
    [InlineData(16384L, "le16k")]
    [InlineData(16385L, "le256k")]
    [InlineData(262144L, "le256k")]
    [InlineData(262145L, "le4m")]
    [InlineData(4194304L, "le4m")]
    [InlineData(4194305L, "gt4m")]
    [InlineData(long.MaxValue, "gt4m")]
    public void Content_length_has_only_fixed_buckets(long? length, string bucket)
        => Assert.Equal(bucket, ArrivalRequestClassification.ForHttp("POST", "/api/context/build", length).PayloadSizeBucket);

    [Theory]
    [InlineData("GET", "/api/memories/search", "memory-read")]
    [InlineData("POST", "/api/context/build", "context-read")]
    [InlineData("GET", "/api/projects/information/{projectId}", "project-read")]
    [InlineData("PUT", "/api/projects/information/{projectId}", "project-write")]
    [InlineData("GET", "/api/work-items", "work-item-read")]
    [InlineData("POST", "/api/work-items", "work-item-write")]
    [InlineData("GET", "/api/dashboard/runtime", "diagnostics-read")]
    [InlineData("GET", "/api/dashboard/cache-metrics", "diagnostics-read")]
    [InlineData("POST", "/api/discussions/threads", "discussion-write")]
    [InlineData("GET", "/api/security/tenants", "security-read")]
    [InlineData("POST", "/api/agent-executions/prepare", "agent-write")]
    [InlineData("POST", "/api/projects/information/{projectId}", "unknown")]
    [InlineData("GET", "/api/projects/information/SECRET_PROJECT", "unknown")]
    [InlineData("GET", "/api/memories/search?query=SECRET_QUERY", "unknown")]
    [InlineData("GET", "/api/future/SECRET_PATH", "unknown")]
    [InlineData("SECRET_METHOD", "/api/memories/search", "unknown")]
    [InlineData("get", "/api/memories/search", "unknown")]
    public void Only_known_method_and_authored_endpoint_template_pair_is_classified(string method, string route, string family)
    {
        var result = ArrivalRequestClassification.ForHttp(method, route, 1);
        Assert.Equal(family, result.Family);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
        Assert.DoesNotContain(route, JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("memory_search", "memory-read")]
    [InlineData("memory_upsert", "memory-write")]
    [InlineData("build_working_context", "context-read")]
    [InlineData("projects_list", "project-read")]
    [InlineData("project_work_item_create", "work-item-write")]
    [InlineData("project_work_items_list", "work-item-read")]
    [InlineData("governance_batch_execute", "governance-write")]
    [InlineData("scheduled_governance_run_get", "governance-read")]
    [InlineData("memory_search_SECRET_TOOL", "unknown")]
    [InlineData("Memory_search", "unknown")]
    [InlineData(null, "unknown")]
    public void Tool_classification_is_exact_and_does_not_reuse_http_envelope_size(string? toolName, string family)
    {
        var result = ArrivalRequestClassification.ForTool(toolName);
        Assert.Equal(family, result.Family);
        Assert.Equal("UNKNOWN", result.Method);
        Assert.Equal("unknown", result.PayloadSizeBucket);
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Untrusted_metadata_is_replaced_and_never_serialized()
    {
        var result = ArrivalRequestClassification.Normalize(new("SECRET_FAMILY", "SECRET_METHOD", "SECRET_LENGTH"));
        Assert.Equal(new ArrivalRequestClassification("unknown", "UNKNOWN", "unknown"), result);
        Assert.Equal(result, ArrivalRequestClassification.Normalize(null));
        Assert.DoesNotContain("SECRET", JsonSerializer.Serialize(result));
    }

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public void Mcp_envelope_preserves_fixed_method_without_recording_route(string method)
    {
        var result = ArrivalRequestClassification.ForHttp(method, null, null, mcpEnvelope: true);
        Assert.Equal(new ArrivalRequestClassification("http-mcp", method, "unknown"), result);
    }
}

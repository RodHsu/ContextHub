using System.Text.Json.Nodes;
using FluentAssertions;
using Memory.McpTransport;

namespace Memory.McpProtocolTests;

public sealed class McpProtocolCompatibilityTests
{
    [Fact]
    public void Legacy_Request_With_Ordinary_Metadata_Should_Pass_Unchanged()
    {
        var request = ParseRequest("""
            {"jsonrpc":"2.0","id":1,"method":"tools/list","params":{"_meta":{"progressToken":"p-1"}}}
            """);

        var result = McpProtocolCompatibilityMiddleware.Normalize(request, "2025-11-25", hasSessionHeader: false);

        result.Success.Should().BeTrue();
        result.ChangedBody.Should().BeFalse();
        request.ToJsonString().Should().Contain("progressToken");
    }

    [Fact]
    public void ChatGpt_Legacy_Request_Shape_Should_Remove_Only_Reserved_Per_Request_Metadata()
    {
        var request = ParseRequest("""
            {
              "jsonrpc":"2.0",
              "id":2,
              "method":"tools/call",
              "params":{
                "name":"log_search",
                "arguments":{"request":{"query":"protocol"}},
                "_meta":{
                  "progressToken":"p-2",
                  "io.modelcontextprotocol/protocolVersion":"2025-11-25",
                  "io.modelcontextprotocol/clientInfo":{"name":"ChatGPT","version":"1"},
                  "io.modelcontextprotocol/clientCapabilities":{}
                }
              }
            }
            """);

        var result = McpProtocolCompatibilityMiddleware.Normalize(request, "2025-11-25", hasSessionHeader: true);

        result.Success.Should().BeTrue();
        result.ChangedBody.Should().BeTrue();
        result.RemovedLegacySessionHeader.Should().BeTrue();
        var normalized = request.ToJsonString();
        normalized.Should().Contain("progressToken");
        normalized.Should().NotContain("io.modelcontextprotocol/clientCapabilities");
        normalized.Should().NotContain("io.modelcontextprotocol/clientInfo");
        normalized.Should().NotContain("io.modelcontextprotocol/protocolVersion");
        normalized.Should().NotContain(
            "The reserved per-request metadata key '_meta/io.modelcontextprotocol/clientCapabilities' is not valid with protocol version '2025-11-25'.");
    }

    [Fact]
    public void Legacy_Request_With_Conflicting_Modern_Metadata_Version_Should_Fail_Closed()
    {
        var request = ParseRequest("""
            {"jsonrpc":"2.0","id":3,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
            """);

        var result = McpProtocolCompatibilityMiddleware.Normalize(request, "2025-11-25", hasSessionHeader: false);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("protocol version mismatch");
    }

    [Fact]
    public void Modern_Request_Should_Preserve_Required_Per_Request_Metadata()
    {
        var request = ParseRequest("""
            {"jsonrpc":"2.0","id":4,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientInfo":{"name":"client","version":"1"},"io.modelcontextprotocol/clientCapabilities":{}}}}
            """);
        var original = request.ToJsonString();

        var result = McpProtocolCompatibilityMiddleware.Normalize(request, "2026-07-28", hasSessionHeader: false);

        result.Success.Should().BeTrue();
        result.ChangedBody.Should().BeFalse();
        request.ToJsonString().Should().Be(original);
    }

    [Fact]
    public void Modern_Request_Without_Header_Should_Fail_Closed()
    {
        var request = ParseRequest("""
            {"jsonrpc":"2.0","id":5,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}
            """);

        var result = McpProtocolCompatibilityMiddleware.Normalize(request, null, hasSessionHeader: false);

        result.Success.Should().BeFalse();
        result.Error.Should().Contain("requires matching header");
    }

    [Fact]
    public void Stale_Legacy_Session_Should_Be_Normalized_Without_Protocol_Drift()
    {
        var request = ParseRequest("""
            {"jsonrpc":"2.0","id":6,"method":"tools/list","params":{"_meta":{"io.modelcontextprotocol/clientCapabilities":{}}}}
            """);

        var result = McpProtocolCompatibilityMiddleware.Normalize(request, null, hasSessionHeader: true);

        result.Success.Should().BeTrue();
        result.ProtocolVersion.Should().Be("2025-11-25");
        result.RemovedLegacySessionHeader.Should().BeTrue();
        request.ToJsonString().Should().NotContain("io.modelcontextprotocol/clientCapabilities");
    }

    private static JsonObject ParseRequest(string json)
        => JsonNode.Parse(json)!.AsObject();
}

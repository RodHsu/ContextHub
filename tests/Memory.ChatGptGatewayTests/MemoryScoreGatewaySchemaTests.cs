using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.ChatGptGateway;
using ModelContextProtocol.Server;

namespace Memory.ChatGptGatewayTests;

public sealed class MemoryScoreGatewaySchemaTests
{
    [Theory]
    [InlineData("memory_upsert")]
    [InlineData("memory_update")]
    [InlineData("user_preference_upsert")]
    public void Proposal_tool_schemas_publish_canonical_score_bounds(string toolName)
    {
        var method = typeof(ChatGptGatewayTools).GetMethod(toolName);
        method.Should().NotBeNull();

        var target = (ChatGptGatewayTools)System.Runtime.CompilerServices.RuntimeHelpers
            .GetUninitializedObject(typeof(ChatGptGatewayTools));
        var tool = McpServerTool.Create(method!, target, new McpServerToolCreateOptions());
        var requestSchema = JsonSerializer.SerializeToElement(tool.ProtocolTool)
            .GetProperty("inputSchema")
            .GetProperty("properties")
            .GetProperty("request")
            .GetProperty("properties");

        foreach (var field in new[] { "importance", "confidence" })
        {
            var property = requestSchema.GetProperty(field);
            property.GetProperty("minimum").GetDouble().Should().Be(0d);
            property.GetProperty("maximum").GetDouble().Should().Be(1d);
        }
    }
}

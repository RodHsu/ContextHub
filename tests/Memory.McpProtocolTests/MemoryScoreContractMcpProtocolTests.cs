using System.ComponentModel.DataAnnotations;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using FluentAssertions;
using Memory.McpServer;
using ModelContextProtocol.Server;

namespace Memory.McpProtocolTests;

public sealed class MemoryScoreContractMcpProtocolTests
{
    [Theory]
    [InlineData("memory_upsert", typeof(MemoryUpsertToolRequest))]
    [InlineData("memory_update", typeof(MemoryUpdateToolRequest))]
    [InlineData("user_preference_upsert", typeof(UserPreferenceUpsertToolRequest))]
    public void Numeric_mcp_tool_request_types_publish_minimum_and_maximum(
        string toolName,
        Type requestType)
    {
        var method = typeof(MemoryMcpTools).GetMethod(toolName);
        method.Should().NotBeNull();
        method!.GetParameters().Should().ContainSingle(parameter => parameter.ParameterType == requestType);

        AssertRange(requestType, "Importance");
        AssertRange(requestType, "Confidence");
    }

    [Theory]
    [InlineData("memory_upsert")]
    [InlineData("memory_update")]
    [InlineData("user_preference_upsert")]
    public void Numeric_mcp_tool_schemas_publish_minimum_and_maximum(string toolName)
    {
        var method = typeof(MemoryMcpTools).GetMethod(toolName);
        method.Should().NotBeNull();

        var target = (MemoryMcpTools)RuntimeHelpers.GetUninitializedObject(typeof(MemoryMcpTools));
        var tool = McpServerTool.Create(method!, target, new McpServerToolCreateOptions());
        var schema = JsonSerializer.SerializeToElement(tool.ProtocolTool)
            .GetProperty("inputSchema")
            .GetProperty("properties")
            .GetProperty("request");

        foreach (var field in new[] { "importance", "confidence" })
        {
            var property = schema.GetProperty("properties").GetProperty(field);
            property.GetProperty("minimum").GetDouble().Should().Be(0d);
            property.GetProperty("maximum").GetDouble().Should().Be(1d);
        }
    }

    private static void AssertRange(Type requestType, string propertyName)
    {
        var property = requestType.GetProperty(propertyName);
        property.Should().NotBeNull();

        var range = property!.GetCustomAttribute<RangeAttribute>();
        range.Should().NotBeNull();
        Convert.ToDecimal(range!.Minimum, System.Globalization.CultureInfo.InvariantCulture).Should().Be(0m);
        Convert.ToDecimal(range.Maximum, System.Globalization.CultureInfo.InvariantCulture).Should().Be(1m);
    }
}

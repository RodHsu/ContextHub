using System.Text.Json;
using FluentAssertions;
using Memory.Application;

namespace Memory.UnitTests;

public sealed class McpToolCallTelemetryTests
{
    [Fact]
    public void ResolveGovernanceRunId_Should_Read_TopLevel_And_Wrapped_Arguments()
    {
        var topLevel = new Dictionary<string, JsonElement>
        {
            ["governanceRunId"] = JsonSerializer.SerializeToElement("run-top")
        };
        var wrapped = new Dictionary<string, JsonElement>
        {
            ["request"] = JsonSerializer.SerializeToElement(new { governanceRunId = "run-wrapped" })
        };

        McpToolCallTelemetry.ResolveGovernanceRunId(topLevel).Should().Be("run-top");
        McpToolCallTelemetry.ResolveGovernanceRunId(wrapped).Should().Be("run-wrapped");
        McpToolCallTelemetry.ResolveGovernanceRunId(null).Should().BeEmpty();
    }

    [Fact]
    public void ResolveGovernanceRunId_Should_Not_Emit_Unbounded_Or_Control_Character_Input()
    {
        var controlCharacters = new Dictionary<string, JsonElement>
        {
            ["governanceRunId"] = JsonSerializer.SerializeToElement("run-ok\r\nforged-log=true")
        };
        var unbounded = new Dictionary<string, JsonElement>
        {
            ["governanceRunId"] = JsonSerializer.SerializeToElement(new string('a', 129))
        };

        McpToolCallTelemetry.ResolveGovernanceRunId(controlCharacters).Should().BeEmpty();
        McpToolCallTelemetry.ResolveGovernanceRunId(unbounded).Should().BeEmpty();
    }

    [Fact]
    public void ResolveProjectId_Should_Read_Direct_ProjectId()
    {
        var arguments = ParseArguments("""{"projectId":"ContextHub"}""");

        McpToolCallTelemetry.ResolveProjectId(arguments).Should().Be("ContextHub");
    }

    [Fact]
    public void ResolveProjectId_Should_Read_Nested_Request_ProjectId()
    {
        var arguments = ParseArguments("""{"request":{"projectId":"Vital_AirMeet"}}""");

        McpToolCallTelemetry.ResolveProjectId(arguments).Should().Be("Vital_AirMeet");
    }

    [Fact]
    public void ResolveProjectId_Should_Fall_Back_To_Default_When_Not_Present()
    {
        var arguments = ParseArguments("""{"request":{"query":"test"}}""");

        McpToolCallTelemetry.ResolveProjectId(arguments).Should().Be(ProjectContext.DefaultProjectId);
    }

    private static IDictionary<string, JsonElement> ParseArguments(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement
            .EnumerateObject()
            .ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal);
    }
}

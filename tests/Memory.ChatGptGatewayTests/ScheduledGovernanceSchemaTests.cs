using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.ChatGptGateway;
using Memory.Tests.Shared;
using ModelContextProtocol.Server;

namespace Memory.ChatGptGatewayTests;

public sealed class ScheduledGovernanceSchemaTests
{
    [Fact]
    public void Catalog_Should_Expose_Only_Dedicated_Tools_And_No_Irreversible_Authority()
    {
        var target = new ScheduledGovernanceTools(new StubScheduledGovernanceService());
        var methods = typeof(ScheduledGovernanceTools).GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: true).Length > 0)
            .ToArray();

        methods.Select(method => method.Name).Should().BeEquivalentTo(
            ScheduledGovernanceToolCatalog.PublishedToolNames);

        foreach (var method in methods)
        {
            var tool = McpServerTool.Create(method, target, new McpServerToolCreateOptions());
            var element = JsonSerializer.SerializeToElement(tool.ProtocolTool);
            var json = element.GetRawText();
            json.Should().NotContainAny(
                "allowHardDelete", "allowMaturedDelete", "allowedActionTypes", "MaturedDelete",
                "memory_delete", "executionMode", "maxRiskLevel", "dryRun",
                "autoDeleted", "deleteEligible", "deleteMatured", "deleteCancelled", "tombstone");
            element.GetProperty("inputSchema").GetRawText().Should().NotContain("projectIds");
        }
    }

    [Fact]
    public void Execute_Schema_Should_Match_Versioned_Contract()
    {
        var target = new ScheduledGovernanceTools(new StubScheduledGovernanceService());
        var method = typeof(ScheduledGovernanceTools).GetMethod(
            nameof(ScheduledGovernanceTools.scheduled_governance_execute))!;
        var tool = McpServerTool.Create(method, target, new McpServerToolCreateOptions());
        var element = JsonSerializer.SerializeToElement(tool.ProtocolTool);

        PublishedToolSchemaHash.Compute(element).Should().Be(ScheduledGovernanceContract.SchemaHash);
        var request = element.GetProperty("inputSchema").GetProperty("properties").GetProperty("request");
        request.GetProperty("properties").EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo([
            "governanceRunId", "snapshotToken", "cursor", "maxMutations", "maxDurationSeconds",
            "isReReview", "toolContractVersion", "schemaHash"
        ]);
        element.GetRawText().Should().NotContainAny(
            "allowHardDelete", "allowMaturedDelete", "allowedActionTypes", "MaturedDelete",
            "memory_delete", "projectIds", "executionMode", "maxRiskLevel", "dryRun");
        var annotations = element.GetProperty("annotations");
        annotations.GetProperty("readOnlyHint").GetBoolean().Should().BeFalse();
        annotations.GetProperty("destructiveHint").GetBoolean().Should().BeFalse();
        annotations.GetProperty("idempotentHint").GetBoolean().Should().BeTrue();
        annotations.GetProperty("openWorldHint").GetBoolean().Should().BeFalse();
    }

    private sealed class StubScheduledGovernanceService : IScheduledGovernanceService
    {
        public Task<ScheduledGovernanceReviewResult> ReviewAsync(
            ScheduledGovernanceReviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ScheduledGovernanceExecutionResult> ExecuteAsync(
            ScheduledGovernanceExecuteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ScheduledGovernanceRunResult?> GetReceiptAsync(
            string governanceRunId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

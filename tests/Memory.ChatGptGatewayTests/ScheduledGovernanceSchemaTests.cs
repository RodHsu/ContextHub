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

    [Fact]
    public void Review_And_Run_Get_Should_Publish_Explicit_ReadOnly_Recovery_Contracts()
    {
        var target = new ScheduledGovernanceTools(new StubScheduledGovernanceService());
        var methods = typeof(ScheduledGovernanceTools).GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: true).Length > 0)
            .ToDictionary(method => method.Name, StringComparer.Ordinal);

        var review = JsonSerializer.SerializeToElement(McpServerTool.Create(
            methods[ScheduledGovernanceContract.ReviewToolName], target, new McpServerToolCreateOptions()).ProtocolTool);
        review.GetProperty("description").GetString().Should().StartWith(
            "Read a full-governance snapshot without modifying, moving, archiving, or deleting governed resources.");
        var reviewAnnotations = review.GetProperty("annotations");
        reviewAnnotations.GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
        reviewAnnotations.GetProperty("destructiveHint").GetBoolean().Should().BeFalse();
        reviewAnnotations.GetProperty("openWorldHint").GetBoolean().Should().BeFalse();

        var runGet = JsonSerializer.SerializeToElement(McpServerTool.Create(
            methods[ScheduledGovernanceContract.ReceiptToolName], target, new McpServerToolCreateOptions()).ProtocolTool);
        var outputProperties = runGet.GetProperty("outputSchema").GetProperty("properties");
        outputProperties.TryGetProperty("runExists", out _).Should().BeTrue();
        outputProperties.TryGetProperty("received", out _).Should().BeTrue();
        outputProperties.TryGetProperty("terminal", out _).Should().BeTrue();
        outputProperties.TryGetProperty("decision", out _).Should().BeTrue();
        outputProperties.TryGetProperty("outcome", out _).Should().BeTrue();
    }

    [Fact]
    public void Contract_Get_Should_Expose_Runtime_Build_Identity()
    {
        var target = new ScheduledGovernanceTools(new StubScheduledGovernanceService());

        var contract = target.scheduled_governance_contract_get();
        contract.RuntimeIdentity.Should().NotBeNull();
        contract.RuntimeIdentity!.ServiceName.Should().Be(ScheduledGovernanceContract.RuntimeServiceName);
        contract.RuntimeIdentity.BuildVersion.Should().Be(BuildMetadata.Current.Version);
        contract.RuntimeIdentity.BuildTimestampUtc.Should().Be(BuildMetadata.Current.TimestampUtc);
        contract.RuntimeIdentity.DerivedIdentity.Should().Contain(contract.RuntimeIdentity.BuildVersion);
        contract.RuntimeIdentity.DerivedIdentity.Should().Contain(ScheduledGovernanceContract.PublishedCatalogVersion);

        var json = JsonSerializer.SerializeToElement(contract, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        json.GetProperty("runtimeIdentity").GetProperty("buildVersion").GetString()
            .Should().Be(BuildMetadata.Current.Version);
        json.GetProperty("runtimeIdentity").GetProperty("buildTimestampUtc").GetDateTimeOffset()
            .Should().Be(BuildMetadata.Current.TimestampUtc);
        json.GetProperty("runtimeIdentity").GetProperty("derivedIdentity").GetString()
            .Should().Be(contract.RuntimeIdentity.DerivedIdentity);
    }

    private sealed class StubScheduledGovernanceService : IScheduledGovernanceService
    {
        public Task<ScheduledGovernanceReviewResult> ReviewAsync(
            ScheduledGovernanceReviewRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ScheduledGovernanceExecutionResult> ExecuteAsync(
            ScheduledGovernanceExecuteRequest request,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<ScheduledGovernanceRunResult> GetReceiptAsync(
            string governanceRunId,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}

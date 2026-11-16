using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using FluentAssertions;
using Memory.Application;
using Memory.ChatGptGateway;
using Memory.Tests.Shared;
using ModelContextProtocol.Server;

namespace Memory.ChatGptGatewayTests;

public sealed class ScheduledGovernanceSchemaTests
{
    [Fact]
    public void Canonical_Automation_Artifact_Should_Match_Runtime_Catalog_And_All_Tool_Schemas()
    {
        var target = new ScheduledGovernanceTools(new StubScheduledGovernanceService());
        var tools = typeof(ScheduledGovernanceTools).GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: true).Length > 0)
            .Select(method => JsonSerializer.SerializeToElement(
                McpServerTool.Create(method, target, new McpServerToolCreateOptions()).ProtocolTool))
            .ToArray();
        var repoRoot = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        using var artifact = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repoRoot,
            "tools",
            "automation",
            "scheduled-governance-automation.json")));
        var catalog = artifact.RootElement.GetProperty("catalog");
        var contract = artifact.RootElement.GetProperty("contract");

        catalog.GetProperty("toolNames").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(
            ScheduledGovernanceToolCatalog.PublishedToolNames);
        catalog.GetProperty("publishedCatalogHash").GetString().Should().Be(
            ScheduledGovernanceToolCatalog.PublishedCatalogHash);
        catalog.GetProperty("publishedCatalogVersion").GetString().Should().Be(
            ScheduledGovernanceContract.PublishedCatalogVersion);
        contract.GetProperty("toolContractVersion").GetString().Should().Be(
            ScheduledGovernanceContract.ToolContractVersion);
        contract.GetProperty("schemaHash").GetString().Should().Be(
            ScheduledGovernanceContract.SchemaHash);
        PublishedToolSchemaHash.ComputeCatalog(tools).Should().Be(
            catalog.GetProperty("publishedToolSchemasHash").GetString());
    }

    [Fact]
    public void Catalog_Should_Expose_Only_Dedicated_Tools_And_No_Irreversible_Authority()
    {
        var target = new ScheduledGovernanceTools(new StubScheduledGovernanceService());
        var methods = typeof(ScheduledGovernanceTools).GetMethods()
            .Where(method => method.GetCustomAttributes(typeof(McpServerToolAttribute), inherit: true).Length > 0)
            .ToArray();

        methods.Select(method => method.Name).Should().BeEquivalentTo(
            ScheduledGovernanceToolCatalog.PublishedToolNames);
        methods.Should().HaveCount(4);
        ScheduledGovernanceContract.ToolContractVersion.Should().Be("1.5");
        ScheduledGovernanceContract.PublishedCatalogVersion.Should().Be("2026-10-04-automation-v11");
        ScheduledGovernanceContract.FixedReversibleActions.Should().Contain(GovernanceBatchActionType.SkillMetadataProposal);

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
        var reviewOutputProperties = review.GetProperty("outputSchema").GetProperty("properties");
        reviewOutputProperties.TryGetProperty("skillCoverage", out _).Should().BeTrue();
        reviewOutputProperties.TryGetProperty("skillSignalCounts", out _).Should().BeTrue();
        var reviewAnnotations = review.GetProperty("annotations");
        reviewAnnotations.GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
        reviewAnnotations.GetProperty("destructiveHint").GetBoolean().Should().BeFalse();
        reviewAnnotations.GetProperty("openWorldHint").GetBoolean().Should().BeFalse();

        var runGet = JsonSerializer.SerializeToElement(McpServerTool.Create(
            methods[ScheduledGovernanceContract.ReceiptToolName], target, new McpServerToolCreateOptions()).ProtocolTool);
        var outputProperties = runGet.GetProperty("outputSchema").GetProperty("properties");
        outputProperties.GetProperty("serverInvariants").GetProperty("additionalProperties")
            .GetProperty("type").EnumerateArray().Select(x => x.GetString()).Should().Contain("null");
        var proofProperties = outputProperties.GetProperty("serverInvariantProofs")
            .GetProperty("additionalProperties").GetProperty("properties");
        proofProperties.GetProperty("status").GetProperty("enum").EnumerateArray()
            .Select(x => x.GetString()).Should().BeEquivalentTo(Enum.GetNames<ScheduledGovernanceInvariantProofStatus>());
        proofProperties.TryGetProperty("reason", out _).Should().BeTrue();
        proofProperties.TryGetProperty("scope", out _).Should().BeTrue();
        outputProperties.TryGetProperty("runExists", out _).Should().BeTrue();
        outputProperties.TryGetProperty("received", out _).Should().BeTrue();
        outputProperties.TryGetProperty("terminal", out _).Should().BeTrue();
        outputProperties.TryGetProperty("decision", out _).Should().BeTrue();
        outputProperties.TryGetProperty("outcome", out _).Should().BeTrue();
        outputProperties.TryGetProperty("reliability", out var reliability).Should().BeTrue();
        outputProperties.TryGetProperty("skillCoverage", out _).Should().BeTrue();
        outputProperties.TryGetProperty("skillSignalCounts", out _).Should().BeTrue();
        var reliabilityProperties = reliability.GetProperty("properties");
        reliabilityProperties.TryGetProperty("consecutiveQualifyingRuns", out _).Should().BeTrue();
        reliabilityProperties.TryGetProperty("naturalOriginEvidence", out _).Should().BeTrue();
        reliabilityProperties.TryGetProperty("schedule", out _).Should().BeTrue();
        reliabilityProperties.TryGetProperty("resetEvents", out _).Should().BeTrue();
        var runProperties = reliabilityProperties.GetProperty("runs").GetProperty("items")
            .GetProperty("properties");
        foreach (var name in new[] { "surface", "dispatchProvenance", "provenanceTrusted",
                     "evidenceKind", "evidenceReferenceHash", "naturalScheduleSlotHash", "exclusionReason",
                     "streakBefore", "streakAfter", "resetReason", "serverInvariants", "serverInvariantProofs" })
        {
            runProperties.TryGetProperty(name, out _).Should().BeTrue();
        }
        runProperties.GetProperty("serverInvariantProofs").GetProperty("additionalProperties")
            .GetProperty("$ref").GetString().Should().Be("#/properties/serverInvariantProofs/additionalProperties");
    }

    internal static readonly string[] ScalarProofNames =
    [
        "initialReviewReceived", "countInvariantSatisfied", "decisionObeyed", "noGeneralConnectorFallback",
        "noUnauthorizedMutation", "noDuplicateMutation", "displayNameUnchanged", "businessWorkItemsUntouched",
        "hostDispatchCompleted", "immutableSnapshotBound", "fixedReversibleExecutorUsed"
    ];

    internal static void AssertScalarProofSchema(JsonElement schema)
    {
        AssertExactOrderAndHostBudget(schema);
        var properties = schema.GetProperty("properties");
        foreach (var name in ScalarProofNames)
        {
            properties.GetProperty(name).GetProperty("type").EnumerateArray().Select(x => x.GetString())
                .Should().BeEquivalentTo("boolean", "null");
            var status = properties.GetProperty(name + "Status");
            // System.Text.Json may omit redundant type when enum contains only strings.
            if (status.TryGetProperty("type", out var statusType))
                statusType.GetString().Should().Be("string");
            status.GetProperty("enum").EnumerateArray().Should().OnlyContain(x => x.ValueKind == JsonValueKind.String);
            status.GetProperty("enum").EnumerateArray().Select(x => x.GetString()).Should().BeEquivalentTo(
                "ProvenTrue", "ProvenFalse", "NotApplicable", "Unproven", "NotObservable");
            foreach (var suffix in new[] { "Reason", "Scope" })
            {
                var type = properties.GetProperty(name + suffix).GetProperty("type");
                var types = type.ValueKind == JsonValueKind.Array
                    ? type.EnumerateArray().Select(x => x.GetString()).ToArray() : [type.GetString()];
                types.Should().Contain("string").And.OnlyContain(x => x == "string" || x == "null");
            }
        }
    }

    // 42 is one observed legacy declaration size, not a platform contract.
    // The complete A1 prefix is 25 fields, leaving 17 slots below that observation.
    internal static void AssertExactOrderAndHostBudget(JsonElement schema)
    {
        var expected = ReadNames("RunGetSchemaOrder.json");
        var properties = schema.GetProperty("properties");
        properties.EnumerateObject().Select(x => x.Name).Should().Equal(expected);
        expected.Should().HaveCount(86);
        expected.Take(11).Should().Equal(ScalarProofNames.Select(x => x + "Status"));
        foreach (var budget in new[] { 25, 32, 42 })
        {
            var projected = ProjectHost(schema, budget);
            AssertA1(projected);
            projected.GetProperty("properties").EnumerateObject().Should().HaveCount(budget);
        }
    }

    private static string[] ReadNames(string file) => JsonSerializer.Deserialize<string[]>(File.ReadAllText(
        Path.Combine(AppContext.BaseDirectory, "..", "..", "..", file)))!;

    private static JsonElement ProjectHost(JsonElement schema, int budget)
    {
        var collapsed = CollapseComplexHostSchema(schema);
        // Simulate selection before the alphabetic presentation observed in the host declaration.
        var selected = collapsed.GetProperty("properties").EnumerateObject().Take(budget)
            .OrderBy(x => x.Name, StringComparer.Ordinal);
        var properties = new JsonObject();
        foreach (var property in selected)
            properties.Add(property.Name, JsonNode.Parse(property.Value.GetRawText()));
        return JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object", ["properties"] = properties });
    }

    private static void AssertA1(JsonElement schema)
    {
        var properties = schema.GetProperty("properties");
        foreach (var name in ScalarProofNames)
        {
            properties.GetProperty(name + "Status").GetProperty("enum").EnumerateArray()
                .Select(x => x.GetString()).Should().Equal(
                    "ProvenTrue", "ProvenFalse", "NotApplicable", "Unproven", "NotObservable");
        }
        foreach (var name in new[] { "receiptId", "governanceRunId", "runExists", "received", "decision",
                     "coverageComplete", "applied", "failed", "auditIds", "latestBatchReceived",
                     "requestIdentityHash", "toolContractVersion", "schemaHash", "publishedCatalogVersion" })
            properties.TryGetProperty(name, out _).Should().BeTrue(name + " is required by A1");
    }

    [Fact]
    public void Observed_Legacy_Host_Selection_Is_Not_A_Raw_Ordered_Prefix_And_Must_Fail_A1()
    {
        var observed = ReadNames("HostRegistryObservedNames.json");
        var baseline = ReadNames("RunGetSchema107Order.json");
        observed.Should().HaveCount(42);
        observed.Should().Equal(observed.Order(StringComparer.Ordinal));
        observed.Should().BeEquivalentTo(baseline.Where(x => !ScalarProofNames.Any(
            proof => x == proof || x == proof + "Status" || x == proof + "Reason" || x == proof + "Scope")));
        observed.Should().Contain(baseline[84]).And.Contain(baseline[85]);
        observed.Should().NotContain(baseline[40]).And.NotContain(baseline[41]);
        observed.Should().NotBeEquivalentTo(baseline.Take(42));
        var properties = new JsonObject();
        foreach (var name in observed) properties.Add(name, new JsonObject());
        var stale = JsonSerializer.SerializeToElement(new JsonObject { ["properties"] = properties });
        Action gate = () => AssertA1(stale);
        gate.Should().Throw<KeyNotFoundException>("an old registry cannot attest a new static contract");
    }

    internal static JsonElement CollapseComplexHostSchema(JsonElement schema)
    {
        var node = JsonNode.Parse(schema.GetRawText())!;
        var properties = node["properties"]!.AsObject();
        foreach (var property in properties.ToArray())
        {
            var value = property.Value!;
            if (value["properties"] is not null || value["additionalProperties"] is not null ||
                value["items"] is not null || value["$ref"] is not null)
            {
                properties[property.Key] = JsonNode.Parse("{\"type\":[\"object\",\"null\"]}");
            }
        }
        return JsonSerializer.SerializeToElement(node);
    }

    [Fact]
    public void Clr_And_Mcp_Scalar_Proof_Schema_Should_Survive_Complex_Host_Collapse()
    {
        var options = new JsonSerializerOptions(JsonSerializerOptions.Web);
        options.MakeReadOnly(populateMissingResolver: true);
        var clr = JsonSerializer.SerializeToElement(options.GetJsonSchemaAsNode(typeof(ScheduledGovernanceRunResult)));
        AssertScalarProofSchema(clr);
        var method = typeof(ScheduledGovernanceTools).GetMethod(nameof(ScheduledGovernanceTools.scheduled_governance_run_get))!;
        var tool = McpServerTool.Create(method, new ScheduledGovernanceTools(new StubScheduledGovernanceService()),
            new McpServerToolCreateOptions());
        var schema = JsonSerializer.SerializeToElement(tool.ProtocolTool).GetProperty("outputSchema");
        AssertScalarProofSchema(schema);
        var host = CollapseComplexHostSchema(schema);
        host.GetProperty("properties").GetProperty("serverInvariantProofs").TryGetProperty("additionalProperties", out _)
            .Should().BeFalse("the host deliberately erases complex structure");
        AssertScalarProofSchema(host);
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

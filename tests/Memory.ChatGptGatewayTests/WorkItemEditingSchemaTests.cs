extern alias mcpserver;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Memory.Application;
using Memory.ChatGptGateway;
using Memory.Domain;
using ModelContextProtocol;
using ModelContextProtocol.Server;
using BackendTools = mcpserver::Memory.McpServer.MemoryMcpTools;

namespace Memory.ChatGptGatewayTests;

public sealed class WorkItemEditingSchemaTests
{
    [Theory]
    [InlineData("project_work_item_update")]
    [InlineData("project_work_item_checklist_update")]
    [InlineData("project_work_item_archive")]
    [InlineData("project_work_item_restore")]
    [InlineData("project_work_item_set_governance_exclusion")]
    [InlineData("project_work_item_edit_receipt_get")]
    public void Actual_SDK_schemas_match_between_backend_and_gateway(string name)
    {
        var backend = Schema(typeof(BackendTools), name);
        var gateway = Schema(typeof(ChatGptGatewayTools), name);
        JsonNode.DeepEquals(JsonNode.Parse(backend.GetRawText()), JsonNode.Parse(gateway.GetRawText())).Should().BeTrue();
        var properties = gateway.GetProperty("properties");
        properties.TryGetProperty("cancellationToken", out _).Should().BeFalse();
        if (name == "project_work_item_edit_receipt_get")
        {
            var request = properties.GetProperty("request").GetProperty("properties");
            request.TryGetProperty("expectedRequestHash", out _).Should().BeTrue();
            request.TryGetProperty("hashVersion", out _).Should().BeTrue();
        }
        else
        {
            var request = properties.TryGetProperty("request", out var wrapped) ? wrapped.GetProperty("properties") : properties;
            request.TryGetProperty("expectedRevision", out _).Should().BeTrue();
            request.TryGetProperty("requestId", out _).Should().BeTrue();
        }
    }

    [Fact]
    public void Recovery_is_a_distinct_read_only_query_not_a_hidden_write_or_current_state_alias()
    {
        const string name = "project_work_item_edit_receipt_get";
        foreach (var type in new[] { typeof(BackendTools), typeof(ChatGptGatewayTools) })
        {
            var target = RuntimeHelpers.GetUninitializedObject(type);
            var tool = McpServerTool.Create(type.GetMethod(name)!, target, new McpServerToolCreateOptions());
            var metadata = JsonSerializer.SerializeToElement(tool.ProtocolTool);
            metadata.GetProperty("annotations").GetProperty("readOnlyHint").GetBoolean().Should().BeTrue();
            metadata.GetProperty("annotations").GetProperty("destructiveHint").GetBoolean().Should().BeFalse();
        }
        McpPublishedToolCatalog.QueryToolNames.Should().Contain(name);
        McpPublishedToolCatalog.RestrictedToolNames.Should().Contain(name);
        McpPublishedToolCatalog.DirectMutationToolNames.Should().NotContain(name);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Scalar_wrappers_forward_original_preconditions_into_typed_application_requests(bool gateway)
    {
        var service = new WorkItems(); var editing = new Editing(); var tools = Tools(gateway, service, editing);
        var id = Guid.NewGuid(); var child = Guid.NewGuid();
        await Invoke(tools, "project_work_item_archive", id, CancellationToken.None, 7L, "archive-key");
        service.Archive.Should().Be(new ProjectWorkItemArchiveRequest(id, true, 7, "archive-key"));
        await Invoke(tools, "project_work_item_checklist_update", id, child, false, CancellationToken.None, 8L, "leaf-key");
        service.Checklist.Should().Be(new ProjectWorkItemChecklistUpdateRequest(id, child, false, 8, "leaf-key"));
        var recovery = new WorkItemEditRecoveryRequest("ContextHub", id, "key", new string('a', 64));
        await Invoke(tools, "project_work_item_edit_receipt_get", recovery, CancellationToken.None);
        editing.Recovery.Should().Be(recovery);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Conflicts_keep_machine_code_but_unexpected_details_never_cross_the_MCP_boundary(bool gateway)
    {
        var service = new WorkItems { Failure = new SafeEditConflictException(SafeEditConflictCodes.StaleReadVersion, 9) };
        var tools = Tools(gateway, service, new Editing()); var request = new ProjectWorkItemUpdateRequest(Guid.NewGuid());
        var conflict = await FluentActions.Awaiting(() => Invoke(tools, "project_work_item_update", request, CancellationToken.None))
            .Should().ThrowAsync<McpException>();
        using var json = JsonDocument.Parse(conflict.Which.Message);
        json.RootElement.GetProperty("code").GetString().Should().Be(SafeEditConflictCodes.StaleReadVersion);
        json.RootElement.GetProperty("currentRevision").GetInt64().Should().Be(9);
        service.Failure = new Exception("synthetic-private-SQL-path");
        var unknown = await FluentActions.Awaiting(() => Invoke(tools, "project_work_item_update", request, CancellationToken.None))
            .Should().ThrowAsync<McpException>();
        unknown.Which.Message.Should().Contain("RequestOutcomeUnknown").And.NotContain("synthetic-private");
    }

    private static JsonElement Schema(Type type, string method)
        => JsonSerializer.SerializeToElement(McpServerTool.Create(type.GetMethod(method)!,
            RuntimeHelpers.GetUninitializedObject(type), new McpServerToolCreateOptions()).ProtocolTool).GetProperty("inputSchema");
    private static object Tools(bool gateway, WorkItems service, Editing editing)
    {
        var constructor = (gateway ? typeof(ChatGptGatewayTools) : typeof(BackendTools)).GetConstructors().Single();
        var args = constructor.GetParameters().Select(parameter => parameter.ParameterType == typeof(IProjectWorkItemService) ? (object)service
            : parameter.ParameterType == typeof(IWorkItemEditingService) ? editing : null).ToArray();
        return constructor.Invoke(args); // Constructors only capture dependencies; no host/DB starts.
    }
    private static async Task Invoke(object tools, string method, params object[] args)
        => await (Task)tools.GetType().GetMethod(method)!.Invoke(tools, args)!;
    private sealed class Editing : IWorkItemEditingService
    {
        public WorkItemEditRecoveryRequest? Recovery { get; private set; }
        public Task<WorkItemEditReceipt> ApplyAsync(WorkItemEditCommand command, CancellationToken ct) => throw new NotSupportedException();
        public Task<WorkItemEditReceipt?> ReadReceiptAsync(WorkItemEditRecoveryRequest request, CancellationToken ct)
        { Recovery = request; return Task.FromResult<WorkItemEditReceipt?>(null); }
    }
    private sealed class WorkItems : IProjectWorkItemService
    {
        public ProjectWorkItemArchiveRequest? Archive { get; private set; }
        public ProjectWorkItemChecklistUpdateRequest? Checklist { get; private set; }
        public Exception? Failure { get; set; }
        private static ProjectWorkItemResult Result(Guid id) => new(id, "ContextHub", "synthetic", "", [], [], ProjectWorkItemStatus.Pending, 0, null,
            DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, null)
        { Revision = 7 };
        public Task<ProjectWorkItemResult> UpdateAsync(ProjectWorkItemUpdateRequest request, CancellationToken ct)
            => Failure is null ? Task.FromResult(Result(request.Id)) : Task.FromException<ProjectWorkItemResult>(Failure);
        public Task<ProjectWorkItemResult> SetArchivedAsync(ProjectWorkItemArchiveRequest request, CancellationToken ct)
        { Archive = request; return Task.FromResult(Result(request.WorkItemId)); }
        public Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(ProjectWorkItemChecklistUpdateRequest request, CancellationToken ct)
        { Checklist = request; return Task.FromResult(Result(request.WorkItemId)); }
        public Task<ProjectWorkItemResult?> GetAsync(Guid id, bool includeArchived, CancellationToken ct) => Task.FromResult<ProjectWorkItemResult?>(null);
        public Task<IReadOnlyList<ProjectWorkItemResult>> ListAsync(ProjectWorkItemListRequest request, CancellationToken ct) => Task.FromResult<IReadOnlyList<ProjectWorkItemResult>>([]);
        public Task<ProjectWorkItemResult> CreateAsync(ProjectWorkItemCreateRequest request, CancellationToken ct) => throw new NotSupportedException();
        public Task<ProjectWorkItemResult> SetArchivedAsync(Guid id, bool archived, CancellationToken ct) => throw new NotSupportedException("Legacy overload must not be used.");
        public Task<ProjectWorkItemResult> SetChecklistItemCompletionAsync(Guid id, Guid leaf, bool completed, CancellationToken ct) => throw new NotSupportedException("Legacy overload must not be used.");
        public Task<ProjectWorkItemResult> SetGovernanceExclusionAsync(ProjectWorkItemGovernanceExclusionRequest request, CancellationToken ct) => throw new NotSupportedException();
    }
}

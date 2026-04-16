using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Tests.Shared;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;

namespace Memory.McpProtocolTests;

public sealed class McpProtocolTests(ContainerTestEnvironment environment) : IClassFixture<ContainerTestEnvironment>
{
    [DockerRequiredFact]
    public async Task Raw_Http_Tools_List_And_Call_Should_Work_After_Sdk_Session_Initialization()
    {
        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var memoryService = scope.ServiceProvider.GetRequiredService<IMemoryService>();
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();

            await memoryService.UpsertAsync(
                new MemoryUpsertRequest(
                    ExternalKey: "repo:mcp:1",
                    Scope: MemoryScope.Project,
                    MemoryType: MemoryType.Artifact,
                    Title: "MCP transport note",
                    Content: "The MCP endpoint uses Streamable HTTP at /mcp.",
                    Summary: "MCP endpoint note",
                    SourceType: "document",
                    SourceRef: "spec",
                    Tags: ["mcp", "transport"],
                    Importance: 0.8m,
                    Confidence: 0.95m),
                CancellationToken.None);

            dbContext.RuntimeLogEntries.AddRange(
                new RuntimeLogEntry
                {
                    ServiceName = "mcp-server",
                    Category = "Memory.McpProtocolTests",
                    Level = "Error",
                    Message = "MCP log search validation event.",
                    Exception = "System.Exception: synthetic",
                    TraceId = "trace-mcp-log-1",
                    RequestId = "request-mcp-log-1",
                    PayloadJson = """{"kind":"mcp-test"}""",
                    CreatedAt = DateTimeOffset.UtcNow
                },
                new RuntimeLogEntry
                {
                    ServiceName = "worker",
                    Category = "Memory.McpProtocolTests",
                    Level = "Warning",
                    Message = "MCP log search validation event from worker.",
                    Exception = string.Empty,
                    TraceId = "trace-mcp-log-2",
                    RequestId = "request-mcp-log-2",
                    PayloadJson = """{"kind":"mcp-test-worker"}""",
                    CreatedAt = DateTimeOffset.UtcNow.AddSeconds(-1)
                });

            await dbContext.SaveChangesAsync(CancellationToken.None);
            await processor.ProcessNextAsync(CancellationToken.None);
        }

        var captureHandler = new SessionCaptureHandler(environment.GetFactory().Server.CreateHandler());
        using var client = new HttpClient(captureHandler)
        {
            BaseAddress = environment.GetFactory().Server.BaseAddress
        };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(client.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, client);

        await using var mcpClient = await McpClient.CreateAsync(transport);
        var tools = await mcpClient.ListToolsAsync();
        tools.Should().NotBeEmpty();

        var sessionId = captureHandler.SessionId;
        sessionId.Should().NotBeNullOrWhiteSpace();

        var toolsPayload = await SendMcpAsync(client, sessionId!, 2, "tools/list", new { });
        var searchPayload = await SendMcpAsync(client, sessionId!, 3, "tools/call", new
        {
            name = "memory_search",
            arguments = new
            {
                query = "MCP endpoint",
                limit = 5,
                includeArchived = false
            }
        });
        var logPayload = await SendMcpAsync(client, sessionId!, 4, "tools/call", new
        {
            name = "log_search",
            arguments = new
            {
                request = new
                {
                    query = "validation event",
                    serviceName = "mcp-server,worker",
                    level = "Error,Warning",
                    limit = 5
                }
            }
        });
        var userPreferencePayload = await SendMcpAsync(client, sessionId!, 5, "tools/call", new
        {
            name = "user_preference_upsert",
            arguments = new
            {
                request = new
                {
                    key = "preferred-language",
                    kind = "CommunicationStyle",
                    title = "偏好繁體中文",
                    content = "回覆預設使用繁體中文。",
                    rationale = "長期偏好",
                    tags = new[] { "language" }
                }
            }
        });
        var userPreferenceUpdatePayload = await SendMcpAsync(client, sessionId!, 6, "tools/call", new
        {
            name = "user_preference_upsert",
            arguments = new
            {
                request = new
                {
                    key = "preferred-language",
                    kind = "CommunicationStyle",
                    title = "偏好繁體中文",
                    content = "回覆預設使用繁體中文，技術名詞保留英文。",
                    rationale = "更新偏好",
                    tags = new[] { "language", "style" }
                }
            }
        });
        var contextPayload = await SendMcpAsync(client, sessionId!, 7, "tools/call", new
        {
            name = "build_working_context",
            arguments = new
            {
                request = new
                {
                    query = "請依照我的偏好建立工作上下文",
                    limit = 3,
                    recentLogLimit = 3
                }
            }
        });

        toolsPayload.Should().Contain("memory_search");
        toolsPayload.Should().Contain("log_search");
        toolsPayload.Should().Contain("user_preference_upsert");
        searchPayload.Should().Contain("MCP transport note");
        logPayload.Should().Contain("trace-mcp-log-1");
        logPayload.Should().Contain("trace-mcp-log-2");
        userPreferencePayload.Should().Contain("preferred-language");
        ExtractToolJsonField(userPreferenceUpdatePayload, "content").Should().Contain("技術名詞保留英文");
        contextPayload.Should().Contain("userPreferences");
    }

    [DockerRequiredFact]
    public async Task Sdk_Client_Should_List_Tools_And_Call_Search()
    {
        using var httpClient = environment.GetFactory().CreateClient();
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(httpClient.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, httpClient);

        await using var client = await McpClient.CreateAsync(transport);
        var tools = await client.ListToolsAsync();

        tools.Should().NotBeEmpty();
        tools.Select(x => x.ProtocolTool.Name).Should().Contain("memory_search");
        tools.Select(x => x.ProtocolTool.Name).Should().Contain("user_preference_upsert");
    }

    [DockerRequiredFact]
    public async Task Raw_Http_Conversation_Ingest_Tool_Should_Create_And_Promote_Insights()
    {
        var conversationId = $"mcp-conversation-{Guid.NewGuid():N}";

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            dbContext.InstanceSettings.Add(new InstanceSetting
            {
                InstanceId = ProjectContext.DefaultProjectId,
                SettingKey = "behavior",
                ValueJson = JsonSerializer.Serialize(new InstanceBehaviorSettingsResult(
                    true,
                    true,
                    true,
                    20,
                    "Automatic",
                    240,
                    ProjectContext.DefaultProjectId,
                    MemoryQueryMode.CurrentOnly,
                    false,
                    true,
                    new DashboardSnapshotPollingSettingsResult(
                        30,
                        30,
                        10,
                        30,
                        5,
                        5,
                        1),
                    10,
                    5,
                    8,
                    10,
                    30)),
                Revision = 1,
                UpdatedAt = DateTimeOffset.UtcNow,
                UpdatedBy = "mcp-tests"
            });
            await dbContext.SaveChangesAsync(CancellationToken.None);
        }

        var captureHandler = new SessionCaptureHandler(environment.GetFactory().Server.CreateHandler());
        using var client = new HttpClient(captureHandler)
        {
            BaseAddress = environment.GetFactory().Server.BaseAddress
        };
        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = new Uri(client.BaseAddress!, "/mcp"),
            TransportMode = HttpTransportMode.StreamableHttp
        }, client);

        await using var mcpClient = await McpClient.CreateAsync(transport);
        var tools = await mcpClient.ListToolsAsync();
        tools.Select(x => x.ProtocolTool.Name).Should().Contain("conversation_ingest");

        var sessionId = captureHandler.SessionId;
        sessionId.Should().NotBeNullOrWhiteSpace();

        var ingestPayload = await SendMcpAsync(client, sessionId!, 2, "tools/call", new
        {
            name = "conversation_ingest",
            arguments = new
            {
                request = new
                {
                    conversationId = conversationId,
                    turnId = "turn-1",
                    eventType = "SessionCheckpoint",
                    sourceKind = "HostEvent",
                    sourceSystem = "codex",
                    sourceRef = "mcp-tests",
                    projectName = "ContextHub",
                    userMessageSummary = "使用者偏好回覆預設使用繁體中文。",
                    agentMessageSummary = "系統決定採用 shared summary layer。"
                }
            }
        });

        ExtractToolText(ingestPayload).Should().Contain("automationScheduled");

        using (var scope = environment.GetFactory().Services.CreateScope())
        {
            var processor = scope.ServiceProvider.GetRequiredService<IBackgroundJobProcessor>();
            var dbContext = scope.ServiceProvider.GetRequiredService<MemoryDbContext>();
            await DrainConversationAutomationAsync(processor, dbContext, conversationId, CancellationToken.None);
        }

        var insightPayload = await SendMcpAsync(client, sessionId!, 3, "tools/call", new
        {
            name = "conversation_insights_list",
            arguments = new
            {
                request = new
                {
                    conversationId = conversationId,
                    limit = 20
                }
            }
        });

        insightPayload.Should().Contain("Promoted");
        insightPayload.Should().Contain("PreferenceCandidate");
    }

    private static async Task DrainConversationAutomationAsync(
        IBackgroundJobProcessor processor,
        MemoryDbContext dbContext,
        string conversationId,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            await processor.ProcessNextAsync(cancellationToken);

            var promoted = await dbContext.ConversationInsights.AnyAsync(
                x => x.ConversationId == conversationId &&
                     x.PromotionStatus == ConversationPromotionStatus.Promoted,
                cancellationToken);

            if (promoted)
            {
                return;
            }
        }
    }

    private static async Task<string> SendMcpAsync(HttpClient client, string sessionId, int id, string method, object @params)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new
            {
                jsonrpc = "2.0",
                id,
                method,
                @params
            })
        };
        request.Headers.Add("Mcp-Session-Id", sessionId);
        request.Headers.Add("MCP-Protocol-Version", "2025-03-26");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        using var response = await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync();
    }

    private static string ExtractToolText(string payload)
    {
        var dataLine = payload
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(line => line.StartsWith("data: ", StringComparison.Ordinal))
            ?? throw new InvalidOperationException("Expected SSE data line.");

        using var document = JsonDocument.Parse(dataLine["data: ".Length..]);
        return document.RootElement
            .GetProperty("result")
            .GetProperty("content")[0]
            .GetProperty("text")
            .GetString()
            ?? string.Empty;
    }

    private static string ExtractToolJsonField(string payload, string fieldName)
    {
        using var document = JsonDocument.Parse(ExtractToolText(payload));
        return document.RootElement.TryGetProperty(fieldName, out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private sealed class SessionCaptureHandler(HttpMessageHandler innerHandler) : DelegatingHandler(innerHandler)
    {
        public string? SessionId { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = await base.SendAsync(request, cancellationToken);
            if (string.IsNullOrWhiteSpace(SessionId) &&
                (response.Headers.TryGetValues("Mcp-Session-Id", out var values) ||
                 response.Headers.TryGetValues("mcp-session-id", out values)))
            {
                SessionId = values.SingleOrDefault();
            }

            return response;
        }
    }
}

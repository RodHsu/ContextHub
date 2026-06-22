using System.Net;
using ContextHub.McpStdioBridge;
using FluentAssertions;

namespace Memory.UnitTests;

public sealed class McpStdioBridgeTests
{
    [Fact]
    public async Task Local_Initialize_Should_Not_Call_Remote()
    {
        var handler = new QueueHttpMessageHandler();
        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""");

        output.Should().Contain("ContextHub.McpStdioBridge");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task Tools_List_Should_Reconnect_And_Retry_After_Transient_Status()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s1"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":2,"error":{"code":-32603,"message":"restart"}}""", HttpStatusCode.ServiceUnavailable));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":3,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s2"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":4,"result":{"tools":[{"name":"memory_search","description":"","inputSchema":{},"execution":{"mode":"server"}}]}}"""));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        output.Should().Contain("memory_search");
        output.Should().NotContain("execution");
        handler.Requests.Count(r => r.RemoteMethod == "initialize").Should().Be(2);
        handler.Requests.Count(r => r.RemoteMethod == "tools/list").Should().Be(2);
        handler.Requests.Where(r => r.RemoteMethod == "tools/list")
            .Select(r => r.SessionId)
            .Should()
            .Equal("s1", "s2");
    }

    [Fact]
    public async Task Read_Only_Tool_Should_Reconnect_And_Retry_After_Connection_Reset()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s1"));
        handler.Enqueue(_ => throw new HttpRequestException("connection reset"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":3,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s2"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":4,"result":{"content":[{"type":"text","text":"{\"facts\":[]}"}]}}"""));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            ToolCallMessage(2, "memory_search"));

        output.Should().Contain("facts");
        handler.Requests.Count(r => r.RemoteToolName == "memory_search").Should().Be(2);
        handler.Requests.Where(r => r.RemoteToolName == "memory_search")
            .Select(r => r.SessionId)
            .Should()
            .Equal("s1", "s2");
    }

    [Fact]
    public async Task Mutation_Tool_Should_Not_Retry_After_Connection_Reset()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s1"));
        handler.Enqueue(_ => throw new HttpRequestException("connection reset"));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            ToolCallMessage(2, "conversation_ingest"));

        output.Should().Contain("\"error\"");
        output.Should().Contain("connection reset");
        handler.Requests.Count(r => r.RemoteToolName == "conversation_ingest").Should().Be(1);
        handler.Requests.Count(r => r.RemoteMethod == "initialize").Should().Be(1);
    }

    [Fact]
    public async Task Stale_Session_Should_Clear_Session_And_Retry_Read_Only_Call()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s1"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"session expired"}}""", HttpStatusCode.Conflict));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":3,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s2"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":4,"result":{"content":[{"type":"text","text":"{\"memoryId\":\"1\"}"}]}}"""));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            ToolCallMessage(2, "memory_get"));

        output.Should().Contain("memoryId");
        handler.Requests.Count(r => r.RemoteToolName == "memory_get").Should().Be(2);
        handler.Requests.Where(r => r.RemoteToolName == "memory_get")
            .Select(r => r.SessionId)
            .Should()
            .Equal("s1", "s2");
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authentication_And_Authorization_Failures_Should_Not_Retry(HttpStatusCode statusCode)
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s1"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":2,"error":{"code":-32001,"message":"auth failed"}}""", statusCode));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            ToolCallMessage(2, "memory_search"));

        output.Should().Contain("\"error\"");
        output.Should().Contain(((int)statusCode).ToString());
        handler.Requests.Count(r => r.RemoteToolName == "memory_search").Should().Be(1);
        handler.Requests.Count(r => r.RemoteMethod == "initialize").Should().Be(1);
    }

    [Fact]
    public async Task Malformed_Sse_Should_Return_Error_And_Keep_Bridge_Process_Alive()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s1"));
        handler.Enqueue(_ => RawResponse("event: message\n\n"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":3,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s2"));
        handler.Enqueue(_ => RawResponse("event: message\n\n"));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""",
            """{"jsonrpc":"2.0","id":3,"method":"ping","params":{}}""");

        output.Should().Contain("did not contain JSON or SSE data");
        output.Should().Contain("\"id\":3");
    }

    [Fact]
    public async Task Reconnect_Log_Should_Not_Contain_Token()
    {
        var logPath = Path.GetTempFileName();
        try
        {
            var handler = new QueueHttpMessageHandler();
            handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":1,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s1"));
            handler.Enqueue(_ => throw new HttpRequestException("connection reset"));
            handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":3,"result":{"serverInfo":{"name":"remote"}}}""", sessionId: "s2"));
            handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":4,"result":{"content":[{"type":"text","text":"{\"facts\":[]}"}]}}"""));

            var bridge = CreateBridge(handler, logPath: logPath, token: "secret-token-for-test");

            _ = await RunBridgeAsync(bridge,
                InitializeMessage(),
                ToolCallMessage(2, "memory_search"));

            var log = await File.ReadAllTextAsync(logPath);
            log.Should().Contain("rebuilding remote MCP session");
            log.Should().NotContain("secret-token-for-test");
        }
        finally
        {
            File.Delete(logPath);
        }
    }

    private static StdioBridge CreateBridge(
        QueueHttpMessageHandler handler,
        string? logPath = null,
        string token = "test-token")
    {
        var options = new BridgeOptions(
            new Uri("https://context-hub.test/mcp"),
            token,
            logPath,
            TimeSpan.FromSeconds(5),
            TimeSpan.Zero,
            ReconnectOnError: true);
        var httpClient = new HttpClient(handler)
        {
            Timeout = options.RemoteTimeout
        };
        var logger = BridgeLogger.FromPath(logPath);
        return new StdioBridge(
            new RemoteMcpClient(httpClient, options, logger),
            BridgeRetryPolicy.Default,
            logger);
    }

    private static async Task<string> RunBridgeAsync(StdioBridge bridge, params string[] messages)
    {
        using var input = new StringReader(string.Join(Environment.NewLine, messages));
        using var output = new StringWriter();
        await bridge.RunAsync(input, output);
        return output.ToString();
    }

    private static string InitializeMessage()
        => """{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-06-18","capabilities":{},"clientInfo":{"name":"test","version":"1.0"}}}""";

    private static string ToolCallMessage(int id, string toolName)
        => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"tools/call\",\"params\":{\"name\":\"" + toolName + "\",\"arguments\":{\"request\":{\"projectId\":\"ContextHub\",\"query\":\"test\"}}}}";

    private static HttpResponseMessage McpResponse(string json, string? sessionId = null)
        => McpResponse(json, HttpStatusCode.OK, sessionId);

    private static HttpResponseMessage McpResponse(string json, HttpStatusCode statusCode, string? sessionId = null)
    {
        var response = RawResponse($"event: message\ndata: {json}\n\n", statusCode);
        if (!string.IsNullOrWhiteSpace(sessionId))
        {
            response.Headers.Add("Mcp-Session-Id", sessionId);
        }

        return response;
    }

    private static HttpResponseMessage RawResponse(string content, HttpStatusCode statusCode = HttpStatusCode.OK)
        => new(statusCode)
        {
            Content = new StringContent(content)
        };

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<Func<HttpRequestMessage, HttpResponseMessage>> responses = new();

        public List<RequestRecord> Requests { get; } = [];

        public void Enqueue(Func<HttpRequestMessage, HttpResponseMessage> response)
            => responses.Enqueue(response);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Requests.Add(RequestRecord.From(request, body));

            if (responses.Count == 0)
            {
                throw new InvalidOperationException("No queued HTTP response.");
            }

            return responses.Dequeue()(request);
        }
    }

    private sealed record RequestRecord(string? SessionId, string? RemoteMethod, string? RemoteToolName)
    {
        public static RequestRecord From(HttpRequestMessage request, string body)
        {
            string? method = null;
            string? toolName = null;
            using var document = System.Text.Json.JsonDocument.Parse(body);
            if (document.RootElement.TryGetProperty("method", out var methodElement))
            {
                method = methodElement.GetString();
            }

            if (document.RootElement.TryGetProperty("params", out var parameters) &&
                parameters.TryGetProperty("name", out var nameElement))
            {
                toolName = nameElement.GetString();
            }

            request.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues);
            return new RequestRecord(sessionValues?.FirstOrDefault(), method, toolName);
        }
    }
}

using System.Net;
using System.Text.Json;
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
    public async Task Local_Discover_Should_Advertise_Modern_And_Legacy_Protocols_Without_Calling_Remote()
    {
        var handler = new QueueHttpMessageHandler();
        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            """{"jsonrpc":"2.0","id":1,"method":"server/discover","params":{"_meta":{"io.modelcontextprotocol/protocolVersion":"2026-07-28","io.modelcontextprotocol/clientCapabilities":{}}}}""");

        output.Should().Contain("2026-07-28");
        output.Should().Contain("2025-11-25");
        output.Should().Contain("io.modelcontextprotocol/serverInfo");
        handler.Requests.Should().BeEmpty();
    }

    [Theory]
    [InlineData("resources/read", "uri", "working-context://ContextHub?query=mcp")]
    [InlineData("prompts/get", "name", "context-summary")]
    public async Task Modern_Named_Request_Should_Mirror_The_Correct_Parameter_To_Mcp_Name(
        string method,
        string parameterName,
        string parameterValue)
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => ModernDiscoverResponse());
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":2,"result":{}}"""));
        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(
            bridge,
            InitializeMessage(),
            $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"{method}\",\"params\":{{\"{parameterName}\":\"{parameterValue}\"}}}}");

        output.Should().Contain("\"result\":{}");
        handler.Requests.Single(r => r.RemoteMethod == method).McpName.Should().Be(parameterValue);
    }

    [Theory]
    [InlineData("resources/read", "uri", "記憶://ContextHub", "=?base64?6KiY5oa2Oi8vQ29udGV4dEh1Yg==?=")]
    [InlineData("tools/call", "name", " memory_search", "=?base64?IG1lbW9yeV9zZWFyY2g=?=")]
    [InlineData("prompts/get", "name", "=?base64?literal?=", "=?base64?PT9iYXNlNjQ/bGl0ZXJhbD89?=")]
    public async Task Modern_Named_Request_Should_Base64_Encode_Unsafe_Routing_Header_Values(
        string method,
        string parameterName,
        string parameterValue,
        string expectedHeaderValue)
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => ModernDiscoverResponse());
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":2,"result":{}}"""));
        var bridge = CreateBridge(handler);

        var parameterJson = JsonSerializer.Serialize(parameterValue);
        _ = await RunBridgeAsync(
            bridge,
            InitializeMessage(),
            $"{{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"{method}\",\"params\":{{\"{parameterName}\":{parameterJson}}}}}");

        handler.Requests.Single(r => r.RemoteMethod == method).McpName.Should().Be(expectedHeaderValue);
    }

    [Fact]
    public async Task Tools_List_Should_Reconnect_And_Retry_After_Transient_Status()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => ModernDiscoverResponse());
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":2,"error":{"code":-32603,"message":"restart"}}""", HttpStatusCode.ServiceUnavailable));
        handler.Enqueue(_ => ModernDiscoverResponse());
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":4,"result":{"tools":[{"name":"memory_search","description":"","inputSchema":{},"execution":{"mode":"server"}}]}}"""));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            """{"jsonrpc":"2.0","method":"notifications/initialized","params":{}}""",
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        output.Should().Contain("memory_search");
        output.Should().NotContain("execution");
        handler.Requests.Count(r => r.RemoteMethod == "server/discover").Should().Be(2);
        handler.Requests.Count(r => r.RemoteMethod == "tools/list").Should().Be(2);
        handler.Requests.Where(r => r.RemoteMethod == "tools/list")
            .Select(r => r.SessionId)
            .Should()
            .OnlyContain(x => x == null);
        handler.Requests.Where(r => r.RemoteMethod == "tools/list")
            .Should()
            .OnlyContain(r => r.ProtocolVersion == "2026-07-28" &&
                              r.MetaProtocolVersion == "2026-07-28" &&
                              r.McpMethod == "tools/list");
    }

    [Fact]
    public async Task Read_Only_Tool_Should_Reconnect_And_Retry_After_Connection_Reset()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => ModernDiscoverResponse());
        handler.Enqueue(_ => throw new HttpRequestException("connection reset"));
        handler.Enqueue(_ => ModernDiscoverResponse());
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
            .OnlyContain(x => x == null);
        handler.Requests.Where(r => r.RemoteToolName == "memory_search")
            .Should()
            .OnlyContain(r => r.McpName == "memory_search" && r.McpMethod == "tools/call");
    }

    [Fact]
    public async Task Mutation_Tool_Should_Not_Retry_After_Connection_Reset()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => ModernDiscoverResponse());
        handler.Enqueue(_ => throw new HttpRequestException("connection reset"));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            ToolCallMessage(2, "conversation_ingest"));

        output.Should().Contain("\"error\"");
        output.Should().Contain("connection reset");
        handler.Requests.Count(r => r.RemoteToolName == "conversation_ingest").Should().Be(1);
        handler.Requests.Count(r => r.RemoteMethod == "server/discover").Should().Be(1);
    }

    [Fact]
    public async Task Stale_Session_Should_Clear_Session_And_Retry_Read_Only_Call()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => LegacyDiscoverResponse());
        handler.Enqueue(_ => LegacyInitializeResponse("2025-06-18", "s1"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":2,"error":{"code":-32000,"message":"session expired"}}""", HttpStatusCode.Conflict));
        handler.Enqueue(_ => LegacyDiscoverResponse());
        handler.Enqueue(_ => LegacyInitializeResponse("2025-06-18", "s2"));
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
        handler.Requests.Where(r => r.RemoteToolName == "memory_get")
            .Should()
            .OnlyContain(r => r.ProtocolVersion == "2025-06-18" && r.MetaProtocolVersion == null);
    }

    [Fact]
    public async Task Legacy_Stateless_Server_Should_Not_Require_A_Session_Header()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => LegacyUnsupportedVersionResponse());
        handler.Enqueue(_ => LegacyInitializeResponse("2025-11-25"));
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":3,"result":{"tools":[]}}"""));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            """{"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}""");

        output.Should().Contain("\"tools\":[]");
        handler.Requests.Count(r => r.RemoteMethod == "initialize").Should().Be(1);
        handler.Requests.Single(r => r.RemoteMethod == "tools/list").Should().Match<RequestRecord>(r =>
            r.SessionId == null &&
            r.ProtocolVersion == "2025-11-25" &&
            r.MetaProtocolVersion == null);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task Authentication_And_Authorization_Failures_Should_Not_Retry(HttpStatusCode statusCode)
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => ModernDiscoverResponse());
        handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":2,"error":{"code":-32001,"message":"auth failed"}}""", statusCode));

        var bridge = CreateBridge(handler);

        var output = await RunBridgeAsync(bridge,
            InitializeMessage(),
            ToolCallMessage(2, "memory_search"));

        output.Should().Contain("\"error\"");
        output.Should().Contain(((int)statusCode).ToString());
        handler.Requests.Count(r => r.RemoteToolName == "memory_search").Should().Be(1);
        handler.Requests.Count(r => r.RemoteMethod == "server/discover").Should().Be(1);
    }

    [Fact]
    public async Task Malformed_Sse_Should_Return_Error_And_Keep_Bridge_Process_Alive()
    {
        var handler = new QueueHttpMessageHandler();
        handler.Enqueue(_ => ModernDiscoverResponse());
        handler.Enqueue(_ => RawResponse("event: message\n\n"));
        handler.Enqueue(_ => ModernDiscoverResponse());
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
            handler.Enqueue(_ => ModernDiscoverResponse());
            handler.Enqueue(_ => throw new HttpRequestException("connection reset"));
            handler.Enqueue(_ => ModernDiscoverResponse());
            handler.Enqueue(_ => McpResponse("""{"jsonrpc":"2.0","id":4,"result":{"content":[{"type":"text","text":"{\"facts\":[]}"}]}}"""));

            var bridge = CreateBridge(handler, logPath: logPath, token: "secret-token-for-test");

            _ = await RunBridgeAsync(bridge,
                InitializeMessage(),
                ToolCallMessage(2, "memory_search"));

            var log = await File.ReadAllTextAsync(logPath);
            log.Should().Contain("renegotiating remote MCP protocol");
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

    private static HttpResponseMessage ModernDiscoverResponse()
        => McpResponse("""{"jsonrpc":"2.0","id":1,"result":{"resultType":"complete","supportedVersions":["2026-07-28"],"capabilities":{}}}""");

    private static HttpResponseMessage LegacyDiscoverResponse()
        => McpResponse("""{"jsonrpc":"2.0","id":1,"error":{"code":-32601,"message":"Method not found"}}""");

    private static HttpResponseMessage LegacyUnsupportedVersionResponse()
        => McpResponse("""{"jsonrpc":"2.0","id":1,"error":{"code":-32020,"message":"Unsupported protocol version"}}""");

    private static HttpResponseMessage LegacyInitializeResponse(string protocolVersion, string? sessionId = null)
        => McpResponse(
            "{\"jsonrpc\":\"2.0\",\"id\":1,\"result\":{\"protocolVersion\":\"" + protocolVersion + "\",\"capabilities\":{},\"serverInfo\":{\"name\":\"remote\",\"version\":\"1.0\"}}}",
            sessionId);

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

    private sealed record RequestRecord(
        string? SessionId,
        string? ProtocolVersion,
        string? McpMethod,
        string? McpName,
        string? MetaProtocolVersion,
        string? RemoteMethod,
        string? RemoteToolName)
    {
        public static RequestRecord From(HttpRequestMessage request, string body)
        {
            string? method = null;
            string? toolName = null;
            string? metaProtocolVersion = null;
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

            if (document.RootElement.TryGetProperty("params", out parameters) &&
                parameters.TryGetProperty("_meta", out var meta) &&
                meta.TryGetProperty("io.modelcontextprotocol/protocolVersion", out var metaVersion))
            {
                metaProtocolVersion = metaVersion.GetString();
            }

            request.Headers.TryGetValues("Mcp-Session-Id", out var sessionValues);
            request.Headers.TryGetValues("MCP-Protocol-Version", out var protocolVersions);
            request.Headers.TryGetValues("Mcp-Method", out var mcpMethods);
            request.Headers.TryGetValues("Mcp-Name", out var mcpNames);
            return new RequestRecord(
                sessionValues?.FirstOrDefault(),
                protocolVersions?.FirstOrDefault(),
                mcpMethods?.FirstOrDefault(),
                mcpNames?.FirstOrDefault(),
                metaProtocolVersion,
                method,
                toolName);
        }
    }
}

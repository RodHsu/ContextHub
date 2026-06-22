using ContextHub.McpStdioBridge;

var options = BridgeOptions.FromEnvironment();
using var httpClient = new HttpClient
{
    Timeout = options.RemoteTimeout
};

var logger = BridgeLogger.FromPath(options.LogPath);
var remoteClient = new RemoteMcpClient(httpClient, options, logger);
var bridge = new StdioBridge(remoteClient, BridgeRetryPolicy.Default, logger);

await bridge.RunAsync(Console.In, Console.Out);

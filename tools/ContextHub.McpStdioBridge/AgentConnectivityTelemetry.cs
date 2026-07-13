using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace ContextHub.McpStdioBridge;

public interface IAgentConnectivityTelemetrySink
{
    ValueTask RecordAsync(AgentConnectivityObservation observation, CancellationToken cancellationToken);
    Task FlushAsync(CancellationToken cancellationToken);
}

public sealed record AgentConnectivityObservation(
    string AgentId,
    string AgentName,
    string AgentVersion,
    string BridgeVersion,
    string EndpointHost,
    string Transport,
    string McpMethod,
    string? ToolName,
    int Attempt,
    bool Success,
    int? StatusCode,
    string? ErrorKind,
    double ClientElapsedMs,
    double? ServerElapsedMs,
    bool SessionWasInitialized,
    bool ReconnectAttempted,
    string? CorrelationId,
    string? Source,
    DateTimeOffset ObservedAtUtc);

public sealed class NoOpAgentConnectivityTelemetrySink : IAgentConnectivityTelemetrySink
{
    public static readonly NoOpAgentConnectivityTelemetrySink Instance = new();

    public ValueTask RecordAsync(AgentConnectivityObservation observation, CancellationToken cancellationToken)
        => ValueTask.CompletedTask;

    public Task FlushAsync(CancellationToken cancellationToken)
        => Task.CompletedTask;
}

public sealed class AgentConnectivityTelemetryUploader : IAgentConnectivityTelemetrySink, IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly Random SharedRandom = new();
    private readonly HttpClient httpClient;
    private readonly BridgeOptions options;
    private readonly BridgeLogger logger;
    private readonly Channel<AgentConnectivityObservation> channel;
    private readonly Uri ingestEndpoint;
    private readonly CancellationTokenSource cts = new();
    private readonly Task worker;

    public AgentConnectivityTelemetryUploader(HttpClient httpClient, BridgeOptions options, BridgeLogger logger)
    {
        this.httpClient = httpClient;
        this.options = options;
        this.logger = logger;
        ingestEndpoint = BuildIngestEndpoint(options.Endpoint);
        channel = Channel.CreateBounded<AgentConnectivityObservation>(new BoundedChannelOptions(Math.Max(1, options.AgentTelemetryMaxBatchSize * 10))
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
        worker = Task.Run(() => RunAsync(cts.Token));
    }

    public ValueTask RecordAsync(AgentConnectivityObservation observation, CancellationToken cancellationToken)
    {
        if (!options.AgentTelemetryEnabled ||
            string.Equals(options.AgentTelemetryProfile, "Off", StringComparison.OrdinalIgnoreCase) ||
            !ShouldSample(observation.Success))
        {
            return ValueTask.CompletedTask;
        }

        _ = channel.Writer.TryWrite(observation);
        return ValueTask.CompletedTask;
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        var batch = new List<AgentConnectivityObservation>(options.AgentTelemetryMaxBatchSize);
        while (channel.Reader.TryRead(out var item) && batch.Count < options.AgentTelemetryMaxBatchSize)
        {
            batch.Add(item);
        }

        if (batch.Count > 0)
        {
            await UploadAsync(batch, cancellationToken);
        }
    }

    public async ValueTask DisposeAsync()
    {
        channel.Writer.TryComplete();
        cts.Cancel();
        try
        {
            await worker;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            cts.Dispose();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await FlushAsync(timeout.Token);
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        var batch = new List<AgentConnectivityObservation>(options.AgentTelemetryMaxBatchSize);
        using var timer = new PeriodicTimer(options.AgentTelemetryUploadInterval);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                while (channel.Reader.TryRead(out var item))
                {
                    batch.Add(item);
                    if (batch.Count >= options.AgentTelemetryMaxBatchSize)
                    {
                        await UploadAndClearAsync(batch, cancellationToken);
                    }
                }

                if (batch.Count > 0)
                {
                    await UploadAndClearAsync(batch, cancellationToken);
                }

                await timer.WaitForNextTickAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task UploadAndClearAsync(List<AgentConnectivityObservation> batch, CancellationToken cancellationToken)
    {
        await UploadAsync(batch, cancellationToken);
        batch.Clear();
    }

    private async Task UploadAsync(IReadOnlyList<AgentConnectivityObservation> observations, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, ingestEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", options.Token);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new AgentConnectivityObservationBatch(options.ProjectId, observations), JsonOptions),
                Encoding.UTF8,
                "application/json");

            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.Log($"agent connectivity telemetry upload returned {(int)response.StatusCode}");
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.Log($"agent connectivity telemetry upload failed: {ex.GetType().Name}");
        }
    }

    private bool ShouldSample(bool success)
    {
        var rate = success ? options.AgentTelemetrySuccessSampleRate : options.AgentTelemetryFailureSampleRate;
        if (rate >= 1)
        {
            return true;
        }

        if (rate <= 0)
        {
            return false;
        }

        lock (SharedRandom)
        {
            return SharedRandom.NextDouble() <= rate;
        }
    }

    private static Uri BuildIngestEndpoint(Uri mcpEndpoint)
    {
        var builder = new UriBuilder(mcpEndpoint)
        {
            Path = "/api/agent-connectivity/observations",
            Query = string.Empty,
            Fragment = string.Empty
        };
        return builder.Uri;
    }

    private sealed record AgentConnectivityObservationBatch(
        string ProjectId,
        IReadOnlyList<AgentConnectivityObservation> Observations);
}

using Memory.Application;
using Memory.EmbeddingServer;
using Memory.Infrastructure;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<JsonOptions>(options =>
{
    options.SerializerOptions.PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase;
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
});

builder.Services.AddProblemDetails();
builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection(EmbeddingOptions.SectionName));
builder.Services.PostConfigure<EmbeddingOptions>(EmbeddingProfileResolver.ApplyResolvedDefaults);
builder.Services.AddSingleton(sp => EmbeddingProfileResolver.Resolve(sp.GetRequiredService<IOptions<EmbeddingOptions>>().Value));
builder.Services.AddSingleton<IResolvedEmbeddingProfileAccessor, ResolvedEmbeddingProfileAccessor>();
builder.Services.AddHttpClient(ModelAssetDownloadClient.Name, client =>
{
    client.Timeout = TimeSpan.FromMinutes(20);
    client.DefaultRequestHeaders.UserAgent.ParseAdd("ContextHub-EmbeddingServer/1.0");
})
.ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
{
    // Some Docker environments expose IPv6 DNS answers for Hugging Face
    // without actually routing IPv6 traffic. Prefer IPv4 first to keep the
    // first model bootstrap path stable.
    ConnectCallback = async (context, cancellationToken) =>
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, cancellationToken);
        var orderedAddresses = addresses
            .OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1)
            .ToArray();

        Exception? lastException = null;
        foreach (var address in orderedAddresses)
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), cancellationToken);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (Exception ex)
            {
                lastException = ex;
                socket.Dispose();
            }
        }

        throw lastException ?? new SocketException((int)SocketError.HostUnreachable);
    }
});
builder.Services.AddSingleton<OnnxEmbeddingRuntime>();

var app = builder.Build();

app.UseExceptionHandler();

var runtime = app.Services.GetRequiredService<OnnxEmbeddingRuntime>();
await runtime.InitializeAsync(app.Lifetime.ApplicationStopping);

app.MapGet("/health/live", () => Results.Ok(new { status = "live" }));
app.MapGet("/health/ready", () => runtime.IsReady
    ? Results.Ok(new { status = "ready" })
    : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

app.MapGet("/info", (IResolvedEmbeddingProfileAccessor profileAccessor) =>
    Results.Ok(new EmbeddingServiceInfoResult(
        profileAccessor.Current.Profile,
        profileAccessor.Current.ModelId,
        profileAccessor.Current.ModelKey,
        profileAccessor.Current.Dimensions,
        profileAccessor.Current.MaxTokens,
        OnnxEmbeddingRuntime.ExecutionProviderName,
        profileAccessor.Current.InferenceThreads,
        profileAccessor.Current.BatchSize,
        true,
        runtime.IsReady)));

app.MapPost("/embed", async (EmbeddingServiceEmbedRequest request, CancellationToken cancellationToken) =>
{
    if (string.IsNullOrWhiteSpace(request.Text))
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["text"] = ["Text is required."]
        });
    }

    var result = await runtime.EmbedAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.MapPost("/embed/batch", async (BatchEmbeddingServiceEmbedRequest request, CancellationToken cancellationToken) =>
{
    if (request.Items.Count == 0)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["items"] = ["At least one item is required."]
        });
    }

    if (request.Items.Count > OnnxEmbeddingRuntime.MaxBatchSize)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            ["items"] = [$"A maximum of {OnnxEmbeddingRuntime.MaxBatchSize} items is supported per batch request."]
        });
    }

    var invalidItem = request.Items
        .Select((item, index) => new { item, index })
        .FirstOrDefault(x => string.IsNullOrWhiteSpace(x.item.Text));
    if (invalidItem is not null)
    {
        return Results.ValidationProblem(new Dictionary<string, string[]>
        {
            [$"items[{invalidItem.index}].text"] = ["Text is required."]
        });
    }

    var result = await runtime.EmbedBatchAsync(request, cancellationToken);
    return Results.Ok(result);
});

app.Run();

public partial class Program;

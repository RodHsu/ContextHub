using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Memory.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class DeterministicEmbeddingProvider(IOptions<EmbeddingOptions> options) : IEmbeddingProvider
{
    private readonly EmbeddingOptions _options = options.Value;

    public string ProviderName => "Deterministic";
    public string ExecutionProvider => "Deterministic";
    public string EmbeddingProfile => _options.Profile;
    public string ModelKey => _options.ModelKey;
    public int Dimensions => _options.Dimensions;
    public int MaxTokens => _options.MaxTokens;
    public int InferenceThreads => Math.Max(1, _options.InferenceThreads);
    public int BatchSize => Math.Clamp(_options.BatchSize > 0 ? _options.BatchSize : 8, 1, 16);
    public bool BatchingEnabled => true;

    public Task<EmbeddingVector> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new EmbeddingVector(ModelKey, Dimensions, HashToVector(text, Dimensions, $"{ModelKey}:{purpose}")));
    }

    public Task<IReadOnlyList<EmbeddingVector>> EmbedBatchAsync(IReadOnlyList<BatchEmbeddingItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<EmbeddingVector> results = items
            .Select(item => new EmbeddingVector(ModelKey, Dimensions, HashToVector(item.Text, Dimensions, $"{ModelKey}:{item.Purpose}")))
            .ToArray();
        return Task.FromResult(results);
    }

    internal static float[] HashToVector(string text, int dimensions, string salt)
    {
        var vector = new float[dimensions];
        foreach (var token in text.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{salt}:{token}"));
            var index = Math.Abs(BitConverter.ToInt32(hash, 0)) % dimensions;
            var sign = (hash[4] & 1) == 0 ? 1f : -1f;
            vector[index] += sign;
        }

        Normalize(vector);
        return vector;
    }

    internal static void Normalize(float[] values)
    {
        var sum = 0d;
        foreach (var value in values)
        {
            sum += value * value;
        }

        var norm = (float)Math.Sqrt(sum);
        if (norm <= float.Epsilon)
        {
            return;
        }

        for (var i = 0; i < values.Length; i++)
        {
            values[i] /= norm;
        }
    }
}

public sealed class LocalOnnxEmbeddingProvider(IOptions<EmbeddingOptions> options) : IEmbeddingProvider
{
    private readonly EmbeddingOptions _options = options.Value;

    public string ProviderName => "Onnx";
    public string ExecutionProvider => "CPUExecutionProvider";
    public string EmbeddingProfile => _options.Profile;
    public string ModelKey => _options.ModelKey;
    public int Dimensions => _options.Dimensions;
    public int MaxTokens => _options.MaxTokens;
    public int InferenceThreads => Math.Max(1, _options.InferenceThreads);
    public int BatchSize => Math.Clamp(_options.BatchSize > 0 ? _options.BatchSize : 8, 1, 16);
    public bool BatchingEnabled => false;

    public Task<EmbeddingVector> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("LocalOnnxEmbeddingProvider is no longer a supported deployment path. Use the Http embedding provider backed by embedding-service.");
    }

    public Task<IReadOnlyList<EmbeddingVector>> EmbedBatchAsync(IReadOnlyList<BatchEmbeddingItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw new InvalidOperationException("LocalOnnxEmbeddingProvider is no longer a supported deployment path. Use the Http embedding provider backed by embedding-service.");
    }
}

public sealed class HttpEmbeddingProvider(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingOptions> options,
    ILogger<HttpEmbeddingProvider> logger) : IEmbeddingProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly EmbeddingOptions _options = options.Value;

    public string ProviderName => "Http";
    public string ExecutionProvider => "CPUExecutionProvider";
    public string EmbeddingProfile => _options.Profile;
    public string ModelKey => _options.ModelKey;
    public int Dimensions => _options.Dimensions;
    public int MaxTokens => _options.MaxTokens;
    public int InferenceThreads => Math.Max(1, _options.InferenceThreads);
    public int BatchSize => Math.Clamp(_options.BatchSize > 0 ? _options.BatchSize : 8, 1, 16);
    public bool BatchingEnabled => true;

    public async Task<EmbeddingVector> EmbedAsync(string text, EmbeddingPurpose purpose, CancellationToken cancellationToken)
    {
        var results = await EmbedBatchAsync([new BatchEmbeddingItem(text, purpose)], cancellationToken);
        return results[0];
    }

    public async Task<IReadOnlyList<EmbeddingVector>> EmbedBatchAsync(IReadOnlyList<BatchEmbeddingItem> items, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            throw new InvalidOperationException("Embeddings provider is set to Http but BaseUrl is missing.");
        }

        if (items.Count == 0)
        {
            return [];
        }

        var normalizedItems = items
            .Select(item => new EmbeddingServiceEmbedRequest(item.Text.Trim(), item.Purpose))
            .ToArray();
        var client = httpClientFactory.CreateClient(HttpEmbeddingProviderClient.Name);

        if (normalizedItems.Length == 1)
        {
            var singleResponse = await client.PostAsJsonAsync("/embed", normalizedItems[0], SerializerOptions, cancellationToken);
            singleResponse.EnsureSuccessStatusCode();

            var singlePayload = await singleResponse.Content.ReadFromJsonAsync<EmbeddingServiceEmbedResponse>(cancellationToken: cancellationToken)
                ?? throw new InvalidOperationException("Embedding service returned an empty payload.");

            if (singlePayload.Values.Length == 0)
            {
                throw new InvalidOperationException("Embedding service returned an empty vector.");
            }

            if (singlePayload.Truncated)
            {
                logger.LogWarning(
                    "Embedding input was truncated from {TokenCount} to {MaxTokens} tokens for profile {Profile}.",
                    singlePayload.TokenCount,
                    singlePayload.MaxTokens,
                    _options.Profile);
            }

            return [new EmbeddingVector(singlePayload.ModelKey, singlePayload.Dimensions, singlePayload.Values)];
        }

        var batchResponse = await client.PostAsJsonAsync(
            "/embed/batch",
            new BatchEmbeddingServiceEmbedRequest(normalizedItems),
            SerializerOptions,
            cancellationToken);
        batchResponse.EnsureSuccessStatusCode();

        var batchPayload = await batchResponse.Content.ReadFromJsonAsync<BatchEmbeddingServiceEmbedResponse>(cancellationToken: cancellationToken)
            ?? throw new InvalidOperationException("Embedding service returned an empty batch payload.");

        if (batchPayload.Results.Count != normalizedItems.Length)
        {
            throw new InvalidOperationException("Embedding service batch result count does not match request count.");
        }

        var truncatedCount = batchPayload.Results.Count(result => result.Truncated);
        if (truncatedCount > 0)
        {
            logger.LogWarning(
                "Embedding batch truncated {TruncatedCount} items to {MaxTokens} tokens for profile {Profile}.",
                truncatedCount,
                batchPayload.MaxTokens,
                _options.Profile);
        }

        return batchPayload.Results
            .Select(result =>
            {
                if (result.Values.Length == 0)
                {
                    throw new InvalidOperationException("Embedding service returned an empty vector in batch mode.");
                }

                return new EmbeddingVector(batchPayload.ModelKey, batchPayload.Dimensions, result.Values);
            })
            .ToArray();
    }
}

public static class HttpEmbeddingProviderClient
{
    public const string Name = "embedding-service";
}

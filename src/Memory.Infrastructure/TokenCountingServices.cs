using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Memory.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Memory.Infrastructure;

public sealed class TokenCountingService(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingOptions> options,
    ILogger<TokenCountingService> logger) : ITokenCountingService
{
    private const int MaxTokenCountBatchSize = 16;
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        Converters =
        {
            new JsonStringEnumConverter()
        }
    };

    private readonly EmbeddingOptions _options = options.Value;

    public async Task<IReadOnlyList<TokenCountResult>> CountAsync(IReadOnlyList<TokenCountRequest> requests, CancellationToken cancellationToken)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        var approximate = requests
            .Select(request => ContextSavingsEstimator.EstimateTextTokens(request.Text))
            .ToArray();

        if (!_options.Provider.Equals("Http", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(_options.BaseUrl))
        {
            return BuildApproximateResults(approximate);
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpEmbeddingProviderClient.Name);
            var exactResults = new List<BatchTokenCountResult>(requests.Count);
            foreach (var batch in requests.Chunk(MaxTokenCountBatchSize))
            {
                var payload = new BatchEmbeddingServiceTokenCountRequest(
                    batch.Select(request => new EmbeddingServiceTokenCountRequest(request.Text.Trim())).ToArray());
                var response = await client.PostAsJsonAsync("/tokens/count/batch", payload, SerializerOptions, cancellationToken);
                response.EnsureSuccessStatusCode();
                var tokenCounts = await response.Content.ReadFromJsonAsync<BatchEmbeddingServiceTokenCountResponse>(
                    SerializerOptions,
                    cancellationToken)
                    ?? throw new InvalidOperationException("Embedding service returned an empty token count payload.");

                if (tokenCounts.Results.Count != batch.Length)
                {
                    throw new InvalidOperationException("Embedding service token count result count does not match request count.");
                }

                exactResults.AddRange(tokenCounts.Results);
            }

            return exactResults
                .Select((result, index) => new TokenCountResult(
                    approximate[index],
                    result.TokenCount,
                    true,
                    TokenCountingModes.Exact))
                .ToArray();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            logger.LogWarning(ex, "Exact token counting failed. Falling back to approximate token estimates.");
            return BuildApproximateResults(approximate);
        }
    }

    private static IReadOnlyList<TokenCountResult> BuildApproximateResults(IReadOnlyList<int> approximate)
        => approximate
            .Select(tokens => new TokenCountResult(tokens, null, false, TokenCountingModes.Approximate))
            .ToArray();
}

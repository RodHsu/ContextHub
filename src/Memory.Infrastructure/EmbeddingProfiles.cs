namespace Memory.Infrastructure;

public sealed record EmbeddingAssetFile(string RemotePath, string LocalPath);

public sealed record ResolvedEmbeddingProfile(
    string Profile,
    string ModelId,
    string ModelKey,
    int Dimensions,
    int MaxTokens,
    int InferenceThreads,
    int BatchSize,
    string AssetRepository,
    string ModelFile,
    string TokenizerFile,
    IReadOnlyList<EmbeddingAssetFile> AssetFiles);

public interface IResolvedEmbeddingProfileAccessor
{
    ResolvedEmbeddingProfile Current { get; }
}

public sealed class ResolvedEmbeddingProfileAccessor(ResolvedEmbeddingProfile current) : IResolvedEmbeddingProfileAccessor
{
    public ResolvedEmbeddingProfile Current { get; } = current;
}

public static class EmbeddingProfileResolver
{
    private static readonly IReadOnlyDictionary<string, ResolvedEmbeddingProfile> Profiles =
        new Dictionary<string, ResolvedEmbeddingProfile>(StringComparer.OrdinalIgnoreCase)
        {
            ["compact"] = new(
                "compact",
                "intfloat/multilingual-e5-small",
                "intfloat/multilingual-e5-small",
                384,
                512,
                2,
                8,
                "WiseIntelligence/multilingual-e5-small-Optimum-ONNX",
                "model.onnx",
                "sentencepiece.bpe.model",
                [
                    new EmbeddingAssetFile("config.json", "config.json"),
                    new EmbeddingAssetFile("tokenizer.json", "tokenizer.json"),
                    new EmbeddingAssetFile("tokenizer_config.json", "tokenizer_config.json"),
                    new EmbeddingAssetFile("special_tokens_map.json", "special_tokens_map.json"),
                    new EmbeddingAssetFile("sentencepiece.bpe.model", "sentencepiece.bpe.model"),
                    new EmbeddingAssetFile("model.onnx", "model.onnx")
                ]),
            ["balanced"] = new(
                "balanced",
                "intfloat/multilingual-e5-base",
                "intfloat/multilingual-e5-base",
                768,
                512,
                4,
                4,
                "intfloat/multilingual-e5-base",
                "model.onnx",
                "sentencepiece.bpe.model",
                [
                    new EmbeddingAssetFile("onnx/config.json", "config.json"),
                    new EmbeddingAssetFile("onnx/tokenizer.json", "tokenizer.json"),
                    new EmbeddingAssetFile("onnx/tokenizer_config.json", "tokenizer_config.json"),
                    new EmbeddingAssetFile("onnx/special_tokens_map.json", "special_tokens_map.json"),
                    new EmbeddingAssetFile("onnx/sentencepiece.bpe.model", "sentencepiece.bpe.model"),
                    new EmbeddingAssetFile("onnx/model_O4.onnx", "model.onnx")
                ])
        };

    public static ResolvedEmbeddingProfile Resolve(EmbeddingOptions options)
    {
        var requestedProfile = string.IsNullOrWhiteSpace(options.Profile) ? "compact" : options.Profile.Trim();
        if (!Profiles.TryGetValue(requestedProfile, out var selected))
        {
            throw new InvalidOperationException($"Unsupported embedding profile '{requestedProfile}'.");
        }

        if (!string.IsNullOrWhiteSpace(options.ModelId))
        {
            selected = Profiles.Values.FirstOrDefault(x => x.ModelId.Equals(options.ModelId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Unsupported EMBEDDING_MODEL_ID '{options.ModelId}'. Only the built-in E5 profiles are supported.");
        }

        var modelKey = string.IsNullOrWhiteSpace(options.ModelKey) ? selected.ModelKey : options.ModelKey.Trim();
        var dimensions = options.Dimensions > 0 ? options.Dimensions : selected.Dimensions;
        var maxTokens = options.MaxTokens > 0 ? options.MaxTokens : selected.MaxTokens;
        var inferenceThreads = options.InferenceThreads > 0
            ? options.InferenceThreads
            : Math.Min(Environment.ProcessorCount, 6);
        var batchSize = options.BatchSize > 0 ? options.BatchSize : selected.BatchSize;
        batchSize = Math.Clamp(batchSize, 1, 16);

        return selected with
        {
            ModelKey = modelKey,
            Dimensions = dimensions,
            MaxTokens = maxTokens,
            InferenceThreads = inferenceThreads,
            BatchSize = batchSize
        };
    }

    public static void ApplyResolvedDefaults(EmbeddingOptions options)
    {
        var resolved = Resolve(options);
        options.Profile = resolved.Profile;
        options.ModelId = resolved.ModelId;
        options.ModelKey = resolved.ModelKey;
        options.Dimensions = resolved.Dimensions;
        options.MaxTokens = resolved.MaxTokens;
        options.InferenceThreads = resolved.InferenceThreads;
        options.BatchSize = resolved.BatchSize;
    }
}

public sealed record EmbeddingServiceEmbedRequest(string Text, Memory.Application.EmbeddingPurpose Purpose);

public sealed record EmbeddingServiceEmbedResponse(
    string ModelKey,
    int Dimensions,
    int MaxTokens,
    int TokenCount,
    bool Truncated,
    float[] Values);

public sealed record BatchEmbeddingServiceEmbedRequest(
    IReadOnlyList<EmbeddingServiceEmbedRequest> Items);

public sealed record BatchEmbeddingResult(
    int TokenCount,
    bool Truncated,
    float[] Values);

public sealed record BatchEmbeddingServiceEmbedResponse(
    string ModelKey,
    int Dimensions,
    int MaxTokens,
    IReadOnlyList<BatchEmbeddingResult> Results);

public sealed record EmbeddingServiceInfoResult(
    string Profile,
    string ModelId,
    string ModelKey,
    int Dimensions,
    int MaxTokens,
    string ExecutionProvider,
    int InferenceThreads,
    int BatchSize,
    bool BatchingEnabled,
    bool Ready);

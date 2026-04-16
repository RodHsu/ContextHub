using System.Net.Http.Headers;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;
using Microsoft.Extensions.Options;
using Memory.Application;
using Memory.Infrastructure;
using OnnxSessionOptions = Microsoft.ML.OnnxRuntime.SessionOptions;

namespace Memory.EmbeddingServer;

internal sealed class OnnxEmbeddingRuntime(
    IHttpClientFactory httpClientFactory,
    IOptions<EmbeddingOptions> options,
    IResolvedEmbeddingProfileAccessor profileAccessor,
    ILogger<OnnxEmbeddingRuntime> logger) : IAsyncDisposable
{
    public const string ExecutionProviderName = "CPUExecutionProvider";
    public const int MaxBatchSize = 16;

    private readonly EmbeddingOptions _options = options.Value;
    private readonly ResolvedEmbeddingProfile _profile = profileAccessor.Current;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private readonly SemaphoreSlim _inferenceGate = new(1, 1);
    private InferenceSession? _session;
    private SentencePieceTokenizer? _tokenizer;
    private string? _modelDirectory;

    public bool IsReady { get; private set; }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (IsReady)
        {
            return;
        }

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (IsReady)
            {
                return;
            }

            _modelDirectory = Path.Combine(_options.ModelCachePath, SanitizePathSegment(_profile.Profile));
            Directory.CreateDirectory(_modelDirectory);

            await EnsureModelAssetsAsync(_modelDirectory, cancellationToken);
            _tokenizer = await LoadTokenizerAsync(_modelDirectory, cancellationToken);
            _session = CreateSession(Path.Combine(_modelDirectory, _profile.ModelFile));

            var probe = await EmbedCoreBatchAsync([new EmbeddingServiceEmbedRequest("health probe", EmbeddingPurpose.Query)], cancellationToken);
            if (probe.Dimensions != _profile.Dimensions)
            {
                throw new InvalidOperationException(
                    $"Resolved embedding dimension {_profile.Dimensions} does not match actual output {probe.Dimensions} for model '{_profile.ModelKey}'.");
            }

            IsReady = true;
            logger.LogInformation(
                "Embedding runtime initialized for profile {Profile} using model {ModelKey} ({Dimensions} dims).",
                _profile.Profile,
                _profile.ModelKey,
                _profile.Dimensions);
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    public async Task<EmbeddingServiceEmbedResponse> EmbedAsync(EmbeddingServiceEmbedRequest request, CancellationToken cancellationToken)
    {
        if (!IsReady)
        {
            throw new InvalidOperationException("Embedding runtime is not ready.");
        }

        var batch = await EmbedCoreBatchAsync([request], cancellationToken);
        var result = batch.Results[0];
        return new EmbeddingServiceEmbedResponse(
            batch.ModelKey,
            batch.Dimensions,
            batch.MaxTokens,
            result.TokenCount,
            result.Truncated,
            result.Values);
    }

    public async Task<BatchEmbeddingServiceEmbedResponse> EmbedBatchAsync(BatchEmbeddingServiceEmbedRequest request, CancellationToken cancellationToken)
    {
        if (!IsReady)
        {
            throw new InvalidOperationException("Embedding runtime is not ready.");
        }

        if (request.Items.Count == 0)
        {
            throw new InvalidOperationException("Batch request must contain at least one item.");
        }

        if (request.Items.Count > MaxBatchSize)
        {
            throw new InvalidOperationException($"Batch request exceeds the maximum supported size of {MaxBatchSize}.");
        }

        return await EmbedCoreBatchAsync(request.Items, cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        _session?.Dispose();
        _initializationGate.Dispose();
        _inferenceGate.Dispose();
        await Task.CompletedTask;
    }

    private async Task EnsureModelAssetsAsync(string modelDirectory, CancellationToken cancellationToken)
    {
        foreach (var asset in _profile.AssetFiles)
        {
            var targetPath = Path.Combine(modelDirectory, asset.LocalPath);
            if (File.Exists(targetPath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var url = $"https://huggingface.co/{_profile.AssetRepository}/resolve/main/{asset.RemotePath}?download=true";
            logger.LogInformation("Downloading embedding asset {Asset} from {Repository}", asset.RemotePath, _profile.AssetRepository);

            var client = httpClientFactory.CreateClient(ModelAssetDownloadClient.Name);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var httpStream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var tempPath = $"{targetPath}.download";
            await using var fileStream = File.Create(tempPath);
            await httpStream.CopyToAsync(fileStream, cancellationToken);
            await fileStream.FlushAsync(cancellationToken);

            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }

            File.Move(tempPath, targetPath);
        }
    }

    private async Task<SentencePieceTokenizer> LoadTokenizerAsync(string modelDirectory, CancellationToken cancellationToken)
    {
        var tokenizerPath = Path.Combine(modelDirectory, _profile.TokenizerFile);
        if (!File.Exists(tokenizerPath))
        {
            throw new FileNotFoundException($"Tokenizer asset '{tokenizerPath}' was not found.");
        }

        await using var stream = File.OpenRead(tokenizerPath);
        cancellationToken.ThrowIfCancellationRequested();
        return SentencePieceTokenizer.Create(stream, addBeginningOfSentence: true, addEndOfSentence: true, specialTokens: new Dictionary<string, int>());
    }

    private InferenceSession CreateSession(string modelPath)
    {
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException($"Model asset '{modelPath}' was not found.");
        }

        var sessionOptions = new OnnxSessionOptions
        {
            InterOpNumThreads = 1,
            IntraOpNumThreads = Math.Max(1, _profile.InferenceThreads),
            GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL
        };

        return new InferenceSession(modelPath, sessionOptions);
    }

    private async Task<BatchEmbeddingServiceEmbedResponse> EmbedCoreBatchAsync(
        IReadOnlyList<EmbeddingServiceEmbedRequest> requests,
        CancellationToken cancellationToken)
    {
        var tokenizer = _tokenizer ?? throw new InvalidOperationException("Tokenizer is not initialized.");
        var session = _session ?? throw new InvalidOperationException("ONNX session is not initialized.");
        var preparedInputs = requests
            .Select(request => PrepareInput(tokenizer, request.Text, request.Purpose))
            .ToArray();

        await _inferenceGate.WaitAsync(cancellationToken);
        try
        {
            using var results = session.Run(BuildInputs(session, preparedInputs));
            var output = ResolveOutput(results, session);
            var vectors = ExtractVectors(output, preparedInputs.Select(x => x.AttentionMask).ToArray());
            if (vectors.Length != requests.Count)
            {
                throw new InvalidOperationException(
                    $"Embedding output count {vectors.Length} does not match request count {requests.Count}.");
            }

            foreach (var vector in vectors)
            {
                Normalize(vector);
            }

            return new BatchEmbeddingServiceEmbedResponse(
                _profile.ModelKey,
                vectors[0].Length,
                _profile.MaxTokens,
                requests.Select((_, index) => new BatchEmbeddingResult(
                    preparedInputs[index].TokenCount,
                    preparedInputs[index].Truncated,
                    vectors[index]))
                    .ToArray());
        }
        finally
        {
            _inferenceGate.Release();
        }
    }

    private PreparedEmbeddingInput PrepareInput(SentencePieceTokenizer tokenizer, string text, EmbeddingPurpose purpose)
    {
        var normalizedText = text.Trim();
        var preparedText = purpose == EmbeddingPurpose.Query ? $"query: {normalizedText}" : $"passage: {normalizedText}";
        var tokenIds = tokenizer.EncodeToIds(preparedText, true, true, true, true).ToArray();
        var tokenCount = tokenIds.Length;
        var truncated = tokenCount > _profile.MaxTokens;
        if (truncated)
        {
            tokenIds = tokenIds.Take(_profile.MaxTokens).ToArray();
        }

        if (tokenIds.Length == 0)
        {
            throw new InvalidOperationException("Tokenizer produced zero tokens.");
        }

        return new PreparedEmbeddingInput(
            tokenIds.Select(x => (long)x).ToArray(),
            Enumerable.Repeat(1L, tokenIds.Length).ToArray(),
            tokenCount,
            truncated);
    }

    private static List<NamedOnnxValue> BuildInputs(InferenceSession session, IReadOnlyList<PreparedEmbeddingInput> inputsBatch)
    {
        var inputs = new List<NamedOnnxValue>(capacity: 3);
        var batchSize = inputsBatch.Count;
        var maxSequenceLength = inputsBatch.Max(x => x.InputIds.Length);
        var shape = new[] { batchSize, maxSequenceLength };

        if (session.InputMetadata.ContainsKey("input_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("input_ids", CreateTensor(inputsBatch.Select(x => x.InputIds).ToArray(), shape)));
        }

        if (session.InputMetadata.ContainsKey("attention_mask"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("attention_mask", CreateTensor(inputsBatch.Select(x => x.AttentionMask).ToArray(), shape)));
        }

        if (session.InputMetadata.ContainsKey("token_type_ids"))
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor("token_type_ids", CreateTensor(new long[batchSize][], shape)));
        }

        return inputs;
    }

    private static DenseTensor<long> CreateTensor(IReadOnlyList<long>[] values, int[] shape)
    {
        var tensor = new DenseTensor<long>(shape);
        for (var batchIndex = 0; batchIndex < values.Length; batchIndex++)
        {
            var row = values[batchIndex] ?? [];
            for (var tokenIndex = 0; tokenIndex < row.Count; tokenIndex++)
            {
                tensor[batchIndex, tokenIndex] = row[tokenIndex];
            }
        }

        return tensor;
    }

    private static DisposableNamedOnnxValue ResolveOutput(IDisposableReadOnlyCollection<DisposableNamedOnnxValue> results, InferenceSession session)
    {
        var outputName = new[]
        {
            "sentence_embedding",
            "last_hidden_state",
            "token_embeddings",
            session.OutputMetadata.Keys.FirstOrDefault()
        }
        .Where(x => !string.IsNullOrWhiteSpace(x))
        .FirstOrDefault(name => results.Any(result => string.Equals(result.Name, name, StringComparison.Ordinal)))
            ?? throw new InvalidOperationException("Embedding model did not expose a supported output tensor.");

        return results.Single(x => string.Equals(x.Name, outputName, StringComparison.Ordinal));
    }

    private static float[][] ExtractVectors(DisposableNamedOnnxValue output, IReadOnlyList<long>[] attentionMasks)
    {
        var tensor = output.AsTensor<float>();
        var batchSize = attentionMasks.Length;

        if (tensor.Rank == 1)
        {
            return [tensor.ToArray()];
        }

        if (tensor.Rank == 2)
        {
            if (tensor.Dimensions[0] != batchSize)
            {
                if (batchSize == 1)
                {
                    return [tensor.ToArray()];
                }

                throw new InvalidOperationException(
                    $"Embedding output batch dimension {tensor.Dimensions[0]} does not match request batch size {batchSize}.");
            }

            var hiddenDimension = tensor.Dimensions[1];
            var vectors = new float[batchSize][];
            for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
            {
                var vector = new float[hiddenDimension];
                for (var hiddenIndex = 0; hiddenIndex < hiddenDimension; hiddenIndex++)
                {
                    vector[hiddenIndex] = tensor[batchIndex, hiddenIndex];
                }

                vectors[batchIndex] = vector;
            }

            return vectors;
        }

        if (tensor.Rank != 3)
        {
            throw new InvalidOperationException($"Unsupported embedding output rank {tensor.Rank}.");
        }

        var sequenceLength = tensor.Dimensions[1];
        var hiddenSize = tensor.Dimensions[2];
        var pooledVectors = new float[batchSize][];

        for (var batchIndex = 0; batchIndex < batchSize; batchIndex++)
        {
            var attentionMask = attentionMasks[batchIndex];
            var pooled = new float[hiddenSize];
            var validTokens = 0;

            for (var tokenIndex = 0; tokenIndex < sequenceLength && tokenIndex < attentionMask.Count; tokenIndex++)
            {
                if (attentionMask[tokenIndex] == 0)
                {
                    continue;
                }

                validTokens++;
                for (var hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
                {
                    pooled[hiddenIndex] += tensor[batchIndex, tokenIndex, hiddenIndex];
                }
            }

            if (validTokens > 0)
            {
                for (var hiddenIndex = 0; hiddenIndex < hiddenSize; hiddenIndex++)
                {
                    pooled[hiddenIndex] /= validTokens;
                }
            }

            pooledVectors[batchIndex] = pooled;
        }

        return pooledVectors;
    }

    private static void Normalize(float[] values)
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

    private static string SanitizePathSegment(string value)
        => string.Concat(value.Select(ch => char.IsLetterOrDigit(ch) ? ch : '-'));

    private sealed record PreparedEmbeddingInput(
        long[] InputIds,
        long[] AttentionMask,
        int TokenCount,
        bool Truncated);
}

internal static class ModelAssetDownloadClient
{
    public const string Name = "model-assets";
}

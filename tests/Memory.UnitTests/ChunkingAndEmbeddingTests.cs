using Memory.Application;
using Memory.Domain;
using Memory.Infrastructure;
using Memory.Worker;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace Memory.UnitTests;

public sealed class ChunkingAndEmbeddingTests
{
    [Fact]
    public void Chunking_Splits_Long_Document_Into_Multiple_Chunks()
    {
        var service = new ChunkingService();
        var content = string.Join(
            Environment.NewLine + Environment.NewLine,
            Enumerable.Range(0, 12).Select(i => $"Section {i} " + string.Join(' ', Enumerable.Repeat("token", 60))));

        var chunks = service.Chunk(MemoryType.Fact, "document", content);

        Assert.True(chunks.Count > 1);
        Assert.Equal(Enumerable.Range(0, chunks.Count), chunks.Select(x => x.Index));
    }

    [Fact]
    public void Chunking_Should_Split_Long_Cjk_Paragraph_Without_Whitespace()
    {
        var service = new ChunkingService();
        var sentence = "這是一段沒有空白但會持續延伸的中文內容，用來模擬 tokenizer 實際上會切出很多 token 的情境。";
        var content = string.Concat(Enumerable.Repeat(sentence, 40));

        var chunks = service.Chunk(MemoryType.Fact, "document", content);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(ChunkingService.ApproximateTokenCount(chunk.Text) <= 360));
    }

    [Fact]
    public void Chunking_Should_Split_Oversized_Code_Block_By_Line()
    {
        var service = new ChunkingService();
        var longLine = "var payload = new { Name = \"ContextHub\", Description = \"" + new string('x', 200) + "\" };";
        var content = string.Join(Environment.NewLine, Enumerable.Repeat(longLine, 60));

        var chunks = service.Chunk(MemoryType.Artifact, "code/csharp", content);

        Assert.True(chunks.Count > 1);
        Assert.All(chunks, chunk => Assert.True(ChunkingService.ApproximateTokenCount(chunk.Text) <= 180));
    }

    [Fact]
    public async Task Deterministic_Embedding_Is_Stable_For_Same_Input()
    {
        var provider = new DeterministicEmbeddingProvider(Options.Create(new EmbeddingOptions
        {
            ModelKey = "deterministic-384",
            Dimensions = 384
        }));

        var first = await provider.EmbedAsync("NullReferenceException in worker pipeline", EmbeddingPurpose.Query, CancellationToken.None);
        var second = await provider.EmbedAsync("NullReferenceException in worker pipeline", EmbeddingPurpose.Query, CancellationToken.None);

        Assert.Equal(first.Values, second.Values);
        Assert.Equal(384, first.Values.Length);
    }

    [Fact]
    public async Task Deterministic_Embedding_Changes_When_Purpose_Changes()
    {
        var provider = new DeterministicEmbeddingProvider(Options.Create(new EmbeddingOptions
        {
            ModelKey = "deterministic-384",
            Dimensions = 384
        }));

        var document = await provider.EmbedAsync("worker null reference", EmbeddingPurpose.Document, CancellationToken.None);
        var query = await provider.EmbedAsync("worker null reference", EmbeddingPurpose.Query, CancellationToken.None);

        Assert.NotEqual(document.Values, query.Values);
    }

    [Fact]
    public async Task Deterministic_Embedding_Batch_Should_Preserve_Order()
    {
        var provider = new DeterministicEmbeddingProvider(Options.Create(new EmbeddingOptions
        {
            ModelKey = "deterministic-384",
            Dimensions = 384,
            InferenceThreads = 0,
            BatchSize = 8
        }));

        var batch = await provider.EmbedBatchAsync(
        [
            new BatchEmbeddingItem("worker null reference", EmbeddingPurpose.Document),
            new BatchEmbeddingItem("status endpoint", EmbeddingPurpose.Query)
        ], CancellationToken.None);

        Assert.Equal(2, batch.Count);
        Assert.Equal(384, batch[0].Values.Length);
        Assert.Equal(384, batch[1].Values.Length);
        Assert.NotEqual(batch[0].Values, batch[1].Values);
    }

    [Fact]
    public void Embedding_Profile_Resolver_Should_Resolve_Compact_Defaults()
    {
        var resolved = EmbeddingProfileResolver.Resolve(new EmbeddingOptions
        {
            Profile = "compact"
        });

        Assert.Equal("compact", resolved.Profile);
        Assert.Equal("intfloat/multilingual-e5-small", resolved.ModelId);
        Assert.Equal(384, resolved.Dimensions);
        Assert.Equal(512, resolved.MaxTokens);
        Assert.Equal(Math.Min(Environment.ProcessorCount, 6), resolved.InferenceThreads);
        Assert.Equal(8, resolved.BatchSize);
    }

    [Fact]
    public void Embedding_Profile_Resolver_Should_Respect_Overrides()
    {
        var resolved = EmbeddingProfileResolver.Resolve(new EmbeddingOptions
        {
            Profile = "balanced",
            Dimensions = 768,
            MaxTokens = 256,
            InferenceThreads = 6,
            BatchSize = 1
        });

        Assert.Equal("balanced", resolved.Profile);
        Assert.Equal("intfloat/multilingual-e5-base", resolved.ModelId);
        Assert.Equal(768, resolved.Dimensions);
        Assert.Equal(256, resolved.MaxTokens);
        Assert.Equal(6, resolved.InferenceThreads);
        Assert.Equal(1, resolved.BatchSize);
    }

    [Fact]
    public void Embedding_Profile_Resolver_Should_Auto_Resolve_Threads_And_Clamp_Batch_Size()
    {
        var resolved = EmbeddingProfileResolver.Resolve(new EmbeddingOptions
        {
            Profile = "balanced",
            InferenceThreads = 0,
            BatchSize = 64
        });

        Assert.Equal(Math.Min(Environment.ProcessorCount, 6), resolved.InferenceThreads);
        Assert.Equal(16, resolved.BatchSize);
    }

    [Fact]
    public async Task Http_Embedding_Provider_Should_Map_Batch_Response_In_Order()
    {
        var handler = new StubHttpMessageHandler(request =>
        {
            Assert.Equal(HttpMethod.Post, request.Method);
            Assert.Equal("/embed/batch", request.RequestUri!.AbsolutePath);
            var requestBody = request.Content!.ReadAsStringAsync().GetAwaiter().GetResult();
            Assert.Contains("\"purpose\":\"Document\"", requestBody);

            var payload = JsonSerializer.Serialize(new BatchEmbeddingServiceEmbedResponse(
                "intfloat/multilingual-e5-small",
                384,
                512,
                [
                    new BatchEmbeddingResult(5, false, [1f, 0f]),
                    new BatchEmbeddingResult(7, false, [0f, 1f])
                ]));

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json")
            };
        });

        var factory = new StubHttpClientFactory(new HttpClient(handler)
        {
            BaseAddress = new Uri("http://embedding-service")
        });
        var provider = new HttpEmbeddingProvider(factory, Options.Create(new EmbeddingOptions
        {
            Provider = "Http",
            BaseUrl = "http://embedding-service",
            Profile = "compact",
            ModelKey = "intfloat/multilingual-e5-small",
            Dimensions = 384,
            MaxTokens = 512,
            BatchSize = 8,
            InferenceThreads = 4
        }), NullLogger<HttpEmbeddingProvider>.Instance);

        var results = await provider.EmbedBatchAsync(
        [
            new BatchEmbeddingItem("first", EmbeddingPurpose.Document),
            new BatchEmbeddingItem("second", EmbeddingPurpose.Document)
        ], CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.Equal([1f, 0f], results[0].Values);
        Assert.Equal([0f, 1f], results[1].Values);
    }

    [Fact]
    public async Task JobWorker_Should_Wait_For_Redis_Signal_When_No_Job_Is_Available()
    {
        var processor = new StubBackgroundJobProcessor();
        var cacheStore = new RecordingCacheVersionStore();
        var worker = new JobWorker(processor, cacheStore, NullLogger<JobWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);
        await cacheStore.WaitCalled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        Assert.True(cacheStore.WaitCallCount >= 1);
    }

    private sealed class StubHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(handler(request));
    }

    private sealed class StubBackgroundJobProcessor : IBackgroundJobProcessor
    {
        public Task<JobResult?> ProcessNextAsync(CancellationToken cancellationToken) => Task.FromResult<JobResult?>(null);
    }

    private sealed class RecordingCacheVersionStore : ICacheVersionStore
    {
        public TaskCompletionSource<bool> WaitCalled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int WaitCallCount { get; private set; }

        public Task<long> GetVersionAsync(CancellationToken cancellationToken) => Task.FromResult(1L);

        public Task<long> IncrementAsync(CancellationToken cancellationToken) => Task.FromResult(2L);

        public Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken) => Task.FromResult<T?>(default);

        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task PublishJobSignalAsync(Guid jobId, CancellationToken cancellationToken) => Task.CompletedTask;

        public async Task<bool> WaitForJobSignalAsync(TimeSpan timeout, CancellationToken cancellationToken)
        {
            WaitCallCount++;
            WaitCalled.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return false;
        }
    }
}

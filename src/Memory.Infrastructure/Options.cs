using Microsoft.Extensions.Logging;

namespace Memory.Infrastructure;

public sealed class MemoryOptions
{
    public const string SectionName = "Memory";
    public string Namespace { get; set; } = "context-hub";
}

public sealed class EmbeddingOptions
{
    public const string SectionName = "Embeddings";
    public string Provider { get; set; } = "Deterministic";
    public string Profile { get; set; } = "compact";
    public string ModelId { get; set; } = string.Empty;
    public string ModelKey { get; set; } = string.Empty;
    public int Dimensions { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public int TimeoutSeconds { get; set; } = 30;
    public string ModelPath { get; set; } = string.Empty;
    public string TokenizerPath { get; set; } = string.Empty;
    public string InputIdsName { get; set; } = "input_ids";
    public string AttentionMaskName { get; set; } = "attention_mask";
    public string TokenTypeIdsName { get; set; } = string.Empty;
    public string OutputName { get; set; } = "last_hidden_state";
    public int MaxTokens { get; set; }
    public int InferenceThreads { get; set; }
    public int BatchSize { get; set; }
    public string ModelCachePath { get; set; } = "/models";
}

public sealed class DatabaseLoggingOptions
{
    public const string SectionName = "DatabaseLogging";
    public string ServiceName { get; set; } = "memory-service";
    public LogLevel MinimumLevel { get; set; } = LogLevel.Warning;
    public int BatchSize { get; set; } = 50;
    public int FlushIntervalSeconds { get; set; } = 2;
}

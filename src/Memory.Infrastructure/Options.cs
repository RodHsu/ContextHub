using Microsoft.Extensions.Logging;

namespace Memory.Infrastructure;

public sealed class MemoryOptions
{
    public const string SectionName = "Memory";
    public string Namespace { get; set; } = "context-hub";
    public RedisCacheOptions RedisCache { get; set; } = new();
}

public sealed class RedisCacheOptions
{
    public bool Enabled { get; set; } = true;
    public int SearchTtlMinutes { get; set; } = 15;
    public int WorkingContextTtlMinutes { get; set; } = 15;
    public int EmbeddingTtlHours { get; set; } = 24;
    public int SemanticHitTtlMinutes { get; set; } = 10;
    public int MetadataTtlSeconds { get; set; } = 60;
    public int SecurityTtlSeconds { get; set; } = 30;

    public TimeSpan SearchTtl => TimeSpan.FromMinutes(Math.Max(1, SearchTtlMinutes));
    public TimeSpan WorkingContextTtl => TimeSpan.FromMinutes(Math.Max(1, WorkingContextTtlMinutes));
    public TimeSpan EmbeddingTtl => TimeSpan.FromHours(Math.Max(1, EmbeddingTtlHours));
    public TimeSpan SemanticHitTtl => TimeSpan.FromMinutes(Math.Max(1, SemanticHitTtlMinutes));
    public TimeSpan MetadataTtl => TimeSpan.FromSeconds(Math.Max(1, MetadataTtlSeconds));
    public TimeSpan SecurityTtl => TimeSpan.FromSeconds(Math.Max(1, SecurityTtlSeconds));
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

public sealed class TelemetryRetentionOptions
{
    public const string SectionName = "TelemetryRetention";
    public bool Enabled { get; set; } = true;
    public int HitsRetentionDays { get; set; } = 3;
    public int EventsRetentionDays { get; set; } = 7;
    public int SummaryRetentionDays { get; set; } = 30;
    public int SecurityAuditRetentionDays { get; set; } = 180;
    public int RuntimeLogRetentionDays { get; set; } = 30;
    public int MaintenanceRunRetentionDays { get; set; } = 180;
    public int HitSummaryTopPerBucket { get; set; } = 100;
    public int MaxSummaryDaysPerRun { get; set; } = 3;
    public string RunAtLocalTime { get; set; } = "03:00";
    public string TimeZone { get; set; } = "Asia/Taipei";
    public int BatchSize { get; set; } = 5_000;
    public int EventBatchSize { get; set; } = 1_000;
    public int TimeWindowDays { get; set; } = 3;
    public int DelayBetweenBatchesMs { get; set; } = 250;
    public int CommandTimeoutSeconds { get; set; } = 300;
    public int MaxDurationMinutes { get; set; } = 120;
    public bool RunVacuumAnalyzeAfterRetention { get; set; } = true;
    public bool RunVacuumFullAutomatically { get; set; }
}

public sealed class MemoryDataRetentionOptions
{
    public const string SectionName = "MemoryDataRetention";
    public bool Enabled { get; set; } = true;
    public bool AutoApplyEnabled { get; set; }
    public int ArchivedItemsRetentionDays { get; set; } = 365;
    public int HitWindowDays { get; set; } = 180;
    public long MaxRecentHitCount { get; set; }
    public int MaxLinkDegree { get; set; }
    public decimal MaxImportance { get; set; } = 0.55m;
    public decimal MaxConfidence { get; set; } = 0.70m;
    public int PreviewLimit { get; set; } = 50;
    public int BatchSize { get; set; } = 1_000;
    public int DelayBetweenBatchesMs { get; set; } = 150;
    public string RunAtLocalTime { get; set; } = "04:00";
    public string TimeZone { get; set; } = "Asia/Taipei";
    public int CommandTimeoutSeconds { get; set; } = 300;
    public int MaxDurationMinutes { get; set; } = 20;
}

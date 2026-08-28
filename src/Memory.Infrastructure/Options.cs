using Memory.Application;
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

public sealed class AgentConnectivityTelemetryOptions
{
    public const string SectionName = "AgentConnectivityTelemetry";
    public bool Enabled { get; set; } = true;
    public string Profile { get; set; } = "Balanced";
    public double SuccessSampleRate { get; set; } = 0.2;
    public double FailureSampleRate { get; set; } = 1.0;
    public int ProbeIntervalSeconds { get; set; } = 60;
    public int UploadIntervalSeconds { get; set; } = 15;
    public int MaxBatchSize { get; set; } = 100;
    public int MaxSamplesPerAgentMethodPerMinute { get; set; } = 60;
    public int RawRetentionDays { get; set; } = 7;
    public int SummaryRetentionDays { get; set; } = 14;

    public AgentConnectivityTelemetryProfile ResolvedProfile
        => Enum.TryParse<AgentConnectivityTelemetryProfile>(Profile, ignoreCase: true, out var parsed)
            ? parsed
            : AgentConnectivityTelemetryProfile.Balanced;

    public double NormalizedSuccessSampleRate
        => Math.Clamp(SuccessSampleRate, 0, 1);

    public double NormalizedFailureSampleRate
        => Math.Clamp(FailureSampleRate, 0, 1);

    public int NormalizedProbeIntervalSeconds
        => Math.Clamp(ProbeIntervalSeconds, 10, 86_400);

    public int NormalizedUploadIntervalSeconds
        => Math.Clamp(UploadIntervalSeconds, 1, 3_600);

    public int NormalizedMaxBatchSize
        => Math.Clamp(MaxBatchSize, 1, 1_000);

    public int NormalizedMaxSamplesPerAgentMethodPerMinute
        => Math.Clamp(MaxSamplesPerAgentMethodPerMinute, 1, 10_000);

    public int NormalizedRawRetentionDays
        => Math.Clamp(RawRetentionDays, 1, 31);

    public int NormalizedSummaryRetentionDays
        => Math.Clamp(SummaryRetentionDays, NormalizedRawRetentionDays, 90);
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
    public int RevisionRetentionDays { get; set; } = 90;
    public int MinRevisionsToKeep { get; set; } = 20;
    public int MaxChunksPerMemoryItem { get; set; } = 5_000;
    public string RunAtLocalTime { get; set; } = "04:00";
    public string TimeZone { get; set; } = "Asia/Taipei";
    public int CommandTimeoutSeconds { get; set; } = 300;
    public int MaxDurationMinutes { get; set; } = 20;
}

public sealed class AutonomousGovernanceOptions
{
    public const string SectionName = "AutonomousGovernance";
    public string RetentionPolicyVersion { get; set; } = "2026-08-28-v1";
    public int MachineExecutionEvidenceGraceDays { get; set; } = 7;
    public int RuntimeNoiseGraceDays { get; set; } = 14;
    public int AutomatedEpisodeGraceDays { get; set; } = 30;
    public int TemporaryArtifactGraceDays { get; set; } = 30;
    public int HitWindowDays { get; set; } = 30;
    public long MaxRecentHitCount { get; set; }
    public int MaxLinkDegree { get; set; }
    public decimal MaxImportance { get; set; } = 0.35m;
    public decimal MaxConfidence { get; set; } = 0.60m;

    public int NormalizedMachineExecutionEvidenceGraceDays => Math.Clamp(MachineExecutionEvidenceGraceDays, 1, 3650);
    public int NormalizedRuntimeNoiseGraceDays => Math.Clamp(RuntimeNoiseGraceDays, 1, 3650);
    public int NormalizedAutomatedEpisodeGraceDays => Math.Clamp(AutomatedEpisodeGraceDays, 1, 3650);
    public int NormalizedTemporaryArtifactGraceDays => Math.Clamp(TemporaryArtifactGraceDays, 1, 3650);
    public int NormalizedHitWindowDays => Math.Clamp(HitWindowDays, 1, 3650);
    public long NormalizedMaxRecentHitCount => Math.Max(0, MaxRecentHitCount);
    public int NormalizedMaxLinkDegree => Math.Max(0, MaxLinkDegree);
    public decimal NormalizedMaxImportance => Math.Clamp(MaxImportance, 0m, 1m);
    public decimal NormalizedMaxConfidence => Math.Clamp(MaxConfidence, 0m, 1m);
}

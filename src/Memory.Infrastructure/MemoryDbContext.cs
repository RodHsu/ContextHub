using Microsoft.EntityFrameworkCore;
using System.Text.Json;
using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace Memory.Infrastructure;

public sealed class MemoryDbContext(DbContextOptions<MemoryDbContext> options) : DbContext(options), IApplicationDbContext
{
    private static readonly ValueConverter<string, JsonDocument> JsonDocumentConverter = new(
        value => JsonDocument.Parse(string.IsNullOrWhiteSpace(value) ? "{}" : value),
        document => document.RootElement.GetRawText());

    private static readonly ValueComparer<string> JsonStringComparer = new(
        (left, right) => string.Equals(NormalizeJson(left), NormalizeJson(right), StringComparison.Ordinal),
        value => NormalizeJson(value).GetHashCode(StringComparison.Ordinal),
        value => value);

    public DbSet<InstanceSetting> InstanceSettings => Set<InstanceSetting>();
    public DbSet<MemoryItem> MemoryItems => Set<MemoryItem>();
    public DbSet<MemoryItemRevision> MemoryItemRevisions => Set<MemoryItemRevision>();
    public DbSet<MemoryItemChunk> MemoryItemChunks => Set<MemoryItemChunk>();
    public DbSet<MemoryChunkVector> MemoryChunkVectors => Set<MemoryChunkVector>();
    public DbSet<MemoryLink> MemoryLinks => Set<MemoryLink>();
    public DbSet<MemoryJob> MemoryJobs => Set<MemoryJob>();
    public DbSet<RuntimeLogEntry> RuntimeLogEntries => Set<RuntimeLogEntry>();
    public DbSet<RetrievalEvent> RetrievalEvents => Set<RetrievalEvent>();
    public DbSet<RetrievalHit> RetrievalHits => Set<RetrievalHit>();
    public DbSet<LogIngestionCheckpoint> LogIngestionCheckpoints => Set<LogIngestionCheckpoint>();
    public DbSet<SourceConnection> SourceConnections => Set<SourceConnection>();
    public DbSet<SourceSyncRun> SourceSyncRuns => Set<SourceSyncRun>();
    public DbSet<GovernanceFinding> GovernanceFindings => Set<GovernanceFinding>();
    public DbSet<EvaluationSuite> EvaluationSuites => Set<EvaluationSuite>();
    public DbSet<EvaluationCase> EvaluationCases => Set<EvaluationCase>();
    public DbSet<EvaluationRun> EvaluationRuns => Set<EvaluationRun>();
    public DbSet<EvaluationRunItem> EvaluationRunItems => Set<EvaluationRunItem>();
    public DbSet<SuggestedAction> SuggestedActions => Set<SuggestedAction>();
    public DbSet<ConversationSession> ConversationSessions => Set<ConversationSession>();
    public DbSet<ConversationCheckpoint> ConversationCheckpoints => Set<ConversationCheckpoint>();
    public DbSet<ConversationInsight> ConversationInsights => Set<ConversationInsight>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<InstanceSetting>(entity =>
        {
            entity.ToTable("instance_settings");
            entity.HasKey(x => new { x.InstanceId, x.SettingKey });
            entity.Property(x => x.InstanceId).HasColumnName("instance_id");
            entity.Property(x => x.SettingKey).HasColumnName("setting_key");
            entity.Property(x => x.ValueJson)
                .HasColumnName("value_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.Revision).HasColumnName("revision");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.UpdatedBy).HasColumnName("updated_by");
        });

        modelBuilder.Entity<MemoryItem>(entity =>
        {
            entity.ToTable("memory_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.ExternalKey).HasColumnName("external_key");
            entity.Property(x => x.Scope).HasColumnName("scope").HasConversion<string>();
            entity.Property(x => x.MemoryType).HasColumnName("memory_type").HasConversion<string>();
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Content).HasColumnName("content");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.Tags).HasColumnName("tags").HasColumnType("text[]");
            entity.Property(x => x.SourceType).HasColumnName("source_type");
            entity.Property(x => x.SourceRef).HasColumnName("source_ref");
            entity.Property(x => x.Importance).HasColumnName("importance");
            entity.Property(x => x.Confidence).HasColumnName("confidence");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.IsReadOnly).HasColumnName("is_read_only");
            entity.Property(x => x.MetadataJson).HasColumnName("metadata_json");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.ProjectId, x.ExternalKey }).IsUnique();
            entity.HasMany(x => x.Revisions).WithOne(x => x.MemoryItem).HasForeignKey(x => x.MemoryItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Chunks).WithOne(x => x.MemoryItem).HasForeignKey(x => x.MemoryItemId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemoryItemRevision>(entity =>
        {
            entity.ToTable("memory_item_revisions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.MemoryItemId).HasColumnName("memory_item_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Content).HasColumnName("content");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.MetadataJson).HasColumnName("metadata_json");
            entity.Property(x => x.ChangedBy).HasColumnName("changed_by");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<MemoryItemChunk>(entity =>
        {
            entity.ToTable("memory_item_chunks");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.MemoryItemId).HasColumnName("memory_item_id");
            entity.Property(x => x.ChunkKind).HasColumnName("chunk_kind").HasConversion<string>();
            entity.Property(x => x.ChunkIndex).HasColumnName("chunk_index");
            entity.Property(x => x.ChunkText).HasColumnName("chunk_text");
            entity.Property(x => x.MetadataJson).HasColumnName("metadata_json");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasMany(x => x.Vectors).WithOne(x => x.Chunk).HasForeignKey(x => x.ChunkId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemoryChunkVector>(entity =>
        {
            entity.ToTable("memory_chunk_vectors");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ChunkId).HasColumnName("chunk_id");
            entity.Property(x => x.ModelKey).HasColumnName("model_key");
            entity.Property(x => x.Dimension).HasColumnName("dimension");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<MemoryLink>(entity =>
        {
            entity.ToTable("memory_links");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.FromId).HasColumnName("from_id");
            entity.Property(x => x.ToId).HasColumnName("to_id");
            entity.Property(x => x.LinkType).HasColumnName("link_type");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasOne(x => x.From).WithMany(x => x.OutgoingLinks).HasForeignKey(x => x.FromId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.To).WithMany(x => x.IncomingLinks).HasForeignKey(x => x.ToId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MemoryJob>(entity =>
        {
            entity.ToTable("memory_jobs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.JobType).HasColumnName("job_type").HasConversion<string>();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json");
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
        });

        modelBuilder.Entity<RuntimeLogEntry>(entity =>
        {
            entity.ToTable("runtime_log_entries");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.ServiceName).HasColumnName("service_name");
            entity.Property(x => x.Category).HasColumnName("category");
            entity.Property(x => x.Level).HasColumnName("level");
            entity.Property(x => x.Message).HasColumnName("message");
            entity.Property(x => x.Exception).HasColumnName("exception");
            entity.Property(x => x.TraceId).HasColumnName("trace_id");
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<RetrievalEvent>(entity =>
        {
            entity.ToTable("retrieval_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Channel).HasColumnName("channel");
            entity.Property(x => x.EntryPoint).HasColumnName("entry_point");
            entity.Property(x => x.Purpose).HasColumnName("purpose");
            entity.Property(x => x.QueryText).HasColumnName("query_text");
            entity.Property(x => x.QueryHash).HasColumnName("query_hash");
            entity.Property(x => x.QueryMode).HasColumnName("query_mode");
            entity.Property(x => x.IncludedProjectIds).HasColumnName("included_project_ids").HasColumnType("text[]");
            entity.Property(x => x.UseSummaryLayer).HasColumnName("use_summary_layer");
            entity.Property(x => x.Limit).HasColumnName("result_limit");
            entity.Property(x => x.CacheHit).HasColumnName("cache_hit");
            entity.Property(x => x.ResultCount).HasColumnName("result_count");
            entity.Property(x => x.DurationMs).HasColumnName("duration_ms");
            entity.Property(x => x.Success).HasColumnName("success");
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.TraceId).HasColumnName("trace_id");
            entity.Property(x => x.RequestId).HasColumnName("request_id");
            entity.Property(x => x.MetadataJson)
                .HasColumnName("metadata_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasMany(x => x.Hits).WithOne(x => x.RetrievalEvent).HasForeignKey(x => x.RetrievalEventId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RetrievalHit>(entity =>
        {
            entity.ToTable("retrieval_hits");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.RetrievalEventId).HasColumnName("retrieval_event_id");
            entity.Property(x => x.Rank).HasColumnName("rank");
            entity.Property(x => x.MemoryId).HasColumnName("memory_id");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.MemoryType).HasColumnName("memory_type");
            entity.Property(x => x.SourceType).HasColumnName("source_type");
            entity.Property(x => x.SourceRef).HasColumnName("source_ref");
            entity.Property(x => x.Score).HasColumnName("score");
            entity.Property(x => x.Excerpt).HasColumnName("excerpt");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
        });

        modelBuilder.Entity<LogIngestionCheckpoint>(entity =>
        {
            entity.ToTable("log_ingestion_checkpoints");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ServiceName).HasColumnName("service_name");
            entity.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
        });

        modelBuilder.Entity<SourceConnection>(entity =>
        {
            entity.ToTable("source_connections");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.SourceKind).HasColumnName("source_kind").HasConversion<string>();
            entity.Property(x => x.Enabled).HasColumnName("enabled");
            entity.Property(x => x.ConfigJson)
                .HasColumnName("config_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.SecretJsonProtected).HasColumnName("secret_json_protected");
            entity.Property(x => x.LastCursor).HasColumnName("last_cursor");
            entity.Property(x => x.LastSuccessfulSyncAt).HasColumnName("last_successful_sync_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.ProjectId, x.Name }).IsUnique();
            entity.HasMany(x => x.SyncRuns).WithOne(x => x.SourceConnection).HasForeignKey(x => x.SourceConnectionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SourceSyncRun>(entity =>
        {
            entity.ToTable("source_sync_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Trigger).HasColumnName("trigger").HasConversion<string>();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.ScannedCount).HasColumnName("scanned_count");
            entity.Property(x => x.UpsertedCount).HasColumnName("upserted_count");
            entity.Property(x => x.ArchivedCount).HasColumnName("archived_count");
            entity.Property(x => x.ErrorCount).HasColumnName("error_count");
            entity.Property(x => x.CursorBefore).HasColumnName("cursor_before");
            entity.Property(x => x.CursorAfter).HasColumnName("cursor_after");
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
        });

        modelBuilder.Entity<GovernanceFinding>(entity =>
        {
            entity.ToTable("governance_findings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.SourceConnectionId).HasColumnName("source_connection_id");
            entity.Property(x => x.PrimaryMemoryId).HasColumnName("primary_memory_id");
            entity.Property(x => x.SecondaryMemoryId).HasColumnName("secondary_memory_id");
            entity.Property(x => x.Type).HasColumnName("type").HasConversion<string>();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.DetailsJson)
                .HasColumnName("details_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.DedupKey).HasColumnName("dedup_key");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.DedupKey).IsUnique();
        });

        modelBuilder.Entity<EvaluationSuite>(entity =>
        {
            entity.ToTable("evaluation_suites");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasMany(x => x.Cases).WithOne(x => x.Suite).HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Runs).WithOne(x => x.Suite).HasForeignKey(x => x.SuiteId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvaluationCase>(entity =>
        {
            entity.ToTable("evaluation_cases");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SuiteId).HasColumnName("suite_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.ScenarioLabel).HasColumnName("scenario_label");
            entity.Property(x => x.Query).HasColumnName("query");
            entity.Property(x => x.ExpectedMemoryIds).HasColumnName("expected_memory_ids").HasColumnType("text[]");
            entity.Property(x => x.ExpectedExternalKeys).HasColumnName("expected_external_keys").HasColumnType("text[]");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasMany(x => x.RunItems).WithOne(x => x.Case).HasForeignKey(x => x.CaseId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvaluationRun>(entity =>
        {
            entity.ToTable("evaluation_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SuiteId).HasColumnName("suite_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.EmbeddingProfile).HasColumnName("embedding_profile");
            entity.Property(x => x.QueryMode).HasColumnName("query_mode");
            entity.Property(x => x.UseSummaryLayer).HasColumnName("use_summary_layer");
            entity.Property(x => x.TopK).HasColumnName("top_k");
            entity.Property(x => x.HitRate).HasColumnName("hit_rate");
            entity.Property(x => x.RecallAtK).HasColumnName("recall_at_k");
            entity.Property(x => x.MeanReciprocalRank).HasColumnName("mean_reciprocal_rank");
            entity.Property(x => x.AverageLatencyMs).HasColumnName("average_latency_ms");
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.HasMany(x => x.Items).WithOne(x => x.Run).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<EvaluationRunItem>(entity =>
        {
            entity.ToTable("evaluation_run_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.RunId).HasColumnName("run_id");
            entity.Property(x => x.CaseId).HasColumnName("case_id");
            entity.Property(x => x.Query).HasColumnName("query");
            entity.Property(x => x.ScenarioLabel).HasColumnName("scenario_label");
            entity.Property(x => x.ExpectedMemoryIds).HasColumnName("expected_memory_ids").HasColumnType("text[]");
            entity.Property(x => x.ExpectedExternalKeys).HasColumnName("expected_external_keys").HasColumnType("text[]");
            entity.Property(x => x.HitMemoryIds).HasColumnName("hit_memory_ids").HasColumnType("text[]");
            entity.Property(x => x.HitExternalKeys).HasColumnName("hit_external_keys").HasColumnType("text[]");
            entity.Property(x => x.HitAtK).HasColumnName("hit_at_k");
            entity.Property(x => x.RecallAtK).HasColumnName("recall_at_k");
            entity.Property(x => x.ReciprocalRank).HasColumnName("reciprocal_rank");
            entity.Property(x => x.LatencyMs).HasColumnName("latency_ms");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
        });

        modelBuilder.Entity<SuggestedAction>(entity =>
        {
            entity.ToTable("suggested_actions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Type).HasColumnName("type").HasConversion<string>();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.PayloadJson)
                .HasColumnName("payload_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.ExecutedAt).HasColumnName("executed_at");
        });

        modelBuilder.Entity<ConversationSession>(entity =>
        {
            entity.ToTable("conversation_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ConversationId).HasColumnName("conversation_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.ProjectName).HasColumnName("project_name");
            entity.Property(x => x.TaskId).HasColumnName("task_id");
            entity.Property(x => x.SourceSystem).HasColumnName("source_system");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.LastTurnId).HasColumnName("last_turn_id");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.LastCheckpointAt).HasColumnName("last_checkpoint_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.SourceSystem, x.ConversationId }).IsUnique();
            entity.HasMany(x => x.Checkpoints).WithOne(x => x.Session).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Insights).WithOne(x => x.Session).HasForeignKey(x => x.SessionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationCheckpoint>(entity =>
        {
            entity.ToTable("conversation_checkpoints");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SessionId).HasColumnName("session_id");
            entity.Property(x => x.ConversationId).HasColumnName("conversation_id");
            entity.Property(x => x.TurnId).HasColumnName("turn_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.ProjectName).HasColumnName("project_name");
            entity.Property(x => x.TaskId).HasColumnName("task_id");
            entity.Property(x => x.SourceSystem).HasColumnName("source_system");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasConversion<string>();
            entity.Property(x => x.SourceKind).HasColumnName("source_kind").HasConversion<string>();
            entity.Property(x => x.SourceRef).HasColumnName("source_ref");
            entity.Property(x => x.UserMessageSummary).HasColumnName("user_message_summary");
            entity.Property(x => x.AgentMessageSummary).HasColumnName("agent_message_summary");
            entity.Property(x => x.ToolCallsJson)
                .HasColumnName("tool_calls_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.SessionSummary).HasColumnName("session_summary");
            entity.Property(x => x.ShortExcerpt).HasColumnName("short_excerpt");
            entity.Property(x => x.DedupKey).HasColumnName("dedup_key");
            entity.Property(x => x.MetadataJson)
                .HasColumnName("metadata_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => x.DedupKey).IsUnique();
            entity.HasMany(x => x.Insights).WithOne(x => x.Checkpoint).HasForeignKey(x => x.CheckpointId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ConversationInsight>(entity =>
        {
            entity.ToTable("conversation_insights");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SessionId).HasColumnName("session_id");
            entity.Property(x => x.CheckpointId).HasColumnName("checkpoint_id");
            entity.Property(x => x.ConversationId).HasColumnName("conversation_id");
            entity.Property(x => x.TurnId).HasColumnName("turn_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.ProjectName).HasColumnName("project_name");
            entity.Property(x => x.TaskId).HasColumnName("task_id");
            entity.Property(x => x.SourceSystem).HasColumnName("source_system");
            entity.Property(x => x.SourceKind).HasColumnName("source_kind").HasConversion<string>();
            entity.Property(x => x.InsightType).HasColumnName("insight_type").HasConversion<string>();
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Content).HasColumnName("content");
            entity.Property(x => x.Summary).HasColumnName("summary");
            entity.Property(x => x.SourceRef).HasColumnName("source_ref");
            entity.Property(x => x.Tags).HasColumnName("tags").HasColumnType("text[]");
            entity.Property(x => x.Importance).HasColumnName("importance");
            entity.Property(x => x.Confidence).HasColumnName("confidence");
            entity.Property(x => x.DedupKey).HasColumnName("dedup_key");
            entity.Property(x => x.PromotionStatus).HasColumnName("promotion_status").HasConversion<string>();
            entity.Property(x => x.PromotedMemoryId).HasColumnName("promoted_memory_id");
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.MetadataJson)
                .HasColumnName("metadata_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.DedupKey).IsUnique();
        });
    }

    private static string NormalizeJson(string? value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value;
}

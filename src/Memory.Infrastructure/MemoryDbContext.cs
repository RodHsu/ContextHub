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
    public DbSet<AgentConnectivityObservation> AgentConnectivityObservations => Set<AgentConnectivityObservation>();
    public DbSet<AgentConnectivitySummary> AgentConnectivitySummaries => Set<AgentConnectivitySummary>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantUser> TenantUsers => Set<TenantUser>();
    public DbSet<TenantProjectGrant> TenantProjectGrants => Set<TenantProjectGrant>();
    public DbSet<ApiToken> ApiTokens => Set<ApiToken>();
    public DbSet<SecurityAuditEvent> SecurityAuditEvents => Set<SecurityAuditEvent>();
    public DbSet<MemoryItem> MemoryItems => Set<MemoryItem>();
    public DbSet<MemoryItemRevision> MemoryItemRevisions => Set<MemoryItemRevision>();
    public DbSet<MemoryItemChunk> MemoryItemChunks => Set<MemoryItemChunk>();
    public DbSet<MemoryChunkVector> MemoryChunkVectors => Set<MemoryChunkVector>();
    public DbSet<MemoryLink> MemoryLinks => Set<MemoryLink>();
    public DbSet<MemoryJob> MemoryJobs => Set<MemoryJob>();
    public DbSet<MaintenanceRun> MaintenanceRuns => Set<MaintenanceRun>();
    public DbSet<RuntimeLogEntry> RuntimeLogEntries => Set<RuntimeLogEntry>();
    public DbSet<RetrievalEvent> RetrievalEvents => Set<RetrievalEvent>();
    public DbSet<RetrievalHit> RetrievalHits => Set<RetrievalHit>();
    public DbSet<RetrievalTelemetryDailySummary> RetrievalTelemetryDailySummaries => Set<RetrievalTelemetryDailySummary>();
    public DbSet<RetrievalTelemetryDailyHitSummary> RetrievalTelemetryDailyHitSummaries => Set<RetrievalTelemetryDailyHitSummary>();
    public DbSet<EmbeddingUsageHourly> EmbeddingUsageHourly => Set<EmbeddingUsageHourly>();
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
    public DbSet<KnowledgeGovernanceSnapshot> KnowledgeGovernanceSnapshots => Set<KnowledgeGovernanceSnapshot>();
    public DbSet<GovernanceBatchRun> GovernanceBatchRuns => Set<GovernanceBatchRun>();
    public DbSet<GovernanceBatchExecution> GovernanceBatchExecutions => Set<GovernanceBatchExecution>();
    public DbSet<ProjectHierarchy> ProjectHierarchies => Set<ProjectHierarchy>();
    public DbSet<DiscussionThread> DiscussionThreads => Set<DiscussionThread>();
    public DbSet<DiscussionParticipant> DiscussionParticipants => Set<DiscussionParticipant>();
    public DbSet<DiscussionMessage> DiscussionMessages => Set<DiscussionMessage>();
    public DbSet<ProjectWorkItem> ProjectWorkItems => Set<ProjectWorkItem>();
    public DbSet<ProjectWorkItemChecklistItem> ProjectWorkItemChecklistItems => Set<ProjectWorkItemChecklistItem>();

    public void ClearTrackedChanges()
        => ChangeTracker.Clear();

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

        modelBuilder.Entity<AgentConnectivityObservation>(entity =>
        {
            entity.ToTable("agent_connectivity_observations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.AgentId).HasColumnName("agent_id");
            entity.Property(x => x.AgentName).HasColumnName("agent_name");
            entity.Property(x => x.AgentVersion).HasColumnName("agent_version");
            entity.Property(x => x.BridgeVersion).HasColumnName("bridge_version");
            entity.Property(x => x.EndpointHost).HasColumnName("endpoint_host");
            entity.Property(x => x.Transport).HasColumnName("transport");
            entity.Property(x => x.McpMethod).HasColumnName("mcp_method");
            entity.Property(x => x.ToolName).HasColumnName("tool_name");
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.Success).HasColumnName("success");
            entity.Property(x => x.StatusCode).HasColumnName("status_code");
            entity.Property(x => x.ErrorKind).HasColumnName("error_kind");
            entity.Property(x => x.ClientElapsedMs).HasColumnName("client_elapsed_ms");
            entity.Property(x => x.ServerElapsedMs).HasColumnName("server_elapsed_ms");
            entity.Property(x => x.NetworkOverheadMs).HasColumnName("network_overhead_ms");
            entity.Property(x => x.SessionWasInitialized).HasColumnName("session_was_initialized");
            entity.Property(x => x.ReconnectAttempted).HasColumnName("reconnect_attempted");
            entity.Property(x => x.CorrelationId).HasColumnName("correlation_id");
            entity.Property(x => x.Source).HasColumnName("source");
            entity.Property(x => x.ObservedAtUtc).HasColumnName("observed_at_utc");
            entity.Property(x => x.CreatedAtUtc).HasColumnName("created_at_utc");
            entity.HasIndex(x => new { x.ProjectId, x.ObservedAtUtc });
            entity.HasIndex(x => new { x.AgentId, x.ObservedAtUtc });
            entity.HasIndex(x => new { x.Success, x.ObservedAtUtc });
        });

        modelBuilder.Entity<AgentConnectivitySummary>(entity =>
        {
            entity.ToTable("agent_connectivity_summaries");
            entity.HasKey(x => new { x.BucketStartUtc, x.BucketMinutes, x.ProjectId, x.AgentId, x.EndpointHost, x.Transport, x.McpMethod, x.ToolName });
            entity.Property(x => x.BucketStartUtc).HasColumnName("bucket_start_utc");
            entity.Property(x => x.BucketMinutes).HasColumnName("bucket_minutes");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.AgentId).HasColumnName("agent_id");
            entity.Property(x => x.EndpointHost).HasColumnName("endpoint_host");
            entity.Property(x => x.Transport).HasColumnName("transport");
            entity.Property(x => x.McpMethod).HasColumnName("mcp_method");
            entity.Property(x => x.ToolName).HasColumnName("tool_name");
            entity.Property(x => x.SampleCount).HasColumnName("sample_count");
            entity.Property(x => x.SuccessCount).HasColumnName("success_count");
            entity.Property(x => x.FailureCount).HasColumnName("failure_count");
            entity.Property(x => x.TimeoutCount).HasColumnName("timeout_count");
            entity.Property(x => x.AuthFailureCount).HasColumnName("auth_failure_count");
            entity.Property(x => x.ReconnectCount).HasColumnName("reconnect_count");
            entity.Property(x => x.AvgClientElapsedMs).HasColumnName("avg_client_elapsed_ms");
            entity.Property(x => x.P95ClientElapsedMs).HasColumnName("p95_client_elapsed_ms");
            entity.Property(x => x.MaxClientElapsedMs).HasColumnName("max_client_elapsed_ms");
            entity.Property(x => x.LastObservedAtUtc).HasColumnName("last_observed_at_utc");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.UpdatedAtUtc).HasColumnName("updated_at_utc");
            entity.HasIndex(x => new { x.ProjectId, x.BucketStartUtc });
            entity.HasIndex(x => new { x.AgentId, x.BucketStartUtc });
        });

        modelBuilder.Entity<Tenant>(entity =>
        {
            entity.ToTable("tenants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Slug).HasColumnName("slug");
            entity.Property(x => x.DisplayName).HasColumnName("display_name");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.Slug).IsUnique();
            entity.HasMany(x => x.Users).WithOne(x => x.Tenant).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.ProjectGrants).WithOne(x => x.Tenant).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.ApiTokens).WithOne(x => x.Tenant).HasForeignKey(x => x.TenantId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TenantUser>(entity =>
        {
            entity.ToTable("tenant_users");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.Username).HasColumnName("username");
            entity.Property(x => x.DisplayName).HasColumnName("display_name");
            entity.Property(x => x.Email).HasColumnName("email");
            entity.Property(x => x.PasswordHash).HasColumnName("password_hash");
            entity.Property(x => x.Role).HasColumnName("role").HasConversion<string>();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.LastLoginAt).HasColumnName("last_login_at");
            entity.Property(x => x.PasswordUpdatedAt).HasColumnName("password_updated_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.TenantId, x.Username }).IsUnique();
            entity.HasMany(x => x.ApiTokens).WithOne(x => x.OwnerUser).HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TenantProjectGrant>(entity =>
        {
            entity.ToTable("tenant_project_grants");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.CanRead).HasColumnName("can_read");
            entity.Property(x => x.CanWrite).HasColumnName("can_write");
            entity.Property(x => x.CanManageTokens).HasColumnName("can_manage_tokens");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.TenantId, x.ProjectId }).IsUnique();
        });

        modelBuilder.Entity<ApiToken>(entity =>
        {
            entity.ToTable("api_tokens");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Notes).HasColumnName("notes");
            entity.Property(x => x.TokenPrefix).HasColumnName("token_prefix");
            entity.Property(x => x.TokenHash).HasColumnName("token_hash");
            entity.Property(x => x.TokenLastFour).HasColumnName("token_last_four");
            entity.Property(x => x.Scopes).HasColumnName("scopes").HasColumnType("text[]");
            entity.Property(x => x.AllowedProjectIds).HasColumnName("allowed_project_ids").HasColumnType("text[]");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.Property(x => x.LastUsedAt).HasColumnName("last_used_at");
            entity.Property(x => x.LastUsedIp).HasColumnName("last_used_ip");
            entity.Property(x => x.LastUsedUserAgent).HasColumnName("last_used_user_agent");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Name });
            entity.HasIndex(x => x.LastUsedAt);
        });

        modelBuilder.Entity<SecurityAuditEvent>(entity =>
        {
            entity.ToTable("security_audit_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(x => x.ApiTokenId).HasColumnName("api_token_id");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasConversion<string>();
            entity.Property(x => x.Outcome).HasColumnName("outcome");
            entity.Property(x => x.IpAddress).HasColumnName("ip_address");
            entity.Property(x => x.UserAgent).HasColumnName("user_agent");
            entity.Property(x => x.DetailsJson)
                .HasColumnName("details_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => new { x.TenantId, x.CreatedAt });
            entity.HasIndex(x => new { x.ApiTokenId, x.CreatedAt });
        });

        modelBuilder.Entity<MemoryItem>(entity =>
        {
            entity.ToTable("memory_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
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
            entity.HasIndex(x => new { x.ProjectId, x.OwnerUserId, x.ExternalKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.ProjectId, x.Status });
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
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.JobType).HasColumnName("job_type").HasConversion<string>();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json");
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
        });

        modelBuilder.Entity<MaintenanceRun>(entity =>
        {
            entity.ToTable("maintenance_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.MaintenanceType).HasColumnName("maintenance_type").HasConversion<string>();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.TriggeredBy).HasColumnName("triggered_by");
            entity.Property(x => x.PolicyJson)
                .HasColumnName("policy_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.ResultJson)
                .HasColumnName("result_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.Error).HasColumnName("error");
            entity.HasIndex(x => x.StartedAt);
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
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
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
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => new { x.CreatedAt, x.RetrievalEventId })
                .HasDatabaseName("ix_retrieval_hits_created_at_event_id");
        });

        modelBuilder.Entity<RetrievalTelemetryDailySummary>(entity =>
        {
            entity.ToTable("retrieval_telemetry_daily_summaries");
            entity.HasKey(x => new { x.SummaryDate, x.TenantId, x.OwnerUserId, x.ProjectId, x.Channel, x.EntryPoint, x.Purpose, x.QueryMode });
            entity.Property(x => x.SummaryDate).HasColumnName("summary_date");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Channel).HasColumnName("channel");
            entity.Property(x => x.EntryPoint).HasColumnName("entry_point");
            entity.Property(x => x.Purpose).HasColumnName("purpose");
            entity.Property(x => x.QueryMode).HasColumnName("query_mode");
            entity.Property(x => x.RequestCount).HasColumnName("request_count");
            entity.Property(x => x.SuccessCount).HasColumnName("success_count");
            entity.Property(x => x.ErrorCount).HasColumnName("error_count");
            entity.Property(x => x.ZeroResultCount).HasColumnName("zero_result_count");
            entity.Property(x => x.CacheHitCount).HasColumnName("cache_hit_count");
            entity.Property(x => x.ResultCountSum).HasColumnName("result_count_sum");
            entity.Property(x => x.DurationMsSum).HasColumnName("duration_ms_sum");
            entity.Property(x => x.DurationMsMax).HasColumnName("duration_ms_max");
            entity.Property(x => x.DurationMsP95).HasColumnName("duration_ms_p95");
            entity.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
            entity.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.SummaryDate);
            entity.HasIndex(x => new { x.ProjectId, x.EntryPoint, x.SummaryDate });
        });

        modelBuilder.Entity<RetrievalTelemetryDailyHitSummary>(entity =>
        {
            entity.ToTable("retrieval_telemetry_daily_hit_summaries");
            entity.HasKey(x => new { x.SummaryDate, x.TenantId, x.OwnerUserId, x.ProjectId, x.EntryPoint, x.MemoryId, x.SourceRef });
            entity.Property(x => x.SummaryDate).HasColumnName("summary_date");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.EntryPoint).HasColumnName("entry_point");
            entity.Property(x => x.MemoryId).HasColumnName("memory_id");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.MemoryType).HasColumnName("memory_type");
            entity.Property(x => x.SourceType).HasColumnName("source_type");
            entity.Property(x => x.SourceRef).HasColumnName("source_ref");
            entity.Property(x => x.HitCount).HasColumnName("hit_count");
            entity.Property(x => x.BestRank).HasColumnName("best_rank");
            entity.Property(x => x.BestScore).HasColumnName("best_score");
            entity.Property(x => x.AverageScore).HasColumnName("average_score");
            entity.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
            entity.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.SummaryDate);
            entity.HasIndex(x => new { x.ProjectId, x.EntryPoint, x.SummaryDate, x.HitCount });
        });

        modelBuilder.Entity<EmbeddingUsageHourly>(entity =>
        {
            entity.ToTable("embedding_usage_hourly");
            entity.HasKey(x => new { x.BucketStartUtc, x.ServiceName, x.Provider, x.Profile, x.Purpose, x.SourceKind, x.MaxTokens });
            entity.Property(x => x.BucketStartUtc).HasColumnName("bucket_start_utc");
            entity.Property(x => x.ServiceName).HasColumnName("service_name");
            entity.Property(x => x.Provider).HasColumnName("provider");
            entity.Property(x => x.Profile).HasColumnName("profile");
            entity.Property(x => x.Purpose).HasColumnName("purpose");
            entity.Property(x => x.SourceKind).HasColumnName("source_kind");
            entity.Property(x => x.MaxTokens).HasColumnName("max_tokens");
            entity.Property(x => x.TotalInputs).HasColumnName("total_inputs");
            entity.Property(x => x.TruncatedInputs).HasColumnName("truncated_inputs");
            entity.Property(x => x.TotalTokenCount).HasColumnName("total_token_count");
            entity.Property(x => x.TotalTruncatedTokens).HasColumnName("total_truncated_tokens");
            entity.Property(x => x.MaxTokenCount).HasColumnName("max_token_count");
            entity.Property(x => x.HistogramJson)
                .HasColumnName("histogram_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.FirstSeenAt).HasColumnName("first_seen_at");
            entity.Property(x => x.LastSeenAt).HasColumnName("last_seen_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.BucketStartUtc);
            entity.HasIndex(x => new { x.ServiceName, x.Profile, x.Purpose, x.SourceKind, x.BucketStartUtc });
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
            entity.Property(x => x.GovernanceReason).HasColumnName("governance_reason");
            entity.Property(x => x.GovernanceRunId).HasColumnName("governance_run_id");
            entity.Property(x => x.GovernanceActor).HasColumnName("governance_actor");
            entity.Property(x => x.GovernanceRetryCount).HasColumnName("governance_retry_count");
            entity.Property(x => x.GovernanceUpdatedAt).HasColumnName("governance_updated_at");
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
            entity.Property(x => x.DedupKey).HasColumnName("dedup_key");
            entity.Property(x => x.Error).HasColumnName("error");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.ExecutedAt).HasColumnName("executed_at");
            entity.HasIndex(x => new { x.ProjectId, x.Type, x.DedupKey });
        });

        modelBuilder.Entity<ConversationSession>(entity =>
        {
            entity.ToTable("conversation_sessions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
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
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
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
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
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
            entity.Property(x => x.GovernanceReason).HasColumnName("governance_reason");
            entity.Property(x => x.GovernanceRunId).HasColumnName("governance_run_id");
            entity.Property(x => x.GovernanceRetryCount).HasColumnName("governance_retry_count");
            entity.Property(x => x.GovernanceUpdatedAt).HasColumnName("governance_updated_at");
            entity.Property(x => x.MetadataJson)
                .HasColumnName("metadata_json")
                .HasColumnType("jsonb")
                .HasConversion(JsonDocumentConverter, JsonStringComparer);
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => x.DedupKey).IsUnique();
        });

        modelBuilder.Entity<KnowledgeGovernanceSnapshot>(entity =>
        {
            entity.ToTable("knowledge_governance_snapshots");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.GovernanceRunId).HasColumnName("governance_run_id");
            entity.Property(x => x.IsReReview).HasColumnName("is_re_review");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.ProjectSetHash).HasColumnName("project_set_hash");
            entity.Property(x => x.ProjectIdsJson).HasColumnName("project_ids_json").HasColumnType("jsonb");
            entity.Property(x => x.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
            entity.Property(x => x.TotalCount).HasColumnName("total_count");
            entity.Property(x => x.ScannedCount).HasColumnName("scanned_count");
            entity.Property(x => x.CoverageComplete).HasColumnName("coverage_complete");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.GovernanceRunId, x.IsReReview, x.Generation }).IsUnique();
        });

        modelBuilder.Entity<GovernanceBatchRun>(entity =>
        {
            entity.ToTable("governance_batch_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.GovernanceRunId).HasColumnName("governance_run_id");
            entity.Property(x => x.SnapshotToken).HasColumnName("snapshot_token");
            entity.Property(x => x.ProjectSetHash).HasColumnName("project_set_hash");
            entity.Property(x => x.ProjectIdsJson).HasColumnName("project_ids_json").HasColumnType("jsonb");
            entity.Property(x => x.PlanJson).HasColumnName("plan_json").HasColumnType("jsonb");
            entity.Property(x => x.LastCursor).HasColumnName("last_cursor");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.GovernanceRunId, x.SnapshotToken }).IsUnique();
            entity.HasMany(x => x.Executions).WithOne(x => x.Run).HasForeignKey(x => x.GovernanceBatchRunId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<GovernanceBatchExecution>(entity =>
        {
            entity.ToTable("governance_batch_executions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.GovernanceBatchRunId).HasColumnName("governance_batch_run_id");
            entity.Property(x => x.RequestHash).HasColumnName("request_hash");
            entity.Property(x => x.RequestJson).HasColumnName("request_json").HasColumnType("jsonb");
            entity.Property(x => x.CursorBefore).HasColumnName("cursor_before");
            entity.Property(x => x.CursorAfter).HasColumnName("cursor_after");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(x => new { x.GovernanceBatchRunId, x.RequestHash }).IsUnique();
        });

        modelBuilder.Entity<ProjectHierarchy>(entity =>
        {
            entity.ToTable("project_hierarchies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.ParentProjectId).HasColumnName("parent_project_id");
            entity.Property(x => x.ChildProjectId).HasColumnName("child_project_id");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.ParentProjectId, x.ChildProjectId }).IsUnique();
        });

        modelBuilder.Entity<DiscussionThread>(entity =>
        {
            entity.ToTable("discussion_threads");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.HostProjectId).HasColumnName("host_project_id");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Status).HasColumnName("status");
            entity.Property(x => x.ArchivedAt).HasColumnName("archived_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.HostProjectId, x.UpdatedAt });
            entity.HasMany(x => x.Participants).WithOne(x => x.Thread).HasForeignKey(x => x.ThreadId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Messages).WithOne(x => x.Thread).HasForeignKey(x => x.ThreadId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DiscussionParticipant>(entity =>
        {
            entity.ToTable("discussion_participants");
            entity.HasKey(x => new { x.ThreadId, x.ProjectId });
            entity.Property(x => x.ThreadId).HasColumnName("thread_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.LastReadAt).HasColumnName("last_read_at");
            entity.HasIndex(x => x.ProjectId);
        });

        modelBuilder.Entity<DiscussionMessage>(entity =>
        {
            entity.ToTable("discussion_messages");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ThreadId).HasColumnName("thread_id");
            entity.Property(x => x.SenderProjectId).HasColumnName("sender_project_id");
            entity.Property(x => x.Content).HasColumnName("content");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => new { x.ThreadId, x.CreatedAt });
        });

        modelBuilder.Entity<ProjectWorkItem>(entity =>
        {
            entity.ToTable("project_work_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Title).HasColumnName("title");
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.Tags).HasColumnName("tags").HasColumnType("text[]");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.Priority).HasColumnName("priority");
            entity.Property(x => x.DueAt).HasColumnName("due_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.Property(x => x.ArchivedAt).HasColumnName("archived_at");
            entity.Property(x => x.GovernanceExclusionsJson).HasColumnName("governance_exclusions_json").HasColumnType("jsonb");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.ProjectId, x.Status, x.DueAt });
        });

        modelBuilder.Entity<ProjectWorkItemChecklistItem>(entity =>
        {
            entity.ToTable("project_work_item_checklist_items");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.WorkItemId).HasColumnName("work_item_id");
            entity.Property(x => x.Content).HasColumnName("content");
            entity.Property(x => x.IsCompleted).HasColumnName("is_completed");
            entity.Property(x => x.SortOrder).HasColumnName("sort_order");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasOne(x => x.WorkItem).WithMany(x => x.ChecklistItems).HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.WorkItemId, x.SortOrder });
        });
    }

    private static string NormalizeJson(string? value)
        => string.IsNullOrWhiteSpace(value) ? "{}" : value;
}

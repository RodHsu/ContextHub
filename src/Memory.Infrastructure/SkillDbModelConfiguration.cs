using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

internal static class SkillDbModelConfiguration
{
    public static void ConfigureSkillModels(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Skill>(entity =>
        {
            entity.ToTable("skills");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.StableKey).HasColumnName("stable_key");
            entity.Property(x => x.Name).HasColumnName("name");
            entity.Property(x => x.Description).HasColumnName("description");
            entity.Property(x => x.WhenToUse).HasColumnName("when_to_use");
            entity.Property(x => x.Tags).HasColumnName("tags").HasColumnType("text[]");
            entity.Property(x => x.Aliases).HasColumnName("aliases").HasColumnType("text[]");
            entity.Property(x => x.License).HasColumnName("license");
            entity.Property(x => x.MaintainersJson).HasColumnName("maintainers_json").HasColumnType("jsonb");
            entity.Property(x => x.RiskLevel).HasColumnName("risk_level").HasConversion<string>();
            entity.Property(x => x.MetadataVersion).HasColumnName("metadata_version").IsConcurrencyToken();
            entity.Property(x => x.DefaultVersionId).HasColumnName("default_version_id");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.ArchivedAt).HasColumnName("archived_at");
            entity.HasIndex(x => new { x.TenantId, x.StableKey }).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.Name });
            entity.HasMany(x => x.Versions).WithOne(x => x.Skill).HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.Restrict);
            entity.HasMany(x => x.Bindings).WithOne(x => x.Skill).HasForeignKey(x => x.SkillId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SkillVersion>(entity =>
        {
            entity.ToTable("skill_versions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SkillId).HasColumnName("skill_id");
            entity.Property(x => x.Version).HasColumnName("version");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.ContentHash).HasColumnName("content_hash");
            entity.Property(x => x.BundleJson).HasColumnName("bundle_json").HasColumnType("jsonb");
            entity.Property(x => x.SearchText).HasColumnName("search_text");
            entity.Property(x => x.CompatibilityJson).HasColumnName("compatibility_json").HasColumnType("jsonb");
            entity.Property(x => x.RequiredCapabilities).HasColumnName("required_capabilities").HasColumnType("text[]");
            entity.Property(x => x.RequiredTools).HasColumnName("required_tools").HasColumnType("text[]");
            entity.Property(x => x.AllowedActions).HasColumnName("allowed_actions").HasColumnType("text[]");
            entity.Property(x => x.RequiresNetwork).HasColumnName("requires_network");
            entity.Property(x => x.RequiresSecrets).HasColumnName("requires_secrets");
            entity.Property(x => x.SourceKind).HasColumnName("source_kind").HasConversion<string>();
            entity.Property(x => x.SourceRef).HasColumnName("source_ref");
            entity.Property(x => x.SourceRevision).HasColumnName("source_revision");
            entity.Property(x => x.TrustLevel).HasColumnName("trust_level").HasConversion<string>();
            entity.Property(x => x.SignatureAlgorithm).HasColumnName("signature_algorithm");
            entity.Property(x => x.SignatureValue).HasColumnName("signature_value");
            entity.Property(x => x.SignatureVerified).HasColumnName("signature_verified");
            entity.Property(x => x.PublishEvidenceJson).HasColumnName("publish_evidence_json").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.PublishedAt).HasColumnName("published_at");
            entity.Property(x => x.DeprecatedAt).HasColumnName("deprecated_at");
            entity.Property(x => x.RevokedAt).HasColumnName("revoked_at");
            entity.Property(x => x.ArchivedAt).HasColumnName("archived_at");
            entity.HasIndex(x => new { x.SkillId, x.Version }).IsUnique();
            entity.HasIndex(x => new { x.SkillId, x.ContentHash }).IsUnique();
            entity.HasIndex(x => new { x.Status, x.UpdatedAt });
            entity.HasMany(x => x.Dependencies).WithOne(x => x.SkillVersion).HasForeignKey(x => x.SkillVersionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SkillVersionDependency>(entity =>
        {
            entity.ToTable("skill_version_dependencies");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SkillVersionId).HasColumnName("skill_version_id");
            entity.Property(x => x.TargetSkillId).HasColumnName("target_skill_id");
            entity.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>();
            entity.Property(x => x.VersionConstraint).HasColumnName("version_constraint");
            entity.HasIndex(x => new { x.SkillVersionId, x.TargetSkillId, x.Kind }).IsUnique();
        });

        modelBuilder.Entity<SkillBinding>(entity =>
        {
            entity.ToTable("skill_bindings");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SkillId).HasColumnName("skill_id");
            entity.Property(x => x.Scope).HasColumnName("scope").HasConversion<string>();
            entity.Property(x => x.ScopeValue).HasColumnName("scope_value");
            entity.Property(x => x.Mode).HasColumnName("mode").HasConversion<string>();
            entity.Property(x => x.VersionConstraint).HasColumnName("version_constraint");
            entity.Property(x => x.Revision).HasColumnName("revision").IsConcurrencyToken();
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.SkillId, x.Scope, x.ScopeValue }).IsUnique();
        });

        modelBuilder.Entity<SkillSearchGeneration>(entity =>
        {
            entity.ToTable("skill_search_generations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.SearchProfileVersion).HasColumnName("search_profile_version");
            entity.Property(x => x.EmbeddingModelId).HasColumnName("embedding_model_id");
            entity.Property(x => x.EmbeddingModelVersion).HasColumnName("embedding_model_version");
            entity.Property(x => x.Threshold).HasColumnName("threshold").HasPrecision(8, 6);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.BenchmarkJson).HasColumnName("benchmark_json").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.ActivatedAt).HasColumnName("activated_at");
            entity.HasIndex(x => new { x.TenantId, x.Status });
        });

        modelBuilder.Entity<SkillSearchDocument>(entity =>
        {
            entity.ToTable("skill_search_documents");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.GenerationId).HasColumnName("generation_id");
            entity.Property(x => x.SkillVersionId).HasColumnName("skill_version_id");
            entity.Property(x => x.SearchText).HasColumnName("search_text");
            entity.Property(x => x.TermsJson).HasColumnName("terms_json").HasColumnType("jsonb");
            entity.Property(x => x.EmbeddingJson).HasColumnName("embedding_json").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => new { x.GenerationId, x.SkillVersionId }).IsUnique();
        });

        modelBuilder.Entity<SkillResolution>(entity =>
        {
            entity.ToTable("skill_resolutions");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.ExecutionId).HasColumnName("execution_id");
            entity.Property(x => x.WorkItemId).HasColumnName("work_item_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.AgentType).HasColumnName("agent_type");
            entity.Property(x => x.Round).HasColumnName("round");
            entity.Property(x => x.MaxSearchRounds).HasColumnName("max_search_rounds");
            entity.Property(x => x.MaxSelectedSkills).HasColumnName("max_selected_skills");
            entity.Property(x => x.QueryHash).HasColumnName("query_hash");
            entity.Property(x => x.QueryTermsJson).HasColumnName("query_terms_json").HasColumnType("jsonb");
            entity.Property(x => x.SearchGenerationId).HasColumnName("search_generation_id");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.ExecutionId, x.Round }).IsUnique();
            entity.HasMany(x => x.Candidates).WithOne(x => x.Resolution).HasForeignKey(x => x.ResolutionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(x => x.Pins).WithOne(x => x.Resolution).HasForeignKey(x => x.ResolutionId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SkillResolutionCandidate>(entity =>
        {
            entity.ToTable("skill_resolution_candidates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ResolutionId).HasColumnName("resolution_id");
            entity.Property(x => x.SkillVersionId).HasColumnName("skill_version_id");
            entity.Property(x => x.Rank).HasColumnName("rank");
            entity.Property(x => x.Score).HasColumnName("score").HasPrecision(8, 6);
            entity.Property(x => x.Threshold).HasColumnName("threshold").HasPrecision(8, 6);
            entity.Property(x => x.MatchReasonsJson).HasColumnName("match_reasons_json").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => new { x.ResolutionId, x.SkillVersionId }).IsUnique();
        });

        modelBuilder.Entity<SkillResolutionPin>(entity =>
        {
            entity.ToTable("skill_resolution_pins");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ResolutionId).HasColumnName("resolution_id");
            entity.Property(x => x.SkillVersionId).HasColumnName("skill_version_id");
            entity.Property(x => x.ContentHash).HasColumnName("content_hash");
            entity.Property(x => x.IsDependency).HasColumnName("is_dependency");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.ReleasedAt).HasColumnName("released_at");
            entity.HasIndex(x => new { x.ResolutionId, x.SkillVersionId }).IsUnique();
        });

        modelBuilder.Entity<SkillTelemetryEvent>(entity =>
        {
            entity.ToTable("skill_telemetry_events");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.SkillId).HasColumnName("skill_id");
            entity.Property(x => x.SkillVersionId).HasColumnName("skill_version_id");
            entity.Property(x => x.ExecutionId).HasColumnName("execution_id");
            entity.Property(x => x.WorkItemId).HasColumnName("work_item_id");
            entity.Property(x => x.ResolutionId).HasColumnName("resolution_id");
            entity.Property(x => x.ResolutionRound).HasColumnName("resolution_round");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasConversion<string>();
            entity.Property(x => x.RejectionStage).HasColumnName("rejection_stage").HasConversion<string>();
            entity.Property(x => x.ReasonClass).HasColumnName("reason_class").HasConversion<string>();
            entity.Property(x => x.ReasonText).HasColumnName("reason_text");
            entity.Property(x => x.EvidenceJson).HasColumnName("evidence_json").HasColumnType("jsonb");
            entity.Property(x => x.QueryHash).HasColumnName("query_hash");
            entity.Property(x => x.CandidateRank).HasColumnName("candidate_rank");
            entity.Property(x => x.CandidateScore).HasColumnName("candidate_score").HasPrecision(8, 6);
            entity.Property(x => x.Threshold).HasColumnName("threshold").HasPrecision(8, 6);
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.AgentType).HasColumnName("agent_type");
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.SkillVersionId, x.EventType, x.OccurredAt });
            entity.HasIndex(x => new { x.ProjectId, x.RepositoryId, x.AgentType, x.OccurredAt });
        });

        modelBuilder.Entity<SkillMaterialization>(entity =>
        {
            entity.ToTable("skill_materializations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.ExecutionId).HasColumnName("execution_id");
            entity.Property(x => x.ResolutionId).HasColumnName("resolution_id");
            entity.Property(x => x.SkillVersionId).HasColumnName("skill_version_id");
            entity.Property(x => x.ContentHash).HasColumnName("content_hash");
            entity.Property(x => x.RelativePath).HasColumnName("relative_path");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.FailureReason).HasColumnName("failure_reason");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.CleanedAt).HasColumnName("cleaned_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.ExecutionId, x.SkillVersionId, x.ContentHash }).IsUnique();
        });

        modelBuilder.Entity<SkillMetadataProposal>(entity =>
        {
            entity.ToTable("skill_metadata_proposals");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.SkillId).HasColumnName("skill_id");
            entity.Property(x => x.ExpectedMetadataVersion).HasColumnName("expected_metadata_version");
            entity.Property(x => x.ExpectedMetadataHash).HasColumnName("expected_metadata_hash");
            entity.Property(x => x.ProposedPatchJson).HasColumnName("proposed_patch_json").HasColumnType("jsonb");
            entity.Property(x => x.EvidenceJson).HasColumnName("evidence_json").HasColumnType("jsonb");
            entity.Property(x => x.Confidence).HasColumnName("confidence").HasPrecision(8, 6);
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.GovernanceRunId).HasColumnName("governance_run_id");
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.SkillId, x.Status, x.UpdatedAt });
        });

        modelBuilder.Entity<SkillSourceObservation>(entity =>
        {
            entity.ToTable("skill_source_observations");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.SkillId).HasColumnName("skill_id");
            entity.Property(x => x.SourceRef).HasColumnName("source_ref");
            entity.Property(x => x.ObservedRevision).HasColumnName("observed_revision");
            entity.Property(x => x.ObservedContentHash).HasColumnName("observed_content_hash");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.SourceAvailable).HasColumnName("source_available");
            entity.Property(x => x.SignatureVerified).HasColumnName("signature_verified");
            entity.Property(x => x.EvidenceJson).HasColumnName("evidence_json").HasColumnType("jsonb");
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(x => x.ObservedAt).HasColumnName("observed_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.IdempotencyKey }).IsUnique();
            entity.HasIndex(x => new { x.SkillId, x.ObservedAt });
        });

        modelBuilder.Entity<SkillTelemetryDailyAggregate>(entity =>
        {
            entity.ToTable("skill_telemetry_daily_aggregates");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.AggregateDate).HasColumnName("aggregate_date");
            entity.Property(x => x.SkillId).HasColumnName("skill_id");
            entity.Property(x => x.SkillVersionId).HasColumnName("skill_version_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.RepositoryId).HasColumnName("repository_id");
            entity.Property(x => x.AgentType).HasColumnName("agent_type");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasConversion<string>();
            entity.Property(x => x.RejectionStage).HasColumnName("rejection_stage").HasConversion<string>();
            entity.Property(x => x.ReasonClass).HasColumnName("reason_class").HasConversion<string>();
            entity.Property(x => x.EventCount).HasColumnName("event_count");
            entity.Property(x => x.LastOccurredAt).HasColumnName("last_occurred_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.SkillId, x.SkillVersionId, x.AggregateDate });
            entity.HasIndex(x => new { x.ProjectId, x.RepositoryId, x.AgentType, x.AggregateDate });
        });

        modelBuilder.Entity<SkillTelemetryAggregationLedger>(entity =>
        {
            entity.ToTable("skill_telemetry_aggregation_ledger");
            entity.HasKey(x => x.EventId);
            entity.Property(x => x.EventId).HasColumnName("event_id");
            entity.Property(x => x.AggregatedAt).HasColumnName("aggregated_at");
        });

        modelBuilder.Entity<SkillTelemetryReconciliationRun>(entity =>
        {
            entity.ToTable("skill_telemetry_reconciliation_runs");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
            entity.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key");
            entity.Property(x => x.AggregatedEventCount).HasColumnName("aggregated_event_count");
            entity.Property(x => x.AggregateRowCount).HasColumnName("aggregate_row_count");
            entity.Property(x => x.DeletedRawEventCount).HasColumnName("deleted_raw_event_count");
            entity.Property(x => x.DeletedAggregateRowCount).HasColumnName("deleted_aggregate_row_count");
            entity.Property(x => x.ProtectedRawEventCount).HasColumnName("protected_raw_event_count");
            entity.Property(x => x.RawRetentionDays).HasColumnName("raw_retention_days");
            entity.Property(x => x.AggregateRetentionDays).HasColumnName("aggregate_retention_days");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasIndex(x => new { x.TenantId, x.OwnerUserId, x.IdempotencyKey }).IsUnique();
        });
    }
}

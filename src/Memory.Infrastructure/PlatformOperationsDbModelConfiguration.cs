using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

internal static class PlatformOperationsDbModelConfiguration
{
    public static void ConfigurePlatformOperations(this ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AgentExecutionResolutionSnapshot>(entity =>
        {
            entity.ToTable("agent_execution_resolution_snapshots", "authority");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ExecutionId).HasColumnName("execution_id");
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.ResolutionSequence).HasColumnName("resolution_sequence");
            entity.Property(x => x.RetryMode).HasColumnName("retry_mode").HasConversion<string>();
            entity.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<string>();
            entity.Property(x => x.AuthorityContextHash).HasColumnName("authority_context_hash");
            entity.Property(x => x.SnapshotHash).HasColumnName("snapshot_hash");
            entity.Property(x => x.EvidenceRefsJson).HasColumnName("evidence_refs_json").HasColumnType("jsonb");
            entity.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
            entity.HasOne(x => x.Execution).WithMany(x => x.ResolutionSnapshots).HasForeignKey(x => x.ExecutionId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ExecutionId, x.Attempt, x.ResolutionSequence }).IsUnique();
        });
        modelBuilder.Entity<AgentExecutionResolutionItem>(entity =>
        {
            entity.ToTable("agent_execution_resolution_items", "authority");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.SnapshotId).HasColumnName("snapshot_id");
            entity.Property(x => x.RequirementId).HasColumnName("requirement_id");
            entity.Property(x => x.Kind).HasColumnName("kind").HasConversion<string>();
            entity.Property(x => x.Outcome).HasColumnName("outcome").HasConversion<string>();
            entity.Property(x => x.LogicalResourceId).HasColumnName("logical_resource_id");
            entity.Property(x => x.ResolvedVersionId).HasColumnName("resolved_version_id");
            entity.Property(x => x.IntegrityIdentity).HasColumnName("integrity_identity");
            entity.Property(x => x.AuthorityRevision).HasColumnName("authority_revision");
            entity.Property(x => x.PolicyRevision).HasColumnName("policy_revision");
            entity.Property(x => x.CapabilityLeaseId).HasColumnName("capability_lease_id");
            entity.Property(x => x.CapabilityExpiresAt).HasColumnName("capability_expires_at");
            entity.Property(x => x.ReasonCode).HasColumnName("reason_code");
            entity.Property(x => x.EvidenceRefsJson).HasColumnName("evidence_refs_json").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasOne(x => x.Snapshot).WithMany(x => x.Items).HasForeignKey(x => x.SnapshotId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.SnapshotId, x.RequirementId }).IsUnique();
        });
        modelBuilder.Entity<AgentExecutionResourceApproval>(entity =>
        {
            entity.ToTable("agent_execution_resource_approvals", "authority");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.ExecutionId).HasColumnName("execution_id");
            entity.Property(x => x.RequirementId).HasColumnName("requirement_id");
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.ApprovedByUserId).HasColumnName("approved_by_user_id");
            entity.Property(x => x.AssertionId).HasColumnName("assertion_id");
            entity.Property(x => x.AuthorityRevision).HasColumnName("authority_revision");
            entity.Property(x => x.PolicyRevision).HasColumnName("policy_revision");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.ExpiresAt).HasColumnName("expires_at");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.ExecutionId, x.RequirementId, x.Attempt, x.Status });
        });
        modelBuilder.Entity<AuthorityOutboxEvent>(entity =>
        {
            entity.ToTable("authority_outbox_events", "audit");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.Sequence).HasColumnName("sequence").ValueGeneratedOnAdd();
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Category).HasColumnName("category");
            entity.Property(x => x.AggregateType).HasColumnName("aggregate_type");
            entity.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            entity.Property(x => x.EventType).HasColumnName("event_type");
            entity.Property(x => x.AuthorityRevision).HasColumnName("authority_revision");
            entity.Property(x => x.SecurityCritical).HasColumnName("security_critical");
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            entity.HasIndex(x => x.Sequence).IsUnique();
            entity.HasIndex(x => new { x.TenantId, x.ProjectId, x.Sequence });
        });
        modelBuilder.Entity<PlatformOutboxDelivery>(entity =>
        {
            entity.ToTable("outbox_deliveries", "monitoring");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.OutboxEventId).HasColumnName("outbox_event_id");
            entity.Property(x => x.Consumer).HasColumnName("consumer");
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.LastErrorCode).HasColumnName("last_error_code");
            entity.Property(x => x.EligibleAt).HasColumnName("eligible_at");
            entity.Property(x => x.DeliveredAt).HasColumnName("delivered_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.HasIndex(x => new { x.OutboxEventId, x.Consumer }).IsUnique();
        });
        modelBuilder.Entity<MonitoringActivityProjection>(entity =>
        {
            entity.ToTable("activity_projections", "monitoring");
            entity.HasKey(x => x.OutboxEventId);
            entity.Property(x => x.OutboxEventId).HasColumnName("outbox_event_id");
            entity.Property(x => x.AuthoritySequence).HasColumnName("authority_sequence");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Category).HasColumnName("category");
            entity.Property(x => x.AggregateType).HasColumnName("aggregate_type");
            entity.Property(x => x.AggregateId).HasColumnName("aggregate_id");
            entity.Property(x => x.EventType).HasColumnName("event_type");
            entity.Property(x => x.AuthorityRevision).HasColumnName("authority_revision");
            entity.Property(x => x.SecurityCritical).HasColumnName("security_critical");
            entity.Property(x => x.RedactedPayloadJson).HasColumnName("redacted_payload_json").HasColumnType("jsonb");
            entity.Property(x => x.OccurredAt).HasColumnName("occurred_at");
            entity.Property(x => x.ProjectedAt).HasColumnName("projected_at");
            entity.HasIndex(x => new { x.TenantId, x.ProjectId, x.AuthoritySequence });
        });
        modelBuilder.Entity<MonitoringProjectionState>(entity =>
        {
            entity.ToTable("projection_states", "monitoring");
            entity.HasKey(x => new { x.ProjectionName, x.TenantScopeKey, x.ProjectId });
            entity.Property(x => x.ProjectionName).HasColumnName("projection_name");
            entity.Property(x => x.TenantScopeKey).HasColumnName("tenant_scope_key");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.AuthoritySequence).HasColumnName("authority_sequence");
            entity.Property(x => x.Cursor).HasColumnName("cursor");
            entity.Property(x => x.LastSuccessAt).HasColumnName("last_success_at");
            entity.Property(x => x.NextRunAt).HasColumnName("next_run_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
        });
        modelBuilder.Entity<PlatformBackgroundRun>(entity =>
        {
            entity.ToTable("background_runs", "authority");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.JobType).HasColumnName("job_type");
            entity.Property(x => x.ScopeKey).HasColumnName("scope_key");
            entity.Property(x => x.Mode).HasColumnName("mode").HasConversion<string>();
            entity.Property(x => x.Status).HasColumnName("status").HasConversion<string>();
            entity.Property(x => x.Generation).HasColumnName("generation");
            entity.Property(x => x.AuthoritySequenceBoundary).HasColumnName("authority_sequence_boundary");
            entity.Property(x => x.Cursor).HasColumnName("cursor");
            entity.Property(x => x.ExpectedCount).HasColumnName("expected_count");
            entity.Property(x => x.ScannedCount).HasColumnName("scanned_count");
            entity.Property(x => x.CoverageComplete).HasColumnName("coverage_complete");
            entity.Property(x => x.StaleCount).HasColumnName("stale_count");
            entity.Property(x => x.DriftCount).HasColumnName("drift_count");
            entity.Property(x => x.RepairedCount).HasColumnName("repaired_count");
            entity.Property(x => x.RebuiltCount).HasColumnName("rebuilt_count");
            entity.Property(x => x.FailedCount).HasColumnName("failed_count");
            entity.Property(x => x.Attempt).HasColumnName("attempt");
            entity.Property(x => x.MaxAttempts).HasColumnName("max_attempts");
            entity.Property(x => x.OwnerId).HasColumnName("owner_id");
            entity.Property(x => x.LeaseTokenHash).HasColumnName("lease_token_hash");
            entity.Property(x => x.LeaseVersion).HasColumnName("lease_version");
            entity.Property(x => x.LeaseExpiresAt).HasColumnName("lease_expires_at");
            entity.Property(x => x.EligibleAt).HasColumnName("eligible_at");
            entity.Property(x => x.FailureCode).HasColumnName("failure_code");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.StartedAt).HasColumnName("started_at");
            entity.Property(x => x.CompletedAt).HasColumnName("completed_at");
            entity.HasIndex(x => new { x.TenantId, x.ProjectId, x.JobType, x.Mode, x.Status });
        });
        modelBuilder.Entity<PlatformBackgroundEvent>(entity =>
        {
            entity.ToTable("background_events", "audit");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasColumnName("id");
            entity.Property(x => x.RunId).HasColumnName("run_id");
            entity.Property(x => x.Sequence).HasColumnName("sequence");
            entity.Property(x => x.EventType).HasColumnName("event_type").HasConversion<string>();
            entity.Property(x => x.PayloadJson).HasColumnName("payload_json").HasColumnType("jsonb");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.HasOne(x => x.Run).WithMany(x => x.Events).HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.RunId, x.Sequence }).IsUnique();
        });
    }
}

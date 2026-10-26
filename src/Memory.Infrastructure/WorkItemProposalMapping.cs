using Memory.Application;
using Memory.Domain;
using Microsoft.EntityFrameworkCore;

namespace Memory.Infrastructure;

public static class WorkItemProposalMapping
{
    public static void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WorkItemProposalOperation>(entity =>
        {
            entity.ToTable("work_item_proposal_operations", table =>
            {
                table.HasCheckConstraint("ck_work_item_proposal_original_revision", "original_revision > 0");
                table.HasCheckConstraint("ck_work_item_proposal_request", "length(request_id) BETWEEN 1 AND 200 AND request_id = btrim(request_id)");
                table.HasCheckConstraint("ck_work_item_proposal_hash", $"request_hash ~ '^[0-9a-f]{{64}}$' AND hash_version = '{WorkItemEditContract.IntentHashVersion}'");
                table.HasCheckConstraint("ck_work_item_proposal_project", "length(btrim(project_id)) > 0 AND project_key = public.project_identity_key(project_id)");
                table.HasCheckConstraint("ck_work_item_proposal_state", """
                    (state = 'Dispatched' AND conflict_code IS NULL AND receipt_id IS NULL AND finalized_at IS NULL)
                    OR (state = 'Conflict' AND conflict_code IS NOT NULL AND conflict_code IN ('MissingReadVersion','StaleReadVersion','RequestIdentityMismatch','RequiresReopen') AND receipt_id IS NULL AND finalized_at IS NULL)
                    OR (state = 'Applied' AND conflict_code IS NULL AND receipt_id IS NOT NULL AND receipt_id <> '00000000-0000-0000-0000-000000000000'::uuid AND finalized_at IS NOT NULL)
                    """);
            });
            entity.HasKey(x => x.ProposalId);
            entity.Property(x => x.ProposalId).HasColumnName("proposal_id").ValueGeneratedNever();
            entity.Property(x => x.TenantId).HasColumnName("tenant_id");
            entity.Property(x => x.ActorUserId).HasColumnName("actor_user_id");
            entity.Property(x => x.ProjectId).HasColumnName("project_id");
            entity.Property(x => x.ProjectKey).HasColumnName("project_key");
            entity.Property(x => x.WorkItemId).HasColumnName("work_item_id");
            entity.Property(x => x.OriginalRevision).HasColumnName("original_revision");
            entity.Property(x => x.RequestId).HasColumnName("request_id").HasMaxLength(200);
            entity.Property(x => x.RequestHash).HasColumnName("request_hash").HasMaxLength(64);
            entity.Property(x => x.HashVersion).HasColumnName("hash_version").HasMaxLength(64);
            entity.Property(x => x.Archived).HasColumnName("archived");
            entity.Property(x => x.State).HasColumnName("state").HasConversion<string>().IsConcurrencyToken();
            entity.Property(x => x.ConflictCode).HasColumnName("conflict_code").HasMaxLength(32);
            entity.Property(x => x.ReceiptId).HasColumnName("receipt_id");
            entity.Property(x => x.CreatedAt).HasColumnName("created_at");
            entity.Property(x => x.UpdatedAt).HasColumnName("updated_at");
            entity.Property(x => x.FinalizedAt).HasColumnName("finalized_at");
            entity.HasOne<ConversationInsight>().WithMany().HasForeignKey(x => x.ProposalId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<ProjectWorkItem>().WithMany().HasForeignKey(x => x.WorkItemId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<WorkItemEditReceiptRecord>().WithMany().HasForeignKey(x => x.ReceiptId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => new { x.TenantId, x.ActorUserId, x.ProjectKey, x.WorkItemId, x.RequestId });
        });
    }
}

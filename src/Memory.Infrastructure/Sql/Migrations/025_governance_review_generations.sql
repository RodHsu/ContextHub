ALTER TABLE knowledge_governance_snapshots
    ADD COLUMN IF NOT EXISTS generation INTEGER NOT NULL DEFAULT 0;

DROP INDEX IF EXISTS ix_knowledge_governance_snapshots_actor_run_phase;

CREATE UNIQUE INDEX IF NOT EXISTS ix_knowledge_governance_snapshots_actor_run_phase_generation
    ON knowledge_governance_snapshots(tenant_id, owner_user_id, governance_run_id, is_re_review, generation);

CREATE INDEX IF NOT EXISTS ix_knowledge_governance_snapshots_actor_run_latest
    ON knowledge_governance_snapshots(tenant_id, owner_user_id, governance_run_id, generation DESC);

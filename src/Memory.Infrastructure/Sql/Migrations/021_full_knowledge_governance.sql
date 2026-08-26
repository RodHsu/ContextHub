ALTER TABLE conversation_insights
    ADD COLUMN IF NOT EXISTS governance_reason TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_run_id TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_retry_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS governance_updated_at TIMESTAMPTZ NULL;

CREATE TABLE IF NOT EXISTS knowledge_governance_snapshots
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    owner_user_id UUID NOT NULL,
    governance_run_id TEXT NOT NULL,
    is_re_review BOOLEAN NOT NULL DEFAULT FALSE,
    project_set_hash TEXT NOT NULL,
    project_ids_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    result_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    total_count INTEGER NOT NULL DEFAULT 0,
    scanned_count INTEGER NOT NULL DEFAULT 0,
    coverage_complete BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_knowledge_governance_snapshots_actor_run_phase
    ON knowledge_governance_snapshots(tenant_id, owner_user_id, governance_run_id, is_re_review);

CREATE INDEX IF NOT EXISTS ix_knowledge_governance_snapshots_completed_at
    ON knowledge_governance_snapshots(completed_at DESC);

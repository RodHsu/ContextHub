CREATE TABLE IF NOT EXISTS memory_retention_states
(
    resource_id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    owner_user_id UUID NOT NULL,
    project_id TEXT NOT NULL,
    resource_type TEXT NOT NULL,
    classification TEXT NOT NULL,
    policy_kind TEXT NOT NULL,
    policy_version TEXT NOT NULL,
    grace_period_days INTEGER NOT NULL,
    lifecycle_status TEXT NOT NULL,
    quarantined_at TIMESTAMPTZ NULL,
    delete_eligible_at TIMESTAMPTZ NULL,
    last_revalidated_at TIMESTAMPTZ NULL,
    evidence_fingerprint TEXT NOT NULL DEFAULT '',
    reason_codes_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    blocked_reasons_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    replacement_resource_id UUID NULL,
    governance_run_id TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_memory_retention_states_matured
    ON memory_retention_states(tenant_id, owner_user_id, delete_eligible_at)
    WHERE lifecycle_status = 'Eligible' AND delete_eligible_at IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_memory_retention_states_project
    ON memory_retention_states(tenant_id, owner_user_id, project_id, lifecycle_status);

CREATE TABLE IF NOT EXISTS resource_tombstones
(
    id UUID PRIMARY KEY,
    resource_id UUID NOT NULL,
    resource_type TEXT NOT NULL,
    tenant_id UUID NOT NULL,
    owner_user_id UUID NOT NULL,
    project_id TEXT NOT NULL,
    content_hash TEXT NOT NULL,
    classification TEXT NOT NULL,
    archived_at TIMESTAMPTZ NOT NULL,
    deleted_at TIMESTAMPTZ NOT NULL,
    retention_policy_version TEXT NOT NULL,
    reason_codes_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    replacement_resource_id UUID NULL,
    governance_run_id TEXT NOT NULL,
    audit_id UUID NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_resource_tombstones_resource
    ON resource_tombstones(tenant_id, owner_user_id, resource_type, resource_id);

CREATE INDEX IF NOT EXISTS ix_resource_tombstones_project_deleted
    ON resource_tombstones(tenant_id, owner_user_id, project_id, deleted_at DESC);

ALTER TABLE governance_findings
    ADD COLUMN IF NOT EXISTS governance_evidence_fingerprint TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_policy_version TEXT NOT NULL DEFAULT '';

ALTER TABLE conversation_insights
    ADD COLUMN IF NOT EXISTS governance_evidence_fingerprint TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_policy_version TEXT NOT NULL DEFAULT '';

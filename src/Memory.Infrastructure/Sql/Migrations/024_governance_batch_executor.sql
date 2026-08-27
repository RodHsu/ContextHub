CREATE TABLE IF NOT EXISTS governance_batch_runs
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    owner_user_id UUID NOT NULL,
    governance_run_id TEXT NOT NULL,
    snapshot_token TEXT NOT NULL,
    project_set_hash TEXT NOT NULL,
    project_ids_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    plan_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    last_cursor TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_governance_batch_runs_actor_run_snapshot
    ON governance_batch_runs(tenant_id, owner_user_id, governance_run_id, snapshot_token);

CREATE INDEX IF NOT EXISTS ix_governance_batch_runs_expires_at
    ON governance_batch_runs(expires_at);

CREATE TABLE IF NOT EXISTS governance_batch_executions
(
    id UUID PRIMARY KEY,
    governance_batch_run_id UUID NOT NULL REFERENCES governance_batch_runs(id) ON DELETE CASCADE,
    request_hash TEXT NOT NULL,
    request_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    cursor_before TEXT NOT NULL DEFAULT '',
    cursor_after TEXT NOT NULL DEFAULT '',
    status TEXT NOT NULL DEFAULT 'Running',
    result_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_governance_batch_executions_run_request
    ON governance_batch_executions(governance_batch_run_id, request_hash);

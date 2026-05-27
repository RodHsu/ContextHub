CREATE TABLE IF NOT EXISTS maintenance_runs
(
    id UUID PRIMARY KEY,
    maintenance_type TEXT NOT NULL,
    status TEXT NOT NULL,
    started_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NULL,
    triggered_by TEXT NOT NULL DEFAULT 'system',
    policy_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    result_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    error TEXT NOT NULL DEFAULT ''
);

CREATE INDEX IF NOT EXISTS ix_maintenance_runs_started_at
    ON maintenance_runs(started_at DESC);

CREATE INDEX IF NOT EXISTS ix_retrieval_events_created_at_id
    ON retrieval_events(created_at DESC, id DESC);

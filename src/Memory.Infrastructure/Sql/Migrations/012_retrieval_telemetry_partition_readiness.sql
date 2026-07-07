ALTER TABLE retrieval_hits
    ADD COLUMN IF NOT EXISTS created_at TIMESTAMPTZ NULL;

CREATE TABLE IF NOT EXISTS retrieval_telemetry_partition_runs
(
    id UUID PRIMARY KEY,
    parent_table TEXT NOT NULL,
    partition_name TEXT NOT NULL,
    partition_start DATE NOT NULL,
    partition_end DATE NOT NULL,
    action TEXT NOT NULL,
    triggered_by TEXT NOT NULL DEFAULT 'system',
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE INDEX IF NOT EXISTS ix_retrieval_telemetry_partition_runs_parent_created
    ON retrieval_telemetry_partition_runs(parent_table, created_at DESC);

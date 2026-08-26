ALTER TABLE governance_findings
    ADD COLUMN IF NOT EXISTS governance_reason TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_run_id TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_actor TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_retry_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS governance_updated_at TIMESTAMPTZ NULL;

ALTER TABLE suggested_actions
    ADD COLUMN IF NOT EXISTS dedup_key TEXT NOT NULL DEFAULT '';

UPDATE suggested_actions
SET dedup_key = COALESCE(payload_json ->> 'dedupKey', '')
WHERE dedup_key = '';

CREATE INDEX IF NOT EXISTS ix_suggested_actions_project_type_dedup_key
    ON suggested_actions(project_id, type, dedup_key);

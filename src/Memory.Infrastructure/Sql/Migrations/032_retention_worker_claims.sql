ALTER TABLE memory_retention_states
    ADD COLUMN IF NOT EXISTS claim_token TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS claimed_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS claim_attempt_count INTEGER NOT NULL DEFAULT 0,
    ADD COLUMN IF NOT EXISTS claim_last_error TEXT NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS ix_memory_retention_states_worker_claim
    ON memory_retention_states(lifecycle_status, delete_eligible_at, claimed_at)
    WHERE lifecycle_status = 'Eligible' AND delete_eligible_at IS NOT NULL;

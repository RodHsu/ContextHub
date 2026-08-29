ALTER TABLE governance_run_receipts
    ADD COLUMN IF NOT EXISTS event_type text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS status text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS latest_batch_received boolean NOT NULL DEFAULT false,
    ADD COLUMN IF NOT EXISTS request_identity_hash text NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS ix_governance_run_receipts_request_identity
    ON governance_run_receipts(tenant_id, owner_user_id, governance_run_id, request_identity_hash)
    WHERE request_identity_hash <> '';

COMMENT ON COLUMN governance_run_receipts.request_identity_hash IS
    'SHA-256 identity of the received governance batch request excluding optional published contract echo fields; no governed content is stored.';

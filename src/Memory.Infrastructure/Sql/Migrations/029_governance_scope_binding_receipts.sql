ALTER TABLE governance_run_receipts
    ADD COLUMN IF NOT EXISTS request_hash text NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS failure_phase text NOT NULL DEFAULT '';

CREATE INDEX IF NOT EXISTS ix_governance_run_receipts_request_hash
    ON governance_run_receipts(tenant_id, owner_user_id, governance_run_id, request_hash)
    WHERE request_hash <> '';

COMMENT ON COLUMN governance_run_receipts.request_hash IS
    'SHA-256 identity of the normalized complete governance batch request; no governed content is stored.';

COMMENT ON COLUMN governance_run_receipts.failure_phase IS
    'Machine-readable batch failure phase such as PreExecutionScopeValidation, ItemExecution, or OutcomeUnknown.';

CREATE TABLE IF NOT EXISTS governance_run_receipts (
    id UUID PRIMARY KEY,
    event_sequence BIGINT GENERATED ALWAYS AS IDENTITY UNIQUE,
    tenant_id UUID NOT NULL,
    owner_user_id UUID NOT NULL,
    governance_run_id TEXT NOT NULL,
    event_key TEXT NOT NULL,
    actor TEXT NOT NULL,
    execution_mode TEXT NOT NULL,
    started_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NOT NULL,
    tool_contract_version TEXT NOT NULL,
    schema_hash TEXT NOT NULL,
    published_catalog_version TEXT NOT NULL,
    initial_snapshot_token TEXT NOT NULL,
    final_snapshot_token TEXT NOT NULL,
    coverage_complete BOOLEAN NOT NULL,
    initial_governance_actionable INTEGER NOT NULL,
    final_governance_actionable INTEGER NOT NULL,
    candidate_count INTEGER NOT NULL,
    execution_actionable_count INTEGER NOT NULL,
    governed_exception_count INTEGER NOT NULL,
    applied INTEGER NOT NULL,
    failed INTEGER NOT NULL,
    deferred INTEGER NOT NULL,
    requires_user_decision INTEGER NOT NULL,
    host_blocked INTEGER NOT NULL,
    quarantined INTEGER NOT NULL,
    delete_eligible INTEGER NOT NULL,
    delete_matured INTEGER NOT NULL,
    auto_deleted INTEGER NOT NULL,
    delete_cancelled INTEGER NOT NULL,
    tombstoned INTEGER NOT NULL,
    semantic_auto_resolved INTEGER NOT NULL,
    business_work_item_actionable INTEGER NOT NULL,
    final_convergence_status TEXT NOT NULL,
    stopped_reason TEXT NOT NULL,
    audit_ids_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    project_ids_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    is_replay BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT uq_governance_run_receipts_event
        UNIQUE (tenant_id, owner_user_id, governance_run_id, event_key)
);

CREATE INDEX IF NOT EXISTS ix_governance_run_receipts_owner_completed
    ON governance_run_receipts(tenant_id, owner_user_id, completed_at DESC);

CREATE INDEX IF NOT EXISTS ix_governance_run_receipts_run_completed
    ON governance_run_receipts(tenant_id, owner_user_id, governance_run_id, completed_at DESC);

CREATE OR REPLACE FUNCTION reject_governance_run_receipt_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'governance_run_receipts is append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS governance_run_receipts_immutable ON governance_run_receipts;
CREATE TRIGGER governance_run_receipts_immutable
    BEFORE UPDATE OR DELETE ON governance_run_receipts
    FOR EACH ROW EXECUTE FUNCTION reject_governance_run_receipt_mutation();

COMMENT ON TABLE governance_run_receipts IS
    'Append-only immutable governance execution receipt events; latest event is the read model for one GovernanceRunId.';
COMMENT ON COLUMN governance_run_receipts.event_key IS
    'Deterministic idempotency key for one review, execution, replay, host-blocked, timeout, or internal-retention event.';

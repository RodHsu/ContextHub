ALTER TABLE governance_findings
    ADD COLUMN IF NOT EXISTS governance_blocked_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS governance_last_reevaluated_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS governance_blocking_layer TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_reason_class TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_related_tool TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_evidence_changed_since_block BOOLEAN NOT NULL DEFAULT FALSE;

ALTER TABLE conversation_insights
    ADD COLUMN IF NOT EXISTS governance_blocked_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS governance_last_reevaluated_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS governance_blocking_layer TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_reason_class TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_related_tool TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS governance_evidence_changed_since_block BOOLEAN NOT NULL DEFAULT FALSE;

CREATE INDEX IF NOT EXISTS ix_governance_findings_host_blocked_reevaluation
    ON governance_findings(tenant_id, owner_user_id, governance_last_reevaluated_at)
    WHERE status = 'HostBlocked';

CREATE INDEX IF NOT EXISTS ix_conversation_insights_host_blocked_reevaluation
    ON conversation_insights(tenant_id, owner_user_id, governance_last_reevaluated_at)
    WHERE promotion_status = 'HostBlocked';

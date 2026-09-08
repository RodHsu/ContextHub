ALTER TABLE governance_findings
    ADD COLUMN IF NOT EXISTS governance_last_evidence_changed_at TIMESTAMPTZ NULL;

ALTER TABLE conversation_insights
    ADD COLUMN IF NOT EXISTS governance_last_evidence_changed_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS ix_governance_findings_exception_aging
    ON governance_findings(tenant_id, owner_user_id, status, governance_last_evidence_changed_at, governance_last_reevaluated_at)
    WHERE status IN ('Deferred', 'RequiresUserDecision', 'HostBlocked');

CREATE INDEX IF NOT EXISTS ix_conversation_insights_exception_aging
    ON conversation_insights(tenant_id, owner_user_id, promotion_status, governance_last_evidence_changed_at, governance_last_reevaluated_at)
    WHERE promotion_status IN ('Deferred', 'RequiresUserDecision', 'HostBlocked');

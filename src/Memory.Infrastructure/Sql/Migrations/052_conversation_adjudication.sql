-- Additive ledger only. No historical insight is reclassified by migration.
CREATE TABLE IF NOT EXISTS conversation_adjudications (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    owner_user_id uuid NOT NULL,
    project_id text NOT NULL,
    insight_id uuid NOT NULL REFERENCES conversation_insights(id) ON DELETE RESTRICT,
    request_hash text NOT NULL,
    evidence_fingerprint text NOT NULL,
    action text NOT NULL,
    outcome text NOT NULL,
    previous_status text NOT NULL,
    result_status text NOT NULL,
    reason text NOT NULL,
    governance_run_id text NOT NULL,
    actor text NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT ck_conversation_adjudication_reason CHECK (length(trim(reason)) BETWEEN 1 AND 2000),
    CONSTRAINT ck_conversation_adjudication_action CHECK (action IN ('ResolveSkipped','RejectProposal','Reconcile','Reopen'))
);
CREATE INDEX IF NOT EXISTS ix_conversation_adjudications_owner_insight
    ON conversation_adjudications(tenant_id, owner_user_id, project_id, insight_id, created_at);

CREATE OR REPLACE FUNCTION reject_conversation_adjudication_change() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    RAISE EXCEPTION 'Conversation adjudication receipts are append-only';
END;
$$;
DROP TRIGGER IF EXISTS conversation_adjudications_append_only ON conversation_adjudications;
CREATE TRIGGER conversation_adjudications_append_only
    BEFORE UPDATE OR DELETE ON conversation_adjudications
    FOR EACH ROW EXECUTE FUNCTION reject_conversation_adjudication_change();

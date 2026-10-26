-- Source-only migration. Dispatch markers and edit receipts have separate commit owners.
-- Existing Pending proposals are intentionally not backfilled from current WorkItem revisions.
CREATE TABLE IF NOT EXISTS work_item_proposal_operations (
    proposal_id uuid PRIMARY KEY REFERENCES conversation_insights(id) ON DELETE RESTRICT,
    tenant_id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    project_id text NOT NULL,
    project_key text NOT NULL,
    work_item_id uuid NOT NULL REFERENCES project_work_items(id) ON DELETE RESTRICT,
    original_revision bigint NOT NULL,
    request_id varchar(200) NOT NULL,
    request_hash varchar(64) NOT NULL,
    hash_version varchar(64) NOT NULL,
    archived boolean NOT NULL,
    state text NOT NULL,
    conflict_code varchar(32) NULL,
    receipt_id uuid NULL REFERENCES work_item_edit_receipts(id) ON DELETE RESTRICT,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    finalized_at timestamptz NULL,
    CONSTRAINT ck_work_item_proposal_original_revision CHECK (original_revision > 0),
    CONSTRAINT ck_work_item_proposal_request CHECK (length(request_id) BETWEEN 1 AND 200 AND request_id = btrim(request_id)),
    CONSTRAINT ck_work_item_proposal_hash CHECK (request_hash ~ '^[0-9a-f]{64}$' AND hash_version = 'WorkItemEditIntent/v2'),
    CONSTRAINT ck_work_item_proposal_project CHECK (length(btrim(project_id)) > 0 AND project_key = public.project_identity_key(project_id)),
    CONSTRAINT ck_work_item_proposal_state CHECK (
        (state = 'Dispatched' AND conflict_code IS NULL AND receipt_id IS NULL AND finalized_at IS NULL)
        OR (state = 'Conflict' AND conflict_code IS NOT NULL AND conflict_code IN ('MissingReadVersion','StaleReadVersion','RequestIdentityMismatch','RequiresReopen') AND receipt_id IS NULL AND finalized_at IS NULL)
        OR (state = 'Applied' AND conflict_code IS NULL AND receipt_id IS NOT NULL AND receipt_id <> '00000000-0000-0000-0000-000000000000'::uuid AND finalized_at IS NOT NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_work_item_proposal_operation_identity
    ON work_item_proposal_operations (tenant_id, actor_user_id, project_key, work_item_id, request_id);

CREATE OR REPLACE FUNCTION guard_work_item_proposal_operation() RETURNS trigger AS $$
BEGIN
    IF TG_OP = 'DELETE' THEN
        RAISE EXCEPTION 'Work item proposal coordination evidence cannot be deleted.';
    END IF;
    IF NEW.proposal_id IS DISTINCT FROM OLD.proposal_id OR
       NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR
       NEW.actor_user_id IS DISTINCT FROM OLD.actor_user_id OR
       NEW.project_id IS DISTINCT FROM OLD.project_id OR
       NEW.project_key IS DISTINCT FROM OLD.project_key OR
       NEW.work_item_id IS DISTINCT FROM OLD.work_item_id OR
       NEW.original_revision IS DISTINCT FROM OLD.original_revision OR
       NEW.request_id IS DISTINCT FROM OLD.request_id OR
       NEW.request_hash IS DISTINCT FROM OLD.request_hash OR
       NEW.hash_version IS DISTINCT FROM OLD.hash_version OR
       NEW.archived IS DISTINCT FROM OLD.archived OR
       NEW.created_at IS DISTINCT FROM OLD.created_at THEN
        RAISE EXCEPTION 'The original work item proposal intent is immutable.';
    END IF;
    IF OLD.state IN ('Conflict', 'Applied') AND NEW IS DISTINCT FROM OLD THEN
        RAISE EXCEPTION 'A terminal work item proposal operation is immutable.';
    END IF;
    IF OLD.state <> 'Dispatched' OR NEW.state NOT IN ('Dispatched', 'Conflict', 'Applied') THEN
        RAISE EXCEPTION 'The work item proposal operation transition is invalid.';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS tr_work_item_proposal_operation_guard ON work_item_proposal_operations;
CREATE TRIGGER tr_work_item_proposal_operation_guard
    BEFORE UPDATE OR DELETE ON work_item_proposal_operations
    FOR EACH ROW EXECUTE FUNCTION guard_work_item_proposal_operation();

-- A dispatched proposal must not be reclassified into a different tool or given a
-- different caller intent through an ordinary ConversationInsight writer. Lifecycle
-- status, rejection note, governance timestamps and promoted target remain writable.
-- Insights without a dispatch marker retain their existing update behavior.
CREATE OR REPLACE FUNCTION guard_dispatched_work_item_proposal_intent() RETURNS trigger AS $$
BEGIN
    IF EXISTS (SELECT 1 FROM work_item_proposal_operations WHERE proposal_id = OLD.id) THEN
        IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR
           NEW.owner_user_id IS DISTINCT FROM OLD.owner_user_id OR
           NEW.project_id IS DISTINCT FROM OLD.project_id OR
           NEW.source_system IS DISTINCT FROM OLD.source_system OR
           NEW.tags IS DISTINCT FROM OLD.tags OR
           NEW.metadata_json IS DISTINCT FROM OLD.metadata_json OR
           NEW.content IS DISTINCT FROM OLD.content THEN
            RAISE EXCEPTION 'The original dispatched work item proposal intent is immutable.';
        END IF;
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS tr_dispatched_work_item_proposal_intent_guard ON conversation_insights;
CREATE TRIGGER tr_dispatched_work_item_proposal_intent_guard
    BEFORE UPDATE ON conversation_insights
    FOR EACH ROW EXECUTE FUNCTION guard_dispatched_work_item_proposal_intent();

-- Rollback: disable new proposal dispatch/finalize entry points, retain this additive
-- evidence and both RESTRICT-linked records. Do not reset Dispatched or drop evidence
-- to manufacture a retry. No destructive down migration is provided.
-- Retention/export/system-metadata classification and governance coverage of this new
-- table remain pending. This migration adds no TTL, archive/delete worker or acceptance claim.

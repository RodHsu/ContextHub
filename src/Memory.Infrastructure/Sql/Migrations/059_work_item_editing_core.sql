-- Additive source migration. No historical revisions are reconstructed by this baseline.
ALTER TABLE project_work_items ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;
ALTER TABLE project_work_items DROP CONSTRAINT IF EXISTS ck_work_item_revision_positive;
ALTER TABLE project_work_items ADD CONSTRAINT ck_work_item_revision_positive CHECK (revision > 0);

CREATE TABLE IF NOT EXISTS work_item_edit_history (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    owner_user_id uuid NULL,
    work_item_id uuid NOT NULL REFERENCES project_work_items(id) ON DELETE RESTRICT,
    project_id text NOT NULL,
    previous_revision bigint NOT NULL,
    revision bigint NOT NULL,
    operation text NOT NULL,
    request_id text NOT NULL,
    before_json jsonb NOT NULL,
    after_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT ck_work_item_edit_history_revision_step CHECK (previous_revision > 0 AND revision = previous_revision + 1),
    CONSTRAINT ck_work_item_edit_history_request CHECK (length(request_id) BETWEEN 1 AND 200),
    UNIQUE (work_item_id, revision)
);

CREATE TABLE IF NOT EXISTS work_item_edit_receipts (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    actor_user_id uuid NOT NULL,
    work_item_id uuid NOT NULL REFERENCES project_work_items(id) ON DELETE RESTRICT,
    project_id text NOT NULL,
    project_key text NOT NULL,
    request_id text NOT NULL,
    request_hash text NOT NULL,
    result_json jsonb NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT ck_work_item_edit_receipts_request CHECK (length(request_id) BETWEEN 1 AND 200),
    CONSTRAINT ck_work_item_edit_receipts_hash CHECK (request_hash ~ '^[0-9a-f]{64}$'),
    UNIQUE (tenant_id, actor_user_id, project_key, work_item_id, request_id)
);

CREATE OR REPLACE FUNCTION prevent_work_item_edit_evidence_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'Work item edit evidence is append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS work_item_edit_history_immutable ON work_item_edit_history;
CREATE TRIGGER work_item_edit_history_immutable BEFORE UPDATE OR DELETE ON work_item_edit_history
FOR EACH ROW EXECUTE FUNCTION prevent_work_item_edit_evidence_mutation();
DROP TRIGGER IF EXISTS work_item_edit_receipts_immutable ON work_item_edit_receipts;
CREATE TRIGGER work_item_edit_receipts_immutable BEFORE UPDATE OR DELETE ON work_item_edit_receipts
FOR EACH ROW EXECUTE FUNCTION prevent_work_item_edit_evidence_mutation();

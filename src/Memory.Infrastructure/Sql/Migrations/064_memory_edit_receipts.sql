CREATE TABLE IF NOT EXISTS memory_edit_receipts (
    id uuid PRIMARY KEY,
    tenant_id uuid NOT NULL,
    owner_user_id uuid NOT NULL,
    project_id text NOT NULL,
    project_key text NOT NULL,
    memory_item_id uuid NOT NULL,
    request_id text NOT NULL,
    request_hash text NOT NULL,
    hash_version text NOT NULL,
    previous_version integer NOT NULL,
    version integer NOT NULL,
    revision_id uuid NOT NULL,
    committed_at timestamptz NOT NULL,
    CONSTRAINT ck_memory_edit_receipt_project CHECK (
        length(project_key) > 0 AND project_key = public.project_identity_key(project_id)
    ),
    CONSTRAINT ck_memory_edit_receipt_request CHECK (
        length(request_id) BETWEEN 1 AND 200 AND request_hash ~ '^[0-9a-f]{64}$' AND length(hash_version) > 0
    ),
    CONSTRAINT ck_memory_edit_receipt_version CHECK (
        previous_version > 0 AND version::bigint = previous_version::bigint + 1
    )
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_edit_receipt_scope_request
    ON memory_edit_receipts (tenant_id, owner_user_id, project_key, memory_item_id, request_id);

DROP TRIGGER IF EXISTS trg_memory_edit_receipts_immutable ON memory_edit_receipts;
CREATE TRIGGER trg_memory_edit_receipts_immutable
    BEFORE UPDATE OR DELETE ON memory_edit_receipts
    FOR EACH ROW EXECUTE FUNCTION audit.reject_immutable_mutation();

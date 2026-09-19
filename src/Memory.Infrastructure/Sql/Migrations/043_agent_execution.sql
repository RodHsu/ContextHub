CREATE TABLE IF NOT EXISTS agent_executions (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    work_item_id uuid NOT NULL REFERENCES project_work_items(id) ON DELETE RESTRICT,
    parent_execution_id uuid NULL REFERENCES agent_executions(id) ON DELETE RESTRICT,
    project_id text NOT NULL,
    repository_id text NOT NULL,
    agent_type text NOT NULL,
    required_capabilities_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    allowed_actions_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    package_json jsonb NOT NULL,
    package_hash text NOT NULL,
    package_context_version text NOT NULL,
    skill_snapshot_json jsonb NULL,
    status text NOT NULL,
    priority integer NOT NULL DEFAULT 0,
    attempt integer NOT NULL DEFAULT 1,
    max_attempts integer NOT NULL DEFAULT 3,
    claimed_by_agent_id text NOT NULL DEFAULT '',
    lease_token_hash text NOT NULL DEFAULT '',
    lease_version bigint NOT NULL DEFAULT 0,
    lease_expires_at timestamptz NULL,
    failure_class text NOT NULL DEFAULT '',
    structured_reason_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    eligible_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    started_at timestamptz NULL,
    completed_at timestamptz NULL,
    CONSTRAINT ck_agent_executions_attempt CHECK (attempt > 0 AND max_attempts >= attempt),
    CONSTRAINT ck_agent_executions_priority CHECK (priority BETWEEN 0 AND 100),
    CONSTRAINT ck_agent_executions_lease_version CHECK (lease_version >= 0),
    CONSTRAINT ck_agent_executions_status CHECK (status IN ('Ready','Claimed','Running','Blocked','FailedRetryable','FailedTerminal','Completed','Abandoned','Expired','Cancelled')),
    CONSTRAINT ck_agent_executions_package_hash CHECK (package_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_agent_executions_lease CHECK (
        (status IN ('Claimed','Running') AND claimed_by_agent_id <> '' AND lease_token_hash ~ '^[0-9a-f]{64}$' AND lease_expires_at IS NOT NULL)
        OR
        (status NOT IN ('Claimed','Running') AND claimed_by_agent_id = '' AND lease_token_hash = '' AND lease_expires_at IS NULL)
    )
);

CREATE INDEX IF NOT EXISTS ix_agent_executions_queue
    ON agent_executions (tenant_id, project_id, repository_id, status, eligible_at, priority DESC, created_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_executions_one_active_work_item
    ON agent_executions (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), work_item_id)
    WHERE status IN ('Claimed','Running');
CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_executions_one_open_work_item
    ON agent_executions (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), work_item_id)
    WHERE status IN ('Ready','Claimed','Running','Blocked','FailedRetryable');

CREATE TABLE IF NOT EXISTS agent_execution_events (
    id uuid PRIMARY KEY,
    execution_id uuid NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
    event_type text NOT NULL,
    sequence bigint NOT NULL,
    agent_id text NOT NULL DEFAULT '',
    payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL,
    CONSTRAINT ux_agent_execution_events_sequence UNIQUE (execution_id, sequence)
);

CREATE TABLE IF NOT EXISTS agent_execution_operations (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    execution_id uuid NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
    agent_id text NOT NULL,
    operation text NOT NULL,
    idempotency_key text NOT NULL,
    request_hash text NOT NULL,
    protected_result_json text NOT NULL,
    created_at timestamptz NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_agent_execution_operations_idempotency
    ON agent_execution_operations (
        COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid),
        agent_id,
        operation,
        idempotency_key);

CREATE OR REPLACE FUNCTION prevent_agent_execution_package_mutation() RETURNS trigger AS $$
BEGIN
    IF NEW.tenant_id IS DISTINCT FROM OLD.tenant_id OR
       NEW.owner_user_id IS DISTINCT FROM OLD.owner_user_id OR
       NEW.work_item_id IS DISTINCT FROM OLD.work_item_id OR
       NEW.parent_execution_id IS DISTINCT FROM OLD.parent_execution_id OR
       NEW.project_id IS DISTINCT FROM OLD.project_id OR
       NEW.repository_id IS DISTINCT FROM OLD.repository_id OR
       NEW.agent_type IS DISTINCT FROM OLD.agent_type OR
       NEW.required_capabilities_json IS DISTINCT FROM OLD.required_capabilities_json OR
       NEW.allowed_actions_json IS DISTINCT FROM OLD.allowed_actions_json OR
       NEW.package_json IS DISTINCT FROM OLD.package_json OR
       NEW.package_hash IS DISTINCT FROM OLD.package_hash OR
       NEW.package_context_version IS DISTINCT FROM OLD.package_context_version OR
       NEW.skill_snapshot_json IS DISTINCT FROM OLD.skill_snapshot_json THEN
        RAISE EXCEPTION 'agent execution package is immutable after preparation';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_agent_execution_package_immutable ON agent_executions;
CREATE TRIGGER trg_agent_execution_package_immutable
BEFORE UPDATE ON agent_executions
FOR EACH ROW EXECUTE FUNCTION prevent_agent_execution_package_mutation();

CREATE OR REPLACE FUNCTION prevent_agent_execution_audit_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'agent execution audit rows are append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_agent_execution_events_append_only ON agent_execution_events;
CREATE TRIGGER trg_agent_execution_events_append_only
BEFORE UPDATE OR DELETE ON agent_execution_events
FOR EACH ROW EXECUTE FUNCTION prevent_agent_execution_audit_mutation();

DROP TRIGGER IF EXISTS trg_agent_execution_operations_append_only ON agent_execution_operations;
CREATE TRIGGER trg_agent_execution_operations_append_only
BEFORE UPDATE OR DELETE ON agent_execution_operations
FOR EACH ROW EXECUTE FUNCTION prevent_agent_execution_audit_mutation();

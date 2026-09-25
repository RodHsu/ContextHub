CREATE SCHEMA IF NOT EXISTS authority;
CREATE SCHEMA IF NOT EXISTS audit;
CREATE SCHEMA IF NOT EXISTS monitoring;
CREATE SCHEMA IF NOT EXISTS diagnostics;

ALTER TABLE source_connections ADD COLUMN IF NOT EXISTS revision bigint NOT NULL DEFAULT 1;
ALTER TABLE source_connections DROP CONSTRAINT IF EXISTS ck_source_connections_revision;
ALTER TABLE source_connections ADD CONSTRAINT ck_source_connections_revision CHECK (revision > 0);

CREATE TABLE IF NOT EXISTS audit.authority_outbox_events (
    id uuid PRIMARY KEY,
    sequence bigint GENERATED ALWAYS AS IDENTITY UNIQUE,
    tenant_id uuid NULL,
    project_id text NOT NULL DEFAULT '',
    category text NOT NULL,
    aggregate_type text NOT NULL,
    aggregate_id text NOT NULL,
    event_type text NOT NULL,
    authority_revision bigint NOT NULL DEFAULT 0,
    security_critical boolean NOT NULL,
    payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at timestamptz NOT NULL,
    CONSTRAINT ck_authority_outbox_revision CHECK (authority_revision >= 0),
    CONSTRAINT ck_authority_outbox_payload_object CHECK (jsonb_typeof(payload_json) = 'object')
);
CREATE INDEX IF NOT EXISTS ix_authority_outbox_tenant_project_sequence ON audit.authority_outbox_events(tenant_id, project_id, sequence);
CREATE INDEX IF NOT EXISTS ix_authority_outbox_category_time ON audit.authority_outbox_events(category, occurred_at);

CREATE TABLE IF NOT EXISTS monitoring.outbox_deliveries (
    id uuid PRIMARY KEY,
    outbox_event_id uuid NOT NULL REFERENCES audit.authority_outbox_events(id) ON DELETE RESTRICT,
    consumer text NOT NULL,
    status text NOT NULL,
    attempt integer NOT NULL DEFAULT 0,
    last_error_code text NOT NULL DEFAULT '',
    eligible_at timestamptz NOT NULL,
    delivered_at timestamptz NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ux_monitoring_outbox_delivery UNIQUE(outbox_event_id, consumer),
    CONSTRAINT ck_monitoring_outbox_delivery_status CHECK (status IN ('Pending','Delivered','RetryScheduled','DeadLetter')),
    CONSTRAINT ck_monitoring_outbox_delivery_attempt CHECK (attempt >= 0)
);

CREATE TABLE IF NOT EXISTS monitoring.activity_projections (
    outbox_event_id uuid PRIMARY KEY REFERENCES audit.authority_outbox_events(id) ON DELETE RESTRICT,
    authority_sequence bigint NOT NULL,
    generation bigint NOT NULL,
    tenant_id uuid NULL,
    project_id text NOT NULL,
    category text NOT NULL,
    aggregate_type text NOT NULL,
    aggregate_id text NOT NULL,
    event_type text NOT NULL,
    authority_revision bigint NOT NULL,
    security_critical boolean NOT NULL,
    redacted_payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    occurred_at timestamptz NOT NULL,
    projected_at timestamptz NOT NULL,
    CONSTRAINT ck_activity_projection_generation CHECK (generation >= 0),
    CONSTRAINT ck_activity_projection_authority CHECK (authority_sequence > 0 AND authority_revision >= 0),
    CONSTRAINT ck_activity_projection_payload_object CHECK (jsonb_typeof(redacted_payload_json) = 'object')
);
CREATE INDEX IF NOT EXISTS ix_activity_projection_tenant_project_sequence ON monitoring.activity_projections(tenant_id, project_id, authority_sequence);
CREATE INDEX IF NOT EXISTS ix_activity_projection_category_time ON monitoring.activity_projections(category, occurred_at);

CREATE TABLE IF NOT EXISTS monitoring.projection_states (
    projection_name text NOT NULL,
    tenant_scope_key text NOT NULL,
    tenant_id uuid NULL,
    project_id text NOT NULL,
    generation bigint NOT NULL DEFAULT 0,
    authority_sequence bigint NOT NULL DEFAULT 0,
    cursor bigint NOT NULL DEFAULT 0,
    last_success_at timestamptz NULL,
    next_run_at timestamptz NULL,
    updated_at timestamptz NOT NULL,
    PRIMARY KEY(projection_name, tenant_scope_key, project_id),
    CONSTRAINT ck_projection_state_tenant_scope CHECK (tenant_scope_key = COALESCE(tenant_id::text, 'system')),
    CONSTRAINT ck_projection_state_monotonic CHECK (generation >= 0 AND authority_sequence >= 0 AND cursor >= 0)
);

CREATE TABLE IF NOT EXISTS authority.background_runs (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    project_id text NOT NULL,
    job_type text NOT NULL,
    scope_key text NOT NULL,
    mode text NOT NULL,
    status text NOT NULL,
    generation bigint NOT NULL DEFAULT 0,
    authority_sequence_boundary bigint NOT NULL DEFAULT 0,
    cursor bigint NOT NULL DEFAULT 0,
    expected_count bigint NOT NULL DEFAULT 0,
    scanned_count bigint NOT NULL DEFAULT 0,
    coverage_complete boolean NOT NULL DEFAULT false,
    stale_count bigint NOT NULL DEFAULT 0,
    drift_count bigint NOT NULL DEFAULT 0,
    repaired_count bigint NOT NULL DEFAULT 0,
    rebuilt_count bigint NOT NULL DEFAULT 0,
    failed_count bigint NOT NULL DEFAULT 0,
    attempt integer NOT NULL DEFAULT 1,
    max_attempts integer NOT NULL DEFAULT 5,
    owner_id text NOT NULL DEFAULT '',
    lease_token_hash text NOT NULL DEFAULT '',
    lease_version bigint NOT NULL DEFAULT 0,
    lease_expires_at timestamptz NULL,
    eligible_at timestamptz NOT NULL,
    failure_code text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    started_at timestamptz NULL,
    completed_at timestamptz NULL,
    CONSTRAINT ck_background_run_mode CHECK (mode IN ('Incremental','Full')),
    CONSTRAINT ck_background_run_status CHECK (status IN ('Pending','Running','RetryScheduled','Completed','FailedTerminal','Cancelled','DeadLetter')),
    CONSTRAINT ck_background_run_counts CHECK (generation >= 0 AND authority_sequence_boundary >= 0 AND cursor >= 0 AND expected_count >= 0 AND scanned_count >= 0 AND stale_count >= 0 AND drift_count >= 0 AND repaired_count >= 0 AND rebuilt_count >= 0 AND failed_count >= 0),
    CONSTRAINT ck_background_run_attempt CHECK (attempt > 0 AND max_attempts >= attempt),
    CONSTRAINT ck_background_run_lease CHECK ((status = 'Running' AND owner_id <> '' AND lease_token_hash ~ '^[0-9a-f]{64}$' AND lease_expires_at IS NOT NULL) OR status <> 'Running')
);
CREATE INDEX IF NOT EXISTS ix_background_runs_scope ON authority.background_runs(tenant_id, project_id, job_type, mode, status, eligible_at);
CREATE UNIQUE INDEX IF NOT EXISTS ux_background_runs_one_active_scope
    ON authority.background_runs(scope_key, job_type, mode) WHERE status = 'Running';

CREATE TABLE IF NOT EXISTS audit.background_events (
    id uuid PRIMARY KEY,
    run_id uuid NOT NULL REFERENCES authority.background_runs(id) ON DELETE CASCADE,
    sequence bigint NOT NULL,
    event_type text NOT NULL,
    payload_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL,
    CONSTRAINT ux_background_events_sequence UNIQUE(run_id, sequence),
    CONSTRAINT ck_background_event_type CHECK (event_type IN ('Prepared','Claimed','Checkpoint','Retrying','Completed','Failed','Cancelled','LeaseLost')),
    CONSTRAINT ck_background_event_payload_object CHECK (jsonb_typeof(payload_json) = 'object')
);

CREATE TABLE IF NOT EXISTS authority.agent_execution_resolution_snapshots (
    id uuid PRIMARY KEY,
    execution_id uuid NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
    attempt integer NOT NULL,
    resolution_sequence integer NOT NULL,
    retry_mode text NOT NULL,
    outcome text NOT NULL,
    authority_context_hash text NOT NULL,
    snapshot_hash text NOT NULL,
    evidence_refs_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    resolved_at timestamptz NOT NULL,
    CONSTRAINT ux_execution_resolution_sequence UNIQUE(execution_id, attempt, resolution_sequence),
    CONSTRAINT ck_execution_resolution_attempt CHECK (attempt > 0 AND resolution_sequence > 0),
    CONSTRAINT ck_execution_resolution_retry CHECK (retry_mode IN ('ReResolve','ReuseSnapshot')),
    CONSTRAINT ck_execution_resolution_outcome CHECK (outcome IN ('Resolved','RequiresStepUp','RequiresExternalApproval','HumanDecision','Denied')),
    CONSTRAINT ck_execution_resolution_hashes CHECK (authority_context_hash ~ '^[0-9a-f]{64}$' AND snapshot_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_execution_resolution_evidence CHECK (jsonb_typeof(evidence_refs_json) = 'array')
);

CREATE TABLE IF NOT EXISTS authority.agent_execution_resolution_items (
    id uuid PRIMARY KEY,
    snapshot_id uuid NOT NULL REFERENCES authority.agent_execution_resolution_snapshots(id) ON DELETE CASCADE,
    requirement_id uuid NOT NULL,
    kind text NOT NULL,
    outcome text NOT NULL,
    logical_resource_id uuid NOT NULL,
    resolved_version_id uuid NULL,
    integrity_identity text NOT NULL DEFAULT '',
    authority_revision bigint NOT NULL DEFAULT 0,
    policy_revision text NOT NULL DEFAULT '',
    capability_lease_id uuid NULL,
    capability_expires_at timestamptz NULL,
    reason_code text NOT NULL DEFAULT '',
    evidence_refs_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL,
    CONSTRAINT ux_execution_resolution_requirement UNIQUE(snapshot_id, requirement_id),
    CONSTRAINT ck_execution_resolution_item_kind CHECK (kind IN ('File','Credential','ConnectionProfile','Skill')),
    CONSTRAINT ck_execution_resolution_item_outcome CHECK (outcome IN ('Resolved','RequiresStepUp','RequiresExternalApproval','HumanDecision','Denied')),
    CONSTRAINT ck_execution_resolution_item_authority CHECK (authority_revision >= 0),
    CONSTRAINT ck_execution_resolution_item_evidence CHECK (jsonb_typeof(evidence_refs_json) = 'array')
);

CREATE TABLE IF NOT EXISTS authority.agent_execution_resource_approvals (
    id uuid PRIMARY KEY,
    execution_id uuid NOT NULL REFERENCES agent_executions(id) ON DELETE CASCADE,
    requirement_id uuid NOT NULL,
    attempt integer NOT NULL,
    approved_by_user_id uuid NOT NULL,
    assertion_id uuid NOT NULL REFERENCES step_up_assertions(id) ON DELETE RESTRICT,
    authority_revision bigint NOT NULL,
    policy_revision text NOT NULL,
    status text NOT NULL,
    expires_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ck_execution_resource_approval_status CHECK (status IN ('Active','Consumed','Expired','Revoked')),
    CONSTRAINT ck_execution_resource_approval_attempt CHECK (attempt > 0),
    CONSTRAINT ck_execution_resource_approval_revision CHECK (authority_revision > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_execution_resource_active_approval
    ON authority.agent_execution_resource_approvals(execution_id, requirement_id, attempt) WHERE status = 'Active';

CREATE OR REPLACE FUNCTION audit.reject_immutable_mutation() RETURNS trigger AS $$
BEGIN
    RAISE EXCEPTION 'immutable authority/audit evidence cannot be changed';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_authority_outbox_immutable ON audit.authority_outbox_events;
CREATE TRIGGER trg_authority_outbox_immutable BEFORE UPDATE OR DELETE ON audit.authority_outbox_events
FOR EACH ROW EXECUTE FUNCTION audit.reject_immutable_mutation();
DROP TRIGGER IF EXISTS trg_background_events_immutable ON audit.background_events;
CREATE TRIGGER trg_background_events_immutable BEFORE UPDATE OR DELETE ON audit.background_events
FOR EACH ROW EXECUTE FUNCTION audit.reject_immutable_mutation();
DROP TRIGGER IF EXISTS trg_execution_resolution_snapshot_immutable ON authority.agent_execution_resolution_snapshots;
CREATE TRIGGER trg_execution_resolution_snapshot_immutable BEFORE UPDATE OR DELETE ON authority.agent_execution_resolution_snapshots
FOR EACH ROW EXECUTE FUNCTION audit.reject_immutable_mutation();
DROP TRIGGER IF EXISTS trg_execution_resolution_item_immutable ON authority.agent_execution_resolution_items;
CREATE TRIGGER trg_execution_resolution_item_immutable BEFORE UPDATE OR DELETE ON authority.agent_execution_resolution_items
FOR EACH ROW EXECUTE FUNCTION audit.reject_immutable_mutation();

CREATE OR REPLACE FUNCTION monitoring.prune_activity_projections(retain_after timestamptz) RETURNS bigint AS $$
DECLARE removed bigint;
BEGIN
    DELETE FROM monitoring.activity_projections
    WHERE security_critical = false AND occurred_at < retain_after;
    GET DIAGNOSTICS removed = ROW_COUNT;
    RETURN removed;
END;
$$ LANGUAGE plpgsql;

COMMENT ON TABLE audit.authority_outbox_events IS 'Immutable sanitized authority events committed with business mutations; never monitoring authority.';
COMMENT ON TABLE monitoring.activity_projections IS 'Asynchronous rebuildable monitoring projection; never used as business or security authority.';
COMMENT ON SCHEMA diagnostics IS 'Restricted short-retention raw diagnostics boundary. Raw secrets, file content, factor material, and provider locators are forbidden.';

-- B5/B6: durable, server-derived reliability evidence for scheduled
-- governance. Receipt history remains append-only; this table is the
-- idempotent read model used by scheduled_governance_run_get.
CREATE TABLE IF NOT EXISTS scheduled_governance_reliability_runs (
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    owner_user_id UUID NOT NULL,
    governance_run_id TEXT NOT NULL,
    receipt_id UUID NOT NULL,
    execution_mode TEXT NOT NULL,
    is_replay BOOLEAN NOT NULL DEFAULT FALSE,
    replay_projection_count INTEGER NOT NULL DEFAULT 0,
    replay_receipt_ids_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    observed_at_utc TIMESTAMPTZ NOT NULL,
    expected_at_utc TIMESTAMPTZ NULL,
    signed_drift_ticks BIGINT NULL,
    absolute_drift_ticks BIGINT NULL,
    drift_within_tolerance BOOLEAN NULL,
    counted_toward_gate BOOLEAN NOT NULL DEFAULT FALSE,
    qualifies BOOLEAN NOT NULL DEFAULT FALSE,
    is_ignored BOOLEAN NOT NULL DEFAULT FALSE,
    is_failed BOOLEAN NOT NULL DEFAULT FALSE,
    natural_origin_status TEXT NOT NULL DEFAULT 'Unattested',
    platform_signed_natural_origin_attested BOOLEAN NOT NULL DEFAULT FALSE,
    evidence_boundary TEXT NOT NULL,
    reasons_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    projection_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    intended_time_zone_id TEXT NOT NULL,
    scheduler_time_zone_id TEXT NOT NULL,
    cadence_ticks BIGINT NOT NULL,
    intended_local_run_times_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    scheduler_local_run_times_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    compensation_description TEXT NOT NULL,
    reset_reason TEXT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT uq_scheduled_governance_reliability_run
        UNIQUE (tenant_id, owner_user_id, governance_run_id),
    CONSTRAINT ck_scheduled_governance_reliability_replay_count
        CHECK (replay_projection_count >= 0)
);

CREATE INDEX IF NOT EXISTS ix_scheduled_governance_reliability_owner_observed
    ON scheduled_governance_reliability_runs(tenant_id, owner_user_id, observed_at_utc DESC);

COMMENT ON TABLE scheduled_governance_reliability_runs IS
    'Server-derived, idempotent reliability projection; no ChatGPT self-report or timing observation is treated as signed natural-origin attestation.';
COMMENT ON COLUMN scheduled_governance_reliability_runs.evidence_boundary IS
    'Explains which provenance facts ContextHub can observe and which platform-signed facts remain unavailable.';
COMMENT ON COLUMN scheduled_governance_reliability_runs.expected_at_utc IS
    'Expected intended schedule slot derived from the persisted schedule intent, not a host timezone mutation.';

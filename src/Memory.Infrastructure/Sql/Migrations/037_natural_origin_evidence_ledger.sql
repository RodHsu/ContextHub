-- Strict scheduled-governance provenance boundary.
-- Only verified, normalized platform/control-plane evidence belongs here.
-- Raw tokens, prompts, and caller-provided payloads are intentionally absent.
CREATE TABLE IF NOT EXISTS natural_origin_evidence_ledger (
    id UUID PRIMARY KEY,
    evidence_kind TEXT NOT NULL
        CHECK (evidence_kind IN ('PlatformAttestation', 'ControlPlaneAudit')),
    issuer VARCHAR(256) NOT NULL,
    environment VARCHAR(64) NOT NULL,
    key_id VARCHAR(128) NOT NULL,
    algorithm VARCHAR(32) NOT NULL,
    evidence_version VARCHAR(64) NOT NULL,
    jti_hash VARCHAR(64) NOT NULL,
    source_system VARCHAR(128) NOT NULL,
    source_event_id_hash VARCHAR(64) NOT NULL,
    source_sequence BIGINT NULL,
    tenant_id UUID NOT NULL,
    owner_user_id UUID NOT NULL,
    project_scope_hash VARCHAR(64) NOT NULL,
    trigger_kind VARCHAR(32) NOT NULL CHECK (trigger_kind = 'NaturalSchedule'),
    audience VARCHAR(64) NOT NULL CHECK (audience = '/mcp-automation'),
    actor_binding_hash VARCHAR(64) NOT NULL,
    task_binding_hash VARCHAR(64) NOT NULL,
    automation_binding_hash VARCHAR(64) NOT NULL,
    governance_run_id_hash VARCHAR(64) NOT NULL,
    slot_id_hash VARCHAR(64) NOT NULL,
    expected_at_utc TIMESTAMPTZ NOT NULL,
    issued_at_utc TIMESTAMPTZ NOT NULL,
    observed_at_utc TIMESTAMPTZ NOT NULL,
    expires_at_utc TIMESTAMPTZ NOT NULL,
    schedule_digest VARCHAR(64) NOT NULL,
    configuration_digest VARCHAR(64) NOT NULL,
    request_identity_hash VARCHAR(64) NOT NULL,
    dispatch_identity_hash VARCHAR(64) NOT NULL,
    receipt_id UUID NOT NULL,
    receipt_event_key_hash VARCHAR(64) NOT NULL,
    signature_digest VARCHAR(64) NOT NULL,
    tenant_binding_hash VARCHAR(64) NOT NULL,
    tool_contract_version VARCHAR(32) NOT NULL,
    schema_hash VARCHAR(64) NOT NULL,
    published_catalog_version VARCHAR(128) NOT NULL,
    runtime_identity_hash VARCHAR(64) NOT NULL,
    verification_status VARCHAR(32) NOT NULL CHECK (verification_status = 'Verified'),
    created_at_utc TIMESTAMPTZ NOT NULL,
    CONSTRAINT uq_natural_origin_evidence_issuer_jti UNIQUE (issuer, jti_hash),
    CONSTRAINT uq_natural_origin_evidence_source_event UNIQUE (source_system, source_event_id_hash),
    CONSTRAINT uq_natural_origin_evidence_run_slot_kind
        UNIQUE (tenant_id, owner_user_id, governance_run_id_hash, slot_id_hash, evidence_kind),
    CONSTRAINT ck_natural_origin_evidence_non_empty_uuid CHECK (
        tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
        owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
        receipt_id <> '00000000-0000-0000-0000-000000000000'::uuid),
    CONSTRAINT ck_natural_origin_evidence_identifier_shape CHECK (
        issuer ~ '^[!-~]+$' AND
        environment ~ '^[!-~]+$' AND
        key_id ~ '^[!-~]+$' AND
        algorithm ~ '^[!-~]+$' AND
        evidence_version ~ '^[!-~]+$' AND
        source_system ~ '^[!-~]+$' AND
        tool_contract_version ~ '^[!-~]+$' AND
        published_catalog_version ~ '^[!-~]+$'),
    CONSTRAINT ck_natural_origin_evidence_validity
        CHECK (
            expires_at_utc > issued_at_utc AND
            expires_at_utc - issued_at_utc <= INTERVAL '15 minutes' AND
            observed_at_utc >= issued_at_utc AND
            observed_at_utc <= expires_at_utc),
    CONSTRAINT ck_natural_origin_evidence_source_sequence CHECK (
        (evidence_kind = 'ControlPlaneAudit' AND source_sequence IS NOT NULL AND source_sequence >= 0) OR
        (evidence_kind = 'PlatformAttestation' AND source_sequence IS NULL)),
    CONSTRAINT ck_natural_origin_evidence_digest_shape CHECK (
        actor_binding_hash ~ '^[0-9a-f]{64}$' AND
        project_scope_hash ~ '^[0-9a-f]{64}$' AND
        jti_hash ~ '^[0-9a-f]{64}$' AND
        source_event_id_hash ~ '^[0-9a-f]{64}$' AND
        task_binding_hash ~ '^[0-9a-f]{64}$' AND
        automation_binding_hash ~ '^[0-9a-f]{64}$' AND
        governance_run_id_hash ~ '^[0-9a-f]{64}$' AND
        slot_id_hash ~ '^[0-9a-f]{64}$' AND
        schedule_digest ~ '^[0-9a-f]{64}$' AND
        configuration_digest ~ '^[0-9a-f]{64}$' AND
        request_identity_hash ~ '^[0-9a-f]{64}$' AND
        dispatch_identity_hash ~ '^[0-9a-f]{64}$' AND
        receipt_event_key_hash ~ '^[0-9a-f]{64}$' AND
        signature_digest ~ '^[0-9a-f]{64}$' AND
        tenant_binding_hash ~ '^[0-9a-f]{64}$' AND
        schema_hash ~ '^[0-9a-f]{64}$' AND
        runtime_identity_hash ~ '^[0-9a-f]{64}$'
    )
);

CREATE INDEX IF NOT EXISTS ix_natural_origin_evidence_run_expected
    ON natural_origin_evidence_ledger(tenant_id, owner_user_id, governance_run_id_hash, expected_at_utc);

CREATE OR REPLACE FUNCTION reject_natural_origin_evidence_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'natural_origin_evidence_ledger is append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS natural_origin_evidence_ledger_immutable ON natural_origin_evidence_ledger;
CREATE TRIGGER natural_origin_evidence_ledger_immutable
    BEFORE UPDATE OR DELETE ON natural_origin_evidence_ledger
    FOR EACH ROW EXECUTE FUNCTION reject_natural_origin_evidence_mutation();

COMMENT ON TABLE natural_origin_evidence_ledger IS
    'Append-only verified natural-origin evidence for scheduled governance; no raw token, prompt, or PII payload is stored.';
COMMENT ON COLUMN natural_origin_evidence_ledger.jti_hash IS
    'Durable issuer-scoped replay identity; a conflicting reuse is rejected.';
COMMENT ON COLUMN natural_origin_evidence_ledger.source_event_id_hash IS
    'Durable source plus event identity for platform attestation or control-plane audit delivery.';

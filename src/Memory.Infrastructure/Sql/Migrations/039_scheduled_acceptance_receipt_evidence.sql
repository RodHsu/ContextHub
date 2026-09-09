-- Preserve the exact server-computed durable-memory count invariant on the
-- immutable scheduled decision event. Existing receipts remain legacy/unknown;
-- only version 1 events may be used as reliability evidence.
ALTER TABLE governance_run_receipts
    ADD COLUMN IF NOT EXISTS acceptance_evidence_version VARCHAR(16) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS authorized_durable_memory_count INTEGER NULL,
    ADD COLUMN IF NOT EXISTS covered_durable_memory_count INTEGER NULL,
    ADD COLUMN IF NOT EXISTS scanned_durable_memory_count INTEGER NULL,
    ADD COLUMN IF NOT EXISTS total_durable_memory_count INTEGER NULL,
    ADD COLUMN IF NOT EXISTS shared_scope_occurrences INTEGER NULL,
    ADD COLUMN IF NOT EXISTS user_scope_occurrences INTEGER NULL,
    ADD COLUMN IF NOT EXISTS user_scope_handled_separately BOOLEAN NULL,
    ADD COLUMN IF NOT EXISTS count_invariant_satisfied BOOLEAN NULL;

ALTER TABLE governance_run_receipts
    ADD COLUMN IF NOT EXISTS runtime_evidence_version VARCHAR(16) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_service_name VARCHAR(128) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_build_version VARCHAR(64) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_build_timestamp_utc TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS runtime_derived_identity VARCHAR(256) NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS runtime_identity_hash VARCHAR(64) NOT NULL DEFAULT '';

ALTER TABLE scheduled_governance_reliability_runs
    ADD COLUMN IF NOT EXISTS receipt_event_sequence BIGINT NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_scheduled_reliability_receipt_event_sequence'
          AND conrelid = 'scheduled_governance_reliability_runs'::regclass
    ) THEN
        ALTER TABLE scheduled_governance_reliability_runs
            ADD CONSTRAINT ck_scheduled_reliability_receipt_event_sequence CHECK (
                receipt_event_sequence IS NULL OR receipt_event_sequence > 0
            );
    END IF;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_governance_receipt_runtime_evidence'
          AND conrelid = 'governance_run_receipts'::regclass
    ) THEN
        ALTER TABLE governance_run_receipts
            ADD CONSTRAINT ck_governance_receipt_runtime_evidence CHECK (
                (
                    runtime_evidence_version = '' AND
                    runtime_service_name = '' AND
                    runtime_build_version = '' AND
                    runtime_build_timestamp_utc IS NULL AND
                    runtime_derived_identity = '' AND
                    runtime_identity_hash = ''
                ) OR
                (
                    runtime_evidence_version = '1' AND
                    runtime_service_name <> '' AND
                    runtime_build_version <> '' AND
                    runtime_build_timestamp_utc IS NOT NULL AND
                    runtime_derived_identity <> '' AND
                    runtime_identity_hash ~ '^[0-9a-f]{64}$'
                )
            );
    END IF;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'ck_governance_receipt_acceptance_evidence'
          AND conrelid = 'governance_run_receipts'::regclass
    ) THEN
        ALTER TABLE governance_run_receipts
            ADD CONSTRAINT ck_governance_receipt_acceptance_evidence CHECK (
                (
                    acceptance_evidence_version = '' AND
                    authorized_durable_memory_count IS NULL AND
                    covered_durable_memory_count IS NULL AND
                    scanned_durable_memory_count IS NULL AND
                    total_durable_memory_count IS NULL AND
                    shared_scope_occurrences IS NULL AND
                    user_scope_occurrences IS NULL AND
                    user_scope_handled_separately IS NULL AND
                    count_invariant_satisfied IS NULL
                ) OR
                (
                    acceptance_evidence_version = '1' AND
                    execution_mode = 'Scheduled' AND
                    event_type IN ('ReviewCompleted', 'ScheduledDecisionProjected') AND
                    authorized_durable_memory_count IS NOT NULL AND
                    covered_durable_memory_count IS NOT NULL AND
                    scanned_durable_memory_count IS NOT NULL AND
                    total_durable_memory_count IS NOT NULL AND
                    shared_scope_occurrences IS NOT NULL AND
                    user_scope_occurrences IS NOT NULL AND
                    user_scope_handled_separately IS NOT NULL AND
                    count_invariant_satisfied IS NOT NULL AND
                    authorized_durable_memory_count >= 0 AND
                    covered_durable_memory_count >= 0 AND
                    scanned_durable_memory_count >= 0 AND
                    total_durable_memory_count >= 0 AND
                    shared_scope_occurrences >= 0 AND
                    user_scope_occurrences >= 0 AND
                    count_invariant_satisfied = (
                        authorized_durable_memory_count = covered_durable_memory_count AND
                        covered_durable_memory_count = scanned_durable_memory_count AND
                        scanned_durable_memory_count = total_durable_memory_count AND
                        shared_scope_occurrences = 1 AND
                        user_scope_occurrences = 0 AND
                        user_scope_handled_separately
                    )
                )
            );
    END IF;
END;
$$;

COMMENT ON COLUMN governance_run_receipts.acceptance_evidence_version IS
    'Versioned server-owned acceptance evidence. Empty means legacy/unknown and must not qualify.';
COMMENT ON COLUMN governance_run_receipts.count_invariant_satisfied IS
    'Exact recomputation from the persisted count tuple; never inferred from coverage_complete alone.';
COMMENT ON COLUMN governance_run_receipts.runtime_identity_hash IS
    'Hash of the immutable capture-time scheduled gateway runtime identity; empty means legacy/unknown.';

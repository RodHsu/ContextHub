-- Close the remaining database-level integrity gaps for the append-only
-- governance receipt and natural-origin evidence ledgers. Application checks
-- remain fail-closed, but the database must also reject orphan/cross-owner
-- evidence and bulk removal through TRUNCATE.

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM natural_origin_evidence_ledger evidence
        LEFT JOIN governance_run_receipts receipt
          ON receipt.id = evidence.receipt_id
         AND receipt.tenant_id = evidence.tenant_id
         AND receipt.owner_user_id = evidence.owner_user_id
        WHERE receipt.id IS NULL
    ) THEN
        RAISE EXCEPTION
            'natural_origin_evidence_ledger contains orphan or cross-owner receipt bindings';
    END IF;

END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'uq_governance_run_receipts_owner_receipt'
          AND conrelid = 'governance_run_receipts'::regclass
    ) THEN
        ALTER TABLE governance_run_receipts
            ADD CONSTRAINT uq_governance_run_receipts_owner_receipt
            UNIQUE (id, tenant_id, owner_user_id);
    END IF;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conname = 'fk_natural_origin_evidence_owner_receipt'
          AND conrelid = 'natural_origin_evidence_ledger'::regclass
    ) THEN
        ALTER TABLE natural_origin_evidence_ledger
            ADD CONSTRAINT fk_natural_origin_evidence_owner_receipt
            FOREIGN KEY (receipt_id, tenant_id, owner_user_id)
            REFERENCES governance_run_receipts (id, tenant_id, owner_user_id)
            ON DELETE RESTRICT;
    END IF;
END;
$$;

DROP TRIGGER IF EXISTS governance_run_receipts_reject_truncate ON governance_run_receipts;
CREATE TRIGGER governance_run_receipts_reject_truncate
    BEFORE TRUNCATE ON governance_run_receipts
    FOR EACH STATEMENT EXECUTE FUNCTION reject_governance_run_receipt_mutation();

DROP TRIGGER IF EXISTS natural_origin_evidence_reject_truncate ON natural_origin_evidence_ledger;
CREATE TRIGGER natural_origin_evidence_reject_truncate
    BEFORE TRUNCATE ON natural_origin_evidence_ledger
    FOR EACH STATEMENT EXECUTE FUNCTION reject_natural_origin_evidence_mutation();

COMMENT ON CONSTRAINT fk_natural_origin_evidence_owner_receipt
    ON natural_origin_evidence_ledger IS
    'Evidence must bind an existing immutable receipt owned by the same tenant and user.';

-- One-time reconciliation for the exact legacy memory-score population that
-- predates the canonical [0,1] contract.  No source evidence proves a safe
-- replacement value, so every row is moved to immutable quarantine as
-- RequiresHumanDecision.  This is not a governance run and is not a hard-delete
-- decision: the full source row and every directly affected aggregate are
-- copied and read back before the source row is removed in this transaction.

CREATE TABLE IF NOT EXISTS memory_score_reconciliation_runs
(
    reconciliation_key TEXT PRIMARY KEY,
    implementation_plan_id UUID NOT NULL
        CHECK (implementation_plan_id = '3bbcc307-50b9-4f18-ad01-439f166feed3'),
    authority_artifact_id UUID NOT NULL
        CHECK (authority_artifact_id = 'edf09285-cc6b-482b-8bac-f4568b62728e'),
    authority_fact_id UUID NOT NULL
        CHECK (authority_fact_id = 'e311bfc7-08f8-42e9-84ec-1a97f85258dc'),
    master_work_item_id UUID NOT NULL
        CHECK (master_work_item_id = 'e1eac3fb-b506-418c-9408-61ae68b522d8'),
    successor_target_id UUID NOT NULL
        CHECK (successor_target_id = '789bd766-5623-4137-a566-e8531b6b08af'),
    source_commit CHAR(40) NOT NULL
        CHECK (source_commit = '773878ce0aacdb55d6fff3c7e9a74977f5df2675'),
    manifest_hash CHAR(32) NOT NULL CHECK (manifest_hash ~ '^[0-9a-f]{32}$'),
    outcome TEXT NOT NULL CHECK (outcome IN ('ArchivedRequiresHumanDecision', 'CleanDatabaseNoOp')),
    expected_source_count INTEGER NOT NULL CHECK (expected_source_count = 33),
    archived_source_count INTEGER NOT NULL CHECK (archived_source_count IN (0, 33)),
    replacement_count INTEGER NOT NULL CHECK (replacement_count = 0),
    aggregate_counts JSONB NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp()
);

CREATE TABLE IF NOT EXISTS memory_score_reconciliation_quarantine
(
    reconciliation_key TEXT NOT NULL
        REFERENCES memory_score_reconciliation_runs(reconciliation_key) ON DELETE RESTRICT,
    source_memory_id UUID NOT NULL,
    source_external_key TEXT NOT NULL,
    stored_importance NUMERIC NOT NULL,
    stored_confidence NUMERIC NOT NULL CHECK (stored_confidence BETWEEN 0 AND 1),
    evidence_class TEXT NOT NULL CHECK (evidence_class = 'RequiresHumanDecision'),
    replacement_memory_id UUID NULL,
    source_payload JSONB NOT NULL,
    related_payload JSONB NOT NULL,
    payload_hash CHAR(32) NOT NULL CHECK (payload_hash ~ '^[0-9a-f]{32}$'),
    archived_at_utc TIMESTAMPTZ NOT NULL DEFAULT clock_timestamp(),
    PRIMARY KEY (reconciliation_key, source_memory_id),
    CONSTRAINT ck_memory_score_reconciliation_no_replacement
        CHECK (replacement_memory_id IS NULL)
);

CREATE OR REPLACE FUNCTION reject_memory_score_reconciliation_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'memory score reconciliation evidence is append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS memory_score_reconciliation_runs_immutable
    ON memory_score_reconciliation_runs;
CREATE TRIGGER memory_score_reconciliation_runs_immutable
    BEFORE UPDATE OR DELETE ON memory_score_reconciliation_runs
    FOR EACH ROW EXECUTE FUNCTION reject_memory_score_reconciliation_mutation();

DROP TRIGGER IF EXISTS memory_score_reconciliation_runs_reject_truncate
    ON memory_score_reconciliation_runs;
CREATE TRIGGER memory_score_reconciliation_runs_reject_truncate
    BEFORE TRUNCATE ON memory_score_reconciliation_runs
    FOR EACH STATEMENT EXECUTE FUNCTION reject_memory_score_reconciliation_mutation();

DROP TRIGGER IF EXISTS memory_score_reconciliation_quarantine_immutable
    ON memory_score_reconciliation_quarantine;
CREATE TRIGGER memory_score_reconciliation_quarantine_immutable
    BEFORE UPDATE OR DELETE ON memory_score_reconciliation_quarantine
    FOR EACH ROW EXECUTE FUNCTION reject_memory_score_reconciliation_mutation();

DROP TRIGGER IF EXISTS memory_score_reconciliation_quarantine_reject_truncate
    ON memory_score_reconciliation_quarantine;
CREATE TRIGGER memory_score_reconciliation_quarantine_reject_truncate
    BEFORE TRUNCATE ON memory_score_reconciliation_quarantine
    FOR EACH STATEMENT EXECUTE FUNCTION reject_memory_score_reconciliation_mutation();

DROP TABLE IF EXISTS pg_temp.expected_memory_score_reconciliation;
CREATE TEMP TABLE expected_memory_score_reconciliation
(
    source_memory_id UUID PRIMARY KEY,
    source_external_key TEXT NOT NULL UNIQUE,
    stored_importance NUMERIC NOT NULL
) ON COMMIT DROP;

INSERT INTO expected_memory_score_reconciliation
    (source_memory_id, source_external_key, stored_importance)
VALUES
    ('fc4ace51-e36d-4f6c-bb9d-79553913d918', '2026-05-14-testreports-root-folder', 4),
    ('1449fcef-1a78-462a-a033-d665bf68effd', 'passenger-v1:formal-quote:2026-07-21', 8),
    ('28b9ac37-9d6d-4ec0-9567-15fff251fdb0', 'passenger-auth-verification-code-delivery-failclosed-v1', 7),
    ('10d08e95-4cd1-4502-962a-7393108da459', 'passenger-session-restore-authoritative-expiry-v1', 8),
    ('2b60ad7c-db5a-41ae-91ac-24ce47736a72', 'passenger-authoritative-readback-stale-data-failclosed-v1', 8),
    ('79073435-8a83-4adf-ab67-391f18166220', 'passenger-device-smoke-privacy-defaults-v1', 8),
    ('bc9ae07d-ffd8-4fa1-90be-e87b3875d23c', 'passenger-safety-event-immutable-retry-payload-v1', 8),
    ('49b61f0f-9a55-4e35-9319-6cc6510f560c', 'passenger-notifications-stale-inbox-v1', 7),
    ('62f5348b-1890-44a8-854e-f0e043c3b004', 'passenger-nondebug-readiness-fingerprint-v1', 8),
    ('5466affb-20d3-42aa-9069-6b44a2fea6a4', 'passenger-ci-manifest-provenance-contract-v1', 9),
    ('b2cecae0-abf2-4622-a8a1-a6c6b8ff0801', 'wjcy-cloud-nextcloud-desktop-423-lock-20260726', 9),
    ('08253e63-e260-4013-9ce8-9a8a4fbb4fe8', 'driverapp-controlled-test-account-roles', 9),
    ('8bf96557-7665-4a05-a0eb-05bf3042e4c0', 'driverapp:statement-detail-settlement-readback', 8),
    ('3a0f40cd-fff8-459a-bc0e-10652aa74e3f', 'driverapp.dark-theme.shared-foundation.v1', 8),
    ('f5c26d02-beb1-4e06-b4dd-78cd4f70aa0e', 'driverapp.dark-theme.auth-entry.v1', 7),
    ('9668de66-8e70-4af4-9d4e-a64cdc399aa2', 'driverapp.dark-theme.activation.v1', 7),
    ('7cb36be1-d745-4391-930f-02bc54b1e925', 'driverapp.dark-theme.global-feedback.v1', 8),
    ('93072a37-218e-4f34-9816-13ac3e3d0bdc', 'airmeet-document-open-discussion-classification-2026-07-30', 9),
    ('250a6a42-2325-4dfc-94fd-ae186faafeb5', 'orderadmin.refunds.orderno-filter', 7),
    ('1535b63b-0d6b-4232-9df6-dc973d8a31f7', 'full-document-contract-audit-f148e09-20260730', 9),
    ('ece98e90-156a-4e61-b56f-1f2cce1ece0f', 'api-document-history-generation-v1', 9),
    ('badb94f4-2abf-44c9-a8ab-4b3beb189660', 'verification-request-active-uniqueness-a183', 8),
    ('e12fd4cc-f7cb-464c-81e4-05c48d7489fa', 'wjcy-developer02-controlled-ssh-resolver-v1', 9),
    ('4f41a265-7cdd-4268-acf7-75da59497e8f', 'developer02-controlled-ssh-resolver-v1', 9),
    ('14af6e62-e335-41b7-b966-547744f0306c', 'nextcloud-nt88316-trashbin-composition-2026-08-11', 9),
    ('e170ba72-fd77-4c29-ad69-308b5157b868', 'wjcy-cloud:talk-backend:coturn-stdout-log-rotation:2026-08-11', 8),
    ('b623e026-2b50-4742-9b54-718aa9e226e7', 'backend-document-sync-current', 9),
    ('1d7a33ed-a4b0-4a3f-9e46-f0e27eb30f84', 'document-sync-readback-current', 8),
    ('92843ff1-f34b-463a-99d0-50e73bf4ed68', 'sandbox-controlled-passenger-reset-recovery', 9),
    ('33d1bb4a-21ae-43b6-bf83-a01f8c79c18b', 'accounting-period-close-production-policy', 9),
    ('159ee739-42a4-4a3a-8c35-eb0f0495adeb', 'formal-finance-four-level-local-validation-2026-08-26', 9),
    ('01e87223-0fb6-4e4f-b91a-cd7143627ba2', 'formal-finance-recipient-batch-order-statement-decision-20260826', 9),
    ('3a72841e-e640-4cd5-951b-105a55bf5f4d', 'vital-suscon-oracle-boundary-v1', 9);

-- Freeze both score-bearing source tables while exact-set evidence is checked
-- and moved. This prevents a legacy writer from racing the immutable
-- copy/read-back gate during a rolling deployment.
LOCK TABLE memory_items IN ACCESS EXCLUSIVE MODE;
LOCK TABLE conversation_insights IN ACCESS EXCLUSIVE MODE;

DO $$
DECLARE
    reconciliation CONSTANT TEXT := 'memory-score-contract-2026-09-13-v1';
    expected_manifest_hash TEXT;
    existing_outcome TEXT;
BEGIN
    SELECT md5(string_agg(
        source_memory_id::text || '|' || source_external_key || '|' || stored_importance::text,
        E'\n' ORDER BY source_memory_id))
    INTO expected_manifest_hash
    FROM expected_memory_score_reconciliation;

    IF (SELECT COUNT(*) FROM expected_memory_score_reconciliation) <> 33 THEN
        RAISE EXCEPTION 'memory score reconciliation manifest must contain exactly 33 rows';
    END IF;

    SELECT outcome
    INTO existing_outcome
    FROM memory_score_reconciliation_runs
    WHERE reconciliation_key = reconciliation;

    IF existing_outcome IS NOT NULL THEN
        IF NOT EXISTS (
            SELECT 1
            FROM memory_score_reconciliation_runs
            WHERE reconciliation_key = reconciliation
              AND implementation_plan_id = '3bbcc307-50b9-4f18-ad01-439f166feed3'
              AND authority_artifact_id = 'edf09285-cc6b-482b-8bac-f4568b62728e'
              AND authority_fact_id = 'e311bfc7-08f8-42e9-84ec-1a97f85258dc'
              AND master_work_item_id = 'e1eac3fb-b506-418c-9408-61ae68b522d8'
              AND successor_target_id = '789bd766-5623-4137-a566-e8531b6b08af'
              AND source_commit = '773878ce0aacdb55d6fff3c7e9a74977f5df2675'
              AND manifest_hash = expected_manifest_hash
              AND expected_source_count = 33
              AND replacement_count = 0) THEN
            RAISE EXCEPTION 'memory score reconciliation replay identity mismatch';
        END IF;

        IF existing_outcome = 'ArchivedRequiresHumanDecision' THEN
            IF (SELECT COUNT(*) FROM memory_score_reconciliation_quarantine
                WHERE reconciliation_key = reconciliation) <> 33
               OR EXISTS (
                    SELECT 1
                    FROM memory_score_reconciliation_quarantine q
                    JOIN expected_memory_score_reconciliation e
                      ON e.source_memory_id = q.source_memory_id
                    WHERE q.reconciliation_key = reconciliation
                      AND (q.source_external_key IS DISTINCT FROM e.source_external_key
                           OR q.stored_importance IS DISTINCT FROM e.stored_importance
                           OR q.evidence_class <> 'RequiresHumanDecision'
                           OR q.replacement_memory_id IS NOT NULL
                           OR q.payload_hash <> md5(q.source_payload::text || q.related_payload::text)))
               OR EXISTS (
                    SELECT 1
                    FROM expected_memory_score_reconciliation e
                    LEFT JOIN memory_score_reconciliation_quarantine q
                      ON q.reconciliation_key = reconciliation
                     AND q.source_memory_id = e.source_memory_id
                    WHERE q.source_memory_id IS NULL) THEN
                RAISE EXCEPTION 'memory score reconciliation replay evidence is incomplete or altered';
            END IF;
        ELSIF existing_outcome = 'CleanDatabaseNoOp' THEN
            IF EXISTS (SELECT 1 FROM memory_score_reconciliation_quarantine
                       WHERE reconciliation_key = reconciliation) THEN
                RAISE EXCEPTION 'clean-database reconciliation replay unexpectedly has quarantine rows';
            END IF;
        ELSE
            RAISE EXCEPTION 'memory score reconciliation replay outcome is invalid';
        END IF;

        IF EXISTS (SELECT 1 FROM memory_items WHERE importance < 0 OR importance > 1
                   OR confidence < 0 OR confidence > 1)
           OR EXISTS (SELECT 1 FROM conversation_insights WHERE importance < 0 OR importance > 1
                      OR confidence < 0 OR confidence > 1) THEN
            RAISE EXCEPTION 'memory score reconciliation replay found malformed scores';
        END IF;

        RETURN;
    END IF;

    -- A clean database that contains none of the authority-bound source IDs
    -- legitimately has nothing to reconcile. If even one source ID exists,
    -- it must still satisfy the exact production manifest below.
    IF NOT EXISTS (SELECT 1 FROM memory_items WHERE importance < 0 OR importance > 1
                   OR confidence < 0 OR confidence > 1)
       AND NOT EXISTS (
            SELECT 1
            FROM memory_items
            WHERE id IN (SELECT source_memory_id FROM expected_memory_score_reconciliation)) THEN
        INSERT INTO memory_score_reconciliation_runs
            (reconciliation_key, implementation_plan_id, authority_artifact_id,
             authority_fact_id, master_work_item_id, successor_target_id,
             source_commit, manifest_hash, outcome, expected_source_count,
             archived_source_count, replacement_count, aggregate_counts)
        VALUES
            (reconciliation,
             '3bbcc307-50b9-4f18-ad01-439f166feed3',
             'edf09285-cc6b-482b-8bac-f4568b62728e',
             'e311bfc7-08f8-42e9-84ec-1a97f85258dc',
             'e1eac3fb-b506-418c-9408-61ae68b522d8',
             '789bd766-5623-4137-a566-e8531b6b08af',
             '773878ce0aacdb55d6fff3c7e9a74977f5df2675',
             expected_manifest_hash, 'CleanDatabaseNoOp', 33, 0, 0,
             '{"memoryItems":0,"replacements":0}'::jsonb);
        RETURN;
    END IF;

    IF EXISTS (SELECT 1 FROM conversation_insights WHERE importance < 0 OR importance > 1
               OR confidence < 0 OR confidence > 1) THEN
        RAISE EXCEPTION 'memory score reconciliation refuses malformed conversation insight scores';
    END IF;

    -- Exact set equality: catches a 34th row, a missing row, a changed key,
    -- a changed score, or an invalid confidence value.
    IF EXISTS (
        (SELECT id, external_key, importance
         FROM memory_items
         WHERE importance < 0 OR importance > 1 OR confidence < 0 OR confidence > 1)
        EXCEPT
        (SELECT source_memory_id, source_external_key, stored_importance
         FROM expected_memory_score_reconciliation))
       OR EXISTS (
        (SELECT source_memory_id, source_external_key, stored_importance
         FROM expected_memory_score_reconciliation)
        EXCEPT
        (SELECT id, external_key, importance
         FROM memory_items
         WHERE (importance < 0 OR importance > 1)
           AND confidence BETWEEN 0 AND 1)) THEN
        RAISE EXCEPTION 'malformed memory score set differs from the exact 33-row reconciliation manifest';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM memory_items item
        JOIN expected_memory_score_reconciliation e ON e.source_memory_id = item.id
        WHERE item.supersedes_id IS NOT NULL
           OR item.superseded_by_id IS NOT NULL
           OR item.successor_evidence_id IS NOT NULL)
       OR EXISTS (
        SELECT 1
        FROM memory_items item
        WHERE item.supersedes_id IN (SELECT source_memory_id FROM expected_memory_score_reconciliation)
           OR item.superseded_by_id IN (SELECT source_memory_id FROM expected_memory_score_reconciliation)
           OR item.successor_evidence_id IN (SELECT source_memory_id FROM expected_memory_score_reconciliation)) THEN
        RAISE EXCEPTION 'memory score reconciliation refuses rows with authority/replacement linkage';
    END IF;

    INSERT INTO memory_score_reconciliation_runs
        (reconciliation_key, implementation_plan_id, authority_artifact_id,
         authority_fact_id, master_work_item_id, successor_target_id,
         source_commit, manifest_hash, outcome, expected_source_count,
         archived_source_count, replacement_count, aggregate_counts)
    SELECT
        reconciliation,
        '3bbcc307-50b9-4f18-ad01-439f166feed3',
        'edf09285-cc6b-482b-8bac-f4568b62728e',
        'e311bfc7-08f8-42e9-84ec-1a97f85258dc',
        'e1eac3fb-b506-418c-9408-61ae68b522d8',
        '789bd766-5623-4137-a566-e8531b6b08af',
        '773878ce0aacdb55d6fff3c7e9a74977f5df2675',
        expected_manifest_hash,
        'ArchivedRequiresHumanDecision',
        33,
        33,
        0,
        jsonb_build_object(
            'memoryItems', 33,
            'revisions', (SELECT COUNT(*) FROM memory_item_revisions WHERE memory_item_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation)),
            'chunks', (SELECT COUNT(*) FROM memory_item_chunks WHERE memory_item_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation)),
            'vectors', (SELECT COUNT(*) FROM memory_chunk_vectors v JOIN memory_item_chunks c ON c.id = v.chunk_id
                WHERE c.memory_item_id IN (SELECT source_memory_id FROM expected_memory_score_reconciliation)),
            'links', (SELECT COUNT(*) FROM memory_links WHERE from_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation) OR to_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation)),
            'governanceFindings', (SELECT COUNT(*) FROM governance_findings WHERE primary_memory_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation) OR secondary_memory_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation)),
            'retrievalHits', (SELECT COUNT(*) FROM retrieval_hits WHERE memory_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation)),
            'retentionStates', (SELECT COUNT(*) FROM memory_retention_states WHERE resource_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation)),
            'resourceTombstones', (SELECT COUNT(*) FROM resource_tombstones WHERE resource_id IN
                (SELECT source_memory_id FROM expected_memory_score_reconciliation)),
            'replacements', 0);

    INSERT INTO memory_score_reconciliation_quarantine
        (reconciliation_key, source_memory_id, source_external_key,
         stored_importance, stored_confidence, evidence_class,
         replacement_memory_id, source_payload, related_payload, payload_hash)
    SELECT
        reconciliation,
        item.id,
        item.external_key,
        item.importance,
        item.confidence,
        'RequiresHumanDecision',
        NULL,
        to_jsonb(item),
        related.payload,
        md5(to_jsonb(item)::text || related.payload::text)
    FROM memory_items item
    JOIN expected_memory_score_reconciliation e ON e.source_memory_id = item.id
    CROSS JOIN LATERAL (
        SELECT jsonb_build_object(
            'revisions', COALESCE((SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id)
                FROM memory_item_revisions r WHERE r.memory_item_id = item.id), '[]'::jsonb),
            'chunks', COALESCE((SELECT jsonb_agg(to_jsonb(c) ORDER BY c.id)
                FROM memory_item_chunks c WHERE c.memory_item_id = item.id), '[]'::jsonb),
            'vectors', COALESCE((SELECT jsonb_agg(to_jsonb(v) ORDER BY v.id)
                FROM memory_chunk_vectors v JOIN memory_item_chunks c ON c.id = v.chunk_id
                WHERE c.memory_item_id = item.id), '[]'::jsonb),
            'links', COALESCE((SELECT jsonb_agg(to_jsonb(l) ORDER BY l.id)
                FROM memory_links l WHERE l.from_id = item.id OR l.to_id = item.id), '[]'::jsonb),
            'governanceFindings', COALESCE((SELECT jsonb_agg(to_jsonb(f) ORDER BY f.id)
                FROM governance_findings f WHERE f.primary_memory_id = item.id OR f.secondary_memory_id = item.id), '[]'::jsonb),
            'retrievalHits', COALESCE((SELECT jsonb_agg(to_jsonb(h) ORDER BY h.id)
                FROM retrieval_hits h WHERE h.memory_id = item.id), '[]'::jsonb),
            'retentionStates', COALESCE((SELECT jsonb_agg(to_jsonb(s) ORDER BY s.resource_id)
                FROM memory_retention_states s WHERE s.resource_id = item.id), '[]'::jsonb),
            'resourceTombstones', COALESCE((SELECT jsonb_agg(to_jsonb(t) ORDER BY t.id)
                FROM resource_tombstones t WHERE t.resource_id = item.id), '[]'::jsonb)) AS payload
    ) related;

    -- Complete immutable-copy/read-back gate before any source removal.
    IF (SELECT COUNT(*) FROM memory_score_reconciliation_quarantine
        WHERE reconciliation_key = reconciliation) <> 33
       OR EXISTS (
            SELECT 1
            FROM expected_memory_score_reconciliation e
            LEFT JOIN memory_score_reconciliation_quarantine q
              ON q.reconciliation_key = reconciliation
             AND q.source_memory_id = e.source_memory_id
            WHERE q.source_memory_id IS NULL
               OR q.source_external_key IS DISTINCT FROM e.source_external_key
               OR q.stored_importance IS DISTINCT FROM e.stored_importance
               OR q.evidence_class <> 'RequiresHumanDecision'
               OR q.replacement_memory_id IS NOT NULL
               OR q.source_payload->>'id' IS DISTINCT FROM e.source_memory_id::text
               OR q.source_payload->>'external_key' IS DISTINCT FROM e.source_external_key
               OR (q.source_payload->>'importance')::numeric IS DISTINCT FROM e.stored_importance
               OR q.payload_hash <> md5(q.source_payload::text || q.related_payload::text)) THEN
        RAISE EXCEPTION 'memory score reconciliation immutable copy/read-back gate failed';
    END IF;

    DELETE FROM memory_retention_states
    WHERE resource_id IN (SELECT source_memory_id FROM expected_memory_score_reconciliation);

    DELETE FROM memory_items
    WHERE id IN (SELECT source_memory_id FROM expected_memory_score_reconciliation);

    IF EXISTS (SELECT 1 FROM memory_items WHERE id IN
               (SELECT source_memory_id FROM expected_memory_score_reconciliation))
       OR EXISTS (SELECT 1 FROM memory_item_revisions WHERE memory_item_id IN
                  (SELECT source_memory_id FROM expected_memory_score_reconciliation))
       OR EXISTS (SELECT 1 FROM memory_item_chunks WHERE memory_item_id IN
                  (SELECT source_memory_id FROM expected_memory_score_reconciliation))
       OR EXISTS (SELECT 1 FROM memory_links WHERE from_id IN
                  (SELECT source_memory_id FROM expected_memory_score_reconciliation) OR to_id IN
                  (SELECT source_memory_id FROM expected_memory_score_reconciliation))
       OR EXISTS (SELECT 1 FROM memory_retention_states WHERE resource_id IN
                  (SELECT source_memory_id FROM expected_memory_score_reconciliation))
       OR EXISTS (SELECT 1 FROM governance_findings WHERE primary_memory_id IN
                  (SELECT source_memory_id FROM expected_memory_score_reconciliation) OR secondary_memory_id IN
                  (SELECT source_memory_id FROM expected_memory_score_reconciliation))
       OR EXISTS (SELECT 1 FROM retrieval_hits WHERE memory_id IN
                  (SELECT source_memory_id FROM expected_memory_score_reconciliation))
       OR EXISTS (SELECT 1 FROM memory_items WHERE importance < 0 OR importance > 1
                  OR confidence < 0 OR confidence > 1) THEN
        RAISE EXCEPTION 'memory score reconciliation source-removal read-back failed';
    END IF;
END $$;

COMMENT ON TABLE memory_score_reconciliation_runs IS
    'Append-only one-time migration evidence; unrelated to governanceRunId and Scheduled Governance reliability.';
COMMENT ON TABLE memory_score_reconciliation_quarantine IS
    'Immutable full-fidelity legacy score evidence awaiting explicit human reconciliation; no replacement score is inferred.';

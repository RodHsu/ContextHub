\set ON_ERROR_STOP on

-- Run with psql in autocommit mode. CREATE INDEX CONCURRENTLY cannot run
-- in the transaction used by the application's embedded migrations.
CREATE INDEX CONCURRENTLY IF NOT EXISTS ix_security_audit_events_tenant_governance_run
ON public.security_audit_events (tenant_id, md5((details_json -> 'governanceRunId')::text))
WHERE details_json ? 'governanceRunId';

DO $verify$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_index i
        JOIN pg_class c ON c.oid = i.indexrelid
        JOIN pg_am a ON a.oid = c.relam
        WHERE i.indexrelid = to_regclass('public.ix_security_audit_events_tenant_governance_run')
          AND i.indrelid = 'public.security_audit_events'::regclass
          AND i.indisvalid AND i.indisready AND i.indislive
          AND NOT i.indisunique AND i.indnkeyatts = 2 AND i.indnatts = 2
          AND a.amname = 'btree'
          AND pg_get_indexdef(i.indexrelid, 1, false) = 'tenant_id'
          AND regexp_replace(pg_get_indexdef(i.indexrelid, 2, false), '[[:space:]()]', '', 'g')
              = $key$md5details_json->'governanceRunId'::text::text$key$
          AND regexp_replace(pg_get_expr(i.indpred, i.indrelid), '[[:space:]()]', '', 'g')
              = $predicate$details_json?'governanceRunId'::text$predicate$
    ) THEN
        RAISE EXCEPTION 'Missing, invalid, or unexpected governance audit run index. Inspect it before retrying.';
    END IF;
END
$verify$;

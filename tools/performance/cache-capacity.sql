-- Read-only inventory. Run through an approved PostgreSQL session; never put credentials in argv.
BEGIN READ ONLY;
SET LOCAL statement_timeout = '10s';
SET LOCAL lock_timeout = '2s';

WITH targets(schema_name, table_name) AS (VALUES
    ('monitoring', 'cache_metric_minutes'), ('monitoring', 'cache_metric_coverage'),
    ('monitoring', 'cache_metric_boots'), ('public', 'cache_scope_revisions'),
    ('public', 'dashboard_graph_projection'), ('audit', 'authority_outbox_events'),
    ('public', 'retrieval_events'), ('public', 'retrieval_hits'))
SELECT t.schema_name, t.table_name, c.oid IS NOT NULL AS present,
       c.reltuples AS estimated_rows, c.relkind,
       pg_relation_size(c.oid) AS heap_bytes, pg_indexes_size(c.oid) AS index_bytes,
       pg_total_relation_size(c.oid) AS total_bytes,
       s.n_live_tup, s.n_dead_tup, s.n_tup_ins, s.n_tup_upd,
       s.last_autovacuum, s.last_autoanalyze
FROM targets t
LEFT JOIN pg_namespace n ON n.nspname = t.schema_name
LEFT JOIN pg_class c ON c.relnamespace = n.oid AND c.relname = t.table_name
LEFT JOIN pg_stat_user_tables s ON s.relid = c.oid
ORDER BY t.schema_name, t.table_name;

-- A query window is not a retention policy. These are schema/storage observations only.
SELECT n.nspname AS schema_name, c.relname AS table_name, c.relkind,
       c.reloptions, pg_get_expr(c.relpartbound, c.oid) AS partition_bound
FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
WHERE n.nspname = 'monitoring' AND c.relname LIKE 'cache_metric_%'
ORDER BY c.relname;

SELECT name, applied_at FROM schema_migrations WHERE name ~ '^05[345]_' ORDER BY name;

-- Planning only: this does not execute a 30-day scan or claim runtime performance.
-- An absent migration fails this statement; do not interpret partial output as a release PASS.
EXPLAIN (FORMAT JSON)
SELECT kind, traffic_class, SUM(hits), SUM(misses), SUM(observations)
FROM monitoring.cache_metric_minutes
WHERE bucket_start_utc >= date_trunc('minute', now()) - INTERVAL '30 days'
  AND bucket_start_utc < date_trunc('minute', now())
GROUP BY kind, traffic_class;
COMMIT;

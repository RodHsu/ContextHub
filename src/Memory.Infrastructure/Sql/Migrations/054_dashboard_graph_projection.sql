CREATE TABLE IF NOT EXISTS dashboard_graph_projection (
    scope text PRIMARY KEY,
    generation bigint NOT NULL DEFAULT 0,
    lease_token uuid,
    lease_expires_at timestamptz,
    revisions jsonb NOT NULL DEFAULT '{}'::jsonb,
    snapshot jsonb,
    mode text NOT NULL DEFAULT 'Unknown',
    last_success_at timestamptz,
    last_full_at timestamptz,
    last_checked_at timestamptz,
    dirty_since timestamptz,
    full_builds bigint NOT NULL DEFAULT 0,
    incremental_builds bigint NOT NULL DEFAULT 0,
    skipped bigint NOT NULL DEFAULT 0,
    deduplicated bigint NOT NULL DEFAULT 0,
    failures bigint NOT NULL DEFAULT 0,
    last_error text NOT NULL DEFAULT ''
);

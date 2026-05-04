CREATE TABLE IF NOT EXISTS source_connections
(
    id UUID PRIMARY KEY,
    project_id TEXT NOT NULL DEFAULT 'default',
    name TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    enabled BOOLEAN NOT NULL DEFAULT TRUE,
    config_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    secret_json_protected TEXT NOT NULL DEFAULT '',
    last_cursor TEXT NOT NULL DEFAULT '',
    last_successful_sync_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_source_connections_project_name
    ON source_connections(project_id, name);

CREATE TABLE IF NOT EXISTS source_sync_runs
(
    id UUID PRIMARY KEY,
    source_connection_id UUID NOT NULL REFERENCES source_connections(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL DEFAULT 'default',
    trigger TEXT NOT NULL,
    status TEXT NOT NULL,
    scanned_count INTEGER NOT NULL DEFAULT 0,
    upserted_count INTEGER NOT NULL DEFAULT 0,
    archived_count INTEGER NOT NULL DEFAULT 0,
    error_count INTEGER NOT NULL DEFAULT 0,
    cursor_before TEXT NOT NULL DEFAULT '',
    cursor_after TEXT NOT NULL DEFAULT '',
    error TEXT NOT NULL DEFAULT '',
    started_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_source_sync_runs_source_started_at
    ON source_sync_runs(source_connection_id, started_at DESC);

CREATE TABLE IF NOT EXISTS governance_findings
(
    id UUID PRIMARY KEY,
    project_id TEXT NOT NULL DEFAULT 'default',
    source_connection_id UUID NULL REFERENCES source_connections(id) ON DELETE SET NULL,
    primary_memory_id UUID NULL REFERENCES memory_items(id) ON DELETE SET NULL,
    secondary_memory_id UUID NULL REFERENCES memory_items(id) ON DELETE SET NULL,
    type TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Open',
    title TEXT NOT NULL,
    summary TEXT NOT NULL,
    details_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    dedup_key TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_governance_findings_dedup_key
    ON governance_findings(dedup_key);

CREATE INDEX IF NOT EXISTS ix_governance_findings_project_status_updated_at
    ON governance_findings(project_id, status, updated_at DESC);

CREATE TABLE IF NOT EXISTS evaluation_suites
(
    id UUID PRIMARY KEY,
    project_id TEXT NOT NULL DEFAULT 'default',
    name TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_evaluation_suites_project_updated_at
    ON evaluation_suites(project_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS evaluation_cases
(
    id UUID PRIMARY KEY,
    suite_id UUID NOT NULL REFERENCES evaluation_suites(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL DEFAULT 'default',
    scenario_label TEXT NOT NULL,
    query TEXT NOT NULL,
    expected_memory_ids TEXT[] NOT NULL DEFAULT '{}',
    expected_external_keys TEXT[] NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS evaluation_runs
(
    id UUID PRIMARY KEY,
    suite_id UUID NOT NULL REFERENCES evaluation_suites(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL DEFAULT 'default',
    status TEXT NOT NULL DEFAULT 'Pending',
    embedding_profile TEXT NOT NULL DEFAULT '',
    query_mode TEXT NOT NULL DEFAULT 'CurrentOnly',
    use_summary_layer BOOLEAN NOT NULL DEFAULT FALSE,
    top_k INTEGER NOT NULL DEFAULT 5,
    hit_rate NUMERIC(9,4) NOT NULL DEFAULT 0,
    recall_at_k NUMERIC(9,4) NOT NULL DEFAULT 0,
    mean_reciprocal_rank NUMERIC(9,4) NOT NULL DEFAULT 0,
    average_latency_ms DOUBLE PRECISION NOT NULL DEFAULT 0,
    error TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL,
    started_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_evaluation_runs_project_started_at
    ON evaluation_runs(project_id, started_at DESC);

CREATE TABLE IF NOT EXISTS evaluation_run_items
(
    id UUID PRIMARY KEY,
    run_id UUID NOT NULL REFERENCES evaluation_runs(id) ON DELETE CASCADE,
    case_id UUID NOT NULL REFERENCES evaluation_cases(id) ON DELETE CASCADE,
    query TEXT NOT NULL,
    scenario_label TEXT NOT NULL,
    expected_memory_ids TEXT[] NOT NULL DEFAULT '{}',
    expected_external_keys TEXT[] NOT NULL DEFAULT '{}',
    hit_memory_ids TEXT[] NOT NULL DEFAULT '{}',
    hit_external_keys TEXT[] NOT NULL DEFAULT '{}',
    hit_at_k BOOLEAN NOT NULL DEFAULT FALSE,
    recall_at_k NUMERIC(9,4) NOT NULL DEFAULT 0,
    reciprocal_rank NUMERIC(9,4) NOT NULL DEFAULT 0,
    latency_ms DOUBLE PRECISION NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS suggested_actions
(
    id UUID PRIMARY KEY,
    project_id TEXT NOT NULL DEFAULT 'default',
    type TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending',
    title TEXT NOT NULL,
    summary TEXT NOT NULL,
    payload_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    error TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    executed_at TIMESTAMPTZ NULL
);

CREATE INDEX IF NOT EXISTS ix_suggested_actions_project_status_updated_at
    ON suggested_actions(project_id, status, updated_at DESC);

CREATE INDEX IF NOT EXISTS ix_memory_jobs_hub_workflow_types
    ON memory_jobs(job_type, status, created_at)
    WHERE job_type IN ('SyncSource', 'AnalyzeGovernance', 'RunEvaluation', 'ExecuteSuggestedAction');

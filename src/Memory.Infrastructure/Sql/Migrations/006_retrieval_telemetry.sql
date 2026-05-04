CREATE TABLE IF NOT EXISTS retrieval_events
(
    id UUID PRIMARY KEY,
    project_id TEXT NOT NULL DEFAULT 'default',
    channel TEXT NOT NULL DEFAULT '',
    entry_point TEXT NOT NULL DEFAULT '',
    purpose TEXT NOT NULL DEFAULT '',
    query_text TEXT NOT NULL DEFAULT '',
    query_hash TEXT NOT NULL DEFAULT '',
    query_mode TEXT NOT NULL DEFAULT '',
    included_project_ids TEXT[] NOT NULL DEFAULT '{}',
    use_summary_layer BOOLEAN NOT NULL DEFAULT FALSE,
    result_limit INTEGER NOT NULL DEFAULT 10,
    cache_hit BOOLEAN NOT NULL DEFAULT FALSE,
    result_count INTEGER NOT NULL DEFAULT 0,
    duration_ms DOUBLE PRECISION NOT NULL DEFAULT 0,
    success BOOLEAN NOT NULL DEFAULT TRUE,
    error TEXT NOT NULL DEFAULT '',
    trace_id TEXT NOT NULL DEFAULT '',
    request_id TEXT NOT NULL DEFAULT '',
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_retrieval_events_project_created_at
    ON retrieval_events(project_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_retrieval_events_entry_point_created_at
    ON retrieval_events(entry_point, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_retrieval_events_query_hash_created_at
    ON retrieval_events(query_hash, created_at DESC);

CREATE TABLE IF NOT EXISTS retrieval_hits
(
    id UUID PRIMARY KEY,
    retrieval_event_id UUID NOT NULL REFERENCES retrieval_events(id) ON DELETE CASCADE,
    rank INTEGER NOT NULL,
    memory_id UUID NULL REFERENCES memory_items(id) ON DELETE SET NULL,
    title TEXT NOT NULL DEFAULT '',
    memory_type TEXT NOT NULL DEFAULT '',
    source_type TEXT NOT NULL DEFAULT '',
    source_ref TEXT NOT NULL DEFAULT '',
    score NUMERIC(18,6) NULL,
    excerpt TEXT NOT NULL DEFAULT '',
    project_id TEXT NOT NULL DEFAULT 'default'
);

CREATE INDEX IF NOT EXISTS ix_retrieval_hits_event_rank
    ON retrieval_hits(retrieval_event_id, rank ASC);

CREATE INDEX IF NOT EXISTS ix_retrieval_hits_memory_id
    ON retrieval_hits(memory_id);

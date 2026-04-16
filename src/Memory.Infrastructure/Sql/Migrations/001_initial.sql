CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE IF NOT EXISTS schema_migrations
(
    name TEXT PRIMARY KEY,
    applied_at TIMESTAMPTZ NOT NULL DEFAULT NOW()
);

CREATE TABLE IF NOT EXISTS memory_items
(
    id UUID PRIMARY KEY,
    external_key TEXT NOT NULL UNIQUE,
    scope TEXT NOT NULL,
    memory_type TEXT NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    summary TEXT NOT NULL,
    tags TEXT[] NOT NULL DEFAULT '{}',
    source_type TEXT NOT NULL,
    source_ref TEXT NOT NULL,
    importance NUMERIC(5,4) NOT NULL DEFAULT 0.5,
    confidence NUMERIC(5,4) NOT NULL DEFAULT 0.5,
    version INTEGER NOT NULL DEFAULT 1,
    status TEXT NOT NULL,
    metadata_json TEXT NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_item_revisions
(
    id UUID PRIMARY KEY,
    memory_item_id UUID NOT NULL REFERENCES memory_items(id) ON DELETE CASCADE,
    version INTEGER NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    summary TEXT NOT NULL,
    metadata_json TEXT NOT NULL DEFAULT '{}',
    changed_by TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_item_chunks
(
    id UUID PRIMARY KEY,
    memory_item_id UUID NOT NULL REFERENCES memory_items(id) ON DELETE CASCADE,
    chunk_kind TEXT NOT NULL,
    chunk_index INTEGER NOT NULL,
    chunk_text TEXT NOT NULL,
    metadata_json TEXT NOT NULL DEFAULT '{}',
    content_tsv TSVECTOR GENERATED ALWAYS AS (to_tsvector('simple', coalesce(chunk_text, ''))) STORED,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_chunk_vectors
(
    id UUID PRIMARY KEY,
    chunk_id UUID NOT NULL REFERENCES memory_item_chunks(id) ON DELETE CASCADE,
    model_key TEXT NOT NULL,
    dimension INTEGER NOT NULL,
    status TEXT NOT NULL,
    embedding VECTOR NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_links
(
    id UUID PRIMARY KEY,
    from_id UUID NOT NULL REFERENCES memory_items(id) ON DELETE CASCADE,
    to_id UUID NOT NULL REFERENCES memory_items(id) ON DELETE CASCADE,
    link_type TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS memory_jobs
(
    id UUID PRIMARY KEY,
    job_type TEXT NOT NULL,
    status TEXT NOT NULL,
    payload_json TEXT NOT NULL DEFAULT '{}',
    error TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL,
    started_at TIMESTAMPTZ NULL,
    completed_at TIMESTAMPTZ NULL
);

CREATE TABLE IF NOT EXISTS runtime_log_entries
(
    id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    service_name TEXT NOT NULL,
    category TEXT NOT NULL,
    level TEXT NOT NULL,
    message TEXT NOT NULL,
    exception TEXT NOT NULL DEFAULT '',
    trace_id TEXT NOT NULL DEFAULT '',
    request_id TEXT NOT NULL DEFAULT '',
    payload_json TEXT NOT NULL DEFAULT '{}',
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS log_ingestion_checkpoints
(
    id UUID PRIMARY KEY,
    service_name TEXT NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_memory_item_chunks_memory_item_id ON memory_item_chunks(memory_item_id);
CREATE INDEX IF NOT EXISTS ix_memory_item_chunks_content_tsv ON memory_item_chunks USING GIN(content_tsv);
CREATE INDEX IF NOT EXISTS ix_memory_chunk_vectors_chunk_model ON memory_chunk_vectors(chunk_id, model_key, status);
CREATE INDEX IF NOT EXISTS ix_memory_jobs_status_created_at ON memory_jobs(status, created_at);
CREATE INDEX IF NOT EXISTS ix_runtime_log_entries_service_created_at ON runtime_log_entries(service_name, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_runtime_log_entries_trace_id ON runtime_log_entries(trace_id);

CREATE TABLE IF NOT EXISTS retrieval_telemetry_daily_summaries
(
    summary_date DATE NOT NULL,
    tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    owner_user_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    project_id TEXT NOT NULL DEFAULT 'default',
    channel TEXT NOT NULL DEFAULT '',
    entry_point TEXT NOT NULL DEFAULT '',
    purpose TEXT NOT NULL DEFAULT '',
    query_mode TEXT NOT NULL DEFAULT '',
    request_count BIGINT NOT NULL DEFAULT 0,
    success_count BIGINT NOT NULL DEFAULT 0,
    error_count BIGINT NOT NULL DEFAULT 0,
    zero_result_count BIGINT NOT NULL DEFAULT 0,
    cache_hit_count BIGINT NOT NULL DEFAULT 0,
    result_count_sum BIGINT NOT NULL DEFAULT 0,
    duration_ms_sum DOUBLE PRECISION NOT NULL DEFAULT 0,
    duration_ms_max DOUBLE PRECISION NOT NULL DEFAULT 0,
    duration_ms_p95 DOUBLE PRECISION NOT NULL DEFAULT 0,
    first_seen_at TIMESTAMPTZ NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (summary_date, tenant_id, owner_user_id, project_id, channel, entry_point, purpose, query_mode)
);

CREATE INDEX IF NOT EXISTS ix_retrieval_telemetry_daily_summaries_date
    ON retrieval_telemetry_daily_summaries(summary_date DESC);

CREATE INDEX IF NOT EXISTS ix_retrieval_telemetry_daily_summaries_project_entry_date
    ON retrieval_telemetry_daily_summaries(project_id, entry_point, summary_date DESC);

CREATE TABLE IF NOT EXISTS retrieval_telemetry_daily_hit_summaries
(
    summary_date DATE NOT NULL,
    tenant_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    owner_user_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    project_id TEXT NOT NULL DEFAULT 'default',
    entry_point TEXT NOT NULL DEFAULT '',
    memory_id UUID NOT NULL DEFAULT '00000000-0000-0000-0000-000000000000',
    title TEXT NOT NULL DEFAULT '',
    memory_type TEXT NOT NULL DEFAULT '',
    source_type TEXT NOT NULL DEFAULT '',
    source_ref TEXT NOT NULL DEFAULT '',
    hit_count BIGINT NOT NULL DEFAULT 0,
    best_rank INTEGER NOT NULL DEFAULT 0,
    best_score NUMERIC NULL,
    average_score NUMERIC NULL,
    first_seen_at TIMESTAMPTZ NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (summary_date, tenant_id, owner_user_id, project_id, entry_point, memory_id, source_ref)
);

CREATE INDEX IF NOT EXISTS ix_retrieval_telemetry_daily_hit_summaries_date
    ON retrieval_telemetry_daily_hit_summaries(summary_date DESC);

CREATE INDEX IF NOT EXISTS ix_retrieval_telemetry_daily_hit_summaries_project_entry_date_count
    ON retrieval_telemetry_daily_hit_summaries(project_id, entry_point, summary_date DESC, hit_count DESC);

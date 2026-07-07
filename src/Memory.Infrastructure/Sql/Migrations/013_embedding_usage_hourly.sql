CREATE TABLE IF NOT EXISTS embedding_usage_hourly
(
    bucket_start_utc TIMESTAMPTZ NOT NULL,
    service_name TEXT NOT NULL,
    provider TEXT NOT NULL,
    profile TEXT NOT NULL,
    purpose TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    max_tokens INTEGER NOT NULL,
    total_inputs BIGINT NOT NULL DEFAULT 0,
    truncated_inputs BIGINT NOT NULL DEFAULT 0,
    total_token_count BIGINT NOT NULL DEFAULT 0,
    total_truncated_tokens BIGINT NOT NULL DEFAULT 0,
    max_token_count INTEGER NOT NULL DEFAULT 0,
    histogram_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    first_seen_at TIMESTAMPTZ NOT NULL,
    last_seen_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT pk_embedding_usage_hourly PRIMARY KEY
    (
        bucket_start_utc,
        service_name,
        provider,
        profile,
        purpose,
        source_kind,
        max_tokens
    )
);

CREATE INDEX IF NOT EXISTS ix_embedding_usage_hourly_bucket_start
    ON embedding_usage_hourly(bucket_start_utc);

CREATE INDEX IF NOT EXISTS ix_embedding_usage_hourly_group_bucket
    ON embedding_usage_hourly(service_name, profile, purpose, source_kind, bucket_start_utc);

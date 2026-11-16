CREATE SCHEMA IF NOT EXISTS monitoring;

CREATE TABLE IF NOT EXISTS monitoring.cache_metric_boots (
    instance_id text NOT NULL,
    boot_id uuid NOT NULL,
    started_at_utc timestamptz NOT NULL,
    last_flushed_at_utc timestamptz NOT NULL,
    stopped_at_utc timestamptz NULL,
    pending_buckets integer NOT NULL DEFAULT 0,
    dropped_samples bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (instance_id, boot_id)
);

CREATE TABLE IF NOT EXISTS monitoring.cache_metric_minutes (
    instance_id text NOT NULL,
    boot_id uuid NOT NULL,
    bucket_start_utc timestamptz NOT NULL,
    kind text NOT NULL,
    traffic_class text NOT NULL,
    revision bigint NOT NULL,
    hits bigint NOT NULL DEFAULT 0,
    misses bigint NOT NULL DEFAULT 0,
    sets bigint NOT NULL DEFAULT 0,
    bypasses bigint NOT NULL DEFAULT 0,
    errors bigint NOT NULL DEFAULT 0,
    invalid_payloads bigint NOT NULL DEFAULT 0,
    duration_ms double precision NOT NULL DEFAULT 0,
    observations bigint NOT NULL DEFAULT 0,
    PRIMARY KEY (instance_id, boot_id, bucket_start_utc, kind, traffic_class)
);

CREATE INDEX IF NOT EXISTS ix_cache_metric_minutes_window
    ON monitoring.cache_metric_minutes (bucket_start_utc);
CREATE INDEX IF NOT EXISTS ix_cache_metric_boots_window
    ON monitoring.cache_metric_boots (last_flushed_at_utc);

CREATE TABLE IF NOT EXISTS monitoring.cache_metric_coverage (
    instance_id text NOT NULL,
    boot_id uuid NOT NULL,
    bucket_start_utc timestamptz NOT NULL,
    PRIMARY KEY (instance_id, boot_id, bucket_start_utc)
);

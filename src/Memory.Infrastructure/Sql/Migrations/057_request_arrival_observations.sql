-- Additive, opt-in diagnostic capture. No payload, actor/project identifiers or credentials.
CREATE TABLE IF NOT EXISTS monitoring.request_arrival_boots (
    boot_id uuid PRIMARY KEY,
    started_at_utc timestamptz NOT NULL,
    until_utc timestamptz NOT NULL,
    timestamp_frequency bigint NOT NULL CHECK (timestamp_frequency > 0),
    revision bigint NOT NULL,
    last_flushed_at_utc timestamptz NOT NULL,
    stopped_at_utc timestamptz,
    http_started bigint NOT NULL,
    http_completed bigint NOT NULL,
    http_active integer NOT NULL,
    tool_started bigint NOT NULL,
    tool_completed bigint NOT NULL,
    tool_active integer NOT NULL,
    admitted bigint NOT NULL,
    dropped_starts bigint NOT NULL,
    dropped_finishes bigint NOT NULL,
    window_closed boolean NOT NULL DEFAULT false
);

CREATE TABLE IF NOT EXISTS monitoring.request_arrival_samples (
    boot_id uuid NOT NULL REFERENCES monitoring.request_arrival_boots(boot_id),
    sequence bigint NOT NULL,
    parent_sequence bigint NOT NULL,
    layer text NOT NULL CHECK (layer IN ('http','mcp-tool')),
    operation text NOT NULL CHECK (operation IN ('memory_search','build_working_context','memory_upsert','memory_update','rest-memory-search','rest-working-context','http-mcp','other')),
    started_at_utc timestamptz NOT NULL,
    started_timestamp bigint NOT NULL,
    active_at_start integer NOT NULL CHECK (active_at_start > 0),
    finished_at_utc timestamptz,
    duration_ms double precision,
    outcome text NOT NULL CHECK (outcome IN ('inflight','success','error','cancelled')),
    revision integer NOT NULL CHECK (revision IN (1,2)),
    PRIMARY KEY (boot_id, sequence)
);
CREATE INDEX IF NOT EXISTS ix_request_arrival_samples_start ON monitoring.request_arrival_samples(started_at_utc);

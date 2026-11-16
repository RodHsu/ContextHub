-- Additive diagnostic evidence. The migration-057 columns and v1 contract remain unchanged.
ALTER TABLE monitoring.request_arrival_boots
    ADD COLUMN IF NOT EXISTS capture_schema_version integer NOT NULL DEFAULT 1 CHECK (capture_schema_version IN (1,2)),
    ADD COLUMN IF NOT EXISTS v2_clock_kind text CHECK (v2_clock_kind IS NULL OR v2_clock_kind = 'CLOCK_MONOTONIC_RAW'),
    ADD COLUMN IF NOT EXISTS kernel_boot_id uuid,
    ADD COLUMN IF NOT EXISTS time_namespace text CHECK (time_namespace IS NULL OR time_namespace ~ '^time:\[[0-9]{1,20}\]$'),
    ADD COLUMN IF NOT EXISTS monotonic_offset_ns bigint,
    ADD COLUMN IF NOT EXISTS boottime_offset_ns bigint,
    ADD COLUMN IF NOT EXISTS clocksource text CHECK (clocksource IS NULL OR clocksource ~ '^[A-Za-z0-9_.-]{1,64}$'),
    ADD COLUMN IF NOT EXISTS raw_started_before_ns bigint CHECK (raw_started_before_ns >= 0),
    ADD COLUMN IF NOT EXISTS raw_started_after_ns bigint CHECK (raw_started_after_ns >= raw_started_before_ns),
    ADD COLUMN IF NOT EXISTS raw_max_span_ns bigint CHECK (raw_max_span_ns > 0 AND raw_max_span_ns <= 86400000000000),
    ADD COLUMN IF NOT EXISTS clock_checks bigint NOT NULL DEFAULT 0 CHECK (clock_checks >= 0),
    ADD COLUMN IF NOT EXISTS clock_failures bigint NOT NULL DEFAULT 0 CHECK (clock_failures >= 0),
    ADD COLUMN IF NOT EXISTS clock_discontinuities bigint NOT NULL DEFAULT 0 CHECK (clock_discontinuities >= 0),
    ADD COLUMN IF NOT EXISTS clock_suspends bigint NOT NULL DEFAULT 0 CHECK (clock_suspends >= 0),
    ADD COLUMN IF NOT EXISTS clock_invalid_reason text CHECK (clock_invalid_reason IS NULL OR clock_invalid_reason IN
        ('CLOCK_DOMAIN_UNAVAILABLE','CLOCK_DOMAIN_CHANGED','CLOCK_READ_FAILED','CLOCK_READ_BRACKET_EXCEEDED',
         'RAW_CLOCK_REGRESSED','CLOCK_SUSPEND_OBSERVED')),
    ADD COLUMN IF NOT EXISTS clock_anchor_admitted bigint NOT NULL DEFAULT 0 CHECK (clock_anchor_admitted >= 0),
    ADD COLUMN IF NOT EXISTS clock_anchor_dropped bigint NOT NULL DEFAULT 0 CHECK (clock_anchor_dropped >= 0);

ALTER TABLE monitoring.request_arrival_samples
    ADD COLUMN IF NOT EXISTS raw_started_before_ns bigint CHECK (raw_started_before_ns >= 0),
    ADD COLUMN IF NOT EXISTS raw_started_after_ns bigint CHECK (raw_started_after_ns >= raw_started_before_ns),
    ADD COLUMN IF NOT EXISTS raw_finished_before_ns bigint CHECK (raw_finished_before_ns >= raw_started_after_ns),
    ADD COLUMN IF NOT EXISTS raw_finished_after_ns bigint CHECK (raw_finished_after_ns >= raw_finished_before_ns),
    ADD COLUMN IF NOT EXISTS request_family text CHECK (request_family IS NULL OR request_family IN
        ('http-mcp','memory-read','memory-write','context-read','project-read','project-write',
         'work-item-read','work-item-write','discussion-read','discussion-write','governance-read','governance-write',
         'diagnostics-read','diagnostics-write','security-read','security-write','agent-read','agent-write',
         'artifact-read','artifact-write','other-api-read','other-api-write','unknown')),
    ADD COLUMN IF NOT EXISTS request_method text CHECK (request_method IS NULL OR request_method IN
        ('GET','POST','PUT','PATCH','DELETE','HEAD','OPTIONS','UNKNOWN')),
    ADD COLUMN IF NOT EXISTS payload_size_bucket text CHECK (payload_size_bucket IS NULL OR payload_size_bucket IN
        ('unknown','empty','le1k','le16k','le256k','le4m','gt4m'));

CREATE TABLE IF NOT EXISTS monitoring.request_arrival_clock_anchors (
    boot_id uuid NOT NULL REFERENCES monitoring.request_arrival_boots(boot_id),
    sequence bigint NOT NULL CHECK (sequence > 0),
    raw_before_ns bigint NOT NULL CHECK (raw_before_ns >= 0),
    raw_after_ns bigint NOT NULL CHECK (raw_after_ns >= raw_before_ns),
    monotonic_ns bigint NOT NULL CHECK (monotonic_ns >= 0),
    boottime_ns bigint NOT NULL CHECK (boottime_ns >= 0),
    realtime_ns bigint NOT NULL CHECK (realtime_ns >= 0),
    kernel_boot_id uuid NOT NULL,
    time_namespace text NOT NULL CHECK (time_namespace ~ '^time:\[[0-9]{1,20}\]$'),
    monotonic_offset_ns bigint NOT NULL,
    boottime_offset_ns bigint NOT NULL,
    clocksource text NOT NULL CHECK (clocksource ~ '^[A-Za-z0-9_.-]{1,64}$'),
    PRIMARY KEY (boot_id, sequence)
);

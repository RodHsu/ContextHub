CREATE TABLE IF NOT EXISTS agent_connectivity_observations (
    id uuid PRIMARY KEY,
    project_id text NOT NULL,
    agent_id text NOT NULL,
    agent_name text NOT NULL DEFAULT '',
    agent_version text NOT NULL DEFAULT '',
    bridge_version text NOT NULL DEFAULT '',
    endpoint_host text NOT NULL,
    transport text NOT NULL DEFAULT 'mcp-streamable-http',
    mcp_method text NOT NULL,
    tool_name text NOT NULL DEFAULT '',
    attempt integer NOT NULL DEFAULT 1,
    success boolean NOT NULL,
    status_code integer NULL,
    error_kind text NOT NULL DEFAULT '',
    client_elapsed_ms double precision NOT NULL,
    server_elapsed_ms double precision NULL,
    network_overhead_ms double precision NULL,
    session_was_initialized boolean NOT NULL DEFAULT false,
    reconnect_attempted boolean NOT NULL DEFAULT false,
    correlation_id text NOT NULL DEFAULT '',
    source text NOT NULL DEFAULT 'stdio-bridge',
    observed_at_utc timestamptz NOT NULL,
    created_at_utc timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_agent_conn_obs_project_observed
    ON agent_connectivity_observations (project_id, observed_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_agent_conn_obs_agent_observed
    ON agent_connectivity_observations (agent_id, observed_at_utc DESC);

CREATE INDEX IF NOT EXISTS ix_agent_conn_obs_success_observed
    ON agent_connectivity_observations (success, observed_at_utc DESC);

CREATE TABLE IF NOT EXISTS agent_connectivity_summaries (
    bucket_start_utc timestamptz NOT NULL,
    bucket_minutes integer NOT NULL DEFAULT 1,
    project_id text NOT NULL,
    agent_id text NOT NULL,
    endpoint_host text NOT NULL,
    transport text NOT NULL DEFAULT 'mcp-streamable-http',
    mcp_method text NOT NULL,
    tool_name text NOT NULL DEFAULT '',
    sample_count integer NOT NULL,
    success_count integer NOT NULL,
    failure_count integer NOT NULL,
    timeout_count integer NOT NULL,
    auth_failure_count integer NOT NULL,
    reconnect_count integer NOT NULL,
    avg_client_elapsed_ms double precision NOT NULL,
    p95_client_elapsed_ms double precision NOT NULL,
    max_client_elapsed_ms double precision NOT NULL,
    last_observed_at_utc timestamptz NOT NULL,
    status text NOT NULL,
    updated_at_utc timestamptz NOT NULL,
    CONSTRAINT pk_agent_connectivity_summaries PRIMARY KEY (
        bucket_start_utc,
        bucket_minutes,
        project_id,
        agent_id,
        endpoint_host,
        transport,
        mcp_method,
        tool_name
    )
);

CREATE INDEX IF NOT EXISTS ix_agent_conn_summary_project_bucket
    ON agent_connectivity_summaries (project_id, bucket_start_utc DESC);

CREATE INDEX IF NOT EXISTS ix_agent_conn_summary_agent_bucket
    ON agent_connectivity_summaries (agent_id, bucket_start_utc DESC);

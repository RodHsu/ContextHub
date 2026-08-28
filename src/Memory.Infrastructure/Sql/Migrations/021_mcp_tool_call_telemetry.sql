CREATE TABLE IF NOT EXISTS mcp_tool_call_events
(
    id uuid PRIMARY KEY,
    tenant_id uuid NULL REFERENCES tenants(id) ON DELETE SET NULL,
    owner_user_id uuid NULL REFERENCES tenant_users(id) ON DELETE SET NULL,
    project_id text NOT NULL,
    service_name text NOT NULL,
    tool_name text NOT NULL,
    success boolean NOT NULL,
    duration_ms double precision NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_mcp_tool_call_events_created_at
    ON mcp_tool_call_events(created_at DESC);

CREATE INDEX IF NOT EXISTS ix_mcp_tool_call_events_tenant_created_at
    ON mcp_tool_call_events(tenant_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_mcp_tool_call_events_project_created_at
    ON mcp_tool_call_events(project_id, created_at DESC);

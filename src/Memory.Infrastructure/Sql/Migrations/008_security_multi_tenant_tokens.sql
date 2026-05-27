CREATE TABLE IF NOT EXISTS tenants
(
    id UUID PRIMARY KEY,
    slug TEXT NOT NULL,
    display_name TEXT NOT NULL,
    status TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS tenant_users
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    username TEXT NOT NULL,
    display_name TEXT NOT NULL,
    email TEXT NOT NULL DEFAULT '',
    role TEXT NOT NULL,
    status TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS tenant_project_grants
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    can_read BOOLEAN NOT NULL DEFAULT TRUE,
    can_write BOOLEAN NOT NULL DEFAULT FALSE,
    can_manage_tokens BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS api_tokens
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NOT NULL REFERENCES tenant_users(id) ON DELETE RESTRICT,
    name TEXT NOT NULL,
    notes TEXT NOT NULL DEFAULT '',
    token_prefix TEXT NOT NULL,
    token_hash TEXT NOT NULL,
    token_last_four TEXT NOT NULL,
    scopes TEXT[] NOT NULL DEFAULT '{}',
    allowed_project_ids TEXT[] NOT NULL DEFAULT '{}',
    expires_at TIMESTAMPTZ NULL,
    revoked_at TIMESTAMPTZ NULL,
    last_used_at TIMESTAMPTZ NULL,
    last_used_ip TEXT NOT NULL DEFAULT '',
    last_used_user_agent TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS security_audit_events
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL,
    actor_user_id UUID NULL,
    api_token_id UUID NULL,
    event_type TEXT NOT NULL,
    outcome TEXT NOT NULL,
    ip_address TEXT NOT NULL DEFAULT '',
    user_agent TEXT NOT NULL DEFAULT '',
    details_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_tenants_slug ON tenants(slug);
CREATE UNIQUE INDEX IF NOT EXISTS ix_tenant_users_tenant_username ON tenant_users(tenant_id, username);
CREATE UNIQUE INDEX IF NOT EXISTS ix_tenant_project_grants_tenant_project ON tenant_project_grants(tenant_id, project_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_api_tokens_token_hash ON api_tokens(token_hash);
CREATE INDEX IF NOT EXISTS ix_api_tokens_tenant_name ON api_tokens(tenant_id, name);
CREATE INDEX IF NOT EXISTS ix_api_tokens_last_used_at ON api_tokens(last_used_at);
CREATE INDEX IF NOT EXISTS ix_security_audit_events_tenant_created_at ON security_audit_events(tenant_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_security_audit_events_token_created_at ON security_audit_events(api_token_id, created_at DESC);

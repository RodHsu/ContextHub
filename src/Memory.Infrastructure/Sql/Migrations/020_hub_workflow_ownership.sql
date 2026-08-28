ALTER TABLE source_connections
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

ALTER TABLE governance_findings
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

ALTER TABLE evaluation_suites
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

ALTER TABLE suggested_actions
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE source_connections
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE source_connections.tenant_id IS NULL OR source_connections.owner_user_id IS NULL;

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE governance_findings
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE governance_findings.tenant_id IS NULL OR governance_findings.owner_user_id IS NULL;

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE evaluation_suites
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE evaluation_suites.tenant_id IS NULL OR evaluation_suites.owner_user_id IS NULL;

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE suggested_actions
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE suggested_actions.tenant_id IS NULL OR suggested_actions.owner_user_id IS NULL;

DROP INDEX IF EXISTS ix_source_connections_project_name;
CREATE UNIQUE INDEX IF NOT EXISTS ix_source_connections_owner_project_name
    ON source_connections(tenant_id, owner_user_id, project_id, name);

DROP INDEX IF EXISTS ix_governance_findings_dedup_key;
CREATE UNIQUE INDEX IF NOT EXISTS ix_governance_findings_owner_dedup_key
    ON governance_findings(tenant_id, owner_user_id, dedup_key);

CREATE INDEX IF NOT EXISTS ix_evaluation_suites_owner_project_updated_at
    ON evaluation_suites(tenant_id, owner_user_id, project_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS ix_suggested_actions_owner_project_status_updated_at
    ON suggested_actions(tenant_id, owner_user_id, project_id, status, updated_at DESC);

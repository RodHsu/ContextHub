ALTER TABLE tenant_users
    ADD COLUMN IF NOT EXISTS password_hash TEXT NOT NULL DEFAULT '',
    ADD COLUMN IF NOT EXISTS last_login_at TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS password_updated_at TIMESTAMPTZ NULL;

INSERT INTO tenants (id, slug, display_name, status, created_at, updated_at)
VALUES ('72000000-0000-0000-0000-000000000001', 'context-team', 'Context Team', 'Active', NOW(), NOW())
ON CONFLICT (slug) DO NOTHING;

INSERT INTO tenant_users (
    id,
    tenant_id,
    username,
    display_name,
    email,
    password_hash,
    role,
    status,
    password_updated_at,
    created_at,
    updated_at)
SELECT
    '73000000-0000-0000-0000-000000000001',
    t.id,
    'admin',
    'Admin User',
    'admin@example.com',
    'AQAAAAIAAYagAAAAEIbguUQEApMQehlC51gjy+uGulsE4ahRI7UtbdAlSsGMynNrNM3J3KfsJL+3IuBUxQ==',
    'Owner',
    'Active',
    NOW(),
    NOW(),
    NOW()
FROM tenants t
WHERE t.slug = 'context-team'
ON CONFLICT (tenant_id, username) DO NOTHING;

UPDATE tenant_users
SET password_hash = 'AQAAAAIAAYagAAAAEIbguUQEApMQehlC51gjy+uGulsE4ahRI7UtbdAlSsGMynNrNM3J3KfsJL+3IuBUxQ==',
    password_updated_at = COALESCE(password_updated_at, NOW()),
    updated_at = NOW()
WHERE username = 'admin'
  AND password_hash = '';

ALTER TABLE memory_items
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

ALTER TABLE retrieval_events
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

ALTER TABLE conversation_sessions
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

ALTER TABLE conversation_checkpoints
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

ALTER TABLE conversation_insights
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

ALTER TABLE memory_jobs
    ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE RESTRICT,
    ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE RESTRICT;

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE memory_items
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE memory_items.tenant_id IS NULL OR memory_items.owner_user_id IS NULL;

-- Historical retrieval_events can be large on long-running instances.
-- Keep owner columns nullable and let new telemetry writes populate them; legacy
-- rows can be backfilled later by an offline maintenance job.

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE conversation_sessions
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE conversation_sessions.tenant_id IS NULL OR conversation_sessions.owner_user_id IS NULL;

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE conversation_checkpoints
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE conversation_checkpoints.tenant_id IS NULL OR conversation_checkpoints.owner_user_id IS NULL;

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE conversation_insights
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE conversation_insights.tenant_id IS NULL OR conversation_insights.owner_user_id IS NULL;

WITH admin_user AS (
    SELECT u.tenant_id, u.id AS owner_user_id
    FROM tenant_users u
    JOIN tenants t ON t.id = u.tenant_id
    WHERE t.slug = 'context-team' AND u.username = 'admin'
    LIMIT 1
)
UPDATE memory_jobs
SET tenant_id = admin_user.tenant_id,
    owner_user_id = admin_user.owner_user_id
FROM admin_user
WHERE memory_jobs.tenant_id IS NULL OR memory_jobs.owner_user_id IS NULL;

DROP INDEX IF EXISTS ix_memory_items_project_external_key;
CREATE UNIQUE INDEX IF NOT EXISTS ix_memory_items_project_owner_external_key
    ON memory_items(project_id, owner_user_id, external_key);
CREATE INDEX IF NOT EXISTS ix_memory_items_owner_project_status_updated_at
    ON memory_items(owner_user_id, project_id, status, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_conversation_sessions_owner_updated_at
    ON conversation_sessions(owner_user_id, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_conversation_checkpoints_owner_created_at
    ON conversation_checkpoints(owner_user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_conversation_insights_owner_created_at
    ON conversation_insights(owner_user_id, created_at DESC);
CREATE INDEX IF NOT EXISTS ix_memory_jobs_owner_created_at
    ON memory_jobs(owner_user_id, created_at DESC);

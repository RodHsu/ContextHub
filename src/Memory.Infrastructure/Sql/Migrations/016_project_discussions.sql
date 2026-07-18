CREATE TABLE IF NOT EXISTS project_hierarchies
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    parent_project_id TEXT NOT NULL,
    child_project_id TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_project_hierarchies_distinct_projects CHECK (parent_project_id <> child_project_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_project_hierarchies_owner_parent_child ON project_hierarchies(tenant_id, owner_user_id, parent_project_id, child_project_id) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS discussion_threads
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    host_project_id TEXT NOT NULL,
    title TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Open',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_discussion_threads_owner_host_updated ON discussion_threads(tenant_id, owner_user_id, host_project_id, updated_at DESC);

CREATE TABLE IF NOT EXISTS discussion_participants
(
    thread_id UUID NOT NULL REFERENCES discussion_threads(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    last_read_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY(thread_id, project_id)
);
CREATE INDEX IF NOT EXISTS ix_discussion_participants_project ON discussion_participants(project_id);

CREATE TABLE IF NOT EXISTS discussion_messages
(
    id UUID PRIMARY KEY,
    thread_id UUID NOT NULL REFERENCES discussion_threads(id) ON DELETE CASCADE,
    sender_project_id TEXT NOT NULL,
    content TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_discussion_messages_thread_created ON discussion_messages(thread_id, created_at);

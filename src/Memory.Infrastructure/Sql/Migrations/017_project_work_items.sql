CREATE TABLE IF NOT EXISTS project_work_items
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    title TEXT NOT NULL,
    description TEXT NOT NULL DEFAULT '',
    tags TEXT[] NOT NULL DEFAULT '{}',
    status TEXT NOT NULL,
    priority INTEGER NOT NULL DEFAULT 0,
    due_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    completed_at TIMESTAMPTZ NULL
);
CREATE TABLE IF NOT EXISTS project_work_item_checklist_items
(
    id UUID PRIMARY KEY,
    work_item_id UUID NOT NULL REFERENCES project_work_items(id) ON DELETE CASCADE,
    content TEXT NOT NULL,
    is_completed BOOLEAN NOT NULL DEFAULT FALSE,
    sort_order INTEGER NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_project_work_items_owner_project_status_due
    ON project_work_items(tenant_id, owner_user_id, project_id, status, due_at);
CREATE INDEX IF NOT EXISTS ix_project_work_item_checklist_items_work_item_sort
    ON project_work_item_checklist_items(work_item_id, sort_order);

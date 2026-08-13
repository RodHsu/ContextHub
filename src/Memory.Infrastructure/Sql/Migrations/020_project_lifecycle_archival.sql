ALTER TABLE discussion_threads
    ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ NULL;

ALTER TABLE project_work_items
    ADD COLUMN IF NOT EXISTS archived_at TIMESTAMPTZ NULL;

CREATE INDEX IF NOT EXISTS ix_discussion_threads_active_owner_host_updated
    ON discussion_threads(tenant_id, owner_user_id, host_project_id, updated_at DESC)
    WHERE archived_at IS NULL;

CREATE INDEX IF NOT EXISTS ix_project_work_items_active_owner_project_status_due
    ON project_work_items(tenant_id, owner_user_id, project_id, status, due_at)
    WHERE archived_at IS NULL;

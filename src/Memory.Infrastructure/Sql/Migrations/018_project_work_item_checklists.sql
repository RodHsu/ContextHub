ALTER TABLE project_work_items
    ADD COLUMN IF NOT EXISTS tags TEXT[] NOT NULL DEFAULT '{}';

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

CREATE INDEX IF NOT EXISTS ix_project_work_item_checklist_items_work_item_sort
    ON project_work_item_checklist_items(work_item_id, sort_order);

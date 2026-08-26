ALTER TABLE project_work_items
    ADD COLUMN IF NOT EXISTS governance_exclusions_json JSONB NOT NULL DEFAULT '[]'::jsonb;

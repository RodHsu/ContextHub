ALTER TABLE memory_items
    ADD COLUMN IF NOT EXISTS project_id TEXT NOT NULL DEFAULT 'default',
    ADD COLUMN IF NOT EXISTS is_read_only BOOLEAN NOT NULL DEFAULT FALSE;

UPDATE memory_items
SET project_id = 'user'
WHERE scope = 'User'
  AND memory_type = 'Preference'
  AND project_id = 'default';

ALTER TABLE memory_jobs
    ADD COLUMN IF NOT EXISTS project_id TEXT NOT NULL DEFAULT 'default';

ALTER TABLE runtime_log_entries
    ADD COLUMN IF NOT EXISTS project_id TEXT NOT NULL DEFAULT 'default';

ALTER TABLE memory_items
    DROP CONSTRAINT IF EXISTS memory_items_external_key_key;
CREATE UNIQUE INDEX IF NOT EXISTS ix_memory_items_project_external_key ON memory_items(project_id, external_key);
CREATE INDEX IF NOT EXISTS ix_memory_items_project_status_updated_at ON memory_items(project_id, status, updated_at DESC);
CREATE INDEX IF NOT EXISTS ix_memory_jobs_project_status_created_at ON memory_jobs(project_id, status, created_at);
CREATE INDEX IF NOT EXISTS ix_runtime_log_entries_project_created_at ON runtime_log_entries(project_id, created_at DESC);

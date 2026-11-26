ALTER TABLE managed_transfer_sessions
    ADD COLUMN IF NOT EXISTS file_version_id uuid;

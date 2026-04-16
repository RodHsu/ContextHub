CREATE TABLE IF NOT EXISTS instance_settings
(
    instance_id TEXT NOT NULL,
    setting_key TEXT NOT NULL,
    value_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    revision INTEGER NOT NULL DEFAULT 1,
    updated_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    updated_by TEXT NOT NULL DEFAULT 'system',
    PRIMARY KEY (instance_id, setting_key)
);

CREATE INDEX IF NOT EXISTS idx_instance_settings_updated_at
    ON instance_settings (updated_at DESC);

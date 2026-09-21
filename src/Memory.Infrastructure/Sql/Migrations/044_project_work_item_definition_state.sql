ALTER TABLE project_work_items
    ADD COLUMN IF NOT EXISTS definition_state TEXT;

DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM project_work_items
        WHERE definition_state IS NULL
          AND (
              CASE WHEN tags && ARRAY['討論中', 'discussion:active']::text[] THEN 1 ELSE 0 END +
              CASE WHEN tags && ARRAY['definition:draft']::text[] THEN 1 ELSE 0 END +
              CASE WHEN tags && ARRAY['definition:ready-for-development']::text[] THEN 1 ELSE 0 END +
              CASE WHEN tags && ARRAY['definition:frozen']::text[] THEN 1 ELSE 0 END +
              CASE WHEN tags && ARRAY['definition:superseded']::text[] THEN 1 ELSE 0 END
          ) > 1
    ) THEN
        RAISE EXCEPTION 'project_work_items contains conflicting explicit DefinitionState compatibility tags';
    END IF;
END $$;

UPDATE project_work_items
SET definition_state = CASE
    WHEN tags && ARRAY['討論中', 'discussion:active']::text[] THEN 'Discussing'
    WHEN tags && ARRAY['definition:ready-for-development']::text[] THEN 'ReadyForDevelopment'
    WHEN tags && ARRAY['definition:frozen']::text[] THEN 'Frozen'
    WHEN tags && ARRAY['definition:superseded']::text[] THEN 'Superseded'
    ELSE 'Draft'
END
WHERE definition_state IS NULL;

ALTER TABLE project_work_items
    ALTER COLUMN definition_state SET DEFAULT 'Draft',
    ALTER COLUMN definition_state SET NOT NULL;

ALTER TABLE project_work_items
    DROP CONSTRAINT IF EXISTS ck_project_work_items_definition_state;

ALTER TABLE project_work_items
    ADD CONSTRAINT ck_project_work_items_definition_state
    CHECK (definition_state IN ('Discussing', 'Draft', 'ReadyForDevelopment', 'Frozen', 'Superseded'));

CREATE INDEX IF NOT EXISTS ix_project_work_items_owner_project_definition_state_status
    ON project_work_items(tenant_id, owner_user_id, project_id, definition_state, status)
    WHERE archived_at IS NULL;

-- Conversation automation deduplication is scoped to the owning tenant/user.
--
-- tenant_id and owner_user_id remain nullable for legacy rows.  Rows with a
-- missing identity are deliberately excluded from these indexes and the
-- application actor-scope predicates never return them.  This is fail-closed:
-- this migration does not guess or backfill ownership for legacy data.

-- Fail with a stable, actionable error before replacing the legacy indexes.
-- A duplicate must be reconciled explicitly so the migration never chooses an
-- arbitrary winner or discards audit history.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM conversation_sessions
        WHERE tenant_id IS NOT NULL AND owner_user_id IS NOT NULL
        GROUP BY tenant_id, owner_user_id, source_system, conversation_id
        HAVING COUNT(*) > 1) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23505',
            MESSAGE = 'conversation session scoped deduplication preflight failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM conversation_checkpoints
        WHERE tenant_id IS NOT NULL AND owner_user_id IS NOT NULL
        GROUP BY tenant_id, owner_user_id, dedup_key
        HAVING COUNT(*) > 1) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23505',
            MESSAGE = 'conversation checkpoint scoped deduplication preflight failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM conversation_insights
        WHERE tenant_id IS NOT NULL AND owner_user_id IS NOT NULL
        GROUP BY tenant_id, owner_user_id, dedup_key
        HAVING COUNT(*) > 1) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23505',
            MESSAGE = 'conversation insight scoped deduplication preflight failed';
    END IF;
END;
$$;

DROP INDEX IF EXISTS ix_conversation_sessions_source_conversation;
CREATE UNIQUE INDEX ix_conversation_sessions_source_conversation
    ON conversation_sessions(tenant_id, owner_user_id, source_system, conversation_id)
    WHERE tenant_id IS NOT NULL AND owner_user_id IS NOT NULL;

DROP INDEX IF EXISTS ix_conversation_checkpoints_dedup_key;
CREATE UNIQUE INDEX ix_conversation_checkpoints_dedup_key
    ON conversation_checkpoints(tenant_id, owner_user_id, dedup_key)
    WHERE tenant_id IS NOT NULL AND owner_user_id IS NOT NULL;

DROP INDEX IF EXISTS ix_conversation_insights_dedup_key;
CREATE UNIQUE INDEX ix_conversation_insights_dedup_key
    ON conversation_insights(tenant_id, owner_user_id, dedup_key)
    WHERE tenant_id IS NOT NULL AND owner_user_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_conversation_sessions_owner_project_updated_at
    ON conversation_sessions(tenant_id, owner_user_id, project_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS ix_conversation_checkpoints_owner_project_created_at
    ON conversation_checkpoints(tenant_id, owner_user_id, project_id, created_at DESC);

CREATE INDEX IF NOT EXISTS ix_conversation_insights_owner_project_status_created_at
    ON conversation_insights(tenant_id, owner_user_id, project_id, promotion_status, created_at);

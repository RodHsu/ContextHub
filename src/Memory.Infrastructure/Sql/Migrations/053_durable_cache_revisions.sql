-- Cache revisions commit with authority data, including direct SQL and vector writes.
-- 052 is reserved for the independent governance-adjudication change.
CREATE TABLE IF NOT EXISTS cache_scope_revisions (
    scope text PRIMARY KEY,
    revision bigint NOT NULL CHECK (revision > 0),
    updated_at timestamptz NOT NULL,
    last_transaction xid8 NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_memory_items_cache_project ON memory_items (btrim(project_id));

CREATE OR REPLACE FUNCTION bump_cache_scope_revision(target_scope text, source_project text DEFAULT NULL) RETURNS bigint
LANGUAGE plpgsql AS $$
DECLARE next_revision bigint;
BEGIN
    INSERT INTO cache_scope_revisions(scope, revision, updated_at, last_transaction)
    VALUES (target_scope, 1, clock_timestamp(), pg_current_xact_id())
    ON CONFLICT (scope) DO UPDATE
        SET revision = cache_scope_revisions.revision + 1,
            updated_at = clock_timestamp(), last_transaction = pg_current_xact_id()
        WHERE cache_scope_revisions.last_transaction <> pg_current_xact_id()
    RETURNING revision INTO next_revision;

    IF next_revision IS NULL THEN
        SELECT revision INTO next_revision FROM cache_scope_revisions WHERE scope = target_scope;
    ELSIF target_scope LIKE 'project:%' THEN
        source_project := COALESCE(source_project,
            (SELECT min(project_id) FROM memory_items WHERE btrim(project_id) = substring(target_scope FROM 9)),
            substring(target_scope FROM 9));
        -- One durable, content-free knowledge dirty event per scope per transaction.
        -- The revision ledger is the coalescing replay source; sequence allocation is not commit order.
        INSERT INTO audit.authority_outbox_events
            (id, tenant_id, project_id, category, aggregate_type, aggregate_id, event_type,
             authority_revision, security_critical, payload_json, occurred_at)
        VALUES (gen_random_uuid(), NULL, COALESCE(source_project, substring(target_scope FROM 9)), 'KnowledgeRevision',
                'CacheScope', target_scope, 'Changed', next_revision, false,
                jsonb_build_object('schemaVersion', '1.0', 'revision', next_revision), clock_timestamp());
    END IF;
    RETURN next_revision;
END;
$$;

-- The helper resolves owner/project before a delete removes parent identity.
CREATE OR REPLACE FUNCTION cache_scopes_for_row(table_name text, row_data jsonb) RETURNS SETOF text
LANGUAGE plpgsql AS $$
DECLARE item_row record;
BEGIN
    IF row_data IS NULL THEN RETURN; END IF;
    IF table_name IN ('memory_items', 'source_connections') THEN
        RETURN NEXT 'project:' || btrim(row_data->>'project_id');
        IF table_name = 'memory_items' AND lower(row_data->>'project_id') = 'user'
            AND row_data->>'tenant_id' IS NOT NULL AND row_data->>'owner_user_id' IS NOT NULL THEN
            RETURN NEXT 'user:' || replace(row_data->>'tenant_id', '-', '') || ':' || replace(row_data->>'owner_user_id', '-', '');
        END IF;
    ELSE
        FOR item_row IN
            SELECT DISTINCT m.project_id, m.tenant_id, m.owner_user_id FROM memory_items m
            WHERE (table_name IN ('memory_item_chunks', 'memory_item_revisions') AND m.id = (row_data->>'memory_item_id')::uuid)
               OR (table_name = 'memory_links' AND m.id IN ((row_data->>'from_id')::uuid, (row_data->>'to_id')::uuid))
               OR (table_name = 'governance_findings' AND m.id IN
                    ((row_data->>'primary_memory_id')::uuid, (row_data->>'secondary_memory_id')::uuid))
               OR (table_name = 'memory_chunk_vectors' AND m.id IN
                    (SELECT c.memory_item_id FROM memory_item_chunks c WHERE c.id = (row_data->>'chunk_id')::uuid))
        LOOP
            RETURN NEXT 'project:' || btrim(item_row.project_id);
            IF lower(item_row.project_id) = 'user' AND item_row.tenant_id IS NOT NULL AND item_row.owner_user_id IS NOT NULL THEN
                RETURN NEXT 'user:' || replace(item_row.tenant_id::text, '-', '') || ':' || replace(item_row.owner_user_id::text, '-', '');
            END IF;
        END LOOP;
    END IF;
END;
$$;

CREATE OR REPLACE FUNCTION capture_cache_scope_revision() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE target_scope text; source_project text;
BEGIN
    IF TG_OP = 'UPDATE' AND to_jsonb(OLD) = to_jsonb(NEW) THEN RETURN NEW; END IF;
    FOR target_scope IN
        SELECT scope FROM (
            SELECT cache_scopes_for_row(TG_TABLE_NAME, CASE WHEN TG_OP <> 'INSERT' THEN to_jsonb(OLD) END) AS scope
            UNION
            SELECT cache_scopes_for_row(TG_TABLE_NAME, CASE WHEN TG_OP <> 'DELETE' THEN to_jsonb(NEW) END) AS scope
        ) s WHERE scope IS NOT NULL ORDER BY scope
    LOOP
        source_project := NULL;
        IF target_scope LIKE 'project:%' THEN
            -- Keep the canonical project spelling in project-facing outbox records.
            IF TG_TABLE_NAME IN ('memory_items', 'source_connections') THEN
                IF TG_OP <> 'DELETE' AND 'project:' || btrim(NEW.project_id) = target_scope THEN source_project := NEW.project_id;
                ELSIF TG_OP <> 'INSERT' THEN source_project := OLD.project_id;
                END IF;
            END IF;
        END IF;
        PERFORM bump_cache_scope_revision(target_scope, source_project);
    END LOOP;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;

DO $$ DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['memory_items', 'memory_item_chunks', 'memory_item_revisions', 'memory_chunk_vectors', 'memory_links', 'source_connections', 'governance_findings']
    LOOP
        EXECUTE format('CREATE TRIGGER capture_cache_revision BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION capture_cache_scope_revision()', table_name);
    END LOOP;
END $$;

CREATE OR REPLACE FUNCTION capture_cache_security_revision() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE old_value jsonb; new_value jsonb;
BEGIN
    old_value := CASE WHEN TG_OP <> 'INSERT' THEN to_jsonb(OLD) END;
    new_value := CASE WHEN TG_OP <> 'DELETE' THEN to_jsonb(NEW) END;
    -- Token usage and login timestamps must not invalidate every request.
    IF TG_TABLE_NAME = 'api_tokens' THEN
        old_value := old_value - ARRAY['last_used_at', 'last_used_ip', 'last_used_user_agent', 'updated_at'];
        new_value := new_value - ARRAY['last_used_at', 'last_used_ip', 'last_used_user_agent', 'updated_at'];
    ELSIF TG_TABLE_NAME = 'tenant_users' THEN
        old_value := old_value - ARRAY['last_login_at', 'updated_at'];
        new_value := new_value - ARRAY['last_login_at', 'updated_at'];
    END IF;
    IF old_value IS DISTINCT FROM new_value THEN PERFORM bump_cache_scope_revision('security'); END IF;
    IF TG_OP = 'DELETE' THEN RETURN OLD; END IF;
    RETURN NEW;
END;
$$;

DO $$ DECLARE table_name text;
BEGIN
    FOREACH table_name IN ARRAY ARRAY['tenants', 'tenant_users', 'tenant_project_grants', 'api_tokens',
        'project_security_revisions', 'project_authorization_policies', 'project_explicit_grants', 'project_hierarchies']
    LOOP
        EXECUTE format('CREATE TRIGGER capture_cache_security BEFORE INSERT OR UPDATE OR DELETE ON %I FOR EACH ROW EXECUTE FUNCTION capture_cache_security_revision()', table_name);
    END LOOP;
END $$;

-- Seed existing scopes so graph publication can lock and compare the complete baseline.
INSERT INTO cache_scope_revisions(scope, revision, updated_at, last_transaction)
SELECT DISTINCT 'project:' || btrim(project_id), 1, clock_timestamp(), pg_current_xact_id() FROM memory_items
ON CONFLICT DO NOTHING;
SELECT bump_cache_scope_revision('global');
SELECT bump_cache_scope_revision('security');

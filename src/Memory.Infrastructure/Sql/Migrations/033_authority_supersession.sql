-- B1: add an explicit, backward-compatible authority contract to durable memory.
-- Legacy status and metadata fields remain available to existing callers; these
-- columns provide stable, queryable authority and replacement-chain semantics.
ALTER TABLE memory_items
    ADD COLUMN IF NOT EXISTS authority_state TEXT NOT NULL DEFAULT 'Current',
    ADD COLUMN IF NOT EXISTS supersedes_id UUID NULL,
    ADD COLUMN IF NOT EXISTS superseded_by_id UUID NULL,
    ADD COLUMN IF NOT EXISTS valid_from TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS valid_until TIMESTAMPTZ NULL,
    ADD COLUMN IF NOT EXISTS successor_evidence_id UUID NULL,
    ADD COLUMN IF NOT EXISTS successor_evidence_ref TEXT NOT NULL DEFAULT '';

-- Existing lifecycle values are preserved while the new authority state is
-- initialized once. Lifecycle (Active/Stale/Archived) is deliberately
-- orthogonal to authority: only an explicit legacy supersession (or pending
-- authority claim) is strong enough to infer a non-Current authority state.
-- Stale and archived rows therefore remain Current unless their explicit
-- authority fields or metadata prove otherwise; no history is deleted here.
UPDATE memory_items
SET authority_state = CASE LOWER(COALESCE(status, ''))
    WHEN 'superseded' THEN 'Superseded'
    WHEN 'pending' THEN 'Pending'
    ELSE 'Current'
END
WHERE authority_state = 'Current';

UPDATE memory_items
SET valid_from = created_at
WHERE valid_from IS NULL;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'memory_items'::regclass
          AND conname = 'ck_memory_items_authority_state') THEN
        ALTER TABLE memory_items
            ADD CONSTRAINT ck_memory_items_authority_state
            CHECK (authority_state IN ('Current', 'Superseded', 'Historical', 'Pending'));
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'memory_items'::regclass
          AND conname = 'ck_memory_items_authority_window') THEN
        ALTER TABLE memory_items
            ADD CONSTRAINT ck_memory_items_authority_window
            CHECK (valid_until IS NULL OR valid_from IS NULL OR valid_until > valid_from);
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'memory_items'::regclass
          AND conname = 'ck_memory_items_authority_self_reference') THEN
        ALTER TABLE memory_items
            ADD CONSTRAINT ck_memory_items_authority_self_reference
            CHECK ((supersedes_id IS NULL OR supersedes_id <> id)
                   AND (superseded_by_id IS NULL OR superseded_by_id <> id)
                   AND (successor_evidence_id IS NULL OR successor_evidence_id <> id))
            NOT VALID;
        ALTER TABLE memory_items
            VALIDATE CONSTRAINT ck_memory_items_authority_self_reference;
    END IF;
END;
$$;

DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'memory_items'::regclass
          AND conname = 'fk_memory_items_supersedes') THEN
        ALTER TABLE memory_items
            ADD CONSTRAINT fk_memory_items_supersedes
            FOREIGN KEY (supersedes_id)
            REFERENCES memory_items(id)
            ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'memory_items'::regclass
          AND conname = 'fk_memory_items_superseded_by') THEN
        ALTER TABLE memory_items
            ADD CONSTRAINT fk_memory_items_superseded_by
            FOREIGN KEY (superseded_by_id)
            REFERENCES memory_items(id)
            ON DELETE RESTRICT;
    END IF;

    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'memory_items'::regclass
          AND conname = 'fk_memory_items_successor_evidence') THEN
        ALTER TABLE memory_items
            ADD CONSTRAINT fk_memory_items_successor_evidence
            FOREIGN KEY (successor_evidence_id)
            REFERENCES memory_items(id)
            ON DELETE RESTRICT;
    END IF;
END;
$$;

-- At most one direct successor/predecessor is allowed for a memory item. The
-- partial unique indexes make retries idempotent and provide a fast duplicate
-- edge check; the deferred constraint trigger below validates the reciprocal
-- pair and the complete directed graph at transaction commit.
-- Run an explicit preflight first so a legacy or manually repaired database
-- fails with a stable reason instead of a generic CREATE INDEX error. The
-- migration never chooses a relationship winner or drops authority history.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM memory_items
        WHERE supersedes_id IS NOT NULL
        GROUP BY supersedes_id
        HAVING COUNT(*) > 1) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23505',
            MESSAGE = 'memory authority predecessor fork preflight failed';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM memory_items
        WHERE superseded_by_id IS NOT NULL
        GROUP BY superseded_by_id
        HAVING COUNT(*) > 1) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23505',
            MESSAGE = 'memory authority successor fork preflight failed';
    END IF;
END;
$$;

CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_items_supersedes_id
    ON memory_items(supersedes_id)
    WHERE supersedes_id IS NOT NULL;

CREATE UNIQUE INDEX IF NOT EXISTS ux_memory_items_superseded_by_id
    ON memory_items(superseded_by_id)
    WHERE superseded_by_id IS NOT NULL;

CREATE INDEX IF NOT EXISTS ix_memory_items_authority_window
    ON memory_items(tenant_id, owner_user_id, project_id, authority_state, valid_from, valid_until);

CREATE INDEX IF NOT EXISTS ix_memory_items_successor_evidence
    ON memory_items(tenant_id, owner_user_id, project_id, successor_evidence_id)
    WHERE successor_evidence_id IS NOT NULL;

-- Do not install a deferred validator on top of already-invalid committed
-- data. Existing one-sided, cross-scope, or cyclic relationships must be
-- repaired explicitly; silently accepting them would leave the database in a
-- state that the new trigger promises never to commit.
DO $$
BEGIN
    IF EXISTS (
        SELECT 1
        FROM memory_items item
        WHERE item.supersedes_id IS NOT NULL
          AND NOT EXISTS (
              SELECT 1
              FROM memory_items target
              WHERE target.id = item.supersedes_id
                AND target.superseded_by_id = item.id)) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'existing memory authority replacement is missing its reciprocal successor link';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM memory_items item
        WHERE item.superseded_by_id IS NOT NULL
          AND NOT EXISTS (
              SELECT 1
              FROM memory_items target
              WHERE target.id = item.superseded_by_id
                AND target.supersedes_id = item.id)) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'existing memory authority replacement is missing its reciprocal predecessor link';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM memory_items item
        JOIN memory_items target ON target.id = item.supersedes_id
        WHERE item.supersedes_id IS NOT NULL
          AND (item.tenant_id IS NULL
               OR item.owner_user_id IS NULL
               OR target.tenant_id IS NULL
               OR target.owner_user_id IS NULL
               OR item.project_id IS NULL
               OR target.project_id IS NULL
               OR item.scope IS NULL
               OR target.scope IS NULL
               OR item.tenant_id IS DISTINCT FROM target.tenant_id
               OR item.owner_user_id IS DISTINCT FROM target.owner_user_id
               OR item.project_id IS DISTINCT FROM target.project_id
               OR item.scope IS DISTINCT FROM target.scope)) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'existing memory authority replacement crosses tenant, owner, project, or memory scope';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM memory_items item
        JOIN memory_items target ON target.id = item.superseded_by_id
        WHERE item.superseded_by_id IS NOT NULL
          AND (item.tenant_id IS NULL
               OR item.owner_user_id IS NULL
               OR target.tenant_id IS NULL
               OR target.owner_user_id IS NULL
               OR item.project_id IS NULL
               OR target.project_id IS NULL
               OR item.scope IS NULL
               OR target.scope IS NULL
               OR item.tenant_id IS DISTINCT FROM target.tenant_id
               OR item.owner_user_id IS DISTINCT FROM target.owner_user_id
               OR item.project_id IS DISTINCT FROM target.project_id
               OR item.scope IS DISTINCT FROM target.scope)) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'existing memory authority successor crosses tenant, owner, project, or memory scope';
    END IF;

    IF EXISTS (
        WITH RECURSIVE authority_edges(predecessor_id, successor_id) AS (
            SELECT item.id, item.superseded_by_id
            FROM memory_items item
            WHERE item.superseded_by_id IS NOT NULL
            UNION
            SELECT item.supersedes_id, item.id
            FROM memory_items item
            WHERE item.supersedes_id IS NOT NULL
        ),
        authority_walk(start_id, current_id, path) AS (
            SELECT edge.predecessor_id,
                   edge.successor_id,
                   ARRAY[edge.predecessor_id, edge.successor_id]::UUID[]
            FROM authority_edges edge
            UNION ALL
            SELECT walk.start_id,
                   edge.successor_id,
                   walk.path || edge.successor_id
            FROM authority_walk walk
            JOIN authority_edges edge
              ON edge.predecessor_id = walk.current_id
            WHERE walk.current_id <> walk.start_id
              AND (edge.successor_id = walk.start_id
                   OR NOT (edge.successor_id = ANY(walk.path)))
        )
        SELECT 1
        FROM authority_walk
        WHERE current_id = start_id) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'existing memory authority replacement chain contains a cycle';
    END IF;
END;
$$;

-- Self-references and successor evidence must stay in the same tenant, owner,
-- project, and memory scope. Unknown tenant/owner identity is rejected for a
-- relationship; two NULL values must not be treated as an implicit match.
-- A transaction-scoped advisory lock serializes authority relationship writes so
-- the deferred commit validation cannot miss a concurrent fork or cycle.
CREATE OR REPLACE FUNCTION memory_items_authority_scope_guard()
RETURNS TRIGGER AS $$
BEGIN
    -- Relationship/scope mutations use one transaction-scoped lock. Plain
    -- memory inserts without a relationship do not need serialization. This
    -- deliberately favors correctness over relationship write parallelism
    -- because authority transitions are rare and a per-component lock could
    -- deadlock when one transaction updates both sides in a different row
    -- order.
    IF TG_OP = 'UPDATE'
       OR NEW.supersedes_id IS NOT NULL
       OR NEW.superseded_by_id IS NOT NULL
       OR NEW.successor_evidence_id IS NOT NULL THEN
        PERFORM pg_advisory_xact_lock(941222);
    END IF;

    IF NEW.supersedes_id IS NOT NULL THEN
        IF NEW.supersedes_id = NEW.id THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory authority replacement cannot reference itself';
        END IF;

        IF NEW.tenant_id IS NULL OR NEW.owner_user_id IS NULL THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory authority replacement requires tenant and owner scope';
        END IF;

        IF EXISTS (
            SELECT 1
            FROM memory_items target
            WHERE target.id = NEW.supersedes_id
              AND (target.tenant_id IS NULL
                   OR target.owner_user_id IS NULL
                   OR target.project_id IS NULL
                   OR target.scope IS NULL
                   OR target.tenant_id IS DISTINCT FROM NEW.tenant_id
                   OR target.owner_user_id IS DISTINCT FROM NEW.owner_user_id
                   OR target.project_id IS DISTINCT FROM NEW.project_id
                   OR target.scope IS DISTINCT FROM NEW.scope)) THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory authority replacement must remain within the same tenant, owner, project, and scope';
        END IF;

        IF EXISTS (
            WITH RECURSIVE authority_chain(id) AS (
                SELECT NEW.supersedes_id
                UNION
                SELECT item.supersedes_id
                FROM memory_items item
                JOIN authority_chain chain ON chain.id = item.id
                WHERE item.supersedes_id IS NOT NULL
            )
            SELECT 1
            FROM authority_chain
            WHERE id = NEW.id) THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory authority replacement cannot create a cycle';
        END IF;
    END IF;

    IF NEW.superseded_by_id IS NOT NULL THEN
        IF NEW.superseded_by_id = NEW.id THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory authority successor cannot reference itself';
        END IF;

        IF NEW.tenant_id IS NULL OR NEW.owner_user_id IS NULL THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory authority successor requires tenant and owner scope';
        END IF;

        IF EXISTS (
            SELECT 1
            FROM memory_items target
            WHERE target.id = NEW.superseded_by_id
              AND (target.tenant_id IS NULL
                   OR target.owner_user_id IS NULL
                   OR target.project_id IS NULL
                   OR target.scope IS NULL
                   OR target.tenant_id IS DISTINCT FROM NEW.tenant_id
                   OR target.owner_user_id IS DISTINCT FROM NEW.owner_user_id
                   OR target.project_id IS DISTINCT FROM NEW.project_id
                   OR target.scope IS DISTINCT FROM NEW.scope)) THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory authority successor must remain within the same tenant, owner, project, and scope';
        END IF;

        IF EXISTS (
            WITH RECURSIVE authority_chain(id) AS (
                SELECT NEW.superseded_by_id
                UNION
                SELECT item.superseded_by_id
                FROM memory_items item
                JOIN authority_chain chain ON chain.id = item.id
                WHERE item.superseded_by_id IS NOT NULL
            )
            SELECT 1
            FROM authority_chain
            WHERE id = NEW.id) THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory authority successor cannot create a cycle';
        END IF;
    END IF;

    IF NEW.successor_evidence_id IS NOT NULL THEN
        IF NEW.successor_evidence_id = NEW.id THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory successor evidence cannot reference itself';
        END IF;

        IF NEW.tenant_id IS NULL OR NEW.owner_user_id IS NULL THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory successor evidence requires tenant and owner scope';
        END IF;

        IF EXISTS (
            SELECT 1
            FROM memory_items evidence
            WHERE evidence.id = NEW.successor_evidence_id
              AND (evidence.tenant_id IS NULL
                   OR evidence.owner_user_id IS NULL
                   OR evidence.project_id IS NULL
                   OR evidence.scope IS NULL
                   OR evidence.tenant_id IS DISTINCT FROM NEW.tenant_id
                   OR evidence.owner_user_id IS DISTINCT FROM NEW.owner_user_id
                   OR evidence.project_id IS DISTINCT FROM NEW.project_id
                   OR evidence.scope IS DISTINCT FROM NEW.scope)) THEN
            RAISE EXCEPTION USING
                ERRCODE = '23514',
                MESSAGE = 'memory successor evidence must remain within the same tenant, owner, project, and scope';
        END IF;
    END IF;

    IF TG_OP = 'UPDATE'
       AND (NEW.tenant_id IS DISTINCT FROM OLD.tenant_id
            OR NEW.owner_user_id IS DISTINCT FROM OLD.owner_user_id
            OR NEW.project_id IS DISTINCT FROM OLD.project_id
            OR NEW.scope IS DISTINCT FROM OLD.scope)
       AND EXISTS (
            SELECT 1
            FROM memory_items linked
            WHERE (linked.supersedes_id = NEW.id
                   OR linked.superseded_by_id = NEW.id
                   OR linked.successor_evidence_id = NEW.id)
              AND (NEW.tenant_id IS NULL
                   OR NEW.owner_user_id IS NULL
                   OR NEW.project_id IS NULL
                   OR NEW.scope IS NULL
                   OR linked.tenant_id IS NULL
                   OR linked.owner_user_id IS NULL
                   OR linked.project_id IS NULL
                   OR linked.scope IS NULL
                   OR linked.tenant_id IS DISTINCT FROM NEW.tenant_id
                   OR linked.owner_user_id IS DISTINCT FROM NEW.owner_user_id
                   OR linked.project_id IS DISTINCT FROM NEW.project_id
                   OR linked.scope IS DISTINCT FROM NEW.scope)) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'memory authority chain cannot cross tenant, owner, project, or memory scope';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

-- A replacement relationship is represented twice for compatibility with the
-- application model: predecessor.superseded_by_id = successor.id and
-- successor.supersedes_id = predecessor.id.  The application may write those
-- two rows in either order inside one SaveChanges/transaction, so this is a
-- deferred constraint trigger rather than an immediate CHECK-like trigger.
-- It validates the final committed graph, not an intermediate row state.
CREATE OR REPLACE FUNCTION memory_items_authority_chain_guard()
RETURNS TRIGGER AS $$
BEGIN
    IF TG_OP = 'INSERT'
       AND NEW.supersedes_id IS NULL
       AND NEW.superseded_by_id IS NULL
       AND NEW.successor_evidence_id IS NULL THEN
        RETURN NEW;
    END IF;

    IF TG_OP = 'UPDATE'
       AND NEW.supersedes_id IS NULL
       AND NEW.superseded_by_id IS NULL
       AND NEW.successor_evidence_id IS NULL
       AND OLD.supersedes_id IS NULL
       AND OLD.superseded_by_id IS NULL
       AND OLD.successor_evidence_id IS NULL THEN
        RETURN NEW;
    END IF;

    -- The scope trigger already takes this lock for relationship mutations;
    -- taking it here as well makes direct SET CONSTRAINTS/trigger invocation
    -- safe and documents the serialization boundary for concurrent writers.
    PERFORM pg_advisory_xact_lock(941222);

    IF NEW.supersedes_id IS NOT NULL
       AND EXISTS (
            SELECT 1
            FROM memory_items target
            WHERE target.id = NEW.supersedes_id
              AND target.superseded_by_id IS DISTINCT FROM NEW.id) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'memory authority replacement must have a reciprocal successor link';
    END IF;

    IF NEW.superseded_by_id IS NOT NULL
       AND EXISTS (
            SELECT 1
            FROM memory_items target
            WHERE target.id = NEW.superseded_by_id
              AND target.supersedes_id IS DISTINCT FROM NEW.id) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'memory authority replacement must have a reciprocal predecessor link';
    END IF;

    -- Also inspect rows that point at NEW. This catches mixed-column forks
    -- (one row claims two different successors/predecessors) even when NEW's
    -- own pointer is NULL because the other side was changed in isolation.
    IF EXISTS (
        SELECT 1
        FROM memory_items linked
        WHERE linked.supersedes_id = NEW.id
          AND NEW.superseded_by_id IS DISTINCT FROM linked.id) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'memory authority replacement chain contains a fork';
    END IF;

    IF EXISTS (
        SELECT 1
        FROM memory_items linked
        WHERE linked.superseded_by_id = NEW.id
          AND NEW.supersedes_id IS DISTINCT FROM linked.id) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'memory authority replacement chain contains a fork';
    END IF;

    -- Normalize both pointer representations into predecessor -> successor
    -- edges. UNION removes the duplicate edge created by a valid reciprocal
    -- pair. The path guard makes mixed-field cycles finite and detectable.
    IF EXISTS (
        WITH RECURSIVE authority_edges(predecessor_id, successor_id) AS (
            SELECT item.id, item.superseded_by_id
            FROM memory_items item
            WHERE item.superseded_by_id IS NOT NULL
            UNION
            SELECT item.supersedes_id, item.id
            FROM memory_items item
            WHERE item.supersedes_id IS NOT NULL
        ),
        authority_walk(start_id, current_id, path) AS (
            SELECT edge.predecessor_id,
                   edge.successor_id,
                   ARRAY[edge.predecessor_id, edge.successor_id]::UUID[]
            FROM authority_edges edge
            UNION ALL
            SELECT walk.start_id,
                   edge.successor_id,
                   walk.path || edge.successor_id
            FROM authority_walk walk
            JOIN authority_edges edge
              ON edge.predecessor_id = walk.current_id
            WHERE walk.current_id <> walk.start_id
              AND (edge.successor_id = walk.start_id
                   OR NOT (edge.successor_id = ANY(walk.path)))
        )
        SELECT 1
        FROM authority_walk
        WHERE current_id = start_id) THEN
        RAISE EXCEPTION USING
            ERRCODE = '23514',
            MESSAGE = 'memory authority replacement chain cannot contain a cycle';
    END IF;

    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS memory_items_authority_scope_guard ON memory_items;
CREATE TRIGGER memory_items_authority_scope_guard
    BEFORE INSERT OR UPDATE OF tenant_id, owner_user_id, project_id, scope, supersedes_id, superseded_by_id, successor_evidence_id
    ON memory_items
    FOR EACH ROW EXECUTE FUNCTION memory_items_authority_scope_guard();

DROP TRIGGER IF EXISTS memory_items_authority_chain_guard ON memory_items;
CREATE CONSTRAINT TRIGGER memory_items_authority_chain_guard
    AFTER INSERT OR UPDATE OF tenant_id, owner_user_id, project_id, scope,
        supersedes_id, superseded_by_id, successor_evidence_id
    ON memory_items
    DEFERRABLE INITIALLY DEFERRED
    FOR EACH ROW EXECUTE FUNCTION memory_items_authority_chain_guard();

COMMENT ON COLUMN memory_items.authority_state IS
    'Explicit authority state: Current, Superseded, Historical, or Pending; legacy status remains compatible.';
COMMENT ON COLUMN memory_items.supersedes_id IS
    'Predecessor memory replaced by this item; same tenant/owner/project and restricted deletion.';
COMMENT ON COLUMN memory_items.superseded_by_id IS
    'Successor memory that replaced this item; same tenant/owner/project and restricted deletion.';
COMMENT ON COLUMN memory_items.successor_evidence_ref IS
    'Stable external evidence reference for the successor relationship when no memory item is available.';

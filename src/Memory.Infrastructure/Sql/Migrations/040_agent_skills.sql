CREATE TABLE IF NOT EXISTS skills (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    stable_key text NOT NULL,
    name text NOT NULL,
    description text NOT NULL DEFAULT '',
    when_to_use text NOT NULL DEFAULT '',
    tags text[] NOT NULL DEFAULT ARRAY[]::text[],
    aliases text[] NOT NULL DEFAULT ARRAY[]::text[],
    license text NOT NULL,
    maintainers_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    risk_level text NOT NULL,
    metadata_version bigint NOT NULL DEFAULT 1,
    default_version_id uuid NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    archived_at timestamptz NULL,
    CONSTRAINT ck_skills_stable_key CHECK (stable_key = lower(stable_key) AND length(stable_key) BETWEEN 2 AND 120),
    CONSTRAINT ck_skills_metadata_version CHECK (metadata_version > 0)
);

CREATE UNIQUE INDEX IF NOT EXISTS ux_skills_tenant_stable_key
    ON skills (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), stable_key);
CREATE INDEX IF NOT EXISTS ix_skills_tenant_name ON skills (tenant_id, name);

CREATE TABLE IF NOT EXISTS skill_versions (
    id uuid PRIMARY KEY,
    skill_id uuid NOT NULL REFERENCES skills(id) ON DELETE RESTRICT,
    version text NOT NULL,
    status text NOT NULL,
    content_hash text NOT NULL,
    bundle_json jsonb NOT NULL,
    search_text text NOT NULL DEFAULT '',
    compatibility_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    required_capabilities text[] NOT NULL DEFAULT ARRAY[]::text[],
    required_tools text[] NOT NULL DEFAULT ARRAY[]::text[],
    allowed_actions text[] NOT NULL DEFAULT ARRAY[]::text[],
    requires_network boolean NOT NULL DEFAULT false,
    requires_secrets boolean NOT NULL DEFAULT false,
    source_kind text NOT NULL,
    source_ref text NOT NULL,
    source_revision text NOT NULL DEFAULT '',
    trust_level text NOT NULL,
    signature_algorithm text NOT NULL DEFAULT '',
    signature_value text NOT NULL DEFAULT '',
    signature_verified boolean NOT NULL DEFAULT false,
    publish_evidence_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    published_at timestamptz NULL,
    deprecated_at timestamptz NULL,
    revoked_at timestamptz NULL,
    archived_at timestamptz NULL,
    CONSTRAINT ux_skill_versions_version UNIQUE (skill_id, version),
    CONSTRAINT ux_skill_versions_hash UNIQUE (skill_id, content_hash),
    CONSTRAINT ck_skill_versions_hash CHECK (content_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_skill_versions_status CHECK (status IN ('Draft','Published','Deprecated','Revoked','Archived'))
);
CREATE INDEX IF NOT EXISTS ix_skill_versions_status_updated ON skill_versions (status, updated_at DESC);

ALTER TABLE skills DROP CONSTRAINT IF EXISTS fk_skills_default_version;
ALTER TABLE skills ADD CONSTRAINT fk_skills_default_version
    FOREIGN KEY (default_version_id) REFERENCES skill_versions(id) ON DELETE SET NULL;

CREATE OR REPLACE FUNCTION prevent_published_skill_version_mutation() RETURNS trigger AS $$
BEGIN
    IF OLD.status IN ('Published', 'Deprecated', 'Revoked', 'Archived') AND (
        NEW.skill_id IS DISTINCT FROM OLD.skill_id OR
        NEW.version IS DISTINCT FROM OLD.version OR
        NEW.content_hash IS DISTINCT FROM OLD.content_hash OR
        NEW.bundle_json IS DISTINCT FROM OLD.bundle_json OR
        NEW.search_text IS DISTINCT FROM OLD.search_text OR
        NEW.compatibility_json IS DISTINCT FROM OLD.compatibility_json OR
        NEW.required_capabilities IS DISTINCT FROM OLD.required_capabilities OR
        NEW.required_tools IS DISTINCT FROM OLD.required_tools OR
        NEW.allowed_actions IS DISTINCT FROM OLD.allowed_actions OR
        NEW.requires_network IS DISTINCT FROM OLD.requires_network OR
        NEW.requires_secrets IS DISTINCT FROM OLD.requires_secrets OR
        NEW.source_kind IS DISTINCT FROM OLD.source_kind OR
        NEW.source_ref IS DISTINCT FROM OLD.source_ref OR
        NEW.source_revision IS DISTINCT FROM OLD.source_revision OR
        NEW.trust_level IS DISTINCT FROM OLD.trust_level OR
        NEW.signature_algorithm IS DISTINCT FROM OLD.signature_algorithm OR
        NEW.signature_value IS DISTINCT FROM OLD.signature_value OR
        NEW.signature_verified IS DISTINCT FROM OLD.signature_verified OR
        NEW.publish_evidence_json IS DISTINCT FROM OLD.publish_evidence_json OR
        NEW.created_at IS DISTINCT FROM OLD.created_at OR
        NEW.published_at IS DISTINCT FROM OLD.published_at
    ) THEN
        RAISE EXCEPTION 'Published SkillVersion content and provenance are immutable';
    END IF;
    RETURN NEW;
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS trg_skill_versions_immutable ON skill_versions;
CREATE TRIGGER trg_skill_versions_immutable
    BEFORE UPDATE ON skill_versions
    FOR EACH ROW EXECUTE FUNCTION prevent_published_skill_version_mutation();

CREATE TABLE IF NOT EXISTS skill_version_dependencies (
    id uuid PRIMARY KEY,
    skill_version_id uuid NOT NULL REFERENCES skill_versions(id) ON DELETE CASCADE,
    target_skill_id uuid NOT NULL REFERENCES skills(id) ON DELETE RESTRICT,
    kind text NOT NULL,
    version_constraint text NOT NULL,
    CONSTRAINT ux_skill_version_dependencies UNIQUE (skill_version_id, target_skill_id, kind),
    CONSTRAINT ck_skill_dependency_self CHECK (skill_version_id IS NOT NULL),
    CONSTRAINT ck_skill_dependency_kind CHECK (kind IN ('Requires','Optional','ConflictsWith'))
);

CREATE TABLE IF NOT EXISTS skill_bindings (
    id uuid PRIMARY KEY,
    skill_id uuid NOT NULL REFERENCES skills(id) ON DELETE CASCADE,
    scope text NOT NULL,
    scope_value text NOT NULL DEFAULT '',
    mode text NOT NULL,
    version_constraint text NOT NULL DEFAULT '*',
    revision bigint NOT NULL DEFAULT 1,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ux_skill_bindings UNIQUE (skill_id, scope, scope_value),
    CONSTRAINT ck_skill_binding_revision CHECK (revision > 0),
    CONSTRAINT ck_skill_binding_scope CHECK (scope IN ('Tenant','Project','Repository','AgentType','Execution')),
    CONSTRAINT ck_skill_binding_mode CHECK (mode IN ('Recommended','Auto','Required','AllowList','Disabled'))
);

CREATE TABLE IF NOT EXISTS skill_search_generations (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    search_profile_version text NOT NULL,
    embedding_model_id text NOT NULL,
    embedding_model_version text NOT NULL,
    threshold numeric(8,6) NOT NULL,
    status text NOT NULL,
    benchmark_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    activated_at timestamptz NULL,
    CONSTRAINT ck_skill_search_threshold CHECK (threshold >= 0 AND threshold <= 1),
    CONSTRAINT ck_skill_search_generation_status CHECK (status IN ('Building','Validating','Active','Failed','RolledBack','Retired'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_skill_search_generation_active
    ON skill_search_generations (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid))
    WHERE status = 'Active';

CREATE TABLE IF NOT EXISTS skill_search_documents (
    id uuid PRIMARY KEY,
    generation_id uuid NOT NULL REFERENCES skill_search_generations(id) ON DELETE CASCADE,
    skill_version_id uuid NOT NULL REFERENCES skill_versions(id) ON DELETE CASCADE,
    search_text text NOT NULL,
    terms_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    embedding_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL,
    CONSTRAINT ux_skill_search_documents UNIQUE (generation_id, skill_version_id)
);
CREATE INDEX IF NOT EXISTS ix_skill_search_documents_fts
    ON skill_search_documents USING gin (to_tsvector('simple', search_text));

CREATE TABLE IF NOT EXISTS skill_resolutions (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    execution_id uuid NOT NULL,
    work_item_id uuid NULL,
    project_id text NOT NULL,
    repository_id text NOT NULL,
    agent_type text NOT NULL,
    round integer NOT NULL,
    max_search_rounds integer NOT NULL,
    max_selected_skills integer NOT NULL,
    query_hash text NOT NULL,
    query_terms_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    available_capabilities_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    available_tools_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    allowed_actions_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    maximum_risk text NOT NULL DEFAULT 'Medium',
    search_generation_id uuid NOT NULL REFERENCES skill_search_generations(id) ON DELETE RESTRICT,
    status text NOT NULL,
    idempotency_key text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ux_skill_resolution_round UNIQUE (execution_id, round),
    CONSTRAINT ck_skill_resolution_round CHECK (round > 0 AND max_search_rounds > 0 AND round <= max_search_rounds),
    CONSTRAINT ck_skill_resolution_status CHECK (status IN ('Searching','NoApplicableSkill','Selected','RequiresHumanDecision','Cancelled','Completed'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_skill_resolution_idempotency
    ON skill_resolutions (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid), idempotency_key);

CREATE TABLE IF NOT EXISTS skill_resolution_candidates (
    id uuid PRIMARY KEY,
    resolution_id uuid NOT NULL REFERENCES skill_resolutions(id) ON DELETE CASCADE,
    skill_version_id uuid NOT NULL REFERENCES skill_versions(id) ON DELETE RESTRICT,
    rank integer NOT NULL,
    score numeric(8,6) NOT NULL,
    threshold numeric(8,6) NOT NULL,
    match_reasons_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    created_at timestamptz NOT NULL,
    CONSTRAINT ux_skill_resolution_candidates UNIQUE (resolution_id, skill_version_id),
    CONSTRAINT ck_skill_candidate_rank CHECK (rank > 0),
    CONSTRAINT ck_skill_candidate_score CHECK (score >= 0 AND score <= 1 AND threshold >= 0 AND threshold <= 1)
);

CREATE TABLE IF NOT EXISTS skill_resolution_pins (
    id uuid PRIMARY KEY,
    resolution_id uuid NOT NULL REFERENCES skill_resolutions(id) ON DELETE CASCADE,
    skill_version_id uuid NOT NULL REFERENCES skill_versions(id) ON DELETE RESTRICT,
    content_hash text NOT NULL,
    is_dependency boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL,
    released_at timestamptz NULL,
    CONSTRAINT ux_skill_resolution_pins UNIQUE (resolution_id, skill_version_id)
);

CREATE TABLE IF NOT EXISTS skill_telemetry_events (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    skill_id uuid NOT NULL REFERENCES skills(id) ON DELETE RESTRICT,
    skill_version_id uuid NOT NULL REFERENCES skill_versions(id) ON DELETE RESTRICT,
    execution_id uuid NOT NULL,
    work_item_id uuid NULL,
    resolution_id uuid NULL REFERENCES skill_resolutions(id) ON DELETE SET NULL,
    resolution_round integer NOT NULL,
    event_type text NOT NULL,
    rejection_stage text NULL,
    reason_class text NULL,
    reason_text text NOT NULL DEFAULT '',
    evidence_json jsonb NOT NULL DEFAULT '[]'::jsonb,
    query_hash text NOT NULL DEFAULT '',
    candidate_rank integer NULL,
    candidate_score numeric(8,6) NULL,
    threshold numeric(8,6) NULL,
    project_id text NOT NULL,
    repository_id text NOT NULL,
    agent_type text NOT NULL,
    idempotency_key text NOT NULL,
    occurred_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT ck_skill_telemetry_event_type CHECK (event_type IN ('SearchImpression','Selected','Rejected','SelectedThenReleased','Materialized','InvocationStarted','InvocationSucceeded','InvocationFailed','Cancellation'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_skill_telemetry_idempotency
    ON skill_telemetry_events (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid), idempotency_key);
CREATE INDEX IF NOT EXISTS ix_skill_telemetry_version_type_time ON skill_telemetry_events (skill_version_id, event_type, occurred_at DESC);
CREATE INDEX IF NOT EXISTS ix_skill_telemetry_scope_time ON skill_telemetry_events (project_id, repository_id, agent_type, occurred_at DESC);

CREATE TABLE IF NOT EXISTS skill_telemetry_daily_aggregates (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    aggregate_date date NOT NULL,
    skill_id uuid NOT NULL REFERENCES skills(id) ON DELETE RESTRICT,
    skill_version_id uuid NOT NULL REFERENCES skill_versions(id) ON DELETE RESTRICT,
    project_id text NOT NULL,
    repository_id text NOT NULL,
    agent_type text NOT NULL,
    event_type text NOT NULL,
    rejection_stage text NULL,
    reason_class text NULL,
    event_count integer NOT NULL,
    last_occurred_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ck_skill_telemetry_aggregate_count CHECK (event_count > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_skill_telemetry_daily_aggregate_key
    ON skill_telemetry_daily_aggregates (
        COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid),
        COALESCE(owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid),
        aggregate_date, skill_id, skill_version_id, project_id, repository_id, agent_type,
        event_type, COALESCE(rejection_stage, ''), COALESCE(reason_class, ''));
CREATE INDEX IF NOT EXISTS ix_skill_telemetry_aggregate_skill_date ON skill_telemetry_daily_aggregates (skill_id, skill_version_id, aggregate_date DESC);
CREATE INDEX IF NOT EXISTS ix_skill_telemetry_aggregate_scope_date ON skill_telemetry_daily_aggregates (project_id, repository_id, agent_type, aggregate_date DESC);

CREATE TABLE IF NOT EXISTS skill_telemetry_aggregation_ledger (
    event_id uuid PRIMARY KEY REFERENCES skill_telemetry_events(id) ON DELETE CASCADE,
    aggregated_at timestamptz NOT NULL
);

CREATE TABLE IF NOT EXISTS skill_telemetry_reconciliation_runs (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    idempotency_key text NOT NULL,
    aggregated_event_count integer NOT NULL,
    aggregate_row_count integer NOT NULL,
    deleted_raw_event_count integer NOT NULL,
    deleted_aggregate_row_count integer NOT NULL,
    protected_raw_event_count integer NOT NULL,
    raw_retention_days integer NOT NULL,
    aggregate_retention_days integer NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT ck_skill_telemetry_reconcile_counts CHECK (aggregated_event_count >= 0 AND aggregate_row_count >= 0 AND deleted_raw_event_count >= 0 AND deleted_aggregate_row_count >= 0 AND protected_raw_event_count >= 0),
    CONSTRAINT ck_skill_telemetry_reconcile_retention CHECK (raw_retention_days >= 90 AND aggregate_retention_days >= raw_retention_days)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_skill_telemetry_reconcile_idempotency
    ON skill_telemetry_reconciliation_runs (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid), idempotency_key);

CREATE TABLE IF NOT EXISTS skill_materializations (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    execution_id uuid NOT NULL,
    resolution_id uuid NOT NULL REFERENCES skill_resolutions(id) ON DELETE RESTRICT,
    skill_version_id uuid NOT NULL REFERENCES skill_versions(id) ON DELETE RESTRICT,
    content_hash text NOT NULL,
    relative_path text NOT NULL,
    status text NOT NULL,
    failure_reason text NOT NULL DEFAULT '',
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    cleaned_at timestamptz NULL,
    CONSTRAINT ck_skill_materialization_status CHECK (status IN ('Active','Revoked','Cleaned','Failed'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_skill_materialization_execution_version
    ON skill_materializations (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid), execution_id, skill_version_id, content_hash);

CREATE TABLE IF NOT EXISTS skill_metadata_proposals (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    skill_id uuid NOT NULL REFERENCES skills(id) ON DELETE RESTRICT,
    expected_metadata_version bigint NOT NULL,
    expected_metadata_hash text NOT NULL,
    proposed_patch_json jsonb NOT NULL,
    evidence_json jsonb NOT NULL,
    confidence numeric(8,6) NOT NULL,
    status text NOT NULL,
    governance_run_id text NOT NULL,
    idempotency_key text NOT NULL,
    created_at timestamptz NOT NULL,
    updated_at timestamptz NOT NULL,
    CONSTRAINT ck_skill_proposal_status CHECK (status IN ('Pending','Applied','Rejected','Stale','Failed')),
    CONSTRAINT ck_skill_proposal_confidence CHECK (confidence >= 0 AND confidence <= 1)
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_skill_metadata_proposal_idempotency
    ON skill_metadata_proposals (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid), idempotency_key);
CREATE INDEX IF NOT EXISTS ix_skill_metadata_proposals_status ON skill_metadata_proposals (skill_id, status, updated_at DESC);

CREATE TABLE IF NOT EXISTS skill_source_observations (
    id uuid PRIMARY KEY,
    tenant_id uuid NULL,
    owner_user_id uuid NULL,
    skill_id uuid NOT NULL REFERENCES skills(id) ON DELETE RESTRICT,
    source_ref text NOT NULL,
    observed_revision text NOT NULL,
    observed_content_hash text NOT NULL,
    status text NOT NULL,
    source_available boolean NOT NULL,
    signature_verified boolean NOT NULL,
    evidence_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    idempotency_key text NOT NULL,
    observed_at timestamptz NOT NULL,
    created_at timestamptz NOT NULL,
    CONSTRAINT ck_skill_source_observation_status CHECK (status IN ('InSync','Changed','Deleted','TrustChanged','Compromised'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_skill_source_observation_idempotency
    ON skill_source_observations (COALESCE(tenant_id, '00000000-0000-0000-0000-000000000000'::uuid), COALESCE(owner_user_id, '00000000-0000-0000-0000-000000000000'::uuid), idempotency_key);
CREATE INDEX IF NOT EXISTS ix_skill_source_observations_skill_time ON skill_source_observations (skill_id, observed_at DESC);

ALTER TABLE governance_run_receipts
    ADD COLUMN IF NOT EXISTS skill_coverage_json jsonb NOT NULL DEFAULT '{}'::jsonb,
    ADD COLUMN IF NOT EXISTS skill_signal_counts_json jsonb NOT NULL DEFAULT '{}'::jsonb;

ALTER TABLE canonical_tag_definitions ADD COLUMN IF NOT EXISTS status TEXT;
ALTER TABLE canonical_tag_definitions ADD COLUMN IF NOT EXISTS redirect_to_id UUID NULL;
ALTER TABLE canonical_tag_definitions ADD COLUMN IF NOT EXISTS revision BIGINT;
ALTER TABLE canonical_tag_definitions ADD COLUMN IF NOT EXISTS last_used_at TIMESTAMPTZ NULL;
ALTER TABLE canonical_tag_definitions ADD COLUMN IF NOT EXISTS last_validated_at TIMESTAMPTZ NULL;
UPDATE canonical_tag_definitions SET status = 'Active' WHERE status IS NULL;
UPDATE canonical_tag_definitions SET revision = 1 WHERE revision IS NULL;
ALTER TABLE canonical_tag_definitions ALTER COLUMN status SET NOT NULL;
ALTER TABLE canonical_tag_definitions ALTER COLUMN status SET DEFAULT 'Active';
ALTER TABLE canonical_tag_definitions ALTER COLUMN revision SET NOT NULL;
ALTER TABLE canonical_tag_definitions ALTER COLUMN revision SET DEFAULT 1;
DO $$ BEGIN
    ALTER TABLE canonical_tag_definitions ADD CONSTRAINT fk_canonical_tag_definitions_redirect
        FOREIGN KEY (redirect_to_id) REFERENCES canonical_tag_definitions(id) ON DELETE RESTRICT;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
    ALTER TABLE canonical_tag_definitions ADD CONSTRAINT ck_canonical_tag_definitions_status
        CHECK (status IN ('Active', 'Deprecated', 'Superseded'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
    ALTER TABLE canonical_tag_definitions ADD CONSTRAINT ck_canonical_tag_definitions_redirect
        CHECK ((status = 'Superseded') = (redirect_to_id IS NOT NULL));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

ALTER TABLE canonical_tag_bindings ADD COLUMN IF NOT EXISTS source TEXT;
ALTER TABLE canonical_tag_bindings ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE;
ALTER TABLE canonical_tag_bindings ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE;
ALTER TABLE canonical_tag_bindings ADD COLUMN IF NOT EXISTS confidence NUMERIC(5,4);
ALTER TABLE canonical_tag_bindings ADD COLUMN IF NOT EXISTS evidence_ref TEXT;
ALTER TABLE canonical_tag_bindings ADD COLUMN IF NOT EXISTS status TEXT;
ALTER TABLE canonical_tag_bindings ADD COLUMN IF NOT EXISTS last_validated_at TIMESTAMPTZ NULL;
UPDATE canonical_tag_bindings SET source = 'Manual' WHERE source IS NULL;
UPDATE canonical_tag_bindings SET confidence = 1 WHERE confidence IS NULL;
UPDATE canonical_tag_bindings SET evidence_ref = '' WHERE evidence_ref IS NULL;
UPDATE canonical_tag_bindings SET status = 'Active' WHERE status IS NULL;
UPDATE canonical_tag_bindings b SET tenant_id = d.tenant_id, owner_user_id = d.owner_user_id
FROM canonical_tag_definitions d WHERE b.definition_id = d.id;
ALTER TABLE canonical_tag_bindings ALTER COLUMN source SET NOT NULL;
ALTER TABLE canonical_tag_bindings ALTER COLUMN confidence SET NOT NULL;
ALTER TABLE canonical_tag_bindings ALTER COLUMN evidence_ref SET NOT NULL;
ALTER TABLE canonical_tag_bindings ALTER COLUMN status SET NOT NULL;
DO $$ BEGIN
    ALTER TABLE canonical_tag_bindings ADD CONSTRAINT ck_canonical_tag_bindings_confidence CHECK (confidence BETWEEN 0 AND 1);
EXCEPTION WHEN duplicate_object THEN NULL; END $$;
DO $$ BEGIN
    ALTER TABLE canonical_tag_bindings ADD CONSTRAINT ck_canonical_tag_bindings_status CHECK (status IN ('Active', 'Superseded', 'Removed'));
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

ALTER TABLE canonical_tag_aliases ADD COLUMN IF NOT EXISTS tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE;
ALTER TABLE canonical_tag_aliases ADD COLUMN IF NOT EXISTS owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE;
UPDATE canonical_tag_aliases a SET tenant_id = d.tenant_id, owner_user_id = d.owner_user_id
FROM canonical_tag_definitions d WHERE a.definition_id = d.id;
DROP INDEX IF EXISTS ix_canonical_tag_aliases_project_alias;
CREATE UNIQUE INDEX IF NOT EXISTS ix_canonical_tag_aliases_scope_alias
    ON canonical_tag_aliases(tenant_id, owner_user_id, project_id, normalized_alias) NULLS NOT DISTINCT;
DROP INDEX IF EXISTS ix_canonical_tag_bindings_unique;
CREATE UNIQUE INDEX IF NOT EXISTS ix_canonical_tag_bindings_scope_unique
    ON canonical_tag_bindings(tenant_id, owner_user_id, definition_id, project_id, resource_type, resource_id) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS file_assets
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NULL,
    logical_file_name TEXT NOT NULL,
    normalized_file_name TEXT NOT NULL,
    state TEXT NOT NULL,
    created_by_actor_id TEXT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    governance_reminder_at TIMESTAMPTZ NULL,
    deleted_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_file_assets_name CHECK (length(logical_file_name) BETWEEN 1 AND 512 AND length(normalized_file_name) BETWEEN 1 AND 512),
    CONSTRAINT ck_file_assets_state CHECK (state IN ('Active', 'Unassigned', 'LogicalDeleted')),
    CONSTRAINT ck_file_assets_assignment CHECK ((state = 'Unassigned') = (project_id IS NULL)),
    CONSTRAINT ck_file_assets_deleted CHECK ((state = 'LogicalDeleted') = (deleted_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_file_assets_scope_name
    ON file_assets(tenant_id, owner_user_id, project_id, normalized_file_name) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS file_versions
(
    id UUID PRIMARY KEY,
    file_asset_id UUID NOT NULL REFERENCES file_assets(id) ON DELETE CASCADE,
    managed_object_id UUID NOT NULL REFERENCES managed_objects(id) ON DELETE RESTRICT,
    version_number INTEGER NOT NULL,
    content_sha256 TEXT NOT NULL,
    content_type TEXT NOT NULL,
    deduplication_scope_key TEXT NOT NULL,
    lifecycle TEXT NOT NULL,
    classification TEXT NOT NULL,
    classification_revision BIGINT NOT NULL DEFAULT 1,
    search_projection_allowed BOOLEAN NOT NULL DEFAULT FALSE,
    embedding_allowed BOOLEAN NOT NULL DEFAULT FALSE,
    needs_rescan BOOLEAN NOT NULL DEFAULT FALSE,
    last_scan_at TIMESTAMPTZ NULL,
    scanner_set_version TEXT NOT NULL DEFAULT '',
    security_policy_version TEXT NOT NULL DEFAULT '',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_file_versions_number CHECK (version_number > 0),
    CONSTRAINT ck_file_versions_hash CHECK (content_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_file_versions_lifecycle CHECK (lifecycle IN ('PendingUpload', 'Uploaded', 'IntegrityVerified', 'SecurityScanning', 'Classified', 'Profiling', 'Ready', 'NeedsRescan', 'Quarantined', 'LogicalDeleted')),
    CONSTRAINT ck_file_versions_classification CHECK (classification IN ('Normal', 'Sensitive', 'Restricted', 'Quarantined')),
    CONSTRAINT ck_file_versions_projection CHECK (classification IN ('Normal', 'Sensitive') OR (search_projection_allowed = FALSE AND embedding_allowed = FALSE)),
    CONSTRAINT ck_file_versions_quarantine CHECK ((classification = 'Quarantined') = (lifecycle = 'Quarantined'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_file_versions_asset_version ON file_versions(file_asset_id, version_number);
CREATE UNIQUE INDEX IF NOT EXISTS ix_file_versions_deduplication_scope ON file_versions(deduplication_scope_key);
CREATE INDEX IF NOT EXISTS ix_file_versions_object ON file_versions(managed_object_id);
CREATE INDEX IF NOT EXISTS ix_file_versions_hash_state ON file_versions(content_sha256, classification, lifecycle);

CREATE TABLE IF NOT EXISTS file_relations
(
    id UUID PRIMARY KEY,
    file_asset_id UUID NOT NULL REFERENCES file_assets(id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    target_project_id TEXT NOT NULL,
    target_id TEXT NOT NULL,
    purpose TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_file_relations_kind CHECK (kind IN ('Project', 'Discussion', 'WorkItem', 'LegalHold', 'SecurityHold', 'ParentFile', 'Alias'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_file_relations_identity ON file_relations(file_asset_id, kind, target_project_id, target_id);

CREATE TABLE IF NOT EXISTS file_profiles
(
    id UUID PRIMARY KEY,
    file_version_id UUID NOT NULL REFERENCES file_versions(id) ON DELETE CASCADE,
    detected_content_type TEXT NOT NULL,
    size_bytes BIGINT NOT NULL,
    page_count INTEGER NULL,
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    classification_revision BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_file_profiles_size CHECK (size_bytes >= 0 AND (page_count IS NULL OR page_count >= 0))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_file_profiles_revision ON file_profiles(file_version_id, classification_revision);

CREATE TABLE IF NOT EXISTS file_representations
(
    id UUID PRIMARY KEY,
    file_version_id UUID NOT NULL REFERENCES file_versions(id) ON DELETE CASCADE,
    managed_object_id UUID NULL REFERENCES managed_objects(id) ON DELETE RESTRICT,
    kind TEXT NOT NULL,
    classification TEXT NOT NULL,
    source_classification_revision BIGINT NOT NULL,
    content_hash TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    invalidated_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_file_representations_kind CHECK (kind IN ('ExtractedText', 'Ocr', 'Preview', 'Thumbnail', 'SearchProjection')),
    CONSTRAINT ck_file_representations_classification CHECK (classification IN ('Normal', 'Sensitive', 'Restricted', 'Quarantined')),
    CONSTRAINT ck_file_representations_hash CHECK (content_hash ~ '^[0-9a-f]{64}$')
);
CREATE INDEX IF NOT EXISTS ix_file_representations_source ON file_representations(file_version_id, kind, source_classification_revision);

CREATE TABLE IF NOT EXISTS file_access_events
(
    id UUID PRIMARY KEY,
    file_asset_id UUID NOT NULL,
    file_version_id UUID NULL,
    tenant_id UUID NULL,
    owner_user_id UUID NULL,
    project_id TEXT NOT NULL,
    actor_id TEXT NOT NULL,
    operation TEXT NOT NULL,
    purpose TEXT NOT NULL,
    allowed BOOLEAN NOT NULL,
    reason_code TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_file_access_events_scope ON file_access_events(tenant_id, project_id, file_asset_id, created_at DESC);

CREATE TABLE IF NOT EXISTS file_security_findings
(
    id UUID PRIMARY KEY,
    file_version_id UUID NOT NULL REFERENCES file_versions(id) ON DELETE CASCADE,
    scanner_rule_id TEXT NOT NULL,
    category TEXT NOT NULL,
    severity TEXT NOT NULL,
    confidence NUMERIC(5,4) NOT NULL,
    scanner_version TEXT NOT NULL,
    detected_at TIMESTAMPTZ NOT NULL,
    disposition TEXT NOT NULL,
    evidence_hash TEXT NOT NULL,
    redacted_evidence TEXT NOT NULL,
    CONSTRAINT ck_file_security_findings_confidence CHECK (confidence BETWEEN 0 AND 1),
    CONSTRAINT ck_file_security_findings_evidence CHECK (evidence_hash ~ '^[0-9a-f]{64}$' AND length(redacted_evidence) <= 512)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_file_security_findings_identity ON file_security_findings(file_version_id, scanner_rule_id, evidence_hash);

CREATE TABLE IF NOT EXISTS file_search_projections
(
    file_version_id UUID PRIMARY KEY REFERENCES file_versions(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    classification TEXT NOT NULL,
    classification_revision BIGINT NOT NULL,
    content_search_enabled BOOLEAN NOT NULL,
    embedding_enabled BOOLEAN NOT NULL,
    redacted_text TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    invalidated_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_file_search_projections_classification CHECK (classification IN ('Normal', 'Sensitive', 'Restricted', 'Quarantined')),
    CONSTRAINT ck_file_search_projections_access CHECK (classification IN ('Normal', 'Sensitive') OR (content_search_enabled = FALSE AND embedding_enabled = FALSE))
);
CREATE INDEX IF NOT EXISTS ix_file_search_projections_lookup ON file_search_projections(project_id, content_search_enabled, classification_revision);

CREATE TABLE IF NOT EXISTS file_deletion_records
(
    id UUID PRIMARY KEY,
    file_asset_id UUID NOT NULL,
    managed_object_id UUID NOT NULL,
    state TEXT NOT NULL,
    legal_hold BOOLEAN NOT NULL DEFAULT FALSE,
    reference_blocked BOOLEAN NOT NULL DEFAULT FALSE,
    retain_until TIMESTAMPTZ NULL,
    reason TEXT NOT NULL,
    request_id TEXT NOT NULL,
    attempt_count INTEGER NOT NULL DEFAULT 0,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    primary_deleted_at TIMESTAMPTZ NULL,
    fully_expired_at TIMESTAMPTZ NULL,
    verified_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_file_deletion_records_state CHECK (state IN ('DeleteRequested', 'LogicalDeleted', 'PhysicalDeletePending', 'PrimaryDeleted', 'BackupRetentionPending', 'FullyExpired', 'Verified')),
    CONSTRAINT ck_file_deletion_records_attempts CHECK (attempt_count >= 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_file_deletion_records_replay ON file_deletion_records(file_asset_id, managed_object_id, request_id);
CREATE INDEX IF NOT EXISTS ix_file_deletion_records_pending ON file_deletion_records(state, retain_until);

CREATE TABLE IF NOT EXISTS canonical_tag_telemetry_events
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    definition_id UUID NOT NULL REFERENCES canonical_tag_definitions(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    reason_code TEXT NOT NULL,
    query_hash TEXT NOT NULL,
    resource_type TEXT NOT NULL,
    actor_type TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_canonical_tag_telemetry_kind CHECK (kind IN ('SearchImpression', 'FilterUse', 'Selection', 'Rejection', 'Mismatch')),
    CONSTRAINT ck_canonical_tag_telemetry_hash CHECK (query_hash = '' OR query_hash ~ '^[0-9a-f]{64}$')
);
CREATE INDEX IF NOT EXISTS ix_canonical_tag_telemetry_window ON canonical_tag_telemetry_events(tenant_id, owner_user_id, project_id, definition_id, created_at DESC);

CREATE TABLE IF NOT EXISTS canonical_tag_daily_aggregates
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    definition_id UUID NOT NULL REFERENCES canonical_tag_definitions(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    day DATE NOT NULL,
    search_impressions BIGINT NOT NULL DEFAULT 0,
    filter_uses BIGINT NOT NULL DEFAULT 0,
    selections BIGINT NOT NULL DEFAULT 0,
    rejections BIGINT NOT NULL DEFAULT 0,
    mismatches BIGINT NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_canonical_tag_daily_nonnegative CHECK (search_impressions >= 0 AND filter_uses >= 0 AND selections >= 0 AND rejections >= 0 AND mismatches >= 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canonical_tag_daily_identity ON canonical_tag_daily_aggregates(tenant_id, owner_user_id, project_id, definition_id, day) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS canonical_tag_governance_proposals
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    kind TEXT NOT NULL,
    status TEXT NOT NULL,
    source_definition_id UUID NOT NULL REFERENCES canonical_tag_definitions(id) ON DELETE RESTRICT,
    target_definition_id UUID NULL REFERENCES canonical_tag_definitions(id) ON DELETE RESTRICT,
    proposed_value TEXT NOT NULL,
    reason_code TEXT NOT NULL,
    candidate_resource_ids_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    affected_binding_count INTEGER NOT NULL,
    confidence NUMERIC(5,4) NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    applied_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_canonical_tag_governance_kind CHECK (kind IN ('NormalizeAlias', 'CleanupStaleSuggestion', 'Merge', 'Split', 'Rename', 'Deprecate')),
    CONSTRAINT ck_canonical_tag_governance_status CHECK (status IN ('Pending', 'Applied', 'Dismissed')),
    CONSTRAINT ck_canonical_tag_governance_bounds CHECK (affected_binding_count >= 0 AND confidence BETWEEN 0 AND 1)
);
CREATE INDEX IF NOT EXISTS ix_canonical_tag_governance_pending ON canonical_tag_governance_proposals(tenant_id, owner_user_id, project_id, status, kind);

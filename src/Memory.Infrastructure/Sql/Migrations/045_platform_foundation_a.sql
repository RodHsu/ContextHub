ALTER TABLE project_hierarchies ADD COLUMN IF NOT EXISTS dimension TEXT;
ALTER TABLE project_hierarchies ADD COLUMN IF NOT EXISTS authorization_inheritable BOOLEAN;
ALTER TABLE project_hierarchies ADD COLUMN IF NOT EXISTS revision BIGINT;
UPDATE project_hierarchies SET dimension = 'discussion' WHERE dimension IS NULL;
UPDATE project_hierarchies SET authorization_inheritable = FALSE WHERE authorization_inheritable IS NULL;
UPDATE project_hierarchies SET revision = 1 WHERE revision IS NULL;
ALTER TABLE project_hierarchies ALTER COLUMN dimension SET NOT NULL;
ALTER TABLE project_hierarchies ALTER COLUMN dimension SET DEFAULT 'discussion';
ALTER TABLE project_hierarchies ALTER COLUMN authorization_inheritable SET NOT NULL;
ALTER TABLE project_hierarchies ALTER COLUMN authorization_inheritable SET DEFAULT FALSE;
ALTER TABLE project_hierarchies ALTER COLUMN revision SET NOT NULL;
ALTER TABLE project_hierarchies ALTER COLUMN revision SET DEFAULT 1;
DROP INDEX IF EXISTS ix_project_hierarchies_owner_parent_child;
CREATE UNIQUE INDEX IF NOT EXISTS ix_project_hierarchies_owner_dimension_parent_child
    ON project_hierarchies(tenant_id, owner_user_id, dimension, parent_project_id, child_project_id) NULLS NOT DISTINCT;
CREATE INDEX IF NOT EXISTS ix_project_hierarchies_authorization_child
    ON project_hierarchies(tenant_id, owner_user_id, child_project_id)
    WHERE authorization_inheritable = TRUE;

CREATE TABLE IF NOT EXISTS project_security_revisions
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    topology_revision BIGINT NOT NULL DEFAULT 0,
    policy_revision BIGINT NOT NULL DEFAULT 0,
    grant_revision BIGINT NOT NULL DEFAULT 0,
    tag_revision BIGINT NOT NULL DEFAULT 0,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_project_security_revisions_nonnegative CHECK
        (topology_revision >= 0 AND policy_revision >= 0 AND grant_revision >= 0 AND tag_revision >= 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_project_security_revisions_owner_project
    ON project_security_revisions(tenant_id, owner_user_id, project_id) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS project_authorization_policies
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    principal_id TEXT NOT NULL,
    right_key TEXT NOT NULL,
    effect TEXT NOT NULL,
    resource_type TEXT NULL,
    resource_id TEXT NULL,
    evidence_ref TEXT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_project_authorization_policies_effect CHECK (effect IN ('Allow', 'Deny')),
    CONSTRAINT ck_project_authorization_policies_resource CHECK
        ((resource_type IS NULL AND resource_id IS NULL) OR (resource_type IS NOT NULL AND resource_id IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_project_authorization_policies_lookup
    ON project_authorization_policies(tenant_id, owner_user_id, project_id, principal_id, right_key, resource_type, resource_id) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS project_explicit_grants
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    principal_id TEXT NOT NULL,
    right_key TEXT NOT NULL,
    effect TEXT NOT NULL,
    resource_type TEXT NULL,
    resource_id TEXT NULL,
    evidence_ref TEXT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_project_explicit_grants_effect CHECK (effect IN ('Allow', 'Deny')),
    CONSTRAINT ck_project_explicit_grants_resource CHECK
        ((resource_type IS NULL AND resource_id IS NULL) OR (resource_type IS NOT NULL AND resource_id IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_project_explicit_grants_lookup
    ON project_explicit_grants(tenant_id, owner_user_id, project_id, principal_id, right_key, resource_type, resource_id) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS canonical_tag_definitions
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    canonical_name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    description TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canonical_tag_definitions_owner_project_name
    ON canonical_tag_definitions(tenant_id, owner_user_id, project_id, normalized_name) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS canonical_tag_aliases
(
    id UUID PRIMARY KEY,
    definition_id UUID NOT NULL REFERENCES canonical_tag_definitions(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    alias TEXT NOT NULL,
    normalized_alias TEXT NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canonical_tag_aliases_project_alias
    ON canonical_tag_aliases(project_id, normalized_alias);

CREATE TABLE IF NOT EXISTS canonical_tag_relations
(
    id UUID PRIMARY KEY,
    source_definition_id UUID NOT NULL REFERENCES canonical_tag_definitions(id) ON DELETE CASCADE,
    target_definition_id UUID NOT NULL REFERENCES canonical_tag_definitions(id) ON DELETE CASCADE,
    relation_type TEXT NOT NULL,
    CONSTRAINT ck_canonical_tag_relations_distinct CHECK (source_definition_id <> target_definition_id)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canonical_tag_relations_unique
    ON canonical_tag_relations(source_definition_id, target_definition_id, relation_type);

CREATE TABLE IF NOT EXISTS canonical_tag_bindings
(
    id UUID PRIMARY KEY,
    definition_id UUID NOT NULL REFERENCES canonical_tag_definitions(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    resource_type TEXT NOT NULL,
    resource_id TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_canonical_tag_bindings_unique
    ON canonical_tag_bindings(definition_id, project_id, resource_type, resource_id);
CREATE INDEX IF NOT EXISTS ix_canonical_tag_bindings_resource
    ON canonical_tag_bindings(project_id, resource_type, resource_id);

CREATE TABLE IF NOT EXISTS canonical_tag_suggestions
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    suggested_name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    rationale TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Pending',
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_canonical_tag_suggestions_status CHECK (status IN ('Pending', 'Accepted', 'Rejected'))
);
CREATE INDEX IF NOT EXISTS ix_canonical_tag_suggestions_review
    ON canonical_tag_suggestions(tenant_id, owner_user_id, project_id, status, normalized_name);

-- Durable server-owned generation ledger for scheduled-governance authority.
-- Every scope has one append-only predecessor chain. A historical digest can
-- be replayed for idempotency, but it can never become the current epoch.

-- tenant_users.id is globally unique today, but the composite key makes the
-- tenant binding explicit for the ledger's owner foreign key.
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM pg_constraint
        WHERE conrelid = 'tenant_users'::regclass
          AND conname = 'uq_tenant_users_id_tenant') THEN
        ALTER TABLE tenant_users
            ADD CONSTRAINT uq_tenant_users_id_tenant UNIQUE (id, tenant_id);
    END IF;
END;
$$;

CREATE TABLE scheduled_governance_authority_epochs
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL,
    owner_user_id UUID NOT NULL,
    environment VARCHAR(64) NOT NULL,
    configuration_digest VARCHAR(64) NOT NULL,
    authority_epoch_digest VARCHAR(64) NOT NULL,
    generation BIGINT NOT NULL,
    previous_authority_epoch_digest VARCHAR(64) NULL,
    previous_generation BIGINT NULL,
    request_hash VARCHAR(64) NOT NULL,
    created_at_utc TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT fk_scheduled_authority_epoch_tenant
        FOREIGN KEY (tenant_id)
        REFERENCES tenants (id)
        ON DELETE RESTRICT,
    CONSTRAINT fk_scheduled_authority_epoch_owner
        FOREIGN KEY (owner_user_id, tenant_id)
        REFERENCES tenant_users (id, tenant_id)
        ON DELETE RESTRICT,
    CONSTRAINT ck_scheduled_authority_epoch_identity
        CHECK (
            id <> '00000000-0000-0000-0000-000000000000'::uuid AND
            tenant_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
            owner_user_id <> '00000000-0000-0000-0000-000000000000'::uuid AND
            environment <> '' AND
            environment = btrim(environment) AND
            environment ~ '^[!-~]+$'),
    CONSTRAINT ck_scheduled_authority_epoch_digest_shape
        CHECK (
            configuration_digest ~ '^[0-9a-f]{64}$' AND
            authority_epoch_digest ~ '^[0-9a-f]{64}$' AND
            request_hash ~ '^[0-9a-f]{64}$' AND
            (previous_authority_epoch_digest IS NULL OR
             previous_authority_epoch_digest ~ '^[0-9a-f]{64}$')),
    CONSTRAINT ck_scheduled_authority_epoch_generation
        CHECK (generation BETWEEN 1 AND 4096),
    CONSTRAINT ck_scheduled_authority_epoch_predecessor
        CHECK (
            (generation = 1 AND
             previous_authority_epoch_digest IS NULL AND
             previous_generation IS NULL) OR
            (generation > 1 AND
             previous_authority_epoch_digest IS NOT NULL AND
             previous_generation IS NOT NULL AND
             previous_generation = generation - 1)),
    CONSTRAINT uq_scheduled_authority_epoch_scope_generation
        UNIQUE (tenant_id, owner_user_id, environment, generation),
    CONSTRAINT uq_scheduled_authority_epoch_scope_digest
        UNIQUE (tenant_id, owner_user_id, environment, authority_epoch_digest),
    CONSTRAINT uq_scheduled_authority_epoch_scope_digest_generation
        UNIQUE (tenant_id, owner_user_id, environment, authority_epoch_digest, generation),
    CONSTRAINT uq_scheduled_authority_epoch_scope_request
        UNIQUE (tenant_id, owner_user_id, environment, request_hash),
    CONSTRAINT fk_scheduled_authority_epoch_predecessor
        FOREIGN KEY (
            tenant_id,
            owner_user_id,
            environment,
            previous_authority_epoch_digest,
            previous_generation)
        REFERENCES scheduled_governance_authority_epochs (
            tenant_id,
            owner_user_id,
            environment,
            authority_epoch_digest,
            generation)
        ON DELETE RESTRICT
);

CREATE INDEX ix_scheduled_authority_epoch_scope_latest
    ON scheduled_governance_authority_epochs (
        tenant_id,
        owner_user_id,
        environment,
        generation DESC);

CREATE OR REPLACE FUNCTION reject_scheduled_governance_authority_epoch_mutation()
RETURNS TRIGGER AS $$
BEGIN
    RAISE EXCEPTION 'scheduled_governance_authority_epochs is append-only';
END;
$$ LANGUAGE plpgsql;

DROP TRIGGER IF EXISTS scheduled_governance_authority_epochs_immutable
    ON scheduled_governance_authority_epochs;
CREATE TRIGGER scheduled_governance_authority_epochs_immutable
    BEFORE UPDATE OR DELETE ON scheduled_governance_authority_epochs
    FOR EACH ROW
    EXECUTE FUNCTION reject_scheduled_governance_authority_epoch_mutation();

DROP TRIGGER IF EXISTS scheduled_governance_authority_epochs_reject_truncate
    ON scheduled_governance_authority_epochs;
CREATE TRIGGER scheduled_governance_authority_epochs_reject_truncate
    BEFORE TRUNCATE ON scheduled_governance_authority_epochs
    FOR EACH STATEMENT
    EXECUTE FUNCTION reject_scheduled_governance_authority_epoch_mutation();

COMMENT ON TABLE scheduled_governance_authority_epochs IS
    'Append-only server-owned scheduled-governance authority generation chain; historical epochs are never current authority.';
COMMENT ON COLUMN scheduled_governance_authority_epochs.request_hash IS
    'Canonical non-secret request identity for exact replay idempotency, including tenant, owner, environment, configuration, digest, and predecessor binding.';
COMMENT ON COLUMN scheduled_governance_authority_epochs.previous_authority_epoch_digest IS
    'The only allowed predecessor for this scope; the composite foreign key prevents an unbound or cross-scope fork.';

CREATE TABLE IF NOT EXISTS secrets
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    name TEXT NOT NULL,
    normalized_name TEXT NOT NULL,
    kind TEXT NOT NULL,
    state TEXT NOT NULL,
    current_version_id UUID NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    compromised_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_secrets_name CHECK (length(name) BETWEEN 1 AND 300 AND length(normalized_name) BETWEEN 1 AND 300),
    CONSTRAINT ck_secrets_kind CHECK (kind IN ('Opaque', 'ApiToken', 'OAuthCredential', 'SshPrivateKeyPkcs8', 'SshCertificateAuthorityReference')),
    CONSTRAINT ck_secrets_state CHECK (state IN ('Active', 'Revoked', 'Compromised')),
    CONSTRAINT ck_secrets_terminal_time CHECK ((state <> 'Revoked' OR revoked_at IS NOT NULL) AND (state <> 'Compromised' OR compromised_at IS NOT NULL)),
    CONSTRAINT ck_secrets_revision CHECK (revision > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_secrets_scope_name
    ON secrets(tenant_id, owner_user_id, project_id, normalized_name) NULLS NOT DISTINCT;
CREATE INDEX IF NOT EXISTS ix_secrets_current_version ON secrets(current_version_id);

CREATE TABLE IF NOT EXISTS secret_versions
(
    id UUID PRIMARY KEY,
    secret_id UUID NOT NULL REFERENCES secrets(id) ON DELETE CASCADE,
    version_number INTEGER NOT NULL,
    state TEXT NOT NULL,
    envelope_schema_version INTEGER NOT NULL DEFAULT 1,
    encryption_algorithm TEXT NOT NULL DEFAULT 'AES-256-GCM',
    key_id TEXT NOT NULL,
    wrapped_dek BYTEA NOT NULL,
    wrap_nonce BYTEA NOT NULL,
    wrap_tag BYTEA NOT NULL,
    ciphertext BYTEA NOT NULL,
    ciphertext_nonce BYTEA NOT NULL,
    ciphertext_tag BYTEA NOT NULL,
    ciphertext_sha256 TEXT NOT NULL,
    plaintext_length INTEGER NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NULL,
    retired_at TIMESTAMPTZ NULL,
    revoked_at TIMESTAMPTZ NULL,
    compromised_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_secret_versions_number CHECK (version_number > 0),
    CONSTRAINT ck_secret_versions_state CHECK (state IN ('Active', 'Retired', 'Revoked', 'Compromised')),
    CONSTRAINT ck_secret_versions_envelope CHECK (envelope_schema_version = 1 AND encryption_algorithm = 'AES-256-GCM'),
    CONSTRAINT ck_secret_versions_crypto CHECK (octet_length(wrapped_dek) = 32 AND octet_length(wrap_nonce) = 12 AND octet_length(wrap_tag) = 16 AND octet_length(ciphertext_nonce) = 12 AND octet_length(ciphertext_tag) = 16),
    CONSTRAINT ck_secret_versions_hash CHECK (ciphertext_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_secret_versions_length CHECK (plaintext_length > 0 AND plaintext_length = octet_length(ciphertext))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_secret_versions_number ON secret_versions(secret_id, version_number);
CREATE INDEX IF NOT EXISTS ix_secret_versions_ciphertext_hash ON secret_versions(ciphertext_sha256);
DO $$ BEGIN
    ALTER TABLE secrets ADD CONSTRAINT fk_secrets_current_version
        FOREIGN KEY (current_version_id) REFERENCES secret_versions(id) ON DELETE RESTRICT DEFERRABLE INITIALLY DEFERRED;
EXCEPTION WHEN duplicate_object THEN NULL; END $$;

CREATE TABLE IF NOT EXISTS secret_relations
(
    id UUID PRIMARY KEY,
    secret_id UUID NOT NULL REFERENCES secrets(id) ON DELETE CASCADE,
    kind TEXT NOT NULL,
    target_project_id TEXT NOT NULL,
    target_id TEXT NOT NULL,
    purpose TEXT NOT NULL,
    is_stale BOOLEAN NOT NULL DEFAULT FALSE,
    created_at TIMESTAMPTZ NOT NULL,
    last_validated_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_secret_relations_kind CHECK (kind IN ('Project', 'WorkItem', 'ConnectionProfile', 'Rotates', 'Supersedes'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_secret_relations_identity ON secret_relations(secret_id, kind, target_project_id, target_id);

CREATE TABLE IF NOT EXISTS secret_grants
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    secret_id UUID NOT NULL REFERENCES secrets(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    principal_id TEXT NOT NULL,
    secret_right TEXT NOT NULL,
    effect TEXT NOT NULL,
    evidence_ref TEXT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    expires_at TIMESTAMPTZ NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_secret_grants_right CHECK (secret_right IN ('Metadata', 'Use', 'Manage', 'Rotate', 'Revoke', 'Reveal', 'Export', 'SshIssue', 'SshSign')),
    CONSTRAINT ck_secret_grants_effect CHECK (effect IN ('Allow', 'Deny')),
    CONSTRAINT ck_secret_grants_revision CHECK (revision > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_secret_grants_identity ON secret_grants(secret_id, principal_id, secret_right);

CREATE TABLE IF NOT EXISTS secret_policies
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    secret_id UUID NULL REFERENCES secrets(id) ON DELETE CASCADE,
    principal_id TEXT NOT NULL,
    secret_right TEXT NOT NULL,
    effect TEXT NOT NULL,
    evidence_ref TEXT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_secret_policies_right CHECK (secret_right IN ('Metadata', 'Use', 'Manage', 'Rotate', 'Revoke', 'Reveal', 'Export', 'SshIssue', 'SshSign')),
    CONSTRAINT ck_secret_policies_effect CHECK (effect IN ('Allow', 'Deny')),
    CONSTRAINT ck_secret_policies_revision CHECK (revision > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_secret_policies_identity
    ON secret_policies(tenant_id, owner_user_id, project_id, secret_id, principal_id, secret_right) NULLS NOT DISTINCT;

CREATE TABLE IF NOT EXISTS secret_leases
(
    id UUID PRIMARY KEY,
    secret_id UUID NOT NULL REFERENCES secrets(id) ON DELETE CASCADE,
    secret_version_id UUID NOT NULL,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    actor_id TEXT NOT NULL,
    execution_id UUID NULL,
    kind TEXT NOT NULL,
    purpose TEXT NOT NULL,
    target TEXT NOT NULL,
    capability_id UUID NOT NULL,
    capability_hash TEXT NOT NULL,
    authority_revision BIGINT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    max_uses INTEGER NOT NULL,
    used_count INTEGER NOT NULL DEFAULT 0,
    max_concurrency INTEGER NOT NULL,
    active_uses INTEGER NOT NULL DEFAULT 0,
    state TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_secret_leases_kind CHECK (kind IN ('Use', 'SshSigner', 'SshCertificate')),
    CONSTRAINT ck_secret_leases_state CHECK (state IN ('Active', 'Exhausted', 'Revoked', 'Expired')),
    CONSTRAINT ck_secret_leases_hash CHECK (capability_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_secret_leases_usage CHECK (max_uses > 0 AND used_count >= 0 AND used_count <= max_uses AND max_concurrency > 0 AND active_uses >= 0 AND active_uses <= max_concurrency),
    CONSTRAINT ck_secret_leases_revision CHECK (authority_revision > 0 AND revision > 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_secret_leases_capability ON secret_leases(capability_id);
CREATE INDEX IF NOT EXISTS ix_secret_leases_active ON secret_leases(secret_id, state, expires_at);

CREATE TABLE IF NOT EXISTS secret_access_events
(
    id UUID PRIMARY KEY,
    secret_id UUID NOT NULL,
    secret_version_id UUID NULL,
    lease_id UUID NULL,
    tenant_id UUID NULL,
    owner_user_id UUID NULL,
    project_id TEXT NOT NULL,
    actor_id TEXT NOT NULL,
    operation TEXT NOT NULL,
    purpose TEXT NOT NULL,
    target_hash TEXT NOT NULL,
    request_id TEXT NOT NULL,
    allowed BOOLEAN NOT NULL,
    reason_code TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_secret_access_events_operation CHECK (operation IN ('Create', 'AddVersion', 'Rotate', 'Use', 'Reveal', 'Revoke', 'Grant', 'IssueSshCertificate', 'RenewSshCertificate', 'SignSshAuthentication')),
    CONSTRAINT ck_secret_access_events_target_hash CHECK (target_hash ~ '^[0-9a-f]{64}$')
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_secret_access_events_replay ON secret_access_events(secret_id, request_id);
CREATE INDEX IF NOT EXISTS ix_secret_access_events_scope ON secret_access_events(tenant_id, project_id, secret_id, created_at DESC);

CREATE TABLE IF NOT EXISTS step_up_assertions
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    actor_user_id UUID NOT NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    session_hash TEXT NOT NULL,
    authentication_method TEXT NOT NULL,
    assurance_level TEXT NOT NULL,
    purpose TEXT NOT NULL,
    resource_type TEXT NULL,
    resource_id TEXT NULL,
    nonce_hash TEXT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    max_uses INTEGER NOT NULL,
    used_count INTEGER NOT NULL DEFAULT 0,
    state TEXT NOT NULL,
    auth_time TIMESTAMPTZ NOT NULL,
    issued_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_step_up_assertions_method CHECK (authentication_method = 'Password'),
    CONSTRAINT ck_step_up_assertions_assurance CHECK (assurance_level = 'Aal1'),
    CONSTRAINT ck_step_up_assertions_binding CHECK ((resource_type IS NULL) = (resource_id IS NULL)),
    CONSTRAINT ck_step_up_assertions_nonce CHECK (nonce_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_step_up_assertions_session CHECK (session_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_step_up_assertions_usage CHECK (revision > 0 AND max_uses BETWEEN 1 AND 5 AND used_count BETWEEN 0 AND max_uses),
    CONSTRAINT ck_step_up_assertions_state CHECK (state IN ('Active', 'Exhausted', 'Revoked', 'Expired')),
    CONSTRAINT ck_step_up_assertions_ttl CHECK (expires_at > issued_at AND expires_at <= issued_at + INTERVAL '15 minutes')
);
CREATE INDEX IF NOT EXISTS ix_step_up_assertions_active ON step_up_assertions(tenant_id, actor_user_id, state, expires_at);

CREATE TABLE IF NOT EXISTS step_up_authentication_attempts
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    actor_user_id UUID NOT NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    session_hash TEXT NOT NULL,
    succeeded BOOLEAN NOT NULL,
    reason_code TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_step_up_attempts_session_hash CHECK (session_hash ~ '^[0-9a-f]{64}$')
);
CREATE INDEX IF NOT EXISTS ix_step_up_attempts_window ON step_up_authentication_attempts(tenant_id, actor_user_id, created_at DESC);

CREATE TABLE IF NOT EXISTS ssh_certificate_leases
(
    id UUID PRIMARY KEY,
    secret_lease_id UUID NOT NULL REFERENCES secret_leases(id) ON DELETE CASCADE,
    ca_secret_id UUID NOT NULL REFERENCES secrets(id) ON DELETE RESTRICT,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    actor_id TEXT NOT NULL,
    execution_id UUID NULL,
    target_host TEXT NOT NULL,
    target_port INTEGER NOT NULL,
    target_user TEXT NOT NULL,
    public_key TEXT NOT NULL,
    public_key_fingerprint TEXT NOT NULL,
    certificate TEXT NOT NULL,
    certificate_fingerprint TEXT NOT NULL,
    serial BIGINT NOT NULL,
    renewal_count INTEGER NOT NULL DEFAULT 0,
    max_renewals INTEGER NOT NULL,
    session_started_at TIMESTAMPTZ NOT NULL,
    max_session_expires_at TIMESTAMPTZ NOT NULL,
    valid_after TIMESTAMPTZ NOT NULL,
    valid_before TIMESTAMPTZ NOT NULL,
    renewal_eligible_at TIMESTAMPTZ NOT NULL,
    authority_revision BIGINT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    state TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    revoked_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_ssh_certificate_target CHECK (target_port BETWEEN 1 AND 65535 AND length(target_host) BETWEEN 1 AND 512 AND length(target_user) BETWEEN 1 AND 512),
    CONSTRAINT ck_ssh_certificate_renewal CHECK (renewal_count >= 0 AND max_renewals >= 0 AND renewal_count <= max_renewals),
    CONSTRAINT ck_ssh_certificate_window CHECK (valid_before > valid_after AND max_session_expires_at > session_started_at AND renewal_eligible_at < valid_before),
    CONSTRAINT ck_ssh_certificate_revision CHECK (authority_revision > 0 AND revision > 0),
    CONSTRAINT ck_ssh_certificate_state CHECK (state IN ('Active', 'Superseded', 'Expired', 'Revoked'))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_ssh_certificate_serial ON ssh_certificate_leases(serial);
CREATE INDEX IF NOT EXISTS ix_ssh_certificate_active ON ssh_certificate_leases(secret_lease_id, state, valid_before);

CREATE TABLE IF NOT EXISTS ssh_revocation_records
(
    id UUID PRIMARY KEY,
    ssh_certificate_lease_id UUID NOT NULL REFERENCES ssh_certificate_leases(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    serial BIGINT NOT NULL,
    certificate_fingerprint TEXT NOT NULL,
    reason_code TEXT NOT NULL,
    krl_required BOOLEAN NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    reconciled_at TIMESTAMPTZ NULL
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_ssh_revocation_certificate ON ssh_revocation_records(ssh_certificate_lease_id);
CREATE INDEX IF NOT EXISTS ix_ssh_revocation_pending ON ssh_revocation_records(krl_required, reconciled_at);

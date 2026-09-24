ALTER TABLE secrets DROP CONSTRAINT IF EXISTS ck_secrets_kind;
ALTER TABLE secrets ADD CONSTRAINT ck_secrets_kind
    CHECK (kind IN ('Opaque', 'ApiToken', 'OAuthCredential', 'SshPrivateKeyPkcs8', 'SshCertificateAuthorityReference', 'TotpSeed'));

ALTER TABLE step_up_assertions DROP CONSTRAINT IF EXISTS ck_step_up_assertions_method;
ALTER TABLE step_up_assertions ADD CONSTRAINT ck_step_up_assertions_method
    CHECK (authentication_method IN ('Password', 'Totp', 'RecoveryCode', 'WebAuthnPlatform', 'WebAuthnSecurityKey', 'Passkey'));
ALTER TABLE step_up_assertions DROP CONSTRAINT IF EXISTS ck_step_up_assertions_assurance;
ALTER TABLE step_up_assertions ADD CONSTRAINT ck_step_up_assertions_assurance
    CHECK ((authentication_method = 'Password' AND assurance_level = 'Aal1') OR
           (authentication_method IN ('Totp', 'RecoveryCode') AND assurance_level = 'Aal2') OR
           (authentication_method IN ('WebAuthnPlatform', 'WebAuthnSecurityKey', 'Passkey') AND assurance_level = 'Aal3'));

ALTER TABLE step_up_assertions ADD COLUMN IF NOT EXISTS mfa_authority_revision BIGINT NOT NULL DEFAULT 1;
ALTER TABLE step_up_assertions ADD COLUMN IF NOT EXISTS mfa_policy_revision TEXT NOT NULL DEFAULT 'wave4b-v1';
ALTER TABLE step_up_assertions DROP CONSTRAINT IF EXISTS ck_step_up_assertions_mfa_authority;
ALTER TABLE step_up_assertions ADD CONSTRAINT ck_step_up_assertions_mfa_authority
    CHECK (mfa_authority_revision > 0 AND length(mfa_policy_revision) BETWEEN 1 AND 200);

CREATE TABLE IF NOT EXISTS mfa_authority_states
(
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    actor_user_id UUID NOT NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    revision BIGINT NOT NULL DEFAULT 1,
    policy_revision TEXT NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    PRIMARY KEY (tenant_id, actor_user_id),
    CONSTRAINT ck_mfa_authority_states_revision CHECK (revision > 0),
    CONSTRAINT ck_mfa_authority_states_policy CHECK (length(policy_revision) BETWEEN 1 AND 200)
);

CREATE TABLE IF NOT EXISTS totp_factors
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    actor_user_id UUID NOT NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    seed_secret_id UUID NOT NULL REFERENCES secrets(id) ON DELETE RESTRICT,
    seed_secret_version_id UUID NOT NULL REFERENCES secret_versions(id) ON DELETE RESTRICT,
    issuer TEXT NOT NULL,
    account_name TEXT NOT NULL,
    authority_revision_at_start BIGINT NOT NULL,
    policy_revision_at_start TEXT NOT NULL,
    required_assurance_at_start TEXT NOT NULL,
    authorization_assertion_id UUID NOT NULL REFERENCES step_up_assertions(id) ON DELETE RESTRICT,
    authorization_assertion_revision BIGINT NOT NULL,
    state TEXT NOT NULL,
    last_accepted_counter BIGINT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    confirmed_at TIMESTAMPTZ NULL,
    removed_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_totp_factors_state CHECK (state IN ('Pending', 'Active', 'Removed', 'Revoked')),
    CONSTRAINT ck_totp_factors_text CHECK (length(issuer) BETWEEN 1 AND 200 AND length(account_name) BETWEEN 1 AND 300),
    CONSTRAINT ck_totp_factors_authority CHECK (authority_revision_at_start > 0 AND authorization_assertion_revision > 0 AND length(policy_revision_at_start) BETWEEN 1 AND 200),
    CONSTRAINT ck_totp_factors_assurance CHECK (required_assurance_at_start IN ('Aal1', 'Aal2', 'Aal3')),
    CONSTRAINT ck_totp_factors_revision CHECK (revision > 0),
    CONSTRAINT ck_totp_factors_lifecycle CHECK (
        (state = 'Pending' AND confirmed_at IS NULL AND removed_at IS NULL) OR
        (state = 'Active' AND confirmed_at IS NOT NULL AND removed_at IS NULL) OR
        (state IN ('Removed', 'Revoked') AND removed_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_totp_factors_actor ON totp_factors(tenant_id, actor_user_id, state);
CREATE UNIQUE INDEX IF NOT EXISTS ix_totp_factors_one_active
    ON totp_factors(tenant_id, actor_user_id) WHERE state = 'Active';

CREATE TABLE IF NOT EXISTS mfa_recovery_codes
(
    id UUID PRIMARY KEY,
    totp_factor_id UUID NOT NULL REFERENCES totp_factors(id) ON DELETE CASCADE,
    salt BYTEA NOT NULL,
    code_hash BYTEA NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_mfa_recovery_code_crypto CHECK (octet_length(salt) = 16 AND octet_length(code_hash) = 32)
);
CREATE INDEX IF NOT EXISTS ix_mfa_recovery_codes_active ON mfa_recovery_codes(totp_factor_id, used_at);

CREATE TABLE IF NOT EXISTS webauthn_credentials
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    actor_user_id UUID NOT NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    credential_id BYTEA NOT NULL,
    public_key BYTEA NOT NULL,
    user_handle BYTEA NOT NULL,
    sign_count BIGINT NOT NULL,
    transports TEXT NOT NULL,
    kind TEXT NOT NULL,
    user_verification_required BOOLEAN NOT NULL,
    is_backup_eligible BOOLEAN NOT NULL,
    is_backed_up BOOLEAN NOT NULL,
    aaguid UUID NOT NULL,
    state TEXT NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    created_at TIMESTAMPTZ NOT NULL,
    last_used_at TIMESTAMPTZ NOT NULL,
    removed_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_webauthn_credentials_material CHECK (octet_length(credential_id) BETWEEN 16 AND 1024 AND octet_length(public_key) BETWEEN 16 AND 4096 AND octet_length(user_handle) BETWEEN 16 AND 64),
    CONSTRAINT ck_webauthn_credentials_counter CHECK (sign_count BETWEEN 0 AND 4294967295),
    CONSTRAINT ck_webauthn_credentials_kind CHECK (kind IN ('Platform', 'RoamingSecurityKey', 'Passkey')),
    CONSTRAINT ck_webauthn_credentials_state CHECK (state IN ('Active', 'Removed', 'Revoked')),
    CONSTRAINT ck_webauthn_credentials_revision CHECK (revision > 0),
    CONSTRAINT ck_webauthn_credentials_lifecycle CHECK ((state = 'Active' AND removed_at IS NULL) OR (state IN ('Removed', 'Revoked') AND removed_at IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_webauthn_credentials_id ON webauthn_credentials(credential_id);
CREATE INDEX IF NOT EXISTS ix_webauthn_credentials_actor ON webauthn_credentials(tenant_id, actor_user_id, state);

CREATE TABLE IF NOT EXISTS webauthn_ceremonies
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    actor_user_id UUID NOT NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    session_hash TEXT NOT NULL,
    kind TEXT NOT NULL,
    state TEXT NOT NULL,
    purpose TEXT NOT NULL,
    resource_type TEXT NULL,
    resource_id TEXT NULL,
    options_json TEXT NOT NULL,
    challenge_hash TEXT NOT NULL,
    user_verification_required BOOLEAN NOT NULL,
    authority_revision_at_start BIGINT NOT NULL,
    policy_revision_at_start TEXT NOT NULL,
    required_assurance_at_start TEXT NOT NULL,
    authorization_assertion_id UUID NULL REFERENCES step_up_assertions(id) ON DELETE RESTRICT,
    authorization_assertion_revision BIGINT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    used_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_webauthn_ceremonies_kind CHECK (kind IN ('Registration', 'Authentication')),
    CONSTRAINT ck_webauthn_ceremonies_state CHECK (state IN ('Pending', 'Used', 'Expired', 'Failed')),
    CONSTRAINT ck_webauthn_ceremonies_binding CHECK ((resource_type IS NULL) = (resource_id IS NULL)),
    CONSTRAINT ck_webauthn_ceremonies_hashes CHECK (session_hash ~ '^[0-9a-f]{64}$' AND challenge_hash ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_webauthn_ceremonies_authority CHECK (
        authority_revision_at_start > 0 AND length(policy_revision_at_start) BETWEEN 1 AND 200 AND
        required_assurance_at_start IN ('Aal1', 'Aal2', 'Aal3') AND
        ((kind = 'Registration' AND authorization_assertion_id IS NOT NULL AND authorization_assertion_revision > 0) OR
         (kind = 'Authentication' AND authorization_assertion_id IS NULL AND authorization_assertion_revision IS NULL))),
    CONSTRAINT ck_webauthn_ceremonies_ttl CHECK (expires_at > created_at AND expires_at <= created_at + INTERVAL '10 minutes'),
    CONSTRAINT ck_webauthn_ceremonies_lifecycle CHECK ((state = 'Pending' AND used_at IS NULL) OR (state <> 'Pending' AND used_at IS NOT NULL))
);
CREATE INDEX IF NOT EXISTS ix_webauthn_ceremonies_pending ON webauthn_ceremonies(tenant_id, actor_user_id, state, expires_at);

CREATE TABLE IF NOT EXISTS mfa_security_events
(
    id UUID PRIMARY KEY,
    tenant_id UUID NOT NULL REFERENCES tenants(id) ON DELETE CASCADE,
    actor_user_id UUID NOT NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    factor_id UUID NULL,
    action TEXT NOT NULL,
    authentication_method TEXT NULL,
    assurance_level TEXT NULL,
    purpose TEXT NOT NULL,
    resource_hash TEXT NOT NULL,
    reason_code TEXT NOT NULL,
    succeeded BOOLEAN NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_mfa_security_events_action CHECK (action IN ('TotpEnrollmentStarted', 'TotpEnrolled', 'TotpVerified', 'TotpReplayRejected', 'RecoveryCodeUsed', 'RecoveryCodeReplayRejected', 'WebAuthnRegistrationStarted', 'WebAuthnRegistered', 'WebAuthnAuthenticationStarted', 'WebAuthnAuthenticated', 'WebAuthnReplayRejected', 'FactorRemoved', 'FactorReset', 'RecoveryApproved', 'AssuranceIssued')),
    CONSTRAINT ck_mfa_security_events_method CHECK (authentication_method IS NULL OR authentication_method IN ('Password', 'Totp', 'RecoveryCode', 'WebAuthnPlatform', 'WebAuthnSecurityKey', 'Passkey')),
    CONSTRAINT ck_mfa_security_events_assurance CHECK (assurance_level IS NULL OR assurance_level IN ('Aal0', 'Aal1', 'Aal2', 'Aal3')),
    CONSTRAINT ck_mfa_security_events_resource_hash CHECK (resource_hash ~ '^[0-9a-f]{64}$')
);
CREATE INDEX IF NOT EXISTS ix_mfa_security_events_actor ON mfa_security_events(tenant_id, actor_user_id, created_at DESC);

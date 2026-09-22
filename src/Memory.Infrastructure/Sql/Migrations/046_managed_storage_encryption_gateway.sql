CREATE TABLE IF NOT EXISTS managed_objects
(
    id UUID PRIMARY KEY,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    security_domain TEXT NOT NULL,
    state TEXT NOT NULL,
    storage_id TEXT NOT NULL,
    plaintext_length BIGINT NOT NULL,
    chunk_size INTEGER NOT NULL,
    chunk_count INTEGER NOT NULL,
    encryption_schema_version INTEGER NOT NULL,
    encryption_generation INTEGER NOT NULL,
    encryption_algorithm TEXT NOT NULL,
    key_id TEXT NOT NULL,
    wrapped_dek BYTEA NOT NULL,
    wrap_nonce BYTEA NOT NULL,
    wrap_tag BYTEA NOT NULL,
    plaintext_sha256 TEXT NULL,
    staged_until TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    tombstoned_at TIMESTAMPTZ NULL,
    CONSTRAINT ck_managed_objects_security_domain CHECK (security_domain IN ('ManagedFile', 'Secret')),
    CONSTRAINT ck_managed_objects_state CHECK (state IN ('Staged', 'Ready', 'Orphaned', 'Missing', 'Corrupt', 'Tombstoned')),
    CONSTRAINT ck_managed_objects_layout CHECK (plaintext_length > 0 AND chunk_size BETWEEN 65536 AND 16777216 AND chunk_count > 0),
    CONSTRAINT ck_managed_objects_crypto CHECK
        (encryption_schema_version > 0 AND encryption_generation > 0 AND encryption_algorithm = 'AES-256-GCM'
         AND octet_length(wrapped_dek) = 32 AND octet_length(wrap_nonce) = 12 AND octet_length(wrap_tag) = 16),
    CONSTRAINT ck_managed_objects_storage_id CHECK (storage_id ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_managed_objects_plaintext_sha256 CHECK (plaintext_sha256 IS NULL OR plaintext_sha256 ~ '^[0-9a-f]{64}$'),
    CONSTRAINT ck_managed_objects_tombstone CHECK ((state = 'Tombstoned') = (tombstoned_at IS NOT NULL))
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_managed_objects_storage_id ON managed_objects(storage_id);
CREATE INDEX IF NOT EXISTS ix_managed_objects_scope_state
    ON managed_objects(tenant_id, owner_user_id, project_id, state);
CREATE INDEX IF NOT EXISTS ix_managed_objects_staged_until
    ON managed_objects(staged_until) WHERE state = 'Staged';

CREATE TABLE IF NOT EXISTS managed_object_chunks
(
    id UUID PRIMARY KEY,
    managed_object_id UUID NOT NULL REFERENCES managed_objects(id) ON DELETE CASCADE,
    chunk_index INTEGER NOT NULL,
    plaintext_offset BIGINT NOT NULL,
    plaintext_length INTEGER NOT NULL,
    ciphertext_length INTEGER NOT NULL,
    nonce BYTEA NOT NULL,
    authentication_tag BYTEA NOT NULL,
    plaintext_sha256 TEXT NOT NULL,
    ciphertext_sha256 TEXT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_managed_object_chunks_layout CHECK
        (chunk_index >= 0 AND plaintext_offset >= 0 AND plaintext_length > 0 AND ciphertext_length = plaintext_length),
    CONSTRAINT ck_managed_object_chunks_crypto CHECK
        (octet_length(nonce) = 12 AND octet_length(authentication_tag) = 16),
    CONSTRAINT ck_managed_object_chunks_hashes CHECK
        (plaintext_sha256 ~ '^[0-9a-f]{64}$' AND ciphertext_sha256 ~ '^[0-9a-f]{64}$')
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_managed_object_chunks_index
    ON managed_object_chunks(managed_object_id, chunk_index);
CREATE UNIQUE INDEX IF NOT EXISTS ix_managed_object_chunks_nonce
    ON managed_object_chunks(managed_object_id, nonce);

CREATE TABLE IF NOT EXISTS managed_transfer_sessions
(
    id UUID PRIMARY KEY,
    managed_object_id UUID NOT NULL REFERENCES managed_objects(id) ON DELETE CASCADE,
    tenant_id UUID NULL REFERENCES tenants(id) ON DELETE CASCADE,
    owner_user_id UUID NULL REFERENCES tenant_users(id) ON DELETE CASCADE,
    project_id TEXT NOT NULL,
    actor_id TEXT NOT NULL,
    agent_id TEXT NULL,
    execution_id UUID NULL,
    capability_id UUID NOT NULL,
    capability_hash TEXT NOT NULL,
    operation TEXT NOT NULL,
    purpose TEXT NOT NULL,
    max_bytes BIGINT NOT NULL,
    used_bytes BIGINT NOT NULL DEFAULT 0,
    max_concurrency INTEGER NOT NULL,
    revision BIGINT NOT NULL DEFAULT 1,
    encryption_generation INTEGER NOT NULL,
    state TEXT NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_managed_transfer_sessions_operation CHECK (operation IN ('Upload', 'Download')),
    CONSTRAINT ck_managed_transfer_sessions_state CHECK (state IN ('Active', 'Completed', 'Revoked', 'Expired')),
    CONSTRAINT ck_managed_transfer_sessions_limits CHECK
        (max_bytes > 0 AND used_bytes >= 0 AND used_bytes <= max_bytes AND max_concurrency BETWEEN 1 AND 8
         AND revision > 0 AND encryption_generation > 0),
    CONSTRAINT ck_managed_transfer_sessions_capability_hash CHECK (capability_hash ~ '^[0-9a-f]{64}$')
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_managed_transfer_sessions_capability_id
    ON managed_transfer_sessions(capability_id);
CREATE UNIQUE INDEX IF NOT EXISTS ix_managed_transfer_sessions_capability_hash
    ON managed_transfer_sessions(capability_hash);
CREATE INDEX IF NOT EXISTS ix_managed_transfer_sessions_scope_state
    ON managed_transfer_sessions(tenant_id, owner_user_id, project_id, state, expires_at);

CREATE TABLE IF NOT EXISTS managed_transfer_operations
(
    id UUID PRIMARY KEY,
    session_id UUID NOT NULL REFERENCES managed_transfer_sessions(id) ON DELETE CASCADE,
    request_id TEXT NOT NULL,
    request_hash TEXT NOT NULL,
    bytes_transferred BIGINT NOT NULL,
    created_at TIMESTAMPTZ NOT NULL,
    CONSTRAINT ck_managed_transfer_operations_request CHECK
        (length(request_id) BETWEEN 1 AND 200 AND request_hash ~ '^[0-9a-f]{64}$' AND bytes_transferred >= 0)
);
CREATE UNIQUE INDEX IF NOT EXISTS ix_managed_transfer_operations_request
    ON managed_transfer_operations(session_id, request_id);

CREATE TABLE IF NOT EXISTS chatgpt_oauth_token_state
(
    token_hash BYTEA PRIMARY KEY,
    token_kind TEXT NOT NULL,
    client_id TEXT NOT NULL,
    payload_json JSONB NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL,
    created_at TIMESTAMPTZ NOT NULL DEFAULT NOW(),
    CONSTRAINT ck_chatgpt_oauth_token_state_kind
        CHECK (token_kind IN ('authorization_code', 'refresh_token'))
);

CREATE INDEX IF NOT EXISTS ix_chatgpt_oauth_token_state_expiry
    ON chatgpt_oauth_token_state(expires_at);

CREATE INDEX IF NOT EXISTS ix_chatgpt_oauth_token_state_client_kind
    ON chatgpt_oauth_token_state(client_id, token_kind);

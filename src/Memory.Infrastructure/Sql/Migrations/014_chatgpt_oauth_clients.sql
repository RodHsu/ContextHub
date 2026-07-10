CREATE TABLE IF NOT EXISTS chatgpt_oauth_clients
(
    client_id TEXT PRIMARY KEY,
    redirect_uris TEXT[] NOT NULL,
    token_endpoint_auth_method TEXT NOT NULL,
    grant_types TEXT[] NOT NULL,
    response_types TEXT[] NOT NULL,
    registered_at TIMESTAMPTZ NOT NULL,
    expires_at TIMESTAMPTZ NOT NULL
);

CREATE INDEX IF NOT EXISTS ix_chatgpt_oauth_clients_expires_at
    ON chatgpt_oauth_clients(expires_at);

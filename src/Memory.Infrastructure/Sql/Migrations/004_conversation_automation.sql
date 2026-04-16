CREATE TABLE IF NOT EXISTS conversation_sessions
(
    id UUID PRIMARY KEY,
    conversation_id TEXT NOT NULL,
    project_id TEXT NOT NULL DEFAULT 'default',
    project_name TEXT NOT NULL DEFAULT '',
    task_id TEXT NOT NULL DEFAULT '',
    source_system TEXT NOT NULL,
    status TEXT NOT NULL DEFAULT 'Active',
    last_turn_id TEXT NOT NULL DEFAULT '',
    started_at TIMESTAMPTZ NOT NULL,
    last_checkpoint_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS conversation_checkpoints
(
    id UUID PRIMARY KEY,
    session_id UUID NOT NULL REFERENCES conversation_sessions(id) ON DELETE CASCADE,
    conversation_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    project_id TEXT NOT NULL DEFAULT 'default',
    project_name TEXT NOT NULL DEFAULT '',
    task_id TEXT NOT NULL DEFAULT '',
    source_system TEXT NOT NULL,
    event_type TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    source_ref TEXT NOT NULL,
    user_message_summary TEXT NOT NULL DEFAULT '',
    agent_message_summary TEXT NOT NULL DEFAULT '',
    tool_calls_json JSONB NOT NULL DEFAULT '[]'::jsonb,
    session_summary TEXT NOT NULL DEFAULT '',
    short_excerpt TEXT NOT NULL DEFAULT '',
    dedup_key TEXT NOT NULL,
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL
);

CREATE TABLE IF NOT EXISTS conversation_insights
(
    id UUID PRIMARY KEY,
    session_id UUID NOT NULL REFERENCES conversation_sessions(id) ON DELETE CASCADE,
    checkpoint_id UUID NOT NULL REFERENCES conversation_checkpoints(id) ON DELETE CASCADE,
    conversation_id TEXT NOT NULL,
    turn_id TEXT NOT NULL,
    project_id TEXT NOT NULL DEFAULT 'default',
    project_name TEXT NOT NULL DEFAULT '',
    task_id TEXT NOT NULL DEFAULT '',
    source_system TEXT NOT NULL,
    source_kind TEXT NOT NULL,
    insight_type TEXT NOT NULL,
    title TEXT NOT NULL,
    content TEXT NOT NULL,
    summary TEXT NOT NULL,
    source_ref TEXT NOT NULL,
    tags TEXT[] NOT NULL DEFAULT '{}',
    importance NUMERIC(5,4) NOT NULL DEFAULT 0.5,
    confidence NUMERIC(5,4) NOT NULL DEFAULT 0.5,
    dedup_key TEXT NOT NULL,
    promotion_status TEXT NOT NULL DEFAULT 'Pending',
    promoted_memory_id UUID NULL,
    error TEXT NOT NULL DEFAULT '',
    metadata_json JSONB NOT NULL DEFAULT '{}'::jsonb,
    created_at TIMESTAMPTZ NOT NULL,
    updated_at TIMESTAMPTZ NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS ix_conversation_sessions_source_conversation
    ON conversation_sessions(source_system, conversation_id);

CREATE UNIQUE INDEX IF NOT EXISTS ix_conversation_checkpoints_dedup_key
    ON conversation_checkpoints(dedup_key);

CREATE INDEX IF NOT EXISTS ix_conversation_checkpoints_created_at
    ON conversation_checkpoints(created_at DESC);

CREATE INDEX IF NOT EXISTS ix_conversation_checkpoints_project_created_at
    ON conversation_checkpoints(project_id, created_at DESC);

CREATE UNIQUE INDEX IF NOT EXISTS ix_conversation_insights_dedup_key
    ON conversation_insights(dedup_key);

CREATE INDEX IF NOT EXISTS ix_conversation_insights_promotion_status_created_at
    ON conversation_insights(promotion_status, created_at);

CREATE INDEX IF NOT EXISTS ix_conversation_insights_project_updated_at
    ON conversation_insights(project_id, updated_at DESC);

CREATE INDEX IF NOT EXISTS ix_memory_jobs_conversation_types
    ON memory_jobs(job_type, status, created_at)
    WHERE job_type IN ('IngestConversation', 'PromoteConversationInsights');

-- Pippin's memory. The AI remembers nothing between calls, so the workflow loads this before every
-- message and saves it after: where each user is in the conversation, and what was said.
COMMENT ON SCHEMA agents IS 'LifeAim Agents module: Pippin''s conversations (workflow stage and history).';

-- One small row per user, updated on every message: the workflow's state.
CREATE TABLE agents.conversations (
    user_id    uuid        PRIMARY KEY,
    stage      text        NOT NULL,
    user_name  text        NULL,
    updated_at timestamptz NOT NULL
);

-- Append-only: each message is a new row, old ones are never rewritten.
CREATE TABLE agents.messages (
    id         uuid        PRIMARY KEY,
    user_id    uuid        NOT NULL,
    role       text        NOT NULL CHECK (role IN ('user', 'assistant')),
    content    text        NOT NULL,
    stage      text        NOT NULL,
    created_at timestamptz NOT NULL
);

CREATE INDEX ix_messages_user_id_created_at ON agents.messages (user_id, created_at);

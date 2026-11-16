-- Funcionalidade 2: the first plan proposal of the user's goals, written in the background, offered when ready and
-- refined with the user one goal at a time.
COMMENT ON SCHEMA planning IS 'LifeAim Planning module: plan proposals prepared in the background.';

-- user_id belongs to another module, so there is no foreign key (same as progress.progress_entries).
CREATE TABLE planning.proposals (
    id           uuid        PRIMARY KEY,
    user_id      uuid        NOT NULL,
    status       text        NOT NULL CHECK (status IN ('requested', 'ready', 'offered', 'closed', 'failed')),
    attempts     integer     NOT NULL DEFAULT 0 CHECK (attempts >= 0),
    last_error   text        NULL,
    requested_at timestamptz NOT NULL,
    ready_at     timestamptz NULL,
    offered_at   timestamptz NULL,
    closed_at    timestamptz NULL
);

-- At most one proposal in progress per user: a new request closes the previous one first.
CREATE UNIQUE INDEX ux_proposals_one_open ON planning.proposals (user_id)
    WHERE status IN ('requested', 'ready', 'offered');

-- The background job looks for work by status.
CREATE INDEX ix_proposals_status ON planning.proposals (status, requested_at);

CREATE TABLE planning.goal_proposals (
    proposal_id uuid        NOT NULL REFERENCES planning.proposals (id),
    goal_id     uuid        NOT NULL,
    position    integer     NOT NULL,
    message     text        NOT NULL CHECK (length(btrim(message)) BETWEEN 1 AND 2000),
    question    text        NOT NULL CHECK (length(btrim(question)) BETWEEN 1 AND 500),
    routines    jsonb       NOT NULL,
    status      text        NOT NULL CHECK (status IN ('open', 'accepted', 'skipped')),
    updated_at  timestamptz NOT NULL,
    PRIMARY KEY (proposal_id, goal_id)
);

COMMENT ON COLUMN planning.goal_proposals.message IS 'The concrete suggestion and its short reasoning, shown before the question.';
COMMENT ON COLUMN planning.goal_proposals.question IS 'The contextual question that adapts the suggestion to the user.';
COMMENT ON COLUMN planning.goal_proposals.routines IS 'The suggested routines (same shape as goals.goal_routines), as JSON.';

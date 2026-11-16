-- What the user actually did (Funcionalidade 3: "realidade" and "histórico"), reported by conversation:
-- "hoje li 10 páginas", "corri 5 km ontem", "pesei 92 kg". One row per report, never rewritten.
COMMENT ON SCHEMA progress IS 'LifeAim Progress module: what the user reported having done for each goal.';

-- user_id and goal_id belong to other modules' schemas, so there are no foreign keys: the Agents module checks
-- through IGoalService that the goal is the user's before recording.
CREATE TABLE progress.progress_entries (
    id          uuid        PRIMARY KEY,
    user_id     uuid        NOT NULL,
    goal_id     uuid        NOT NULL,
    occurred_on date        NOT NULL,
    amount      numeric     NULL CHECK (amount > 0),
    unit        text        NULL CHECK (length(unit) <= 30),
    note        text        NOT NULL CHECK (length(btrim(note)) BETWEEN 1 AND 500),
    created_at  timestamptz NOT NULL,
    -- A unit alone says nothing.
    CHECK (unit IS NULL OR amount IS NOT NULL)
);

CREATE INDEX ix_progress_entries_goal ON progress.progress_entries (user_id, goal_id, occurred_on DESC, created_at DESC);

COMMENT ON COLUMN progress.progress_entries.occurred_on IS 'The day it happened in the user''s calendar ("ontem").';
COMMENT ON COLUMN progress.progress_entries.amount IS 'How much (10, with unit "páginas"); not necessarily the goal''s indicator.';
COMMENT ON COLUMN progress.progress_entries.note IS 'What was done, in short words ("leu 10 páginas do livro").';

-- Funcionalidade 5: the conversations Pippin started about a goal with recurring difficulty or a conflict between goals,
-- so the user is not asked again too soon, plus what the user said was in the way and whether the adjustment was saved.
-- user_id and the goal ids belong to other modules, so there are no foreign keys (same as the other modules).
CREATE TABLE reporting.balance_reviews (
    id                 uuid        PRIMARY KEY,
    user_id            uuid        NOT NULL,
    focus_goal_id      uuid        NOT NULL,
    signals            text[]      NOT NULL CHECK (cardinality(signals) > 0),
    protected_goal_ids uuid[]      NOT NULL,
    message            text        NOT NULL CHECK (length(message) <= 4000),
    status             text        NOT NULL CHECK (status IN ('offered', 'explained', 'accepted', 'declined')),
    obstacle           text        NULL CHECK (length(obstacle) <= 1000),
    created_at         timestamptz NOT NULL,
    answered_at        timestamptz NULL
);

CREATE INDEX ix_balance_reviews_user_created ON reporting.balance_reviews (user_id, created_at DESC);

COMMENT ON COLUMN reporting.balance_reviews.signals IS 'Why the review started: recurring_difficulty, simultaneous_drop, lack_of_time_or_energy.';
COMMENT ON COLUMN reporting.balance_reviews.protected_goal_ids IS 'The goals working well when the review started: the adjustment never touches them.';
COMMENT ON COLUMN reporting.balance_reviews.obstacle IS 'What the user said is in the way, in their words.';

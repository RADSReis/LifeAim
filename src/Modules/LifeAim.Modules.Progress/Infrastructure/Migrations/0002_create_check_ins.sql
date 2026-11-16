-- Funcionalidade 3 (acompanhamento diário): each time Pippin asks about a planned activity, and what the user answered.
-- It joins the plan (goals.goal_routines) to reality (progress_entries): "planejado, realizado e não confirmado são
-- situações diferentes". A row exists only once Pippin asked (or the user reported first): a planned day with no row
-- was never asked, and a row still 'asked' after its day is "não confirmado", never a failure.
CREATE TABLE progress.check_ins (
    id                uuid        PRIMARY KEY,
    user_id           uuid        NOT NULL,
    goal_id           uuid        NOT NULL,
    -- goals.goal_routines is replaced when the plan changes, so the id may stop existing; activity keeps the history.
    routine_id        uuid        NOT NULL,
    day               date        NOT NULL,
    activity          text        NOT NULL CHECK (length(btrim(activity)) BETWEEN 1 AND 100),
    status            text        NOT NULL CHECK (status IN ('asked', 'done', 'missed')),
    times_asked       integer     NOT NULL CHECK (times_asked >= 1),
    first_asked_at    timestamptz NOT NULL,
    last_asked_at     timestamptz NOT NULL,
    answered_at       timestamptz NULL,
    reason            text        NULL CHECK (length(reason) <= 500),
    progress_entry_id uuid        NULL REFERENCES progress.progress_entries (id),
    CHECK ((status = 'asked') = (answered_at IS NULL)),
    CHECK (status = 'missed' OR reason IS NULL)
);

-- One check-in per activity per day: asking again only counts the new attempt.
CREATE UNIQUE INDEX ux_check_ins_routine_day ON progress.check_ins (routine_id, day);

CREATE INDEX ix_check_ins_user_day ON progress.check_ins (user_id, day);

COMMENT ON TABLE progress.check_ins IS 'Pippin asked about a planned activity of that day; status is what the user answered.';
COMMENT ON COLUMN progress.check_ins.times_asked IS 'How many messages asked about it (a second attempt later that day, or the next day).';
COMMENT ON COLUMN progress.check_ins.reason IS 'Why it did not happen, in the user''s words, when they said.';

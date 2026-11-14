-- How the user intends to reach a goal (Funcionalidade 3, "intenção, planejamento"), in two forms:
-- the plan exactly as the user wrote it (user_plan), and the same plan broken into routines that the daily
-- follow-up can query ("which routines are this morning?").
ALTER TABLE goals.goals
    ADD COLUMN user_plan text NULL;

COMMENT ON COLUMN goals.goals.user_plan IS 'The plan as the user wrote it ("vou ler seg, qua e sex de manhã").';

-- One recurring activity of a goal's plan. A goal may have several ("academia seg/qua/sex de manhã" and
-- "corrida no sábado"). Modelled on iCalendar's RRULE (FREQ, INTERVAL, BYDAY) and on habit trackers'
-- "X times per period", as columns instead of an RRULE string so they can be queried.
CREATE TABLE goals.goal_routines (
    id               uuid        PRIMARY KEY,
    goal_id          uuid        NOT NULL REFERENCES goals.goals (id) ON DELETE CASCADE,
    user_id          uuid        NOT NULL,
    activity         text        NOT NULL CHECK (length(btrim(activity)) BETWEEN 1 AND 100),
    frequency        text        NOT NULL CHECK (frequency IN ('daily', 'weekly', 'monthly')),
    interval         integer     NOT NULL DEFAULT 1 CHECK (interval >= 1),
    -- ISO weekdays: 1 = Monday … 7 = Sunday. Empty when the days are not fixed.
    days_of_week     integer[]   NOT NULL DEFAULT '{}' CHECK (days_of_week <@ ARRAY[1, 2, 3, 4, 5, 6, 7]),
    times_per_period integer     NULL CHECK (times_per_period >= 1),
    time_of_day      time        NULL,
    day_period       text        NULL CHECK (day_period IN ('morning', 'afternoon', 'evening', 'night')),
    amount           numeric     NULL CHECK (amount > 0),
    unit             text        NULL CHECK (length(unit) <= 30),
    duration_minutes integer     NULL CHECK (duration_minutes > 0),
    source           text        NOT NULL CHECK (source IN ('user', 'pippin')),
    starts_on        date        NULL,
    ends_on          date        NULL CHECK (ends_on >= starts_on),
    created_at       timestamptz NOT NULL,
    -- Fixed days only make sense weekly, and fixed days already say how many times.
    CHECK (cardinality(days_of_week) = 0 OR frequency = 'weekly'),
    CHECK (cardinality(days_of_week) = 0 OR times_per_period IS NULL)
);

CREATE INDEX ix_goal_routines_goal_id ON goals.goal_routines (goal_id, created_at);
CREATE INDEX ix_goal_routines_user_id ON goals.goal_routines (user_id);

COMMENT ON COLUMN goals.goal_routines.times_per_period IS '"3 vezes por semana" without fixed days.';
COMMENT ON COLUMN goals.goal_routines.time_of_day IS 'Only when the user said an exact time ("às 9h30").';
COMMENT ON COLUMN goals.goal_routines.day_period IS 'An approximate period ("de manhã"); never turned into an exact time.';
COMMENT ON COLUMN goals.goal_routines.amount IS 'How much per session (10, with unit "páginas").';
COMMENT ON COLUMN goals.goal_routines.source IS 'user: the user described it; pippin: Pippin proposed it.';

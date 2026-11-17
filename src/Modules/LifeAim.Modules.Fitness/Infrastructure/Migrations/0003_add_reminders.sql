-- Reminders of the journal that Pippin sends on his own, each one switched on by the user in conversation
-- ("me lembra de pesar às 8h", "me manda o fechamento do dia às 21h30", "me lembra da vitamina às 9h").
CREATE TABLE fitness.reminders (
    id         uuid        PRIMARY KEY,
    user_id    uuid        NOT NULL,
    kind       text        NOT NULL CHECK (kind IN ('weigh_in', 'no_record', 'day_close', 'custom')),
    remind_at  time        NOT NULL,
    text       text        NULL CHECK (length(btrim(text)) BETWEEN 1 AND 120),
    created_at timestamptz NOT NULL,
    -- Only a free reminder says what it is about.
    CHECK ((kind = 'custom') = (text IS NOT NULL))
);

-- One of each built-in kind per user; a free reminder is told apart by its text.
CREATE UNIQUE INDEX ux_reminders_user_kind ON fitness.reminders (user_id, kind) WHERE kind <> 'custom';
CREATE UNIQUE INDEX ux_reminders_user_text ON fitness.reminders (user_id, lower(text)) WHERE kind = 'custom';

COMMENT ON COLUMN fitness.reminders.kind IS 'weigh_in = no weight yet today; no_record = no meal recorded yet; day_close = the summary of the day; custom = a free reminder.';
COMMENT ON COLUMN fitness.reminders.remind_at IS 'The local time the user chose. A time after midnight still belongs to the food day before.';

-- What happened to each reminder on each food day: sent at most once, and (free reminders) done or not.
-- The job only looks here, so a restart never sends a reminder twice.
CREATE TABLE fitness.reminder_log (
    reminder_id uuid        NOT NULL REFERENCES fitness.reminders (id) ON DELETE CASCADE,
    day         date        NOT NULL,
    sent_at     timestamptz NULL,
    done_at     timestamptz NULL,
    PRIMARY KEY (reminder_id, day)
);

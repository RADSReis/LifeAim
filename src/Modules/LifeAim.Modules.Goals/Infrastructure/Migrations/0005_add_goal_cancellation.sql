-- Cancelling a goal by conversation (Funcionalidade 1, decision 5) does not delete it: Funcionalidade 6 asks to
-- keep the history of a goal even when it is not completed. A cancelled goal leaves every list and stays here.
ALTER TABLE goals.goals
    ADD COLUMN status       text        NOT NULL DEFAULT 'active' CHECK (status IN ('active', 'cancelled')),
    ADD COLUMN cancelled_at timestamptz NULL,
    ADD CHECK ((status = 'cancelled') = (cancelled_at IS NOT NULL));

COMMENT ON COLUMN goals.goals.status IS 'active, or cancelled by the user (kept for the history, hidden from the lists).';

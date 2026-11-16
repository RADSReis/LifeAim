-- Funcionalidade 4: the monthly summaries (and quarterly panoramas) Pippin sent, one per user per month, so a month is
-- never summarized twice, plus the insight offered after it and whether the user wanted to hear it.
COMMENT ON SCHEMA reporting IS 'LifeAim Reporting module: monthly summaries and quarterly panoramas sent to the user.';

-- user_id belongs to another module, so there is no foreign key (same as the other modules).
CREATE TABLE reporting.summaries (
    id                  uuid        PRIMARY KEY,
    user_id             uuid        NOT NULL,
    month               date        NOT NULL CHECK (extract(day FROM month) = 1),
    status              text        NOT NULL CHECK (status IN ('sent', 'skipped')),
    includes_quarter    boolean     NOT NULL,
    message             text        NULL CHECK (length(message) <= 4000),
    insight_teaser      text        NULL CHECK (length(insight_teaser) <= 500),
    insight             text        NULL CHECK (length(insight) <= 2000),
    insight_status      text        NOT NULL CHECK (insight_status IN ('none', 'offered', 'revealed', 'declined')),
    created_at          timestamptz NOT NULL,
    insight_answered_at timestamptz NULL,
    CHECK ((status = 'sent') = (message IS NOT NULL)),
    CHECK ((insight_status = 'none') = (insight IS NULL))
);

CREATE UNIQUE INDEX ux_summaries_user_month ON reporting.summaries (user_id, month);

COMMENT ON COLUMN reporting.summaries.month IS 'The first day of the month summarized.';
COMMENT ON COLUMN reporting.summaries.includes_quarter IS 'The month closed a quarter, so the message also has the quarterly panorama.';
COMMENT ON COLUMN reporting.summaries.insight IS 'The pattern found in the history, revealed only if the user accepts the invitation.';

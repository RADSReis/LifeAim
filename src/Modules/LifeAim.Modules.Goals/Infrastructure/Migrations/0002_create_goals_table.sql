-- A goal as the goal-intake conversation understands it (Funcionalidade 1): only the name is required;
-- description, measurable target and due date may stay unknown ("A ser determinado").
-- user_id is LifeAim's user id; it lives in another module's schema, so there is no foreign key.
CREATE TABLE goals.goals (
    id          uuid        PRIMARY KEY,
    user_id     uuid        NOT NULL,
    name        text        NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 200),
    description text        NULL,
    indicator   text        NULL,
    due_date    date        NULL,
    created_at  timestamptz NOT NULL
);

CREATE INDEX ix_goals_user_id ON goals.goals (user_id, created_at);

-- Who is who on each channel: the Discord user 123... is LifeAim user <id>. The rest of LifeAim only
-- knows the LifeAim id, so moving to (or adding) another channel keeps the user's goals.
COMMENT ON SCHEMA communication IS 'LifeAim Communication module: channels (Discord) and their users.';

CREATE TABLE communication.channel_users (
    id           uuid        PRIMARY KEY,
    channel      text        NOT NULL,
    external_id  text        NOT NULL,
    display_name text        NOT NULL,
    created_at   timestamptz NOT NULL,
    CONSTRAINT uq_channel_users_channel_external_id UNIQUE (channel, external_id)
);

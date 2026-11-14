-- The goals Pippin understood but the user has not confirmed yet (and the period they chose), carried
-- from one message to the next. jsonb: it is the workflow's working memory, its shape follows the code
-- (GoalsDraft) and nothing queries inside it; confirmed goals go to goals.goals, with real columns.
ALTER TABLE agents.conversations ADD COLUMN draft jsonb NULL;

COMMENT ON COLUMN agents.conversations.draft IS 'Unconfirmed goals and the chosen period (GoalsDraft as JSON).';

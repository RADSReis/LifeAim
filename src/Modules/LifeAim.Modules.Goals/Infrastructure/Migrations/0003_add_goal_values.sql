-- A measurable goal as numbers, so progress can be computed later ("faltam 2 kg"):
-- "estou com 94 kg e quero chegar a 90 kg" → start_value 94, target_value 90, unit 'kg'.
-- All optional: many goals are not measurable, or the user hasn't said the numbers yet.
ALTER TABLE goals.goals
    ADD COLUMN start_value  numeric NULL,
    ADD COLUMN target_value numeric NULL,
    ADD COLUMN unit         text    NULL CHECK (length(unit) <= 30);

COMMENT ON COLUMN goals.goals.indicator IS 'What is measured, in words (e.g. "peso corporal", "livros terminados").';
COMMENT ON COLUMN goals.goals.start_value IS 'Where the user is when the goal is created (e.g. 94).';
COMMENT ON COLUMN goals.goals.target_value IS 'Where the user wants to get (e.g. 90).';
COMMENT ON COLUMN goals.goals.unit IS 'Unit of start_value and target_value (e.g. kg, livros, km).';

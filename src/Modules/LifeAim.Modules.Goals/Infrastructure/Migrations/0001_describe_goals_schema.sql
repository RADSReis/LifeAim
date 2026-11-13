-- First Goals migration: proves the Migrator pipeline end to end. LifeAim.Migrator creates the
-- schema itself (DbUp keeps its journal there), so this script only describes it.
COMMENT ON SCHEMA goals IS 'LifeAim Goals module: the user''s goals and their measurable targets.';

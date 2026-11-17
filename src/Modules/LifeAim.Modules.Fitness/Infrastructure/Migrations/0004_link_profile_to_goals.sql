-- Which saved goals the journal belongs to: the weight goal (a weigh-in is progress of it) and the training goal (a
-- workout is progress of it). Learned the first time a record is tied to a goal, and used from then on, so the daily
-- follow-up does not ask "foi à academia?" about a workout already in the journal.
-- The ids belong to the Goals module's schema: no foreign keys; the caller checks that the goal is still active.
ALTER TABLE fitness.profiles ADD COLUMN weight_goal_id uuid NULL;
ALTER TABLE fitness.profiles ADD COLUMN exercise_goal_id uuid NULL;

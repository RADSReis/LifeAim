-- The weight-loss journal (goals with a specialty: "emagrecimento / academia"). What the user used to keep by hand
-- in text files, told to Pippin by conversation: what they ate, what they weigh and measure, how they trained.
COMMENT ON SCHEMA fitness IS 'LifeAim Fitness module: the food, body and exercise journal Pippin keeps by conversation.';

-- user_id belongs to another module's schema, so there are no foreign keys to it.

-- What Pippin needs to know about the person to do the maths (one row per user, filled little by little).
CREATE TABLE fitness.profiles (
    user_id             uuid        PRIMARY KEY,
    height_cm           numeric     NULL CHECK (height_cm BETWEEN 80 AND 250),
    birth_year          integer     NULL CHECK (birth_year BETWEEN 1900 AND 2100),
    sex                 text        NULL CHECK (sex IN ('male', 'female')),
    target_weight_kg    numeric     NULL CHECK (target_weight_kg BETWEEN 20 AND 400),
    daily_energy_kcal   integer     NULL CHECK (daily_energy_kcal BETWEEN 800 AND 8000),
    calorie_target_kcal integer     NULL CHECK (calorie_target_kcal BETWEEN 800 AND 8000),
    protein_target_g    integer     NULL CHECK (protein_target_g BETWEEN 10 AND 500),
    notes               text        NULL CHECK (length(notes) <= 2000),
    updated_at          timestamptz NOT NULL
);

COMMENT ON COLUMN fitness.profiles.daily_energy_kcal IS 'Estimated daily expenditure WITHOUT the workouts (metabolism + normal activities).';
COMMENT ON COLUMN fitness.profiles.calorie_target_kcal IS 'How much to eat per day, when the user (or a nutritionist) set a number.';
COMMENT ON COLUMN fitness.profiles.notes IS 'Routine, restrictions, preferences: free text, in the words of the user.';

-- Weight, circumferences and bioimpedance: one row per day, completed as the user tells more ("pesei 94,5" in the
-- morning, the smartwatch numbers later). A value told again on the same day replaces the earlier one.
CREATE TABLE fitness.body_records (
    user_id     uuid        NOT NULL,
    day         date        NOT NULL,
    weight_kg   numeric     NULL CHECK (weight_kg BETWEEN 20 AND 400),
    waist_cm    numeric     NULL CHECK (waist_cm BETWEEN 30 AND 300),
    hip_cm      numeric     NULL CHECK (hip_cm BETWEEN 30 AND 300),
    chest_cm    numeric     NULL CHECK (chest_cm BETWEEN 30 AND 300),
    muscle_kg   numeric     NULL CHECK (muscle_kg BETWEEN 5 AND 200),
    fat_kg      numeric     NULL CHECK (fat_kg BETWEEN 1 AND 300),
    water_kg    numeric     NULL CHECK (water_kg BETWEEN 5 AND 200),
    fat_percent numeric     NULL CHECK (fat_percent BETWEEN 1 AND 80),
    bmi         numeric     NULL CHECK (bmi BETWEEN 5 AND 100),
    bmr_kcal    integer     NULL CHECK (bmr_kcal BETWEEN 500 AND 6000),
    note        text        NULL CHECK (length(note) <= 500),
    updated_at  timestamptz NOT NULL,
    PRIMARY KEY (user_id, day)
);

COMMENT ON COLUMN fitness.body_records.waist_cm IS 'Circunferência abdominal.';

-- One meal (or snack): when, and what it was called. The day is the "food day": a ceia after midnight still
-- belongs to the day before (the caller decides).
CREATE TABLE fitness.meals (
    id         uuid        PRIMARY KEY,
    user_id    uuid        NOT NULL,
    day        date        NOT NULL,
    eaten_at   time        NULL,
    name       text        NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 80),
    created_at timestamptz NOT NULL
);

CREATE INDEX ix_meals_user_day ON fitness.meals (user_id, day);

-- What was in it. Calories and protein are a range (an estimate is honest about its doubt); an exact value from a
-- label has min = max. Carbs and fat are kept when known.
CREATE TABLE fitness.meal_items (
    meal_id       uuid    NOT NULL REFERENCES fitness.meals (id) ON DELETE CASCADE,
    position      integer NOT NULL,
    description   text    NOT NULL CHECK (length(btrim(description)) BETWEEN 1 AND 200),
    quantity      numeric NULL CHECK (quantity > 0),
    unit          text    NULL CHECK (length(unit) <= 20),
    kcal_min      numeric NOT NULL CHECK (kcal_min >= 0),
    kcal_max      numeric NOT NULL CHECK (kcal_max >= kcal_min),
    protein_min_g numeric NOT NULL CHECK (protein_min_g >= 0),
    protein_max_g numeric NOT NULL CHECK (protein_max_g >= protein_min_g),
    carbs_g       numeric NULL CHECK (carbs_g >= 0),
    fat_g         numeric NULL CHECK (fat_g >= 0),
    source        text    NOT NULL CHECK (source IN ('user', 'food_table', 'estimate')),
    PRIMARY KEY (meal_id, position)
);

COMMENT ON COLUMN fitness.meal_items.source IS 'user = the user said the numbers; food_table = from fitness.foods; estimate = estimated by Pippin.';

CREATE TABLE fitness.exercises (
    id               uuid        PRIMARY KEY,
    user_id          uuid        NOT NULL,
    day              date        NOT NULL,
    done_at          time        NULL,
    activity         text        NOT NULL CHECK (length(btrim(activity)) BETWEEN 1 AND 200),
    duration_minutes integer     NULL CHECK (duration_minutes BETWEEN 1 AND 1440),
    kcal_min         numeric     NULL CHECK (kcal_min >= 0),
    kcal_max         numeric     NULL CHECK (kcal_max >= kcal_min),
    measured         boolean     NOT NULL,
    created_at       timestamptz NOT NULL,
    CHECK ((kcal_min IS NULL) = (kcal_max IS NULL))
);

CREATE INDEX ix_exercises_user_day ON fitness.exercises (user_id, day);

COMMENT ON COLUMN fitness.exercises.measured IS 'true when the calories came from a device (smartwatch); false for an estimate.';

-- The personal nutrition table: products whose label the user read to Pippin ("o iogurte Vigor zero tem 51 kcal").
-- It wins over any estimate the next time the food appears.
CREATE TABLE fitness.foods (
    id         uuid        PRIMARY KEY,
    user_id    uuid        NOT NULL,
    name       text        NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 120),
    serving    text        NOT NULL CHECK (length(btrim(serving)) BETWEEN 1 AND 60),
    kcal       numeric     NOT NULL CHECK (kcal >= 0),
    protein_g  numeric     NULL CHECK (protein_g >= 0),
    carbs_g    numeric     NULL CHECK (carbs_g >= 0),
    fat_g      numeric     NULL CHECK (fat_g >= 0),
    updated_at timestamptz NOT NULL
);

CREATE UNIQUE INDEX ux_foods_user_name ON fitness.foods (user_id, lower(name));

COMMENT ON COLUMN fitness.foods.serving IS 'The portion the numbers are for, as on the label ("30 g (1 scoop)", "1 pote de 90 g").';

-- What explains a day: "dormi mal", "aniversário da esposa", "vitamina tomada", "resfriado, sem treino".
CREATE TABLE fitness.day_notes (
    id         uuid        PRIMARY KEY,
    user_id    uuid        NOT NULL,
    day        date        NOT NULL,
    note       text        NOT NULL CHECK (length(btrim(note)) BETWEEN 1 AND 500),
    created_at timestamptz NOT NULL
);

CREATE INDEX ix_day_notes_user_day ON fitness.day_notes (user_id, day);

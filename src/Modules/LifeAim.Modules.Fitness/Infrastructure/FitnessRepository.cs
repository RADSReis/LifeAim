using LifeAim.Modules.Fitness.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace LifeAim.Modules.Fitness.Infrastructure;

/// <summary>Plain ADO.NET over the "fitness" schema. Every value goes in as a parameter, never as SQL text.</summary>
internal sealed class FitnessRepository(NpgsqlDataSource dataSource)
{
    private const string ProfileColumns =
        "user_id, height_cm, birth_year, sex, target_weight_kg, daily_energy_kcal, calorie_target_kcal, protein_target_g, notes, updated_at, " +
        "weight_goal_id, exercise_goal_id";

    private const string BodyColumns =
        "day, weight_kg, waist_cm, hip_cm, chest_cm, muscle_kg, fat_kg, water_kg, fat_percent, bmi, bmr_kcal, note";

    public async Task<FitnessProfileDto?> GetProfileAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand($"SELECT {ProfileColumns} FROM fitness.profiles WHERE user_id = $1");
        command.Parameters.Add(new() { Value = userId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadProfile(reader) : null;
    }

    // Only the fields given change: COALESCE keeps what was there.
    public async Task<FitnessProfileDto> SaveProfileAsync(SaveProfileRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO fitness.profiles AS p ({ProfileColumns})
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)
            ON CONFLICT (user_id) DO UPDATE SET
                weight_goal_id      = COALESCE(EXCLUDED.weight_goal_id, p.weight_goal_id),
                exercise_goal_id    = COALESCE(EXCLUDED.exercise_goal_id, p.exercise_goal_id),
                height_cm           = COALESCE(EXCLUDED.height_cm, p.height_cm),
                birth_year          = COALESCE(EXCLUDED.birth_year, p.birth_year),
                sex                 = COALESCE(EXCLUDED.sex, p.sex),
                target_weight_kg    = COALESCE(EXCLUDED.target_weight_kg, p.target_weight_kg),
                daily_energy_kcal   = COALESCE(EXCLUDED.daily_energy_kcal, p.daily_energy_kcal),
                calorie_target_kcal = COALESCE(EXCLUDED.calorie_target_kcal, p.calorie_target_kcal),
                protein_target_g    = COALESCE(EXCLUDED.protein_target_g, p.protein_target_g),
                notes               = COALESCE(EXCLUDED.notes, p.notes),
                updated_at          = EXCLUDED.updated_at
            RETURNING {ProfileColumns}
            """);
        command.Parameters.Add(new() { Value = request.UserId });
        Add(command, request.HeightCm);
        Add(command, request.BirthYear);
        Add(command, request.Sex is { } sex ? SexText(sex) : null);
        Add(command, request.TargetWeightKg);
        Add(command, request.DailyEnergyKcal);
        Add(command, request.CalorieTargetKcal);
        Add(command, request.ProteinTargetG);
        Add(command, request.Notes);
        command.Parameters.Add(new() { Value = now });
        Add(command, request.WeightGoalId);
        Add(command, request.ExerciseGoalId);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadProfile(reader);
    }

    public async Task<BodyRecordDto> SaveBodyAsync(RecordBodyRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO fitness.body_records AS b (user_id, {BodyColumns}, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
            ON CONFLICT (user_id, day) DO UPDATE SET
                weight_kg   = COALESCE(EXCLUDED.weight_kg, b.weight_kg),
                waist_cm    = COALESCE(EXCLUDED.waist_cm, b.waist_cm),
                hip_cm      = COALESCE(EXCLUDED.hip_cm, b.hip_cm),
                chest_cm    = COALESCE(EXCLUDED.chest_cm, b.chest_cm),
                muscle_kg   = COALESCE(EXCLUDED.muscle_kg, b.muscle_kg),
                fat_kg      = COALESCE(EXCLUDED.fat_kg, b.fat_kg),
                water_kg    = COALESCE(EXCLUDED.water_kg, b.water_kg),
                fat_percent = COALESCE(EXCLUDED.fat_percent, b.fat_percent),
                bmi         = COALESCE(EXCLUDED.bmi, b.bmi),
                bmr_kcal    = COALESCE(EXCLUDED.bmr_kcal, b.bmr_kcal),
                note        = COALESCE(EXCLUDED.note, b.note),
                updated_at  = EXCLUDED.updated_at
            RETURNING {BodyColumns}
            """);
        command.Parameters.Add(new() { Value = request.UserId });
        command.Parameters.Add(new() { Value = request.Day });
        Add(command, request.WeightKg);
        Add(command, request.WaistCm);
        Add(command, request.HipCm);
        Add(command, request.ChestCm);
        Add(command, request.MuscleKg);
        Add(command, request.FatKg);
        Add(command, request.WaterKg);
        Add(command, request.FatPercent);
        Add(command, request.Bmi);
        Add(command, request.BmrKcal);
        Add(command, request.Note);
        command.Parameters.Add(new() { Value = now });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadBody(reader);
    }

    public async Task<IReadOnlyList<BodyRecordDto>> ListBodyAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {BodyColumns} FROM fitness.body_records WHERE user_id = $1 AND day BETWEEN $2 AND $3 ORDER BY day");
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = from });
        command.Parameters.Add(new() { Value = to });

        var records = new List<BodyRecordDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            records.Add(ReadBody(reader));
        }

        return records;
    }

    public async Task AddMealAsync(Guid userId, MealDto meal, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var command = new NpgsqlCommand(
                         "INSERT INTO fitness.meals (id, user_id, day, eaten_at, name, created_at) VALUES ($1, $2, $3, $4, $5, $6)",
                         connection, transaction))
        {
            command.Parameters.Add(new() { Value = meal.Id });
            command.Parameters.Add(new() { Value = userId });
            command.Parameters.Add(new() { Value = meal.Day });
            Add(command, meal.EatenAt);
            command.Parameters.Add(new() { Value = meal.Name });
            command.Parameters.Add(new() { Value = now });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await InsertItemsAsync(connection, transaction, meal.Id, meal.Items, cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<MealDto?> UpdateMealAsync(UpdateMealRequest request, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        DateOnly day;
        TimeOnly? eatenAt;
        string name;
        await using (var command = new NpgsqlCommand(
                         """
                         UPDATE fitness.meals
                         SET name = COALESCE($3, name), eaten_at = COALESCE($4, eaten_at)
                         WHERE id = $1 AND user_id = $2
                         RETURNING day, eaten_at, name
                         """,
                         connection, transaction))
        {
            command.Parameters.Add(new() { Value = request.MealId });
            command.Parameters.Add(new() { Value = request.UserId });
            Add(command, request.Name);
            Add(command, request.EatenAt);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            day = reader.GetFieldValue<DateOnly>(0);
            eatenAt = reader.IsDBNull(1) ? null : reader.GetFieldValue<TimeOnly>(1);
            name = reader.GetString(2);
        }

        if (request.Items is { } items)
        {
            await using (var delete = new NpgsqlCommand("DELETE FROM fitness.meal_items WHERE meal_id = $1", connection, transaction))
            {
                delete.Parameters.Add(new() { Value = request.MealId });
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await InsertItemsAsync(connection, transaction, request.MealId, items, cancellationToken);
        }

        IReadOnlyList<MealItemDto> saved;
        await using (var select = new NpgsqlCommand(
                         $"SELECT meal_id, {ItemColumns} FROM fitness.meal_items WHERE meal_id = $1 ORDER BY position",
                         connection, transaction))
        {
            select.Parameters.Add(new() { Value = request.MealId });
            saved = (await ReadItemsAsync(select, cancellationToken)).GetValueOrDefault(request.MealId) ?? [];
        }

        await transaction.CommitAsync(cancellationToken);
        return new MealDto(request.MealId, day, eatenAt, name, saved);
    }

    public async Task<bool> DeleteMealAsync(Guid mealId, Guid userId, CancellationToken cancellationToken)
    {
        // The items go with it (ON DELETE CASCADE).
        await using var command = dataSource.CreateCommand("DELETE FROM fitness.meals WHERE id = $1 AND user_id = $2");
        command.Parameters.Add(new() { Value = mealId });
        command.Parameters.Add(new() { Value = userId });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<MealDto>> ListMealsAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        Dictionary<Guid, List<MealItemDto>> items;
        await using (var command = dataSource.CreateCommand(
                         $"""
                          SELECT i.meal_id, {ItemColumns}
                          FROM fitness.meal_items i
                          JOIN fitness.meals m ON m.id = i.meal_id
                          WHERE m.user_id = $1 AND m.day BETWEEN $2 AND $3
                          ORDER BY i.meal_id, i.position
                          """))
        {
            command.Parameters.Add(new() { Value = userId });
            command.Parameters.Add(new() { Value = from });
            command.Parameters.Add(new() { Value = to });
            items = await ReadItemsAsync(command, cancellationToken);
        }

        // In the order they were eaten: the ones after midnight (still the same food day) after the evening, and the
        // ones without a time last.
        await using var meals = dataSource.CreateCommand(
            """
            SELECT id, day, eaten_at, name
            FROM fitness.meals
            WHERE user_id = $1 AND day BETWEEN $2 AND $3
            ORDER BY day, (eaten_at IS NULL), (eaten_at < $4), eaten_at, created_at, id
            """);
        meals.Parameters.Add(new() { Value = userId });
        meals.Parameters.Add(new() { Value = from });
        meals.Parameters.Add(new() { Value = to });
        meals.Parameters.Add(new() { Value = FoodDay.Start });

        var result = new List<MealDto>();
        await using var reader = await meals.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetGuid(0);
            result.Add(new MealDto(
                id,
                reader.GetFieldValue<DateOnly>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeOnly>(2),
                reader.GetString(3),
                items.GetValueOrDefault(id) ?? []));
        }

        return result;
    }

    public async Task AddExerciseAsync(Guid userId, ExerciseDto exercise, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO fitness.exercises (id, user_id, day, done_at, activity, duration_minutes, kcal_min, kcal_max, measured, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            """);
        command.Parameters.Add(new() { Value = exercise.Id });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = exercise.Day });
        Add(command, exercise.DoneAt);
        command.Parameters.Add(new() { Value = exercise.Activity });
        Add(command, exercise.DurationMinutes);
        Add(command, exercise.KcalMin);
        Add(command, exercise.KcalMax);
        command.Parameters.Add(new() { Value = exercise.Measured });
        command.Parameters.Add(new() { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<bool> DeleteExerciseAsync(Guid exerciseId, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("DELETE FROM fitness.exercises WHERE id = $1 AND user_id = $2");
        command.Parameters.Add(new() { Value = exerciseId });
        command.Parameters.Add(new() { Value = userId });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<IReadOnlyList<ExerciseDto>> ListExercisesAsync(Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, day, done_at, activity, duration_minutes, kcal_min, kcal_max, measured
            FROM fitness.exercises
            WHERE user_id = $1 AND day BETWEEN $2 AND $3
            ORDER BY day, created_at, id
            """);
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = from });
        command.Parameters.Add(new() { Value = to });

        var exercises = new List<ExerciseDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            exercises.Add(new ExerciseDto(
                reader.GetGuid(0),
                reader.GetFieldValue<DateOnly>(1),
                reader.IsDBNull(2) ? null : reader.GetFieldValue<TimeOnly>(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt32(4),
                Decimal(reader, 5),
                Decimal(reader, 6),
                reader.GetBoolean(7)));
        }

        return exercises;
    }

    public async Task<FoodDto> SaveFoodAsync(Guid id, SaveFoodRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO fitness.foods (id, user_id, name, serving, kcal, protein_g, carbs_g, fat_g, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ON CONFLICT (user_id, lower(name)) DO UPDATE SET
                name = EXCLUDED.name, serving = EXCLUDED.serving, kcal = EXCLUDED.kcal, protein_g = EXCLUDED.protein_g,
                carbs_g = EXCLUDED.carbs_g, fat_g = EXCLUDED.fat_g, updated_at = EXCLUDED.updated_at
            RETURNING id, name, serving, kcal, protein_g, carbs_g, fat_g
            """);
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = request.UserId });
        command.Parameters.Add(new() { Value = request.Name });
        command.Parameters.Add(new() { Value = request.Serving });
        command.Parameters.Add(new() { Value = request.Kcal });
        Add(command, request.ProteinG);
        Add(command, request.CarbsG);
        Add(command, request.FatG);
        command.Parameters.Add(new() { Value = now });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return ReadFood(reader);
    }

    public async Task<IReadOnlyList<FoodDto>> ListFoodsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT id, name, serving, kcal, protein_g, carbs_g, fat_g FROM fitness.foods WHERE user_id = $1 ORDER BY lower(name)");
        command.Parameters.Add(new() { Value = userId });

        var foods = new List<FoodDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            foods.Add(ReadFood(reader));
        }

        return foods;
    }

    // The words are already reduced to a-z and 0-9 (FoodSearch), so they carry no LIKE wildcards. A food with every
    // word comes before one with some of them; among equals, the shortest name (the plainest food) first.
    public async Task<IReadOnlyList<ReferenceFoodDto>> SearchReferenceFoodsAsync(
        IReadOnlyList<string> words, int limit, CancellationToken cancellationToken)
    {
        var score = string.Join(" + ", words.Select((_, i) => $"((' ' || search_name) LIKE ${i + 1})::int"));
        await using var command = dataSource.CreateCommand(
            $"""
            SELECT name, category, kcal, protein_g, carbs_g, fat_g, fiber_g
            FROM (SELECT *, {score} AS score FROM fitness.reference_foods) AS scored
            WHERE score > 0
            ORDER BY score DESC, length(name), id
            LIMIT ${words.Count + 1}
            """);
        foreach (var word in words)
        {
            command.Parameters.Add(new() { Value = $"% {word}%" });
        }

        command.Parameters.Add(new() { Value = limit });

        var foods = new List<ReferenceFoodDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            foods.Add(new ReferenceFoodDto(
                reader.GetString(0), reader.GetString(1), reader.GetDecimal(2),
                Decimal(reader, 3), Decimal(reader, 4), Decimal(reader, 5), Decimal(reader, 6)));
        }

        return foods;
    }

    public async Task<ReferenceFoodDto?> FindReferenceFoodAsync(string searchName, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT name, category, kcal, protein_g, carbs_g, fat_g, fiber_g FROM fitness.reference_foods WHERE search_name = $1 ORDER BY id LIMIT 1");
        command.Parameters.Add(new() { Value = searchName });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken)
            ? new ReferenceFoodDto(
                reader.GetString(0), reader.GetString(1), reader.GetDecimal(2),
                Decimal(reader, 3), Decimal(reader, 4), Decimal(reader, 5), Decimal(reader, 6))
            : null;
    }

    public async Task AddDayNoteAsync(Guid id, Guid userId, DateOnly day, string note, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "INSERT INTO fitness.day_notes (id, user_id, day, note, created_at) VALUES ($1, $2, $3, $4, $5)");
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = day });
        command.Parameters.Add(new() { Value = note });
        command.Parameters.Add(new() { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<(DateOnly Day, string Note)>> ListDayNotesAsync(
        Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT day, note FROM fitness.day_notes WHERE user_id = $1 AND day BETWEEN $2 AND $3 ORDER BY day, created_at, id");
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = from });
        command.Parameters.Add(new() { Value = to });

        var notes = new List<(DateOnly, string)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            notes.Add((reader.GetFieldValue<DateOnly>(0), reader.GetString(1)));
        }

        return notes;
    }

    private const string ItemColumns =
        "description, quantity, unit, kcal_min, kcal_max, protein_min_g, protein_max_g, carbs_g, fat_g, source";

    private static async Task InsertItemsAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid mealId,
        IReadOnlyList<MealItemDto> items,
        CancellationToken cancellationToken)
    {
        for (var position = 0; position < items.Count; position++)
        {
            var item = items[position];
            await using var command = new NpgsqlCommand(
                $"INSERT INTO fitness.meal_items (meal_id, position, {ItemColumns}) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12)",
                connection, transaction);
            command.Parameters.Add(new() { Value = mealId });
            command.Parameters.Add(new() { Value = position + 1 });
            command.Parameters.Add(new() { Value = item.Description });
            Add(command, item.Quantity);
            Add(command, item.Unit);
            command.Parameters.Add(new() { Value = item.KcalMin });
            command.Parameters.Add(new() { Value = item.KcalMax });
            command.Parameters.Add(new() { Value = item.ProteinMinG });
            command.Parameters.Add(new() { Value = item.ProteinMaxG });
            Add(command, item.CarbsG);
            Add(command, item.FatG);
            command.Parameters.Add(new() { Value = SourceText(item.Source) });
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    // Column 0 is the meal id; the rest is ItemColumns.
    private static async Task<Dictionary<Guid, List<MealItemDto>>> ReadItemsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var items = new Dictionary<Guid, List<MealItemDto>>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mealId = reader.GetGuid(0);
            if (!items.TryGetValue(mealId, out var list))
            {
                items[mealId] = list = [];
            }

            list.Add(new MealItemDto(
                reader.GetString(1),
                Decimal(reader, 2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetDecimal(4),
                reader.GetDecimal(5),
                reader.GetDecimal(6),
                reader.GetDecimal(7),
                Decimal(reader, 8),
                Decimal(reader, 9),
                ParseSource(reader.GetString(10))));
        }

        return items;
    }

    private static FitnessProfileDto ReadProfile(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0),
            Decimal(reader, 1),
            reader.IsDBNull(2) ? null : reader.GetInt32(2),
            reader.IsDBNull(3) ? null : reader.GetString(3) == "female" ? Sex.Female : Sex.Male,
            Decimal(reader, 4),
            reader.IsDBNull(5) ? null : reader.GetInt32(5),
            reader.IsDBNull(6) ? null : reader.GetInt32(6),
            reader.IsDBNull(7) ? null : reader.GetInt32(7),
            reader.IsDBNull(8) ? null : reader.GetString(8),
            reader.GetFieldValue<DateTimeOffset>(9),
            reader.IsDBNull(10) ? null : reader.GetGuid(10),
            reader.IsDBNull(11) ? null : reader.GetGuid(11));

    private static BodyRecordDto ReadBody(NpgsqlDataReader reader) =>
        new(reader.GetFieldValue<DateOnly>(0),
            Decimal(reader, 1), Decimal(reader, 2), Decimal(reader, 3), Decimal(reader, 4), Decimal(reader, 5),
            Decimal(reader, 6), Decimal(reader, 7), Decimal(reader, 8), Decimal(reader, 9),
            reader.IsDBNull(10) ? null : reader.GetInt32(10),
            reader.IsDBNull(11) ? null : reader.GetString(11));

    private static FoodDto ReadFood(NpgsqlDataReader reader) =>
        new(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetDecimal(3),
            Decimal(reader, 4), Decimal(reader, 5), Decimal(reader, 6));

    private static decimal? Decimal(NpgsqlDataReader reader, int column) =>
        reader.IsDBNull(column) ? null : reader.GetDecimal(column);

    private static string SexText(Sex sex) => sex == Sex.Female ? "female" : "male";

    private static string SourceText(NutritionSource source) => source switch
    {
        NutritionSource.User => "user",
        NutritionSource.FoodTable => "food_table",
        NutritionSource.ReferenceTable => "reference_table",
        _ => "estimate",
    };

    private static NutritionSource ParseSource(string text) => text switch
    {
        "user" => NutritionSource.User,
        "food_table" => NutritionSource.FoodTable,
        "reference_table" => NutritionSource.ReferenceTable,
        _ => NutritionSource.Estimate,
    };

    // A null needs its type said: PostgreSQL cannot guess it inside COALESCE.
    private static void Add(NpgsqlCommand command, decimal? value) =>
        command.Parameters.Add(new() { Value = value.HasValue ? value.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Numeric });

    private static void Add(NpgsqlCommand command, int? value) =>
        command.Parameters.Add(new() { Value = value.HasValue ? value.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Integer });

    private static void Add(NpgsqlCommand command, string? value) =>
        command.Parameters.Add(new() { Value = (object?)value ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });

    private static void Add(NpgsqlCommand command, Guid? value) =>
        command.Parameters.Add(new() { Value = value.HasValue ? value.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });

    private static void Add(NpgsqlCommand command, TimeOnly? value) =>
        command.Parameters.Add(new() { Value = value.HasValue ? value.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Time });
}

using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Goals.Domain;
using Npgsql;

namespace LifeAim.Modules.Goals.Infrastructure;

/// <summary>Plain ADO.NET over the "goals" schema. Every value goes in as a parameter, never as SQL text.</summary>
internal sealed class GoalRepository(NpgsqlDataSource dataSource)
{
    public async Task AddAsync(Goal goal, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO goals.goals
                (id, user_id, name, description, indicator, due_date, start_value, target_value, unit, user_plan,
                 created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """);
        command.Parameters.Add(new() { Value = goal.Id });
        command.Parameters.Add(new() { Value = goal.UserId });
        command.Parameters.Add(new() { Value = goal.Name });
        command.Parameters.Add(new() { Value = (object?)goal.Description ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)goal.Indicator ?? DBNull.Value });
        command.Parameters.Add(new() { Value = goal.DueDate.HasValue ? goal.DueDate.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = goal.StartValue.HasValue ? goal.StartValue.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = goal.TargetValue.HasValue ? goal.TargetValue.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)goal.Unit ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)goal.UserPlan ?? DBNull.Value });
        command.Parameters.Add(new() { Value = goal.CreatedAt });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>Writes the editable fields; false when no goal with that id belongs to that user.</summary>
    public async Task<bool> UpdateAsync(Goal goal, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE goals.goals
            SET name = $3, description = $4, indicator = $5, due_date = $6,
                start_value = $7, target_value = $8, unit = $9, user_plan = $10
            WHERE id = $1 AND user_id = $2 AND status = 'active'
            """);
        command.Parameters.Add(new() { Value = goal.Id });
        command.Parameters.Add(new() { Value = goal.UserId });
        command.Parameters.Add(new() { Value = goal.Name });
        command.Parameters.Add(new() { Value = (object?)goal.Description ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)goal.Indicator ?? DBNull.Value });
        command.Parameters.Add(new() { Value = goal.DueDate.HasValue ? goal.DueDate.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = goal.StartValue.HasValue ? goal.StartValue.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = goal.TargetValue.HasValue ? goal.TargetValue.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)goal.Unit ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)goal.UserPlan ?? DBNull.Value });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>
    /// Marks an active goal as cancelled; its row, routines and history stay. False when no active goal with
    /// that id belongs to that user (also when it was already cancelled).
    /// </summary>
    public async Task<bool> CancelAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE goals.goals
            SET status = 'cancelled', cancelled_at = $3
            WHERE id = $1 AND user_id = $2 AND status = 'active'
            """);
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = now });

        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    /// <summary>One of the user's active goals, or null.</summary>
    public async Task<Goal?> FindAsync(Guid id, Guid userId, CancellationToken cancellationToken) =>
        (await ListByUserAsync(userId, cancellationToken)).FirstOrDefault(goal => goal.Id == id);

    /// <summary>The user's active goals, oldest first; cancelled ones stay in the table but not here.</summary>
    public async Task<IReadOnlyList<Goal>> ListByUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, user_id, name, description, indicator, due_date, start_value, target_value, unit, user_plan,
                   created_at
            FROM goals.goals
            WHERE user_id = $1 AND status = 'active'
            ORDER BY created_at, id
            """);
        command.Parameters.Add(new() { Value = userId });

        var goals = new List<Goal>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            goals.Add(Goal.Load(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetFieldValue<DateTimeOffset>(10)));
        }

        return goals;
    }

    /// <summary>
    /// Deletes the goal's routines and inserts the new ones in one transaction: the plan is replaced as a
    /// whole, so a failure in the middle never leaves half of the old plan and half of the new one.
    /// </summary>
    public async Task ReplaceRoutinesAsync(
        Guid goalId,
        Guid userId,
        IReadOnlyList<GoalRoutine> routines,
        CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var delete = new NpgsqlCommand(
            "DELETE FROM goals.goal_routines WHERE goal_id = $1 AND user_id = $2", connection, transaction))
        {
            delete.Parameters.Add(new() { Value = goalId });
            delete.Parameters.Add(new() { Value = userId });
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var routine in routines)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO goals.goal_routines
                    (id, goal_id, user_id, activity, frequency, interval, days_of_week, times_per_period,
                     time_of_day, day_period, amount, unit, duration_minutes, source, starts_on, ends_on, created_at)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, $16, $17)
                """,
                connection,
                transaction);
            insert.Parameters.Add(new() { Value = routine.Id });
            insert.Parameters.Add(new() { Value = routine.GoalId });
            insert.Parameters.Add(new() { Value = routine.UserId });
            insert.Parameters.Add(new() { Value = routine.Activity });
            insert.Parameters.Add(new() { Value = ToText(routine.Frequency) });
            insert.Parameters.Add(new() { Value = routine.Interval });
            insert.Parameters.Add(new() { Value = routine.DaysOfWeek.ToArray() });
            insert.Parameters.Add(new() { Value = routine.TimesPerPeriod.HasValue ? routine.TimesPerPeriod.Value : DBNull.Value });
            insert.Parameters.Add(new() { Value = routine.TimeOfDay.HasValue ? routine.TimeOfDay.Value : DBNull.Value });
            insert.Parameters.Add(new() { Value = routine.DayPeriod is { } period ? ToText(period) : DBNull.Value });
            insert.Parameters.Add(new() { Value = routine.Amount.HasValue ? routine.Amount.Value : DBNull.Value });
            insert.Parameters.Add(new() { Value = (object?)routine.Unit ?? DBNull.Value });
            insert.Parameters.Add(new() { Value = routine.DurationMinutes.HasValue ? routine.DurationMinutes.Value : DBNull.Value });
            insert.Parameters.Add(new() { Value = ToText(routine.Source) });
            insert.Parameters.Add(new() { Value = routine.StartsOn.HasValue ? routine.StartsOn.Value : DBNull.Value });
            insert.Parameters.Add(new() { Value = routine.EndsOn.HasValue ? routine.EndsOn.Value : DBNull.Value });
            insert.Parameters.Add(new() { Value = routine.CreatedAt });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>The users with at least one routine in an active goal (the daily follow-up looks at them).</summary>
    public async Task<IReadOnlyList<Guid>> ListUserIdsWithRoutinesAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT DISTINCT r.user_id
            FROM goals.goal_routines r
            JOIN goals.goals g ON g.id = r.goal_id
            WHERE g.status = 'active'
            """);

        var users = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            users.Add(reader.GetGuid(0));
        }

        return users;
    }

    public async Task<IReadOnlyList<GoalRoutine>> ListRoutinesByUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT r.id, r.goal_id, r.user_id, r.activity, r.frequency, r.interval, r.days_of_week, r.times_per_period,
                   r.time_of_day, r.day_period, r.amount, r.unit, r.duration_minutes, r.source, r.starts_on, r.ends_on,
                   r.created_at
            FROM goals.goal_routines r
            JOIN goals.goals g ON g.id = r.goal_id
            WHERE r.user_id = $1 AND g.status = 'active'
            ORDER BY r.created_at, r.id
            """);
        command.Parameters.Add(new() { Value = userId });

        var routines = new List<GoalRoutine>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            routines.Add(GoalRoutine.Load(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetString(3),
                Enum.Parse<RoutineFrequency>(reader.GetString(4), ignoreCase: true),
                reader.GetInt32(5),
                reader.GetFieldValue<int[]>(6),
                reader.IsDBNull(7) ? null : reader.GetInt32(7),
                reader.IsDBNull(8) ? null : reader.GetFieldValue<TimeOnly>(8),
                reader.IsDBNull(9) ? null : Enum.Parse<DayPeriod>(reader.GetString(9), ignoreCase: true),
                reader.IsDBNull(10) ? null : reader.GetDecimal(10),
                reader.IsDBNull(11) ? null : reader.GetString(11),
                reader.IsDBNull(12) ? null : reader.GetInt32(12),
                Enum.Parse<RoutineSource>(reader.GetString(13), ignoreCase: true),
                reader.IsDBNull(14) ? null : reader.GetFieldValue<DateOnly>(14),
                reader.IsDBNull(15) ? null : reader.GetFieldValue<DateOnly>(15),
                reader.GetFieldValue<DateTimeOffset>(16)));
        }

        return routines;
    }

    // Enums are stored as lowercase text ("weekly"), so the table reads well and the CHECKs can list the values.
    private static string ToText<TEnum>(TEnum value) where TEnum : struct, Enum => value.ToString().ToLowerInvariant();
}

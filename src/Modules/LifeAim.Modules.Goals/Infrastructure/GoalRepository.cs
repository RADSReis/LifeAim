using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Goals.Domain;
using Npgsql;
using NpgsqlTypes;

namespace LifeAim.Modules.Goals.Infrastructure;

/// <summary>Plain ADO.NET over the "goals" schema. Every value goes in as a parameter, never as SQL text.</summary>
internal sealed class GoalRepository(NpgsqlDataSource dataSource)
{
    public async Task AddAsync(Goal goal, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var command = InsertCommand(goal, connection, null);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static NpgsqlCommand InsertCommand(Goal goal, NpgsqlConnection connection, NpgsqlTransaction? transaction)
    {
        var command = new NpgsqlCommand(
            """
            INSERT INTO goals.goals
                (id, user_id, name, description, indicator, due_date, start_value, target_value, unit, user_plan,
                 created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            """,
            connection,
            transaction);
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
        return command;
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
    /// Ends an active or paused goal (cancelled): its row, routines and history stay. False when no such goal
    /// belongs to that user (also when it was already ended). The change is written to goal_status_changes.
    /// </summary>
    public Task<bool> CancelAsync(Guid id, Guid userId, string? reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        ChangeStatusAsync(
            id, userId, GoalLifecycle.CanCancel, GoalStatus.Cancelled, reason, null, now,
            "cancelled_at = $5, paused_at = NULL, resume_on = NULL, resume_offered_at = NULL",
            cancellationToken);

    /// <summary>Pauses an active goal, or changes the day a paused one comes back.</summary>
    public Task<bool> PauseAsync(
        Guid id, Guid userId, DateOnly? resumeOn, string? reason, DateTimeOffset now, CancellationToken cancellationToken) =>
        ChangeStatusAsync(
            id, userId, GoalLifecycle.CanPause, GoalStatus.Paused, reason, resumeOn, now,
            "paused_at = COALESCE(g.paused_at, $5), resume_on = $6, resume_offered_at = NULL",
            cancellationToken);

    public Task<bool> ResumeAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken) =>
        ChangeStatusAsync(
            id, userId, GoalLifecycle.CanResume, GoalStatus.Active, null, null, now,
            "paused_at = NULL, resume_on = NULL, resume_offered_at = NULL, status_reason = NULL",
            cancellationToken);

    /// <summary>
    /// Inserts the new goal and closes the old one as replaced by it, in one transaction: if the old one is not the
    /// user's active or paused goal, nothing is written and the result is false.
    /// </summary>
    public async Task<bool> ReplaceAsync(
        Guid id, Guid userId, Goal replacement, string? reason, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var insert = InsertCommand(replacement, connection, transaction))
        {
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var update = new NpgsqlCommand(
            """
            WITH old AS (
                SELECT id, status FROM goals.goals
                WHERE id = $1 AND user_id = $2 AND status = ANY($3) FOR UPDATE
            ), changed AS (
                UPDATE goals.goals g
                SET status = 'replaced', replaced_by = $4, status_reason = $5, cancelled_at = $6,
                    paused_at = NULL, resume_on = NULL, resume_offered_at = NULL
                FROM old WHERE g.id = old.id
                RETURNING old.status AS from_status
            )
            INSERT INTO goals.goal_status_changes (id, goal_id, user_id, from_status, to_status, reason, resume_on, changed_at)
            SELECT $7, $1, $2, from_status, 'replaced', $5, NULL, $6 FROM changed
            """,
            connection,
            transaction);
        update.Parameters.Add(new() { Value = id });
        update.Parameters.Add(new() { Value = userId });
        update.Parameters.Add(new() { Value = GoalLifecycle.CanReplace.Select(GoalLifecycle.ToText).ToArray() });
        update.Parameters.Add(new() { Value = replacement.Id });
        update.Parameters.Add(new() { Value = (object?)reason ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        update.Parameters.Add(new() { Value = now });
        update.Parameters.Add(new() { Value = Guid.CreateVersion7(now) });

        if (await update.ExecuteNonQueryAsync(cancellationToken) != 1)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>The user's goals that are not active (paused, cancelled, replaced), oldest first.</summary>
    public async Task<IReadOnlyList<GoalDto>> ListHistoryAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
             SELECT {HistoryColumns} FROM goals.goals
             WHERE user_id = $1 AND status <> 'active'
             ORDER BY created_at, id
             """);
        command.Parameters.Add(new() { Value = userId });
        return await ReadHistoryAsync(command, cancellationToken);
    }

    /// <summary>Paused goals (of every user) whose pause ended and that Pippin has not asked about yet.</summary>
    public async Task<IReadOnlyList<GoalDto>> ListPausesEndingAsync(DateOnly today, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
             SELECT {HistoryColumns} FROM goals.goals
             WHERE status = 'paused' AND resume_on <= $1 AND resume_offered_at IS NULL
             ORDER BY resume_on, id
             """);
        command.Parameters.Add(new() { Value = today });
        return await ReadHistoryAsync(command, cancellationToken);
    }

    public async Task<bool> MarkResumeOfferedAsync(Guid id, Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE goals.goals SET resume_offered_at = $3
            WHERE id = $1 AND user_id = $2 AND status = 'paused' AND resume_offered_at IS NULL
            """);
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private const string HistoryColumns =
        "id, user_id, name, description, indicator, due_date, start_value, target_value, unit, user_plan, created_at, " +
        "status, resume_on, status_reason, replaced_by, COALESCE(cancelled_at, paused_at)";

    private static async Task<IReadOnlyList<GoalDto>> ReadHistoryAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var goals = new List<GoalDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            goals.Add(new GoalDto(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetFieldValue<DateOnly>(5),
                reader.IsDBNull(6) ? null : reader.GetDecimal(6),
                reader.IsDBNull(7) ? null : reader.GetDecimal(7),
                reader.IsDBNull(8) ? null : reader.GetString(8),
                reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                GoalLifecycle.Parse(reader.GetString(11)),
                reader.IsDBNull(12) ? null : reader.GetFieldValue<DateOnly>(12),
                reader.IsDBNull(13) ? null : reader.GetString(13),
                reader.IsDBNull(14) ? null : reader.GetGuid(14),
                reader.IsDBNull(15) ? null : reader.GetFieldValue<DateTimeOffset>(15)));
        }

        return goals;
    }

    // One statement: the goal changes only from an allowed status, and the change is logged with where it came from.
    // $1 id, $2 user, $3 allowed statuses, $4 new status, $5 now, $6 resume_on, $7 reason, $8 id of the log row.
    private async Task<bool> ChangeStatusAsync(
        Guid id,
        Guid userId,
        IReadOnlyList<GoalStatus> from,
        GoalStatus to,
        string? reason,
        DateOnly? resumeOn,
        DateTimeOffset now,
        string set,
        CancellationToken cancellationToken)
    {
        var reasonSet = to == GoalStatus.Active ? "" : ", status_reason = $7";
        await using var command = dataSource.CreateCommand(
            $"""
             WITH old AS (
                 SELECT id, status FROM goals.goals
                 WHERE id = $1 AND user_id = $2 AND status = ANY($3) FOR UPDATE
             ), changed AS (
                 UPDATE goals.goals g SET status = $4, {set}{reasonSet}
                 FROM old WHERE g.id = old.id
                 RETURNING old.status AS from_status
             )
             INSERT INTO goals.goal_status_changes (id, goal_id, user_id, from_status, to_status, reason, resume_on, changed_at)
             SELECT $8, $1, $2, from_status, $4, $7, $6, $5 FROM changed
             """);
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = from.Select(GoalLifecycle.ToText).ToArray() });
        command.Parameters.Add(new() { Value = GoalLifecycle.ToText(to) });
        command.Parameters.Add(new() { Value = now });
        command.Parameters.Add(new() { Value = resumeOn.HasValue ? resumeOn.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Date });
        command.Parameters.Add(new() { Value = (object?)reason ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = Guid.CreateVersion7(now) });

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

    /// <summary>
    /// The routines of the user's active goals; with <paramref name="goalId"/>, the routines of that one goal of the
    /// user, whatever its status (a paused goal keeps its plan).
    /// </summary>
    public async Task<IReadOnlyList<GoalRoutine>> ListRoutinesByUserAsync(
        Guid userId, CancellationToken cancellationToken, Guid? goalId = null)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT r.id, r.goal_id, r.user_id, r.activity, r.frequency, r.interval, r.days_of_week, r.times_per_period,
                   r.time_of_day, r.day_period, r.amount, r.unit, r.duration_minutes, r.source, r.starts_on, r.ends_on,
                   r.created_at
            FROM goals.goal_routines r
            JOIN goals.goals g ON g.id = r.goal_id
            WHERE r.user_id = $1 AND ($2::uuid IS NULL AND g.status = 'active' OR r.goal_id = $2)
            ORDER BY r.created_at, r.id
            """);
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = goalId.HasValue ? goalId.Value : DBNull.Value, NpgsqlDbType = NpgsqlDbType.Uuid });

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

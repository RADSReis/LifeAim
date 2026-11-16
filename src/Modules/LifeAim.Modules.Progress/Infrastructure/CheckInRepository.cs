using LifeAim.Modules.Progress.Contracts;
using Npgsql;

namespace LifeAim.Modules.Progress.Infrastructure;

/// <summary>Plain ADO.NET over progress.check_ins. Every value goes in as a parameter, never as SQL text.</summary>
internal sealed class CheckInRepository(NpgsqlDataSource dataSource)
{
    private const string Columns =
        "id, user_id, goal_id, routine_id, day, activity, status, times_asked, first_asked_at, last_asked_at, answered_at, reason";

    public async Task<IReadOnlyList<CheckInDto>> ListAsync(
        Guid userId, DateOnly from, DateOnly to, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
             SELECT {Columns} FROM progress.check_ins
             WHERE user_id = $1 AND day BETWEEN $2 AND $3
             ORDER BY day, first_asked_at, id
             """);
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = from });
        command.Parameters.Add(new() { Value = to });
        return await ReadAsync(command, cancellationToken);
    }

    /// <summary>A new check-in, or one more attempt on the one still asked; answered ones are left alone.</summary>
    public async Task<CheckInDto> UpsertAskedAsync(
        Guid id, AskCheckInRequest request, string activity, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using (var upsert = dataSource.CreateCommand(
            """
            INSERT INTO progress.check_ins
                (id, user_id, goal_id, routine_id, day, activity, status, times_asked, first_asked_at, last_asked_at)
            VALUES ($1, $2, $3, $4, $5, $6, 'asked', 1, $7, $7)
            ON CONFLICT (routine_id, day) DO UPDATE
                SET times_asked = progress.check_ins.times_asked + 1, last_asked_at = EXCLUDED.last_asked_at
                WHERE progress.check_ins.status = 'asked' AND progress.check_ins.user_id = EXCLUDED.user_id
            """))
        {
            upsert.Parameters.Add(new() { Value = id });
            upsert.Parameters.Add(new() { Value = request.UserId });
            upsert.Parameters.Add(new() { Value = request.GoalId });
            upsert.Parameters.Add(new() { Value = request.RoutineId });
            upsert.Parameters.Add(new() { Value = request.Day });
            upsert.Parameters.Add(new() { Value = activity });
            upsert.Parameters.Add(new() { Value = now });
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var select = dataSource.CreateCommand(
            $"SELECT {Columns} FROM progress.check_ins WHERE routine_id = $1 AND day = $2 AND user_id = $3");
        select.Parameters.Add(new() { Value = request.RoutineId });
        select.Parameters.Add(new() { Value = request.Day });
        select.Parameters.Add(new() { Value = request.UserId });
        return (await ReadAsync(select, cancellationToken)).Single();
    }

    /// <summary>Answers one open check-in of the user. False when there is none with that id.</summary>
    public async Task<bool> AnswerAsync(
        Guid id,
        Guid userId,
        CheckInStatus status,
        string? reason,
        Guid? progressEntryId,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE progress.check_ins
            SET status = $3, reason = $4, progress_entry_id = $5, answered_at = $6
            WHERE id = $1 AND user_id = $2 AND status = 'asked'
            """);
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = ToText(status) });
        command.Parameters.Add(new() { Value = (object?)reason ?? DBNull.Value });
        command.Parameters.Add(new() { Value = progressEntryId.HasValue ? progressEntryId.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<int> AnswerDoneForGoalAsync(
        Guid userId, Guid goalId, DateOnly day, Guid? progressEntryId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE progress.check_ins
            SET status = 'done', progress_entry_id = $4, answered_at = $5
            WHERE user_id = $1 AND goal_id = $2 AND day = $3 AND status = 'asked'
            """);
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = goalId });
        command.Parameters.Add(new() { Value = day });
        command.Parameters.Add(new() { Value = progressEntryId.HasValue ? progressEntryId.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task<IReadOnlyList<CheckInDto>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var checkIns = new List<CheckInDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            checkIns.Add(new CheckInDto(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetGuid(3),
                reader.GetFieldValue<DateOnly>(4),
                reader.GetString(5),
                Enum.Parse<CheckInStatus>(reader.GetString(6), ignoreCase: true),
                reader.GetInt32(7),
                reader.GetFieldValue<DateTimeOffset>(8),
                reader.GetFieldValue<DateTimeOffset>(9),
                reader.IsDBNull(10) ? null : reader.GetFieldValue<DateTimeOffset>(10),
                reader.IsDBNull(11) ? null : reader.GetString(11)));
        }

        return checkIns;
    }

    private static string ToText(CheckInStatus status) => status.ToString().ToLowerInvariant();
}

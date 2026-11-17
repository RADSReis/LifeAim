using LifeAim.Modules.Fitness.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace LifeAim.Modules.Fitness.Infrastructure;

/// <summary>Plain ADO.NET over fitness.reminders and fitness.reminder_log.</summary>
internal sealed class ReminderRepository(NpgsqlDataSource dataSource)
{
    public async Task<Guid> SetAsync(Guid newId, SetReminderRequest request, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // The same reminder again only changes its time (and keeps today's log, so it is not sent twice).
        await using (var update = dataSource.CreateCommand(
                         """
                         UPDATE fitness.reminders SET remind_at = $3
                         WHERE user_id = $1 AND kind = $2 AND ($2 <> 'custom' OR lower(text) = lower($4))
                         RETURNING id
                         """))
        {
            update.Parameters.Add(new() { Value = request.UserId });
            update.Parameters.Add(new() { Value = KindText(request.Kind) });
            update.Parameters.Add(new() { Value = request.At });
            update.Parameters.Add(new() { Value = (object?)request.Text ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
            if (await update.ExecuteScalarAsync(cancellationToken) is Guid existing)
            {
                return existing;
            }
        }

        await using var insert = dataSource.CreateCommand(
            "INSERT INTO fitness.reminders (id, user_id, kind, remind_at, text, created_at) VALUES ($1, $2, $3, $4, $5, $6)");
        insert.Parameters.Add(new() { Value = newId });
        insert.Parameters.Add(new() { Value = request.UserId });
        insert.Parameters.Add(new() { Value = KindText(request.Kind) });
        insert.Parameters.Add(new() { Value = request.At });
        insert.Parameters.Add(new() { Value = (object?)request.Text ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        insert.Parameters.Add(new() { Value = now });
        await insert.ExecuteNonQueryAsync(cancellationToken);
        return newId;
    }

    public async Task<bool> RemoveAsync(Guid userId, ReminderKind kind, string? text, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "DELETE FROM fitness.reminders WHERE user_id = $1 AND kind = $2 AND ($2 <> 'custom' OR lower(text) = lower($3))");
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = KindText(kind) });
        command.Parameters.Add(new() { Value = (object?)text ?? DBNull.Value, NpgsqlDbType = NpgsqlDbType.Text });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    public async Task<IReadOnlyList<ReminderDto>> ListAsync(Guid userId, DateOnly day, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT r.id, r.kind, r.remind_at, r.text, l.sent_at IS NOT NULL, l.done_at IS NOT NULL
            FROM fitness.reminders r
            LEFT JOIN fitness.reminder_log l ON l.reminder_id = r.id AND l.day = $2
            WHERE r.user_id = $1
            ORDER BY r.remind_at, r.created_at, r.id
            """);
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = day });

        var reminders = new List<ReminderDto>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            reminders.Add(new ReminderDto(
                reader.GetGuid(0),
                ParseKind(reader.GetString(1)),
                reader.GetFieldValue<TimeOnly>(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5)));
        }

        return reminders;
    }

    public async Task<IReadOnlyList<Guid>> ListUserIdsAsync(CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand("SELECT DISTINCT user_id FROM fitness.reminders");
        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    /// <summary>Sets sent_at (or done_at) of the day's log once; the first moment recorded stays.</summary>
    public async Task<bool> MarkAsync(
        Guid reminderId, Guid userId, DateOnly day, bool done, DateTimeOffset now, CancellationToken cancellationToken)
    {
        var column = done ? "done_at" : "sent_at";
        await using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO fitness.reminder_log (reminder_id, day, {column})
            SELECT r.id, $3, $4 FROM fitness.reminders r WHERE r.id = $1 AND r.user_id = $2
            ON CONFLICT (reminder_id, day) DO UPDATE SET {column} = COALESCE(fitness.reminder_log.{column}, EXCLUDED.{column})
            """);
        command.Parameters.Add(new() { Value = reminderId });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = day });
        command.Parameters.Add(new() { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) > 0;
    }

    private static string KindText(ReminderKind kind) => kind switch
    {
        ReminderKind.WeighIn => "weigh_in",
        ReminderKind.NoRecord => "no_record",
        ReminderKind.DayClose => "day_close",
        _ => "custom",
    };

    private static ReminderKind ParseKind(string text) => text switch
    {
        "weigh_in" => ReminderKind.WeighIn,
        "no_record" => ReminderKind.NoRecord,
        "day_close" => ReminderKind.DayClose,
        _ => ReminderKind.Custom,
    };
}

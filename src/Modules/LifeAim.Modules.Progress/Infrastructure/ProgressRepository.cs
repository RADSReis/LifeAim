using LifeAim.Modules.Progress.Domain;
using Npgsql;

namespace LifeAim.Modules.Progress.Infrastructure;

/// <summary>Plain ADO.NET over the "progress" schema. Every value goes in as a parameter, never as SQL text.</summary>
internal sealed class ProgressRepository(NpgsqlDataSource dataSource)
{
    public async Task AddAsync(ProgressEntry entry, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO progress.progress_entries (id, user_id, goal_id, occurred_on, amount, unit, note, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8)
            """);
        command.Parameters.Add(new() { Value = entry.Id });
        command.Parameters.Add(new() { Value = entry.UserId });
        command.Parameters.Add(new() { Value = entry.GoalId });
        command.Parameters.Add(new() { Value = entry.OccurredOn });
        command.Parameters.Add(new() { Value = entry.Amount.HasValue ? entry.Amount.Value : DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)entry.Unit ?? DBNull.Value });
        command.Parameters.Add(new() { Value = entry.Note });
        command.Parameters.Add(new() { Value = entry.CreatedAt });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ProgressEntry>> ListByGoalAsync(
        Guid userId,
        Guid goalId,
        int limit,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, user_id, goal_id, occurred_on, amount, unit, note, created_at
            FROM progress.progress_entries
            WHERE user_id = $1 AND goal_id = $2
            ORDER BY occurred_on DESC, created_at DESC
            LIMIT $3
            """);
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = goalId });
        command.Parameters.Add(new() { Value = limit });
        return await ReadAsync(command, cancellationToken);
    }

    public async Task<IReadOnlyList<ProgressEntry>> ListByPeriodAsync(
        Guid userId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, user_id, goal_id, occurred_on, amount, unit, note, created_at
            FROM progress.progress_entries
            WHERE user_id = $1 AND occurred_on BETWEEN $2 AND $3
            ORDER BY occurred_on, created_at
            """);
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = from });
        command.Parameters.Add(new() { Value = to });
        return await ReadAsync(command, cancellationToken);
    }

    private static async Task<IReadOnlyList<ProgressEntry>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var entries = new List<ProgressEntry>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            entries.Add(ProgressEntry.Load(
                reader.GetGuid(0),
                reader.GetGuid(1),
                reader.GetGuid(2),
                reader.GetFieldValue<DateOnly>(3),
                reader.IsDBNull(4) ? null : reader.GetDecimal(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.GetString(6),
                reader.GetFieldValue<DateTimeOffset>(7)));
        }

        return entries;
    }
}

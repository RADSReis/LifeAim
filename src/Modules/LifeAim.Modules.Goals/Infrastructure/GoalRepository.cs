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
                (id, user_id, name, description, indicator, due_date, start_value, target_value, unit, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
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
        command.Parameters.Add(new() { Value = goal.CreatedAt });

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<Goal>> ListByUserAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, user_id, name, description, indicator, due_date, start_value, target_value, unit, created_at
            FROM goals.goals
            WHERE user_id = $1
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
                reader.GetFieldValue<DateTimeOffset>(9)));
        }

        return goals;
    }
}

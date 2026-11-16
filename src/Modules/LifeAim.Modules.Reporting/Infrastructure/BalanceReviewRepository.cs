using System.Text.Json;
using LifeAim.Modules.Reporting.Contracts;
using Npgsql;

namespace LifeAim.Modules.Reporting.Infrastructure;

/// <summary>Plain ADO.NET over reporting.balance_reviews. Every value goes in as a parameter, never as SQL text.</summary>
internal sealed class BalanceReviewRepository(NpgsqlDataSource dataSource)
{
    private const string Columns =
        "id, user_id, focus_goal_id, signals, protected_goal_ids, message, status, obstacle, created_at";

    public async Task<bool> AddAsync(BalanceReviewDto review, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO reporting.balance_reviews
                (id, user_id, focus_goal_id, signals, protected_goal_ids, message, status, obstacle, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, NULL, $8)
            ON CONFLICT (id) DO NOTHING
            """);
        command.Parameters.Add(new() { Value = review.Id });
        command.Parameters.Add(new() { Value = review.UserId });
        command.Parameters.Add(new() { Value = review.FocusGoalId });
        command.Parameters.Add(new() { Value = review.Signals.Select(Name).ToArray() });
        command.Parameters.Add(new() { Value = review.ProtectedGoalIds.ToArray() });
        command.Parameters.Add(new() { Value = review.Message });
        command.Parameters.Add(new() { Value = Name(review.Status) });
        command.Parameters.Add(new() { Value = review.CreatedAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<BalanceReviewDto?> GetLatestAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {Columns} FROM reporting.balance_reviews WHERE user_id = $1 ORDER BY created_at DESC LIMIT 1");
        command.Parameters.Add(new() { Value = userId });
        return await ReadOneAsync(command, cancellationToken);
    }

    public async Task<BalanceReviewDto?> FindAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {Columns} FROM reporting.balance_reviews WHERE id = $1 AND user_id = $2");
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        return await ReadOneAsync(command, cancellationToken);
    }

    /// <summary>Only an open review (offered or explained) is answered; the obstacle, once said, is kept.</summary>
    public async Task<bool> AnswerAsync(
        Guid id, Guid userId, ReviewStatus status, string? obstacle, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE reporting.balance_reviews
            SET status = $3, obstacle = COALESCE($4, obstacle), answered_at = $5
            WHERE id = $1 AND user_id = $2 AND status IN ('offered', 'explained')
            """);
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = Name(status) });
        command.Parameters.Add(new() { Value = (object?)obstacle ?? DBNull.Value, NpgsqlDbType = NpgsqlTypes.NpgsqlDbType.Text });
        command.Parameters.Add(new() { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<BalanceReviewDto?> ReadOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new BalanceReviewDto(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetGuid(2),
            reader.GetFieldValue<string[]>(3).Select(Parse<BalanceSignal>).ToList(),
            reader.GetFieldValue<Guid[]>(4),
            reader.GetString(5),
            Parse<ReviewStatus>(reader.GetString(6)),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            reader.GetFieldValue<DateTimeOffset>(8));
    }

    // RecurringDifficulty ↔ "recurring_difficulty", the same snake_case as the CHECK constraint and the comments.
    private static string Name<T>(T value) where T : struct, Enum => JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());

    private static T Parse<T>(string value) where T : struct, Enum => Enum.Parse<T>(value.Replace("_", ""), ignoreCase: true);
}

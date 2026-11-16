using LifeAim.Modules.Reporting.Contracts;
using Npgsql;

namespace LifeAim.Modules.Reporting.Infrastructure;

/// <summary>Plain ADO.NET over reporting.summaries. Every value goes in as a parameter, never as SQL text.</summary>
internal sealed class SummaryRepository(NpgsqlDataSource dataSource)
{
    private const string Columns =
        "id, user_id, month, status, includes_quarter, message, insight_teaser, insight, insight_status, created_at";

    /// <summary>False when the user already has a summary for that month (the unique index decides).</summary>
    public async Task<bool> AddAsync(SummaryDto summary, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO reporting.summaries
                (id, user_id, month, status, includes_quarter, message, insight_teaser, insight, insight_status, created_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10)
            ON CONFLICT (user_id, month) DO NOTHING
            """);
        command.Parameters.Add(new() { Value = summary.Id });
        command.Parameters.Add(new() { Value = summary.UserId });
        command.Parameters.Add(new() { Value = summary.Month });
        command.Parameters.Add(new() { Value = summary.Status.ToString().ToLowerInvariant() });
        command.Parameters.Add(new() { Value = summary.IncludesQuarter });
        command.Parameters.Add(new() { Value = (object?)summary.Message ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)summary.InsightTeaser ?? DBNull.Value });
        command.Parameters.Add(new() { Value = (object?)summary.Insight ?? DBNull.Value });
        command.Parameters.Add(new() { Value = summary.InsightStatus.ToString().ToLowerInvariant() });
        command.Parameters.Add(new() { Value = summary.CreatedAt });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    public async Task<SummaryDto?> GetForMonthAsync(Guid userId, DateOnly month, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {Columns} FROM reporting.summaries WHERE user_id = $1 AND month = $2");
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = month });
        return await ReadOneAsync(command, cancellationToken);
    }

    public async Task<SummaryDto?> FindAsync(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"SELECT {Columns} FROM reporting.summaries WHERE id = $1 AND user_id = $2");
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        return await ReadOneAsync(command, cancellationToken);
    }

    public async Task<bool> AnswerInsightAsync(
        Guid id, Guid userId, InsightStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE reporting.summaries SET insight_status = $3, insight_answered_at = $4
            WHERE id = $1 AND user_id = $2 AND insight_status = 'offered'
            """);
        command.Parameters.Add(new() { Value = id });
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = status.ToString().ToLowerInvariant() });
        command.Parameters.Add(new() { Value = now });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    private static async Task<SummaryDto?> ReadOneAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new SummaryDto(
            reader.GetGuid(0),
            reader.GetGuid(1),
            reader.GetFieldValue<DateOnly>(2),
            Enum.Parse<SummaryStatus>(reader.GetString(3), ignoreCase: true),
            reader.GetBoolean(4),
            reader.IsDBNull(5) ? null : reader.GetString(5),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            Enum.Parse<InsightStatus>(reader.GetString(8), ignoreCase: true),
            reader.GetFieldValue<DateTimeOffset>(9));
    }
}

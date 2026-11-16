using System.Text.Json;
using System.Text.Json.Serialization;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace LifeAim.Modules.Planning.Infrastructure;

/// <summary>Plain ADO.NET over the "planning" schema. Every value goes in as a parameter, never as SQL text.</summary>
internal sealed class ProposalRepository(NpgsqlDataSource dataSource)
{
    // Enums as text ("weekly"), like the conversation drafts: reordering an enum never changes saved data.
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const string OpenStatuses = "('requested', 'ready', 'offered')";

    /// <summary>Closes what is ready or offered and inserts a new request; false when one is already requested.</summary>
    public async Task<bool> RequestAsync(Guid userId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var requested = new NpgsqlCommand(
            "SELECT 1 FROM planning.proposals WHERE user_id = $1 AND status = 'requested' FOR UPDATE", connection, transaction))
        {
            requested.Parameters.Add(new() { Value = userId });
            if (await requested.ExecuteScalarAsync(cancellationToken) is not null)
            {
                return false;
            }
        }

        await using (var close = new NpgsqlCommand(
            $"UPDATE planning.proposals SET status = 'closed', closed_at = $2 WHERE user_id = $1 AND status IN {OpenStatuses}",
            connection, transaction))
        {
            close.Parameters.Add(new() { Value = userId });
            close.Parameters.Add(new() { Value = now });
            await close.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var insert = new NpgsqlCommand(
            "INSERT INTO planning.proposals (id, user_id, status, requested_at) VALUES ($1, $2, 'requested', $3)",
            connection, transaction))
        {
            insert.Parameters.Add(new() { Value = Guid.CreateVersion7(now) });
            insert.Parameters.Add(new() { Value = userId });
            insert.Parameters.Add(new() { Value = now });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IReadOnlyList<PlanProposalDto>> ListByStatusAsync(
        ProposalStatus status, int limit, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, user_id, status, attempts, requested_at FROM planning.proposals
            WHERE status = $1 ORDER BY requested_at LIMIT $2
            """);
        command.Parameters.Add(new() { Value = ToText(status) });
        command.Parameters.Add(new() { Value = limit });
        return await ReadWithGoalsAsync(command, cancellationToken);
    }

    public async Task<PlanProposalDto?> GetOpenAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT id, user_id, status, attempts, requested_at FROM planning.proposals
            WHERE user_id = $1 AND status IN ('ready', 'offered')
            """);
        command.Parameters.Add(new() { Value = userId });
        return (await ReadWithGoalsAsync(command, cancellationToken)).FirstOrDefault();
    }

    public async Task<IReadOnlyList<Guid>> AcceptedGoalIdsAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT DISTINCT g.goal_id FROM planning.goal_proposals g
            JOIN planning.proposals p ON p.id = g.proposal_id
            WHERE p.user_id = $1 AND g.status = 'accepted'
            """);
        command.Parameters.Add(new() { Value = userId });

        var ids = new List<Guid>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            ids.Add(reader.GetGuid(0));
        }

        return ids;
    }

    public async Task MarkReadyAsync(
        Guid proposalId, IReadOnlyList<GoalProposalInput> goals, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        var position = 1;
        foreach (var goal in goals)
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO planning.goal_proposals (proposal_id, goal_id, position, message, question, routines, status, updated_at)
                VALUES ($1, $2, $3, $4, $5, $6, 'open', $7)
                ON CONFLICT (proposal_id, goal_id) DO NOTHING
                """, connection, transaction);
            insert.Parameters.Add(new() { Value = proposalId });
            insert.Parameters.Add(new() { Value = goal.GoalId });
            insert.Parameters.Add(new() { Value = position++ });
            insert.Parameters.Add(new() { Value = goal.Message.Trim() });
            insert.Parameters.Add(new() { Value = goal.Question.Trim() });
            insert.Parameters.Add(new() { Value = JsonSerializer.Serialize(goal.Routines, Json), NpgsqlDbType = NpgsqlDbType.Jsonb });
            insert.Parameters.Add(new() { Value = now });
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var update = new NpgsqlCommand(
            goals.Count == 0
                ? "UPDATE planning.proposals SET status = 'closed', ready_at = $2, closed_at = $2 WHERE id = $1 AND status = 'requested'"
                : "UPDATE planning.proposals SET status = 'ready', ready_at = $2 WHERE id = $1 AND status = 'requested'",
            connection, transaction))
        {
            update.Parameters.Add(new() { Value = proposalId });
            update.Parameters.Add(new() { Value = now });
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task MarkFailedAttemptAsync(Guid proposalId, string error, int maxAttempts, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            """
            UPDATE planning.proposals
            SET attempts = attempts + 1,
                last_error = left($2, 1000),
                status = CASE WHEN attempts + 1 >= $3 THEN 'failed' ELSE status END
            WHERE id = $1 AND status = 'requested'
            """);
        command.Parameters.Add(new() { Value = proposalId });
        command.Parameters.Add(new() { Value = error });
        command.Parameters.Add(new() { Value = maxAttempts });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task MarkOfferedAsync(Guid proposalId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE planning.proposals SET status = 'offered', offered_at = $2 WHERE id = $1 AND status = 'ready'");
        command.Parameters.Add(new() { Value = proposalId });
        command.Parameters.Add(new() { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task SetGoalStatusAsync(
        Guid proposalId, Guid goalId, GoalProposalStatus status, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "UPDATE planning.goal_proposals SET status = $3, updated_at = $4 WHERE proposal_id = $1 AND goal_id = $2");
        command.Parameters.Add(new() { Value = proposalId });
        command.Parameters.Add(new() { Value = goalId });
        command.Parameters.Add(new() { Value = status.ToString().ToLowerInvariant() });
        command.Parameters.Add(new() { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task CloseAsync(Guid proposalId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"UPDATE planning.proposals SET status = 'closed', closed_at = $2 WHERE id = $1 AND status IN {OpenStatuses}");
        command.Parameters.Add(new() { Value = proposalId });
        command.Parameters.Add(new() { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async Task<IReadOnlyList<PlanProposalDto>> ReadWithGoalsAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var proposals = new List<PlanProposalDto>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                proposals.Add(new PlanProposalDto(
                    reader.GetGuid(0),
                    reader.GetGuid(1),
                    Enum.Parse<ProposalStatus>(reader.GetString(2), ignoreCase: true),
                    reader.GetInt32(3),
                    reader.GetFieldValue<DateTimeOffset>(4),
                    []));
            }
        }

        if (proposals.Count == 0)
        {
            return proposals;
        }

        await using var goals = dataSource.CreateCommand(
            """
            SELECT proposal_id, goal_id, position, message, question, routines, status
            FROM planning.goal_proposals WHERE proposal_id = ANY($1) ORDER BY position
            """);
        goals.Parameters.Add(new() { Value = proposals.Select(p => p.Id).ToArray() });

        var byProposal = new Dictionary<Guid, List<GoalProposalDto>>();
        await using (var reader = await goals.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var routines = JsonSerializer.Deserialize<List<RoutineInput>>(reader.GetString(5), Json) ?? [];
                var goal = new GoalProposalDto(
                    reader.GetGuid(1),
                    reader.GetInt32(2),
                    reader.GetString(3),
                    reader.GetString(4),
                    routines,
                    Enum.Parse<GoalProposalStatus>(reader.GetString(6), ignoreCase: true));
                if (!byProposal.TryGetValue(reader.GetGuid(0), out var list))
                {
                    byProposal[reader.GetGuid(0)] = list = [];
                }

                list.Add(goal);
            }
        }

        return proposals.Select(p => p with { Goals = byProposal.GetValueOrDefault(p.Id) ?? [] }).ToList();
    }

    private static string ToText(ProposalStatus status) => status.ToString().ToLowerInvariant();
}

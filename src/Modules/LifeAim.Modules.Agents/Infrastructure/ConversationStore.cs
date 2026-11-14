using System.Text.Json;
using LifeAim.Modules.Agents.Application.Workflow;
using Npgsql;
using NpgsqlTypes;

namespace LifeAim.Modules.Agents.Infrastructure;

/// <summary>Plain ADO.NET over the "agents" schema.</summary>
internal sealed class ConversationStore(NpgsqlDataSource dataSource, TimeProvider timeProvider) : IConversationStore
{
    public async Task<ConversationState> LoadAsync(Guid userId, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            "SELECT stage, user_name, draft FROM agents.conversations WHERE user_id = $1");
        command.Parameters.Add(new() { Value = userId });

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return new ConversationState(Stage.Greeting, UserName: null);
        }

        return new ConversationState(
            Enum.Parse<Stage>(reader.GetString(0)),
            reader.IsDBNull(1) ? null : reader.GetString(1),
            reader.IsDBNull(2) ? null : JsonSerializer.Deserialize<GoalsDraft>(reader.GetString(2), Json));
    }

    // camelCase, like the rest of LifeAim's JSON; DateOnly is written as "2026-12-25".
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<ConversationTurn>> RecentMessagesAsync(
        Guid userId,
        int count,
        CancellationToken cancellationToken)
    {
        // The newest N, turned back to oldest first.
        await using var command = dataSource.CreateCommand(
            """
            SELECT role, content FROM (
                SELECT role, content, created_at, id
                FROM agents.messages
                WHERE user_id = $1
                ORDER BY created_at DESC, id DESC
                LIMIT $2
            ) AS recent
            ORDER BY created_at, id
            """);
        command.Parameters.Add(new() { Value = userId });
        command.Parameters.Add(new() { Value = count });

        var turns = new List<ConversationTurn>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var role = reader.GetString(0) == "user" ? ConversationRole.User : ConversationRole.Assistant;
            turns.Add(new ConversationTurn(role, reader.GetString(1)));
        }

        return turns;
    }

    public async Task SaveTurnAsync(
        Guid userId,
        ConversationState state,
        string userMessage,
        string assistantMessage,
        Stage messageStage,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var connection = await dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);

        await using (var upsert = new NpgsqlCommand(
            """
            INSERT INTO agents.conversations (user_id, stage, user_name, updated_at, draft)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (user_id) DO UPDATE
                SET stage = EXCLUDED.stage, user_name = EXCLUDED.user_name, updated_at = EXCLUDED.updated_at,
                    draft = EXCLUDED.draft
            """, connection, transaction))
        {
            upsert.Parameters.Add(new() { Value = userId });
            upsert.Parameters.Add(new() { Value = state.Stage.ToString() });
            upsert.Parameters.Add(new() { Value = (object?)state.UserName ?? DBNull.Value });
            upsert.Parameters.Add(new() { Value = now });
            upsert.Parameters.Add(new()
            {
                Value = state.Draft is null ? DBNull.Value : JsonSerializer.Serialize(state.Draft, Json),
                NpgsqlDbType = NpgsqlDbType.Jsonb,
            });
            await upsert.ExecuteNonQueryAsync(cancellationToken);
        }

        // The assistant's reply gets a later timestamp so the order survives even within one millisecond.
        await InsertMessageAsync(connection, transaction, userId, "user", userMessage, messageStage, now, cancellationToken);
        await InsertMessageAsync(
            connection, transaction, userId, "assistant", assistantMessage, messageStage, now.AddTicks(10), cancellationToken);

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<bool> DeleteAsync(Guid userId, CancellationToken cancellationToken)
    {
        // One statement (a CTE), so it is atomic without an explicit transaction.
        await using var command = dataSource.CreateCommand(
            """
            WITH deleted_messages AS (DELETE FROM agents.messages WHERE user_id = $1 RETURNING 1),
                 deleted_conversation AS (DELETE FROM agents.conversations WHERE user_id = $1 RETURNING 1)
            SELECT (SELECT count(*) FROM deleted_messages) + (SELECT count(*) FROM deleted_conversation)
            """);
        command.Parameters.Add(new() { Value = userId });

        var deleted = (long)(await command.ExecuteScalarAsync(cancellationToken))!;
        return deleted > 0;
    }

    private static async Task InsertMessageAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        Guid userId,
        string role,
        string content,
        Stage stage,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken)
    {
        await using var insert = new NpgsqlCommand(
            """
            INSERT INTO agents.messages (id, user_id, role, content, stage, created_at)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, connection, transaction);
        insert.Parameters.Add(new() { Value = Guid.CreateVersion7(createdAt) });
        insert.Parameters.Add(new() { Value = userId });
        insert.Parameters.Add(new() { Value = role });
        insert.Parameters.Add(new() { Value = content });
        insert.Parameters.Add(new() { Value = stage.ToString() });
        insert.Parameters.Add(new() { Value = createdAt });
        await insert.ExecuteNonQueryAsync(cancellationToken);
    }
}

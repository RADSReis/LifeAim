using Npgsql;

namespace LifeAim.Modules.Communication.Infrastructure;

/// <summary>Maps a channel's user (a Discord id) to the LifeAim user id the other modules use.</summary>
internal sealed class ChannelUserRepository(NpgsqlDataSource dataSource, TimeProvider timeProvider)
{
    /// <summary>
    /// The LifeAim user id for this channel user, created on their first message. One statement, so two
    /// messages arriving together cannot create the same user twice: the UNIQUE constraint decides.
    /// </summary>
    public async Task<Guid> GetOrCreateAsync(
        string channel,
        string externalId,
        string displayName,
        CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();

        await using var command = dataSource.CreateCommand(
            """
            INSERT INTO communication.channel_users (id, channel, external_id, display_name, created_at)
            VALUES ($1, $2, $3, $4, $5)
            ON CONFLICT (channel, external_id) DO UPDATE SET display_name = EXCLUDED.display_name
            RETURNING id
            """);
        command.Parameters.Add(new() { Value = Guid.CreateVersion7(now) });
        command.Parameters.Add(new() { Value = channel });
        command.Parameters.Add(new() { Value = externalId });
        command.Parameters.Add(new() { Value = displayName });
        command.Parameters.Add(new() { Value = now });

        return (Guid)(await command.ExecuteScalarAsync(cancellationToken))!;
    }
}

using LifeAim.Modules.Agents.Contracts;
using Microsoft.Extensions.Logging;

namespace LifeAim.Modules.Communication.Infrastructure.DiscordBot;

/// <summary>
/// Agents' way out (IPippinChannel) on Discord: finds the user's Discord id from the LifeAim id and sends a DM.
/// </summary>
internal sealed class DiscordPippinChannel(
    ChannelUserRepository channelUsers,
    DiscordConnection connection,
    ILogger<DiscordPippinChannel> logger) : IPippinChannel
{
    public async Task<bool> SendAsync(Guid userId, string text, CancellationToken cancellationToken = default)
    {
        var externalId = await channelUsers.FindExternalIdAsync(DiscordBotService.Channel, userId, cancellationToken);
        if (!ulong.TryParse(externalId, out var discordUserId))
        {
            return false;
        }

        try
        {
            return await connection.SendDirectAsync(discordUserId, text, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Failed to send a message Pippin started.");
            return false;
        }
    }
}

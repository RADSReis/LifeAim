using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Options;

namespace LifeAim.Modules.Communication.Infrastructure.DiscordBot;

/// <summary>
/// The bot's connected client, shared: DiscordBotService connects it and sets it here; DiscordPippinChannel uses
/// it to send the messages Pippin starts himself. Null while the bot is off or stopping.
/// </summary>
internal sealed class DiscordConnection(IOptions<DiscordOptions> options)
{
    public DiscordSocketClient? Client { get; set; }

    /// <summary>
    /// Sends a DM that Pippin starts himself (not a reply). False when the bot is off or not connected, or the
    /// user is not allowed: then nothing was sent and the caller tries again later.
    /// </summary>
    public async Task<bool> SendDirectAsync(ulong discordUserId, string text, CancellationToken cancellationToken)
    {
        if (Client is not { ConnectionState: ConnectionState.Connected } client
            || !options.Value.AllowedUserIds.Contains(discordUserId))
        {
            return false;
        }

        var requestOptions = new RequestOptions { CancelToken = cancellationToken };
        var user = await client.GetUserAsync(discordUserId, requestOptions);
        if (user is null)
        {
            return false;
        }

        var channel = await user.CreateDMChannelAsync(requestOptions);
        await channel.SendMessageAsync(text, options: requestOptions);
        return true;
    }
}

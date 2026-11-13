using Discord;
using Discord.WebSocket;
using LifeAim.Modules.Agents.Contracts;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LifeAim.Modules.Communication.Infrastructure.DiscordBot;

/// <summary>
/// Keeps the bot connected to the Discord gateway for as long as the API runs, and hands DMs to Pippin.
/// </summary>
internal sealed class DiscordBotService(
    IOptions<DiscordOptions> options,
    ChannelUserRepository channelUsers,
    IPippin pippin,
    ILogger<DiscordBotService> logger) : BackgroundService
{
    private const string Channel = "discord";
    private const string NotAllowedReply =
        "Oi! Eu ainda sou uma versão privada e só converso com quem foi convidado. 🙂";
    private const string FailureReply =
        "Ops, tropecei aqui do meu lado e não consegui responder. Pode tentar de novo daqui a pouco?";

    private readonly DiscordOptions _options = options.Value;
    private DiscordSocketClient? _client;
    private CancellationToken _stoppingToken;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _stoppingToken = stoppingToken;

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            logger.LogWarning("Discord bot disabled: {Setting} is not configured.", "Discord:Token");
            return;
        }

        _client = new DiscordSocketClient(new DiscordSocketConfig
        {
            // Only what Pippin needs: DMs, plus basic server info the library uses to become ready.
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.DirectMessages,
        });
        _client.Log += OnLogAsync;
        _client.MessageReceived += OnMessageReceivedAsync;

        await _client.LoginAsync(TokenType.Bot, _options.Token);
        await _client.StartAsync(); // Connects in the background; the library reconnects by itself.

        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // The API is shutting down; StopAsync disconnects the bot.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        await base.StopAsync(cancellationToken);

        if (_client is not null)
        {
            await _client.StopAsync();
            await _client.LogoutAsync();
            await _client.DisposeAsync();
        }
    }

    private Task OnMessageReceivedAsync(SocketMessage message)
    {
        // 1. Never answer bots, including Pippin itself: that would loop forever.
        if (message.Author.IsBot)
        {
            return Task.CompletedTask;
        }

        // 2. Pippin only talks in private.
        if (message.Channel is not IDMChannel)
        {
            return Task.CompletedTask;
        }

        // Discord.Net runs this handler on its gateway task: anything slow here (the database, and soon
        // the AI, which takes seconds) would stall every other event. So the work runs on its own task.
        _ = Task.Run(() => AnswerAsync(message));
        return Task.CompletedTask;
    }

    private async Task AnswerAsync(SocketMessage message)
    {
        try
        {
            // 3. While LifeAim is private, only allowed users get a real answer.
            if (!_options.AllowedUserIds.Contains(message.Author.Id))
            {
                await message.Channel.SendMessageAsync(NotAllowedReply);
                return;
            }

            // Shows "Pippin is typing..." until the reply is sent.
            using var typing = message.Channel.EnterTypingState();

            // 4. From here on LifeAim only knows its own user id, never the Discord one.
            var displayName = message.Author.GlobalName ?? message.Author.Username;
            var userId = await channelUsers.GetOrCreateAsync(
                Channel, message.Author.Id.ToString(), displayName, _stoppingToken);

            var reply = await pippin.ReplyAsync(new PippinMessage(userId, displayName, message.Content), _stoppingToken);
            await message.Channel.SendMessageAsync(reply);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to answer a Discord DM.");
            try
            {
                await message.Channel.SendMessageAsync(FailureReply);
            }
            catch (Exception sendEx)
            {
                logger.LogError(sendEx, "Failed to tell the user that the answer failed.");
            }
        }
    }

    // Sends Discord.Net's own log messages to the regular .NET logger (and so to `docker logs`).
    private Task OnLogAsync(LogMessage message)
    {
        var level = message.Severity switch
        {
            LogSeverity.Critical => LogLevel.Critical,
            LogSeverity.Error => LogLevel.Error,
            LogSeverity.Warning => LogLevel.Warning,
            LogSeverity.Info => LogLevel.Information,
            LogSeverity.Verbose => LogLevel.Debug,
            _ => LogLevel.Trace,
        };

        logger.Log(level, message.Exception, "[{Source}] {Message}", message.Source, message.Message);
        return Task.CompletedTask;
    }
}

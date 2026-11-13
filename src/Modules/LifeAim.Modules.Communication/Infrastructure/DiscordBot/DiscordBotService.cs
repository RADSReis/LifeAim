using Discord;
using Discord.WebSocket;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace LifeAim.Modules.Communication.Infrastructure.DiscordBot;

/// <summary>
/// Keeps the bot connected to the Discord gateway for as long as the API runs, and answers DMs.
/// </summary>
internal sealed class DiscordBotService(
    IOptions<DiscordOptions> options,
    ILogger<DiscordBotService> logger) : BackgroundService
{
    private const string GreetingReply = "Olá! Eu sou o Pippin 👋";
    private const string NotAllowedReply =
        "Oi! Eu ainda sou uma versão privada e só converso com quem foi convidado. 🙂";

    private readonly DiscordOptions _options = options.Value;
    private DiscordSocketClient? _client;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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

    private async Task OnMessageReceivedAsync(SocketMessage message)
    {
        // 1. Never answer bots, including Pippin itself: that would loop forever.
        if (message.Author.IsBot)
        {
            return;
        }

        // 2. Pippin only talks in private.
        if (message.Channel is not IDMChannel)
        {
            return;
        }

        try
        {
            // 3. While LifeAim is private, only allowed users get a real answer.
            if (!_options.AllowedUserIds.Contains(message.Author.Id))
            {
                await message.Channel.SendMessageAsync(NotAllowedReply);
                return;
            }

            // 4. Phase 1: a fixed reply, just to prove the pipe works.
            await message.Channel.SendMessageAsync(GreetingReply);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to answer a Discord DM.");
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

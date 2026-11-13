namespace LifeAim.Modules.Communication.Infrastructure.DiscordBot;

/// <summary>
/// The "Discord" configuration section. Locally it comes from User Secrets; on the VPS from
/// environment variables (Discord__Token, Discord__AllowedUserIds__0, ...).
/// </summary>
public sealed class DiscordOptions
{
    public const string SectionName = "Discord";

    /// <summary>The bot token. Without it the bot stays off and the API runs normally.</summary>
    public string? Token { get; init; }

    /// <summary>Discord user IDs allowed to talk to Pippin while LifeAim is private.</summary>
    public List<ulong> AllowedUserIds { get; init; } = [];
}

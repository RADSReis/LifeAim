namespace LifeAim.Modules.Agents.Contracts;

/// <summary>
/// Pippin, as the channels see it: a message from a LifeAim user goes in, Pippin's reply comes out.
/// Channels (Discord today, maybe Telegram later) only talk to this, so Pippin knows nothing about them.
/// </summary>
public interface IPippin
{
    Task<string> ReplyAsync(PippinMessage message, CancellationToken cancellationToken = default);
}

/// <param name="UserId">LifeAim's user id (not the channel's).</param>
/// <param name="DisplayName">How the channel calls the user, until Pippin learns their name.</param>
/// <param name="Text">What the user wrote.</param>
public sealed record PippinMessage(Guid UserId, string DisplayName, string Text);

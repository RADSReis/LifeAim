namespace LifeAim.Modules.Agents.Contracts;

/// <summary>
/// The way out for messages Pippin starts himself (the plan proposal is ready, and later the daily check-ins).
/// Agents declares it and a channel module (Communication, with Discord) implements it, so Agents never
/// depends on Discord, and the dependency between the two modules keeps pointing one way.
/// </summary>
public interface IPippinChannel
{
    /// <summary>Sends <paramref name="text"/> to the LifeAim user. False when it could not be delivered.</summary>
    Task<bool> SendAsync(Guid userId, string text, CancellationToken cancellationToken = default);
}

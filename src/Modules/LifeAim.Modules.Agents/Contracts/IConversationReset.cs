namespace LifeAim.Modules.Agents.Contracts;

/// <summary>
/// Starts a user's conversation with Pippin over: forgets the stage, the name, the draft and the history,
/// so the next message is a first contact again. For end-to-end tests; it never touches saved goals.
/// </summary>
public interface IConversationReset
{
    /// <returns>False when the user had no conversation to reset.</returns>
    Task<bool> ResetAsync(Guid userId, CancellationToken cancellationToken = default);
}

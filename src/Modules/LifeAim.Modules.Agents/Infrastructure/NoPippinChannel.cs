using LifeAim.Modules.Agents.Contracts;

namespace LifeAim.Modules.Agents.Infrastructure;

/// <summary>
/// The default when no channel module is registered (tests, or the API without Communication): nothing is
/// delivered, so nothing is marked as sent and the job tries again later.
/// </summary>
internal sealed class NoPippinChannel : IPippinChannel
{
    public Task<bool> SendAsync(Guid userId, string text, CancellationToken cancellationToken = default) =>
        Task.FromResult(false);
}

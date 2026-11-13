using LifeAim.Modules.Agents.Contracts;

namespace LifeAim.Modules.Agents.Application;

/// <summary>
/// Stand-in until Pippin gets its AI (Phase 3): proves that a DM travels Discord → Communication →
/// Agents and back. Replaced by the real agent, behind the same IPippin.
/// </summary>
internal sealed class PlaceholderPippin : IPippin
{
    public Task<string> ReplyAsync(PippinMessage message, CancellationToken cancellationToken = default) =>
        Task.FromResult($"Olá, {message.DisplayName}! Eu sou o Pippin 👋 Ainda estou sem IA, mas já sei quem você é.");
}

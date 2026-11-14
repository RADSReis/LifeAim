using LifeAim.Modules.Agents.Contracts;

namespace LifeAim.Modules.Agents.Application.Workflow;

internal sealed class ConversationReset(IConversationStore store) : IConversationReset
{
    public Task<bool> ResetAsync(Guid userId, CancellationToken cancellationToken = default) =>
        store.DeleteAsync(userId, cancellationToken);
}

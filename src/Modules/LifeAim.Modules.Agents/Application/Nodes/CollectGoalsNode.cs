using LifeAim.Modules.Agents.Application.Workflow;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// ③ Waiting for the user's goals. For now only a stop sign at the end of the finished part of the
/// workflow: the next step teaches it to turn the answer into goals and save them.
/// </summary>
internal sealed class CollectGoalsNode : INode
{
    public Stage Stage => Stage.CollectGoals;

    public Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(NodeResult.Answer(
            "Anotado! 📝 Entender e guardar as suas metas é justamente a próxima coisa que eu vou aprender. Volte logo!"));
}

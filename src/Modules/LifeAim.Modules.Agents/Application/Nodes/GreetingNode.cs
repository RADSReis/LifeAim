using LifeAim.Modules.Agents.Application.Workflow;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// ① First contact (UX "Primeiro contato"): plain code, no AI. Whatever the user wrote, Pippin
/// introduces itself and asks their name.
/// </summary>
internal sealed class GreetingNode : INode
{
    public const string Question = "Olá! Meu nome é Pippin. Como você se chama?";

    public Stage Stage => Stage.Greeting;

    public Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken) =>
        Task.FromResult(NodeResult.Answer(Question, nextStage: Stage.AskName));
}

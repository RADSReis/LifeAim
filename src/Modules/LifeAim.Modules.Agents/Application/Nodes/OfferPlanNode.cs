using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Application.Workflow;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// Pippin sent, on his own, "Preparei uma primeira proposta… Quer dar uma olhada?" (Funcionalidade 2, "Convite
/// quando estiver pronto"). Yes → the first 3 goals (→ ChoosePlanGoal); not now → it stays saved for later.
/// </summary>
internal sealed class OfferPlanNode(IChatClient chatClient, PlanReview review) : INode
{
    public Stage Stage => Stage.OfferPlan;

    public string? PendingQuestion(ConversationContext context) => PlanReview.OfferQuestion;

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<OfferAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected;
        }

        switch (answer.Decision?.Trim().ToLowerInvariant())
        {
            case "yes":
                var proposal = await review.OpenProposalAsync(context.UserId, cancellationToken);
                if (proposal is null)
                {
                    context.Draft = GoalsDraft.Empty(context.Draft?.Horizon);
                    return NodeResult.Answer(
                        "Hmm, não encontrei a proposta aqui. 🤔 Me conte se quiser mudar alguma meta.", Stage.CollectGoals);
                }

                return await review.ShowChoicesAsync(
                    context, proposal,
                    "Analisei suas metas e já tenho algumas ideias de como podemos começar! Que tal trabalharmos primeiro em algumas delas?",
                    cancellationToken);

            case "later":
                context.Draft = GoalsDraft.Empty(context.Draft?.Horizon);
                return NodeResult.Answer(
                    "Tudo bem! A proposta fica guardada. Quando quiser ver, é só me pedir: \"quero ver a proposta\". 🙂",
                    Stage.CollectGoals);

            case "greeting":
                return NodeResult.Greeting;

            default:
                return NodeResult.NotExpected;
        }
    }

    private const string Instructions =
        """
        Você ajuda o Pippin, um assistente que conversa em português. O Pippin preparou uma proposta de planejamento
        das metas do usuário e perguntou: "Quer dar uma olhada no que pensei?".

        Classifique a resposta do usuário em decision:
        - "yes": quer ver a proposta agora ("sim", "quero", "bora", "mostra", "pode mandar").
        - "later": não quer ver agora ("agora não", "depois", "mais tarde", "não").
        - "greeting": é apenas um cumprimento ou conversa social ("oi", "bom dia").
        - "other": a mensagem é sobre outro assunto (outra meta, um progresso, uma pergunta).
        """;

    internal sealed record OfferAnswer(string? Decision);
}

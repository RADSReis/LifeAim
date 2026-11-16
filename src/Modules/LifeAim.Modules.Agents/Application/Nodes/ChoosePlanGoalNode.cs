using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// Pippin listed up to 3 goals of the proposal and asked which one to talk about. The user may pick one, several, or
/// a goal that was not listed: the AI maps the answer to goal numbers, and the code puts the first one in focus
/// (one goal at a time), keeping the others for the next rounds.
/// </summary>
internal sealed class ChoosePlanGoalNode(IChatClient chatClient, PlanReview review) : INode
{
    public Stage Stage => Stage.ChoosePlanGoal;

    public string? PendingQuestion(ConversationContext context)
    {
        var shown = (context.Draft?.Shown ?? [])
            .Select(id => context.SavedGoals.FirstOrDefault(g => g.Id == id))
            .OfType<GoalDto>()
            .ToList();
        return shown.Count == 0 ? null : PlanReview.Choices(shown);
    }

    // From the classifier ("quero ver a proposta", "vamos ver o plano da leitura"): that goal, or the list.
    public async Task<NodeResult> EnterAsync(ConversationContext context, Guid? goalId, CancellationToken cancellationToken)
    {
        var proposal = await review.OpenProposalAsync(context.UserId, cancellationToken);
        if (proposal is null)
        {
            return NodeResult.Answer(
                "Ainda não tenho uma proposta de planejamento pronta para você. Assim que eu tiver, te aviso! 😉",
                Stage.CollectGoals);
        }

        return goalId is { } id && proposal.Goals.Any(g => g.GoalId == id)
            ? await review.StartGoalAsync(context, proposal, [id], "Claro!", cancellationToken)
            : await review.ShowChoicesAsync(context, proposal, "Claro! Vamos ver a proposta.", cancellationToken);
    }

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var proposal = await review.OpenProposalAsync(context.UserId, cancellationToken);
        if (proposal is null || proposal.Id != context.Draft?.ProposalId)
        {
            context.Draft = GoalsDraft.Empty(context.Draft?.Horizon);
            return NodeResult.NotExpectedAfter("A proposta que eu tinha preparado não está mais aberta.", Stage.CollectGoals);
        }

        var open = PlanReview.OpenGoals(proposal, context.SavedGoals);
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(open.Select(pair => pair.Goal).ToList(), context.Draft?.Shown ?? [])),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<ChoiceAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected;
        }

        switch (answer.Decision?.Trim().ToLowerInvariant())
        {
            case "choose":
                var chosen = (answer.GoalNumbers ?? [])
                    .Where(n => n >= 1 && n <= open.Count)
                    .Distinct()
                    .Select(n => open[n - 1].Goal)
                    .ToList();
                if (chosen.Count == 0)
                {
                    return NodeResult.NotExpected;
                }

                var prefix = chosen.Count == 1
                    ? "Ótimo! Vamos pensar nessa."
                    : $"Ótimo! Vamos começar por **{chosen[0].Name}** e depois seguimos para as outras.";
                return await review.StartGoalAsync(context, proposal, chosen.Select(g => g.Id).ToList(), prefix, cancellationToken);

            case "later":
                context.Draft = GoalsDraft.Empty(context.Draft?.Horizon);
                return NodeResult.Answer(
                    "Tudo bem! Quando quiser continuar, é só me pedir: \"quero ver a proposta\". 🙂", Stage.CollectGoals);

            case "greeting":
                return NodeResult.Greeting;

            default:
                return NodeResult.NotExpected;
        }
    }

    private static string Instructions(IReadOnlyList<GoalDto> open, IReadOnlyList<Guid> shown)
    {
        var list = string.Join("\n", open.Select((g, i) =>
            $"{i + 1}. {g.Name}{(shown.Contains(g.Id) ? " (mostrada agora)" : "")}"));

        return $"""
            Você ajuda o Pippin, um assistente que conversa em português. O Pippin está apresentando uma proposta de
            planejamento das metas do usuário e perguntou sobre qual meta ele quer conversar. Ele mostrou algumas
            metas, mas o usuário pode escolher qualquer uma desta lista:
            {list}

            Classifique a resposta do usuário em decision:
            - "choose": escolheu uma ou mais metas da lista (pelo nome, pelo assunto, "a primeira", "todas").
              goalNumbers: os números escolhidos, na ordem em que ele falou ("todas" → todos os mostrados agora).
            - "later": não quer continuar agora ("depois", "agora não", "chega por hoje").
            - "greeting": é apenas um cumprimento ou conversa social ("oi", "bom dia").
            - "other": a mensagem é sobre outro assunto, ou fala de uma meta que não está na lista.
            Para as decisões que não são "choose", goalNumbers é uma lista vazia.
            """;
    }

    internal sealed record ChoiceAnswer(string? Decision, List<int>? GoalNumbers);
}

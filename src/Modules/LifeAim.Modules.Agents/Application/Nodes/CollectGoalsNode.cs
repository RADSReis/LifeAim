using System.Globalization;
using LifeAim.Modules.Agents.Application.Workflow;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// ③ Collecting goals. People answer freely: only the period, only the goals, both in one sentence,
/// several goals at once, with or without numbers. So one AI call fills every "slot" it can find in the
/// message (slot filling) and the code asks only for what is still missing:
/// period only → asks for the goals; goals → shows what it understood and waits for confirmation.
/// </summary>
internal sealed class CollectGoalsNode(IChatClient chatClient, TimeProvider timeProvider) : INode
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");

    public Stage Stage => Stage.CollectGoals;

    // With goals already saved, the question is about adding more, not about the first ones.
    public string? PendingQuestion(ConversationContext context) =>
        context.Draft?.Horizon is not { } horizon ? GoalHorizon.Question(timeProvider)
        : context.SavedGoals.Count > 0 ? $"Quer adicionar mais alguma meta até {horizon.ToString("dd/MM/yyyy", PtBr)}?"
        : GoalsQuestion(horizon);

    // Coming from another stage (detailing): what was in progress there is left behind, the period stays.
    public Task<NodeResult> EnterAsync(ConversationContext context, Guid? goalId, CancellationToken cancellationToken)
    {
        context.Draft = GoalsDraft.Empty(context.Draft?.Horizon);
        return RunAsync(context, cancellationToken);
    }

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(context)),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<GoalsAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected; // → the router
        }

        var horizon = GoalExtraction.ParseDate(answer.HorizonDate) ?? context.Draft?.Horizon;
        var goals = GoalExtraction.ToDrafts(answer.Goals, horizon);

        // The edges of this node, decided by code from what the AI found.
        if (goals.Count > 0)
        {
            context.Draft = new GoalsDraft(horizon, goals, GoalExtraction.ToDoubts(answer.Doubts));
            return NodeResult.Answer(GoalExtraction.Confirmation(context.Draft), nextStage: Stage.ConfirmGoals);
        }

        if (answer.Finished)
        {
            return NodeResult.Answer(
                "Fechado! Suas metas estão guardadas. 💪 Quando quiser, me conte o que fez (\"hoje li 10 páginas\") " +
                "ou peça para ver, mudar ou cancelar uma meta.");
        }

        if (answer.IsGreeting)
        {
            return NodeResult.Greeting; // → greets back and repeats the question
        }

        if (!answer.AboutGoals)
        {
            return NodeResult.NotExpected; // → the router
        }

        context.Draft = GoalsDraft.Empty(horizon);
        return horizon is { } date
            ? NodeResult.Answer($"Combinado: objetivos até {date.ToString("dd/MM/yyyy", PtBr)}. 🎯 {GoalsQuestion(date)} Pode mandar várias de uma vez.")
            : NodeResult.Answer("Me conta quais metas você quer alcançar. Pode mandar várias de uma vez! 🎯");
    }

    private static string GoalsQuestion(DateOnly horizon) =>
        $"Quais metas você quer alcançar até {horizon.ToString("dd/MM/yyyy", PtBr)}?";

    // The prompt carries what the AI can't know: today's date, the question asked and the period already agreed.
    private string Instructions(ConversationContext context)
    {
        var today = GoalHorizon.Today(timeProvider);
        var agreedPeriod = context.Draft?.Horizon is { } horizon
            ? $"O período já combinado vai até {horizon:yyyy-MM-dd}."
            : "O período ainda não foi combinado.";

        return $"""
            Você ajuda o Pippin, um assistente que conversa em português para cadastrar as metas de vida do usuário.
            Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.
            O Pippin perguntou: "{PendingQuestion(context)}". {agreedPeriod}
            Metas que o usuário JÁ salvou: {(context.SavedGoals.Count == 0 ? "nenhuma" : string.Join("; ", context.SavedGoals.Select(g => g.Name)))}.
            Se a mensagem fala de uma meta já salva (para ver, detalhar, mudar, cancelar, comentar ou contar um progresso
            como "hoje li 10 páginas"), ela NÃO é uma meta nova: não a coloque em goals, e use aboutGoals false.

            O usuário pode responder do jeito que quiser: só o período, só as metas, tudo junto, várias metas numa frase.
            Extraia tudo o que a mensagem trouxer:

            - aboutGoals: true se a mensagem fala do período ou de metas/objetivos; false para outro assunto
              (cumprimento, pergunta ou pedido sem relação com metas).
            - isGreeting: true só quando a mensagem é apenas um cumprimento ou conversa social, sem metas e sem pedido
              ("oi", "olá", "bom dia", "e aí, tudo bem?"). Cumprimento junto com metas ("oi! quero correr 5 km")
              → isGreeting false, e extraia as metas.
            - horizonDate: a data final do período, no formato yyyy-MM-dd ("fim do ano" → 31/12 deste ano;
              "até junho" → último dia de junho). null se o usuário não disse.
            - goals: uma entrada por meta mencionada (lista vazia se nenhuma).
            - doubts: siga as regras de dúvidas abaixo.
            - finished: true se o usuário diz que não quer cadastrar mais metas ("não", "é só isso", "por enquanto é isso").

            {GoalExtraction.GoalFieldRules}
            {GoalExtraction.NoInventionRule}

            {GoalExtraction.DoubtRules}
            """;
    }

    internal sealed record GoalsAnswer(
        bool AboutGoals,
        bool IsGreeting,
        string? HorizonDate,
        List<ExtractedGoal>? Goals,
        List<ExtractedDoubt>? Doubts,
        bool Finished);
}

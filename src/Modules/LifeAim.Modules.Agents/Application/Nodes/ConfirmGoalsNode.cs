using System.Globalization;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// ④ Pippin showed the goals it understood and asked "Confere?". The AI classifies the answer and, when
/// the user corrects something (or answers a doubt), returns the whole updated list. The code takes the
/// edge: confirm → save through the Goals module; change → show the list again; anything else → router.
/// Nothing is saved before the user agrees: the AI may have misunderstood.
/// </summary>
internal sealed class ConfirmGoalsNode(IChatClient chatClient, IGoalService goals, TimeProvider timeProvider) : INode
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Stage Stage => Stage.ConfirmGoals;

    public string? PendingQuestion(ConversationContext context) =>
        context.Draft is { Goals.Count: > 0 } draft ? GoalExtraction.Confirmation(draft) : null;

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var draft = context.Draft ?? GoalsDraft.Empty(null);

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(draft)),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<ConfirmationAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected;
        }

        var decision = answer.Decision?.Trim().ToLowerInvariant();
        var openDoubts = GoalExtraction.ToDoubts(answer.Doubts);

        // Never save in the same message that answers a doubt, nor while one is open: a choice changes the
        // goals, so the user sees the result and says "sim" to it. A "sim" to a choice is not a choice.
        var needsChoice = decision == Decisions.Confirm && openDoubts.Count > 0;
        if (decision == Decisions.Confirm && (draft.Doubts.Count > 0 || openDoubts.Count > 0))
        {
            decision = Decisions.Change;
        }

        // The AI returns the full list; if it forgot it, the goals shown to the user are still valid.
        var updated = GoalExtraction.ToDrafts(answer.Goals, draft.Horizon);
        if (updated.Count == 0 && decision != Decisions.Change)
        {
            updated = [.. draft.Goals];
        }

        switch (decision)
        {
            case Decisions.Confirm:
                await SaveAsync(context.UserId, updated, cancellationToken);
                context.Draft = GoalsDraft.Empty(draft.Horizon);
                return NodeResult.Answer(
                    $"Salvei! ✅\n{GoalExtraction.List(updated)}\n\nQuer adicionar mais alguma meta? É só me contar.",
                    nextStage: Stage.CollectGoals);

            case Decisions.Change when updated.Count == 0:
                context.Draft = GoalsDraft.Empty(draft.Horizon);
                return NodeResult.Answer(
                    "Tudo bem, descartei essas metas. Quais metas você quer alcançar?", nextStage: Stage.CollectGoals);

            case Decisions.Change:
                context.Draft = new GoalsDraft(draft.Horizon, updated, openDoubts);
                var confirmation = GoalExtraction.Confirmation(context.Draft);
                return NodeResult.Answer(
                    needsChoice ? $"Preciso que você escolha uma das opções antes de salvar.\n\n{confirmation}" : confirmation);

            default:
                return NodeResult.NotExpected; // "other" → the router, which repeats the confirmation
        }
    }

    private async Task SaveAsync(Guid userId, IReadOnlyList<GoalDraft> drafts, CancellationToken cancellationToken)
    {
        foreach (var goal in drafts)
        {
            await goals.CreateAsync(
                new CreateGoalRequest(
                    userId, goal.Name, goal.Description, goal.Indicator, goal.DueDate,
                    goal.StartValue, goal.TargetValue, goal.Unit),
                cancellationToken);
        }
    }

    // The draft goes to the AI as JSON: the exact data it must keep or change, not a text to re-read.
    private string Instructions(GoalsDraft draft)
    {
        var today = GoalHorizon.Today(timeProvider);
        var shownGoals = draft.Goals.Select(g => new ExtractedGoal(
            g.Name, g.Description, g.Indicator, g.StartValue, g.TargetValue, g.Unit, g.DueDate?.ToString("yyyy-MM-dd")));

        return $"""
            Você ajuda o Pippin, um assistente que conversa em português para cadastrar as metas de vida do usuário.
            Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.{(draft.Horizon is { } h ? $" O período combinado vai até {h:yyyy-MM-dd}." : "")}

            O Pippin mostrou ao usuário as metas que entendeu e perguntou se está certo.
            Metas mostradas (JSON):
            {JsonSerializer.Serialize(shownGoals, Json)}
            Dúvidas abertas, perguntadas como escolha (JSON; o usuário viu cada opção com a sua letra):
            {(draft.Doubts.Count == 0 ? "nenhuma" : JsonSerializer.Serialize(GoalExtraction.DoubtsForPrompt(draft.Doubts), Json))}

            Uma dúvida só é resolvida quando o usuário ESCOLHE uma opção: pela letra ("A", "a", "letra b", "a primeira")
            ou repetindo o conteúdo dela. "sim", "não", "ok", "pode ser", "tanto faz" NÃO escolhem nenhuma opção:
            nesse caso mantenha a dúvida, sem mudar as metas. Ao escolher, aplique a opção às metas exatamente como
            ela está escrita e tire a dúvida da lista.

            Classifique a resposta do usuário em decision:
            - "{Decisions.Confirm}": concorda com as metas e quer salvar ("sim", "isso", "pode salvar", "perfeito").
            - "{Decisions.Change}": escolhe uma opção de uma dúvida, ou quer corrigir, remover ou acrescentar metas.
            - "{Decisions.Other}": a mensagem não é sobre essas metas.

            goals: a lista COMPLETA e atualizada, aplicando o que o usuário disse; copie sem mudar as metas que ele não
            mencionou; tire as que ele pediu para remover. Para "{Decisions.Other}", devolva a lista mostrada.
            doubts: as dúvidas que continuam sem escolha, copiadas como estão (pergunta e opções, sem as letras),
            mais alguma nova se a correção criou outra ambiguidade. Lista vazia se não sobrou nenhuma.

            {GoalExtraction.GoalFieldRules}

            {GoalExtraction.DoubtRules}
            """;
    }

    /// <summary>The possible decisions, as plain text: what the AI writes in "decision".</summary>
    private static class Decisions
    {
        public const string Confirm = "confirm";
        public const string Change = "change";
        public const string Other = "other";
    }

    internal sealed record ConfirmationAnswer(string Decision, List<ExtractedGoal>? Goals, List<ExtractedDoubt>? Doubts);
}

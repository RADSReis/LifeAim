using System.Globalization;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// ⑤ Deepening the saved goals, one at a time, as a short conversation (a conversational node: one AI call per
/// message returns the decision and the updated goal and plan; the code takes the edges and saves).
/// Per goal there are two moments:
/// 1. Pippin asked the one question (what is missing + "qual o seu plano?"). The answer becomes a plan, the
///    user's own or Pippin's proposal ("pode propor", "não sei"), shown for confirmation.
/// 2. Pippin showed the plan and asked "Está certo?". "sim" saves; a correction shows it again; and a message
///    about something else means the user ignored the question, which counts as "sim".
/// </summary>
internal sealed class DetailGoalsNode(
    IChatClient chatClient,
    GoalDetailing detailing,
    TimeProvider timeProvider) : INode
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Stage Stage => Stage.DetailGoals;

    public string? PendingQuestion(ConversationContext context) => context.Draft switch
    {
        { Goals.Count: > 0, Plan: not null } draft => GoalPlan.Confirmation(draft),
        { Goals.Count: > 0 } draft => draft.Question ?? GoalDetailing.DefaultQuestion(draft.Goals[0]),
        _ => null,
    };

    // Coming from the classifier ("vamos falar da meta dos livros"): that goal first, then the rest of the queue.
    // The message itself is not an answer to the question, so it is not run through RunAsync.
    public async Task<NodeResult> EnterAsync(ConversationContext context, Guid? goalId, CancellationToken cancellationToken)
    {
        if (goalId is not { } id)
        {
            return NodeResult.NotExpected;
        }

        var rest = context.Draft?.DetailQueue?.Where(queued => queued != id) ?? [];
        return await detailing.NextAsync(context, [id, .. rest], "Claro!", cancellationToken);
    }

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var draft = context.Draft;
        if (draft is not { DetailQueue.Count: > 0, Goals.Count: > 0 })
        {
            return await detailing.NextAsync(context, [], "Não tenho nenhuma meta para detalhar agora.", cancellationToken);
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(draft, context.SavedGoals)),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<DetailAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected;
        }

        var decision = answer.Decision?.Trim().ToLowerInvariant();
        var goalId = draft.DetailQueue[0];
        var rest = draft.DetailQueue.Skip(1).ToList();
        var goal = draft.Goals[0];
        var waitingConfirmation = draft.Plan is not null;

        switch (decision)
        {
            // The user described the plan (or corrected it): the plan is theirs, and so are the words.
            case Decisions.Answer or Decisions.Change:
            {
                var updated = Updated(goal, answer.Goal, draft.Horizon) with
                {
                    UserPlan = Append(goal.UserPlan, context.IncomingText),
                };
                return Show(context, draft, updated, answer, RoutineSource.User);
            }

            // "pode propor", "não sei": Pippin proposes a plan and says what it assumed.
            case Decisions.Propose:
                return Show(context, draft, Updated(goal, answer.Goal, draft.Horizon) with { UserPlan = goal.UserPlan },
                    answer, RoutineSource.Pippin);

            case Decisions.Confirm when waitingConfirmation:
                await detailing.SaveAsync(context.UserId, goalId, goal, draft.Plan, cancellationToken);
                return await detailing.NextAsync(context, rest, $"Salvei o plano de **{goal.Name}**! ✅", cancellationToken);

            case Decisions.Skip:
                return await detailing.NextAsync(
                    context, rest, $"Tudo bem! Quando quiser, é só me contar o plano de **{goal.Name}**.", cancellationToken);

            case Decisions.Greeting:
                return NodeResult.Greeting;

            // "cancela a meta do livro", "delete essa meta", "pausa a corrida": never a plan correction. The goals
            // agent takes it and the code asks the explicit "sim"; after it, the detailing goes on (ConfirmCancelGoal).
            case Decisions.GoalAction:
                return NodeResult.GoalTalk;

            // The user moved on without answering "Está certo?": that counts as "sim". The plan is saved, the
            // conversation goes to the next goal, and the message is handled from there.
            case Decisions.Other when waitingConfirmation:
                await detailing.SaveAsync(context.UserId, goalId, goal, draft.Plan, cancellationToken);
                var next = await detailing.NextAsync(
                    context, rest, $"Salvei o plano de **{goal.Name}**! ✅", cancellationToken, withQuestion: false);
                return NodeResult.NotExpectedAfter(next.Reply!, next.NextStage);

            default:
                return NodeResult.NotExpected; // → the path classifier, which repeats the question
        }
    }

    private static NodeResult Show(
        ConversationContext context, GoalsDraft draft, GoalDraft goal, DetailAnswer answer, RoutineSource source)
    {
        context.Draft = draft with
        {
            Goals = [goal],
            Plan = new PlanDraft(GoalPlan.ToRoutines(answer.Routines, source), source),
            Assumptions = GoalExtraction.CleanAssumptions(answer.Assumptions),
        };
        return NodeResult.Answer(GoalPlan.Confirmation(context.Draft));
    }

    // The AI returns the whole goal; if it forgot it, the goal stays as it was.
    private static GoalDraft Updated(GoalDraft goal, ExtractedGoal? returned, DateOnly? horizon) =>
        GoalExtraction.ToDrafts(returned is null ? [] : [returned], horizon).FirstOrDefault() is { } updated
            ? updated with { DueDate = updated.DueDate ?? goal.DueDate }
            : goal;

    // The plan "as the user wrote it": every message in which they described or corrected it, in order.
    private static string Append(string? plan, string message) =>
        string.IsNullOrWhiteSpace(plan) ? message.Trim() : $"{plan}\n{message.Trim()}";

    private string Instructions(GoalsDraft draft, IReadOnlyList<GoalDto> savedGoals)
    {
        // The other goals, so an activity of one ("leio 10 páginas") does not end up in another's plan.
        var others = savedGoals.Where(g => g.Id != draft.DetailQueue![0]).Select(g => g.Name).ToList();
        var today = GoalHorizon.Today(timeProvider);
        var goal = draft.Goals[0];
        var moment = draft.Plan is null
            ? $"""
              O Pippin fez esta pergunta sobre a meta e espera a resposta:
              "{draft.Question ?? GoalDetailing.DefaultQuestion(goal)}"
              """
            : $"""
              O Pippin mostrou este plano e perguntou se está certo:
              {JsonSerializer.Serialize(draft.Plan.Routines.Select(GoalPlan.ToExtracted), Json)}
              O que ele supôs: {(draft.Assumptions is { Count: > 0 } a ? string.Join(" | ", a) : "nada")}
              """;

        return $"""
            Você ajuda o Pippin, um assistente que conversa em português para cadastrar as metas de vida do usuário.
            Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.{(draft.Horizon is { } h ? $" O período combinado vai até {h:yyyy-MM-dd}." : "")}

            O Pippin está aprofundando esta meta já salva (JSON):
            {JsonSerializer.Serialize(GoalExtraction.ToExtracted(goal), Json)}
            {moment}

            Classifique a resposta do usuário em decision:
            - "{Decisions.Answer}": ele respondeu a pergunta descrevendo como vai fazer (dias, horários, quanto por vez)
              ou dando informações da meta (valor de hoje, tamanho dos livros...).
            - "{Decisions.Propose}": ele pede que o Pippin proponha ("pode propor", "me sugere"), diz que não sabe, ou
              respondeu só parte (ex.: só o peso de hoje) sem dizer como vai fazer. Proponha você um plano simples e
              realista para o prazo, coerente com o que ele disse.
            - "{Decisions.Confirm}": concorda com o plano mostrado ("sim", "isso", "perfeito", "pode salvar").
            - "{Decisions.Change}": corrige o plano mostrado ou a meta ("prefiro à noite", "na verdade são 3 livros").
            - "{Decisions.Skip}": quer deixar esta meta para depois ("pula", "depois eu vejo", "agora não").
            - "{Decisions.Greeting}": é apenas um cumprimento ou conversa social ("oi", "olá", "bom dia").
            - "{Decisions.GoalAction}": pede para cancelar, excluir, apagar, deletar, desistir, encerrar, pausar ou
              trocar esta meta ou outra ("cancele a meta do livro, vou mudar a meta", "delete a meta de ler livros",
              "pausa a corrida"). Isso NUNCA é "{Decisions.Change}": mudar o plano ou a meta é corrigir valores,
              dias ou horários; tirar a meta da lista é "{Decisions.GoalAction}".
            - "{Decisions.Other}": a mensagem não é sobre esta meta (outra meta, uma pergunta ou pedido de outro assunto).

            goal: a meta COMPLETA e atualizada com o que o usuário disse (ex.: "peso 97" → startValue 97); mantenha o
            resto. Informações úteis que não cabem nos campos nem nas rotinas ("livros de umas 300 páginas") vão para
            description, somadas ao que já havia; não repita ali o que já está nas rotinas. Não preencha userPlan
            (o código guarda as palavras do usuário).
            {GoalPlan.RoutineRules}
            Só as rotinas DESTA meta. As outras metas do usuário são: {(others.Count == 0 ? "nenhuma" : string.Join("; ", others))}.
            Uma atividade que pertence a outra meta (ex.: ler, numa meta de peso) NÃO entra aqui: ignore-a.
            Para "{Decisions.Answer}" e "{Decisions.Change}": as rotinas do que o usuário disse (aplicando a correção
            sobre o plano mostrado). Complete o que faltar para a rotina fazer sentido e diga em assumptions.
            Para "{Decisions.Propose}": a sua proposta, pensada para atingir a meta no prazo.
            Para as outras decisões: lista vazia.
            assumptions: no máximo 2 frases curtas (até 8 palavras), dirigidas ao usuário, com o que você supôs e ele
            não disse (ex.: "10 páginas por dia terminam 2 livros com folga"). Elas aparecem depois de "Supus:",
            então não comece com "Assumi", "Suponho" ou "Supus". Lista vazia se não supôs nada.

            {GoalExtraction.GoalFieldRules}
            """;
    }

    /// <summary>The possible decisions, as plain text: what the AI writes in "decision".</summary>
    private static class Decisions
    {
        public const string Answer = "answer";
        public const string Propose = "propose";
        public const string Confirm = "confirm";
        public const string Change = "change";
        public const string Skip = "skip";
        public const string Greeting = "greeting";
        public const string GoalAction = "goal_action";
        public const string Other = "other";
    }

    internal sealed record DetailAnswer(
        string Decision,
        ExtractedGoal? Goal,
        List<ExtractedRoutine>? Routines,
        List<string>? Assumptions);
}

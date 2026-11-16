using System.Globalization;
using System.Text;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// The answer to a priorities review (Funcionalidade 5). First Pippin listens: the user tells what is in the way (kept
/// as the review's obstacle) and Pippin may propose ONE small, gradual adjustment of the goal in focus, or just welcome
/// it when it is temporary. Then the user says yes (the new plan is saved), no (the plan stays) or asks for a change.
/// The code guarantees the rule "não reduzir uma atividade bem-sucedida apenas para aumentar o espaço de outra": the
/// only plan this node ever writes is the focus goal's, and the protected goals are shown to the AI as off limits.
/// Anything else drops the review, without insisting.
/// </summary>
internal sealed class BalanceReviewNode(
    IChatClient chatClient,
    IBalanceService balance,
    IGoalService goals,
    TimeProvider timeProvider) : INode
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public const string ConfirmQuestion = "Quer testar assim? Responda **sim** para eu ajustar o plano, ou **não** para manter como está.";

    public Stage Stage => Stage.BalanceReview;

    public string? PendingQuestion(ConversationContext context) => context.Draft?.Question;

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        var draft = context.Draft ?? GoalsDraft.Empty(null);
        if (draft.BalanceReviewId is not { } reviewId
            || await balance.FindAsync(reviewId, context.UserId, cancellationToken)
                is not { Status: ReviewStatus.Offered or ReviewStatus.Explained } review
            || context.SavedGoals.FirstOrDefault(g => g.Id == review.FocusGoalId) is not { } goal)
        {
            return Lapse(context, draft);
        }

        var routines = await goals.ListRoutinesAsync(context.UserId, cancellationToken);
        var proposing = draft.Plan is not null;

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(context, review, goal, routines, draft.Plan)),
            new(ChatRole.User, context.IncomingText),
        ];
        var response = await chatClient.GetResponseAsync<BalanceReviewAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected;
        }

        switch (answer.Decision?.Trim().ToLowerInvariant())
        {
            // The user told what is in the way (or asked for a different adjustment): listen, and maybe propose.
            case "explains" or "adjust":
            {
                if (!proposing)
                {
                    await balance.AnswerAsync(reviewId, context.UserId, ReviewStatus.Explained, answer.Obstacle, cancellationToken);
                }

                var proposal = GoalPlan.ToRoutines(answer.Routines, RoutineSource.Pippin);
                var reply = Blank(answer.Reply);
                if (proposal.Count == 0)
                {
                    // Something temporary, or no change makes sense: welcome it and keep the plan.
                    return proposing ? NodeResult.NotExpected : Finish(context, draft, reply ?? "Entendi, obrigado por me contar! 💙");
                }

                context.Draft = draft with { Plan = new PlanDraft(proposal, RoutineSource.Pippin), Question = ConfirmQuestion };
                var text = new StringBuilder(reply ?? $"Que tal um passo menor em **{goal.Name}**?");
                PlanReview.AppendRoutines(text, proposal);
                text.Append("\n\n").Append(ConfirmQuestion);
                return NodeResult.Answer(text.ToString());
            }

            case "confirm" when draft.Plan is { } plan:
            {
                var today = GoalHorizon.Today(timeProvider);
                var dated = GoalPlan.WithDates(plan.Routines, goal.DueDate, today);
                if (await goals.ReplaceRoutinesAsync(goal.Id, context.UserId, dated, cancellationToken) is null)
                {
                    return Lapse(context, draft);
                }

                await balance.AnswerAsync(reviewId, context.UserId, ReviewStatus.Accepted, null, cancellationToken);
                return Finish(
                    context, draft,
                    $"Ajustei o plano de **{goal.Name}**! ✅ Vamos ver como ele se encaixa nas próximas semanas; se não funcionar, mudamos de novo.");
            }

            case "keep":
                await balance.AnswerAsync(reviewId, context.UserId, ReviewStatus.Declined, answer.Obstacle, cancellationToken);
                return Finish(
                    context, draft,
                    Blank(answer.Reply) ?? $"Tudo bem! Seguimos com o plano de **{goal.Name}** como está. Se quiser mudar depois, é só me falar.");

            case "greeting":
                return NodeResult.Greeting;

            default:
                // Not about the review: it lapses (still open in the database, never insisted on) and the message goes on.
                return Lapse(context, draft);
        }
    }

    private static NodeResult Finish(ConversationContext context, GoalsDraft draft, string reply)
    {
        context.Draft = Closed(draft);
        return NodeResult.Answer(reply, Stage.CollectGoals);
    }

    private static NodeResult Lapse(ConversationContext context, GoalsDraft draft)
    {
        context.Draft = Closed(draft);
        return NodeResult.NotExpectedAfter(null, Stage.CollectGoals);
    }

    private static GoalsDraft Closed(GoalsDraft draft) => draft with { BalanceReviewId = null, Plan = null, Question = null };

    private string Instructions(
        ConversationContext context,
        BalanceReviewDto review,
        GoalDto goal,
        IReadOnlyList<RoutineDto> routines,
        PlanDraft? proposal)
    {
        var today = GoalHorizon.Today(timeProvider);
        var current = routines.Where(r => r.GoalId == goal.Id).Select(r => GoalPlan.ToExtracted(GoalPlan.FromSaved(r)));
        var protectedHabits = context.SavedGoals
            .Where(g => review.ProtectedGoalIds.Contains(g.Id))
            .Select(g => new
            {
                meta = g.Name,
                rotinas = routines.Where(r => r.GoalId == g.Id).Select(r => GoalPlan.Describe(GoalPlan.FromSaved(r))),
            })
            .ToList();

        var phase = proposal is null
            ? """
              Classifique a resposta do usuário em decision:
              - "explains": ele contou o que está atrapalhando, ou como a meta se encaixa (ou não) na rotina dele.
              - "keep": não quer mudar nada agora ("tá tudo bem", "quero manter", "agora não", "deixa assim").
              - "greeting": é apenas um cumprimento ou conversa social.
              - "other": a mensagem é sobre outro assunto (outra meta, um progresso, uma pergunta).

              Para "explains":
              - obstacle: o que atrapalha, nas palavras dele, curto.
              - Se o motivo for passageiro (viagem, doença, uma semana atípica), apenas acolha e incentive a retomada:
                routines vazia.
              - Senão, proponha UM ajuste pequeno e gradual SÓ desta meta em routines (o plano completo dela): menos por
                vez, outro horário ou dia, uma sessão mais curta, ou ancorar num hábito que já funciona ("depois da
                academia, 5 páginas"). Comece por algo fácil de cumprir; dá para crescer depois.
              - reply: até 60 palavras: acolha o que ele contou, sem culpa; se propôs um ajuste, explique em uma frase o
                benefício e o risco (ex.: "vai um pouco mais devagar, mas fica mais fácil de manter"). Não termine com
                pergunta: a confirmação é acrescentada depois.
              """
            : $"""
              O Pippin propôs este ajuste (JSON) e perguntou se o usuário quer testar:
              {JsonSerializer.Serialize(proposal.Routines.Select(GoalPlan.ToExtracted), Json)}

              Classifique a resposta do usuário em decision:
              - "confirm": aceita ("sim", "bora", "pode ajustar", "fechado").
              - "adjust": quer o ajuste de outro jeito ("prefiro de manhã", "menos ainda"): routines com o plano completo
                desta meta adaptado ao que ele pediu, e reply com até 40 palavras explicando, sem terminar com pergunta.
              - "keep": prefere manter o plano atual ("não", "deixa como está").
              - "greeting": é apenas um cumprimento.
              - "other": a mensagem é sobre outro assunto.
              """;

        return $"""
            Você é o Pippin, o assistente do LifeAim: conversa em português, de forma leve, calorosa e natural, pelo
            Discord{(context.UserName is { } name ? $", com {name}" : "")}. Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.

            Você puxou uma conversa sobre as prioridades dele, porque a meta **{goal.Name}** está com dificuldade:
            "{review.Message}"

            Plano atual desta meta (JSON):
            {JsonSerializer.Serialize(current, Json)}
            Prazo da meta: {goal.DueDate?.ToString("yyyy-MM-dd") ?? "sem prazo"}.

            Hábitos que estão funcionando bem e NÃO podem ser reduzidos, movidos nem trocados (só podem servir de apoio):
            {(protectedHabits.Count == 0 ? "nenhum" : JsonSerializer.Serialize(protectedHabits, Json))}

            {phase}

            Regras: não presuma a causa; não compare percentuais entre metas nem tente igualá-los; a decisão é sempre
            dele. Use **negrito** no nome das metas, no máximo um emoji, sem títulos e sem listas.
            {GoalPlan.RoutineRules}
            """;
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    internal sealed record BalanceReviewAnswer(string? Decision, string? Obstacle, List<ExtractedRoutine>? Routines, string? Reply);
}

using System.Globalization;
using System.Text;
using System.Text.Json;
using LifeAim.Modules.Agents.Application.Planning;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;
using Microsoft.Extensions.AI;

namespace LifeAim.Modules.Agents.Application.Nodes;

/// <summary>
/// One goal of the proposal in focus (Funcionalidade 2, "Refinar em conversa"): Pippin showed his suggestion and a
/// contextual question. Each answer adapts the routines to the user's real pattern and shows them again, until the
/// user agrees (saved as the goal's plan through the Goals module), skips it, or talks about something else.
/// Unlike the detailing, ignoring the question does NOT save: this is a suggestion, not something the user said.
/// </summary>
internal sealed class RefinePlanGoalNode(
    IChatClient chatClient,
    PlanReview review,
    GoalDetailing detailing,
    IPlanningService planning,
    TimeProvider timeProvider) : INode
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    public Stage Stage => Stage.RefinePlanGoal;

    public string? PendingQuestion(ConversationContext context) => context.Draft?.Question;

    public async Task<NodeResult> RunAsync(ConversationContext context, CancellationToken cancellationToken)
    {
        if (context.Draft is not { ProposalId: { } proposalId, DetailQueue.Count: > 0, Goals.Count: > 0, Plan: { } plan } draft)
        {
            context.Draft = GoalsDraft.Empty(context.Draft?.Horizon);
            return NodeResult.NotExpectedAfter("Perdi o fio da proposta aqui. 😅", Stage.CollectGoals);
        }

        List<ChatMessage> messages =
        [
            new(ChatRole.System, Instructions(draft, plan, context.SavedGoals)),
            new(ChatRole.User, context.IncomingText),
        ];

        var response = await chatClient.GetResponseAsync<RefineAnswer>(messages, cancellationToken: cancellationToken);
        if (!response.TryGetResult(out var answer))
        {
            return NodeResult.NotExpected;
        }

        var goalId = draft.DetailQueue[0];
        var rest = draft.DetailQueue.Skip(1).ToList();
        var goal = draft.Goals[0];

        switch (answer.Decision?.Trim().ToLowerInvariant())
        {
            // The user told how they really do it, or asked for a change: the plan is adapted and shown again.
            case "adjust":
            {
                var routines = GoalPlan.ToRoutines(answer.Routines, RoutineSource.Pippin);
                if (routines.Count == 0)
                {
                    return NodeResult.NotExpected;
                }

                var userPlan = string.IsNullOrWhiteSpace(goal.UserPlan)
                    ? context.IncomingText.Trim()
                    : $"{goal.UserPlan}\n{context.IncomingText.Trim()}";
                context.Draft = draft with
                {
                    Goals = [goal with { UserPlan = userPlan }],
                    Plan = new PlanDraft(routines, RoutineSource.Pippin),
                    Question = PlanReview.ConfirmQuestion,
                };

                var text = new StringBuilder(string.IsNullOrWhiteSpace(answer.Reply) ? "Ajustei assim:" : answer.Reply.Trim());
                PlanReview.AppendRoutines(text, routines);
                text.Append("\n\n").Append(PlanReview.ConfirmQuestion);
                return NodeResult.Answer(text.ToString());
            }

            case "confirm":
                await detailing.SaveAsync(context.UserId, goalId, goal, plan, cancellationToken);
                await planning.SetGoalStatusAsync(proposalId, goalId, GoalProposalStatus.Accepted, cancellationToken);
                return await review.NextAsync(
                    context, proposalId, rest, $"Salvei o plano de **{goal.Name}**! ✅", cancellationToken);

            case "skip":
                await planning.SetGoalStatusAsync(proposalId, goalId, GoalProposalStatus.Skipped, cancellationToken);
                return await review.NextAsync(
                    context, proposalId, rest, $"Tudo bem, deixamos **{goal.Name}** como está por enquanto.", cancellationToken);

            case "greeting":
                return NodeResult.Greeting;

            default:
                return NodeResult.NotExpected; // → the path classifier, which repeats the question
        }
    }

    private string Instructions(GoalsDraft draft, PlanDraft plan, IReadOnlyList<GoalDto> savedGoals)
    {
        var today = GoalHorizon.Today(timeProvider);
        var goal = draft.Goals[0];
        var others = savedGoals.Where(g => g.Id != draft.DetailQueue![0]).Select(g => g.Name).ToList();

        return $"""
            Você é o Pippin, um assistente que conversa em português, de forma leve e natural, e ajuda o usuário a
            transformar metas em ações. Hoje é {today.ToString("dddd, dd/MM/yyyy", PtBr)}.

            Vocês estão refinando a proposta de plano desta meta (JSON):
            {JsonSerializer.Serialize(GoalExtraction.ToExtracted(goal), Json)}
            Rotinas propostas agora (JSON):
            {JsonSerializer.Serialize(plan.Routines.Select(GoalPlan.ToExtracted), Json)}
            O Pippin perguntou: "{draft.Question}"

            Classifique a resposta do usuário em decision:
            - "adjust": ele contou como costuma fazer (dias, períodos, quanto por vez) ou pediu uma mudança
              ("prefiro ler nos fins de semana", "só consigo à noite", "menos páginas").
            - "confirm": concorda com a proposta como está ("sim", "fechado", "gostei", "pode salvar").
            - "skip": quer deixar esta meta para depois ("pula", "depois vejo essa", "deixa como está").
            - "greeting": é apenas um cumprimento ou conversa social ("oi", "bom dia").
            - "other": a mensagem não é sobre o plano desta meta (outra meta, um progresso, uma pergunta).

            Para "adjust":
            - routines: as rotinas adaptadas ao padrão que ele contou, ainda pensadas para atingir a meta no prazo.
              Mantenha o que ele não pediu para mudar.
            - reply: até 40 palavras, dirigidas ao usuário, explicando a adaptação e o efeito no prazo (ex.: "Então
              fica assim: 5 páginas nos dias úteis e 20 no sábado. Dá o mesmo ritmo e combina mais com você."). Use
              **negrito** no nome da meta. Não termine com pergunta.
            Para as outras decisões: routines vazia e reply null.
            Só as rotinas DESTA meta; as outras metas do usuário são: {(others.Count == 0 ? "nenhuma" : string.Join("; ", others))}.
            {GoalPlan.RoutineRules}
            """;
    }

    internal sealed record RefineAnswer(string? Decision, List<ExtractedRoutine>? Routines, string? Reply);
}

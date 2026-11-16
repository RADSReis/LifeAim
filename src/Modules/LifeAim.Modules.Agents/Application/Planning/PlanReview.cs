using System.Text;
using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Agents.Application.Workflow;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Planning.Contracts;

namespace LifeAim.Modules.Agents.Application.Planning;

/// <summary>
/// Going through the plan proposal with the user (Funcionalidade 2, "Apresentação e refinamento progressivo"),
/// shared by its three nodes: list up to 3 goals (→ ChoosePlanGoal), put one goal in focus with Pippin's suggestion
/// before the question (→ RefinePlanGoal), move on to the next chosen goal or back to the list, and, when every
/// goal was talked about, the overview of the plan (UX item 6) back at CollectGoals.
/// </summary>
internal sealed class PlanReview(IPlanningService planning, IGoalService goals)
{
    /// <summary>How many goals Pippin lists per round, even with 10 saved (no cognitive overload).</summary>
    public const int GoalsPerRound = 3;

    /// <summary>The invitation Pippin sends on his own when the proposal is ready (Notion's words).</summary>
    public const string Invitation =
        "Ei! Preparei uma primeira proposta para ajudar você a avançar nas suas metas. 🗺️ Levei em consideração " +
        "seus objetivos e o tempo que você tem disponível. Quer dar uma olhada no que pensei? Podemos ajustar tudo " +
        "juntos até ficar de acordo com sua rotina.";

    public const string OfferQuestion = "Quer dar uma olhada na proposta que preparei?";

    public const string ConfirmQuestion = "Fica bom assim? Responda **sim** para salvar, ou me diga o que ajustar.";

    public Task<PlanProposalDto?> OpenProposalAsync(Guid userId, CancellationToken cancellationToken) =>
        planning.GetOpenAsync(userId, cancellationToken);

    /// <summary>
    /// The open goals of the proposal that are still saved goals, in the proposal's order, with their goal.
    /// A goal cancelled meanwhile just drops out.
    /// </summary>
    public static IReadOnlyList<(GoalProposalDto Proposal, GoalDto Goal)> OpenGoals(
        PlanProposalDto proposal, IReadOnlyList<GoalDto> saved) =>
        proposal.Goals
            .Where(g => g.Status == GoalProposalStatus.Open)
            .Select(g => (Proposal: g, Goal: saved.FirstOrDefault(s => s.Id == g.GoalId)))
            .Where(pair => pair.Goal is not null)
            .Select(pair => (pair.Proposal, pair.Goal!))
            .ToList();

    /// <summary>
    /// Lists the next (up to 3) goals to talk about (→ ChoosePlanGoal); with none left, the overview (→ CollectGoals).
    /// </summary>
    public async Task<NodeResult> ShowChoicesAsync(
        ConversationContext context, PlanProposalDto proposal, string prefix, CancellationToken cancellationToken)
    {
        var saved = await goals.ListAsync(context.UserId, cancellationToken);
        var open = OpenGoals(proposal, saved);
        if (open.Count == 0)
        {
            return await FinishAsync(context, proposal, prefix, saved, cancellationToken);
        }

        var shown = open.Take(GoalsPerRound).Select(pair => pair.Goal).ToList();
        context.Draft = GoalsDraft.Empty(context.Draft?.Horizon) with
        {
            ProposalId = proposal.Id,
            Shown = shown.Select(g => g.Id).ToList(),
        };

        var text = new StringBuilder(prefix).Append("\n\n").Append(Choices(shown));
        return NodeResult.Answer(text.ToString(), Stage.ChoosePlanGoal);
    }

    /// <summary>"- Ler 2 livros\n- Ir à academia\n\nQuer conversar sobre alguma dessas ou prefere começar por outra meta?"</summary>
    public static string Choices(IReadOnlyList<GoalDto> shown)
    {
        var text = new StringBuilder();
        foreach (var goal in shown)
        {
            text.Append("- **").Append(goal.Name).Append("**\n");
        }

        text.Append('\n').Append(shown.Count switch
        {
            1 => "Vamos conversar sobre ela ou prefere outra meta?",
            2 => "Quer conversar sobre alguma dessas duas ou prefere começar por outra meta?",
            _ => "Quer conversar sobre alguma dessas três ou prefere começar por outra meta?",
        });
        return text.ToString();
    }

    /// <summary>
    /// Puts the first goal of <paramref name="queue"/> in focus: Pippin's suggestion, its routines and the contextual
    /// question (→ RefinePlanGoal). The rest of the queue are the other goals the user chose, for the next rounds.
    /// </summary>
    public async Task<NodeResult> StartGoalAsync(
        ConversationContext context,
        PlanProposalDto proposal,
        IReadOnlyList<Guid> queue,
        string prefix,
        CancellationToken cancellationToken)
    {
        var saved = await goals.ListAsync(context.UserId, cancellationToken);
        var open = OpenGoals(proposal, saved);
        var remaining = queue.Where(id => open.Any(pair => pair.Goal.Id == id)).Distinct().ToList();
        if (remaining.Count == 0)
        {
            return await ShowChoicesAsync(context, proposal, prefix, cancellationToken);
        }

        var (item, goal) = open.First(pair => pair.Goal.Id == remaining[0]);
        context.Draft = new GoalsDraft(
            context.Draft?.Horizon,
            [GoalExtraction.FromSaved(goal)],
            [],
            remaining,
            Question: item.Question,
            Plan: new PlanDraft(item.Routines, RoutineSource.Pippin),
            ProposalId: proposal.Id);

        var text = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(prefix))
        {
            text.Append(prefix).Append("\n\n");
        }

        text.Append(item.Message);
        AppendRoutines(text, item.Routines);
        text.Append("\n\n").Append(item.Question);
        return NodeResult.Answer(text.ToString(), Stage.RefinePlanGoal);
    }

    /// <summary>After one goal: the next chosen goal, or the list again with what is left.</summary>
    public async Task<NodeResult> NextAsync(
        ConversationContext context, Guid proposalId, IReadOnlyList<Guid> rest, string prefix, CancellationToken cancellationToken)
    {
        var proposal = await planning.GetOpenAsync(context.UserId, cancellationToken);
        if (proposal is null || proposal.Id != proposalId)
        {
            context.Draft = GoalsDraft.Empty(context.Draft?.Horizon);
            return NodeResult.Answer(prefix, Stage.CollectGoals);
        }

        return rest.Count > 0
            ? await StartGoalAsync(context, proposal, rest, $"{prefix}\n\nAgora vamos para a próxima.", cancellationToken)
            : await ShowChoicesAsync(context, proposal, $"{prefix}\n\nVamos para outra meta?", cancellationToken);
    }

    /// <summary>"- **Rotina:** ler — todo dia · à noite · 10 páginas", one line per routine.</summary>
    public static void AppendRoutines(StringBuilder text, IReadOnlyList<RoutineInput> routines)
    {
        if (routines.Count == 0)
        {
            return;
        }

        text.Append('\n');
        foreach (var routine in routines)
        {
            text.Append("\n- **Rotina:** ").Append(GoalPlan.Describe(routine));
        }
    }

    // Every goal was talked about: the proposal closes and Pippin shows the plan as it is now (UX item 6).
    private async Task<NodeResult> FinishAsync(
        ConversationContext context,
        PlanProposalDto proposal,
        string prefix,
        IReadOnlyList<GoalDto> saved,
        CancellationToken cancellationToken)
    {
        await planning.CloseAsync(proposal.Id, cancellationToken);
        var routines = await goals.ListRoutinesAsync(context.UserId, cancellationToken);
        context.Draft = GoalsDraft.Empty(context.Draft?.Horizon);

        var text = new StringBuilder(prefix).Append("\n\nPronto, passamos por todas as metas! 🎉\n\n**Seu planejamento:**");
        foreach (var goal in saved)
        {
            text.Append("\n\n**").Append(goal.Name).Append("**");
            var ofGoal = routines.Where(r => r.GoalId == goal.Id).ToList();
            if (ofGoal.Count == 0)
            {
                text.Append("\n- **Rotina:** a combinar");
            }

            foreach (var routine in ofGoal)
            {
                text.Append("\n- **Rotina:** ").Append(GoalPlan.Describe(ToInput(routine)));
            }
        }

        text.Append("\n\nSe quiser consultar ou mudar alguma coisa, é só me falar.");
        return NodeResult.Answer(text.ToString(), Stage.CollectGoals);
    }

    private static RoutineInput ToInput(RoutineDto r) =>
        new(r.Activity, r.Frequency, r.Source, r.Interval, r.DaysOfWeek, r.TimesPerPeriod, r.TimeOfDay, r.DayPeriod,
            r.Amount, r.Unit, r.DurationMinutes, r.StartsOn, r.EndsOn);
}

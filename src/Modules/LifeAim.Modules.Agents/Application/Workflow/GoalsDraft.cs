using LifeAim.Modules.Goals.Contracts;

namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// What Pippin understood about the user's goals and is waiting for them to confirm. Part of the
/// conversation state: it survives between messages (agents.conversations.draft) until it is saved.
/// </summary>
/// <param name="Horizon">The end of the period the user chose ("fim do ano" → 31/12), once known.</param>
/// <param name="Goals">The goals not confirmed yet.</param>
/// <param name="Doubts">What was ambiguous, asked as a choice; nothing is saved while one is open.</param>
/// <param name="DetailQueue">
/// While detailing (Stage.DetailGoals): the ids of the saved goals still to detail, the first one in focus.
/// <see cref="Goals"/> then holds that single goal, with what the user added in this conversation.
/// </param>
/// <param name="Assumptions">What Pippin assumed in the plan it shows ("10 páginas por dia").</param>
/// <param name="Question">While detailing: the one question Pippin asked about the goal in focus.</param>
/// <param name="Plan">While detailing: the plan Pippin understood (or proposed), waiting for confirmation.</param>
internal sealed record GoalsDraft(
    DateOnly? Horizon,
    IReadOnlyList<GoalDraft> Goals,
    IReadOnlyList<GoalDoubt> Doubts,
    IReadOnlyList<Guid>? DetailQueue = null,
    IReadOnlyList<string>? Assumptions = null,
    string? Question = null,
    PlanDraft? Plan = null)
{
    public static GoalsDraft Empty(DateOnly? horizon) => new(horizon, [], []);
}

/// <summary>
/// Something ambiguous, asked as a choice between 2 or 3 options ("x or y"), never as a yes/no question:
/// a letter answers it without room for interpretation, for the user and for the AI.
/// </summary>
internal sealed record GoalDoubt(string Question, IReadOnlyList<string> Options);

/// <summary>One goal as understood from the conversation; becomes a CreateGoalRequest when confirmed.</summary>
/// <param name="UserPlan">How the user said they will reach it, in their own words.</param>
internal sealed record GoalDraft(
    string Name,
    string? Description,
    string? Indicator,
    decimal? StartValue,
    decimal? TargetValue,
    string? Unit,
    DateOnly? DueDate,
    string? UserPlan = null);

/// <summary>
/// The plan of the goal in focus, broken into routines (goals.goal_routines), as Pippin understood it from
/// the user's answer or proposed it himself. Saved only after the user confirms (or ignores the confirmation).
/// </summary>
internal sealed record PlanDraft(IReadOnlyList<RoutineInput> Routines, RoutineSource Source);

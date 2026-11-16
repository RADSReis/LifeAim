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
/// <param name="CancelGoalId">While confirming a cancellation (Stage.ConfirmCancelGoal): the saved goal to cancel.</param>
/// <param name="ReturnStage">
/// Where the conversation was when the cancellation was asked: it goes back there afterwards, with the rest of
/// this draft (a detailing in progress) untouched.
/// </param>
/// <param name="ProposeAfter">
/// The detailing started right after saving new goals: when it ends, Pippin asks for the plan proposal in the
/// background (Funcionalidade 2). Not when the user only came back to detail one goal.
/// </param>
/// <param name="ProposalId">While going through the plan proposal (Funcionalidade 2): which one.</param>
/// <param name="Shown">While choosing a goal of the proposal: the (up to 3) goals Pippin listed, in order.</param>
/// <param name="CheckIns">During the daily follow-up (Stage.CheckIn): the activities Pippin asked about, still unanswered.</param>
internal sealed record GoalsDraft(
    DateOnly? Horizon,
    IReadOnlyList<GoalDraft> Goals,
    IReadOnlyList<GoalDoubt> Doubts,
    IReadOnlyList<Guid>? DetailQueue = null,
    IReadOnlyList<string>? Assumptions = null,
    string? Question = null,
    PlanDraft? Plan = null,
    Guid? CancelGoalId = null,
    Stage? ReturnStage = null,
    bool ProposeAfter = false,
    Guid? ProposalId = null,
    IReadOnlyList<Guid>? Shown = null,
    IReadOnlyList<CheckInItem>? CheckIns = null)
{
    public static GoalsDraft Empty(DateOnly? horizon) => new(horizon, [], []);
}

/// <summary>One activity Pippin asked about in the daily follow-up (a row of progress.check_ins).</summary>
/// <param name="Day">The day it was planned for ("ontem" when asked the next day).</param>
/// <param name="Amount">How much was planned per session (10 "páginas"), to compare with what the user did.</param>
internal sealed record CheckInItem(
    Guid CheckInId,
    Guid GoalId,
    string GoalName,
    string Activity,
    DateOnly Day,
    decimal? Amount,
    string? Unit);

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

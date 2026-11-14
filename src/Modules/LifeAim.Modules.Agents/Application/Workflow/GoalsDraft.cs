namespace LifeAim.Modules.Agents.Application.Workflow;

/// <summary>
/// What Pippin understood about the user's goals and is waiting for them to confirm. Part of the
/// conversation state: it survives between messages (agents.conversations.draft) until it is saved.
/// </summary>
/// <param name="Horizon">The end of the period the user chose ("fim do ano" → 31/12), once known.</param>
/// <param name="Goals">The goals not confirmed yet.</param>
/// <param name="Doubts">What was ambiguous, asked as a choice; nothing is saved while one is open.</param>
internal sealed record GoalsDraft(DateOnly? Horizon, IReadOnlyList<GoalDraft> Goals, IReadOnlyList<GoalDoubt> Doubts)
{
    public static GoalsDraft Empty(DateOnly? horizon) => new(horizon, [], []);
}

/// <summary>
/// Something ambiguous, asked as a choice between 2 or 3 options ("x or y"), never as a yes/no question:
/// a letter answers it without room for interpretation, for the user and for the AI.
/// </summary>
internal sealed record GoalDoubt(string Question, IReadOnlyList<string> Options);

/// <summary>One goal as understood from the conversation; becomes a CreateGoalRequest when confirmed.</summary>
internal sealed record GoalDraft(
    string Name,
    string? Description,
    string? Indicator,
    decimal? StartValue,
    decimal? TargetValue,
    string? Unit,
    DateOnly? DueDate);

using LifeAim.Modules.Goals.Contracts;

namespace LifeAim.Modules.Goals.Domain;

/// <summary>
/// Funcionalidade 6, "Pausar, abandonar ou substituir metas", in plain code: which status a goal may go to from
/// which, and what each change needs. None of them is a failure and none deletes anything: the goal's row, plan and
/// progress stay, and every change is written to goals.goal_status_changes, so the path can always be told.
/// </summary>
internal static class GoalLifecycle
{
    public const int MaxReasonLength = 500;

    /// <summary>A paused goal may be paused again: that only changes the day it comes back.</summary>
    public static readonly IReadOnlyList<GoalStatus> CanPause = [GoalStatus.Active, GoalStatus.Paused];

    public static readonly IReadOnlyList<GoalStatus> CanResume = [GoalStatus.Paused];

    /// <summary>A paused goal can also be ended or replaced, without coming back first.</summary>
    public static readonly IReadOnlyList<GoalStatus> CanCancel = [GoalStatus.Active, GoalStatus.Paused];

    public static readonly IReadOnlyList<GoalStatus> CanReplace = [GoalStatus.Active, GoalStatus.Paused];

    /// <summary>Cancelled and replaced goals are closed: their history is kept, but they never change again.</summary>
    public static bool IsClosed(GoalStatus status) => status is GoalStatus.Cancelled or GoalStatus.Replaced;

    /// <summary>
    /// The day a pause ends is optional ("vou pausar por um tempo"), but when given it is not in the past.
    /// <paramref name="today"/> is the server's UTC day, so "amanhã" said late at night in Brazil is still accepted.
    /// </summary>
    public static DateOnly? ValidResumeOn(DateOnly? resumeOn, DateOnly today)
    {
        if (resumeOn is { } day && day < today)
        {
            throw new ArgumentException("A pause ends on a day that has not passed yet.", nameof(resumeOn));
        }

        return resumeOn;
    }

    /// <summary>Why the user changed the goal, in their words; optional.</summary>
    public static string? ValidReason(string? reason)
    {
        var trimmed = Goal.Blank(reason);
        return trimmed?.Length > MaxReasonLength ? trimmed[..MaxReasonLength] : trimmed;
    }

    public static string ToText(GoalStatus status) => status.ToString().ToLowerInvariant();

    public static GoalStatus Parse(string text) => Enum.Parse<GoalStatus>(text, ignoreCase: true);
}

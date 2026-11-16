namespace LifeAim.Modules.Progress.Contracts;

/// <summary>
/// The daily follow-up's record (Funcionalidade 3): each planned activity Pippin asked about, and the answer.
/// Done, not done and not confirmed (still <see cref="CheckInStatus.Asked"/> after its day) are different things:
/// a check-in nobody answered is never counted as a failure. Like <see cref="IProgressService"/>, the module does not
/// know Goals: the caller checks that the goal and the routine are the user's.
/// </summary>
public interface ICheckInService
{
    /// <summary>The user's check-ins from <paramref name="from"/> to <paramref name="to"/> (inclusive), oldest day first.</summary>
    Task<IReadOnlyList<CheckInDto>> ListAsync(
        Guid userId,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Pippin asked about this activity of this day: a new check-in, or one more attempt on the one still waiting for an
    /// answer. An activity already answered stays as it is. Returns the check-in as it is now.
    /// </summary>
    Task<CheckInDto> MarkAskedAsync(AskCheckInRequest request, CancellationToken cancellationToken = default);

    /// <summary>The user did it (with the progress entry that recorded it). False when it is not the user's open check-in.</summary>
    Task<bool> MarkDoneAsync(
        Guid checkInId,
        Guid userId,
        Guid? progressEntryId,
        CancellationToken cancellationToken = default);

    /// <summary>The user said it did not happen, and why (when they said). False when it is not the user's open check-in.</summary>
    Task<bool> MarkMissedAsync(
        Guid checkInId,
        Guid userId,
        string? reason,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Progress of the goal was recorded for that day some other way ("hoje li 10 páginas" out of the blue): the
    /// check-ins of that goal and day still waiting are answered as done. Returns how many.
    /// </summary>
    Task<int> MarkDoneForGoalAsync(
        Guid userId,
        Guid goalId,
        DateOnly day,
        Guid? progressEntryId,
        CancellationToken cancellationToken = default);
}

public enum CheckInStatus
{
    /// <summary>Waiting for an answer; after its day it means "não confirmado".</summary>
    Asked,
    Done,
    Missed,
}

/// <param name="RoutineId">The goal's routine that planned it (goals.goal_routines).</param>
/// <param name="Day">The day the activity was planned for, in the user's calendar.</param>
/// <param name="Activity">What was planned ("ler"), kept even if the plan changes later.</param>
public sealed record AskCheckInRequest(Guid UserId, Guid GoalId, Guid RoutineId, DateOnly Day, string Activity);

public sealed record CheckInDto(
    Guid Id,
    Guid UserId,
    Guid GoalId,
    Guid RoutineId,
    DateOnly Day,
    string Activity,
    CheckInStatus Status,
    int TimesAsked,
    DateTimeOffset FirstAskedAt,
    DateTimeOffset LastAskedAt,
    DateTimeOffset? AnsweredAt,
    string? Reason);

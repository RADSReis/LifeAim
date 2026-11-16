using LifeAim.Modules.Agents.Application.Nodes;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;

namespace LifeAim.Modules.Agents.Application.FollowUp;

/// <summary>
/// When the daily follow-up (Funcionalidade 3) sends a message, and about what. Plain code, no AI: the AI only writes
/// the words (<see cref="CheckInWriter"/>). The rules, from the Notion page:
/// - An activity is asked about when its moment arrives: the exact time the user said, or the start of the period of
///   the day ("de manhã" → 9h), never an invented exact time. Not between 22h and 9h, unless the user said that time.
/// - Not when the user already reported progress of that goal today ("hoje li 10 páginas").
/// - No answer is not a failure: an activity still unanswered is asked once more later that day (another period),
///   joined to another activity's message when there is one, and the next time that goal is planned ("ontem não
///   consegui confirmar…").
/// </summary>
internal static class FollowUpPlanner
{
    /// <summary>At most this many messages about the same activity on its own day (the first and one more attempt).</summary>
    public const int MaxAsksPerDay = 2;

    /// <summary>How long Pippin waits for an answer before asking again on his own, in another period of the day.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(4);

    /// <summary>How long before an unanswered activity can ride along in the message of another one.</summary>
    public static readonly TimeSpan JoinAfter = TimeSpan.FromHours(2);

    /// <summary>An activity at an exact time is asked about up to this long after it (it may be outside 9h–22h).</summary>
    public static readonly TimeSpan ExactTimeWindow = TimeSpan.FromHours(3);

    /// <summary>How many days back an unconfirmed activity is still asked about, the next time its goal is planned.</summary>
    public const int EarlierDays = 3;

    /// <summary>The most times one activity is asked about in total (its own day and the next ones).</summary>
    public const int MaxAsks = 3;

    /// <summary>When an activity is asked about if the user gave no exact time (UX: periods, not exact times).</summary>
    public static TimeOnly SendTime(RoutineDto routine) =>
        routine.TimeOfDay ?? routine.DayPeriod switch
        {
            DayPeriod.Morning => new TimeOnly(9, 0),
            DayPeriod.Afternoon => new TimeOnly(14, 0),
            DayPeriod.Evening => new TimeOnly(19, 0),
            DayPeriod.Night => new TimeOnly(21, 0),
            _ => new TimeOnly(10, 0),
        };

    /// <summary>
    /// Whether the routine plans its activity on <paramref name="day"/>. Fixed days follow the calendar; "3 vezes por
    /// semana" (no fixed days) is asked every day until the week has that many days done (<paramref name="doneDays"/>).
    /// </summary>
    public static bool IsPlannedOn(RoutineDto routine, DateOnly day, DateOnly start, IReadOnlySet<DateOnly> doneDays)
    {
        if (day < start || (routine.EndsOn is { } ends && day > ends))
        {
            return false;
        }

        var interval = Math.Max(1, routine.Interval);
        switch (routine.Frequency)
        {
            case RoutineFrequency.Daily:
                return (day.DayNumber - start.DayNumber) % interval == 0;

            case RoutineFrequency.Weekly:
                var weekStart = Monday(day);
                if ((weekStart.DayNumber - Monday(start).DayNumber) / 7 % interval != 0)
                {
                    return false;
                }

                return routine.DaysOfWeek.Count > 0
                    ? routine.DaysOfWeek.Contains(IsoDay(day))
                    : DoneBetween(doneDays, weekStart, day) < (routine.TimesPerPeriod ?? 1);

            case RoutineFrequency.Monthly:
                var months = (day.Year * 12 + day.Month) - (start.Year * 12 + start.Month);
                if (months % interval != 0)
                {
                    return false;
                }

                if (routine.TimesPerPeriod is not { } times)
                {
                    return day.Day == Math.Min(start.Day, DateTime.DaysInMonth(day.Year, day.Month));
                }

                // "2 vezes por mês" without fixed days: asked once a week, while the month still misses some.
                return (day.DayNumber - start.DayNumber) % 7 == 0
                       && DoneBetween(doneDays, new DateOnly(day.Year, day.Month, 1), day) < times;

            default:
                return false;
        }
    }

    /// <summary>What to send now, or null when there is nothing to ask.</summary>
    public static FollowUpPlan? Decide(FollowUpInput input)
    {
        var now = GoalHorizon.Local(input.UtcNow);
        var today = DateOnly.FromDateTime(now);
        var quiet = GoalHorizon.IsQuietTime(now);
        var activeGoals = input.Goals.ToDictionary(g => g.Id);

        var due = new List<DueActivity>();
        foreach (var routine in input.Routines)
        {
            if (!activeGoals.TryGetValue(routine.GoalId, out var goal))
            {
                continue;
            }

            var doneDays = DoneDays(input, goal.Id);
            var start = routine.StartsOn ?? DateOnly.FromDateTime(GoalHorizon.Local(routine.CreatedAt));
            if (!IsPlannedOn(routine, today, start, doneDays)
                || doneDays.Contains(today)
                || input.CheckIns.Any(c => c.RoutineId == routine.Id && c.Day == today))
            {
                continue;
            }

            var sendAt = today.ToDateTime(SendTime(routine));
            var inTime = routine.TimeOfDay is not null
                ? now >= sendAt && now < sendAt + ExactTimeWindow
                : now >= sendAt && !quiet;
            if (inTime)
            {
                due.Add(new DueActivity(routine, goal));
            }
        }

        // Unanswered today: on their own after a while, or joined to a new activity's message (never in quiet hours).
        var openToday = quiet
            ? []
            : input.CheckIns
                .Where(c => c.Day == today && c.Status == CheckInStatus.Asked && c.TimesAsked < MaxAsksPerDay)
                .Where(c => activeGoals.ContainsKey(c.GoalId) && !DoneDays(input, c.GoalId).Contains(today))
                .ToList();
        var retry = openToday.Where(c => input.UtcNow - c.LastAskedAt >= RetryAfter).ToList();
        var joined = openToday.Where(c => input.UtcNow - c.LastAskedAt >= JoinAfter).ToList();

        if (due.Count == 0 && retry.Count == 0)
        {
            return null;
        }

        var pendingToday = due.Count > 0 ? joined : retry;

        // The goals planned now that still have an earlier day unconfirmed: the latest one, asked once more.
        var dueGoals = due.Select(d => d.Goal.Id).ToHashSet();
        var earlier = input.CheckIns
            .Where(c => dueGoals.Contains(c.GoalId) && c.Status == CheckInStatus.Asked && c.TimesAsked < MaxAsks)
            .Where(c => c.Day < today && c.Day >= today.AddDays(-EarlierDays) && !DoneDays(input, c.GoalId).Contains(c.Day))
            .GroupBy(c => c.GoalId)
            .Select(g => g.MaxBy(c => c.Day)!)
            .ToList();

        return new FollowUpPlan(today, due, pendingToday, earlier);
    }

    /// <summary>The days the goal had something done: a progress entry or a check-in answered as done.</summary>
    public static IReadOnlySet<DateOnly> DoneDays(FollowUpInput input, Guid goalId) =>
        input.Progress.Where(p => p.GoalId == goalId).Select(p => p.OccurredOn)
            .Concat(input.CheckIns.Where(c => c.GoalId == goalId && c.Status == CheckInStatus.Done).Select(c => c.Day))
            .ToHashSet();

    public static DateOnly Monday(DateOnly day) => day.AddDays(-(IsoDay(day) - 1));

    public static int IsoDay(DateOnly day) => day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;

    private static int DoneBetween(IReadOnlySet<DateOnly> doneDays, DateOnly from, DateOnly before) =>
        doneDays.Count(d => d >= from && d < before);
}

/// <summary>Everything the planner looks at for one user.</summary>
/// <param name="CheckIns">The user's check-ins of the last weeks.</param>
/// <param name="Progress">The recent progress entries of the user's goals.</param>
internal sealed record FollowUpInput(
    DateTimeOffset UtcNow,
    IReadOnlyList<GoalDto> Goals,
    IReadOnlyList<RoutineDto> Routines,
    IReadOnlyList<CheckInDto> CheckIns,
    IReadOnlyList<ProgressEntryDto> Progress);

internal sealed record DueActivity(RoutineDto Routine, GoalDto Goal);

/// <param name="Due">Activities whose moment is now: the reason for the message.</param>
/// <param name="PendingToday">Activities asked earlier today and still unanswered.</param>
/// <param name="Earlier">Activities of earlier days still unconfirmed, of the goals planned now.</param>
internal sealed record FollowUpPlan(
    DateOnly Today,
    IReadOnlyList<DueActivity> Due,
    IReadOnlyList<CheckInDto> PendingToday,
    IReadOnlyList<CheckInDto> Earlier);

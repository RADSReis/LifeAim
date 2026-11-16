using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;

namespace LifeAim.Modules.Reporting.Application;

/// <summary>
/// Counts a period, in plain code. The rule from the Notion page: "o percentual precisa considerar apenas atividades
/// cujo resultado seja conhecido". A day counts once per goal: done if anything was done (progress or the follow-up
/// answered "sim"), else missed if the user said it did not happen, else unconfirmed if Pippin asked and nobody answered.
/// Planned days nobody asked about count nowhere.
/// </summary>
internal static class ReportCalculator
{
    public static PeriodStatsDto Period(
        DateOnly from,
        DateOnly to,
        IReadOnlyList<GoalDto> goals,
        IReadOnlyList<RoutineDto> routines,
        IReadOnlyList<ProgressEntryDto> progress,
        IReadOnlyList<CheckInDto> checkIns)
    {
        var inProgress = progress.Where(p => p.OccurredOn >= from && p.OccurredOn <= to).ToList();
        var inCheckIns = checkIns.Where(c => c.Day >= from && c.Day <= to).ToList();

        var stats = goals
            .Where(g => routines.Any(r => r.GoalId == g.Id)
                        || inProgress.Any(p => p.GoalId == g.Id)
                        || inCheckIns.Any(c => c.GoalId == g.Id))
            .Select(g => Goal(g, inProgress.Where(p => p.GoalId == g.Id).ToList(), inCheckIns.Where(c => c.GoalId == g.Id).ToList()))
            .ToList();

        var done = stats.Sum(g => g.Done);
        var missed = stats.Sum(g => g.Missed);
        return new PeriodStatsDto(from, to, done, missed, stats.Sum(g => g.Unconfirmed), Percent(done, missed), stats);
    }

    private static GoalStatsDto Goal(GoalDto goal, IReadOnlyList<ProgressEntryDto> progress, IReadOnlyList<CheckInDto> checkIns)
    {
        var doneDays = progress.Select(p => p.OccurredOn)
            .Concat(checkIns.Where(c => c.Status == CheckInStatus.Done).Select(c => c.Day))
            .ToHashSet();
        var missedDays = checkIns.Where(c => c.Status == CheckInStatus.Missed && !doneDays.Contains(c.Day))
            .Select(c => c.Day).ToHashSet();
        var unconfirmedDays = checkIns
            .Where(c => c.Status == CheckInStatus.Asked && !doneDays.Contains(c.Day) && !missedDays.Contains(c.Day))
            .Select(c => c.Day).ToHashSet();

        var withAmount = progress.Where(p => p.Amount is not null).ToList();
        var totals = withAmount
            .GroupBy(p => p.Unit ?? "")
            .Select(g => new AmountTotalDto(g.Key, g.Sum(p => p.Amount!.Value)))
            .OrderByDescending(t => t.Total)
            .ToList();
        var mainUnit = withAmount.GroupBy(p => p.Unit ?? "").OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;

        var byWeekday = Enumerable.Range(1, 7)
            .Select(day => new WeekdayStatsDto(
                day,
                doneDays.Count(d => IsoDay(d) == day),
                withAmount.Where(p => IsoDay(p.OccurredOn) == day && (p.Unit ?? "") == mainUnit).Sum(p => p.Amount!.Value)))
            .ToList();

        return new GoalStatsDto(
            goal.Id, goal.Name, doneDays.Count, missedDays.Count, unconfirmedDays.Count,
            Percent(doneDays.Count, missedDays.Count), totals, string.IsNullOrEmpty(mainUnit) ? null : mainUnit, byWeekday,
            goal.TargetValue, goal.Unit, goal.DueDate);
    }

    public static int? Percent(int done, int missed) =>
        done + missed == 0 ? null : (int)Math.Round(done * 100m / (done + missed), MidpointRounding.AwayFromZero);

    public static DateOnly FirstDay(DateOnly day) => new(day.Year, day.Month, 1);

    public static DateOnly LastDay(DateOnly day) => FirstDay(day).AddMonths(1).AddDays(-1);

    public static bool ClosesQuarter(DateOnly month) => month.Month % 3 == 0;

    private static int IsoDay(DateOnly day) => day.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)day.DayOfWeek;
}

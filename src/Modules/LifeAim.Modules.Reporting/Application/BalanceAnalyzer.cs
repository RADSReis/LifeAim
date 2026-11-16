using System.Globalization;
using System.Text;
using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using static LifeAim.Modules.Reporting.Contracts.BalanceRules;

namespace LifeAim.Modules.Reporting.Application;

/// <summary>
/// Funcionalidade 5 in plain code: which goals are working, which have a recurring difficulty, and whether the history
/// shows a conflict between them. "Esses sinais são indícios, não diagnósticos": an isolated bad day never counts, a
/// day nobody answered is never a miss (the days are counted by <see cref="ReportCalculator"/>), and a goal that is
/// working is never the one to change. The percentages are not compared with each other: different numbers are natural.
/// </summary>
internal static class BalanceAnalyzer
{
    /// <summary>Fewer observed days than this and a pace is not worth projecting (one good day is not a pace).</summary>
    public const int MinDaysForPace = 7;

    // Words people use when there was no time or no energy ("não deu tempo", "tava cansado", "trabalhei até tarde").
    private static readonly string[] TimeOrEnergyWords =
    [
        "tempo", "corrid", "ocupad", "agenda", "cansad", "cansac", "energia", "exaust", "esgotad", "sono", "pique", "trabalh",
    ];

    public static BalanceDto Analyze(
        DateOnly today,
        IReadOnlyList<GoalDto> goals,
        IReadOnlyList<RoutineDto> routines,
        IReadOnlyList<ProgressEntryDto> progress,
        IReadOnlyList<CheckInDto> checkIns)
    {
        var from = today.AddDays(-(WindowDays - 1));
        var recentFrom = today.AddDays(-(WindowDays / 2 - 1));

        var window = ReportCalculator.Period(from, today, goals, routines, progress, checkIns);
        var recent = ReportCalculator.Period(recentFrom, today, goals, routines, progress, checkIns);
        var earlier = ReportCalculator.Period(from, recentFrom.AddDays(-1), goals, routines, progress, checkIns);
        var missedInWindow = checkIns.Where(c => c.Day >= from && c.Day <= today && c.Status == CheckInStatus.Missed).ToList();

        var result = window.Goals.Select(stats =>
        {
            var goal = goals.First(g => g.Id == stats.GoalId);
            var entries = progress.Where(p => p.GoalId == goal.Id && p.OccurredOn <= today).ToList();
            var (mainUnit, total) = MainTotal(entries);

            return new GoalBalanceDto(
                goal.Id,
                goal.Name,
                stats.Done,
                stats.Missed,
                stats.Unconfirmed,
                stats.DonePercent,
                recent.Goals.FirstOrDefault(g => g.GoalId == goal.Id)?.DonePercent,
                earlier.Goals.FirstOrDefault(g => g.GoalId == goal.Id)?.DonePercent,
                Health(stats),
                missedInWindow.Where(c => c.GoalId == goal.Id && !string.IsNullOrWhiteSpace(c.Reason))
                    .OrderByDescending(c => c.Day).Select(c => c.Reason!.Trim()).ToList(),
                mainUnit,
                total,
                Project(goal, entries, from, today));
        }).ToList();

        var signals = Signals(result, recent, earlier, missedInWindow);
        return new BalanceDto(from, today, result, signals, Focus(result, signals));
    }

    public static GoalHealth Health(GoalStatsDto stats)
    {
        var known = stats.Done + stats.Missed;
        if (stats.Missed >= RecurringMisses && stats.DonePercent < StrugglingPercent)
        {
            return GoalHealth.Struggling;
        }

        if (known < MinKnownDays)
        {
            return GoalHealth.NotEnoughData;
        }

        return stats.DonePercent >= ThrivingPercent ? GoalHealth.Thriving : GoalHealth.Steady;
    }

    /// <summary>"Não deu tempo", "estava muito cansado"… (accents and case do not matter).</summary>
    public static bool IsTimeOrEnergy(string? reason)
    {
        if (string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        var text = WithoutAccents(reason).ToLowerInvariant();
        return TimeOrEnergyWords.Any(text.Contains);
    }

    private static List<BalanceSignalDto> Signals(
        IReadOnlyList<GoalBalanceDto> goals,
        PeriodStatsDto recent,
        PeriodStatsDto earlier,
        IReadOnlyList<CheckInDto> missed)
    {
        var signals = new List<BalanceSignalDto>();

        var struggling = goals.Where(g => g.Health == GoalHealth.Struggling).Select(g => g.GoalId).ToList();
        if (struggling.Count > 0)
        {
            signals.Add(new BalanceSignalDto(BalanceSignal.RecurringDifficulty, struggling));
        }

        // Two goals or more falling in the same weeks; one goal falling alone is not a conflict between goals.
        var dropping = goals.Where(g =>
        {
            var now = recent.Goals.FirstOrDefault(s => s.GoalId == g.GoalId);
            var before = earlier.Goals.FirstOrDefault(s => s.GoalId == g.GoalId);
            return now is { DonePercent: { } nowPercent } && before is { DonePercent: { } beforePercent }
                   && now.Done + now.Missed >= MinKnownDaysPerHalf
                   && before.Done + before.Missed >= MinKnownDaysPerHalf
                   && nowPercent <= beforePercent - DropPoints;
        }).Select(g => g.GoalId).ToList();
        if (dropping.Count >= 2)
        {
            signals.Add(new BalanceSignalDto(BalanceSignal.SimultaneousDrop, dropping));
        }

        var noTime = missed.Where(c => IsTimeOrEnergy(c.Reason)).ToList();
        if (noTime.Count >= TimeOrEnergyMisses)
        {
            signals.Add(new BalanceSignalDto(BalanceSignal.LackOfTimeOrEnergy, noTime.Select(c => c.GoalId).Distinct().ToList()));
        }

        return signals;
    }

    // The goal to talk about: the hardest one among those the signals point at, never one that is working.
    private static Guid? Focus(IReadOnlyList<GoalBalanceDto> goals, IReadOnlyList<BalanceSignalDto> signals)
    {
        var pointed = signals.SelectMany(s => s.GoalIds).ToHashSet();
        return goals
            .Where(g => pointed.Contains(g.GoalId) && g.Health != GoalHealth.Thriving)
            .OrderBy(g => g.DonePercent ?? 0)
            .ThenByDescending(g => g.Missed)
            .Select(g => (Guid?)g.GoalId)
            .FirstOrDefault();
    }

    private static (string? Unit, decimal? Total) MainTotal(IReadOnlyList<ProgressEntryDto> entries)
    {
        var main = entries.Where(e => e.Amount is not null && !string.IsNullOrWhiteSpace(e.Unit))
            .GroupBy(e => Unit(e.Unit))
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();
        return main is null ? (null, null) : (main.First().Unit!.Trim(), main.Sum(e => e.Amount!.Value));
    }

    /// <summary>
    /// Only goals that add up (no start value: "ler 12 livros", not "pesar 90 kg"), reported in the unit of the target.
    /// The pace is the window's (or the days since the goal was created, at least <see cref="MinDaysForPace"/>).
    /// </summary>
    internal static ProjectionDto? Project(GoalDto goal, IReadOnlyList<ProgressEntryDto> entries, DateOnly from, DateOnly today)
    {
        if (goal is not { TargetValue: > 0, DueDate: { } due } || due <= today || goal.StartValue is not (null or 0)
            || string.IsNullOrWhiteSpace(goal.Unit))
        {
            return null;
        }

        var inUnit = entries.Where(e => e.Amount is > 0 && Unit(e.Unit) == Unit(goal.Unit)).ToList();
        if (inUnit.Count == 0)
        {
            return null;
        }

        var created = DateOnly.FromDateTime(goal.CreatedAt.UtcDateTime);
        var start = created > from ? created : from;
        var days = Math.Max(MinDaysForPace, today.DayNumber - start.DayNumber + 1);
        var pace = inUnit.Where(e => e.OccurredOn >= start).Sum(e => e.Amount!.Value) / days;

        var soFar = inUnit.Sum(e => e.Amount!.Value);
        var projected = Math.Round(soFar + pace * (due.DayNumber - today.DayNumber), 1, MidpointRounding.AwayFromZero);
        return new ProjectionDto(soFar, projected, goal.TargetValue.Value, goal.Unit.Trim(), due);
    }

    private static string Unit(string? unit) => WithoutAccents(unit ?? "").Trim().ToLowerInvariant();

    private static string WithoutAccents(string text)
    {
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var kept = decomposed.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray();
        return new string(kept).Normalize(NormalizationForm.FormC);
    }
}

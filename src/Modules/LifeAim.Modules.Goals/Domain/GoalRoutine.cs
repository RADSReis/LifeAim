using LifeAim.Modules.Goals.Contracts;

namespace LifeAim.Modules.Goals.Domain;

/// <summary>
/// One recurring activity of a goal's plan: "ler seg, qua e sex de manhã, 10 páginas". Modelled on iCalendar's
/// recurrence rule (frequency, interval, weekdays) and on habit trackers' "X times per period".
/// The rules here are the same as the CHECKs of goals.goal_routines, so an invalid routine fails before the database.
/// </summary>
internal sealed class GoalRoutine
{
    public const int MaxActivityLength = 100;

    private GoalRoutine(
        Guid id,
        Guid goalId,
        Guid userId,
        string activity,
        RoutineFrequency frequency,
        int interval,
        IReadOnlyList<int> daysOfWeek,
        int? timesPerPeriod,
        TimeOnly? timeOfDay,
        DayPeriod? dayPeriod,
        decimal? amount,
        string? unit,
        int? durationMinutes,
        RoutineSource source,
        DateOnly? startsOn,
        DateOnly? endsOn,
        DateTimeOffset createdAt)
    {
        Id = id;
        GoalId = goalId;
        UserId = userId;
        Activity = activity;
        Frequency = frequency;
        Interval = interval;
        DaysOfWeek = daysOfWeek;
        TimesPerPeriod = timesPerPeriod;
        TimeOfDay = timeOfDay;
        DayPeriod = dayPeriod;
        Amount = amount;
        Unit = unit;
        DurationMinutes = durationMinutes;
        Source = source;
        StartsOn = startsOn;
        EndsOn = endsOn;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }
    public Guid GoalId { get; }
    public Guid UserId { get; }

    /// <summary>What to do ("ler", "ir à academia").</summary>
    public string Activity { get; }

    public RoutineFrequency Frequency { get; }

    /// <summary>Every how many periods: 2 with Weekly = every other week.</summary>
    public int Interval { get; }

    /// <summary>ISO weekdays (1 = Monday … 7 = Sunday), sorted; empty when the days are not fixed.</summary>
    public IReadOnlyList<int> DaysOfWeek { get; }

    /// <summary>"3 vezes por semana" without fixed days.</summary>
    public int? TimesPerPeriod { get; }

    /// <summary>Only when the user said an exact time.</summary>
    public TimeOnly? TimeOfDay { get; }

    /// <summary>An approximate period ("de manhã"), never turned into an exact time.</summary>
    public DayPeriod? DayPeriod { get; }

    /// <summary>How much per session (10 "páginas").</summary>
    public decimal? Amount { get; }

    public string? Unit { get; }
    public int? DurationMinutes { get; }

    /// <summary>Whether the user described it or Pippin proposed it.</summary>
    public RoutineSource Source { get; }

    public DateOnly? StartsOn { get; }
    public DateOnly? EndsOn { get; }
    public DateTimeOffset CreatedAt { get; }

    public static GoalRoutine Create(Guid goalId, Guid userId, RoutineInput input, DateTimeOffset now)
    {
        if (goalId == Guid.Empty || userId == Guid.Empty)
        {
            throw new ArgumentException("A routine needs its goal and its user.");
        }

        var activity = Goal.Blank(input.Activity)
            ?? throw new ArgumentException("A routine needs an activity.", nameof(input));
        if (activity.Length > MaxActivityLength)
        {
            throw new ArgumentException($"An activity has at most {MaxActivityLength} characters.", nameof(input));
        }

        if (input.Interval < 1)
        {
            throw new ArgumentException("The interval is at least 1.", nameof(input));
        }

        var days = (input.DaysOfWeek ?? []).Distinct().Order().ToList();
        if (days.Any(day => day is < 1 or > 7))
        {
            throw new ArgumentException("Weekdays go from 1 (Monday) to 7 (Sunday).", nameof(input));
        }

        if (days.Count > 0 && input.Frequency != RoutineFrequency.Weekly)
        {
            throw new ArgumentException("Fixed weekdays only make sense in a weekly routine.", nameof(input));
        }

        if (days.Count > 0 && input.TimesPerPeriod is not null)
        {
            throw new ArgumentException("Fixed weekdays already say how many times.", nameof(input));
        }

        if (input.TimesPerPeriod < 1 || input.Amount <= 0 || input.DurationMinutes < 1)
        {
            throw new ArgumentException("Times, amount and duration must be positive.", nameof(input));
        }

        if (input.StartsOn is { } starts && input.EndsOn is { } ends && ends < starts)
        {
            throw new ArgumentException("A routine cannot end before it starts.", nameof(input));
        }

        var unit = Goal.Blank(input.Unit);
        if (unit?.Length > Goal.MaxUnitLength)
        {
            throw new ArgumentException($"A unit has at most {Goal.MaxUnitLength} characters.", nameof(input));
        }

        return new GoalRoutine(
            Guid.CreateVersion7(now), goalId, userId, activity, input.Frequency, input.Interval, days,
            input.TimesPerPeriod, input.TimeOfDay, input.DayPeriod, input.Amount, unit, input.DurationMinutes,
            input.Source, input.StartsOn, input.EndsOn, Goal.ToDatabasePrecision(now));
    }

    /// <summary>Rebuilds a routine read from the database (already validated when it was created).</summary>
    public static GoalRoutine Load(
        Guid id, Guid goalId, Guid userId, string activity, RoutineFrequency frequency, int interval,
        IReadOnlyList<int> daysOfWeek, int? timesPerPeriod, TimeOnly? timeOfDay, DayPeriod? dayPeriod,
        decimal? amount, string? unit, int? durationMinutes, RoutineSource source, DateOnly? startsOn,
        DateOnly? endsOn, DateTimeOffset createdAt) =>
        new(id, goalId, userId, activity, frequency, interval, daysOfWeek, timesPerPeriod, timeOfDay, dayPeriod,
            amount, unit, durationMinutes, source, startsOn, endsOn, createdAt);
}

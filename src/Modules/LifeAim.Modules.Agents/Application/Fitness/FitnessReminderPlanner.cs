using LifeAim.Modules.Fitness.Contracts;

namespace LifeAim.Modules.Agents.Application.Fitness;

/// <summary>
/// Which reminders of the journal are due now. Plain code, no AI, like the follow-up planner:
/// - a reminder is due from the time the user chose until <see cref="Window"/> later, once per food day;
/// - never when what it asks is already there (the weight, a meal) or was said to be done;
/// - the summary of the day needs something to summarize.
/// Every time here was chosen by the user (or is a default inside 9h–22h), so the quiet hours of the daily follow-up
/// do not apply: "me lembra às 7h" is an explicit time (Funcionalidade 3, "Limite padrão para mensagens").
/// </summary>
internal static class FitnessReminderPlanner
{
    /// <summary>How long after its time a reminder is still worth sending (the API was down, the user was busy).</summary>
    public static readonly TimeSpan Window = TimeSpan.FromHours(3);

    public static readonly TimeOnly DefaultWeighIn = new(9, 0);
    public static readonly TimeOnly DefaultNoRecord = new(14, 0);
    public static readonly TimeOnly DefaultDayClose = new(21, 30);

    public static TimeOnly DefaultTime(ReminderKind kind) => kind switch
    {
        ReminderKind.WeighIn => DefaultWeighIn,
        ReminderKind.NoRecord => DefaultNoRecord,
        ReminderKind.DayClose => DefaultDayClose,
        _ => DefaultWeighIn,
    };

    /// <param name="now">The user's local time.</param>
    /// <param name="reminders">The user's reminders with their status on the food day of <paramref name="now"/>.</param>
    /// <param name="day">What the journal has on that food day.</param>
    public static IReadOnlyList<ReminderDto> Due(DateTime now, IReadOnlyList<ReminderDto> reminders, FitnessDayDto day) =>
        reminders.Where(r => !r.SentToday && !r.DoneToday && IsTime(now, r.At) && StillNeeded(r, day)).ToList();

    private static bool IsTime(DateTime now, TimeOnly at)
    {
        // A time after midnight ("00:30") belongs to the food day that started the day before.
        var foodDay = FoodDay.Of(now);
        var moment = foodDay.ToDateTime(at).AddDays(FoodDay.IsLateNight(at) ? 1 : 0);
        return now >= moment && now < moment + Window;
    }

    private static bool StillNeeded(ReminderDto reminder, FitnessDayDto day) => reminder.Kind switch
    {
        ReminderKind.WeighIn => day.Body?.WeightKg is null,
        ReminderKind.NoRecord => day.Meals.Count == 0,
        ReminderKind.DayClose => day.Meals.Count > 0 || day.Exercises.Count > 0,
        _ => true,
    };
}

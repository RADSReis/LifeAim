using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Goals.Domain;

namespace LifeAim.Modules.Goals.Tests;

public class GoalRoutineTests
{
    private static readonly Guid GoalId = Guid.CreateVersion7();
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateTimeOffset Now = new(2026, 11, 14, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Fixed_weekdays_are_sorted_without_repeats()
    {
        var routine = Create(new("ler", RoutineFrequency.Weekly, RoutineSource.User, DaysOfWeek: [5, 1, 3, 1]));

        Assert.Equal([1, 3, 5], routine.DaysOfWeek);
        Assert.Equal("ler", routine.Activity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(8)]
    public void Weekdays_go_from_monday_1_to_sunday_7(int day) =>
        Assert.Throws<ArgumentException>(() =>
            Create(new("ler", RoutineFrequency.Weekly, RoutineSource.User, DaysOfWeek: [day])));

    [Fact]
    public void Fixed_weekdays_only_in_a_weekly_routine() =>
        Assert.Throws<ArgumentException>(() =>
            Create(new("ler", RoutineFrequency.Daily, RoutineSource.User, DaysOfWeek: [1])));

    [Fact]
    public void Fixed_weekdays_and_times_per_week_cannot_both_be_said() =>
        Assert.Throws<ArgumentException>(() =>
            Create(new("ler", RoutineFrequency.Weekly, RoutineSource.User, DaysOfWeek: [1, 3], TimesPerPeriod: 2)));

    [Fact]
    public void A_routine_needs_an_activity_and_a_positive_interval()
    {
        Assert.Throws<ArgumentException>(() => Create(new(" ", RoutineFrequency.Daily, RoutineSource.User)));
        Assert.Throws<ArgumentException>(() => Create(new("ler", RoutineFrequency.Daily, RoutineSource.User, Interval: 0)));
    }

    [Fact]
    public void A_routine_cannot_end_before_it_starts() =>
        Assert.Throws<ArgumentException>(() => Create(new(
            "ler", RoutineFrequency.Daily, RoutineSource.User,
            StartsOn: new DateOnly(2026, 12, 1), EndsOn: new DateOnly(2026, 11, 30))));

    [Fact]
    public void Amounts_and_durations_must_be_positive()
    {
        Assert.Throws<ArgumentException>(() => Create(new("ler", RoutineFrequency.Daily, RoutineSource.User, Amount: 0)));
        Assert.Throws<ArgumentException>(() => Create(new("ler", RoutineFrequency.Daily, RoutineSource.User, DurationMinutes: 0)));
    }

    private static GoalRoutine Create(RoutineInput input) => GoalRoutine.Create(GoalId, UserId, input, Now);
}

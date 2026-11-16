using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Application;

namespace LifeAim.Modules.Reporting.Tests;

/// <summary>Funcionalidade 4: the month's numbers count only the days whose result is known.</summary>
public class ReportCalculatorTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();
    private static readonly DateOnly November = new(2026, 11, 1);
    private static readonly DateOnly EndOfNovember = new(2026, 11, 30);

    private readonly GoalDto _reading = Goal("Ler 12 livros");
    private readonly GoalDto _gym = Goal("Academia 3x por semana");

    [Fact]
    public void The_percentage_counts_done_and_missed_days_only()
    {
        var progress = new[]
        {
            Entry(_reading, 2, 10), Entry(_reading, 3, 10), Entry(_reading, 4, 15), Entry(_reading, 5, 5),
        };
        var checkIns = new[]
        {
            CheckIn(_reading, 6, CheckInStatus.Missed),
            CheckIn(_reading, 7, CheckInStatus.Asked), // nobody answered: not a failure
        };

        var month = ReportCalculator.Period(November, EndOfNovember, [_reading], [Routine(_reading)], progress, checkIns);

        var reading = Assert.Single(month.Goals);
        Assert.Equal((4, 1, 1, 80), (reading.Done, reading.Missed, reading.Unconfirmed, reading.DonePercent));
        Assert.Equal((4, 1, 1, 80), (month.Done, month.Missed, month.Unconfirmed, month.DonePercent));
        Assert.Equal(40m, Assert.Single(reading.Totals).Total);
        Assert.Equal("páginas", reading.MainUnit);
    }

    [Fact]
    public void A_day_counts_once_per_goal_and_done_wins_over_an_earlier_no()
    {
        var progress = new[] { Entry(_gym, 9, null), Entry(_gym, 9, null) };
        var checkIns = new[] { CheckIn(_gym, 9, CheckInStatus.Missed), CheckIn(_gym, 9, CheckInStatus.Done) };

        var gym = Assert.Single(ReportCalculator.Period(November, EndOfNovember, [_gym], [Routine(_gym)], progress, checkIns).Goals);

        Assert.Equal((1, 0, 100), (gym.Done, gym.Missed, gym.DonePercent));
    }

    [Fact]
    public void No_known_result_means_no_percentage()
    {
        var checkIns = new[] { CheckIn(_gym, 3, CheckInStatus.Asked) };

        var month = ReportCalculator.Period(November, EndOfNovember, [_gym], [Routine(_gym)], [], checkIns);

        Assert.Null(month.DonePercent);
        Assert.Equal(1, month.Unconfirmed);
    }

    [Fact]
    public void Outside_the_period_does_not_count()
    {
        var progress = new[] { Entry(_reading, 1, 10), new ProgressEntryDto(
            Guid.CreateVersion7(), UserId, _reading.Id, new DateOnly(2026, 10, 31), 10, "páginas", "leu", DateTimeOffset.UtcNow) };

        var month = ReportCalculator.Period(November, EndOfNovember, [_reading], [Routine(_reading)], progress, []);

        Assert.Equal(1, month.Done);
    }

    [Fact]
    public void A_goal_without_plan_nor_activity_is_left_out()
    {
        var month = ReportCalculator.Period(November, EndOfNovember, [_reading, _gym], [Routine(_gym)], [], []);

        Assert.Equal(_gym.Id, Assert.Single(month.Goals).GoalId);
    }

    [Fact]
    public void The_weekdays_show_where_the_effort_goes()
    {
        // Saturday 7 and Sunday 8: 30 pages each; Monday 9: 10 pages.
        var progress = new[] { Entry(_reading, 7, 30), Entry(_reading, 8, 30), Entry(_reading, 9, 10) };

        var reading = Assert.Single(ReportCalculator.Period(November, EndOfNovember, [_reading], [Routine(_reading)], progress, []).Goals);

        Assert.Equal(30m, reading.ByWeekday.Single(d => d.IsoDay == 6).Amount);
        Assert.Equal(30m, reading.ByWeekday.Single(d => d.IsoDay == 7).Amount);
        Assert.Equal((1, 10m), (reading.ByWeekday[0].DoneDays, reading.ByWeekday[0].Amount));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(6, true)]
    [InlineData(11, false)]
    [InlineData(12, true)]
    public void Quarters_close_in_march_june_september_and_december(int month, bool closes) =>
        Assert.Equal(closes, ReportCalculator.ClosesQuarter(new DateOnly(2026, month, 15)));

    [Fact]
    public void Percentages_are_rounded_half_up() => Assert.Equal(67, ReportCalculator.Percent(2, 1));

    private static GoalDto Goal(string name) =>
        new(Guid.CreateVersion7(), UserId, name, null, null, null, null, null, null, DateTimeOffset.UtcNow);

    private static RoutineDto Routine(GoalDto goal) =>
        new(Guid.CreateVersion7(), goal.Id, "fazer", RoutineFrequency.Daily, 1, [], null, null, null, null, null, null,
            RoutineSource.User, null, null, DateTimeOffset.UtcNow);

    private static ProgressEntryDto Entry(GoalDto goal, int day, decimal? pages) =>
        new(Guid.CreateVersion7(), UserId, goal.Id, new DateOnly(2026, 11, day), pages, pages is null ? null : "páginas",
            "fez", DateTimeOffset.UtcNow);

    private static CheckInDto CheckIn(GoalDto goal, int day, CheckInStatus status) =>
        new(Guid.CreateVersion7(), UserId, goal.Id, Guid.CreateVersion7(), new DateOnly(2026, 11, day), "fazer", status, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, status == CheckInStatus.Asked ? null : DateTimeOffset.UtcNow, null);
}

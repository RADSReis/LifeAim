using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Application;
using LifeAim.Modules.Reporting.Contracts;

namespace LifeAim.Modules.Reporting.Tests;

/// <summary>
/// Funcionalidade 5: the signals come from the observed history, a goal that works is protected, and an isolated
/// difficulty (or a day nobody answered) never starts an intervention.
/// </summary>
public class BalanceAnalyzerTests
{
    private static readonly Guid UserId = Guid.CreateVersion7();

    // The window is 01/11 to 28/11: earlier half 01–14, recent half 15–28.
    private static readonly DateOnly Today = new(2026, 11, 28);

    private readonly GoalDto _gym = Goal("Academia 3x por semana");
    private readonly GoalDto _reading = Goal("Ler 12 livros", target: 12, unit: "livros", due: new DateOnly(2026, 12, 31));
    private readonly GoalDto _meditation = Goal("Meditar");

    private readonly List<ProgressEntryDto> _progress = [];
    private readonly List<CheckInDto> _checkIns = [];

    [Fact]
    public void A_habit_that_works_is_protected_and_the_struggling_goal_is_the_focus()
    {
        Done(_gym, 2, 4, 6, 9, 11, 13, 16, 18, 20, 23);
        Missed(_gym, 25);
        Done(_reading, 3);
        Missed(_reading, 8, 15, 22, 26);

        var balance = Analyze();

        Assert.Equal(GoalHealth.Thriving, Of(balance, _gym).Health);
        Assert.Equal(GoalHealth.Struggling, Of(balance, _reading).Health);
        Assert.Equal(_reading.Id, balance.FocusGoalId);
        var signal = Assert.Single(balance.Signals);
        Assert.Equal(BalanceSignal.RecurringDifficulty, signal.Kind);
        Assert.Equal([_reading.Id], signal.GoalIds);
        Assert.Equal([_gym.Id], balance.Thriving.Select(g => g.GoalId));
    }

    [Fact]
    public void An_isolated_bad_stretch_is_not_a_signal()
    {
        Done(_reading, 2, 5, 9, 12, 19);
        Missed(_reading, 15);
        Missed(_reading, 22, "cansado");

        var balance = Analyze();

        Assert.Empty(balance.Signals);
        Assert.False(balance.NeedsReview);
    }

    [Fact]
    public void Days_nobody_answered_are_never_counted_as_misses()
    {
        Done(_reading, 2);
        foreach (var day in new[] { 5, 8, 11, 15, 19, 23 })
        {
            _checkIns.Add(CheckIn(_reading, day, CheckInStatus.Asked));
        }

        var balance = Analyze();

        var reading = Of(balance, _reading);
        Assert.Equal((1, 0, 6), (reading.Done, reading.Missed, reading.Unconfirmed));
        Assert.Equal(GoalHealth.NotEnoughData, reading.Health);
        Assert.Empty(balance.Signals);
    }

    [Fact]
    public void Two_goals_dropping_in_the_same_weeks_is_a_signal_and_one_alone_is_not()
    {
        Done(_gym, 2, 5, 8, 12, 16);
        Missed(_gym, 19, 22, 26);
        Done(_reading, 3, 6, 10, 17);
        Missed(_reading, 21, 25);

        var balance = Analyze();

        var drop = Assert.Single(balance.Signals, s => s.Kind == BalanceSignal.SimultaneousDrop);
        Assert.Equal(new[] { _gym.Id, _reading.Id }.Order(), drop.GoalIds.Order());
        Assert.Equal((100, 25), (Of(balance, _gym).EarlierPercent, Of(balance, _gym).RecentPercent));
        Assert.Equal(_gym.Id, balance.FocusGoalId); // the lowest percentage of the two (63% against 67%)

        _checkIns.RemoveAll(c => c.GoalId == _reading.Id);
        Assert.DoesNotContain(Analyze().Signals, s => s.Kind == BalanceSignal.SimultaneousDrop);
    }

    [Fact]
    public void Repeated_lack_of_time_or_energy_across_goals_is_a_signal()
    {
        Done(_reading, 2, 9, 16);
        Missed(_reading, 5, "não deu tempo");
        Missed(_reading, 12, "Estava muito CANSADO");
        Done(_meditation, 3, 10);
        Missed(_meditation, 18, "sem energia hoje");
        Missed(_meditation, 20, "choveu"); // not time nor energy

        var balance = Analyze();

        var signal = Assert.Single(balance.Signals);
        Assert.Equal(BalanceSignal.LackOfTimeOrEnergy, signal.Kind);
        Assert.Equal(new[] { _meditation.Id, _reading.Id }.Order(), signal.GoalIds.Order());
        Assert.Equal(_meditation.Id, balance.FocusGoalId); // 50% against 60%
        Assert.Equal(["Estava muito CANSADO", "não deu tempo"], Of(balance, _reading).MissReasons);
    }

    [Fact]
    public void Even_with_a_signal_a_goal_that_works_is_never_the_one_to_change()
    {
        Done(_gym, 1, 2, 3, 5, 7, 9, 11, 14, 16, 18);
        Missed(_gym, 20, "sem tempo");
        Missed(_gym, 22, "trabalhei até tarde");
        Missed(_gym, 24, "cansada");

        var balance = Analyze();

        Assert.Equal(GoalHealth.Thriving, Of(balance, _gym).Health);
        Assert.Contains(balance.Signals, s => s.Kind == BalanceSignal.LackOfTimeOrEnergy);
        Assert.Null(balance.FocusGoalId);
    }

    [Theory]
    [InlineData("Não deu TEMPO", true)]
    [InlineData("tava exausto", true)]
    [InlineData("dia corrido", true)]
    [InlineData("estava sem pique", true)]
    [InlineData("viajei", false)]
    [InlineData(null, false)]
    public void Lack_of_time_or_energy_is_read_from_the_users_words(string? reason, bool expected) =>
        Assert.Equal(expected, BalanceAnalyzer.IsTimeOrEnergy(reason));

    [Fact]
    public void Absolute_progress_is_projected_as_an_estimate_at_the_current_pace()
    {
        _progress.Add(Entry(_reading, 10, 1, "livros"));
        _progress.Add(Entry(_reading, 25, 1, "livros"));
        _progress.Add(Entry(_reading, 26, 1, "Livros"));

        var reading = Of(Analyze(), _reading);

        // 3 books in 28 days; 33 days until 31/12 → 3 + 3/28 × 33 ≈ 6.5 (below the 12 imagined, and still worth it).
        var projection = Assert.IsType<ProjectionDto>(reading.Projection);
        Assert.Equal((3m, 6.5m, 12m, "livros"), (projection.SoFar, projection.Projected, projection.Target, projection.Unit));
        Assert.Equal(("livros", 3m), (reading.MainUnit, reading.TotalSoFar));
    }

    [Fact]
    public void There_is_no_projection_in_another_unit_nor_for_a_goal_that_does_not_add_up()
    {
        _progress.Add(Entry(_reading, 10, 40, "páginas"));
        var weight = Goal("Pesar 90 kg", target: 90, unit: "kg", due: new DateOnly(2026, 12, 31), start: 94);
        _progress.Add(Entry(weight, 12, 93, "kg"));

        var balance = Analyze(weight);

        Assert.Null(Of(balance, _reading).Projection);
        Assert.Equal(("páginas", 40m), (Of(balance, _reading).MainUnit, Of(balance, _reading).TotalSoFar));
        Assert.Null(Of(balance, weight).Projection);
    }

    private BalanceDto Analyze(params GoalDto[] more)
    {
        GoalDto[] goals = [_gym, _reading, _meditation, .. more];
        return BalanceAnalyzer.Analyze(Today, goals, goals.Select(Routine).ToList(), _progress, _checkIns);
    }

    private static GoalBalanceDto Of(BalanceDto balance, GoalDto goal) => balance.Goals.Single(g => g.GoalId == goal.Id);

    private void Done(GoalDto goal, params int[] days) => _progress.AddRange(days.Select(day => Entry(goal, day, null, null)));

    private void Missed(GoalDto goal, params int[] days) =>
        _checkIns.AddRange(days.Select(day => CheckIn(goal, day, CheckInStatus.Missed)));

    private void Missed(GoalDto goal, int day, string reason) =>
        _checkIns.Add(CheckIn(goal, day, CheckInStatus.Missed) with { Reason = reason });

    private static GoalDto Goal(string name, decimal? target = null, string? unit = null, DateOnly? due = null, decimal? start = null) =>
        new(Guid.CreateVersion7(), UserId, name, null, null, due, start, target, unit,
            new DateTimeOffset(2026, 10, 1, 12, 0, 0, TimeSpan.Zero));

    private static RoutineDto Routine(GoalDto goal) =>
        new(Guid.CreateVersion7(), goal.Id, "fazer", RoutineFrequency.Daily, 1, [], null, null, null, null, null, null,
            RoutineSource.User, null, null, DateTimeOffset.UtcNow);

    private static ProgressEntryDto Entry(GoalDto goal, int day, decimal? amount, string? unit) =>
        new(Guid.CreateVersion7(), UserId, goal.Id, new DateOnly(2026, 11, day), amount, unit, "fez", DateTimeOffset.UtcNow);

    private static CheckInDto CheckIn(GoalDto goal, int day, CheckInStatus status) =>
        new(Guid.CreateVersion7(), UserId, goal.Id, Guid.CreateVersion7(), new DateOnly(2026, 11, day), "fazer", status, 1,
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, status == CheckInStatus.Asked ? null : DateTimeOffset.UtcNow, null);
}

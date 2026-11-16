using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>Priorities and conflicts between goals (Funcionalidade 5) against a real, migrated PostgreSQL.</summary>
public sealed class BalanceServiceTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly Guid _userId = Guid.CreateVersion7();

    private IBalanceService Balance => factory.Services.GetRequiredService<IBalanceService>();
    private IGoalService Goals => factory.Services.GetRequiredService<IGoalService>();
    private IProgressService Progress => factory.Services.GetRequiredService<IProgressService>();
    private ICheckInService CheckIns => factory.Services.GetRequiredService<ICheckInService>();

    [Fact]
    public async Task The_history_of_the_other_modules_points_at_the_goal_in_difficulty_and_protects_the_one_that_works()
    {
        var gym = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Academia"));
        await Goals.ReplaceRoutinesAsync(gym.Id, _userId, [new RoutineInput("ir à academia", RoutineFrequency.Daily, RoutineSource.User)]);
        var reading = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ler 12 livros", TargetValue: 12, Unit: "livros"));
        var routineId = (await Goals.ReplaceRoutinesAsync(
            reading.Id, _userId, [new RoutineInput("ler", RoutineFrequency.Daily, RoutineSource.User)]))!.Single().Id;

        foreach (var daysAgo in new[] { 1, 3, 5, 8, 10, 12 })
        {
            await Progress.RecordAsync(new RecordProgressRequest(_userId, gym.Id, Today.AddDays(-daysAgo), "treinou"));
        }

        await Progress.RecordAsync(new RecordProgressRequest(_userId, reading.Id, Today.AddDays(-2), "leu 20 páginas", 20, "páginas"));
        foreach (var (daysAgo, reason) in new[] { (4, "não deu tempo"), (6, "cansado"), (9, (string?)null), (11, "sem energia") })
        {
            var asked = await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, reading.Id, routineId, Today.AddDays(-daysAgo), "ler"));
            await CheckIns.MarkMissedAsync(asked.Id, _userId, reason);
        }

        await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, reading.Id, routineId, Today.AddDays(-7), "ler")); // unanswered

        var balance = await Balance.AnalyzeAsync(_userId, Today);

        Assert.Equal(reading.Id, balance.FocusGoalId);
        Assert.Equal(GoalHealth.Thriving, balance.Goals.Single(g => g.GoalId == gym.Id).Health);
        var focus = balance.Focus!;
        Assert.Equal((1, 4, 1, GoalHealth.Struggling), (focus.Done, focus.Missed, focus.Unconfirmed, focus.Health));
        Assert.Equal(("páginas", 20m), (focus.MainUnit, focus.TotalSoFar));
        Assert.Equal(["não deu tempo", "cansado", "sem energia"], focus.MissReasons);
        Assert.Contains(balance.Signals, s => s.Kind == BalanceSignal.RecurringDifficulty);
        Assert.Contains(balance.Signals, s => s.Kind == BalanceSignal.LackOfTimeOrEnergy);
    }

    [Fact]
    public async Task A_review_is_recorded_answered_and_closed_once()
    {
        var focus = Guid.CreateVersion7();
        var gym = Guid.CreateVersion7();
        var id = Guid.CreateVersion7();
        Assert.True(await Balance.RecordSentAsync(new RecordReviewRequest(
            id, _userId, focus, [BalanceSignal.RecurringDifficulty, BalanceSignal.LackOfTimeOrEnergy], [gym], "Você está indo bem na academia!")));

        var review = await Balance.GetLatestAsync(_userId);
        Assert.Equal((id, focus, ReviewStatus.Offered), (review!.Id, review.FocusGoalId, review.Status));
        Assert.Equal([BalanceSignal.RecurringDifficulty, BalanceSignal.LackOfTimeOrEnergy], review.Signals);
        Assert.Equal([gym], review.ProtectedGoalIds);

        Assert.True(await Balance.AnswerAsync(id, _userId, ReviewStatus.Explained, "chego cansado"));
        Assert.True(await Balance.AnswerAsync(id, _userId, ReviewStatus.Accepted, null));
        Assert.False(await Balance.AnswerAsync(id, _userId, ReviewStatus.Declined, null)); // already closed

        var closed = await Balance.FindAsync(id, _userId);
        Assert.Equal((ReviewStatus.Accepted, "chego cansado"), (closed!.Status, closed.Obstacle));
        Assert.Null(await Balance.FindAsync(id, Guid.CreateVersion7()));
    }

    [Fact]
    public async Task A_goal_that_works_is_never_the_focus_of_a_review()
    {
        var goal = Guid.CreateVersion7();

        await Assert.ThrowsAsync<ArgumentException>(() => Balance.RecordSentAsync(new RecordReviewRequest(
            Guid.CreateVersion7(), _userId, goal, [BalanceSignal.RecurringDifficulty], [goal], "mensagem")));
        await Assert.ThrowsAsync<ArgumentException>(() => Balance.RecordSentAsync(new RecordReviewRequest(
            Guid.CreateVersion7(), _userId, goal, [], [], "mensagem")));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        foreach (var table in new[]
                 {
                     "reporting.balance_reviews", "progress.check_ins", "progress.progress_entries", "goals.goal_routines", "goals.goals",
                 })
        {
            await using var command = dataSource.CreateCommand($"DELETE FROM {table} WHERE user_id = $1");
            command.Parameters.Add(new() { Value = _userId });
            await command.ExecuteNonQueryAsync();
        }
    }
}

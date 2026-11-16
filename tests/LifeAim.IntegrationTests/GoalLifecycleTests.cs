using LifeAim.Modules.Goals.Contracts;
using LifeAim.Modules.Progress.Contracts;
using LifeAim.Modules.Reporting.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>Funcionalidade 6 against a real, migrated PostgreSQL: pause, resume, end and replace, keeping the history.</summary>
public sealed class GoalLifecycleTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.UtcNow);

    private readonly Guid _userId = Guid.CreateVersion7();

    private IGoalService Goals => factory.Services.GetRequiredService<IGoalService>();

    [Fact]
    public async Task A_paused_goal_leaves_the_lists_keeps_its_plan_and_comes_back_with_it()
    {
        var running = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Correr 5 km"));
        await Goals.ReplaceRoutinesAsync(running.Id, _userId, [new RoutineInput("correr", RoutineFrequency.Daily, RoutineSource.User)]);

        Assert.True(await Goals.PauseAsync(running.Id, _userId, Today.AddDays(10), "joelho"));

        Assert.Empty(await Goals.ListAsync(_userId));
        Assert.Empty(await Goals.ListRoutinesAsync(_userId));
        Assert.DoesNotContain(_userId, await Goals.ListUserIdsWithRoutinesAsync());
        Assert.Single(await Goals.ListGoalRoutinesAsync(running.Id, _userId)); // the plan is kept
        var paused = Assert.Single(await Goals.ListHistoryAsync(_userId));
        Assert.Equal((GoalStatus.Paused, Today.AddDays(10), "joelho"), (paused.Status, paused.ResumeOn, paused.StatusReason));
        Assert.NotNull(paused.StatusChangedAt);

        Assert.True(await Goals.ResumeAsync(running.Id, _userId));
        Assert.False(await Goals.ResumeAsync(running.Id, _userId)); // not paused any more

        Assert.Single(await Goals.ListAsync(_userId));
        Assert.Single(await Goals.ListRoutinesAsync(_userId));
        Assert.Empty(await Goals.ListHistoryAsync(_userId));
        Assert.Equal(["active>paused", "paused>active"], await ChangesAsync(running.Id));
    }

    [Fact]
    public async Task A_pause_never_ends_in_the_past()
    {
        var goal = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Meditar"));

        await Assert.ThrowsAsync<ArgumentException>(() => Goals.PauseAsync(goal.Id, _userId, Today.AddDays(-2), null));
        Assert.Single(await Goals.ListAsync(_userId));
    }

    [Fact]
    public async Task Pippin_asks_once_per_pause_when_it_ends()
    {
        var goal = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Correr 5 km"));
        await Goals.PauseAsync(goal.Id, _userId, Today, null);

        Assert.Contains(await Goals.ListPausesEndingAsync(Today), g => g.Id == goal.Id);
        Assert.DoesNotContain(await Goals.ListPausesEndingAsync(Today.AddDays(-1)), g => g.Id == goal.Id);

        Assert.True(await Goals.MarkResumeOfferedAsync(goal.Id, _userId));
        Assert.False(await Goals.MarkResumeOfferedAsync(goal.Id, _userId));
        Assert.DoesNotContain(await Goals.ListPausesEndingAsync(Today), g => g.Id == goal.Id);

        // "mais um tempo": a new pause is a new chance to ask.
        await Goals.PauseAsync(goal.Id, _userId, Today.AddDays(7), null);
        Assert.Contains(await Goals.ListPausesEndingAsync(Today.AddDays(7)), g => g.Id == goal.Id);
        Assert.Equal(["active>paused", "paused>paused"], await ChangesAsync(goal.Id));
    }

    [Fact]
    public async Task Replacing_closes_the_old_goal_pointing_at_the_new_one_in_one_step()
    {
        var running = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Correr 5 km"));
        await Goals.ReplaceRoutinesAsync(running.Id, _userId, [new RoutineInput("correr", RoutineFrequency.Daily, RoutineSource.User)]);

        var swimming = await Goals.ReplaceAsync(running.Id, new CreateGoalRequest(_userId, "Nadar 2x por semana"), "joelho");

        Assert.NotNull(swimming);
        Assert.Equal([swimming.Id], (await Goals.ListAsync(_userId)).Select(g => g.Id));
        Assert.Empty(await Goals.ListRoutinesAsync(_userId)); // the new goal starts without a plan
        var old = Assert.Single(await Goals.ListHistoryAsync(_userId));
        Assert.Equal((GoalStatus.Replaced, swimming.Id, "joelho"), (old.Status, old.ReplacedBy, old.StatusReason));
        Assert.Equal(["active>replaced"], await ChangesAsync(running.Id));

        // A closed goal never changes again, and a failed replacement writes nothing.
        Assert.Null(await Goals.ReplaceAsync(running.Id, new CreateGoalRequest(_userId, "Pedalar"), null));
        Assert.False(await Goals.PauseAsync(running.Id, _userId, null, null));
        Assert.False(await Goals.CancelAsync(running.Id, _userId, "x"));
        Assert.DoesNotContain(await Goals.ListAsync(_userId), g => g.Name == "Pedalar");
    }

    [Fact]
    public async Task A_paused_goal_can_be_ended_and_keeps_the_reason()
    {
        var goal = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ler 12 livros"));
        await Goals.PauseAsync(goal.Id, _userId, null, null);

        Assert.True(await Goals.CancelAsync(goal.Id, _userId, "não faz mais sentido"));

        var ended = Assert.Single(await Goals.ListHistoryAsync(_userId));
        Assert.Equal((GoalStatus.Cancelled, "não faz mais sentido", (DateOnly?)null), (ended.Status, ended.StatusReason, ended.ResumeOn));
        Assert.Equal(["active>paused", "paused>cancelled"], await ChangesAsync(goal.Id));
    }

    [Fact]
    public async Task The_month_still_counts_what_was_done_before_a_goal_was_paused()
    {
        var reading = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ler 12 livros"));
        var progress = factory.Services.GetRequiredService<IProgressService>();
        await progress.RecordAsync(new RecordProgressRequest(_userId, reading.Id, Today.AddDays(-1), "leu 10 páginas", 10, "páginas"));
        await Goals.PauseAsync(reading.Id, _userId, null, "viagem");

        var report = await factory.Services.GetRequiredService<IReportingService>().BuildMonthlyAsync(_userId, Today.AddDays(-1));

        Assert.Equal(1, Assert.Single(report.Month.Goals, g => g.GoalId == reading.Id).Done);
    }

    private async Task<List<string>> ChangesAsync(Guid goalId)
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = dataSource.CreateCommand(
            "SELECT from_status || '>' || to_status FROM goals.goal_status_changes WHERE goal_id = $1 ORDER BY changed_at, id");
        command.Parameters.Add(new() { Value = goalId });
        var changes = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            changes.Add(reader.GetString(0));
        }

        return changes;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        foreach (var table in new[] { "progress.progress_entries", "goals.goal_routines", "goals.goals" })
        {
            await using var command = dataSource.CreateCommand($"DELETE FROM {table} WHERE user_id = $1");
            command.Parameters.Add(new() { Value = _userId });
            await command.ExecuteNonQueryAsync();
        }
    }
}

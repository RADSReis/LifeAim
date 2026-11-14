using LifeAim.Modules.Goals.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>The Goals module's public API against a real, migrated PostgreSQL.</summary>
public sealed class GoalServiceTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    // A user of its own, so the test never touches anyone else's goals and can clean up after itself.
    private readonly Guid _userId = Guid.CreateVersion7();

    private IGoalService Goals => factory.Services.GetRequiredService<IGoalService>();

    [Fact]
    public async Task Creates_goals_and_lists_them_oldest_first()
    {
        var reading = await Goals.CreateAsync(new CreateGoalRequest(
            _userId, "Ler 12 livros no ano", Indicator: "12 livros", DueDate: new DateOnly(2026, 12, 31)));
        var gym = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ir à academia"));

        var goals = await Goals.ListAsync(_userId);

        Assert.Equal([reading.Id, gym.Id], goals.Select(g => g.Id));
        Assert.Equal("12 livros", goals[0].Indicator);
        Assert.Equal(new DateOnly(2026, 12, 31), goals[0].DueDate);
        Assert.Null(goals[1].DueDate);
    }

    [Fact]
    public async Task Saves_and_reads_back_the_numeric_values()
    {
        await Goals.CreateAsync(new CreateGoalRequest(
            _userId, "Emagrecer até 90 kg", Indicator: "peso corporal", DueDate: new DateOnly(2026, 12, 25),
            StartValue: 94.5m, TargetValue: 90m, Unit: "kg"));

        var goal = Assert.Single(await Goals.ListAsync(_userId));

        Assert.Equal(94.5m, goal.StartValue);
        Assert.Equal(90m, goal.TargetValue);
        Assert.Equal("kg", goal.Unit);
    }

    [Fact]
    public async Task Lists_only_the_users_own_goals()
    {
        await Goals.CreateAsync(new CreateGoalRequest(_userId, "Aprender inglês"));

        var someoneElses = await Goals.ListAsync(Guid.CreateVersion7());

        Assert.Empty(someoneElses);
    }

    [Fact]
    public async Task Updates_the_editable_fields_and_keeps_id_user_and_creation_time()
    {
        var created = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ler mais"));

        var updated = await Goals.UpdateAsync(new UpdateGoalRequest(
            created.Id, _userId, "Ler 2 livros", null, "livros terminados", new DateOnly(2026, 12, 31), null, 2m, "livros",
            "vou ler um pouco toda noite"));

        var goal = Assert.Single(await Goals.ListAsync(_userId));
        Assert.Equal(updated, goal);
        Assert.Equal((created.Id, created.CreatedAt), (goal.Id, goal.CreatedAt));
        Assert.Equal(("Ler 2 livros", "livros terminados", 2m), (goal.Name, goal.Indicator!, goal.TargetValue!.Value));
        Assert.Equal("vou ler um pouco toda noite", goal.UserPlan);
    }

    [Fact]
    public async Task Does_not_update_someone_elses_goal()
    {
        var created = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ler mais"));

        var updated = await Goals.UpdateAsync(new UpdateGoalRequest(
            created.Id, Guid.CreateVersion7(), "Roubada", null, null, null, null, null, null, null));

        Assert.Null(updated);
        Assert.Equal("Ler mais", Assert.Single(await Goals.ListAsync(_userId)).Name);
    }

    [Fact]
    public async Task Saves_a_plan_of_routines_and_reads_every_field_back()
    {
        var gym = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ir à academia"));
        RoutineInput mornings = new(
            "ir à academia", RoutineFrequency.Weekly, RoutineSource.User, DaysOfWeek: [5, 1, 3],
            DayPeriod: DayPeriod.Morning, DurationMinutes: 60, StartsOn: new DateOnly(2026, 11, 14), EndsOn: new DateOnly(2026, 12, 31));
        RoutineInput saturdays = new(
            "correr", RoutineFrequency.Weekly, RoutineSource.Pippin, Interval: 2, TimesPerPeriod: 1,
            TimeOfDay: new TimeOnly(9, 30), Amount: 5m, Unit: "km");

        var saved = await Goals.ReplaceRoutinesAsync(gym.Id, _userId, [mornings, saturdays]);

        var routines = await Goals.ListRoutinesAsync(_userId);
        Assert.Equal(saved!.Select(r => r.Id), routines.Select(r => r.Id)); // in the order given
        Assert.Equal([1, 3, 5], routines[0].DaysOfWeek); // sorted, ISO weekdays
        Assert.Equal((DayPeriod.Morning, 60, RoutineSource.User), (routines[0].DayPeriod!.Value, routines[0].DurationMinutes!.Value, routines[0].Source));
        Assert.Equal((new TimeOnly(9, 30), 2, 5m, "km"), (routines[1].TimeOfDay!.Value, routines[1].Interval, routines[1].Amount!.Value, routines[1].Unit!));
        Assert.Empty(routines[1].DaysOfWeek);
    }

    [Fact]
    public async Task Replacing_the_plan_removes_the_old_routines()
    {
        var reading = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ler 2 livros"));
        await Goals.ReplaceRoutinesAsync(reading.Id, _userId, [new("ler", RoutineFrequency.Daily, RoutineSource.Pippin)]);

        await Goals.ReplaceRoutinesAsync(reading.Id, _userId, [new("ler", RoutineFrequency.Weekly, RoutineSource.User, DaysOfWeek: [6, 7])]);

        var routine = Assert.Single(await Goals.ListRoutinesAsync(_userId));
        Assert.Equal(RoutineFrequency.Weekly, routine.Frequency);
    }

    [Fact]
    public async Task Does_not_save_a_plan_for_someone_elses_goal()
    {
        var reading = await Goals.CreateAsync(new CreateGoalRequest(_userId, "Ler 2 livros"));

        var saved = await Goals.ReplaceRoutinesAsync(
            reading.Id, Guid.CreateVersion7(), [new("ler", RoutineFrequency.Daily, RoutineSource.User)]);

        Assert.Null(saved);
        Assert.Empty(await Goals.ListRoutinesAsync(_userId));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = dataSource.CreateCommand("DELETE FROM goals.goals WHERE user_id = $1");
        command.Parameters.Add(new() { Value = _userId });
        await command.ExecuteNonQueryAsync();
    }
}

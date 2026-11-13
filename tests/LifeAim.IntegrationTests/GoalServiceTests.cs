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
    public async Task Lists_only_the_users_own_goals()
    {
        await Goals.CreateAsync(new CreateGoalRequest(_userId, "Aprender inglês"));

        var someoneElses = await Goals.ListAsync(Guid.CreateVersion7());

        Assert.Empty(someoneElses);
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

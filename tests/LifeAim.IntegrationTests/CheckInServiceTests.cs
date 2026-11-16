using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>The daily follow-up's check-ins (Funcionalidade 3) against a real, migrated PostgreSQL.</summary>
public sealed class CheckInServiceTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    private static readonly DateOnly Monday = new(2026, 11, 16);

    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _goalId = Guid.CreateVersion7();
    private readonly Guid _routineId = Guid.CreateVersion7();

    private ICheckInService CheckIns => factory.Services.GetRequiredService<ICheckInService>();
    private IProgressService Progress => factory.Services.GetRequiredService<IProgressService>();

    [Fact]
    public async Task Asking_again_counts_one_more_attempt_on_the_same_check_in()
    {
        var first = await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, _goalId, _routineId, Monday, "ler"));
        var second = await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, _goalId, _routineId, Monday, "ler"));

        Assert.Equal(first.Id, second.Id);
        Assert.Equal(2, second.TimesAsked);
        Assert.Equal(CheckInStatus.Asked, second.Status);
        Assert.Equal([second], await CheckIns.ListAsync(_userId, Monday, Monday));
    }

    [Fact]
    public async Task Done_links_the_progress_and_an_answered_check_in_is_not_asked_again()
    {
        var checkIn = await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, _goalId, _routineId, Monday, "ler"));
        var entry = await Progress.RecordAsync(new RecordProgressRequest(_userId, _goalId, Monday, "leu 10 páginas", 10, "páginas"));

        Assert.True(await CheckIns.MarkDoneAsync(checkIn.Id, _userId, entry.Id));
        Assert.False(await CheckIns.MarkMissedAsync(checkIn.Id, _userId, "tarde demais")); // already answered

        var again = await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, _goalId, _routineId, Monday, "ler"));
        Assert.Equal((CheckInStatus.Done, 1), (again.Status, again.TimesAsked));
        Assert.NotNull(again.AnsweredAt);
    }

    [Fact]
    public async Task Not_done_keeps_the_reason()
    {
        var checkIn = await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, _goalId, _routineId, Monday, "ir à academia"));

        Assert.True(await CheckIns.MarkMissedAsync(checkIn.Id, _userId, "  fiquei preso no trabalho "));

        var missed = Assert.Single(await CheckIns.ListAsync(_userId, Monday, Monday));
        Assert.Equal((CheckInStatus.Missed, "fiquei preso no trabalho"), (missed.Status, missed.Reason));
    }

    [Fact]
    public async Task Progress_reported_some_other_way_answers_the_open_check_ins_of_that_goal_and_day()
    {
        await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, _goalId, _routineId, Monday, "ler"));
        await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, _goalId, _routineId, Monday.AddDays(-1), "ler"));

        Assert.Equal(1, await CheckIns.MarkDoneForGoalAsync(_userId, _goalId, Monday, null));

        var byDay = (await CheckIns.ListAsync(_userId, Monday.AddDays(-1), Monday)).ToDictionary(c => c.Day, c => c.Status);
        Assert.Equal(CheckInStatus.Asked, byDay[Monday.AddDays(-1)]); // another day stays not confirmed
        Assert.Equal(CheckInStatus.Done, byDay[Monday]);
    }

    [Fact]
    public async Task Someone_else_cannot_answer_the_users_check_in()
    {
        var checkIn = await CheckIns.MarkAskedAsync(new AskCheckInRequest(_userId, _goalId, _routineId, Monday, "ler"));

        Assert.False(await CheckIns.MarkDoneAsync(checkIn.Id, Guid.CreateVersion7(), null));
        Assert.Empty(await CheckIns.ListAsync(Guid.CreateVersion7(), Monday, Monday));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        // check_ins first: it references the progress entries.
        foreach (var table in new[] { "progress.check_ins", "progress.progress_entries" })
        {
            await using var command = dataSource.CreateCommand($"DELETE FROM {table} WHERE user_id = $1");
            command.Parameters.Add(new() { Value = _userId });
            await command.ExecuteNonQueryAsync();
        }
    }
}

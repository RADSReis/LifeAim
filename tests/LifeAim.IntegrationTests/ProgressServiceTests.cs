using LifeAim.Modules.Progress.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>The Progress module's public API against a real, migrated PostgreSQL.</summary>
public sealed class ProgressServiceTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    private readonly Guid _userId = Guid.CreateVersion7();
    private readonly Guid _goalId = Guid.CreateVersion7();

    private IProgressService Progress => factory.Services.GetRequiredService<IProgressService>();

    [Fact]
    public async Task Records_progress_and_lists_it_newest_day_first()
    {
        var monday = await Progress.RecordAsync(new RecordProgressRequest(
            _userId, _goalId, new DateOnly(2026, 11, 9), "leu 10 páginas", 10m, "páginas"));
        var wednesday = await Progress.RecordAsync(new RecordProgressRequest(
            _userId, _goalId, new DateOnly(2026, 11, 11), "leu 15 páginas", 15m, "páginas"));

        var entries = await Progress.ListByGoalAsync(_userId, _goalId);

        Assert.Equal([wednesday, monday], entries); // every field read back as written
    }

    [Fact]
    public async Task A_report_without_a_number_keeps_only_the_note()
    {
        await Progress.RecordAsync(new RecordProgressRequest(
            _userId, _goalId, new DateOnly(2026, 11, 14), "foi à academia", Unit: "vezes"));

        var entry = Assert.Single(await Progress.ListByGoalAsync(_userId, _goalId));

        Assert.Null(entry.Amount);
        Assert.Null(entry.Unit); // a unit without an amount says nothing
        Assert.Equal("foi à academia", entry.Note);
    }

    [Fact]
    public async Task Lists_only_the_users_own_progress()
    {
        await Progress.RecordAsync(new RecordProgressRequest(_userId, _goalId, new DateOnly(2026, 11, 14), "correu 5 km", 5m, "km"));

        Assert.Empty(await Progress.ListByGoalAsync(Guid.CreateVersion7(), _goalId));
    }

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = dataSource.CreateCommand("DELETE FROM progress.progress_entries WHERE user_id = $1");
        command.Parameters.Add(new() { Value = _userId });
        await command.ExecuteNonQueryAsync();
    }
}

using LifeAim.Modules.Fitness.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace LifeAim.IntegrationTests;

/// <summary>The journal's reminders (IFitnessReminders) against a real, migrated PostgreSQL.</summary>
public sealed class FitnessRemindersTests(LifeAimApiFactory factory) : IClassFixture<LifeAimApiFactory>, IAsyncLifetime
{
    private static readonly DateOnly Day = new(2026, 7, 13);

    private readonly Guid _userId = Guid.CreateVersion7();

    private IFitnessReminders Reminders => factory.Services.GetRequiredService<IFitnessReminders>();

    [Fact]
    public async Task There_is_one_reminder_of_each_kind_and_free_ones_are_told_apart_by_their_text()
    {
        await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.WeighIn, new TimeOnly(9, 0)));
        var weighIn = await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.WeighIn, new TimeOnly(7, 30)));
        await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.Custom, new TimeOnly(9, 0), "tomar a vitamina"));
        await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.Custom, new TimeOnly(10, 0), "Tomar a Vitamina"));
        await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.Custom, new TimeOnly(22, 0), "tomar a creatina"));

        var list = await Reminders.ListAsync(_userId, Day);

        Assert.Equal(
            [(ReminderKind.WeighIn, new TimeOnly(7, 30), null), (ReminderKind.Custom, new TimeOnly(10, 0), "tomar a vitamina"),
                (ReminderKind.Custom, new TimeOnly(22, 0), "tomar a creatina")],
            list.Select(r => (r.Kind, r.At, r.Text)));
        Assert.Equal(weighIn.Id, list[0].Id);
        Assert.Contains(_userId, await Reminders.ListUserIdsAsync());
    }

    [Fact]
    public async Task What_happened_to_a_reminder_is_kept_per_day()
    {
        var vitamin = await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.Custom, new TimeOnly(9, 0), "tomar a vitamina"));

        Assert.True(await Reminders.MarkSentAsync(vitamin.Id, _userId, Day));
        Assert.True(await Reminders.MarkSentAsync(vitamin.Id, _userId, Day)); // again changes nothing
        Assert.True(await Reminders.MarkDoneAsync(vitamin.Id, _userId, Day.AddDays(1))); // done before being sent
        Assert.False(await Reminders.MarkSentAsync(vitamin.Id, Guid.CreateVersion7(), Day)); // not theirs

        Assert.Equal((true, false), Status(await Reminders.ListAsync(_userId, Day)));
        Assert.Equal((false, true), Status(await Reminders.ListAsync(_userId, Day.AddDays(1))));
        Assert.Equal((false, false), Status(await Reminders.ListAsync(_userId, Day.AddDays(2))));

        // Changing its time keeps today's log: it is not sent twice.
        await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.Custom, new TimeOnly(11, 0), "tomar a vitamina"));
        Assert.Equal((true, false), Status(await Reminders.ListAsync(_userId, Day)));
    }

    [Fact]
    public async Task A_reminder_switched_off_is_gone_with_its_log()
    {
        await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.DayClose, new TimeOnly(21, 30)));
        var vitamin = await Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.Custom, new TimeOnly(9, 0), "tomar a vitamina"));
        await Reminders.MarkSentAsync(vitamin.Id, _userId, Day);

        Assert.True(await Reminders.RemoveAsync(_userId, ReminderKind.Custom, "TOMAR A VITAMINA"));
        Assert.False(await Reminders.RemoveAsync(_userId, ReminderKind.Custom, "tomar a vitamina"));
        Assert.False(await Reminders.RemoveAsync(_userId, ReminderKind.WeighIn));

        Assert.Equal(ReminderKind.DayClose, Assert.Single(await Reminders.ListAsync(_userId, Day)).Kind);
        await Assert.ThrowsAsync<ArgumentException>(() => Reminders.SetAsync(new SetReminderRequest(_userId, ReminderKind.Custom, new TimeOnly(9, 0))));
    }

    private static (bool Sent, bool Done) Status(IReadOnlyList<ReminderDto> reminders) => (reminders[0].SentToday, reminders[0].DoneToday);

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        var dataSource = factory.Services.GetRequiredService<NpgsqlDataSource>();
        await using var command = dataSource.CreateCommand("DELETE FROM fitness.reminders WHERE user_id = $1");
        command.Parameters.Add(new() { Value = _userId });
        await command.ExecuteNonQueryAsync();
    }
}
